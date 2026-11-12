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
}
