using Xunit;

namespace LogForesight.Tests;

/// <summary>第 66 輪終檢：風險日詳情側軌接線、處理面板 onRefreshed、報告卡不記展開狀態的 production 行為。</summary>
public sealed class RecordDetailRailBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("record-detail-rail.behavior.mjs", "hashHandlingWideExpandsOnceAndClearsHash")]
    [InlineData("record-detail-rail.behavior.mjs", "hashHandlingNarrowOnlyClearsHash")]
    [InlineData("record-detail-rail.behavior.mjs", "hashHandlingCaseGrantWithoutRailStillClearsHash")]
    [InlineData("record-detail-rail.behavior.mjs", "noHashDoesNotTouchHistory")]
    [InlineData("record-detail-rail.behavior.mjs", "summaryShowsOverdueSuffixAndLogCount")]
    [InlineData("record-detail-rail.behavior.mjs", "expandForSelectionRules")]
    [InlineData("record-detail-rail.behavior.mjs", "panelNotifiesOnRefreshed")]
    [InlineData("record-detail-rail.behavior.mjs", "panelInternalReloadsKeepOptions")]
    [InlineData("report-card.behavior.mjs", "withoutStorageKeyAlwaysCollapsedAndNeverStores")]
    [InlineData("report-card.behavior.mjs", "withStorageKeyStillRemembers")]
    public void 側軌接線與報告卡的production行為(string scriptName, string caseName)
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

    [Fact]
    public void RecordDetail只經onRefreshed更新側軌_不再直接呼叫()
    {
        var js = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "record-detail.js"));
        Assert.Contains("onRefreshed: updateSideRail", js);
        Assert.DoesNotContain("    updateSideRail(panelInfo);", js);
    }
}
