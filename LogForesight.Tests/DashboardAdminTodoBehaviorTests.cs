using Xunit;

namespace LogForesight.Tests;

/// <summary>第 81 輪批次 F（F11-1）：儀表板「需要你處理」一列與待派工問題卡副標的 production 行為。</summary>
public sealed class DashboardAdminTodoBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("dashboard-admin-todo.behavior.mjs", "itemsLinkToTheirHandlingPlaces")]
    [InlineData("dashboard-admin-todo.behavior.mjs", "zeroCategoriesAreOmitted")]
    [InlineData("dashboard-admin-todo.behavior.mjs", "allZeroCollapsesToOneLine")]
    [InlineData("dashboard-admin-todo.behavior.mjs", "notShownWithoutAssign")]
    [InlineData("dashboard-admin-todo.behavior.mjs", "tooLargePendingStillPointsToGaps")]
    [InlineData("dashboard-admin-todo.behavior.mjs", "rerenderReplacesPreviousContent")]
    [InlineData("dashboard-admin-todo.behavior.mjs", "pendingCardHintSplitsTwoSections")]
    public void 需要你處理一列的production行為(string scriptName, string caseName)
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
