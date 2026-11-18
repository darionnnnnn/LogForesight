using Xunit;

namespace LogForesight.Tests;

/// <summary>處理人詳情與使用者詳情的工作負載分頁：連續換頁保留頁碼、頁碼超出範圍退回、過期回應不覆蓋畫面。</summary>
public class WorkloadPagingBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("userDetailConsecutivePagingKeepsBothPages")]
    [InlineData("userDetailPageBeyondLastFallsBackOnce")]
    [InlineData("handlerDetailStaleFailureDoesNotReplaceNewerPage")]
    [InlineData("handlerDetailCurrentFailureStillShowsError")]
    public void 工作負載分頁行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "workload-paging.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
