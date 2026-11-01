using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 勾選工廠 createPageSelection 的行為：實際執行 core/ui.js 的 production 函式，只替換 DOM。
/// </summary>
public class PageSelectionBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("selectAllOnlyTakesSelectableRows")]
    [InlineData("rowCheckboxSkipsUnselectableAndTracksSelection")]
    [InlineData("selectionKeptAcrossPages")]
    [InlineData("headerTriState")]
    [InlineData("clearEmptiesAndResyncs")]
    public void 勾選工廠行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "page-selection.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { "--experimental-default-type=module", script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        var stdout = r.Stdout;
        var stderr = r.Stderr;

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}
