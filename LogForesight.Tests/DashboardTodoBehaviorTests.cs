using Xunit;

namespace LogForesight.Tests;

/// <summary>K1：儀表板「未處理問題」卡副標的文字（見 JsBehavior/dashboard-todo.behavior.mjs）</summary>
public class DashboardTodoBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("splitsOpenAndInProgress")]
    [InlineData("zeroInProgressStillShown")]
    public void 待辦卡副標(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "dashboard-todo.behavior.mjs");
        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
