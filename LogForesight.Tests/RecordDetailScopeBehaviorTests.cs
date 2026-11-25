using Xunit;

namespace LogForesight.Tests;

/// <summary>H10：風險日詳情顯示範圍三個互斥選項（見 JsBehavior/record-detail-scope.behavior.mjs）</summary>
public class RecordDetailScopeBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("optionsAreThree")]
    [InlineData("countsEqualRows")]
    [InlineData("allEqualsPendingWhenNothingElse")]
    [InlineData("allSortsUnresolvedFirst")]
    [InlineData("noCollapsedRestSection")]
    public void 顯示範圍(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "record-detail-scope.behavior.mjs");
        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
