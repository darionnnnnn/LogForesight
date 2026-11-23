using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 前端共用出口（第 79 輪 I2、I3、I5）的守門：整列導向用真連結、頁籤走 Bootstrap Tab 外掛、
/// 請求序號／輪詢／關閉對話框／欄位錯誤各頁不再手寫一份。行為由 SharedFrontBehaviorTests 驗證，
/// 這裡用原始碼斷言守住「頁面層沒有回到手寫版本」。
/// </summary>
public sealed class SharedExitGuardUiTests
{
    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return Path.Combine(dir!.FullName, "LogForesight.Web");
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(WebRoot(), relative), Encoding.UTF8).Replace("\r\n", "\n");

    /// <summary>pages／core 的 JS（不含 vendor）：檔名與內容</summary>
    private static IEnumerable<(string Name, string Text)> FrontendScripts() =>
        new[] { "core", "pages" }
            .SelectMany(dir => Directory.GetFiles(Path.Combine(WebRoot(), "wwwroot", "js", dir), "*.js"))
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n")));

    [Fact]
    public void 表格整列導向是列內真連結而不是button角色的列()
    {
        var ui = Read("wwwroot/js/core/ui.js");
        Assert.Contains("function attachRowLink(", ui);
        Assert.Contains("lf-row-link__anchor", ui);
        // 可展開列才保留列層級的鍵盤操作；rowHref 路徑不得再走 makeRowActivatable
        Assert.DoesNotContain("location.href = appUrl(href)", ui);
        var table = Regex.Match(ui, @"export function renderTable\(.*?\n\}\n", RegexOptions.Singleline).Value;
        Assert.Contains("attachRowLink(tr, appUrl(href))", table);

        var css = SiteCss.Read();
        Assert.Contains(".lf-row-link__anchor::after", css);
        Assert.Matches(@"\.table > tbody > tr\.lf-row-link \{ position: relative;", css);
        Assert.Contains(".lf-row-link__anchor:focus-visible::after", css);
        // 帶 title 的元素抬到延伸點擊區之上，列內提示才出得來；儲存格排除在外（否則延伸區被切成一格）
        Assert.Matches(@"\.lf-row-link :where\(.*\[data-bs-toggle\], \[title\]:not\(td, th\)\)", css);
        Assert.Contains("function attachTruncateTitle(", ui);
    }

    [Fact]
    public void 頁籤按鈕帶nav_link且位於nav清單內_由Bootstrap_Tab外掛管理()
    {
        var ui = Read("wwwroot/js/core/ui.js");
        Assert.Contains("bootstrap.Tab.getOrCreateInstance(btn)", ui);
        Assert.Contains("shown.bs.tab", ui);
        Assert.Contains("history.replaceState(null, '', '#' + name)", ui);

        // 外掛的子元素選擇器是 .nav-link；頁籤按鈕少了它，外掛不會管到
        var views = Directory.GetFiles(Path.Combine(WebRoot(), "Views"), "*.cshtml", SearchOption.AllDirectories);
        var tabButtons = 0;
        foreach (var view in views)
        {
            var text = File.ReadAllText(view, Encoding.UTF8);
            foreach (Match m in Regex.Matches(text, @"<button[^>]*\bdata-tab=""[^""]+""[^>]*>"))
            {
                tabButtons++;
                Assert.Contains("nav-link", m.Value);
            }
        }
        Assert.True(tabButtons >= 20, $"頁籤按鈕數量異常：{tabButtons}");
    }

    [Fact]
    public void 各頁不手寫頁籤切換也不對頁籤按鈕模擬點擊()
    {
        foreach (var (name, text) in FrontendScripts())
        {
            Assert.DoesNotMatch(@"\[data-tab[^\]]*\]['""`)]?\)?\??\.click\(\)", text);
            if (name != "ui.js")
            {
                Assert.DoesNotContain("'d-none', panel.dataset.panel", text);
            }
        }
    }

    [Fact]
    public void 各頁沒有手寫的請求序號計數器()
    {
        // ++xxxSeq／xxxSeq++／let xxxSeq = 0 這類手寫遞增計數器；請求序號一律 createSeqGuard
        var pattern = new Regex(@"(\+\+\s*\w*(Seq|Sequence|Generation|RequestId)\b)|(\b\w*(Seq|Sequence|Generation|RequestId)\s*(\+\+|=\s*0\b))", RegexOptions.IgnoreCase);
        foreach (var (name, text) in FrontendScripts())
        {
            // 識別碼產生器，不是請求序號守門：prtg-diagnostic-upload 的 sequence 對應 worker 訊息，elements 的 disclosureSeq、field-errors 的 sequence 產生元素 id
            if (name is "prtg-diagnostic-upload.js" or "elements.js" or "field-errors.js") continue;
            Assert.False(pattern.IsMatch(text), $"{name} 有手寫請求序號計數器，改用 async-guards 的 createSeqGuard");
        }
    }

    [Fact]
    public void 各頁輪詢走pollWhile而不是遞迴setTimeout或setInterval()
    {
        // 例外：防抖（debounce）、動畫收尾、物件網址回收、API 逾時，以及報表大螢幕以注入計時器驅動的狀態機
        var allowedFiles = new HashSet<string> { "reports-wall.js", "reports.js", "api.js", "layout.js", "note-editor.js", "async-guards.js" };
        foreach (var (name, text) in FrontendScripts())
        {
            Assert.DoesNotContain("setInterval(", text);
            if (allowedFiles.Contains(name)) continue;
            foreach (Match m in Regex.Matches(text, @"setTimeout\((\w+)\s*,"))
            {
                // 把自己傳回 setTimeout 的遞迴排程（poll／tick／refresh…）才是輪詢
                var fn = m.Groups[1].Value;
                Assert.False(Regex.IsMatch(text, $@"(function|const|let)\s+{fn}\b[\s\S]{{0,2000}}setTimeout\({fn}\s*,"),
                    $"{name} 以遞迴 setTimeout 手寫輪詢（{fn}），改用 pollWhile");
            }
        }
    }

    [Fact]
    public void 對話框送出成功以close關閉而不是模擬點擊關閉鈕()
    {
        foreach (var (name, text) in FrontendScripts())
        {
            Assert.DoesNotMatch(@"data-bs-dismiss=""modal""\]['""`]\)\??\.click\(\)", text);
        }
    }

    [Fact]
    public void 各頁欄位錯誤走field_errors而不手寫is_invalid()
    {
        foreach (var (name, text) in FrontendScripts())
        {
            if (name == "field-errors.js") continue;
            Assert.DoesNotMatch(@"classList\.(add|remove)\('is-invalid'\)", text);
        }
    }
}
