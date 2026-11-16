using Xunit;

namespace LogForesight.Tests;

/// <summary>系統健康「主機日恢復」列的文字（settings.js hostDayRecoveryStatus，第 75 輪 I-4）。</summary>
public sealed class HostDayRecoveryHealthBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("noneWaitingIsNormal")]
    [InlineData("queuedOversizedIsNotAProblem")]
    [InlineData("invalidRowsNeedAttention")]
    [InlineData("unknownReasonShownAsIs")]
    public void 主機日恢復列的文字(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "settings-host-day-recovery.behavior.mjs");
        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
