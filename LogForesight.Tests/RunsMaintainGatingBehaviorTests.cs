using Xunit;

namespace LogForesight.Tests;

/// <summary>排程作業頁（runs.js）：沒有 Maintain 時不送三個需要 Maintain 的端點，三處顯示替代文字。</summary>
public class RunsMaintainGatingBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("devMonitorSendsNoMaintainRequestsAndShowsText")]
    [InlineData("devMonitorUsesStatusProjectionForModuleState")]
    [InlineData("maintainSendsAllThreeRequests")]
    public void 排程頁Maintain能力行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "runs-maintain-gating.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
