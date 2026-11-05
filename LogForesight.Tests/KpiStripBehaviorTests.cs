using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// KPI 卡列 kpiStrip 的結構行為：實際執行 core/ui.js 的 production 函式，只替換 DOM 與 appUrl。
/// </summary>
public class KpiStripBehaviorTests
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
    [InlineData("emptyGroupsAreNotRendered")]
    [InlineData("singleRowOptionAddsModifierOnlyWhenAsked")]
    [InlineData("cardWithoutSubStillHasSubPlaceholder")]
    [InlineData("linkCardIsAnchorWithAppUrlHref")]
    [InlineData("variantAppliesToValue")]
    [InlineData("nodeSubIsAppendedNotParsed")]
    public void KPI卡列結構行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "kpi-strip.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        var stdout = r.Stdout;
        var stderr = r.Stderr;

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}
