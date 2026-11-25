using Xunit;

namespace LogForesight.Tests;

/// <summary>我的交辦空狀態（handler-detail.js）：沒授權／可見範圍有未處理問題／真的沒事三分支，與補查的邊界。</summary>
public class HandlerEmptyStateBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("noAuthorizedHostsDoesNotFetch")]
    [InlineData("openIssuesRewriteTitleAndLink")]
    [InlineData("zeroOpenIssuesKeepsOriginalText")]
    [InlineData("exactlyOneOpenIssueRewrites")]
    [InlineData("fetchFailureKeepsOriginalText")]
    [InlineData("staleResponseIsDropped")]
    [InlineData("otherFiltersUseGenericTextAndDoNotFetch")]
    [InlineData("usesDashboardDaysSetting")]
    public void 我的交辦空狀態行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "handler-empty-state.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
