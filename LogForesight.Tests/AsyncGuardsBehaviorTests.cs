using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 前端共用守門工具（序號守門、輪詢）的行為：直接載入 production 模組逐案檢查。
/// </summary>
public class AsyncGuardsBehaviorTests
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
    [InlineData("seqStale")]
    [InlineData("seqCurrent")]
    [InlineData("pollNoOverlap")]
    [InlineData("pollSurvivesThrow")]
    [InlineData("pollStop")]
    [InlineData("pollStopInFlight")]
    [InlineData("pollFalseStops")]
    public void 非同步守門工具行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "async-guards.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { "--experimental-default-type=module", script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(15), label: caseName);
        var stdout = r.Stdout;
        var stderr = r.Stderr;

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains($"PASS {caseName}", stdout);
    }
}
