using Xunit;

namespace LogForesight.Tests;

/// <summary>規則編輯的「試算影響」逐行說明（rules.js ruleTrialLines）。</summary>
public class RulesTrialBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("showsBeforeArrowAfterForBothNumbers")]
    [InlineData("lowerBoundAddsExplanationOnlyWhenLowerBound")]
    [InlineData("elevatedLineOnlyWhenChanged")]
    [InlineData("newRuleAndMessageConditionNotes")]
    [InlineData("unsupportedShowsOnlyReason")]
    public void 規則試算說明行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "rules-trial.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
