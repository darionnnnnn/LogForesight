using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 儀表板「只顯示第一排、其餘點一下展開」（core/reveal.js）與待處理組順序的行為。
/// 實際執行 production 模組，只替換量測、observer、storage 與 DOM。
/// </summary>
public class DashboardRevealBehaviorTests
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
    [InlineData("collapsesBeyondFirstRow")]
    [InlineData("zeroColumnsMeansNotLaidOutSoNothingHidden")]
    [InlineData("noButtonWhenEverythingFits")]
    [InlineData("sharedButtonSpansAllGridsAndCountsAlerts")]
    [InlineData("toggleExpandsAndRemembers")]
    [InlineData("rememberedOpenStartsExpanded")]
    [InlineData("storageFailureStillToggles")]
    [InlineData("resizeRecomputesAndUnhides")]
    [InlineData("fontScaleEventRecomputes")]
    [InlineData("disposeReleasesObserverListenerAndBar")]
    [InlineData("countsTracksIncludingCollapsedZeroTracks")]
    [InlineData("todoGroupOrder")]
    public void 第一排揭露與待處理順序(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "dashboard-reveal.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { "--experimental-default-type=module", script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
