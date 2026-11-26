using Xunit;

namespace LogForesight.Tests;

/// <summary>第 81 輪補修 B1：儀表板「24 小時登入失敗」卡下鑽與稽核頁套用參數的 production 行為。</summary>
public sealed class DashboardLoginFailuresBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("dashboard-login-failures.behavior.mjs", "drillDownUsesActionAndLocalDates")]
    [InlineData("dashboard-login-failures.behavior.mjs", "cardHintMentionsYesterday")]
    [InlineData("dashboard-login-failures.behavior.mjs", "auditPageAppliesDrillDownParams")]
    public void 登入失敗下鑽的production行為(string scriptName, string caseName)
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
}
