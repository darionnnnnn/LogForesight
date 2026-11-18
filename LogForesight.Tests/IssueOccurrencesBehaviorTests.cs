using Xunit;

namespace LogForesight.Tests;

/// <summary>問題查詢列展開（records.js）：日期連結、另有、請求端點與空狀態。</summary>
public class IssueOccurrencesBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("fourteenDatesNoMore")]
    [InlineData("fifteenthDayBecomesMoreLink")]
    [InlineData("occurrencesEmptyShowsEmptyState")]
    [InlineData("occurrencesRequestUsesIssueHostsEndpoint")]
    [InlineData("occurrencesDoesNotCallDetailEndpoint")]
    public void 問題查詢展開行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "issue-occurrences.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
