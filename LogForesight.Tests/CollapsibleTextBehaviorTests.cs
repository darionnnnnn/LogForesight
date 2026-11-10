using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>長文字收合共用元件（core/ui.js collapsibleText）的 production 行為，以及 record-detail／樣式表 的接線斷言。</summary>
public sealed class CollapsibleTextBehaviorTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [NodeTheory]
    [InlineData("collapsible-text.behavior.mjs", "plainTextCollapsesAtSeparator")]
    [InlineData("collapsible-text.behavior.mjs", "exactLimitReturnsContainer")]
    [InlineData("collapsible-text.behavior.mjs", "noSeparatorCutsAtLimit")]
    [InlineData("collapsible-text.behavior.mjs", "surrogatePairNotSplit")]
    [InlineData("collapsible-text.behavior.mjs", "earlySeparatorNotUsed")]
    [InlineData("collapsible-text.behavior.mjs", "blockModeTogglesClass")]
    [InlineData("collapsible-text.behavior.mjs", "clickStopsPropagation")]
    [InlineData("collapsible-text.behavior.mjs", "printRestoresFullText")]
    [InlineData("collapsible-text.behavior.mjs", "printListenerRegisteredOnce")]
    [InlineData("collapsible-text.behavior.mjs", "detachedNodesPrunedOnNextRegistration")]
    [InlineData("collapsible-text.behavior.mjs", "neverConnectedNodeIsKept")]
    public void 長文字收合元件的production行為(string scriptName, string caseName)
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

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()), Encoding.UTF8);

    [Fact]
    public void 風險日詳情改用共用收合_不再量測也不記報告展開狀態()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "record-detail.js");
        Assert.DoesNotContain("lf-re" + "measure", js);
        Assert.DoesNotContain("lf-issue-details" + "--clamped", js);
        Assert.True(js.Split("collapsibleText(").Length - 1 >= 6, "collapsibleText( 在 record-detail.js 應至少出現 6 次");
        foreach (var line in js.Split('\n').Where(l => l.Contains("reportCard(")))
            Assert.DoesNotContain("storageKey", line);
    }

    [Fact]
    public void 樣式表以collapsible取代舊line_clamp_且列印解除()
    {
        var css = SiteCss.Read();
        Assert.DoesNotContain("lf-issue-details" + "--clamped", css);
        Assert.Contains(".lf-collapsible--clamped", css);
        var print = css.IndexOf("@media print", css.IndexOf(".lf-collapsible--clamped", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(print > 0, "應有 @media print 解除收合");
        var block = css.Substring(print, 300);
        Assert.Contains(".lf-collapsible--clamped", block);
        Assert.Contains("max-height: none", block);
    }
}
