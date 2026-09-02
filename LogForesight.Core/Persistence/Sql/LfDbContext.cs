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

    // ── 處理狀態三表（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P3／根因 B）──────────────
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

    /// <summary>問題的機房首見日（↔ lf_issue_first_seen，回饋十九輪批次B）：不受查詢期間截斷，
    /// 也不受（未來的）保留期修剪——見 <see cref="IssueFirstSeenRow"/> 類別註解。</summary>
    public DbSet<IssueFirstSeenRow> IssueFirstSeen => Set<IssueFirstSeenRow>();

    /// <summary>權限異動檢核（↔ lf_permission_changes，含確認狀態）</summary>
    public DbSet<PermissionChangeRow> PermissionChanges => Set<PermissionChangeRow>();

    /// <summary>報告全文（↔ lf_reports，風險／週檢／權限異動三種）</summary>
    public DbSet<ReportRow> Reports => Set<ReportRow>();

    /// <summary>PRTG 裝置鏡像（↔ lf_prtg_devices）</summary>
    public DbSet<PrtgDeviceRow> PrtgDevices => Set<PrtgDeviceRow>();

    /// <summary>PRTG 感測器鏡像（↔ lf_prtg_sensors）</summary>
    public DbSet<PrtgSensorRow> PrtgSensors => Set<PrtgSensorRow>();

    /// <summary>PRTG 狀態變更與訊息（↔ lf_prtg_state_changes）</summary>
    public DbSet<PrtgStateChangeRow> PrtgStateChanges => Set<PrtgStateChangeRow>();

    /// <summary>PRTG 每小時聚合數值（↔ lf_prtg_values）</summary>
    public DbSet<PrtgValueRow> PrtgValues => Set<PrtgValueRow>();

    /// <summary>PRTG 主機按日映射（↔ lf_prtg_host_map）</summary>
    public DbSet<PrtgHostMapRow> PrtgHostMaps => Set<PrtgHostMapRow>();

    /// <summary>PRTG 人工主機對應（↔ lf_prtg_manual_map）</summary>
    public DbSet<PrtgManualMapRow> PrtgManualMaps => Set<PrtgManualMapRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BlobRow>(e =>
        {
            e.ToTable("lf_blobs");
            e.HasKey(x => x.BlobKey);
            e.Property(x => x.BlobKey).HasColumnName("blob_key").HasMaxLength(100);
            e.Property(x => x.Content).HasColumnName("content");
            e.Property(x => x.Version).HasColumnName("version");
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
            e.Property(x => x.DetailPruned).HasColumnName("detail_pruned").HasDefaultValue(false);

            // 讀取面全面 SQL 化的抽出欄（回饋十九輪批次B）：只有這幾個欄位需要跨越單筆詳情頁
            // （仍讀 ContentJson）以外的清單／聚合路徑，其餘欄位（RiskBasis／TrendAssessment／
            // AI 敘事本文等）留在 blob——沒有查詢路徑需要在不解 blob 的情況下讀它們。
            // 舊列預設值不是正確資料，由 DailyRecordBackfiller 背景補齊（見 ExtractVersion）。
            e.Property(x => x.Headline).HasColumnName("headline").HasDefaultValue(string.Empty);
            e.Property(x => x.DataIncomplete).HasColumnName("data_incomplete").HasDefaultValue(false);
            e.Property(x => x.SecurityLogAvailable).HasColumnName("security_log_available");
            e.Property(x => x.ErrorCount).HasColumnName("error_count").HasDefaultValue(0);
            e.Property(x => x.WarningCount).HasColumnName("warning_count").HasDefaultValue(0);
            e.Property(x => x.AiAnalyzed).HasColumnName("ai_analyzed").HasDefaultValue(false);
            e.Property(x => x.AiPending).HasColumnName("ai_pending").HasDefaultValue(false);
            // 存量回填的判定欄（取代 lf_top_issues 沿用的「record_date 是否為 MinValue」哨兵手法——
            // 這幾個欄位本身的合法值就含 0/false/空字串，沒有哪個值能安全地當「還沒回填」的訊號）。
            // 本輪寫入＝1；舊列預設 0，DailyRecordBackfiller 掃 &lt;1。has_correlation（P1-2 既有欄）
            // 的舊列回填一併掛在同一個版本號下，不另開一欄。
            e.Property(x => x.ExtractVersion).HasColumnName("extract_version").HasDefaultValue(0);

            // 錨定窗查詢（ReadRecent）與缺日判定（HasRecord）都以日期為主軸
            e.HasIndex(x => x.RecordDate);
            e.HasIndex(x => new { x.HostId, x.RecordDate });
            e.HasIndex(x => x.ExtractVersion);   // DailyRecordBackfiller 的候選查詢
            e.HasIndex(x => new { x.AiPending, x.RecordDate }).HasDatabaseName("IX_lf_daily_records_ai_pending_record_date"); // 全域待補查詢（批次C）
            // 可行動快照（ActionableOccurrences）的日層級篩選：risk_level IN (高/中) + 日期範圍。
            // 等值前導欄在前，範圍欄在後——只有 record_date 索引時整段期間都得逐列過濾 risk_level
            e.HasIndex(x => new { x.RiskLevel, x.RecordDate }).HasDatabaseName("IX_lf_daily_records_risk_date");
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

            // 問題事實表的聚合維度（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P4／根因 C）：
            // 這張表原本只當「篩選子表」用（EXISTS 子查詢），拿不到主機與日期就無法在 SQL 端
            // 回答「這個問題影響幾台、跨哪段期間」——那正是需求「主視角改成問題」要的兩個數字。
            // 這四欄自父列與問題本身去正規化而來，寫入時一併填好（同 lf_record_categories
            // 「寫入時算好、查詢端直接讀」的既有分工，WEB-SPEC §10.3）。
            e.Property(x => x.HostId).HasColumnName("host_id").HasDefaultValue(0L);
            e.Property(x => x.RecordDate).HasColumnName("record_date");
            e.Property(x => x.EventCount).HasColumnName("event_count").HasDefaultValue(0);
            e.Property(x => x.ElevatesDayRisk).HasColumnName("elevates_day_risk").HasDefaultValue(false);
            // 完整簽章的另外兩段：依問題視角以 (Source, EventId) 分組，但處理狀態是以
            // **完整簽章**（LogName|Source|EventId|EntryType）為鍵——少了這兩欄就 join 不到
            // 處理狀態，§10.6「排除已有結論的問題」也就做不出來（規劃 §8.1 缺陷 1）
            e.Property(x => x.LogName).HasColumnName("log_name").HasMaxLength(255).HasDefaultValue(string.Empty);
            e.Property(x => x.EntryType).HasColumnName("entry_type").HasDefaultValue(0);
            // 回饋十九輪批次B：依問題視角全面 SQL 化需要的最後兩欄。
            // KnownIssue：latest 快照用（依問題視角「已知問題」欄，只取最近一次出現的值）。
            // EventKey：Linux 完整簽章第五段，沒有它 IssueSignatureKey 組不回去，
            // 「同 program 不同規則」會在 SQL 端誤併成一組（規劃 §8.1 缺陷 1 的 Linux 版本）。
            e.Property(x => x.KnownIssue).HasColumnName("known_issue");
            e.Property(x => x.EventKey).HasColumnName("event_key").HasMaxLength(255).HasDefaultValue(string.Empty);

            e.HasIndex(x => x.RecordId);
            e.HasIndex(x => new { x.EventId, x.SourceName });   // 跨主機同簽章查詢
            e.HasIndex(x => x.Category);
            // 問題聚合的查詢形狀：期間 → 依簽章 GROUP BY
            e.HasIndex(x => new { x.RecordDate, x.SourceName, x.EventId });
            e.HasIndex(x => new { x.HostId, x.RecordDate });
            // 問題彙總查詢（HostIdsByIssue/LatestOccurrences/DailyHostCounts）的形狀是
            // event_id IN (…) + record_date 範圍：(record_date, …) 的前導欄是範圍等於掃整段期間，
            // 這裡補「等值前導」版本讓最佳化器能先縮到指定問題再吃日期範圍
            e.HasIndex(x => new { x.EventId, x.RecordDate }).HasDatabaseName("IX_lf_top_issues_event_date");

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
            // 首次寫入時間（回饋十九輪批次B，MTTA 保底）：只在 INSERT 時落，之後不隨狀態異動改變
            // ——UpdatedAt 已經是並發權杖、每次改狀態都會被覆寫，答不出「這一列何時第一次被人碰」。
            // 舊列為 null，本輪不回填（成效指標另案，見 BACKLOG），本輪也不建任何查詢消費它。
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

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

        b.Entity<IssueFirstSeenRow>(e =>
        {
            e.ToTable("lf_issue_first_seen");
            e.HasKey(x => new { x.SourceKey, x.EventId });
            e.Property(x => x.SourceKey).HasColumnName("source_key").HasMaxLength(255);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.SourceName).HasColumnName("source_name").HasMaxLength(255);
            e.Property(x => x.FirstSeen).HasColumnName("first_seen");
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

        b.Entity<PermissionChangeRow>(e =>
        {
            e.ToTable("lf_permission_changes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.ChangeId).HasColumnName("change_id").HasMaxLength(64);
            // 去重鍵刻意不設長度上限：它由「主機名(≤255)｜Ticks(19)｜EventId｜AlertText(≤503)」串成，
            // 最長可達約 790 字元。設成 nvarchar(512) 在 SQLite 上（TEXT 無長度）測不出來，
            // 到 SQL Server 會變成寫入時「字串或二進位資料會被截斷」。不要改回加長度。
            e.Property(x => x.DedupeKey).HasColumnName("dedupe_key");
            e.Property(x => x.HostName).HasColumnName("host_name").HasMaxLength(255);
            e.Property(x => x.HostNameKey).HasColumnName("host_name_key").HasMaxLength(255);
            e.Property(x => x.DetectedAt).HasColumnName("detected_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            // Target 直接來自事件訊息的 Object Name／Group Name，Windows 長路徑（\?\ 前綴、
            // 深層 UNC 分享）輕易超過 512 字元。設長度上限在 SQLite（TEXT 無長度）測不出來，
            // 到 SQL Server 會變成寫入時截斷例外，讓整批夜間分析的權限異動寫入失敗。
            // 它沒有索引，不設上限沒有代價。
            e.Property(x => x.Target).HasColumnName("target");
            e.Property(x => x.ChangeType).HasColumnName("change_type").HasMaxLength(64);
            e.Property(x => x.Category).HasColumnName("category").HasMaxLength(64);
            e.Property(x => x.IsPrivilegedTarget).HasColumnName("is_privileged_target");
            e.Property(x => x.InitiatorAccount).HasColumnName("initiator_account").HasMaxLength(255);
            e.Property(x => x.TargetAccount).HasColumnName("target_account").HasMaxLength(255);
            e.Property(x => x.ObjectType).HasColumnName("object_type").HasMaxLength(64);
            // 處理程序名稱是完整路徑，不設長度上限（同 target 欄的理由）
            e.Property(x => x.ProcessName).HasColumnName("process_name");
            e.Property(x => x.CoveredFrom).HasColumnName("covered_from");
            e.Property(x => x.CoveredTo).HasColumnName("covered_to");
            e.Property(x => x.PairCount).HasColumnName("pair_count");
            e.Property(x => x.BeforeValue).HasColumnName("before_value");
            e.Property(x => x.AfterValue).HasColumnName("after_value");
            e.Property(x => x.AlertText).HasColumnName("alert_text");
            // 原始事件訊息不設長度上限（同 target／process_name 的理由）：Windows 事件訊息長度不可預期，
            // 設上限在 SQLite（TEXT 無長度）測不出來，到 SQL Server 會變成寫入時的截斷例外
            e.Property(x => x.RawText).HasColumnName("raw_text");
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(64);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            e.Property(x => x.ConfirmedBy).HasColumnName("confirmed_by");
            e.Property(x => x.ConfirmedByAccount).HasColumnName("confirmed_by_account").HasMaxLength(255);
            e.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at");
            e.Property(x => x.ConfirmNote).HasColumnName("confirm_note");

            // dedupe_key 沒有索引：沒有任何查詢以它為條件（GetDedupeKeys 是依 created_at 篩選後
            // 投影這一欄），而它又長到不適合當索引鍵。
            e.HasIndex(x => x.ChangeId).IsUnique();
            e.HasIndex(x => new { x.Status, x.DetectedAt });
            e.HasIndex(x => x.DetectedAt);
            e.HasIndex(x => new { x.HostNameKey, x.DetectedAt });
            e.HasIndex(x => new { x.Category, x.Status });
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<ReportRow>(e =>
        {
            e.ToTable("lf_reports");
            e.HasKey(x => x.ReportId);
            e.Property(x => x.ReportId).HasColumnName("report_id").ValueGeneratedOnAdd();
            // host_id 刻意不設 FK：主機登記失敗時它是 0（全站的 HostIdentity 慣例是
            // 「host_id = 0 時改以 host_name 歸戶」），設 FK 會讓當晚的報告寫入直接拋，
            // 把「報告寫不出來」升級成「整趟分析失敗」。host_name 是那條 fallback 的歸戶鍵。
            e.Property(x => x.HostId).HasColumnName("host_id");
            e.Property(x => x.HostName).HasColumnName("host_name").HasMaxLength(255);
            e.Property(x => x.ReportDate).HasColumnName("report_date");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(20);
            e.Property(x => x.RiskLevel).HasColumnName("risk_level").HasMaxLength(10);
            e.Property(x => x.Categories).HasColumnName("categories").HasMaxLength(200);
            e.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255);
            e.Property(x => x.Content).HasColumnName("content");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            // 唯一鍵＝upsert 的判定鍵：重新分析同一天要就地取代，不是留兩份讓使用者猜哪份是現行的。
            // **host_name 必須納入鍵**：host_id = 0 是「未登記」的哨兵值而不是一台主機，
            // 兩台都還沒登記成功的主機在同一天會撞同一個鍵，只留 host_id 就會讓其中一台的
            // 報告覆蓋另一台——那正是本輪要修掉的檔名碰撞 bug 換個地方重演。
            e.HasIndex(x => new { x.HostId, x.HostName, x.ReportDate, x.Kind }).IsUnique();
            // 保留期清理依 created_at（不是 report_date）：重跑 100 天前的主機日時，
            // 依 report_date 清理會讓剛補出來的報告立刻消失。理由同 lf_permission_changes。
            e.HasIndex(x => x.CreatedAt);
        });

        // ── PRTG 鏡像層資料表 ──────────────────────────────────────────
        //
        // 鏡像 PRTG 設備、感測器、狀態變更、每小時聚合值與主機映射。
        // 比照既有資料表：單參數 ToTable（零 schema 前綴）、欄名全小寫 snake_case、
        // 時間欄位一律 DateTime（本地時間）。
        b.Entity<PrtgDeviceRow>(e =>
        {
            e.ToTable("lf_prtg_devices");
            e.HasKey(x => x.Objid);
            e.Property(x => x.Objid).HasColumnName("objid").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(255);
            e.Property(x => x.GroupPath).HasColumnName("group_path").HasMaxLength(512);
            e.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(64);
            e.Property(x => x.Tags).HasColumnName("tags");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(64);
            e.Property(x => x.DependencyObjid).HasColumnName("dependency_objid");
            e.Property(x => x.Paused).HasColumnName("paused");
            e.Property(x => x.SyncedAt).HasColumnName("synced_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            e.HasIndex(x => x.Ip).HasDatabaseName("IX_lf_prtg_devices_ip");
        });

        b.Entity<PrtgSensorRow>(e =>
        {
            e.ToTable("lf_prtg_sensors");
            e.HasKey(x => x.Objid);
            e.Property(x => x.Objid).HasColumnName("objid").ValueGeneratedNever();
            e.Property(x => x.DeviceObjid).HasColumnName("device_objid");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(255);
            e.Property(x => x.SensorType).HasColumnName("sensor_type").HasMaxLength(128);
            e.Property(x => x.Tags).HasColumnName("tags");
            e.Property(x => x.Unit).HasColumnName("unit").HasMaxLength(64);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(64);
            e.Property(x => x.ThresholdsJson).HasColumnName("thresholds_json");
            e.Property(x => x.DependencyObjid).HasColumnName("dependency_objid");
            e.Property(x => x.Paused).HasColumnName("paused");
            // Category / CategorySource 是 sensor 語意分類欄位，本輪一律為 null（分類引擎屬後續階段），欄位先備好避免日後再改 schema。
            e.Property(x => x.Category).HasColumnName("category").HasMaxLength(64);
            e.Property(x => x.CategorySource).HasColumnName("category_source").HasMaxLength(16);
            e.Property(x => x.SyncedAt).HasColumnName("synced_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            e.HasIndex(x => x.DeviceObjid).HasDatabaseName("IX_lf_prtg_sensors_device");
            e.HasIndex(x => x.SensorType).HasDatabaseName("IX_lf_prtg_sensors_type");
        });

        b.Entity<PrtgStateChangeRow>(e =>
        {
            e.ToTable("lf_prtg_state_changes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.SensorObjid).HasColumnName("sensor_objid");
            e.Property(x => x.ChangedAt).HasColumnName("changed_at");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(64);
            e.Property(x => x.PrevStatus).HasColumnName("prev_status").HasMaxLength(64);
            e.Property(x => x.Message).HasColumnName("message");
            e.Property(x => x.Quality).HasColumnName("quality").HasMaxLength(16);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            e.HasIndex(x => new { x.SensorObjid, x.ChangedAt }).HasDatabaseName("IX_lf_prtg_state_ch_sensor");
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("IX_lf_prtg_state_ch_created");
        });

        b.Entity<PrtgValueRow>(e =>
        {
            e.ToTable("lf_prtg_values");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.SensorObjid).HasColumnName("sensor_objid");
            e.Property(x => x.PeriodStart).HasColumnName("period_start");
            e.Property(x => x.AvgValue).HasColumnName("avg_value");
            e.Property(x => x.MinValue).HasColumnName("min_value");
            e.Property(x => x.MaxValue).HasColumnName("max_value");
            e.Property(x => x.Coverage).HasColumnName("coverage");
            e.Property(x => x.Quality).HasColumnName("quality").HasMaxLength(16);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            e.HasIndex(x => new { x.SensorObjid, x.PeriodStart }).IsUnique().HasDatabaseName("IX_lf_prtg_values_uniq");
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("IX_lf_prtg_values_created");
        });

        b.Entity<PrtgHostMapRow>(e =>
        {
            e.ToTable("lf_prtg_host_map");
            e.HasKey(x => new { x.MapDate, x.DeviceObjid });
            e.Property(x => x.MapDate).HasColumnName("map_date");
            e.Property(x => x.DeviceObjid).HasColumnName("device_objid");
            e.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(64);
            e.Property(x => x.HostId).HasColumnName("host_id");
            e.Property(x => x.HostName).HasColumnName("host_name").HasMaxLength(255);
            e.Property(x => x.MapStatus).HasColumnName("map_status").HasMaxLength(16);
            e.Property(x => x.Note).HasColumnName("note").HasMaxLength(512);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            e.HasIndex(x => x.CreatedAt).HasDatabaseName("IX_lf_prtg_host_map_created");
        });

        b.Entity<PrtgManualMapRow>(e =>
        {
            e.ToTable("lf_prtg_manual_map");
            e.HasKey(x => x.DeviceObjid);
            e.Property(x => x.DeviceObjid).HasColumnName("device_objid");
            e.Property(x => x.HostId).HasColumnName("host_id");
            e.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(64);
            e.Property(x => x.Note).HasColumnName("note").HasMaxLength(512);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            e.HasIndex(x => x.HostId).HasDatabaseName("IX_lf_prtg_manual_map_host");
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

    /// <summary>標記詳情是否已因超過保留期而被清除，以確保夜間作業冪等性</summary>
    public bool DetailPruned { get; set; }

    // ── 讀取面 SQL 化的抽出欄（回饋十九輪批次B）──────────────────────────

    public string Headline { get; set; } = string.Empty;
    public bool DataIncomplete { get; set; }
    public bool? SecurityLogAvailable { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public bool AiAnalyzed { get; set; }
    public bool AiPending { get; set; }

    /// <summary>存量回填的版本號：本輪寫入＝1，舊列預設 0。不是布林是因為未來若再擴充抽出欄，
    /// 版本號可以往上加而不必再想一個新的哨兵值。</summary>
    public int ExtractVersion { get; set; }
}

/// <summary>
/// 問題簽章列。原本只是「供過濾的抽出欄」，自 P4 起同時是**問題聚合的事實表**
/// （docs/archive/SCALE-ISSUE-FIRST-PLAN.md 根因 C）——讀取單筆紀錄的權威來源仍是
/// <see cref="DailyRecordRow.ContentJson"/>，但「這個問題影響幾台、跨哪段期間、
/// 出現幾天」改由這張表 GROUP BY 直接回答，不再把整段期間的紀錄撈回記憶體。
/// ↔ lf_top_issues
/// </summary>
public class TopIssueRow
{
    public long IssueId { get; set; }
    public long RecordId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string Category { get; set; } = string.Empty;
    public int SeverityRank { get; set; }

    // ── 聚合維度（P4 新增，自父列與問題本身去正規化）──────────────────────

    /// <summary>**存活主機** id（合併過的主機以 HostLookup 映射後寫入）——
    /// 直接用紀錄自帶的 host_id 會讓同一台實體機器的墓碑列與存活列各算一台（規劃 §8.1 缺陷 2）</summary>
    public long HostId { get; set; }

    public DateTime RecordDate { get; set; }

    /// <summary>當日該問題的事件次數（<see cref="LogIssueSignature.Count"/>）</summary>
    public int EventCount { get; set; }

    /// <summary>命中「重大」旗標（規劃 §10.2 維度 1 的既有缺口：這個旗標過去只在詳情頁看得到）</summary>
    public bool ElevatesDayRisk { get; set; }

    /// <summary>完整簽章的第一段——與 <see cref="EntryType"/> 一起才能組回 IssueSignatureKey 去 join 處理狀態</summary>
    public string LogName { get; set; } = string.Empty;

    /// <summary>完整簽章的第四段（<see cref="System.Diagnostics.EventLogEntryType"/> 的整數值）</summary>
    public int EntryType { get; set; }

    /// <summary>命中規則表時的已知問題說明（latest 快照，同一 RecordId 內只有一個值）——
    /// 依問題視角全面 SQL 化用（回饋十九輪批次B），未命中為 null</summary>
    public string? KnownIssue { get; set; }

    /// <summary>Linux 完整簽章第五段（回饋十九輪批次B，補上 v1 刻意省略的欄位）：
    /// 沒有它，SQL 端無法組回完整 <c>IssueSignatureKey</c> 去 join 處理狀態，
    /// 「同一個 program 命中不同規則」會被併成同一組。Windows 事件恆為空字串。</summary>
    public string EventKey { get; set; } = string.Empty;
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

    /// <summary>首次寫入時間（回饋十九輪批次B）。舊列為 null，本輪不消費、不回填。</summary>
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// 問題的機房首見日（回饋十九輪批次G 呈現，批次B 落地）。↔ lf_issue_first_seen
///
/// **與 <see cref="TopIssueRow.RecordDate"/> 的 MIN 不同**：問題聚合查詢的 FirstSeen
/// 受查詢期間截斷（選近 7 天時半年前就存在的老問題會顯示成「7 天前首見」），這張表
/// 寫入時 insert-if-absent，之後不論查詢哪個期間、不論（未來的）保留期怎麼修剪歷史紀錄，
/// 這個日期都不會變——它答的是「這個問題第一次在機房出現是什麼時候」，不是「這次查詢
/// 看到的最早一筆」。鍵刻意用 <see cref="SourceKey"/>（正規化大寫，同 HostNameKey 的
/// collation-safety 理由）＋EventId，不含 LogName/EntryType：首見的語意是「這個問題」
/// （依問題視角的分組鍵），不是某個完整簽章第一次出現。
/// </summary>
public class IssueFirstSeenRow
{
    public string SourceKey { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public DateTime FirstSeen { get; set; }
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

    /// <summary>供上層快取判定用的單調遞增版本號，與 UpdatedAt（並發權杖）用途不同。</summary>
    public long Version { get; set; }
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

/// <summary>權限異動檢核一列（含人工確認狀態）。↔ lf_permission_changes</summary>
public class PermissionChangeRow
{
    public long Id { get; set; }
    public string ChangeId { get; set; } = string.Empty;
    public string DedupeKey { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string HostNameKey { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Target { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public string Category { get; set; } = "other";
    public bool IsPrivilegedTarget { get; set; }
    public string? InitiatorAccount { get; set; }
    public string? TargetAccount { get; set; }

    /// <summary>4670 的物件類型／處理程序名稱（見 PermissionChangeRecord）</summary>
    public string? ObjectType { get; set; }

    public string? ProcessName { get; set; }

    /// <summary>彙總列涵蓋的事件時間區間與對數（逐則列為 null，見 PermissionChangeRecord）</summary>
    public DateTime? CoveredFrom { get; set; }

    public DateTime? CoveredTo { get; set; }

    public int? PairCount { get; set; }

    public string BeforeValue { get; set; } = string.Empty;
    public string AfterValue { get; set; } = string.Empty;
    public string AlertText { get; set; } = string.Empty;

    /// <summary>未截斷的原始事件訊息（回饋二十八輪 P9）。升級前寫入的列與彙總列為 null</summary>
    public string? RawText { get; set; }

    public string Source { get; set; } = "本機監控";
    public int? EventId { get; set; }
    public string Status { get; set; } = "pending";
    public long? ConfirmedBy { get; set; }
    public string? ConfirmedByAccount { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string? ConfirmNote { get; set; }
}


/// <summary>
/// 報告全文的一列（風險／週檢／權限異動三種）。↔ lf_reports
///
/// 風險報告在 DB 裡因此是兩層：結構化層（lf_daily_records＋lf_top_issues＋…）供篩選、統計、
/// 排序與餵 AI context；全文層（本表的 <see cref="Content"/>）供使用者點開看完整報告，
/// 一字不差保留既有的 txt 版面。
/// </summary>
public class ReportRow
{
    public long ReportId { get; set; }

    /// <summary>主機 PK；0 代表主機尚未登記成功，改以 <see cref="HostName"/> 歸戶</summary>
    public long HostId { get; set; }

    public string HostName { get; set; } = string.Empty;

    /// <summary>報告所屬日期（不是產生時間；產生時間是 <see cref="CreatedAt"/>）</summary>
    public DateTime ReportDate { get; set; }

    /// <summary>報告種類，值域見 <see cref="LogForesight.Core.Persistence.ReportKinds"/></summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>風險等級，僅 daily_risk 有值</summary>
    public string? RiskLevel { get; set; }

    /// <summary>當日發現的類別串（如「儲存裝置+安全」），僅 daily_risk 有值</summary>
    public string? Categories { get; set; }

    /// <summary>既有檔名格式（如 2026-08-27_高風險_儲存裝置+安全.txt）——顯示與下載檔名用</summary>
    public string FileName { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>產生時間。**保留期清理依這一欄**，不是 <see cref="ReportDate"/></summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>PRTG device 結構鏡像。↔ lf_prtg_devices</summary>
public class PrtgDeviceRow
{
    public long Objid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GroupPath { get; set; } = string.Empty;
    public string? Ip { get; set; }
    public string? Tags { get; set; }
    public string? Status { get; set; }
    public long? DependencyObjid { get; set; }
    public bool Paused { get; set; }
    public DateTime SyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>PRTG sensor 結構鏡像。↔ lf_prtg_sensors</summary>
public class PrtgSensorRow
{
    public long Objid { get; set; }
    public long DeviceObjid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SensorType { get; set; } = string.Empty;
    public string? Tags { get; set; }
    public string? Unit { get; set; }
    public string? Status { get; set; }
    public string? ThresholdsJson { get; set; }
    public long? DependencyObjid { get; set; }
    public bool Paused { get; set; }
    /// <summary>sensor 語意分類欄位，本輪一律為 null（分類引擎屬後續階段），欄位先備好避免日後再改 schema。</summary>
    public string? Category { get; set; }
    /// <summary>sensor 語意分類來源，本輪一律為 null（分類引擎屬後續階段），欄位先備好避免日後再改 schema。</summary>
    public string? CategorySource { get; set; }
    public DateTime SyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>PRTG sensor 狀態變更與訊息。↔ lf_prtg_state_changes</summary>
public class PrtgStateChangeRow
{
    public long Id { get; set; }
    public long SensorObjid { get; set; }
    /// <summary>狀態變更時間（本地時間）</summary>
    public DateTime ChangedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? PrevStatus { get; set; }
    public string? Message { get; set; }
    public string Quality { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>PRTG hourly 聚合數值。↔ lf_prtg_values</summary>
public class PrtgValueRow
{
    public long Id { get; set; }
    public long SensorObjid { get; set; }
    /// <summary>小時起點（本地時間）</summary>
    public DateTime PeriodStart { get; set; }
    public double? AvgValue { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double? Coverage { get; set; }
    public string Quality { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>PRTG 主機對應按日。↔ lf_prtg_host_map</summary>
public class PrtgHostMapRow
{
    public DateTime MapDate { get; set; }
    public long DeviceObjid { get; set; }
    public string? Ip { get; set; }
    public long? HostId { get; set; }
    public string? HostName { get; set; }
    public string MapStatus { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>PRTG 人工主機對應（長期有效，不按日）。↔ lf_prtg_manual_map</summary>
public class PrtgManualMapRow
{
    public long DeviceObjid { get; set; }
    public long HostId { get; set; }
    public string? CreatedBy { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}
