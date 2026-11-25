using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 G 的畫面行為：交辦總覽處理人篩選（負載看板點人名帶入）、批次改派勾選與就地面板流程、
/// 停用／暫停接單前的確認（有單與沒單、先不處理仍可停用）。實際執行 work-orders.js／work-orders-bulk.js／users.js 的函式。
/// </summary>
public sealed class WorkOrdersBulkReassignBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("work-orders-bulk.behavior.mjs", "loadBoardNameAppliesHandlerFilter")]
    [InlineData("work-orders-bulk.behavior.mjs", "unknownHandlerGetsAnOption")]
    [InlineData("work-orders-bulk.behavior.mjs", "loadBoardReassignButtonOnlyWhenAssignableAndHasOrders")]
    [InlineData("work-orders-bulk.behavior.mjs", "autoSelectWhenEverythingFitsOnePage")]
    [InlineData("work-orders-bulk.behavior.mjs", "autoSelectSkippedWhenMoreThanOnePage")]
    [InlineData("work-orders-bulk.behavior.mjs", "autoSelectBoundaryAtTheBatchLimit")]
    [InlineData("work-orders-bulk.behavior.mjs", "autoSelectDoesNothingWhenNotRequested")]
    [InlineData("work-orders-bulk.behavior.mjs", "bulkBarActionsFollowTheSelection")]
    [InlineData("work-orders-bulk.behavior.mjs", "previewLinesCarryEveryFlag")]
    [InlineData("work-orders-bulk.behavior.mjs", "resultSummaryReportsFailures")]
    [InlineData("work-orders-bulk.behavior.mjs", "panelPreviewThenApply")]
    [InlineData("work-orders-bulk.behavior.mjs", "panelApplyStaleStillNotifiesPage")]
    [InlineData("work-orders-bulk.behavior.mjs", "panelApplyDisabledWhenNothingToDo")]
    [InlineData("work-orders-bulk.behavior.mjs", "availabilityAsksWithCountsWhenOrdersExist")]
    [InlineData("work-orders-bulk.behavior.mjs", "availabilityReassignChoiceGoesToBatchFlow")]
    [InlineData("work-orders-bulk.behavior.mjs", "availabilityCancelStopsTheChange")]
    [InlineData("work-orders-bulk.behavior.mjs", "availabilitySilentWithoutOrdersOrSummary")]
    [InlineData("work-orders-bulk.behavior.mjs", "availabilityContinuationNoticeOnlyWhenNotInformed")]
    public void 批次改派與處理人篩選的production行為(string scriptName, string caseName)
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
