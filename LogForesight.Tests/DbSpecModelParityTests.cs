using System.Text;
using System.Text.RegularExpressions;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/DB-SPEC.md 的偽 DDL 區塊對 <see cref="LfDbContext"/> 的 EF 模型守門：
/// 每張 lf_ 表都必須有且只有一個區塊，欄名集合雙向相等，每欄的可空性一致。
/// 不以白名單略過任何一張表——新增表或欄位卻沒更新文件會在這裡變紅。
/// </summary>
public class DbSpecModelParityTests
{
    private sealed record SpecColumn(string Name, bool? Nullable, string Line);

    private sealed class SpecBlock
    {
        public string Table { get; init; } = "";
        public List<SpecColumn> Columns { get; } = new();
    }

    private static readonly Regex BlockHead = new(@"^(lf_\w+)(\s+--.*|\s*)$", RegexOptions.Compiled);
    private static readonly Regex ColumnLine = new(@"^\s{2,}([a-z_0-9]+)\s+(\S+)", RegexOptions.Compiled);
    private static readonly string[] SkipFirstTokens = { "PK", "UNIQUE", "INDEX", "FK" };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    internal static List<string> ParseableSpecLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').ToList();

    private static List<SpecBlock> ParseSpec(IReadOnlyList<string> lines)
    {
        var blocks = new List<SpecBlock>();
        var inFence = false;
        SpecBlock? cur = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                cur = null;
                continue;
            }
            if (!inFence) continue;
            if (string.IsNullOrWhiteSpace(line)) { cur = null; continue; }

            var head = BlockHead.Match(line);
            if (head.Success)
            {
                cur = new SpecBlock { Table = head.Groups[1].Value };
                blocks.Add(cur);
                continue;
            }
            if (cur is null) continue;

            var m = ColumnLine.Match(line);
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            if (SkipFirstTokens.Contains(name, StringComparer.Ordinal)) continue;

            var code = line;
            var cut = code.IndexOf("--", StringComparison.Ordinal);
            if (cut >= 0) code = code[..cut];
            bool? nullable;
            if (Regex.IsMatch(code, @"\bNOT\s+NULL\b")) nullable = false;
            else if (Regex.IsMatch(code, @"\bNULL\b")) nullable = true;
            else if (Regex.IsMatch(code, @"\bPK\b")) nullable = false;
            else nullable = null;
            cur.Columns.Add(new SpecColumn(name, nullable, line.Trim()));
        }
        return blocks;
    }

    private static Dictionary<string, Dictionary<string, bool>> ModelTables()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var ctx = new LfDbContext(options);
        var result = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
        foreach (var et in ctx.Model.GetEntityTypes())
        {
            var table = et.GetTableName();
            if (table is null || !table.StartsWith("lf_", StringComparison.Ordinal)) continue;
            var id = StoreObjectIdentifier.Table(table, et.GetSchema());
            var cols = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var p in et.GetProperties())
            {
                var col = p.GetColumnName(id);
                if (col is null) continue;
                cols[col] = p.IsNullable;
            }
            result[table] = cols;
        }
        return result;
    }

    internal static List<string> Compare(IReadOnlyList<string> specLines)
    {
        var failures = new List<string>();
        var blocks = ParseSpec(specLines);
        var model = ModelTables();

        foreach (var g in blocks.GroupBy(b => b.Table).Where(g => g.Count() > 1))
            failures.Add($"{g.Key}: 文件出現 {g.Count()} 個區塊（只能有一個）");

        foreach (var t in blocks.Select(b => b.Table).Distinct().Where(t => !model.ContainsKey(t)).OrderBy(t => t, StringComparer.Ordinal))
            failures.Add($"{t}: 文件有欄位區塊、模型沒有這張表");

        foreach (var (table, modelCols) in model.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var found = blocks.Where(b => b.Table == table).ToList();
            if (found.Count == 0)
            {
                failures.Add($"{table}: 文件缺少欄位區塊");
                continue;
            }
            var specCols = found.SelectMany(b => b.Columns).ToList();
            var specNames = specCols.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var name in modelCols.Keys.Where(n => !specNames.Contains(n)))
                failures.Add($"{table}.{name}: 模型有、文件缺");
            foreach (var c in specCols.Where(c => !modelCols.ContainsKey(c.Name)))
                failures.Add($"{table}.{c.Name}: 文件有、模型沒有");
            foreach (var d in specCols.GroupBy(c => c.Name).Where(x => x.Count() > 1))
                failures.Add($"{table}.{d.Key}: 文件重複列出 {d.Count()} 次");

            foreach (var c in specCols)
            {
                if (!modelCols.TryGetValue(c.Name, out var modelNullable)) continue;
                if (c.Nullable is null)
                    failures.Add($"{table}.{c.Name}: 文件未標註 NULL／NOT NULL（模型為 {(modelNullable ? "NULL" : "NOT NULL")}）");
                else if (c.Nullable.Value != modelNullable)
                    failures.Add($"{table}.{c.Name}: 可空性不一致，文件 {(c.Nullable.Value ? "NULL" : "NOT NULL")}，模型 {(modelNullable ? "NULL" : "NOT NULL")}");
            }
        }
        return failures;
    }

    [Fact]
    public void DbSpec_ColumnBlocks_MatchEfModel_ForEveryLfTable()
    {
        var path = Path.Combine(FindRepoRoot(), "docs", "DB-SPEC.md");
        var lines = ParseableSpecLines(File.ReadAllText(path, Encoding.UTF8));
        var failures = Compare(lines);
        Assert.True(failures.Count == 0, "DB-SPEC 與 EF 模型不一致：\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ModelHasAllExpectedTables_SoGuardIsNotVacuous()
    {
        Assert.Equal(24, ModelTables().Count);
    }

    [Fact]
    public void Compare_DetectsDroppedColumn_AndFlippedNullability_AndDuplicateBlock()
    {
        var path = Path.Combine(FindRepoRoot(), "docs", "DB-SPEC.md");
        var lines = ParseableSpecLines(File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Compare(lines));

        // 刪掉 lf_reports 的 file_name 一欄
        var dropped = lines.Where(l => !Regex.IsMatch(l, @"^\s+file_name\s+nvarchar\(255\)")).ToList();
        Assert.True(dropped.Count < lines.Count, "突變前提：應找得到 file_name 列");
        Assert.Contains(Compare(dropped), f => f.StartsWith("lf_reports.file_name", StringComparison.Ordinal));

        // 把 lf_reports.report_date 的 NOT NULL 改成 NULL
        var flipped = lines.Select(l => Regex.IsMatch(l, @"^\s+report_date\s+date NOT NULL")
            ? l.Replace("NOT NULL", "NULL") : l).ToList();
        Assert.Contains(Compare(flipped), f => f.StartsWith("lf_reports.report_date", StringComparison.Ordinal) && f.Contains("可空性"));

        // 同一張表出現兩個區塊
        var dup = lines.Concat(new[] { "```", "lf_reports -- 重複", "  report_id bigint PK", "```" }).ToList();
        Assert.Contains(Compare(dup), f => f.StartsWith("lf_reports:", StringComparison.Ordinal) && f.Contains("2 個區塊"));

        // 文件殘留模型已刪的表
        var ghost = lines.Concat(new[] { "```", "lf_ghost_table", "  ghost_id bigint NOT NULL PK", "```" }).ToList();
        Assert.Contains(Compare(ghost), f => f.StartsWith("lf_ghost_table:", StringComparison.Ordinal) && f.Contains("模型沒有"));
    }
}
