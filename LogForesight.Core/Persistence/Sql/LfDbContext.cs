using Microsoft.EntityFrameworkCore;

namespace LogForesight.Sql;

/// <summary>
/// SQL 後端的 EF Core 內容（docs/DB-PLAN.md、SCALE-2000-PLAN §4）。
///
/// 設計取捨（第一版，可驗證可增量）：每筆分析紀錄存成
///   - 一列 <see cref="DailyRecordRow"/>：抽出可過濾/排序的欄（host_id、host_name、
///     record_date、risk_level、weekly_checkup_date）＋整筆紀錄的 JSON（round-trip 保真）；
///   - 多列 <see cref="TopIssueRow"/> 子表：抽出問題層級的過濾維度
///     （category、event_id、source_name、severity_rank），供跨主機/類別查詢在 DB 端預篩。
/// 讀取一律反序列化 JSON（保真、與 JSONL 逐位一致）；過濾靠抽出的欄與子表（效能）。
/// 完整正規化（alerts/categories/deep_dives 各自成表）留待特定查詢需要時再加，不影響此設計正確性。
///
/// LINQ 保持 provider 中立：正式環境 SqlServer、測試 SQLite 跑同一組合約測試。
/// </summary>
public class LfDbContext : DbContext
{
    public LfDbContext(DbContextOptions<LfDbContext> options) : base(options) { }

    public DbSet<DailyRecordRow> DailyRecords => Set<DailyRecordRow>();
    public DbSet<TopIssueRow> TopIssues => Set<TopIssueRow>();

    /// <summary>webdata 各 store 的整份 JSON 內容（一個 key 一列，↔ EfJsonBlobStore）</summary>
    public DbSet<BlobRow> Blobs => Set<BlobRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BlobRow>(e =>
        {
            e.ToTable("lf_blobs");
            e.HasKey(x => x.BlobKey);
            e.Property(x => x.BlobKey).HasColumnName("blob_key").HasMaxLength(100);
            e.Property(x => x.Content).HasColumnName("content");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
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

/// <summary>webdata 整份 JSON 內容的一列（key＝store 名稱，如 "users"）。↔ lf_blobs</summary>
public class BlobRow
{
    public string BlobKey { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}
