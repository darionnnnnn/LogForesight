using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 76-2 輪批次 D-2 的畫面行為：停用主機標記、處理人看不到的台數、續掛已停止（詳情與總覽）、
/// 停用或暫停處理人後的進行中單提示（0 張不提示、&gt;0 提示並帶已篩選的交辦總覽連結）。
/// 實際執行 work-order-detail.js／work-orders.js／users.js 的函式。
/// </summary>
public sealed class HandlerAvailabilityBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("handler-availability.behavior.mjs", "unseenFactHiddenWhenZero")]
    [InlineData("handler-availability.behavior.mjs", "unseenFactShownWhenPositive")]
    [InlineData("handler-availability.behavior.mjs", "disabledHostFactShownWhenPositive")]
    [InlineData("handler-availability.behavior.mjs", "stopNoteOnlyWhenStopped")]
    [InlineData("handler-availability.behavior.mjs", "listBadgeOnlyWhenStopped")]
    [InlineData("handler-availability.behavior.mjs", "memberHostCellMarksDisabledHost")]
    [InlineData("handler-availability.behavior.mjs", "pauseToggleOnlyForDispatchPoolMembers")]
    [InlineData("handler-availability.behavior.mjs", "noticeSilentWhenZero")]
    [InlineData("handler-availability.behavior.mjs", "noticeWithLinkWhenPositive")]
    [InlineData("handler-availability.behavior.mjs", "noticeWithoutLinkWhenViewerCannotOpenWorkOrders")]
    [InlineData("handler-availability.behavior.mjs", "noticeWithLinkWhenViewerHasViewAllOnly")]
    [InlineData("handler-availability.behavior.mjs", "handlerViewerDoesNotSeeAdminOnlyMarks")]
    public void 停用主機與處理人資格提示的production行為(string scriptName, string caseName)
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
