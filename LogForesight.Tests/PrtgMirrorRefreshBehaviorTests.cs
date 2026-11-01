using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>執行正式 loadScopeSnapshot／renderScopeSnapshot／refreshRiskReviewPanel 的可控 API 行為案例。</summary>
public sealed class PrtgMirrorRefreshBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("manualMapRejectOnlyManualMapAreaShowsError")]
    [InlineData("mirrorRejectOnlyMirrorAreaShowsError")]
    [InlineData("ipExcludesRejectOnlyExcludesAreaShowsError")]
    [InlineData("revisionMismatchShowsSameMessageInAllThreeAreas")]
    [InlineData("allSucceedRendersThreeAreasAndStoresRevision")]
    [InlineData("riskReviewPendingRequestIsSilent")]
    [InlineData("rewriteDuringInFlightTriggersOneRerun")]
    [InlineData("threeCallsDuringInFlightStillTwoRounds")]
    [InlineData("callDuringRerunTriggersThirdRound")]
    [InlineData("inFlightFailureStillReruns")]
    [InlineData("noConcurrencyRunsOnce")]
    [InlineData("mirrorSuccessClearsErrorSlotAndKeepsStatsNodes")]
    [InlineData("overlappingRiskReviewCallsRenderPendingListOnce")]
    [InlineData("partialFailureStillStoresRevision")]
    public void PRTG鏡像重整分區錯誤的production行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "prtg-mirror-refresh.behavior.mjs");
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
