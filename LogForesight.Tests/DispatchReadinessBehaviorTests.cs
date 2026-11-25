using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 U 的前端行為：使用者編輯視窗的群組即時摘要（F18-1）、健康頁「派工就緒」卡（F23-2）、
/// 主機群組清單「看得到的派工池群組」欄與批次設定群組後的提示（F17-3）。以 production 原始碼執行。
/// </summary>
public sealed class DispatchReadinessBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("dispatch-readiness.behavior.mjs", "summaryShowsCapabilitiesHostsAndPool")]
    [InlineData("dispatch-readiness.behavior.mjs", "summaryUpdatesWhenGroupsToggle")]
    [InlineData("dispatch-readiness.behavior.mjs", "summaryDropsStaleResponse")]
    [InlineData("dispatch-readiness.behavior.mjs", "summaryFailureShowsNoteAndKeepsPageUsable")]
    [InlineData("dispatch-readiness.behavior.mjs", "summaryLinesWarnPoolMemberWithoutHandle")]
    [InlineData("dispatch-readiness.behavior.mjs", "renderReadinessShowsFailuresWithFixLinks")]
    [InlineData("dispatch-readiness.behavior.mjs", "renderReadinessAllPassedAndMailOffNote")]
    [InlineData("dispatch-readiness.behavior.mjs", "renderReadinessSkippedItemHasNoFailureBadge")]
    [InlineData("dispatch-readiness.behavior.mjs", "renderReadinessNullClearsCard")]
    [InlineData("dispatch-readiness.behavior.mjs", "groupCellZeroWarnsWithAuthorizeLink")]
    [InlineData("dispatch-readiness.behavior.mjs", "groupCellPositiveAndNotApplicable")]
    [InlineData("dispatch-readiness.behavior.mjs", "batchNoticeNamesGroupsAndLinksToAccess")]
    [InlineData("dispatch-readiness.behavior.mjs", "batchNoticeNullWhenNothingToWarn")]
    public void 就緒檢查與上線輔助的production行為(string scriptName, string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, scriptName);
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(
            new[] { script, caseName }, scriptDir,
            timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }

    /// <summary>接線：編輯視窗的群組勾選與「啟用」變動都會重新整理摘要，開窗時也先算一次</summary>
    [Fact]
    public void 使用者編輯視窗接上群組摘要的勾選與啟用事件()
    {
        var js = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "users.js"));
        Assert.Contains("document.getElementById('user-groups').addEventListener('change', refreshGroupSummary)", js);
        Assert.Contains("document.getElementById('user-active').addEventListener('change', refreshGroupSummary)", js);
        var open = js.IndexOf("function openModal(", StringComparison.Ordinal);
        Assert.True(js.IndexOf("refreshGroupSummary();", open, StringComparison.Ordinal) > open);
        var view = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "Views", "Pages", "Users.cshtml"));
        Assert.Contains("id=\"user-group-summary\"", view);
    }
}
