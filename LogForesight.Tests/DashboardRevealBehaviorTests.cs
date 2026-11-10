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
    [InlineData("emptyGridsMountNothing")]
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
    [InlineData("onCollapsedChange初始與切換都呼叫且只傳展開狀態")]
    [InlineData("onCollapsedChange記憶為展開時初始傳true")]
    [InlineData("onCollapsedChange不受量測與字級事件影響")]
    [InlineData("沒給onCollapsedChange照常運作")]
    [InlineData("dashboard收合時strip有collapsed類別且展開移除")]
    [InlineData("todoGroupOrder")]
    public void 第一排揭露與待處理順序(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "dashboard-reveal.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }

    [Fact]
    public void 收合時三組等寬的CSS規則只在放不下全部卡片的寬度_更寬維持依卡數分寬()
    {
        var css = ReadSiteCss();
        var start = css.IndexOf("@media (min-width: 576px) and (max-width: 1799.98px) {", StringComparison.Ordinal);
        Assert.True(start >= 0, "收合等寬要有寬度上限：更寬的畫面全部卡片放得下，等寬會讓卡多的組藏卡");
        var end = css.IndexOf("\n}\n", start, StringComparison.Ordinal);
        Assert.Contains(".lf-kpi-strip--row.lf-kpi-strip--collapsed .lf-kpi-group {\n        flex: 1 1 0;", css[start..end]);
        // 依卡數分寬的規則仍在，且收合規則排在它之後才能蓋過
        var perCount = css.IndexOf(".lf-kpi-strip--row .lf-kpi-group {\n        flex: var(--lf-kpi-count, 1) 1 0;", StringComparison.Ordinal);
        Assert.True(perCount >= 0 && start > perCount, "收合規則要在依卡數分寬規則之後");
    }

    [Fact]
    public void 定位高亮樣式走focus_ring且尊重減少動態()
    {
        var css = ReadSiteCss();
        Assert.Contains(".lf-focus-flash > td {\n    box-shadow: inset var(--lf-focus-ring);", css);
        var motion = css.IndexOf("@media (prefers-reduced-motion: no-preference) {\n    .lf-focus-flash", StringComparison.Ordinal);
        Assert.True(motion >= 0, "淡出動畫只能放在 no-preference 之內");
    }

    private static string ReadSiteCss() =>
        SiteCss.Read().Replace("\r\n", "\n");
}
