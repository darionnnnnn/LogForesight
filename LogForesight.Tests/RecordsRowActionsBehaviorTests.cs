using Xunit;

namespace LogForesight.Tests;

/// <summary>第 74 輪批次F：問題查詢依問題視角的列內動作（一顆主要動作＋「更多」選單）的 production 行為。</summary>
public sealed class RecordsRowActionsBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("records-row-actions.behavior.mjs", "fourActionsKeepFirstInlineRestInMenu")]
    [InlineData("records-row-actions.behavior.mjs", "threeActionsAdminNotMine")]
    [InlineData("records-row-actions.behavior.mjs", "twoActionsInlineWithoutMenu")]
    [InlineData("records-row-actions.behavior.mjs", "oneAction")]
    [InlineData("records-row-actions.behavior.mjs", "noActionReturnsEmpty")]
    [InlineData("records-row-actions.behavior.mjs", "menuItemFocusesToggleBeforeOpening")]
    public void 問題查詢列內動作的production行為(string scriptName, string caseName)
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
