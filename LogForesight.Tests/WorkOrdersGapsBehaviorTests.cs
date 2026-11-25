using Xunit;

namespace LogForesight.Tests;

/// <summary>第 81 輪批次 F（F-2）：待派頁三段、授權入口、單題與整頁派工預覽、靜音中計數、不再自動派工收合的 production 行為。</summary>
public sealed class WorkOrdersGapsBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("work-orders-gaps.behavior.mjs", "unseenGroupAppearsOnceInUnassignableSection")]
    [InlineData("work-orders-gaps.behavior.mjs", "grantControlListsPoolGroupsAndEnablesOnSelect")]
    [InlineData("work-orders-gaps.behavior.mjs", "grantConfirmsPutsAndReloadsGaps")]
    [InlineData("work-orders-gaps.behavior.mjs", "grantCancelDoesNothing")]
    [InlineData("work-orders-gaps.behavior.mjs", "noMaintainShowsContactAdminWithoutLink")]
    [InlineData("work-orders-gaps.behavior.mjs", "noPoolGroupsShowsExplanationAndLink")]
    [InlineData("work-orders-gaps.behavior.mjs", "emptyDispatchableShowsEmptyText")]
    [InlineData("work-orders-gaps.behavior.mjs", "nonEmptyDispatchableRendersTableWithoutEmptyText")]
    [InlineData("work-orders-gaps.behavior.mjs", "perRowButtonOnlyWithAssign")]
    [InlineData("work-orders-gaps.behavior.mjs", "singleIssueDispatchPreviewsThenConfirmsThenPosts")]
    [InlineData("work-orders-gaps.behavior.mjs", "wholePageDispatchOmitsIssueAndShowsSummary")]
    [InlineData("work-orders-gaps.behavior.mjs", "previewNothingToDispatchShowsMessageWithoutConfirm")]
    [InlineData("work-orders-gaps.behavior.mjs", "resultNothingToDispatchShowsMessageNotZeroCounts")]
    [InlineData("work-orders-gaps.behavior.mjs", "cancelledConfirmDoesNotDispatch")]
    [InlineData("work-orders-gaps.behavior.mjs", "resultNoteReportsFailedGroups")]
    [InlineData("work-orders-gaps.behavior.mjs", "mutedCountUsesLightEndpointOnly")]
    [InlineData("work-orders-gaps.behavior.mjs", "activeTabNeverRequestsIssueOwnerList")]
    [InlineData("work-orders-gaps.behavior.mjs", "initDoesNotFetchMutedIssueList")]
    [InlineData("work-orders-gaps.behavior.mjs", "dismissedIsCollapsedAndLoadsOnFirstExpand")]
    [InlineData("work-orders-gaps.behavior.mjs", "openDismissedExpandsAndScrolls")]
    [InlineData("work-orders-gaps.behavior.mjs", "sectionStatsText")]
    public void 待派頁的production行為(string scriptName, string caseName)
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
