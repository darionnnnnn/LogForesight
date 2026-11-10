using Xunit;

namespace LogForesight.Tests;

/// <summary>風險日詳情「問題」欄：白話說明緊接標題、與 knownIssue 去重，guidancePanel 不再重複輸出說明。</summary>
public sealed class RecordDetailIssueCellBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("explanationAndKnownIssueBeforeLogName")]
    [InlineData("identicalTextShownOnce")]
    [InlineData("noGuidanceLeavesNoEmptyNode")]
    [InlineData("guidancePanelOmitsExplanationLabel")]
    public void 問題欄白話說明的production行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "record-detail-issue-cell.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
