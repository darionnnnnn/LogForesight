using Xunit;

namespace LogForesight.Tests;

/// <summary>排程作業頁的階段時間軸與總表執行時間：實際執行 core/run-timeline.js。</summary>
public class RunsTimelineBehaviorTests
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
    [InlineData("barsArePositionedByRunSpan")]
    [InlineData("valueFillIsTopLevelNotSubPhase")]
    [InlineData("missingDayTimingFieldsShowDash")]
    [InlineData("skippedPhaseSaysSo")]
    [InlineData("runningPhaseDrawsToNow")]
    [InlineData("interruptedRunStopsAtLastActivity")]
    [InlineData("legacyRunHasNoPhases")]
    [InlineData("phaseSecondsIgnoresSkippedAndOpen")]
    [InlineData("dayTimingCellTexts")]
    [InlineData("dayTimingBranchesShowEveryPath")]
    [InlineData("dayTimingTwoLines")]
    [InlineData("dayTimingFourPathsAndSubPhases")]
    [InlineData("prtgPauseLabelText")]
    [InlineData("dayTimingOtherAiOnlyDay")]
    [InlineData("dayTimingLegacyNoPhases")]
    [InlineData("dayTimingTotalRow")]
    public void 時間軸與執行時間(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "runs-timeline.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { "--experimental-default-type=module", script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
