using Xunit;

namespace LogForesight.Tests;

/// <summary>階段 A-2：交辦預覽的主機表格、可見範圍提示與候選人標記的 production 行為。</summary>
public sealed class WorkOrderPreviewBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("work-order-preview.behavior.mjs", "statusTextFourKinds")]
    [InlineData("work-order-preview.behavior.mjs", "statusTextOmitsMissingParts")]
    [InlineData("work-order-preview.behavior.mjs", "selectAllOnlyOrdinaryHosts")]
    [InlineData("work-order-preview.behavior.mjs", "headerIndeterminateWhenPartial")]
    [InlineData("work-order-preview.behavior.mjs", "closedRowGoesToReopenSet")]
    [InlineData("work-order-preview.behavior.mjs", "selectionSurvivesPaging")]
    [InlineData("work-order-preview.behavior.mjs", "checkboxHasLabelAndAriaLabel")]
    [InlineData("work-order-preview.behavior.mjs", "summaryShowsNoAccessWarning")]
    [InlineData("work-order-preview.behavior.mjs", "summaryShowsClosedSkips")]
    [InlineData("work-order-preview.behavior.mjs", "candidateBadgesAndOrder")]
    public void 交辦預覽的production行為(string scriptName, string caseName)
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
