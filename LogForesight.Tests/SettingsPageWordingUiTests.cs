using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 設定頁說明文字守門（WEB-SPEC §8.6a「讓版面自己說明」）。三條靜態掃描：
/// 1. 後果句不能因為精簡說明而被一起刪掉；
/// 2. 同一欄位的 placeholder、常駐說明、JS 狀態句不得重複講同一件事；
/// 3. JS 取用的 <c>…-hint</c> 元素必須存在於 Settings.cshtml，否則賦值 <c>textContent</c> 會讓整頁腳本中斷。
/// </summary>
public class SettingsPageWordingUiTests
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

    private static string ReadWeb(params string[] parts)
    {
        var segments = new List<string> { FindRepoRoot(), "LogForesight.Web" };
        segments.AddRange(parts);
        return File.ReadAllText(Path.Combine(segments.ToArray())).Replace("\r\n", "\n");
    }

    private static string Cshtml => ReadWeb("Views", "Pages", "Settings.cshtml");
    private static string SettingsJs => ReadWeb("wwwroot", "js", "pages", "settings.js");
    private static string GuardJs => ReadWeb("wwwroot", "js", "pages", "prtg-guard.js");

    /// <summary>
    /// 「保留常駐」的後果句／特殊值語意／硬性限制，各取一個關鍵片語；
    /// 片語要出現在 Settings.cshtml 或 settings.js／prtg-guard.js（動態寫入的說明在 JS 裡）。
    /// </summary>
    [Theory]
    // AI
    [InlineData("會傳送至第三方服務")]
    [InlineData("留空會停用 AI 分析與加值功能")]
    [InlineData("0＝不設上限")]
    [InlineData("必須是 JSON 物件")]
    [InlineData("已設定金鑰")]
    // AD
    [InlineData("本機救援帳號（serverAdmin）不受影響")]
    // 資料保留
    [InlineData("永久刪除，無法復原")]
    [InlineData("不可小於首次執行回補天數")]
    [InlineData("不可大於歷史資料保留天數")]
    [InlineData("會佔用資料庫空間")]
    // 資源守門
    [InlineData("資源數據取自 PRTG")]
    [InlineData("超過後放行並警告")]
    [InlineData("留空＝自動偵測")]
    // 郵件
    [InlineData("「測試寄信」不受此開關影響")]
    [InlineData("每台主機同一天只寄一次")]
    [InlineData("同時是系統存活訊號")]
    [InlineData("目前沒有任何使用者群組被設為派工池")]
    [InlineData("以逗號分隔，最多四組")]
    // 外觀
    [InlineData("留空＝使用出廠名稱")]
    [InlineData("留空＝不顯示副標題")]
    public void 後果句仍在設定頁(string phrase)
    {
        var all = Cshtml + "\n" + SettingsJs + "\n" + GuardJs;
        Assert.True(all.Contains(phrase, StringComparison.Ordinal),
            $"設定頁的後果句／硬性限制「{phrase}」不見了；精簡說明時只能刪重複與版面已講的，不能刪後果句");
    }

    private static readonly Regex FieldWithFormText = new(
        @"<(?:input|textarea)\b[^>]*?placeholder=""(?<ph>[^""]*)""[^>]*>(?:</textarea>)?\s*" +
        @"<div class=""form-text[^""]*""[^>]*>(?<ft>[^<]*)</div>",
        RegexOptions.Singleline);

    /// <summary>同一欄位的 placeholder 與常駐 form-text 不得同時講「留空＝」，也不得同時講「一行一」。</summary>
    [Fact]
    public void 同一欄位的placeholder與form_text不重複()
    {
        var matches = FieldWithFormText.Matches(Cshtml);
        Assert.NotEmpty(matches);
        foreach (Match m in matches)
        {
            var ph = m.Groups["ph"].Value;
            var ft = m.Groups["ft"].Value;
            Assert.False(ph.Contains("留空＝") && ft.Contains("留空＝"),
                $"placeholder「{ph}」與 form-text「{ft.Trim()}」重複講了「留空＝」");
            Assert.False(ph.Contains("一行一") && ft.Contains("一行一"),
                $"placeholder「{ph}」與 form-text「{ft.Trim()}」重複講了「一行一」");
        }
    }

    /// <summary>placeholder 已經講「一行一…」的欄位，不能在同一個欄位區塊的常駐段落再講一次。</summary>
    [Fact]
    public void 一行一的格式提示只留一處()
    {
        var blocks = Regex.Split(Cshtml, @"(?=<div class=""mb-3)");
        foreach (var block in blocks)
        {
            var ph = Regex.Match(block, @"placeholder=""(?<ph>[^""]*)""");
            if (!ph.Success || !ph.Groups["ph"].Value.Contains("一行一")) continue;
            var visible = Regex.Matches(block, @"<(?:div|p|span)\b[^>]*class=""[^""]*(?:form-text|lf-text-meta|text-muted small|small text-muted)[^""]*""[^>]*>(?<t>[^<]*)<");
            foreach (Match v in visible)
            {
                Assert.DoesNotContain("一行一", v.Groups["t"].Value);
            }
        }
    }

    /// <summary>金鑰／SMTP 密碼的狀態句只講狀態，「留空＝沿用」由 placeholder 承擔，不在 JS 狀態句再講一次。</summary>
    [Fact]
    public void JS狀態句不再含留空()
    {
        var literals = Regex.Matches(SettingsJs, @"'(?<s>[^'\n]*(?:已設定金鑰|已設定密碼|尚未設定金鑰|尚未設定密碼)[^'\n]*)'");
        Assert.NotEmpty(literals);
        foreach (Match m in literals)
        {
            Assert.DoesNotContain("留空", m.Groups["s"].Value);
        }

        // OpenAI 的 API 位址：placeholder 與常駐說明不得同一句
        var pairs = Regex.Matches(SettingsJs,
            @"baseUrlInput\.placeholder = '(?<ph>[^'\n]*)';\s*\n\s*setHint\(baseUrlHint, '(?<ft>[^'\n]*)'\)");
        Assert.NotEmpty(pairs);
        foreach (Match m in pairs)
        {
            Assert.False(m.Groups["ph"].Value.Contains("留空") && m.Groups["ft"].Value.Contains("留空"),
                $"API 位址 placeholder「{m.Groups["ph"].Value}」與說明「{m.Groups["ft"].Value}」重複");
        }
    }

    /// <summary>JS 以 <c>getElementById('…-hint')</c> 取用的元素必須存在於 Settings.cshtml（JS 自行建立者列白名單）。</summary>
    [Theory]
    [InlineData("settings.js")]
    [InlineData("prtg-guard.js")]
    public void JS取用的hint元素都存在於頁面(string jsFile)
    {
        var js = jsFile == "settings.js" ? SettingsJs : GuardJs;
        var cshtml = Cshtml;
        var jsCreated = new HashSet<string>(StringComparer.Ordinal); // 目前沒有由 JS 建立的 -hint 元素

        var ids = Regex.Matches(js, @"getElementById\('(?<id>[^']+-hint)'\)")
            .Select(m => m.Groups["id"].Value)
            .Distinct()
            .ToList();
        Assert.NotEmpty(ids);
        foreach (var id in ids)
        {
            if (jsCreated.Contains(id)) continue;
            Assert.True(cshtml.Contains($"id=\"{id}\"", StringComparison.Ordinal),
                $"{jsFile} 取用 getElementById('{id}')，但 Settings.cshtml 沒有這個 id；對 null 賦值 textContent 會讓整頁腳本中斷");
        }
    }
}
