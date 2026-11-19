using System.Text.Json;
using System.Text.RegularExpressions;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 79 輪批次 H：重複實作收斂後的單一出口守門與行為。</summary>
internal static class ConvergenceScan
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    /// <summary>Core 與 Web 的正式碼檔案（排除 bin／obj）。</summary>
    public static IEnumerable<string> ProductionFiles(string root)
    {
        var sep = Path.DirectorySeparatorChar;
        return new[] { "LogForesight.Core", "LogForesight.Web" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(sep + "bin" + sep) && !f.Contains(sep + "obj" + sep));
    }

    public static List<string> Offenders(Regex pattern, params string[] allowedFileNames)
    {
        var root = FindRepoRoot();
        return ProductionFiles(root)
            .Where(f => !allowedFileNames.Contains(Path.GetFileName(f)))
            .Where(f => pattern.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
    }
}

public class SampleStatisticsTests
{
    [Fact]
    public void 奇數筆取正中間那筆()
    {
        Assert.Equal(3.0, SampleStatistics.Median(new[] { 5.0, 1.0, 3.0 }));
    }

    [Fact]
    public void 偶數筆取中間兩值平均()
    {
        Assert.Equal(2.5, SampleStatistics.Median(new[] { 4.0, 1.0, 2.0, 3.0 }));
    }

    [Fact]
    public void 整數樣本偶數筆同樣取平均而不是下中位數()
    {
        // 校準匯出的「每晚 sensor 數中位」過去取 sorted[n/2]（偶數筆取偏高的一筆）；現在與其餘出口同一定義
        Assert.Equal(2.5, SampleStatistics.Median(new[] { 4, 1, 2, 3 }));
        Assert.Equal(15.0, SampleStatistics.Median(new[] { 10, 20 }));
    }

    [Fact]
    public void 空集合回傳null_單筆回傳自己()
    {
        Assert.Null(SampleStatistics.Median(Array.Empty<double>()));
        Assert.Equal(7.0, SampleStatistics.Median(new[] { 7.0 }));
    }

    [Fact]
    public void 診斷匯出的四捨五入中位數與統計出口對同一份資料相同()
    {
        var data = new[] { 1.0, 2.0, 4.0, 8.0 };
        Assert.Equal(SampleStatistics.Median(data), RunExportService.RoundedMedian(data));
        Assert.Equal(3.0, RunExportService.RoundedMedian(data));
    }

    [Theory]
    [InlineData(1, 0.95, 0)]   // 只有一筆：任何分位都是它
    [InlineData(20, 0.95, 18)] // ceil(19) - 1
    [InlineData(20, 0.0, 0)]   // 下界夾在第一筆
    [InlineData(20, 1.0, 19)]  // 上界夾在最後一筆
    [InlineData(21, 0.95, 19)] // ceil(19.95) - 1
    public void 最近秩位索引夾在合法範圍(int count, double fraction, int expected)
    {
        Assert.Equal(expected, SampleStatistics.NearestRankIndex(count, fraction));
    }

    [Fact]
    public void 中位數定義不再有第二份實作()
    {
        var offenders = ConvergenceScan.Offenders(
            new Regex(@"static\s+[\w<>\?\[\], ]+\s+Median\(", RegexOptions.CultureInvariant), "SampleStatistics.cs");
        Assert.True(offenders.Count == 0, "這些檔案自己定義了 Median，請改用 SampleStatistics：\n  " + string.Join("\n  ", offenders));
    }
}

public class PrtgValueRowUsabilityTests
{
    private static PrtgValueRow Row(string quality, double? coverage) => new() { Quality = quality, Coverage = coverage };

    [Theory]
    [InlineData(PrtgDataQuality.Ok, null, true)]
    [InlineData(PrtgDataQuality.Ok, 10.0, true)]
    [InlineData(PrtgDataQuality.Sampled, 75.0, true)]    // 門檻本身算可用
    [InlineData(PrtgDataQuality.Sampled, 74.9, false)]   // 差一點不算
    [InlineData(PrtgDataQuality.Sampled, null, false)]
    [InlineData(PrtgDataQuality.Unknown, 100.0, false)]
    [InlineData(PrtgDataQuality.NoData, 100.0, false)]
    public void 可用列判定_ok或達標的取樣列(string quality, double? coverage, bool expected)
    {
        var row = Row(quality, coverage);
        Assert.Equal(expected, PrtgValueRowUsability.Usable.Compile()(row));
        Assert.Equal(expected, PrtgValueRowUsability.IsUsable(row));
    }

    [Fact]
    public void 取樣達標與不足互斥且只涵蓋取樣列()
    {
        Assert.True(PrtgValueRowUsability.IsSampledUsable(Row(PrtgDataQuality.Sampled, 75)));
        Assert.False(PrtgValueRowUsability.IsSampledUsable(Row(PrtgDataQuality.Ok, 100)));
        Assert.True(PrtgValueRowUsability.IsSampledLowCoverage(Row(PrtgDataQuality.Sampled, 74.9)));
        Assert.False(PrtgValueRowUsability.IsSampledLowCoverage(Row(PrtgDataQuality.Sampled, 75)));
    }

    [Fact]
    public void 快照涵蓋查詢_SQLite翻譯得出來且展開了可用列判定()
    {
        using var ctx = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>().UseSqlite("Data Source=:memory:").Options);
        var sql = EfPrtgStore.BuildSnapshotValueCoverageQuery(ctx.PrtgValues, new DateTime(2026, 9, 1), new DateTime(2026, 9, 2)).ToQueryString();
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("75", sql);
        Assert.Contains("sampled", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 快照涵蓋查詢_SQLServer翻譯得出來且展開了可用列判定()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;").Options;
        using var ctx = new LfDbContext(options);
        var sql = EfPrtgStore.BuildSnapshotValueCoverageQuery(ctx.PrtgValues, new DateTime(2026, 9, 1), new DateTime(2026, 9, 2)).ToQueryString();
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("75", sql);
        Assert.Contains("sampled", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 帶投影的可用列標記_兩個後端都能展開翻譯()
    {
        foreach (var useSqlite in new[] { true, false })
        {
            var builder = new DbContextOptionsBuilder<LfDbContext>();
            if (useSqlite) builder.UseSqlite("Data Source=:memory:");
            else builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
            using var ctx = new LfDbContext(builder.Options);
            var sql = ctx.PrtgValues
                .Select(v => new { v.SensorObjid, Usable = PrtgValueRowUsability.IsUsable(v) ? 1 : 0, Low = PrtgValueRowUsability.IsSampledLowCoverage(v) ? 1 : 0 })
                .GroupBy(x => x.SensorObjid)
                .Select(g => new { g.Key, U = g.Sum(x => x.Usable), L = g.Sum(x => x.Low) })
                .ExpandPrtgUsability()
                .ToQueryString();
            Assert.Contains("75", sql);
        }
    }

    [Fact]
    public void 可用列門檻不再散在儲存層_只定義在PrtgValueRowUsability()
    {
        var offenders = ConvergenceScan.Offenders(
            new Regex(@"PrtgDataQuality\.Sampled\s*&&[^;]{0,80}SampledMinCoverage", RegexOptions.CultureInvariant),
            "PrtgValueRowUsability.cs", "PrtgEffectivenessService.cs");
        Assert.True(offenders.Count == 0, "這些檔案自己寫了可用列判定，請改用 PrtgValueRowUsability：\n  " + string.Join("\n  ", offenders));
    }
}

public class PrtgJsonTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void 字串欄位_字串數字布林都讀得到()
    {
        var el = Parse("""{"a":"x","b":12,"c":true,"d":false}""");
        Assert.Equal("x", PrtgJson.GetString(el, "a"));
        Assert.Equal("12", PrtgJson.GetString(el, "b"));
        Assert.Equal("true", PrtgJson.GetString(el, "c"));
        Assert.Equal("false", PrtgJson.GetString(el, "d"));
    }

    [Fact]
    public void 字串欄位_缺席null與物件都回null()
    {
        var el = Parse("""{"n":null,"o":{"x":1}}""");
        Assert.Null(PrtgJson.GetString(el, "missing"));
        Assert.Null(PrtgJson.GetString(el, "n"));
        Assert.Null(PrtgJson.GetString(el, "o"));
    }

    [Fact]
    public void 整數欄位_數字與數字字串都讀得到()
    {
        var el = Parse("""{"a":42,"b":"43","c":"abc","d":1.5}""");
        Assert.Equal(42L, PrtgJson.GetLong(el, "a"));
        Assert.Equal(43L, PrtgJson.GetLong(el, "b"));
        Assert.Null(PrtgJson.GetLong(el, "c"));
        Assert.Null(PrtgJson.GetLong(el, "d"));
        Assert.Null(PrtgJson.GetLong(el, "missing"));
    }

    [Fact]
    public void 非物件元素回null而不是擲例外()
    {
        var el = Parse("[1,2]");
        Assert.Null(PrtgJson.GetString(el, "a"));
        Assert.Null(PrtgJson.GetLong(el, "a"));
    }

    [Fact]
    public void PRTG回應欄位讀取方法不再有第二份實作()
    {
        var offenders = ConvergenceScan.Offenders(
            new Regex(@"static\s+(string|long)\?\s+(GetStringProperty|GetLongProperty|ReadLong|GetLong|GetString)\(\s*JsonElement",
                RegexOptions.CultureInvariant),
            "PrtgJson.cs", "KnownIssueRuleStore.cs", "ReportFileMigrator.cs", "CspReportController.cs");
        Assert.True(offenders.Count == 0, "這些檔案自己寫了 PRTG JSON 讀取 helper，請改用 PrtgJson：\n  " + string.Join("\n  ", offenders));
    }
}

public class NetiqAttemptStatusLiteralGuardTests
{
    // 狀態字面值（含讀取端哨兵 unknown）
    private static readonly Regex Literal = new("\"(success|failed|partial|running|stopped|empty-once|retained-incomplete|unknown)\"", RegexOptions.CultureInvariant);
    private static readonly Regex Context = new("attempt|receipt|netiq", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>非註解行同時出現 NetIQ 嘗試語境與狀態字面值就算違規。</summary>
    internal static List<string> FindOffendingLines(string text) =>
        text.Split('\n')
            .Where(line => { var t = line.TrimStart(); return !t.StartsWith("//") && !t.StartsWith("*") && !t.StartsWith("/*"); })
            .Where(line => Literal.IsMatch(line) && Context.IsMatch(line))
            .ToList();

    [Fact]
    public void 狀態字面值在常數定義以外不存在()
    {
        var root = ConvergenceScan.FindRepoRoot();
        var offenders = ConvergenceScan.ProductionFiles(root)
            .Where(f => Path.GetFileName(f) != "NetiqAttemptStatus.cs")
            .SelectMany(f => FindOffendingLines(File.ReadAllText(f)).Select(l => Path.GetRelativePath(root, f) + "：" + l.Trim()))
            .ToList();
        Assert.True(offenders.Count == 0, "NetIQ 嘗試狀態請用 NetiqAttemptStatus 常數與謂詞，不要寫字面值：\n  " + string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData("record.LatestNetiqAttemptStatus = \"success\";")]
    [InlineData("TryRecordAttempt(ids, date, runId, \"failed\");")]
    [InlineData("if (latest.Status == \"unknown\" && receipt != null) {}")]
    [InlineData("NetiqSourceAttempt.RecordBatch(b, ids, d, 1, \"running\");")]
    public void 守門能抓到字面值的反例(string line)
    {
        Assert.Single(FindOffendingLines(line));
    }

    [Theory]
    [InlineData("record.LatestNetiqAttemptStatus = NetiqAttemptStatus.Success;")]
    [InlineData("// netiq attempt \"success\" 只在註解")]
    [InlineData("job.Status = \"failed\";")]
    public void 守門不誤傷常數或無關語境(string line)
    {
        Assert.Empty(FindOffendingLines(line));
    }

    [Fact]
    public void 讀取端哨兵不在可寫入清單且不算重查()
    {
        Assert.False(NetiqAttemptStatus.Known.Contains(NetiqAttemptStatus.Unknown));
        Assert.False(NetiqAttemptStatus.LedgerNeedsRetry(NetiqAttemptStatus.Unknown));
        Assert.True(NetiqAttemptStatus.Known.Contains(NetiqAttemptStatus.Success));
    }
}

public class SmallExitConvergenceTests
{
    [Fact]
    public void PRTG佐證前綴_現行與歷史舊版都認得_其他文字不認()
    {
        Assert.True(PrtgCorroboration.HasCorroborationPrefix(PrtgCorroboration.StoragePrefix + "內文"));
        Assert.True(PrtgCorroboration.HasCorroborationPrefix(PrtgCorroboration.CapacityPrefix));
        Assert.True(PrtgCorroboration.HasCorroborationPrefix(PrtgCorroboration.OutagePrefix));
        foreach (var legacy in PrtgCorroboration.LegacyPrefixes)
            Assert.True(PrtgCorroboration.HasCorroborationPrefix(legacy.Old + "內文"));
        Assert.False(PrtgCorroboration.HasCorroborationPrefix("【其他訊號】內文"));
        Assert.False(PrtgCorroboration.HasCorroborationPrefix(""));
    }

    [Fact]
    public void PRTG佐證前綴字面值只定義在PrtgCorroboration()
    {
        var offenders = ConvergenceScan.Offenders(
            new Regex("【(儲存異常同日訊號|容量異常同日訊號|關機與監測異常同日訊號|儲存故障雙重確認|磁碟容量雙重確認|失聯獲 PRTG 證實)】", RegexOptions.CultureInvariant),
            "PrtgCorroboration.cs");
        Assert.True(offenders.Count == 0, "這些檔案自己寫了佐證前綴，請改用 PrtgCorroboration：\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void 已刪除使用者只有一種顯示_不帶id()
    {
        var names = new FakeNames();
        Assert.Equal("（已刪除）", NameFormat.UserLabel(names, null));
        Assert.Equal("（已刪除）", NameFormat.UserNameOnly(names, null));
        var user = new WebUser { UserId = 5, Account = "amy", DisplayName = "艾咪" };
        Assert.Equal("艾咪(amy)", NameFormat.UserLabel(names, user));
        Assert.Equal("艾咪", NameFormat.UserNameOnly(names, user));
    }

    [Fact]
    public void 已刪除使用者顯示文字在服務層只寫一份()
    {
        var offenders = ConvergenceScan.Offenders(
            new Regex("\"（已刪除）\"|\\(已刪除:\\{", RegexOptions.CultureInvariant),
            "NameFormat.cs");
        // 群組與主機的 (已刪除:{id}) 仍在 NameFormat 內；其餘檔案不得自己組使用者已刪除文字
        Assert.True(offenders.Count == 0, "這些檔案自己寫了已刪除顯示，請改用 NameFormat：\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void 主機鍵一律經HostNameKey_不手寫轉大寫()
    {
        var offenders = ConvergenceScan.Offenders(
            new Regex(@"[Hh]ostName\.ToUpperInvariant\(\)", RegexOptions.CultureInvariant));
        Assert.True(offenders.Count == 0, "這些檔案自己組主機鍵，請改用 HostNameKey.Of：\n  " + string.Join("\n  ", offenders));
    }

    private sealed class FakeNames : IUserDisplayNameService
    {
        public string Of(string? displayName) => displayName ?? string.Empty;
        public string WithAccount(string? displayName, string account) =>
            string.IsNullOrEmpty(displayName) ? account : $"{displayName}({account})";
        public string OfAccount(LogForesight.Core.Persistence.IUserStore users, string account) => account;
        public AccountDisplayRuleSet CurrentRules() => AccountDisplayFormatter.ParseRules(string.Empty);
    }
}
