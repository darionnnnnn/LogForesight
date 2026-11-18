using Xunit;

namespace LogForesight.Tests;

/// <summary>主機群組成員（groups.js）：目前成員跨頁勾選與加入預覽 0 台的按鈕狀態。</summary>
public class GroupsMembersBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("currentMembersSelectionSurvivesPaging")]
    [InlineData("previewWithZeroMatchesDisablesBothApplyButtons")]
    public void 主機群組成員行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "groups-members.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
