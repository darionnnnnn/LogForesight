using Xunit;

namespace LogForesight.Tests;

/// <summary>core/elements.js（揭露按鈕、次要資訊行、操作結果訊息）的 production 行為（docs/FEEDBACK-69-PLAN.md A-3、A-4）。</summary>
public sealed class ElementsBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("elements.behavior.mjs", "disclosureTogglesTargetAndAria")]
    [InlineData("elements.behavior.mjs", "disclosureStopsPropagation")]
    [InlineData("elements.behavior.mjs", "disclosureInitiallyExpandedAndCallback")]
    [InlineData("elements.behavior.mjs", "disclosureUnmanagedTargetUntouched")]
    [InlineData("elements.behavior.mjs", "disclosureNotPrinted")]
    [InlineData("elements.behavior.mjs", "metaLineEmptyReturnsNull")]
    [InlineData("elements.behavior.mjs", "metaLineSkipsEmptyItems")]
    [InlineData("elements.behavior.mjs", "metaLineTextIsNotMarkup")]
    [InlineData("elements.behavior.mjs", "statusNoteRoles")]
    [InlineData("elements.behavior.mjs", "statusNoteTextIsNotMarkup")]
    public void 共用元素工廠的production行為(string scriptName, string caseName)
    {
        var scriptDir = Path.Combine(FindRepoRoot(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, scriptName);
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(60), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }
}
