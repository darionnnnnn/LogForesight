using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SQL Server 手寫冪等 DDL（<see cref="SchemaUpgrader"/>）對 EF 模型的守門：
/// 基表 4 張由 EnsureCreated 從模型建，其餘 20 張另有手寫 DDL；兩邊各自維護，欄位增減漏改一邊
/// 只有在真實 SQL Server 才會爆。這裡只比「欄名集合」與「每欄 NULL／NOT NULL」，不比型別。
/// 模型以 UseSqlServer 建立但不連線。
/// </summary>
public sealed class SchemaUpgraderModelParityTests
{
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "lf_risky_events", SchemaUpgrader.SqlServerCreateRiskyEventsTable },
        new object[] { "lf_issue_handling", SchemaUpgrader.SqlServerCreateIssueHandling },
        new object[] { "lf_issue_cases", SchemaUpgrader.SqlServerCreateIssueCases },
        new object[] { "lf_record_handling", SchemaUpgrader.SqlServerCreateRecordHandling },
        new object[] { "lf_issue_first_seen", SchemaUpgrader.SqlServerCreateIssueFirstSeen },
        new object[] { "lf_permission_changes", SchemaUpgrader.SqlServerCreatePermissionChanges },
        new object[] { "lf_reports", SchemaUpgrader.SqlServerCreateReports },
        new object[] { "lf_prtg_devices", SchemaUpgrader.SqlServerCreatePrtgDevices },
        new object[] { "lf_prtg_sensors", SchemaUpgrader.SqlServerCreatePrtgSensors },
        new object[] { "lf_prtg_state_changes", SchemaUpgrader.SqlServerCreatePrtgStateChanges },
        new object[] { "lf_prtg_values", SchemaUpgrader.SqlServerCreatePrtgValues },
        new object[] { "lf_prtg_sampled_batches", SchemaUpgrader.SqlServerCreatePrtgSampledBatches },
        new object[] { "lf_prtg_host_map", SchemaUpgrader.SqlServerCreatePrtgHostMap },
        new object[] { "lf_prtg_manual_map", SchemaUpgrader.SqlServerCreatePrtgManualMap },
        new object[] { "lf_prtg_ip_excludes", SchemaUpgrader.SqlServerCreatePrtgIpExcludes },
        new object[] { "lf_prtg_scope_purge_jobs", SchemaUpgrader.SqlServerCreatePrtgScopePurgeJobs },
        new object[] { "lf_prtg_transfer_sessions", SchemaUpgrader.SqlServerCreatePrtgTransferSessions },
        new object[] { "lf_prtg_transfer_chunks", SchemaUpgrader.SqlServerCreatePrtgTransferChunks },
        new object[] { "lf_work_orders", SchemaUpgrader.SqlServerCreateWorkOrders },
        new object[] { "lf_work_order_events", SchemaUpgrader.SqlServerCreateWorkOrderEvents },
    };

    private static LfDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=x;Trusted_Connection=True")
            .Options;
        return new LfDbContext(options);
    }

    [Fact]
    public void 案例涵蓋二十張表且表名不重複()
    {
        var names = Cases().Select(c => (string)c[0]).ToList();
        Assert.Equal(20, names.Count);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DDL欄位與EF模型一致(string table, string ddl)
    {
        using var ctx = NewContext();
        var entity = ctx.Model.GetEntityTypes().FirstOrDefault(e => e.GetTableName() == table);
        Assert.True(entity != null, $"模型找不到表 {table}");
        var id = StoreObjectIdentifier.Table(table, entity!.GetSchema());
        var model = entity.GetProperties()
            .Where(p => p.GetColumnName(id) != null)
            .ToDictionary(p => p.GetColumnName(id)!, p => p.IsNullable, StringComparer.OrdinalIgnoreCase);

        var ddlColumns = ParseColumns(ddl);
        Assert.True(ddlColumns.Count > 0, $"{table} 的 DDL 解析不到任何欄位");
        // 後來才加的欄位走 Upgrade() 裡的 AddColumnIfMissing（舊庫補欄），不在 CREATE TABLE 內；一併納入比對
        foreach (var (col, nullable) in AddedColumns(table)) ddlColumns[col] = nullable;

        var onlyInDdl = ddlColumns.Keys.Except(model.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var onlyInModel = model.Keys.Except(ddlColumns.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var nullDiff = ddlColumns.Keys.Intersect(model.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(k => ddlColumns[k] != model[k])
            .Select(k => $"{k}: DDL {(ddlColumns[k] ? "NULL" : "NOT NULL")} / 模型 {(model[k] ? "NULL" : "NOT NULL")}")
            .OrderBy(x => x).ToList();

        Assert.True(onlyInDdl.Count == 0 && onlyInModel.Count == 0 && nullDiff.Count == 0,
            $"{table} DDL 與模型不一致。只在 DDL：[{string.Join(", ", onlyInDdl)}]；只在模型：[{string.Join(", ", onlyInModel)}]；NULL 性不同：[{string.Join("; ", nullDiff)}]");
    }

    [Fact]
    public void 解析器能處理行內多欄_具名約束_括號內逗號()
    {
        var cols = ParseColumns("""
            CREATE TABLE dbo.[t] (
                [a] int NOT NULL CONSTRAINT PK_t PRIMARY KEY, b nvarchar(10) NULL, c bit NOT NULL CONSTRAINT DF_t_c DEFAULT 1,
                d decimal(18,2) NULL,
                e nvarchar(5) NOT NULL CONSTRAINT CK_e CHECK(e IN (N'x,y',N'z')),
                CONSTRAINT UQ_t UNIQUE (a, b),
                PRIMARY KEY (a)
            )
            """);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, cols.Keys.OrderBy(x => x).ToArray());
        Assert.False(cols["a"]);
        Assert.True(cols["b"]);
        Assert.False(cols["c"]);
        Assert.True(cols["d"]);
        Assert.False(cols["e"]);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }

    /// <summary>讀 SchemaUpgrader.cs 原始碼，取出對該表的 AddColumnIfMissing（引數為「isSqlite ? SQLite 定義 : SQL Server 定義」，取第二個字串判斷 NULL 性）。</summary>
    private static List<(string Column, bool Nullable)> AddedColumns(string table)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "LogForesight.Core", "Persistence", "Sql", "SchemaUpgrader.cs"));
        var list = new List<(string, bool)>();
        var pattern = @"AddColumnIfMissing\(ctx,\s*isSqlite,\s*""" + Regex.Escape(table) + @"""\s*,\s*""(?<col>[A-Za-z0-9_]+)""\s*,(?<rest>[^;]*?)\);";
        foreach (Match m in Regex.Matches(source, pattern, RegexOptions.Singleline))
        {
            var literals = Regex.Matches(m.Groups["rest"].Value, @"""([^""]*)""");
            Assert.True(literals.Count > 0, $"{table}.{m.Groups["col"].Value} 的 AddColumnIfMissing 解析不到定義");
            var def = literals[Math.Min(1, literals.Count - 1)].Groups[1].Value;
            list.Add((m.Groups["col"].Value, !Regex.IsMatch(def, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase)));
        }
        return list;
    }

    /// <summary>解析 CREATE TABLE 括號內的欄位定義：欄名 → 是否可為 NULL。略過 CONSTRAINT／PRIMARY KEY／FOREIGN KEY／UNIQUE／CHECK／INDEX 行。</summary>
    private static Dictionary<string, bool> ParseColumns(string ddl)
    {
        var open = ddl.IndexOf('(');
        Assert.True(open > 0, "DDL 找不到左括號");
        var parts = new List<string>();
        var depth = 0;
        var inQuote = false;
        var start = open + 1;
        var end = -1;
        for (var i = open; i < ddl.Length; i++)
        {
            var ch = ddl[i];
            if (ch == '\'') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (ch == '(') depth++;
            else if (ch == ')')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
            else if (ch == ',' && depth == 1)
            {
                parts.Add(ddl[start..i]);
                start = i + 1;
            }
        }
        Assert.True(end > 0, "DDL 括號未配對");
        parts.Add(ddl[start..end]);

        var skip = new Regex(@"^(CONSTRAINT|PRIMARY\s+KEY|FOREIGN\s+KEY|UNIQUE|CHECK|INDEX)\b", RegexOptions.IgnoreCase);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Length == 0 || skip.IsMatch(part)) continue;
            var m = Regex.Match(part, @"^\[?(?<name>[A-Za-z_][A-Za-z0-9_]*)\]?\s+\S");
            Assert.True(m.Success, $"無法解析欄位定義：{part}");
            var notNull = Regex.IsMatch(part, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase);
            var nullable = !notNull;
            result[m.Groups["name"].Value] = nullable;
        }
        return result;
    }
}
