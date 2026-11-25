using Xunit;

namespace LogForesight.Tests;

/// <summary>批次 D（A1b／F22-2）：設定頁「測試連線」與精靈「儲存並測試」走明確的同步探測端點。</summary>
public class AiTestConnectionBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("clickPostsExplicitProbeAndShowsSuccess")]
    [InlineData("failedProbeShowsError")]
    [InlineData("notConfiguredShowsWarningNotError")]
    [InlineData("requestFailureShowsMessageAndRestores")]
    [InlineData("saveAndTestProbesBeforeReadingStatus")]
    [InlineData("checkingRecheckStopsWhenResultArrives")]
    [InlineData("checkingRecheckKeepsDirtyFormAndUpdatesBadgeOnly")]
    [InlineData("checkingRecheckHasAttemptCap")]
    [InlineData("notCheckingSchedulesNothing")]
    [InlineData("healthBackgroundRowTextCoversAllProbeStates")]
    public void AI測試連線行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "ai-test-connection.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
