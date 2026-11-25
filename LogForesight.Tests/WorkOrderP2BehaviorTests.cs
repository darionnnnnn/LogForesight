using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 階段 P-2：期限預填、嚴重度欄與標記、排序、今天需要你處理與篩選橫幅。實際執行 core/format.js、core/paths.js、
/// records.js、handler-detail.js 的 production 函式，只替換 DOM 與 API／UI 邊界（見 JsBehavior/work-order-p2.behavior.mjs）。
/// </summary>
public sealed class WorkOrderP2BehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("prefillFillsSuggestedDueDateOnFirstPreview")]
    [InlineData("prefillDoesNotOverrideAfterUserClears")]
    [InlineData("prefillDoesNotOverrideAfterUserEdits")]
    [InlineData("severityCellShowsFourValuesAndCritical")]
    [InlineData("flagBadgesShowOnlyPositiveCounts")]
    [InlineData("flagBadgesAndSeverityAreWiredIntoBothLists")]
    [InlineData("handlerSortDefaultsToPriorityAndUrlWins")]
    [InlineData("overviewSortKeepsDefaultAndAddsThree")]
    [InlineData("todayCardsClickAppliesFlagFilter")]
    [InlineData("todayCardsZeroIsShownButNotClickable")]
    [InlineData("flagBannerShowsForKnownFlagAndClearDropsIt")]
    [InlineData("flagBannerHiddenWithoutFlagOrWithUnknownFlag")]
    [InlineData("overdueSingleShowsAlertTextInList")]
    public void 交辦期限嚴重度與今天需要處理畫面的production行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "work-order-p2.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
