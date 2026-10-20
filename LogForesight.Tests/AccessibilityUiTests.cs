using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 無障礙基本面守門（WCAG 2.2 AA）：modal 標題關聯、label 關聯、側欄對比、目前頁標示、圖示按鈕名稱退路。
/// 靜態頁面殼用字串／正規式檢查；.NET 測試無法執行 JS，ui.js 與 layout.js 只能以讀檔字串守門。
/// </summary>
public class AccessibilityUiTests
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

    private static string WebPath(params string[] parts)
    {
        var segments = new List<string> { FindRepoRoot(), "LogForesight.Web" };
        segments.AddRange(parts);
        return Path.Combine(segments.ToArray());
    }

    private static string ReadWeb(params string[] parts)
    {
        var path = WebPath(parts);
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        return File.ReadAllText(path);
    }

    private static IEnumerable<string> AllViews()
    {
        var files = Directory.GetFiles(WebPath("Views"), "*.cshtml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        return files;
    }

    // modal 根元素：class 的第一個 token 是 modal（排除 modal-dialog／modal-content 等子元件）
    private static readonly Regex ModalRoot = new(@"<div\b[^>]*\bclass=""modal[ ""][^>]*>", RegexOptions.Compiled);

    [Fact]
    public void 每個modal根元素都有aria_labelledby且指向同檔存在的id()
    {
        var total = 0;
        var problems = new List<string>();
        foreach (var file in AllViews())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in ModalRoot.Matches(text))
            {
                total++;
                var labelled = Regex.Match(m.Value, @"aria-labelledby=""([^""]+)""");
                if (!labelled.Success)
                {
                    problems.Add($"{Path.GetFileName(file)}: 缺 aria-labelledby → {m.Value}");
                    continue;
                }

                var id = labelled.Groups[1].Value;
                var count = Regex.Matches(text, $@"\bid=""{Regex.Escape(id)}""").Count;
                if (count != 1)
                {
                    problems.Add($"{Path.GetFileName(file)}: aria-labelledby=\"{id}\" 指向的 id 出現 {count} 次（應為 1）");
                }
            }
        }

        Assert.True(total >= 20, $"modal 根元素只掃到 {total} 個，正規式可能失準");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void 非form_check_label的label都有for且指向同檔存在的id()
    {
        var problems = new List<string>();
        var scanned = 0;
        foreach (var file in AllViews())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"<label\b[^>]*>"))
            {
                if (Regex.IsMatch(m.Value, @"class=""[^""]*\bform-check-label\b")) continue;
                scanned++;
                var f = Regex.Match(m.Value, @"\bfor=""([^""]+)""");
                if (!f.Success)
                {
                    problems.Add($"{Path.GetFileName(file)}: label 缺 for → {m.Value}");
                    continue;
                }

                // 含 Razor 運算式的 for 無法靜態比對，略過存在性檢查
                if (f.Groups[1].Value.Contains('@')) continue;
                if (!Regex.IsMatch(text, $@"\bid=""{Regex.Escape(f.Groups[1].Value)}"""))
                {
                    problems.Add($"{Path.GetFileName(file)}: label for=\"{f.Groups[1].Value}\" 在同檔找不到對應 id");
                }
            }
        }

        Assert.True(scanned >= 50, $"label 只掃到 {scanned} 個，正規式可能失準");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Theory]
    [InlineData("RecordDetail.cshtml", "chat-issue-select")]
    [InlineData("RecordDetail.cshtml", "chat-input")]
    [InlineData("HelpManual.cshtml", "help-ask-input")]
    [InlineData("Imports.cshtml", "import-owners-file")]
    public void 原本沒有label的輸入現在有label或aria_label(string view, string inputId)
    {
        var text = ReadWeb("Views", "Pages", view);
        var tag = Regex.Match(text, $@"<(?:input|select|textarea)\b[^>]*\bid=""{Regex.Escape(inputId)}""[^>]*>");
        Assert.True(tag.Success, $"{view} 找不到 #{inputId}");

        var hasAria = Regex.IsMatch(tag.Value, @"aria-label=""[^""]+""");
        var hasLabel = Regex.IsMatch(text, $@"<label\b[^>]*\bfor=""{Regex.Escape(inputId)}""[^>]*>[^<]+</label>");
        Assert.True(hasAria || hasLabel, $"{view} 的 #{inputId} 沒有 label[for] 也沒有 aria-label");
    }

    // ── 側欄對比（WCAG 2.x 相對亮度；sRGB 線性化門檻 0.03928）──────────────

    private static string RootTokenValue(string css, string name)
    {
        // 只取 :root 區塊內的定義（深色模式或其他區塊的同名 token 不算）
        foreach (Match block in Regex.Matches(css, @":root\s*\{([^}]*)\}"))
        {
            var m = Regex.Match(block.Groups[1].Value, $@"{Regex.Escape(name)}\s*:\s*(#[0-9a-fA-F]{{6}})\b");
            if (m.Success) return m.Groups[1].Value;
        }

        Assert.Fail($"site.css 的 :root 找不到 {name}");
        return string.Empty;
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var v = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    private static double Contrast(string a, string b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void 側欄分組標題色對側欄底色對比至少4_5()
    {
        var css = ReadWeb("wwwroot", "css", "site.css");
        var fg = RootTokenValue(css, "--lf-sidebar-section");
        var bg = RootTokenValue(css, "--lf-sidebar-bg");
        var ratio = Contrast(fg, bg);
        Assert.True(ratio >= 4.5, $"--lf-sidebar-section {fg} 對 --lf-sidebar-bg {bg} 對比 {ratio:F2}:1，低於 4.5:1");
    }

    [Fact]
    public void 對比公式自我校驗_舊值3_81_新值7_6()
    {
        Assert.InRange(Contrast("#68799a", "#0f1d3a"), 3.7, 3.9);
        Assert.InRange(Contrast("#9fb0cc", "#0f1d3a"), 7.5, 7.7);
    }

    [Fact]
    public void 側欄目前頁有aria_current且詳情頁前綴對照齊全()
    {
        var js = ReadWeb("wwwroot", "js", "core", "layout.js");
        Assert.Contains("link.setAttribute('aria-current', 'page')", js);
        foreach (var prefix in new[] { "/hosts/", "/handlers/", "/records/", "/work-orders/" })
        {
            Assert.Contains($"prefix: '{prefix}'", js);
        }

        // 路由比對走 appPath()，不直接讀 location.pathname
        Assert.Contains("appPath()", js);
    }

    [Fact]
    public void 圖示按鈕缺title時有aria_label退路與console_warn()
    {
        var js = ReadWeb("wwwroot", "js", "core", "ui.js");
        var start = js.IndexOf("export function button(", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 ui.js 的 button()");
        var end = js.IndexOf("\nexport ", start + 10, StringComparison.Ordinal);
        var body = end > 0 ? js[start..end] : js[start..];

        Assert.Contains("console.warn", body);
        Assert.Contains("ICON_FALLBACK_LABELS", body);
        Assert.DoesNotContain("throw ", body);
        Assert.Contains("ICON_FALLBACK_LABELS", js);
        foreach (var pair in new[] { "'x-lg': '關閉'", "'trash': '刪除'", "'pencil': '編輯'", "'arrow-clockwise': '重新整理'", "'clipboard': '複製'" })
        {
            Assert.Contains(pair, js);
        }
    }

    [Fact]
    public void modal內互動元件有focus_visible規則()
    {
        var css = ReadWeb("wwwroot", "css", "site.css");
        foreach (var sel in new[] { ".modal .btn:focus-visible", ".modal .form-control:focus-visible", ".modal .form-select:focus-visible", ".modal .form-check-input:focus-visible" })
        {
            Assert.Contains(sel, css);
        }
    }
}
