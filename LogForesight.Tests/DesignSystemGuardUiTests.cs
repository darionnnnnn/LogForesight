using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 設計系統 v3 的守門（docs/DESIGN-SYSTEM.md §8）：文字語意色、btn-link、手寫按鈕類別在全站前端無條件禁止，
/// 另守樣式表唯一引用點、中斷點白名單與按鈕變體白名單。
/// </summary>
public sealed class DesignSystemGuardUiTests
{
    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return Path.Combine(dir!.FullName, "LogForesight.Web");
    }

    private static IEnumerable<string> Files(string relativeDir, string pattern) =>
        Directory.GetFiles(Path.Combine(WebRoot(), relativeDir), pattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(WebRoot(), f).Replace('\\', '/'));

    private static string Read(string relative) => File.ReadAllText(Path.Combine(WebRoot(), relative), Encoding.UTF8);

    /// <summary>掃描範圍內任何檔案命中 pattern 即失敗；scope 掃不到檔案也算失敗（路徑寫錯時守門會悄悄失效）。</summary>
    private static void AssertNone(IEnumerable<string> scope, Regex pattern, string rule)
    {
        var files = scope.ToList();
        Assert.True(files.Count > 0, $"{rule}：掃描範圍沒有任何檔案，守門的路徑寫錯了");
        var hits = files.Where(f => pattern.IsMatch(Read(f))).OrderBy(f => f).ToList();
        Assert.True(hits.Count == 0, $"{rule}：以下檔案出現舊樣式，改用設計系統 v3 的元件或角色類別：{string.Join("、", hits)}");
    }

    private static IEnumerable<string> PageScope() =>
        Files("wwwroot/js/pages", "*.js").Concat(Files("wwwroot/js/core", "*.js")).Concat(Files("Views", "*.cshtml"));

    // ── A-3 文字語意色：驗證與操作結果走 statusNote、風險狀態走 lf-text-alert、其餘回一般文字色 ──

    internal static readonly Regex AdHocTextColor =
        new(@"\btext-(danger|warning|success)\b|\bsmall text-muted\b|\btext-muted small\b", RegexOptions.Compiled);

    [Fact]
    public void 頁面不得現場拼文字語意色() =>
        AssertNone(PageScope(), AdHocTextColor, "文字語意色");

    // ── A-4 揭露：btn-link 不再當按鈕樣式，就地展開走 disclosure、開視窗走外框按鈕、換頁走連結 ──

    internal static readonly Regex BtnLink = new(@"\bbtn-link\b", RegexOptions.Compiled);

    [Fact]
    public void 不得以btn_link當按鈕樣式() =>
        AssertNone(Files("wwwroot/js", "*.js").Concat(Files("Views", "*.cshtml")), BtnLink, "btn-link");

    // ── A-5 按鈕工廠：頁面模組的按鈕一律經 ui.js button()，不手寫 'btn btn-…' ──

    internal static readonly Regex HandwrittenButton = new(@"['""`]btn btn-", RegexOptions.Compiled);

    [Fact]
    public void 頁面模組不得手寫按鈕類別() =>
        AssertNone(Files("wwwroot/js/pages", "*.js"), HandwrittenButton, "手寫按鈕類別");

    // ── A-1 樣式表唯一引用點：頁面殼一律 <partial name="_Styles" />，四檔載入順序只寫一次 ──

    [Fact]
    public void 樣式表只在共用partial引用()
    {
        var offenders = Files("Views", "*.cshtml")
            .Where(f => f != "Views/Shared/_Styles.cshtml" && Read(f).Contains("rel=\"stylesheet\"", StringComparison.Ordinal))
            .ToList();
        Assert.True(offenders.Count == 0, $"以下檢視自行引用樣式表，改用 <partial name=\"_Styles\" />：{string.Join("、", offenders)}");

        var styles = Read("Views/Shared/_Styles.cshtml");
        var order = SiteCss.Files.Select(f => styles.IndexOf($"~/css/{f}", StringComparison.Ordinal)).ToList();
        Assert.True(order.All(i => i >= 0), "_Styles.cshtml 必須引用 SiteCss.Files 的每一個樣式表");
        Assert.True(order.SequenceEqual(order.OrderBy(i => i)), "_Styles.cshtml 的載入順序必須與 SiteCss.Files 相同");
        Assert.Equal(SiteCss.Files.Length + 1, Regex.Matches(styles, "asp-append-version=\"true\"").Count);
        Assert.False(File.Exists(Path.Combine(SiteCss.CssDir(), "site.css")), "site.css 已拆成四檔，不得再出現");
    }

    // ── A-5 按鈕變體白名單：primary／outline-primary／outline-secondary／outline-danger／danger ──

    private static readonly HashSet<string> AllowedButtonVariants = new(StringComparer.Ordinal)
    {
        "primary", "outline-primary", "outline-secondary", "outline-danger", "danger",
    };

    internal static IEnumerable<string> ButtonVariants(string text) =>
        Regex.Matches(text, @"\bbtn-((?:outline-)?(?:primary|secondary|success|danger|warning|info|light|dark))\b").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(text, @"\b(?:button|linkButton|makeButton)\([^;]*?\bvariant:\s*'([\w-]+)'").Select(m => m.Groups[1].Value))
            .Concat(Regex.Matches(text, @"\bconfirmVariant:\s*'([\w-]+)'").Select(m => m.Groups[1].Value));

    [Fact]
    public void 按鈕只用設計系統的五種變體()
    {
        var bad = Files("wwwroot/js", "*.js").Concat(Files("Views", "*.cshtml"))
            .SelectMany(f => ButtonVariants(Read(f)).Select(v => (f, v)))
            .Where(x => !AllowedButtonVariants.Contains(x.v))
            // 唯一例外：深色側欄上的登出鈕，淺色底的變體在深底上看不見
            .Where(x => !(x.f == "Views/Shared/_Layout.cshtml" && x.v == "outline-light"))
            .Select(x => $"{x.f}：{x.v}")
            .ToList();
        Assert.True(bad.Count == 0, $"以下按鈕用了白名單外的變體（只能 primary／outline-primary／outline-secondary／outline-danger／danger）：{string.Join("、", bad)}");
    }

    [Theory]
    [InlineData("class=\"btn btn-sm btn-outline-danger\"", true)]
    [InlineData("class=\"btn btn-success\"", false)]
    [InlineData("button('啟用', { variant: 'outline-success', onClick })", false)]
    [InlineData("confirmVariant: 'warning'", false)]
    [InlineData("confirmVariant: 'primary'", true)]
    [InlineData("id=\"btn-print-report\"", true)]
    public void 按鈕變體守門的邊界(string sample, bool allowed) =>
        Assert.Equal(allowed, ButtonVariants(sample).All(AllowedButtonVariants.Contains));

    // ── A-2 中斷點：只用 576／768／992／1200／1600（min-width: N、max-width: N−0.02）──

    private static readonly HashSet<string> AllowedBreakpoints = new(StringComparer.Ordinal)
    {
        "min-width: 576px", "min-width: 768px", "min-width: 992px", "min-width: 1200px", "min-width: 1600px",
        "max-width: 575.98px", "max-width: 767.98px", "max-width: 991.98px", "max-width: 1199.98px", "max-width: 1599.98px",
        // 唯一例外：KPI 卡收合時「全部卡片放得下」的內容寬度（components.css 註解說明），不是版面級中斷點
        "max-width: 1799.98px",
    };

    internal static IEnumerable<string> MediaWidthConditions(string css) =>
        Regex.Matches(css, @"@media[^{]*").SelectMany(m => Regex.Matches(m.Value, @"(?:(?:min|max)-width:\s*[\d.]+[a-z%]*|\bwidth\s*[<>]=?\s*[\d.]+[a-z%]*)").Select(c => Regex.Replace(c.Value, @":\s*", ": ")));

    [Fact]
    public void 中斷點只用設計系統的五個值()
    {
        var bad = SiteCss.Files
            .SelectMany(f => MediaWidthConditions(File.ReadAllText(Path.Combine(SiteCss.CssDir(), f))).Select(c => (f, c)))
            .Where(x => !AllowedBreakpoints.Contains(x.c))
            .Select(x => $"{x.f}：{x.c}")
            .ToList();
        Assert.True(bad.Count == 0, $"以下 @media 用了設計系統外的中斷點（只能 576／768／992／1200／1600）：{string.Join("、", bad)}");
    }

    [Theory]
    [InlineData("@media (max-width: 767.98px) {", true)]
    [InlineData("@media (max-width: 768px) {", false)]
    [InlineData("@media (min-width: 1600px) {", true)]
    [InlineData("@media (min-width:1400px) {", false)]
    [InlineData("@media (min-width: 48em) {", false)]
    [InlineData("@media (min-width: 100rem) {", false)]
    [InlineData("@media (width >= 768px) {", false)]
    [InlineData("@media print {", true)]
    public void 中斷點守門的邊界(string sample, bool allowed) =>
        Assert.Equal(allowed, MediaWidthConditions(sample).All(AllowedBreakpoints.Contains));

    /// <summary>守門本身的邊界兩側：該命中的命中、相似但合法的不命中（避免 regex 寫寬或寫窄）。</summary>
    [Theory]
    [InlineData("el.className = 'text-danger small';", true)]
    [InlineData("<span class=\"small text-muted\">", true)]
    [InlineData("class=\"text-muted small\"", true)]
    [InlineData("class=\"lf-text-alert\"", false)]
    [InlineData("class=\"text-muted\"", false)]
    [InlineData("class=\"text-danger-emphasis\"", true)]
    public void 文字語意色守門的邊界(string sample, bool expected) =>
        Assert.Equal(expected, AdHocTextColor.IsMatch(sample));

    [Theory]
    [InlineData("toggle.className = 'btn btn-link btn-sm p-0';", true)]
    [InlineData("class=\"lf-disclosure\"", false)]
    [InlineData("className = 'btn btn-outline-secondary btn-sm'", true)]
    [InlineData("button('儲存', { variant: 'primary' })", false)]
    public void 揭露與按鈕守門的邊界(string sample, bool expected)
    {
        var isLinkSample = sample.Contains("link") || sample.Contains("disclosure");
        Assert.Equal(expected, isLinkSample ? BtnLink.IsMatch(sample) : HandwrittenButton.IsMatch(sample));
    }

    // ── 第 71 輪：標記與顏色訊號（DESIGN-SYSTEM §8.8）——有底色的標記只留給層級最高一級與「重大」 ──

    private static string Rule(string css, string selector)
    {
        var m = Regex.Match(css, $@"(?m)^{Regex.Escape(selector)}\s*\{{([^}}]*)\}}");
        Assert.True(m.Success, $"樣式表找不到 {selector} 規則");
        return m.Groups[1].Value;
    }

    [Fact]
    public void 一般標記的變體只決定圓點顏色_不設底色()
    {
        var css = Regex.Replace(SiteCss.Read(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        foreach (var variant in new[] { "danger", "warning", "success", "info", "primary", "high", "mid", "low" })
        {
            var body = Rule(css, $".lf-badge--{variant}");
            Assert.Contains("--lf-badge-dot", body);
            Assert.DoesNotMatch(@"(^|[\s;])background", body);
        }
        Assert.Matches(@"background:\s*none", Rule(css, ".lf-badge"));
    }

    [Fact]
    public void 篩選膠囊選中不用實心主色()
    {
        var css = Regex.Replace(SiteCss.Read(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var body = Rule(css, ".lf-chip.active");
        Assert.DoesNotMatch(@"background:\s*var\(--lf-primary\)", body);
    }

    [Fact]
    public void 卡片不以左側色條表達嚴重度()
    {
        var css = Regex.Replace(SiteCss.Read(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        Assert.DoesNotMatch(@"\.lf-card--(critical|warning|ok)\s*\{[^}]*border-left", css);
    }
}
