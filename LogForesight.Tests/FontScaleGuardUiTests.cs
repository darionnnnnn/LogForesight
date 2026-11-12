using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 字級系統的守門（docs/DESIGN-SYSTEM.md §3）：根字級只由瀏覽器預設與使用者偏好決定、字階固定且級距拉得開、
/// 控制項不大於內文。需要渲染才能判定的部分（實際字級、最小字、點擊區）由 ui-shots 的量測情境負責。
/// </summary>
public sealed class FontScaleGuardUiTests
{
    private static string CssDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return Path.Combine(dir!.FullName, "LogForesight.Web", "wwwroot", "css");
    }

    /// <summary>去掉註解：說明文字會提到 clamp、vw 等字眼，不能算進規則本文。</summary>
    private static string ReadCss(string file) =>
        Regex.Replace(File.ReadAllText(Path.Combine(CssDir(), file), Encoding.UTF8), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    /// <summary>取出 selector 完全相符的第一個規則區塊的內容；找不到即失敗（避免改名後守門悄悄失效）。</summary>
    private static string RuleBody(string css, string selector)
    {
        var m = Regex.Match(css, $@"(?:^|\}})\s*{Regex.Escape(selector)}\s*\{{([^}}]*)\}}", RegexOptions.Multiline);
        Assert.True(m.Success, $"找不到規則 {selector}");
        return m.Groups[1].Value;
    }

    private static string Declaration(string body, string property)
    {
        var m = Regex.Match(body, $@"(?:^|;|\s){Regex.Escape(property)}\s*:\s*([^;]+);");
        Assert.True(m.Success, $"規則裡沒有 {property}");
        return m.Groups[1].Value.Trim();
    }

    [Fact]
    public void 根字級只由瀏覽器預設與偏好倍率決定()
    {
        var value = Declaration(RuleBody(ReadCss("base.css"), "html"), "font-size");
        // 單位緊接在數字後面（.2vw），不能用 \b 判斷字首
        Assert.DoesNotMatch(new Regex(@"\d\s*[dsl]?(vw|vh|vmin|vmax)\b|clamp\(|\d\s*px\b"), value);
        Assert.Contains("var(--lf-font-scale", value);
    }

    [Fact]
    public void 偏好倍率是小八七點五中一百大一一二點五()
    {
        var css = ReadCss("base.css");
        Assert.Equal(".875", Declaration(RuleBody(css, ":root[data-font-scale=\"small\"]"), "--lf-font-scale"));
        Assert.Equal("1.125", Declaration(RuleBody(css, ":root[data-font-scale=\"large\"]"), "--lf-font-scale"));
    }

    private static readonly string[] Steps = ["xs", "sm", "base", "lg", "xl", "2xl", "stat"];

    private static readonly Dictionary<string, double> Expected = new()
    {
        ["xs"] = .75, ["sm"] = .875, ["base"] = 1, ["lg"] = 1.125, ["xl"] = 1.25, ["2xl"] = 1.5, ["stat"] = 1.875
    };

    /// <summary>字階 token 的 rem 值；xs 寫成 max(.75rem, 12px)（12px 下限），取其中的 rem。</summary>
    private static Dictionary<string, double> StepValues()
    {
        var root = RuleBody(ReadCss("base.css"), ":root");
        var values = new Dictionary<string, double>();
        foreach (var step in Steps)
        {
            var raw = Declaration(root, $"--lf-font-size-{step}");
            var rem = Regex.Match(raw, @"(\d*\.?\d+)rem");
            Assert.True(rem.Success, $"--lf-font-size-{step} 必須以 rem 表示，實際是 {raw}");
            values[step] = double.Parse(rem.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return values;
    }

    [Fact]
    public void 字階數值符合設計系統()
    {
        var values = StepValues();
        foreach (var step in Steps) Assert.True(Math.Abs(values[step] - Expected[step]) < 1e-9, $"--lf-font-size-{step} 應為 {Expected[step]}rem，實際 {values[step]}rem");
        Assert.Matches(new Regex(@"max\(\s*\.75rem\s*,\s*12px\s*\)"), Declaration(RuleBody(ReadCss("base.css"), ":root"), "--lf-font-size-xs"));
    }

    [Fact]
    public void 相鄰字階至少差一點一一倍()
    {
        var values = StepValues();
        for (var i = 1; i < Steps.Length; i++)
        {
            var ratio = values[Steps[i]] / values[Steps[i - 1]];
            Assert.True(ratio >= 1.11 - 1e-9, $"{Steps[i - 1]}→{Steps[i]} 比值 {ratio:F3} < 1.11，相鄰兩級分不出層次");
        }
    }

    [Fact]
    public void 控制項字級不大於內文()
    {
        var css = ReadCss("base.css");
        Assert.Equal("var(--lf-font-size-base)", Declaration(RuleBody(css, ".btn"), "--bs-btn-font-size"));
        Assert.Equal("var(--lf-font-size-sm)", Declaration(RuleBody(css, ".btn-sm,\n.btn-group-sm > .btn"), "--bs-btn-font-size"));
        Assert.Equal("var(--lf-font-size-base)", Declaration(RuleBody(css, ".form-control,\n.form-select"), "font-size"));
        Assert.Equal("var(--lf-font-size-base)", Declaration(RuleBody(css, ":root"), "--bs-body-font-size"));
    }

    private static readonly string[] CssFiles = ["base.css", "components.css", "layout.css", "pages.css"];

    /// <summary>字級只能引用字階 token；以下是有理由的例外（不是文字角色）。</summary>
    private static readonly string[] AllowedFontSizes =
    [
        "calc(100% * var(--lf-font-scale, 1))",     // 根字級
        "var(--lf-brand-name-size)",                // 品牌標誌，隨側欄寬度另算
        "var(--lf-brand-subtitle-size)",
        "var(--lf-probe-font-size)",                // brand-align 量測用的探針，複製實際字級
        "calc(18px * var(--lf-wall-scale, 1))",     // 大螢幕模式的根字級（依視窗高度）
        "calc(64px * var(--lf-wall-scale, 1))",     // 大螢幕模式的 KPI 數字
        ".6rem"                                     // .btn-close.lf-close-xs：關閉鈕圖示的尺寸，不是文字
    ];

    private static readonly Regex TokenFontSize = new(@"^var\(--lf-font-size-(xs|sm|base|lg|xl|2xl|stat)\)(\s*!important)?$");

    [Fact]
    public void 樣式表的字級只引用字階token()
    {
        var bad = new List<string>();
        foreach (var file in CssFiles)
        {
            foreach (Match m in Regex.Matches(ReadCss(file), @"(?<![\w-])(font-size|--bs-btn-font-size)\s*:\s*([^;}]+)"))
            {
                var value = m.Groups[2].Value.Trim();
                if (TokenFontSize.IsMatch(value) || value == "inherit" || AllowedFontSizes.Contains(value)) continue;
                bad.Add($"{file}：{m.Groups[1].Value}: {value}");
            }
        }
        Assert.True(bad.Count == 0, "字級要引用 --lf-font-size-* 字階 token（DESIGN-SYSTEM §3）：\n" + string.Join("\n", bad));
    }

    private static string WebDir() => Path.GetFullPath(Path.Combine(CssDir(), "..", ".."));

    /// <summary>class 屬性或 className 字串裡出現的 Bootstrap 字級類別：fs-1～fs-3、display-*、h1～h4 的字級含 vw，落在字階外。</summary>
    internal static readonly Regex ForbiddenSizeClass =
        new(@"class(Name)?\s*[=:+]\s*[""'`][^""'`]*(?<![\w-])(fs-[1-3]|display-[1-6]|h[1-4])(?![\w-])", RegexOptions.Compiled);

    [Fact]
    public void 不使用字級含視窗單位的Bootstrap類別()
    {
        var files = Directory.GetFiles(Path.Combine(WebDir(), "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(WebDir(), "wwwroot", "js"), "*.js", SearchOption.AllDirectories))
            .Where(f => !f.Replace('\\', '/').Contains("/lib/"))
            .ToList();
        Assert.True(files.Count > 0, "掃描範圍沒有任何檔案，守門的路徑寫錯了");
        var hits = files.Where(f => ForbiddenSizeClass.IsMatch(File.ReadAllText(f, Encoding.UTF8)))
            .Select(f => Path.GetRelativePath(WebDir(), f)).OrderBy(f => f).ToList();
        Assert.True(hits.Count == 0, "改用文字角色類別或 fs-4／fs-5／h5／h6（已對到字階）：" + string.Join("、", hits));
    }
}
