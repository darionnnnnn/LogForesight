using Xunit;

namespace LogForesight.Tests;

/// <summary>第 76-2 輪批次 B-2：交辦總覽待派頁籤下方「不再自動派工」區（0 筆說明、依問題分組、按鈕、分頁、恢復流程）的 production 行為。</summary>
public sealed class WorkOrdersDismissedBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("work-orders-dismissed.behavior.mjs", "emptyShowsExplanation")]
    [InlineData("work-orders-dismissed.behavior.mjs", "groupsRenderOneTablePerIssue")]
    [InlineData("work-orders-dismissed.behavior.mjs", "releaseColumnShowsDaysPreLaunchOrPermanent")]
    [InlineData("work-orders-dismissed.behavior.mjs", "restoreButtonsExistOnlyWithCapability")]
    [InlineData("work-orders-dismissed.behavior.mjs", "paginationUsesServerPageSize")]
    [InlineData("work-orders-dismissed.behavior.mjs", "restoreRowConfirmsThenPostsAndReloads")]
    [InlineData("work-orders-dismissed.behavior.mjs", "restoreIssueConfirmsThenPosts")]
    [InlineData("work-orders-dismissed.behavior.mjs", "cancelDoesNothing")]
    public void 不再自動派工區的production行為(string scriptName, string caseName)
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
