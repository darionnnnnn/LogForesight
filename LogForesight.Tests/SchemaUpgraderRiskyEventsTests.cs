using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="SchemaUpgrader"/> 的 lf_risky_events 建表步驟（docs/archive/WEB-SCHEDULER-PLAN.md §2）：
/// 這是 SQL 後端上線以來第一張不是靠 EnsureCreated 建出來的全新資料表——EnsureCreated
/// 只在資料庫整個不存在時建表，既有部署的 DB 需要靠這個冪等 DDL 步驟補上整張表，
/// 值得直接測（既有的補欄位/補索引步驟沒有專屬測試，但那些改動面小，這個是新能力）。
/// </summary>
public class SchemaUpgraderRiskyEventsTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Upgrade在資料表缺席時建立lf_risky_events並可正常讀寫()
    {
        // 模擬既有部署：DB 已存在（EnsureCreated 已建好含 lf_risky_events 的完整 schema），
        // 但這裡刻意砍掉該表，還原成「這張表問世前」的既有 DB 現況
        using (var ctx = _fx.NewContext())
        {
            ctx.Database.ExecuteSqlRaw("DROP TABLE lf_risky_events");
        }

        using (var ctx = _fx.NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        var store = new EfRiskyEventStore(_fx.NewContext);
        var date = new DateTime(2026, 7, 31);
        store.ReplaceDay(1, date, new List<RiskyEvent>
        {
            new() { HostId = 1, Date = date, LogName = "System", Source = "disk", EventId = 153, Message = "msg", EventTime = date }
        });

        var result = store.Query(1, date, "disk", 153, 20);
        Assert.Single(result);
        Assert.Equal("msg", result[0].Message);
    }

    [Fact]
    public void Upgrade在資料表已存在時為no_op不報錯()
    {
        // EfSqliteFixture 建構時已 EnsureCreated（含 lf_risky_events）；再跑一次 Upgrade
        // 必須安靜跳過，不能因為表已存在而報錯——冪等是這整套 schema 升級機制的前提
        using var ctx = _fx.NewContext();

        var exception = Record.Exception(() => SchemaUpgrader.Upgrade(ctx));

        Assert.Null(exception);
    }

    [Fact]
    public void SqlServer升級SQL在rule_id長度不足100時才ALTER成100()
    {
        var ddl = SchemaUpgrader.SqlServerWidenRiskyEventRuleIdDdl;
        Assert.Contains("ALTER TABLE lf_risky_events ALTER COLUMN rule_id nvarchar(100) NULL", ddl);
        Assert.Contains("INFORMATION_SCHEMA.COLUMNS", ddl);
        Assert.Contains("CHARACTER_MAXIMUM_LENGTH BETWEEN 1 AND 99", ddl);
        Assert.True(ddl.IndexOf("IF EXISTS", StringComparison.Ordinal) < ddl.IndexOf("ALTER TABLE", StringComparison.Ordinal));
        Assert.Contains("rule_id nvarchar(100) NULL", SchemaUpgrader.SqlServerCreateRiskyEventsTable);
    }

    [Fact]
    public void Sqlite舊schema宣告64的rule_id升級後可寫入100字並讀回()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.Database.ExecuteSqlRaw("DROP TABLE lf_risky_events");
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE lf_risky_events (id INTEGER NOT NULL CONSTRAINT PK_lf_risky_events PRIMARY KEY AUTOINCREMENT, " +
                "host_id INTEGER NOT NULL, date TEXT NOT NULL, log_name TEXT NOT NULL, source TEXT NOT NULL, source_key TEXT NULL, " +
                "event_id INTEGER NOT NULL, entry_type INTEGER NOT NULL, event_time TEXT NOT NULL, message TEXT NOT NULL, " +
                "rule_id VARCHAR(64) NULL, created_at TEXT NOT NULL)");
        }
        using (var ctx = _fx.NewContext()) SchemaUpgrader.Upgrade(ctx);

        var longId = new string('R', LogForesight.Core.Analysis.RuleSchemaLimits.IdMaxLength);
        var date = new DateTime(2026, 7, 31);
        var store = new EfRiskyEventStore(_fx.NewContext);
        store.ReplaceDay(1, date, new List<RiskyEvent>
        {
            new() { HostId = 1, Date = date, LogName = "System", Source = "disk", EventId = 153, Message = "m", EventTime = date, RuleId = longId }
        });
        var result = store.Query(1, date, "disk", 153, 20);
        Assert.Single(result);
        Assert.Equal(100, result[0].RuleId!.Length);
    }

    [Fact]
    public void EfModel的rule_id最大長度等於規則Id上限()
    {
        using var ctx = _fx.NewContext();
        var et = ctx.Model.GetEntityTypes().Single(e => e.GetTableName() == "lf_risky_events");
        var prop = et.GetProperties().Single(p => p.GetColumnName() == "rule_id");
        Assert.Equal(LogForesight.Core.Analysis.RuleSchemaLimits.IdMaxLength, prop.GetMaxLength());
    }
}
