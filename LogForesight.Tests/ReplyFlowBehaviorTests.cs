using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回覆流程（批次 E-1）：回覆彈窗（結論按鈕、不預選、觀察中快捷、成功訊息、鍵盤、被拒後常駐訊息與草稿）、
/// 交辦單頁入口與被拒重載、「清單有更新」提示與徽章事件。實際執行 production 的 issue-status-reply.js 等模組，
/// 只替換 DOM 與 API 邊界（見 JsBehavior/reply-flow.behavior.mjs）。
/// </summary>
public class ReplyFlowBehaviorTests
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
    [InlineData("noConclusionDisablesSubmit")]
    [InlineData("observingDefaultsToSevenDaysWithQuickButtons")]
    [InlineData("successMessagesNameTheConclusion")]
    [InlineData("escalationMessageNamesNotifiedAdmins")]
    [InlineData("keyboardArrowKeysCtrlEnterAndEscKeepDraft")]
    [InlineData("rejected403KeepsMessageDraftAndOffersCopy")]
    [InlineData("rejected400AndOtherErrors")]
    [InlineData("describeRejectionNamesNewHandler")]
    [InlineData("successClearsDraftClosesAndApplies")]
    [InlineData("workOrderPageReplyEntryAndRejection")]
    [InlineData("workOrderPageActionOpensModalForAllCounts")]
    [InlineData("badgeEventCarriesMemberAndOrderCounts")]
    [InlineData("unreadBadgeTitleMentionsEscalated")]
    [InlineData("unreadBadgeOnlyForAssign")]
    [InlineData("listUpdateNoticeAppearsOnlyWhenCountsDiffer")]
    public void 回覆流程行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "reply-flow.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
