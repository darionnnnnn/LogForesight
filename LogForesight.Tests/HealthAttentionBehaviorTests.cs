using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 76-2 輪批次 C-4：健康整體狀態的兩個消費端（健康頁頂端、導覽健康告示）的 production 行為。
/// 正常時不顯示原因清單；告示在狀態不正常時列出原因；沒有維護與開發監看能力的角色不發請求。
/// </summary>
public sealed class HealthAttentionBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("health-attention.behavior.mjs", "overallShowsStatusAndReasons")]
    [InlineData("health-attention.behavior.mjs", "overallNormalShowsNoReasonList")]
    [InlineData("health-attention.behavior.mjs", "overallRerenderClearsReasons")]
    [InlineData("health-attention.behavior.mjs", "overallDownUsesErrorNote")]
    [InlineData("health-attention.behavior.mjs", "overallReasonTextIsPlainText")]
    [InlineData("health-attention.behavior.mjs", "bannerListsReasonsWhenNotOk")]
    [InlineData("health-attention.behavior.mjs", "bannerCollapsesReasonsBeyondTwo")]
    [InlineData("health-attention.behavior.mjs", "bannerKeepsStaleBarAndAddsReasons")]
    [InlineData("health-attention.behavior.mjs", "bannerHiddenWhenOk")]
    [InlineData("health-attention.behavior.mjs", "bannerDevMonitorSeesReasonsWithoutSettingsLink")]
    [InlineData("health-attention.behavior.mjs", "bannerMaintainHasNoContactLine")]
    [InlineData("health-attention.behavior.mjs", "bannerContactLineNotRepeatedWhenMainTextHasContacts")]
    [InlineData("health-attention.behavior.mjs", "bannerNoCapabilityMakesNoRequest")]
    [InlineData("health-attention.behavior.mjs", "bannerAttentionFailureKeepsStaleBar")]
    [InlineData("health-attention.behavior.mjs", "bannerAttentionFailureAloneShowsNothing")]
    public void 健康整體狀態消費端的production行為(string scriptName, string caseName)
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
}
