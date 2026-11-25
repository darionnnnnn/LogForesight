using Xunit;

namespace LogForesight.Tests;

/// <summary>排程作業頁「背景補跑窗口」那一列（D1）：沒有窗口、窗口內、窗口外三種說法。</summary>
public class RunsAiWindowBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("noWindowSaysNotAutomaticAndLinks")]
    [InlineData("inWindowSaysAnytime")]
    [InlineData("outsideWindowShowsNextTime")]
    [InlineData("missingFlagIsTreatedAsConfigured")]
    [InlineData("bothCallSitesUseTheSharedExit")]
    public void 背景補跑窗口列行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "runs-ai-window.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
