using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 手寫建表語句對 EF 模型的「型別與長度」對帳（欄名與 NULL 性見 <see cref="SchemaUpgraderModelParityTests"/>）。
/// SQL Server 端比 <c>nvarchar(255)</c>、<c>bigint</c>、<c>datetime2</c> 這類完整型別字串（含長度）；
/// SQLite 端的字串欄一律是 TEXT、沒有長度，所以比儲存類別（TEXT／INTEGER／REAL／BLOB），
/// 長度只在 SQL Server 才有意義。對象是 <see cref="SchemaUpgrader"/> 的整張表建表語句與 <c>AddColumnIfMissing</c> 補欄定義。
/// 模型以 UseSqlServer／UseSqlite 建立但不連線，取的是設計階段模型的欄位型別（與產生 DDL 時同一份）。
/// </summary>
public sealed class SchemaUpgraderTypeParityTests
{
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "lf_risky_events", SchemaUpgrader.SqliteCreateRiskyEventsTable, SchemaUpgrader.SqlServerCreateRiskyEventsTable },
        new object[] { "lf_issue_handling", SchemaUpgrader.SqliteCreateIssueHandling, SchemaUpgrader.SqlServerCreateIssueHandling },
        new object[] { "lf_issue_cases", SchemaUpgrader.SqliteCreateIssueCases, SchemaUpgrader.SqlServerCreateIssueCases },
        new object[] { "lf_record_handling", SchemaUpgrader.SqliteCreateRecordHandling, SchemaUpgrader.SqlServerCreateRecordHandling },
        new object[] { "lf_issue_first_seen", SchemaUpgrader.SqliteCreateIssueFirstSeen, SchemaUpgrader.SqlServerCreateIssueFirstSeen },
        new object[] { "lf_permission_changes", SchemaUpgrader.SqliteCreatePermissionChanges, SchemaUpgrader.SqlServerCreatePermissionChanges },
        new object[] { "lf_reports", SchemaUpgrader.SqliteCreateReports, SchemaUpgrader.SqlServerCreateReports },
        new object[] { "lf_prtg_devices", SchemaUpgrader.SqliteCreatePrtgDevices, SchemaUpgrader.SqlServerCreatePrtgDevices },
        new object[] { "lf_prtg_sensors", SchemaUpgrader.SqliteCreatePrtgSensors, SchemaUpgrader.SqlServerCreatePrtgSensors },
        new object[] { "lf_prtg_state_changes", SchemaUpgrader.SqliteCreatePrtgStateChanges, SchemaUpgrader.SqlServerCreatePrtgStateChanges },
        new object[] { "lf_prtg_values", SchemaUpgrader.SqliteCreatePrtgValues, SchemaUpgrader.SqlServerCreatePrtgValues },
        new object[] { "lf_prtg_sampled_batches", SchemaUpgrader.SqliteCreatePrtgSampledBatches, SchemaUpgrader.SqlServerCreatePrtgSampledBatches },
        new object[] { "lf_prtg_host_map", SchemaUpgrader.SqliteCreatePrtgHostMap, SchemaUpgrader.SqlServerCreatePrtgHostMap },
        new object[] { "lf_prtg_manual_map", SchemaUpgrader.SqliteCreatePrtgManualMap, SchemaUpgrader.SqlServerCreatePrtgManualMap },
        new object[] { "lf_prtg_ip_excludes", SchemaUpgrader.SqliteCreatePrtgIpExcludes, SchemaUpgrader.SqlServerCreatePrtgIpExcludes },
        new object[] { "lf_prtg_scope_purge_jobs", SchemaUpgrader.SqliteCreatePrtgScopePurgeJobs, SchemaUpgrader.SqlServerCreatePrtgScopePurgeJobs },
        new object[] { "lf_prtg_transfer_sessions", SchemaUpgrader.SqliteCreatePrtgTransferSessions, SchemaUpgrader.SqlServerCreatePrtgTransferSessions },
        new object[] { "lf_prtg_transfer_chunks", SchemaUpgrader.SqliteCreatePrtgTransferChunks, SchemaUpgrader.SqlServerCreatePrtgTransferChunks },
        new object[] { "lf_work_orders", SchemaUpgrader.SqliteCreateWorkOrders, SchemaUpgrader.SqlServerCreateWorkOrders },
        new object[] { "lf_work_order_events", SchemaUpgrader.SqliteCreateWorkOrderEvents, SchemaUpgrader.SqlServerCreateWorkOrderEvents },
    };

    private static DbContextOptions<LfDbContext> SqlServerOptions() =>
        new DbContextOptionsBuilder<LfDbContext>().UseSqlServer("Server=.;Database=x;Trusted_Connection=True").Options;

    private static DbContextOptions<LfDbContext> SqliteOptions() =>
        new DbContextOptionsBuilder<LfDbContext>().UseSqlite("Data Source=:memory:").Options;

    // ── 突變用的模型：與正式模型只差一個欄位的長度／型別，用來證明對帳會變紅 ──

    /// <summary>把 PRTG 裝置名稱欄的長度由 255 改成 100。</summary>
    private sealed class ShortenedDeviceNameContext(DbContextOptions<LfDbContext> options) : LfDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            b.Entity<PrtgDeviceRow>().Property(x => x.Name).HasMaxLength(100);
        }
    }

    /// <summary>把交辦單的交辦人欄位型別由 bigint 改成 int（型別不同、長度無關）。</summary>
    private sealed class NarrowedHandlerIdContext(DbContextOptions<LfDbContext> options) : LfDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            b.Entity<WorkOrderRow>().Property(x => x.HandlerId).HasColumnType("int");
        }
    }

    /// <summary>模型欄位的完整儲存型別（小寫、去空白），以資料表欄名為鍵。</summary>
    private static Dictionary<string, string> ModelColumnTypes(LfDbContext ctx, string table)
    {
        var model = ctx.GetService<IDesignTimeModel>().Model;
        var entity = model.GetEntityTypes().FirstOrDefault(e => e.GetTableName() == table);
        Assert.True(entity != null, $"模型找不到表 {table}");
        var id = StoreObjectIdentifier.Table(table, entity!.GetSchema());
        return entity.GetProperties()
            .Where(p => p.GetColumnName(id) != null)
            .ToDictionary(p => p.GetColumnName(id)!,
                p => SchemaUpgraderModelParityTests.NormalizeType(p.GetColumnType(id)),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>DDL 與模型型別不同的欄位描述；空清單代表一致。只比兩邊都有的欄位（欄名集合由另一組測試守門）。</summary>
    private static List<string> TypeDiff(LfDbContext ctx, string table, string ddl,
        IEnumerable<(string Column, string Type)> addedColumns)
    {
        var model = ModelColumnTypes(ctx, table);
        var ddlTypes = SchemaUpgraderModelParityTests.ParseColumnDefs(ddl)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Type, StringComparer.OrdinalIgnoreCase);
        foreach (var (col, type) in addedColumns) ddlTypes[col] = type;
        Assert.True(ddlTypes.Count > 0, $"{table} 的 DDL 解析不到任何欄位");

        return ddlTypes.Keys.Intersect(model.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(k => !string.Equals(ddlTypes[k], model[k], StringComparison.Ordinal))
            .Select(k => $"{table}.{k}: DDL {ddlTypes[k]} / 模型 {model[k]}")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SQLServer建表語句的型別與長度和EF模型一致(string table, string sqliteDdl, string sqlServerDdl)
    {
        _ = sqliteDdl;
        using var ctx = new LfDbContext(SqlServerOptions());
        var added = AddedColumnTypes(table, sqlServerSide: true);
        var diff = TypeDiff(ctx, table, sqlServerDdl, added);
        Assert.True(diff.Count == 0, "SQL Server 建表語句與模型的型別／長度不一致：" + string.Join("；", diff));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SQLite建表語句的儲存類別和EF模型一致(string table, string sqliteDdl, string sqlServerDdl)
    {
        _ = sqlServerDdl;
        using var ctx = new LfDbContext(SqliteOptions());
        var added = AddedColumnTypes(table, sqlServerSide: false);
        var diff = TypeDiff(ctx, table, sqliteDdl, added);
        Assert.True(diff.Count == 0, "SQLite 建表語句與模型的儲存類別不一致：" + string.Join("；", diff));
    }

    [Fact]
    public void 其餘資料表的補欄定義型別與模型一致_兩個後端()
    {
        // 基表（EnsureCreated 建的）只靠 AddColumnIfMissing 補欄：逐表比對補欄定義。
        var coveredTables = Cases().Select(c => (string)c[0]).ToHashSet();
        var tables = AllAddedColumnTables().Except(coveredTables).OrderBy(t => t).ToList();
        Assert.NotEmpty(tables);
        var problems = new List<string>();
        foreach (var sqlServerSide in new[] { true, false })
        {
            using var ctx = new LfDbContext(sqlServerSide ? SqlServerOptions() : SqliteOptions());
            foreach (var table in tables)
            {
                var model = ModelColumnTypes(ctx, table);
                foreach (var (col, type) in AddedColumnTypes(table, sqlServerSide))
                    if (model.TryGetValue(col, out var modelType) && modelType != type)
                        problems.Add($"{(sqlServerSide ? "SQL Server" : "SQLite")} {table}.{col}: 補欄 {type} / 模型 {modelType}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("；", problems));
    }

    // ── 邊界兩側：模型長度或型別被改掉，對帳必須變紅；未改的模型對照組為綠 ──

    [Fact]
    public void 模型欄位長度被改掉時SQLServer對帳變紅()
    {
        using var ctx = new ShortenedDeviceNameContext(SqlServerOptions());
        var diff = TypeDiff(ctx, "lf_prtg_devices", SchemaUpgrader.SqlServerCreatePrtgDevices, []);
        Assert.Contains(diff, d => d.Contains("lf_prtg_devices.name") && d.Contains("nvarchar(255)") && d.Contains("nvarchar(100)"));
    }

    [Fact]
    public void 模型欄位長度未改時SQLServer對帳為綠_對照組()
    {
        using var ctx = new LfDbContext(SqlServerOptions());
        Assert.Empty(TypeDiff(ctx, "lf_prtg_devices", SchemaUpgrader.SqlServerCreatePrtgDevices, []));
    }

    [Fact]
    public void 模型欄位型別被改掉時SQLServer對帳變紅()
    {
        using var ctx = new NarrowedHandlerIdContext(SqlServerOptions());
        var diff = TypeDiff(ctx, "lf_work_orders", SchemaUpgrader.SqlServerCreateWorkOrders, []);
        Assert.Contains(diff, d => d.Contains("lf_work_orders.handler_id") && d.Contains("bigint") && d.Contains("int"));
    }

    [Fact]
    public void 模型欄位儲存類別被改掉時SQLite對帳變紅_未改時為綠()
    {
        using var mutated = new TextHandlerIdContext(SqliteOptions());
        var diff = TypeDiff(mutated, "lf_work_orders", SchemaUpgrader.SqliteCreateWorkOrders, []);
        Assert.Contains(diff, d => d.Contains("lf_work_orders.handler_id") && d.Contains("integer") && d.Contains("text"));

        using var original = new LfDbContext(SqliteOptions());
        Assert.Empty(TypeDiff(original, "lf_work_orders", SchemaUpgrader.SqliteCreateWorkOrders, []));
    }

    /// <summary>把交辦單的交辦人欄位改成文字儲存類別。</summary>
    private sealed class TextHandlerIdContext(DbContextOptions<LfDbContext> options) : LfDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            b.Entity<WorkOrderRow>().Property(x => x.HandlerId).HasColumnType("TEXT");
        }
    }

    // ── D8 守門：有手寫建表語句的表都要在 Cases 內，不能新增一張表卻漏了型別／長度對帳 ──

    private static readonly Regex CreateTablePattern = new(
        @"CREATE\s+TABLE\s+(?:dbo\.)?\[?(?<table>lf_[A-Za-z0-9_]+)\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>升級器原始碼中出現手寫 <c>CREATE TABLE</c> 的表名（去重；含重建表用的語句）。</summary>
    private static List<string> HandwrittenDdlTables(string upgraderSource) =>
        CreateTablePattern.Matches(upgraderSource).Select(m => m.Groups["table"].Value.ToLowerInvariant())
            .Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();

    /// <summary>有手寫建表語句、模型也有、卻不在對帳案例清單內的表。</summary>
    private static List<string> UncoveredTables(string upgraderSource, IEnumerable<string> coveredTables)
    {
        using var ctx = new LfDbContext(SqliteOptions());
        var modelTables = ctx.Model.GetEntityTypes().Select(e => e.GetTableName()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covered = coveredTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return HandwrittenDdlTables(upgraderSource).Where(t => modelTables.Contains(t) && !covered.Contains(t)).ToList();
    }

    [Fact]
    public void 有手寫建表語句的表都在型別對帳案例內()
    {
        var source = UpgraderSource();
        var tables = HandwrittenDdlTables(source);
        Assert.True(tables.Count >= 20, $"只掃到 {tables.Count} 張手寫建表的表，掃描規則可能已失效");

        var uncovered = UncoveredTables(source, Cases().Select(c => (string)c[0]));
        Assert.True(uncovered.Count == 0,
            "下列表在 SchemaUpgrader 有手寫建表語句，卻不在 SchemaUpgraderTypeParityTests.Cases：" + string.Join("、", uncovered));
    }

    [Fact]
    public void 新增手寫建表語句卻沒加入案例時守門變紅_反例()
    {
        var source = UpgraderSource() + "\n        CREATE TABLE lf_work_orders_extra (id bigint)\n        CREATE TABLE dbo.lf_prtg_devices (id bigint)\n";
        // lf_work_orders_extra 不在模型：不計；lf_prtg_devices 在 Cases：不計。拿掉 Cases 內的 lf_prtg_devices 後才會被點名
        var covered = Cases().Select(c => (string)c[0]).Where(t => t != "lf_prtg_devices");
        Assert.Equal(new[] { "lf_prtg_devices" }, UncoveredTables(source, covered));
        // 模型內存在的表被新增了手寫語句、且不在清單：以既有基表 lf_daily_records 代表「新加的一張表」
        var withNewTable = UpgraderSource() + "\n        CREATE TABLE lf_daily_records (id bigint)\n";
        Assert.Contains("lf_daily_records", UncoveredTables(withNewTable, Cases().Select(c => (string)c[0])));
    }

    [Fact]
    public void 型別解析器取得長度_括號內逗號_與max()
    {
        var defs = SchemaUpgraderModelParityTests.ParseColumnDefs("""
            CREATE TABLE dbo.[t] (
                [a] bigint NOT NULL CONSTRAINT PK_t PRIMARY KEY,
                b nvarchar( 255 ) NULL,
                c nvarchar(max) NOT NULL,
                d decimal(18, 2) NULL,
                e datetime2 NOT NULL
            )
            """);
        Assert.Equal("bigint", defs["a"].Type);
        Assert.Equal("nvarchar(255)", defs["b"].Type);
        Assert.Equal("nvarchar(max)", defs["c"].Type);
        Assert.Equal("decimal(18,2)", defs["d"].Type);
        Assert.Equal("datetime2", defs["e"].Type);
    }

    // ── 讀 SchemaUpgrader 原始碼的補欄呼叫：AddColumnIfMissing(ctx, isSqlite, "表", "欄", isSqlite ? "SQLite 定義" : "SQL Server 定義") ──

    private static readonly Regex AddColumnPattern = new(
        @"AddColumnIfMissing\(ctx,\s*isSqlite,\s*""(?<table>[A-Za-z0-9_]+)""\s*,\s*""(?<col>[A-Za-z0-9_]+)""\s*,(?<rest>[^;]*?)\)(?:\)|;|\s*\{)",
        RegexOptions.Singleline);

    private static string UpgraderSource() =>
        File.ReadAllText(Path.Combine(SchemaUpgraderModelParityTests.RepoRoot(),
            "LogForesight.Core", "Persistence", "Sql", "SchemaUpgrader.cs"));

    private static IEnumerable<string> AllAddedColumnTables() =>
        AddColumnPattern.Matches(UpgraderSource()).Select(m => m.Groups["table"].Value).Distinct();

    private static readonly Regex TypeToken = new(@"^\s*(?<type>[A-Za-z0-9]+(?:\s*\([^)]*\))?)", RegexOptions.Compiled);

    private static List<(string Column, string Type)> AddedColumnTypes(string table, bool sqlServerSide)
    {
        var list = new List<(string, string)>();
        foreach (Match m in AddColumnPattern.Matches(UpgraderSource()))
        {
            if (m.Groups["table"].Value != table) continue;
            var literals = Regex.Matches(m.Groups["rest"].Value, @"""([^""]*)""");
            Assert.True(literals.Count > 0, $"{table}.{m.Groups["col"].Value} 的 AddColumnIfMissing 解析不到定義");
            // 兩個字串字面值依序是 SQLite 定義與 SQL Server 定義；只有一個時兩邊共用
            var def = literals[sqlServerSide ? Math.Min(1, literals.Count - 1) : 0].Groups[1].Value;
            var t = TypeToken.Match(def);
            Assert.True(t.Success, $"{table}.{m.Groups["col"].Value} 的補欄定義解析不到型別：{def}");
            list.Add((m.Groups["col"].Value, SchemaUpgraderModelParityTests.NormalizeType(t.Groups["type"].Value)));
        }
        return list;
    }
}
