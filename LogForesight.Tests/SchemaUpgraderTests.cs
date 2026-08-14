using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// P0-1／docs/DB-SPEC.md 定案 13：自製冪等 DDL 升級（不用 EF Core Migrations）。
///
/// 核心驗收：模擬「既有 DB 是升級前建的舊 schema（lf_log_lines 沒有 created_at 欄）」，
/// 驗證 <see cref="SchemaUpgrader.Upgrade"/> 補上欄位與索引後可正常讀寫；
/// 以及在已是新 schema 的 DB 上重複執行仍是 no-op、不拋例外（冪等性）。
/// </summary>
public class SchemaUpgraderTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SchemaUpgraderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(_connection).Options);

    /// <summary>
    /// 建立升級前的最小舊 schema：兩張表都刻意不含本輪要補的新欄位
    /// （lf_log_lines 缺 created_at、lf_daily_records 缺 has_correlation）。
    /// </summary>
    private void CreateOldSchema()
    {
        using var raw = _connection.CreateCommand();
        raw.CommandText = "CREATE TABLE lf_log_lines (seq INTEGER PRIMARY KEY AUTOINCREMENT, log_key TEXT, line TEXT)";
        raw.ExecuteNonQuery();
        // created_at/weekly_checkup_date 是既有欄位（Phase C 就有），只有 has_correlation 是本輪新增——
        // 前兩者不能漏，否則 EF 對 DailyRecordRow 的查詢會因為模型與這張手刻的「假舊表」不符而報錯，
        // 那會是測試假設本身的錯誤，不是 SchemaUpgrader 的問題
        raw.CommandText = "CREATE TABLE lf_daily_records (record_id INTEGER PRIMARY KEY AUTOINCREMENT, host_id INTEGER, host_name TEXT, record_date TEXT, risk_level TEXT, weekly_checkup_date TEXT, content_json TEXT, created_at TEXT)";
        raw.ExecuteNonQuery();
    }

    [Fact]
    public void 舊schema缺created_at欄_升級後補上且可讀寫()
    {
        CreateOldSchema();

        using (var ctx = NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        using (var ctx = NewContext())
        {
            ctx.LogLines.Add(new LogLineRow { LogKey = "test", Line = "hello", CreatedAt = new DateTime(2026, 1, 1) });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var row = ctx.LogLines.Single();
            Assert.Equal("hello", row.Line);
            Assert.Equal(new DateTime(2026, 1, 1), row.CreatedAt);
        }
    }

    /// <summary>索引確實建立（不是只補了欄位卻漏了索引）</summary>
    [Fact]
    public void 舊schema升級後_索引存在()
    {
        CreateOldSchema();

        using var ctx = NewContext();
        SchemaUpgrader.Upgrade(ctx);

        var indexNames = ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='lf_log_lines'").ToList();

        Assert.Contains("IX_lf_log_lines_log_key_created_at", indexNames);
    }

    /// <summary>既有升級前寫入、沒有時間戳記的列不受影響——只是多了一欄 NULL</summary>
    [Fact]
    public void 舊schema既存列_升級後CreatedAt為null()
    {
        CreateOldSchema();
        using (var raw = _connection.CreateCommand())
        {
            raw.CommandText = "INSERT INTO lf_log_lines (log_key, line) VALUES ('test', 'existing-line')";
            raw.ExecuteNonQuery();
        }

        using var ctx = NewContext();
        SchemaUpgrader.Upgrade(ctx);

        var row = ctx.LogLines.Single();
        Assert.Equal("existing-line", row.Line);
        Assert.Null(row.CreatedAt);
    }

    /// <summary>舊 schema 缺 has_correlation 欄——升級後補上，既存列預設為 false（未知即保守值）</summary>
    [Fact]
    public void 舊schema缺has_correlation欄_升級後補上且既存列預設false()
    {
        CreateOldSchema();
        using (var raw = _connection.CreateCommand())
        {
            // created_at 是既有欄位、非 nullable，測試列必須帶值（不是本輪要驗證的重點，但省略會讓
            // EF 讀出 NULL 塞進非 nullable 的 DateTime 屬性而拋例外，與這則測試想驗證的事無關）
            raw.CommandText = "INSERT INTO lf_daily_records (host_id, host_name, record_date, risk_level, content_json, created_at) " +
                               "VALUES (1, 'HOST-A', '2026-01-01', '高', '{}', '2026-01-01')";
            raw.ExecuteNonQuery();
        }

        using var ctx = NewContext();
        SchemaUpgrader.Upgrade(ctx);

        var row = ctx.DailyRecords.Single();
        Assert.False(row.HasCorrelation);
    }

    /// <summary>升級後可正常讀寫 has_correlation（不是只補了欄位卻寫不進去）</summary>
    [Fact]
    public void 舊schema升級後_has_correlation可讀寫()
    {
        CreateOldSchema();

        using (var ctx = NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        using (var ctx = NewContext())
        {
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = 1, HostName = "HOST-A", RecordDate = DateTime.Today,
                RiskLevel = "高", HasCorrelation = true, ContentJson = "{}"
            });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            Assert.True(ctx.DailyRecords.Single().HasCorrelation);
        }
    }

    /// <summary>新建 DB（EnsureCreated 已含最新 schema）上執行升級是 no-op，且可重複執行不拋例外</summary>
    [Fact]
    public void 新schema上執行升級_不拋例外且冪等()
    {
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();

        SchemaUpgrader.Upgrade(ctx);
        SchemaUpgrader.Upgrade(ctx);   // 再跑一次：冪等,不應重複建欄位/索引而出錯
    }

    // ── 回饋十九輪批次B：lf_daily_records 抽出欄／lf_issue_first_seen ──────────────

    /// <summary>舊 schema 缺批次B 的抽出欄——升級後補上，既存列的 extract_version 預設 0
    /// （尚未回填，DailyRecordBackfiller 依此判定候選）</summary>
    [Fact]
    public void 舊schema缺批次B抽出欄_升級後補上且既存列extract_version為0()
    {
        CreateOldSchema();
        using (var raw = _connection.CreateCommand())
        {
            raw.CommandText = "INSERT INTO lf_daily_records (host_id, host_name, record_date, risk_level, content_json, created_at) " +
                               "VALUES (1, 'HOST-A', '2026-01-01', '高', '{}', '2026-01-01')";
            raw.ExecuteNonQuery();
        }

        using var ctx = NewContext();
        SchemaUpgrader.Upgrade(ctx);

        var row = ctx.DailyRecords.Single();
        Assert.Equal(0, row.ExtractVersion);
        Assert.Equal(string.Empty, row.Headline);
        Assert.Equal(0, row.ErrorCount);
    }

    /// <summary>升級後可正常讀寫批次B 的抽出欄（不是只補了欄位卻寫不進去）</summary>
    [Fact]
    public void 舊schema升級後_批次B抽出欄可讀寫()
    {
        CreateOldSchema();

        using (var ctx = NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        using (var ctx = NewContext())
        {
            ctx.DailyRecords.Add(new DailyRecordRow
            {
                HostId = 1, HostName = "HOST-A", RecordDate = DateTime.Today, RiskLevel = "高",
                ContentJson = "{}", Headline = "測試標題", ErrorCount = 3, AiAnalyzed = true, ExtractVersion = 1
            });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var row = ctx.DailyRecords.Single();
            Assert.Equal("測試標題", row.Headline);
            Assert.Equal(3, row.ErrorCount);
            Assert.True(row.AiAnalyzed);
            Assert.Equal(1, row.ExtractVersion);
        }
    }

    /// <summary>lf_issue_first_seen 是全新的表（不掛在既有表升級之下），連線上完全沒有任何表時也要建得起來</summary>
    [Fact]
    public void 空連線升級後_lf_issue_first_seen表存在且可讀寫()
    {
        using (var ctx = NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        using (var ctx = NewContext())
        {
            ctx.IssueFirstSeen.Add(new IssueFirstSeenRow
                { SourceKey = "DISK", EventId = 153, SourceName = "disk", FirstSeen = new DateTime(2026, 1, 1) });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var row = ctx.IssueFirstSeen.Single();
            Assert.Equal("disk", row.SourceName);
            Assert.Equal(new DateTime(2026, 1, 1), row.FirstSeen);
        }
    }
}
