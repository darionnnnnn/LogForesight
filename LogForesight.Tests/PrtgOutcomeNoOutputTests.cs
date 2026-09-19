using System.Reflection;
using System.Text.RegularExpressions;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG「無產出」結局：階段都成功但沒有任何可評估的對象時不能顯示成綠色的「成功」，
/// 以及前後端結局常數與 runs.js 對照表的守門（漏登錄會靜默顯示空白）。
/// </summary>
public class PrtgOutcomeNoOutputTests
{
    public static TheoryData<string, bool, bool, bool, bool, bool, bool, string, string?> DayCases => new()
    {
        // 名稱, syncFailed, anyFailure, rulesAvailable, sensorMirrorEmpty, mapAvailable, conservative, 預期結局, 預期原因
        { "正常", false, false, true, false, true, false, BatchRun.PrtgOutcomeSuccess, null },
        { "保守策略正常", false, false, true, false, true, true, BatchRun.PrtgOutcomeSuccess, "數值由快照供應" },
        { "無規則", false, false, false, false, true, false, BatchRun.PrtgOutcomeNoOutput,
            "規則庫沒有啟用中的 PRTG 規則（請至規則維護頁套用內建規則更新）" },
        { "鏡像空", false, false, true, true, true, false, BatchRun.PrtgOutcomeNoOutput,
            "PRTG 感測器鏡像是空的（請至 PRTG 維護頁執行同步結構與對應）" },
        // 同步失敗且同時無規則：失敗不得被無產出蓋掉
        { "同步失敗", true, false, false, true, false, false, BatchRun.PrtgOutcomeFailed, null },
    };

    [Theory]
    [MemberData(nameof(DayCases))]
    public void 逐日結局與原因(string name, bool syncFailed, bool anyFailure, bool rulesAvailable,
        bool sensorMirrorEmpty, bool mapAvailable, bool conservative, string expectedOutcome, string? expectedNote)
    {
        var (outcome, note) = PrtgDailyPipeline.ClassifyDay(
            syncFailed, anyFailure, rulesAvailable, sensorMirrorEmpty, mapAvailable, conservative);

        Assert.True(expectedOutcome == outcome, $"{name}：結局 {outcome}");
        Assert.Equal(expectedNote, note);
    }

    [Fact]
    public void 無主機對應_判無產出並帶對應原因()
    {
        var (outcome, note) = PrtgDailyPipeline.ClassifyDay(false, false, true, false, false, false);

        Assert.Equal(BatchRun.PrtgOutcomeNoOutput, outcome);
        Assert.Equal("沒有任何主機對應到 PRTG 裝置（請檢查主機清單 IP 與 PRTG 裝置位址）", note);
    }

    [Fact]
    public void 後端每個PRTG結局常數都登錄在runs_js的對照表()
    {
        var values = typeof(BatchRun)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.StartsWith("PrtgOutcome", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.Contains(BatchRun.PrtgOutcomeNoOutput, values);

        var js = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"));
        var block = Regex.Match(js, @"const PRTG_OUTCOME_META = \{(?<body>.*?)\n\};", RegexOptions.Singleline);
        Assert.True(block.Success, "runs.js 找不到 PRTG_OUTCOME_META");
        var keys = Regex.Matches(block.Groups["body"].Value, @"^\s*(?<key>[A-Za-z_]+)\s*:\s*\{", RegexOptions.Multiline)
            .Select(m => m.Groups["key"].Value)
            .ToHashSet();

        foreach (var value in values)
        {
            Assert.True(keys.Contains(value), $"PRTG_OUTCOME_META 缺少結局 '{value}'（現有：{string.Join(",", keys)}）");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }
}
