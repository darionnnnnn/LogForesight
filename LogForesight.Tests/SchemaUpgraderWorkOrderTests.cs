using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="SchemaUpgrader"/> 的交辦單步驟：既有 DB 補 lf_work_orders／lf_work_order_events 兩表、
/// lf_issue_cases 六欄與新索引（含部分唯一索引），且名稱與 EnsureCreated 建出的一致——
/// 不一致的話既有 DB 升級後會與新 DB schema 分岔（或多建一份索引）。
/// </summary>
public class SchemaUpgraderWorkOrderTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static readonly string[] NewCaseColumns =
        { "work_order_id", "source_key", "source_name", "event_id", "day_sync_pending", "cancelled" };

    private static readonly string[] NewCaseIndexes =
        { "IX_lf_issue_cases_work_order_closed", "IX_lf_issue_cases_issue_closed", "IX_lf_issue_cases_day_sync_pending" };

    /// <summary>把 EnsureCreated 建好的 DB 還原成「交辦單問世前」的樣子</summary>
    private static void RevertToLegacy(LfDbContext ctx)
    {
        ctx.Database.ExecuteSqlRaw("DROP TABLE lf_work_order_events");
        ctx.Database.ExecuteSqlRaw("DROP TABLE lf_work_orders");
        foreach (var index in NewCaseIndexes) ctx.Database.ExecuteSqlRaw("DROP INDEX " + index);
        foreach (var column in NewCaseColumns) ctx.Database.ExecuteSqlRaw("ALTER TABLE lf_issue_cases DROP COLUMN " + column);
    }

    private static List<string> Query(LfDbContext ctx, string sql) =>
        ctx.Database.SqlQueryRaw<string>(sql).ToList();

    private static List<string> IndexNames(LfDbContext ctx) =>
        Query(ctx, "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name IN " +
                   "('lf_work_orders', 'lf_work_order_events', 'lf_issue_cases') AND name NOT LIKE 'sqlite_autoindex%'")
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

    [Fact]
    public void Upgrade補齊兩表六欄與新索引_含部分唯一索引()
    {
        using (var ctx = _fx.NewContext())
        {
            RevertToLegacy(ctx);
            Assert.Empty(Query(ctx, "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name LIKE 'lf_work_order%'"));
        }

        using (var ctx = _fx.NewContext()) SchemaUpgrader.Upgrade(ctx);

        using var check = _fx.NewContext();
        var tables = Query(check, "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name LIKE 'lf_work_order%'");
        Assert.Contains("lf_work_orders", tables);
        Assert.Contains("lf_work_order_events", tables);

        var columns = Query(check, "SELECT name AS Value FROM pragma_table_info('lf_issue_cases')");
        foreach (var column in NewCaseColumns) Assert.Contains(column, columns);

        var indexes = IndexNames(check);
        foreach (var name in NewCaseIndexes.Concat(new[]
                 {
                     "IX_lf_work_orders_handler_closed", "IX_lf_work_orders_issue_closed",
                     "IX_lf_work_orders_active_handler_issue", "IX_lf_work_order_events_order_created"
                 }))
            Assert.Contains(name, indexes);

        var partialSql = Query(check,
            "SELECT sql AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_lf_work_orders_active_handler_issue'").Single();
        Assert.Contains("UNIQUE", partialSql);
        Assert.Contains("WHERE closed_at IS NULL AND source_key IS NOT NULL", partialSql);

        // 升級後的表可正常讀寫，部分唯一索引生效
        var store = new EfWorkOrderStore(_fx.NewContext);
        WorkOrder Order() => new()
        {
            SourceName = "Disk", EventId = 153, IssueLabel = "Disk 153", HandlerId = 1,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, CreatedByAccount = "a", CreatedAt = DateTime.Now
        };
        store.Insert(Order());
        Assert.Throws<DbUpdateException>(() => store.Insert(Order()));
    }

    [Fact]
    public void Upgrade重跑不擲例外且索引不重複()
    {
        using (var ctx = _fx.NewContext()) RevertToLegacy(ctx);
        using (var ctx = _fx.NewContext()) SchemaUpgrader.Upgrade(ctx);
        List<string> first;
        using (var ctx = _fx.NewContext()) first = IndexNames(ctx);

        using (var ctx = _fx.NewContext())
            Assert.Null(Record.Exception(() => SchemaUpgrader.Upgrade(ctx)));

        using var check = _fx.NewContext();
        var second = IndexNames(check);
        Assert.Equal(first, second);
        Assert.Equal(second.Count, second.Distinct().Count());
    }

    [Fact]
    public void EnsureCreated與Upgrade建出的索引名稱集合相同()
    {
        // 兩個 DB 都走「啟動時 EnsureCreated 之後一定跑 Upgrade」的真實路徑，比較才公平：
        // lf_issue_cases 既有兩支索引的 EF 名稱與升級器名稱本來就不同（兩者都會存在），不屬本段範圍。
        // A：新 DB（EnsureCreated 建出新表新索引）；B：既有 DB（新表新欄新索引全由 Upgrade 補）
        using (var ctx = _fx.NewContext()) SchemaUpgrader.Upgrade(ctx);

        using var upgraded = new EfSqliteFixture();
        using (var ctx = upgraded.NewContext()) SchemaUpgrader.Upgrade(ctx);
        using (var ctx = upgraded.NewContext()) RevertToLegacy(ctx);
        using (var ctx = upgraded.NewContext()) SchemaUpgrader.Upgrade(ctx);

        List<string> fromUpgrade;
        using (var ctx = upgraded.NewContext()) fromUpgrade = IndexNames(ctx);
        List<string> fromEnsureCreated;
        using (var ctx = _fx.NewContext()) fromEnsureCreated = IndexNames(ctx);
        foreach (var name in NewCaseIndexes) Assert.Contains(name, fromEnsureCreated);

        Assert.Equal(fromEnsureCreated, fromUpgrade);

        // 部分唯一索引兩條路徑的過濾條件一致
        using var a = _fx.NewContext();
        using var b = upgraded.NewContext();
        const string sql = "SELECT sql AS Value FROM sqlite_master WHERE name = 'IX_lf_work_orders_active_handler_issue'";
        Assert.Contains("WHERE closed_at IS NULL AND source_key IS NOT NULL", Query(a, sql).Single());
        Assert.Contains("WHERE closed_at IS NULL AND source_key IS NOT NULL", Query(b, sql).Single());
    }

    [Fact]
    public void SqlServer的DDL常數含source_key與部分唯一索引過濾條件()
    {
        Assert.Contains("CREATE TABLE lf_work_orders", SchemaUpgrader.SqlServerCreateWorkOrders);
        Assert.Contains("source_key", SchemaUpgrader.SqlServerCreateWorkOrders);

        var indexSql = SchemaUpgrader.BuildFilteredUniqueIndexSql(
            "lf_work_orders", "IX_lf_work_orders_active_handler_issue", "handler_id, source_key, event_id",
            SchemaUpgrader.WorkOrderActiveIssueFilter);
        Assert.StartsWith("CREATE UNIQUE INDEX IX_lf_work_orders_active_handler_issue ON lf_work_orders", indexSql);
        Assert.Contains("WHERE closed_at IS NULL AND source_key IS NOT NULL", indexSql);
    }

    [Fact]
    public void EF模型的部分唯一索引過濾字串與升級器常數相同()
    {
        using var ctx = _fx.NewContext();
        var index = ctx.Model.FindEntityType(typeof(WorkOrderRow))!.GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_lf_work_orders_active_handler_issue");

        Assert.True(index.IsUnique);
        Assert.Equal(SchemaUpgrader.WorkOrderActiveIssueFilter, index.GetFilter());
    }
}
