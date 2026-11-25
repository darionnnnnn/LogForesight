using Xunit;

namespace LogForesight.Tests;

/// <summary>執行總表「補跑這些主機」：把模式、資料日、主機三個參數帶進既有的立即執行彈窗；只給 Maintain。</summary>
public class RunsRerunBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("carriesModeDateAndHosts")]
    [InlineData("yesterdayIsOneDayBack")]
    [InlineData("overCapListFallsBackToAllHosts")]
    [InlineData("buttonOnlyForMaintainAndOnlyWhenRerunnable")]
    public void 補跑參數帶入立即執行彈窗(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "runs-rerun.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
