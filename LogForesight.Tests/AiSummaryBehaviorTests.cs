using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 68 輪 B：AI 摘要三格版面、頁首資料來源副標的行為與靜態守門。</summary>
public class AiSummaryBehaviorTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string ReadWeb(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Root(), "LogForesight.Web", "wwwroot" }.Concat(parts).ToArray())).Replace("\r\n", "\n");

    [NodeTheory]
    [InlineData("五欄齊全三格依序且只有建議處置帶色條")]
    [InlineData("缺趨勢只剩兩格")]
    [InlineData("三欄全空沒有三格容器但標題與速報仍在")]
    [InlineData("統計模式維持舊呈現")]
    [InlineData("來源與狀態中文名稱")]
    [InlineData("副標組字")]
    public void AI摘要行為案例(string caseName)
    {
        var scriptDir = Path.Combine(Root(), "LogForesight.Tests", "JsBehavior");
        var script = Path.Combine(scriptDir, "ai-summary.behavior.mjs");
        Assert.True(File.Exists(script), $"找不到行為測試腳本: {script}");
        var r = NodeRuntime.RunOrFail(new[] { script, caseName }, scriptDir, timeout: TimeSpan.FromSeconds(15), label: caseName);
        Assert.True(r.ExitCode == 0, $"行為案例失敗：{caseName}{Environment.NewLine}{r.Stdout}{r.Stderr}");
        Assert.Contains($"PASS {caseName}", r.Stdout);
    }

    [Fact]
    public void NetIQ狀態對照表涵蓋後端所有常數()
    {
        var cs = File.ReadAllText(Path.Combine(Root(), "LogForesight.Core", "Service", "NetiqAttemptStatus.cs"));
        var values = Regex.Matches(cs, "public const string \\w+\\s*=\\s*\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(7, values.Count);
        var format = ReadWeb("js", "core", "format.js");
        foreach (var v in values)
            Assert.Contains($"'{v}'", format);
    }

    [Fact]
    public void 詳情頁不再把列舉值直接上畫面()
    {
        var js = ReadWeb("js", "pages", "record-detail.js");
        Assert.DoesNotContain("最新 NetIQ 嘗試", js);
        Assert.DoesNotContain("可信 PRTG 補充", js);
    }

    [Fact]
    public void 三格摘要列印時改單欄()
    {
        var css = SiteCss.Read();
        Assert.Matches(new Regex(@"@media print\s*\{\s*\.lf-ai-summary\s*\{\s*grid-template-columns:\s*1fr;"), css);
    }
}
