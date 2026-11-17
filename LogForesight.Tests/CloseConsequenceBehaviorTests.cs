using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 代為結案對話框與處理人回覆表單的後果說明（第 76-2 輪 B5-a／B5-b）：
/// 實際執行 work-order-detail.js 的 openAdminCloseModal 與 issue-status-reply.js 的 openWorkOrderReplyModal。
/// </summary>
public class CloseConsequenceBehaviorTests
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
    [InlineData("adminCloseOpensWithoutPreselection")]
    [InlineData("adminCloseBlockCheckboxOnlyForNoFix")]
    [InlineData("adminCloseExplainsConsequences")]
    [InlineData("adminCloseCountsMembersOnDisabledHosts")]
    [InlineData("continuationStopNoteShowsDate")]
    [InlineData("adminCloseSendsBlockRedispatch")]
    [InlineData("adminCloseRefusesWithoutStatus")]
    [InlineData("replyFormShowsNoFixNoteOnlyForNoFix")]
    [InlineData("adminCloseNotifyTextFollowsMailSetting")]
    [InlineData("adminCloseCheckboxWordingForMultiIssueOrder")]
    public void 結案後果說明行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "close-consequence.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
