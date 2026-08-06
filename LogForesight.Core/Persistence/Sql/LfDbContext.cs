using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// SQL 後端的 EF Core 內容（docs/DB-SPEC.md、docs/archive/HISTORY.md「2026-07-23」段 §4）。
///
/// 設計取捨（第一版，可驗證可增量）：每筆分析紀錄存成
///   - 一列 <see cref="DailyRecordRow"/>：抽出可過濾/排序的欄（host_id、host_name、
///     record_date、risk_level、weekly_checkup_date）＋整筆紀錄的 JSON（round-trip 保真）；
///   - 多列 <see cref="TopIssueRow"/> 子表：抽出問題層級的過濾維度
///     （category、event_id、source_name、severity_rank），供跨主機/類別查詢在 DB 端預篩。
/// 讀取一律反序列化 JSON（round-trip 保真）；過濾靠抽出的欄與子表（效能）。
/// 完整正規化（alerts/categories/deep_dives 各自成表）留待特定查詢需要時再加，不影響此設計正確性。
///
/// LINQ 保持 provider 中立：正式環境 SqlServer、測試 SQLite 跑同一組合約測試。
/// </summary>
public class LfDbContext : DbContext
{
    public LfDbContext(DbContextOptions<LfDbContext> options) : base(options) { }

    public DbSet<DailyRecordRow> DailyRecords => Set<DailyRecordRow>();
    public DbSet<TopIssueRow> TopIssues => Set<TopIssueRow>();

    /// <summary>風險 log 暫存（docs/archive/WEB-SCHEDULER-PLAN.md §2，↔ lf_risky_events）</summary>
    public DbSet<RiskyEventRow> RiskyEvents => Set<RiskyEventRow>();

    /// <summary>webdata 各 store 的整份 JSON 內容（一個 key 一列，↔ EfJsonBlobStore）</summary>
    public DbSet<BlobRow> Blobs => Set<BlobRow>();

    /// <summary>append-only 逐行 JSONL（稽核/執行/匯入/處理歷程，↔ EfJsonLogStore）</summary>
    public DbSet<LogLineRow> LogLines => Set<LogLineRow>();

    // ── 處理狀態三表（docs/SCALE-ISSUE-FIRST-PLAN.md P3／根因 B）──────────────
    //
    // 這三份資料原本各是一個整份 JSON blob（lf_blobs 的一列）。它們與 hosts／users 的差別在於
    // **會隨「主機數 × 天數」成長**：6000 台 × 90 天下 issue_handling 約 324 萬列，
    // 序列化後的 C# string 逼近 .NET 的 2 GB 單一物件上限（實測見規劃 §8.5.1，
    // 100 萬列時單次標記已需 6.8 秒、配置 2.4 GB）——那是硬失敗而不是線性劣化。
    //
    // hosts／users／groups 這類「隨組織規模成長」的資料維持 blob（數千筆上限內），
    // 這一刀刻意只切會隨天數成長的三份。

    /// <summary>問題層級處理狀態（↔ lf_issue_handling，原 blob key=issue_handling）</summary>
    public DbSet<IssueHandlingRow> IssueHandlings => Set<IssueHandlingRow>();

    /// <summary>問題案件（↔ lf_issue_cases，原 blob key=issue_cases）</summary>
    public DbSet<IssueCaseRow> IssueCases => Set<IssueCaseRow>();

    /// <summary>日層級處理狀態快照（↔ lf_record_handling，原 blob key=record_handling）</summary>
    public DbSet<RecordHandlingRow> RecordHandlings => Set<RecordHandlingRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BlobRow>(e =>
        {
            e.ToTable("lf_blobs");
            e.HasKey(x => x.BlobKey);
            e.Property(x => x.BlobKey).HasColumnName("blob_key").HasMaxLength(100);
            e.Property(x => x.Content).HasColumnName("content");
            // 樂觀鎖：UpdatedAt 當並發權杖。EfJsonBlobStore.Mutate 是「讀→改→寫」，
            // 沒有這個標記的話兩個行程各自讀到舊內容、後寫的整份蓋掉先寫的（更新遺失）——
            // 這正是 JSONL 檔案時代跨程序鎖檔要防的事故，換 DB 後要用資料庫的機制補上。
            // SaveChanges 時 EF 會在 WHERE 子句帶上原始讀到的值，被別人搶先改過就撞
            // DbUpdateConcurrencyException（繼承自 DbUpdateException，既有重試迴圈已涵蓋）。
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsConcurrencyToken();
        });

        b.Entity<LogLineRow>(e =>
        {
            e.ToTable("lf_log_lines");
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
            e.Property(x => x.LogKey).HasColumnName("log_key").HasMaxLength(100);
            e.Property(x => x.Line).HasColumnName("line");
            // 既存資料庫在本欄新增前已有的列一律為 NULL——SchemaUpgrader 負責幫既有 DB 補這一欄，
            // 新 DB 則由 EnsureCreated 直接建好，兩條路徑最終 schema 相同
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.LogKey, x.Seq });
            e.HasIndex(x => new { x.LogKey, x.CreatedAt });
        });

        b.Entity<DailyRecordRow>(e =>
        {
            e.ToTable("lf_daily_records");
            e.HasKey(x => x.RecordId);
            e.Property(x => x.RecordId).HasColumnName("record_id").ValueGeneratedOnAdd();
            e.Property(x => x.HostId).HasColumnName("host_id");
            e.Property(x => x.HostName).HasColumnName("host_name").HasMaxLength(255);
            e.Property(x => x.RecordDate).HasColumnName("record_date");
            e.Property(x => x.RiskLevel).HasColumnName("risk_level").HasMaxLength(10);
            // 抽出的排序鍵（docs/archive/HISTORY.md P1-2）：問題查詢頁清單排序依
            // 「風險等級→有無關聯訊號→日期」，前者已有 RiskLevel 欄可下推，這欄補上後者——
            // 否則「有無關聯訊號」只存在 ContentJson 裡，逼得分頁查詢必須整批撈回記憶體才能排序。
            // 舊資料（本欄新增前寫入的列）預設 false，下次批次重新分析同一天會自然更新為正確值；
            // 短期內排序稍不精準是可接受的權衡，見 SchemaUpgrader 的補欄位說明。
            e.Property(x => x.HasCorrelation).HasColumnName("has_correlation").HasDefaultValue(false);
            e.Property(x => x.WeeklyCheckupDate).HasColumnName("weekly_checkup_date");
            e.Property(x => x.ContentJson).HasColumnName("content_json");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            // 錨定窗查詢（ReadRecent）與缺日判定（HasRecord）都以日期為主軸
            e.HasIndex(x => x.RecordDate);
            e.HasIndex(x => new { x.HostId, x.RecordDate });
        });

        b.Entity<TopIssueRow>(e =>
        {
            e.ToTable("lf_top_issues");
            e.HasKey(x => x.IssueId);
            e.Property(x => x.IssueId).HasColumnName("issue_id").ValueGeneratedOnAdd();
            e.Property(x => x.RecordId).HasColumnName("record_id");
            e.Property(x => x.SourceName).HasColumnName("source_name").HasMaxLength(255);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.Category).HasColumnName("category").HasMaxLength(20);
            e.Property(x => x.SeverityRank).HasColumnName("severity_rank");

            e.HasIndex(x => x.RecordId);
            e.HasIndex(x => new { x.EventId, x.SourceName });   // 跨主機同簽章查詢
            e.HasIndex(x => x.Category);

            e.HasOne<DailyRecordRow>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── 處理狀態三表 ──────────────────────────────────────────────────────
        //
        // **主機以 host_name_key（大寫正規化）為比對鍵**，不是 host_name 原值，也不是 host_id：
        //   - 用原值不行：C# 全站以 OrdinalIgnoreCase 比對主機名，但 SQL 的 `=` 大小寫語意
        //     依 provider collation 而異（SQLite 預設 BINARY 區分大小寫、SqlServer 常見 CI）。
        //     同一份資料在兩個後端會有不同行為——EfAnalysisRecordStore.OwnedRows 已經為此
        //     踩過一次坑（用 UPPER() 正規化），不該在新表再踩一次。存正規化欄位比每次
        //     查詢都 UPPER() 更好：它走得到索引。
        //   - 刻意**不改用 host_id**（規劃 §8.1 缺陷 3 原本提議 host_id，實作時改回）：
        //     處理狀態的既有語意是「以**現行主機名稱**為鍵」，合併由呼叫端映射到存活主機處理
        //     （RecordListQueryService.HostNameOf）。改鍵會連帶改變改名時的行為，
        //     且遷移需要 name→id 解析與孤兒處理——那是另一個題目，不該夾在儲存層置換裡做。
        //     正規化欄位已經解掉 collation 這個真正的跨後端風險。
        //
        // **updated_at 是並發權杖**：換掉整份 blob 之後，原本靠 lf_blobs.UpdatedAt
        // （WEB-SPEC §10.4 的樂觀鎖）擋下的「兩個人同時標記同一個問題、後寫的靜默蓋掉先寫的」
        // 就沒有防線了。體檢 §0 把併發衝突列為未驗證項目，若不補這一層，它會從
        // 「未驗證」變成「確定會發生」。

        b.Entity<IssueHandlingRow>(e =>
        {
            e.ToTable("lf_issue_handling");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.HostName).HasColumnName("host_name").HasMaxLength(255);
            e.Property(x => x.HostNameKey).HasColumnName("host_name_key").HasMaxLength(255);
            e.Property(x => x.RecordDate).HasColumnName("record_date");
            e.Property(x => x.IssueKey).HasColumnName("issue_key").HasMaxLength(512);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.ActorAccount).HasColumnName("actor_account").HasMaxLength(255);
            e.Property(x => x.Note).HasColumnName("note");
            e.Property(x => x.DueDate).HasColumnName("due_date");
            e.Property(x => x.CaseId).HasColumnName("case_id").HasMaxLength(64);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsConcurrencyToken();

            // 唯一鍵＝原 blob 的「同一 (主機, 日期, 問題) 只有一列」語意，由資料庫保證
            e.HasIndex(x => new { x.HostNameKey, x.RecordDate, x.IssueKey }).IsUnique();
            e.HasIndex(x => new { x.HostNameKey, x.RecordDate });   // GetForDay
            e.HasIndex(x => x.CaseId);                              // GetByCase
        });

        b.Entity<IssueCaseRow>(e =>
        {
            e.ToTable("lf_issue_cases");
            e.HasKey(x => x.CaseId);
            e.Property(x => x.CaseId).HasColumnName("case_id").HasMaxLength(64);
            e.Property(x => x.HostName).HasColumnName("host_name").HasMaxLength(255);
            e.Property(x => x.HostNameKey).HasColumnName("host_name_key").HasMaxLength(255);
            e.Property(x => x.IssueKey).HasColumnName("issue_key").HasMaxLength(512);
            e.Property(x => x.IssueLabel).HasColumnName("issue_label").HasMaxLength(512);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            e.Property(x => x.HandlerId).HasColumnName("handler_id");
            e.Property(x => x.Note).HasColumnName("note");
            e.Property(x => x.DueDate).HasColumnName("due_date");
            e.Property(x => x.FirstLinkedDate).HasColumnName("first_linked_date");
            e.Property(x => x.LastLinkedDate).HasColumnName("last_linked_date");
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedByAccount).HasColumnName("created_by_account").HasMaxLength(255);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsConcurrencyToken();

            // GetOpen／GetOpenForHost：同一 (主機, 問題簽章) 至多一個進行中案件的查詢形狀
            e.HasIndex(x => new { x.HostNameKey, x.IssueKey, x.ClosedAt });
            e.HasIndex(x => new { x.HandlerId, x.ClosedAt });   // GetOpenByHandler／GetByHandler
        });

        b.Entity<RecordHandlingRow>(e =>
        {
            e.ToTable("lf_record_handling");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.HostName).HasColumnName("host_name").HasMaxLength(255);
            e.Property(x => x.HostNameKey).HasColumnName("host_name_key").HasMaxLength(255);
            e.Property(x => x.RecordDate).HasColumnName("record_date");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            e.Property(x => x.HandlerId).HasColumnName("handler_id");
            e.Property(x => x.DueDate).HasColumnName("due_date");
            e.Property(x => x.Note).HasColumnName("note");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsConcurrencyToken();

            e.HasIndex(x => new { x.HostNameKey, x.RecordDate }).IsUnique();
            e.HasIndex(x => x.HandlerId);   // GetByHandler
            e.HasIndex(x => x.Status);      // GetUnresolved
        });

        b.Entity<RiskyEventRow>(e =>
        {
            e.ToTable("lf_risky_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.HostId).HasColumnName("host_id");
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.LogName).HasColumnName("log_name").HasMaxLength(255);
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(255);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.EntryType).HasColumnName("entry_type");
            e.Property(x => x.EventTime).HasColumnName("event_time");
            e.Property(x => x.Message).HasColumnName("message");
            e.Property(x => x.RuleId).HasColumnName("rule_id").HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            // AI 對話查詢形狀（host_id+date+source+event_id）；date 單獨一支供 Prune 清理
            e.HasIndex(x => new { x.HostId, x.Date, x.Source, x.EventId });
            e.HasIndex(x => x.Date);
        });
    }
}

/// <summary>每日分析紀錄列（抽出過濾/排序欄＋整筆 JSON）。↔ lf_daily_records</summary>
public class DailyRecordRow
{
    public long RecordId { get; set; }
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public DateTime RecordDate { get; set; }
    public string RiskLevel { get; set; } = string.Empty;
    public bool HasCorrelation { get; set; }
    public DateTime? WeeklyCheckupDate { get; set; }
    public string ContentJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>問題簽章列（僅供過濾的抽出欄；讀取的權威來源是 DailyRecordRow.ContentJson）。↔ lf_top_issues</summary>
public class TopIssueRow
{
    public long IssueId { get; set; }
    public long RecordId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string Category { get; set; } = string.Empty;
    public int SeverityRank { get; set; }
}

/// <summary>風險 log 暫存一列（docs/archive/WEB-SCHEDULER-PLAN.md §2）。↔ lf_risky_events</summary>
public class RiskyEventRow
{
    public long Id { get; set; }
    public long HostId { get; set; }
    public DateTime Date { get; set; }
    public string LogName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public EventLogEntryType EntryType { get; set; }
    public DateTime EventTime { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? RuleId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 主機名稱的正規化鍵：全站以 <see cref="StringComparer.OrdinalIgnoreCase"/> 比對主機名，
/// 但 SQL 的大小寫語意依 provider collation 而異。存正規化欄位讓兩個後端行為一致，
/// 且比每次查詢都 <c>UPPER()</c> 更好——它走得到索引。
/// 主機名為 ASCII，<c>ToUpperInvariant</c> 等價 OrdinalIgnoreCase。
/// </summary>
public static class HostNameKey
{
    public static string Of(string hostName) => (hostName ?? string.Empty).ToUpperInvariant();
}

/// <summary>問題層級處理狀態的一列。↔ lf_issue_handling</summary>
public class IssueHandlingRow
{
    public long Id { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string HostNameKey { get; set; } = string.Empty;
    public DateTime RecordDate { get; set; }
    public string IssueKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long? ActorId { get; set; }
    public string ActorAccount { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime? DueDate { get; set; }
    public string? CaseId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>問題案件的一列。↔ lf_issue_cases</summary>
public class IssueCaseRow
{
    public string CaseId { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string HostNameKey { get; set; } = string.Empty;
    public string IssueKey { get; set; } = string.Empty;
    public string IssueLabel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long? HandlerId { get; set; }
    public string? Note { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime FirstLinkedDate { get; set; }
    public DateTime LastLinkedDate { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedByAccount { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>日層級處理狀態快照的一列。↔ lf_record_handling</summary>
public class RecordHandlingRow
{
    public long Id { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string HostNameKey { get; set; } = string.Empty;
    public DateTime RecordDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public long? HandlerId { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Note { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>webdata 整份 JSON 內容的一列（key＝store 名稱，如 "users"）。↔ lf_blobs</summary>
public class BlobRow
{
    public string BlobKey { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>append-only JSONL 的一行（log_key＝來源，如 "audit"；seq 自增即附加順序）。↔ lf_log_lines</summary>
public class LogLineRow
{
    public long Seq { get; set; }
    public string LogKey { get; set; } = string.Empty;
    public string Line { get; set; } = string.Empty;

    /// <summary>插入時間；schema 升級前寫入的既存列為 null（見 <see cref="SchemaUpgrader"/>）</summary>
    public DateTime? CreatedAt { get; set; }
}
