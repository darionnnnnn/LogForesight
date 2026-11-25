using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 階段 R-2：復發與觀察分流的畫面。實際執行 core/format.js、work-order-detail.js、handler-detail.js、
/// issue-status-reply.js 的 production 函式，只替換 DOM 與 API／UI 邊界（見 JsBehavior/recurrence-ui.behavior.mjs）。
/// </summary>
public sealed class RecurrenceUiBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("hostCountTextShowsHostsThenMembers")]
    [InlineData("hostCountTextIsTheOnlyFormatter")]
    [InlineData("headerShowsRecurrenceOnlyWhenPositive")]
    [InlineData("memberStatusCellShowsRecurrenceAndLastReply")]
    [InlineData("memberStatusCellShowsPriorDismissal")]
    [InlineData("headerShowsPriorDismissalOnlyWhenPositive")]
    [InlineData("memberHostCellMarksOccurrenceOnlyUnderAllFilter")]
    [InlineData("memberHostCellShowsLogNameOnlyWhenHostRepeats")]
    [InlineData("deniedDetailHidesMemberAndTimelineSections")]
    [InlineData("handlerRowShowsRecurrenceBadgeOnlyWhenPositive")]
    [InlineData("replyModalShowsRecurrenceHintAndThreeSuggestions")]
    [InlineData("replyModalEscalateSuggestionSelectsEscalatedAndPrefillsNote")]
    [InlineData("replyModalEscalateSuggestionKeepsUserNote")]
    [InlineData("replyModalObserveSuggestionSetsSevenDays")]
    [InlineData("replyModalProgressSuggestionSelectsInProgressAndFocusesDue")]
    [InlineData("replyModalSuggestionsHiddenInRestMode")]
    [InlineData("replyModalRecurrenceCountFollowsPartialSelection")]
    [InlineData("singleMemberReplyUsesMembersOwnRecurrence")]
    [InlineData("handlerKpiShowsTodoObservingEscalated")]
    [InlineData("handlerDefaultStatusIsTodoAndUrlWins")]
    [InlineData("observedTextHasThreeForms")]
    [InlineData("quietCloseButtonHiddenUnlessEligible")]
    [InlineData("quietCloseConfirmsThenRepliesWithExpiredIdsAndReloads")]
    [InlineData("quietCloseFailureDoesNotReloadSilently")]
    public void 復發與觀察分流畫面的production行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "recurrence-ui.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
