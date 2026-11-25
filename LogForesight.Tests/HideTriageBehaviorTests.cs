using Xunit;

namespace LogForesight.Tests;

/// <summary>第 81 輪批次 N：雜訊分流入口（F16-1 對照表、後果摘要與實際套用一致、F16-4 總覽文字、入口接線）的 production 行為。</summary>
public sealed class HideTriageBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("hide-triage.behavior.mjs", "tableHasExactlyEighteenCells")]
    [InlineData("hide-triage.behavior.mjs", "everyAnswerCombinationMapsToEndpointAndParams")]
    [InlineData("hide-triage.behavior.mjs", "unavailableReasonsAreSpecific")]
    [InlineData("hide-triage.behavior.mjs", "noRuleHidesPartialScopes")]
    [InlineData("hide-triage.behavior.mjs", "noOrdersUsesDefaultAnswer")]
    [InlineData("hide-triage.behavior.mjs", "closeNeedsAssignAndHandle")]
    [InlineData("hide-triage.behavior.mjs", "coerceOrdersFallsBackToDefault")]
    [InlineData("hide-triage.behavior.mjs", "validationBoundaries")]
    [InlineData("hide-triage.behavior.mjs", "summaryMatchesAppliedMechanism")]
    [InlineData("hide-triage.behavior.mjs", "applyTriageSendsPlanInOrderAndStopsOnFailure")]
    [InlineData("hide-triage.behavior.mjs", "pathsEncodeSourceAndRule")]
    [InlineData("hide-triage.behavior.mjs", "hideSummaryCellTexts")]
    [InlineData("hide-triage.behavior.mjs", "shortcutAppearsOnlyForMaintainOnOpenOrder")]
    [InlineData("hide-triage.behavior.mjs", "recordsIssueActionRequiresMaintain")]
    public void 分流入口的production行為(string scriptName, string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, scriptName);
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
