using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 部分主機回覆（批次 E-2）：彈窗內主機面板（貼上主機名自動勾選、勾選跨頁、單一名稱篩選）、
/// 部分回覆結案後的「其餘 N 台」續問、交辦單頁成員表（勾選跨頁、空狀態、整單結案後篩選切到全部）。
/// 實際執行 production 程式，只替換 DOM 與 API 邊界（見 JsBehavior/reply-partial.behavior.mjs）。
/// </summary>
public class ReplyPartialBehaviorTests
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
    [InlineData("pastedNamesSelectMatchesAndListMissing")]
    [InlineData("selectionKeptAcrossPagesAndSubmitsAll")]
    [InlineData("singleNameFiltersListAndEmptySelectionBlocksSubmit")]
    [InlineData("restPromptAfterPartialClosingReply")]
    [InlineData("restPromptOnlyWhenPartialClosingAndStillActive")]
    [InlineData("pasteAutoSelectsOnlyWhenTwoOrMoreNames")]
    [InlineData("restSectionShowsForClosedPartialReply")]
    [InlineData("restChosenSendsBothRequestsThenCloses")]
    [InlineData("restChoiceCanBeToggledOffAndFallsBackToSecondStep")]
    [InlineData("restRequestFailureKeepsModalInRestModeWithChoice")]
    [InlineData("statusHintSlotKeepsItsLine")]
    [InlineData("orderMembersUrlRepeatsHostNameParam")]
    [InlineData("memberEmptyStateTexts")]
    [InlineData("closedOrderSwitchesMemberFilterToAll")]
    [InlineData("memberTableKeepsSelectionAcrossPages")]
    [InlineData("restNoFixNoteFollowsRestChoice")]
    [InlineData("pickerLoadFailureShowsInlineErrorUntilNextSuccess")]
    [InlineData("rejectionRefreshesPickerAndPrunesSelection")]
    [InlineData("nextAfterPartialClosingReportsRemainder")]
    [InlineData("recurrenceUnitFollowsEntry")]
    [InlineData("caseIdsDraftKeyIgnoresPickOrder")]
    public void 部分主機回覆行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "reply-partial.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
