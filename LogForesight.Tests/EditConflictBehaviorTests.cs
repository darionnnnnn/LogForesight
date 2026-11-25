using Xunit;

namespace LogForesight.Tests;

/// <summary>第 81 輪批次 M：使用者視窗一次送出，使用者與規則視窗收到 409 時保留輸入並常駐提示（users.js、rules.js）。</summary>
public class EditConflictBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("userSaveIsOneRequestWithVersionAndGroups")]
    [InlineData("userCreateSendsNullVersion")]
    [InlineData("userConflictKeepsInputAndShowsPersistentNotice")]
    [InlineData("userOtherErrorToastsWithoutConflictNotice")]
    [InlineData("userReloadButtonRefreshesVersionAndClearsNotice")]
    [InlineData("ruleSaveSendsVersionForEditAndNullForCreate")]
    [InlineData("ruleConflictKeepsInputAndShowsPersistentNotice")]
    [InlineData("ruleReloadButtonRefreshesAndClearsNotice")]
    [InlineData("ruleReloadWhenRuleDeletedToastsAndCloses")]
    public void 編輯衝突行為(string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "edit-conflict.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");

        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(30), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
