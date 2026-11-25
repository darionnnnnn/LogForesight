using Xunit;

namespace LogForesight.Tests;

/// <summary>第 81 輪批次 F（N1）：「不再顯示這個問題：選哪一種」比較表與說明書同一份說法的 production 行為。</summary>
public sealed class HideGuideBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("hide-guide.behavior.mjs", "sixMechanismsFourColumns")]
    [InlineData("hide-guide.behavior.mjs", "everySentenceWithinGuideLimit")]
    [InlineData("hide-guide.behavior.mjs", "helpManualMatchesScreenData")]
    [InlineData("hide-guide.behavior.mjs", "rendersCollapsedTableForCapableUser")]
    [InlineData("hide-guide.behavior.mjs", "hiddenWithoutCapabilityOrData")]
    [InlineData("hide-guide.behavior.mjs", "rerenderDoesNotStack")]
    public void 比較表的production行為(string scriptName, string caseName)
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
