using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 報表大螢幕模式的版面守門（WEB-SPEC §9.6）。專案沒有 Razor 視圖渲染的測試環境，
/// 這裡以 markup／樣式表文字斷言三件事：<c>_Layout</c> 只在 ViewData 要求時才輸出 <c>lf-wall</c> 類別、
/// 只有報表頁會要求且只在網址帶 <c>display=wall</c> 時、大螢幕樣式全部包在 screen＋992px 之內（窄螢幕不套用、列印不受影響）。
/// 真實渲染結果（第一次繪製前沒有側欄）由站台實測驗證。
/// </summary>
public class ReportsWallLayoutUiTests
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

    private static string Web(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot(), "LogForesight.Web" }.Concat(parts).ToArray()));

    [Fact]
    public void Layout的html標籤只在ViewData要求大螢幕時才加類別()
    {
        var layout = Web("Views", "Shared", "_Layout.cshtml");

        Assert.Contains("ViewData[\"DisplayMode\"] as string == \"wall\"", layout);
        // 條件為假時輸出 null：Razor 會整個省略 class 屬性，其他頁的 <html> 與原本完全相同
        Assert.Contains("<html lang=\"zh-Hant\" class=\"@(wallMode ? \"lf-wall\" : null)\">", layout);
        // 類別字面值只出現在這一個條件運算式裡，不得無條件輸出
        Assert.Single(Regex.Matches(layout, "lf-wall"));
    }

    [Fact]
    public void 只有報表頁會要求大螢幕模式()
    {
        var views = Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "LogForesight.Web", "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("DisplayMode", StringComparison.Ordinal))
            .Select(p => Path.GetFileName(p))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // _Layout 只讀、Reports 只寫；其他任何頁面出現這個鍵都是把大螢幕模式帶到不該有的頁面
        Assert.Equal(new[] { "Reports.cshtml", "_Layout.cshtml" }, views);

        var reports = Web("Views", "Pages", "Reports.cshtml");
        Assert.Single(Regex.Matches(reports, @"ViewData\[""DisplayMode""\]\s*="));
    }

    [Fact]
    public void 報表頁只在網址display等於wall時才要求大螢幕模式()
    {
        var reports = Web("Views", "Pages", "Reports.cshtml");
        var m = Regex.Match(reports,
            @"if \(string\.Equals\(Context\.Request\.Query\[""display""\], ""wall"", StringComparison\.Ordinal\)\)\s*\{\s*ViewData\[""DisplayMode""\] = ""wall"";\s*\}");
        Assert.True(m.Success, "ViewData[\"DisplayMode\"] 必須在「display 等於 wall（區分大小寫）」的條件內才設定");
    }

    [Fact]
    public void 大螢幕樣式全部包在screen與992px以上的媒體查詢內()
    {
        var css = Web("wwwroot", "css", "layout.css");
        var open = css.IndexOf("@media screen and (min-width: 992px) {", StringComparison.Ordinal);
        Assert.True(open >= 0, "找不到大螢幕樣式的 @media screen and (min-width: 992px)");

        var depth = 0;
        var close = -1;
        for (var i = css.IndexOf('{', open); i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) { close = i; break; }
        }

        Assert.True(close > open, "大螢幕媒體查詢沒有閉合");

        // 區塊外只允許兩個預設隱藏的元素（標頭列、離開鈕）提到 lf-wall 字樣以外的規則；
        // 任何 `.lf-wall` 選擇器出現在區塊外，窄螢幕或列印就會套到大螢幕版面
        foreach (Match match in Regex.Matches(css, @"(?:^|[\s,}]):root\.lf-wall|\.lf-wall\s[.#:\w]", RegexOptions.Multiline))
        {
            Assert.True(match.Index > open && match.Index < close, $"`{match.Value.Trim()}` 出現在大螢幕媒體查詢之外（位置 {match.Index}）");
        }

        // 第一排數字：64px × 倍率（1080 高 64px、2160 高 128px）
        Assert.Contains("font-size: calc(64px * var(--lf-wall-scale, 1));", css[open..close]);
        // 根字級乘倍率，蓋過使用者的字級偏好
        Assert.Contains(":root.lf-wall {", css[open..close]);
    }

    [Fact]
    public void 報表頁有標頭列與離開鈕且工具列大螢幕鈕預設隱藏()
    {
        var reports = Web("Views", "Pages", "Reports.cshtml");
        foreach (var id in new[] { "wall-header", "wall-period-title", "wall-period-meta", "wall-updated", "wall-stale", "wall-exit" })
        {
            Assert.Contains($"id=\"{id}\"", reports);
        }

        Assert.Contains("id=\"btn-report-fullscreen\" hidden", reports);
        // 失敗提示用風險狀態文字角色
        Assert.Matches(@"class=""lf-text-alert"" id=""wall-stale""", reports);
    }
}
