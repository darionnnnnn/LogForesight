using Xunit;

namespace LogForesight.Tests;

/// <summary>第 76-2 輪批次 C-2：交辦總覽的管理者提醒區（三類都是 0 不顯示、有數字每類一行並連到對應位置）的 production 行為。</summary>
public sealed class WorkOrdersRemindersBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("work-orders-reminders.behavior.mjs", "allZeroShowsNothing")]
    [InlineData("work-orders-reminders.behavior.mjs", "nullShowsNothing")]
    [InlineData("work-orders-reminders.behavior.mjs", "allThreeClassesShowRowsWithLinks")]
    [InlineData("work-orders-reminders.behavior.mjs", "onlyNonZeroClassIsListed")]
    [InlineData("work-orders-reminders.behavior.mjs", "rerenderClearsPreviousContent")]
    [InlineData("work-orders-reminders.behavior.mjs", "detailListsAreCollapsedAndLinked")]
    [InlineData("work-orders-reminders.behavior.mjs", "unassignableAtNullAndTruncationNote")]
    [InlineData("work-orders-reminders.behavior.mjs", "reminderDaysZeroShowsPersistentNote")]
    [InlineData("work-orders-reminders.behavior.mjs", "loadFetchesEndpointAndRenders")]
    [InlineData("work-orders-reminders.behavior.mjs", "loadFailureLeavesAreaEmpty")]
    public void 管理者提醒區的production行為(string scriptName, string caseName)
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
