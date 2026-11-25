using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 T：主機與問題橫切面的前端行為（頁頂主機快速搜尋、主機詳情「這台的交辦」、交辦單頁處置參考／捷徑／
/// 代為結案範圍、風險日側欄的處理人）。實際執行 production 程式，只替換 DOM 與 API 邊界
/// （見 JsBehavior/host-order-cross-cut.behavior.mjs）。
/// </summary>
public class HostOrderCrossCutBehaviorTests
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
    [InlineData("hostSearchByNameAndIp")]
    [InlineData("hostSearchShowsOnlyWhatServerReturnsAndCapsAtTen")]
    [InlineData("hostSearchStaleResponseAndBlankInput")]
    [InlineData("hostSearchEnterOnlyNavigatesWhenQueryIsCurrent")]
    [InlineData("hostOrdersReplyTwoIssuesTwoRequests")]
    [InlineData("hostOrdersEscalationSumsNotifiedAdminsAndCountsIssues")]
    [InlineData("hostOrdersOneRejectedOthersSucceed")]
    [InlineData("hostOrdersEmptyAndNotReplyable")]
    [InlineData("hostOrdersLogNameAndOrderLink")]
    [InlineData("guidanceAndSamplePanels")]
    [InlineData("issueShortcutsDestinationsAndCapabilities")]
    [InlineData("adminCloseScopeOptions")]
    [InlineData("handlingPanelHandlerRows")]
    public void 主機與問題橫切面行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "host-order-cross-cut.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);

        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
