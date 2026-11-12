using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 70 輪 E：頁首 AI 摘要版面、頁首資料來源副標的行為與靜態守門。</summary>
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
    [InlineData("五欄齊全版面區塊依序且各段只一份")]
    [InlineData("AI完成時AI摘要徽章恰好一顆且在標題之後")]
    [InlineData("建議處置底色只在AI完成時出現")]
    [InlineData("複製鈕在重點速報區塊內且複製渲染後文字")]
    [InlineData("只有標題沒有其他區塊也沒有眉標與細線")]
    [InlineData("缺速報時建議處置佔滿這一列")]
    [InlineData("缺建議處置時重點速報佔滿")]
    [InlineData("缺狀況或趨勢時只剩一段且全缺時沒有細線容器")]
    [InlineData("分析中狀態徽章進結論列且沒有AI摘要徽章")]
    [InlineData("降級待補與已放棄只放狀態徽章不放AI摘要徽章")]
    [InlineData("統計模式同一套版面但沒有AI摘要徽章與速報與底色")]
    [InlineData("沒有結論列時狀態徽章不被收下")]
    [InlineData("案件授與檢視沒有白話總覽")]
    [InlineData("五欄全空回null")]
    [InlineData("五欄文字一字不減")]
    [InlineData("第一行身分項目NetIQ主機不重複IP")]
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
    public void 頁首摘要列印時結論以下改單欄()
    {
        var css = SiteCss.Read();
        // 速報＋建議處置、狀況＋趨勢兩組容器在列印時都要單欄（並排的 --split 要明列，否則寬紙張會套到 min-width 的並排）
        Assert.Matches(new Regex(@"@media print\s*\{\s*\.lf-briefing__lead,\s*\.lf-briefing__lead--split,\s*\.lf-briefing__detail\s*\{\s*grid-template-columns:\s*1fr;"), css);
    }

    [Fact]
    public void 頁首摘要舊類別名零命中()
    {
        foreach (var old in new[] { "lf-ai-brief", "lf-ai-summary", "lf-ai-headline" })
        {
            Assert.DoesNotContain(old, SiteCss.Read());
            Assert.DoesNotContain(old, ReadWeb("js", "pages", "record-detail.js"));
        }
    }

    [Fact]
    public void 頁首摘要只在AI完成時給建議處置底色_且底色是唯一有底色的面()
    {
        var css = SiteCss.Read();
        // 底色只掛在 --tinted 修飾類別（JS 只在 aiAnalyzed 時加上）；其餘 briefing 規則不得帶 background
        Assert.Matches(new Regex(@"\.lf-briefing__action--tinted\s*\{[^}]*background:\s*var\(--lf-primary-soft\)"), css);
        var js = ReadWeb("js", "pages", "record-detail.js");
        Assert.Contains("aiOn ? 'lf-briefing__action lf-briefing__action--tinted' : 'lf-briefing__action'", js);
        var briefingRules = Regex.Matches(css, @"\.lf-briefing[\w_-]*(?!--tinted)[^{}]*\{[^}]*\}")
            .Select(m => m.Value).Where(v => !v.Contains("--tinted")).ToList();
        Assert.NotEmpty(briefingRules);
        Assert.DoesNotContain(briefingRules, v => v.Contains("background"));
    }
}
