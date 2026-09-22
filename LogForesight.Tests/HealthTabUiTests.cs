using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 系統健康頁籤、全站過期告示列、儀表板資料時間（任務 A-4）的前端結構守門。
/// </summary>
public class HealthTabUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    /// <summary>取 data-panel="{name}" 那個 div 起算、到下一個頂層 panel 註解／div 之前的片段</summary>
    private static string PanelBlock(string cshtml, string name)
    {
        var start = cshtml.IndexOf($"data-panel=\"{name}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 data-panel=\"{name}\"");
        var next = cshtml.IndexOf("data-panel=\"", start + 1, StringComparison.Ordinal);
        return next < 0 ? cshtml[start..] : cshtml[start..next];
    }

    /// <summary>取 JS 檔中某個 function 的本體（到下一個頂層 function／宣告為止）</summary>
    private static string FunctionBody(string js, string name)
    {
        var m = Regex.Match(js, $@"(async\s+)?function\s+{name}\s*\(");
        Assert.True(m.Success, $"找不到函式 {name}");
        var rest = js[m.Index..];
        var end = Regex.Match(rest[1..], @"\r?\n(async\s+function|function|export|/\*\*|const|let)\b");
        return end.Success ? rest[..(end.Index + 1)] : rest;
    }

    [Fact]
    public void 健康頁籤內所有按鈕都是TypeButton()
    {
        var panel = PanelBlock(Read("LogForesight.Web", "Views", "Pages", "Settings.cshtml"), "health");
        var buttons = Regex.Matches(panel, @"<button\b[^>]*>");
        Assert.NotEmpty(buttons);   // 空集合會讓下面的斷言恆真
        foreach (Match b in buttons)
        {
            Assert.Contains("type=\"button\"", b.Value);
        }
    }

    [Fact]
    public void Layout呼叫Freshness前有能力判斷()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "core", "layout.js");
        var callIdx = js.IndexOf("api/health/freshness", StringComparison.Ordinal);
        Assert.True(callIdx >= 0);

        // 找出包住這次呼叫的函式
        var fnStart = js.LastIndexOf("function ", callIdx, StringComparison.Ordinal);
        var body = js[fnStart..callIdx];
        Assert.Contains("'Maintain'", body);
        Assert.Contains("'DevMonitor'", body);
        Assert.Contains("hasCapability", body);
    }

    [Fact]
    public void 慢查詢容器在健康頁籤而非資料保留面板()
    {
        var cshtml = Read("LogForesight.Web", "Views", "Pages", "Settings.cshtml");
        Assert.Contains("id=\"slow-query-status\"", PanelBlock(cshtml, "health"));
        Assert.DoesNotContain("slow-query-status", PanelBlock(cshtml, "retention"));

        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "settings.js");
        var host = FunctionBody(js, "resolveSlowQueryHost");
        Assert.Contains("'slow-query-status'", host);
        Assert.DoesNotContain("backfill-status", host);
        Assert.DoesNotContain("createElement", host);

        // 回填狀態（資料保留面板）不再渲染慢查詢
        Assert.DoesNotContain("renderSlowQueries", FunctionBody(js, "loadBackfillStatus"));
    }

    [Fact]
    public void 設定頁啟用Hash深連結且HealthDetail只有單一讀取點()
    {
        var js = Read("LogForesight.Web", "wwwroot", "js", "pages", "settings.js");
        Assert.Contains("hash: true", js);
        Assert.Single(Regex.Matches(js, "api/health/detail'"));
    }
}
