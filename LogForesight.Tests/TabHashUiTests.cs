using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 頁籤 hash 深連結（DESIGN-SYSTEM §8.5）的守門：七頁中有唯一頁籤名稱的六頁都開啟 hash 同步，
/// 網址帶 #頁籤名稱即開啟對應頁籤；規則頁三個平台共用同一個 data-tab，hash 表達不出平台，刻意不開。
/// </summary>
public class TabHashUiTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { ConvergenceScan.FindRepoRoot(), "LogForesight.Web" }.Concat(parts).ToArray()));

    [Theory]
    [InlineData("groups", "document.getElementById('group-tabs')")]
    [InlineData("netiq", "document.getElementById('netiq-tabs')")]
    [InlineData("prtg-admin", "document.getElementById('prtg-tabs')")]
    [InlineData("runs", "document.getElementById('runs-tabs')")]
    [InlineData("settings", "document.getElementById('settings-tabs')")]
    [InlineData("work-orders", "tabsEl")]
    public void 有唯一頁籤名稱的頁面開啟hash同步(string page, string tabsExpr)
    {
        var js = Read("wwwroot", "js", "pages", page + ".js");
        var pattern = new Regex(@"bindTabs\(" + Regex.Escape(tabsExpr) + @",\s*\{[^}]*\bhash:\s*true", RegexOptions.Singleline);
        Assert.Matches(pattern, js);
    }

    [Theory]
    [InlineData("Groups", "group-tabs")]
    [InlineData("Netiq", "netiq-tabs")]
    public void 開啟hash的頁籤名稱互不重複(string view, string tabsId)
    {
        var cshtml = Read("Views", "Pages", view + ".cshtml");
        var start = cshtml.IndexOf($"id=\"{tabsId}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {tabsId}");
        var end = cshtml.IndexOf("</ul>", start, StringComparison.Ordinal);
        var names = Regex.Matches(cshtml[start..end], "data-tab=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.True(names.Count >= 2);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void 規則頁三個平台共用同一個頁籤名稱_所以不開hash()
    {
        var cshtml = Read("Views", "Pages", "Rules.cshtml");
        Assert.True(Regex.Matches(cshtml, "data-tab=\"rules\" data-platform=").Count >= 2);
        var js = Read("wwwroot", "js", "pages", "rules.js");
        var bind = js[js.IndexOf("bindTabs(document.getElementById('rule-tabs')", StringComparison.Ordinal)..];
        Assert.DoesNotMatch(new Regex(@"^bindTabs\([^;]*?\bhash:\s*true", RegexOptions.Singleline), bind[..Math.Min(bind.Length, 400)]);
    }

    [Fact]
    public void 守門樣式抓得到沒開hash的反例()
    {
        var bad = "bindTabs(document.getElementById('group-tabs'));";
        Assert.DoesNotMatch(new Regex(@"bindTabs\(document\.getElementById\('group-tabs'\),\s*\{[^}]*\bhash:\s*true", RegexOptions.Singleline), bad);
    }
}
