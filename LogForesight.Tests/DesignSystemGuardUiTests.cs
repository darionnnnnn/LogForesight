using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 設計系統 v3 的棘輪式守門（docs/FEEDBACK-69-PLAN.md D7、A-3～A-5）。
///
/// 每條規則有一份「尚未改版的檔案」允許清單：清單外的檔案一律不得出現舊樣式；
/// 清單內的檔案改乾淨後必須同步從清單移除（清單不得有多餘項），否則守門就悄悄失效。
/// 各頁批次完成時把該頁移出清單，收尾時清單歸零、規則改為無條件。
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

    /// <summary>掃描範圍內命中 pattern 的檔案，與允許清單比對：清單外命中＝退步；清單內沒命中＝清單過期。</summary>
    private static void AssertRatchet(IEnumerable<string> scope, Regex pattern, IReadOnlySet<string> allowed, string rule)
    {
        var hits = scope.Where(f => pattern.IsMatch(Read(f))).ToHashSet();
        var regressions = hits.Except(allowed).OrderBy(f => f).ToList();
        var stale = allowed.Except(hits).OrderBy(f => f).ToList();
        Assert.True(regressions.Count == 0,
            $"{rule}：以下檔案不在允許清單內卻出現舊樣式，改用設計系統 v3 的元件或角色類別：{string.Join("、", regressions)}");
        Assert.True(stale.Count == 0,
            $"{rule}：以下檔案已改乾淨，請從允許清單移除（清單不得有多餘項）：{string.Join("、", stale)}");
    }

    private static IEnumerable<string> PageScope() =>
        Files("wwwroot/js/pages", "*.js").Concat(Files("Views/Pages", "*.cshtml"));

    // ── A-3 文字語意色：驗證與操作結果走 statusNote、風險狀態走 lf-text-alert、其餘回一般文字色 ──

    internal static readonly Regex AdHocTextColor =
        new(@"\btext-(danger|warning|success)\b|\bsmall text-muted\b|\btext-muted small\b", RegexOptions.Compiled);

    private static readonly HashSet<string> AdHocTextColorAllowed = new(StringComparer.Ordinal)
    {
        "Views/Pages/Audit.cshtml", "Views/Pages/Dashboard.cshtml", "Views/Pages/Groups.cshtml",
        "Views/Pages/HandlerDetail.cshtml", "Views/Pages/HelpManual.cshtml", "Views/Pages/HostDetail.cshtml",
        "Views/Pages/Hosts.cshtml", "Views/Pages/Imports.cshtml", "Views/Pages/IssueOwners.cshtml",
        "Views/Pages/Netiq.cshtml", "Views/Pages/PermissionChanges.cshtml", "Views/Pages/Prtg.cshtml",
        "Views/Pages/RecordDetail.cshtml", "Views/Pages/Records.cshtml", "Views/Pages/Reports.cshtml",
        "Views/Pages/Rules.cshtml", "Views/Pages/Runs.cshtml", "Views/Pages/Settings.cshtml",
        "Views/Pages/Setup.cshtml", "Views/Pages/UserDetail.cshtml", "Views/Pages/Users.cshtml",
        "Views/Pages/WorkOrderDetail.cshtml", "Views/Pages/WorkOrders.cshtml",
        "wwwroot/js/pages/audit.js", "wwwroot/js/pages/chat-panel.js", "wwwroot/js/pages/dashboard.js",
        "wwwroot/js/pages/groups.js", "wwwroot/js/pages/handler-detail.js", "wwwroot/js/pages/handling-panel.js",
        "wwwroot/js/pages/help-manual.js", "wwwroot/js/pages/host-detail.js", "wwwroot/js/pages/hosts.js",
        "wwwroot/js/pages/issue-mute-modal.js", "wwwroot/js/pages/issue-owners.js", "wwwroot/js/pages/issue-status-reply.js",
        "wwwroot/js/pages/netiq-import-wizard.js", "wwwroot/js/pages/netiq.js", "wwwroot/js/pages/permission-changes.js",
        "wwwroot/js/pages/prtg-admin.js", "wwwroot/js/pages/prtg-calibration.js", "wwwroot/js/pages/prtg-guard.js",
        "wwwroot/js/pages/record-detail.js", "wwwroot/js/pages/records.js", "wwwroot/js/pages/reports.js",
        "wwwroot/js/pages/rules.js", "wwwroot/js/pages/runs.js", "wwwroot/js/pages/settings.js",
        "wwwroot/js/pages/setup.js", "wwwroot/js/pages/user-detail.js", "wwwroot/js/pages/users.js",
        "wwwroot/js/pages/work-order-detail.js", "wwwroot/js/pages/work-orders.js",
    };

    [Fact]
    public void 頁面不得現場拼文字語意色_棘輪() =>
        AssertRatchet(PageScope(), AdHocTextColor, AdHocTextColorAllowed, "文字語意色");

    // ── A-4 揭露：btn-link 不再當按鈕樣式，就地展開走 disclosure、開視窗走外框按鈕、換頁走連結 ──

    internal static readonly Regex BtnLink = new(@"\bbtn-link\b", RegexOptions.Compiled);

    private static readonly HashSet<string> BtnLinkAllowed = new(StringComparer.Ordinal)
    {
        "Views/Pages/IssueOwners.cshtml",
        "wwwroot/js/core/layout.js", "wwwroot/js/core/note-editor.js", "wwwroot/js/core/ui.js",
        "wwwroot/js/pages/audit.js", "wwwroot/js/pages/groups.js", "wwwroot/js/pages/handler-detail.js",
        "wwwroot/js/pages/issue-status-reply.js", "wwwroot/js/pages/permission-changes.js",
        "wwwroot/js/pages/prtg-admin.js", "wwwroot/js/pages/prtg-value-readiness.js",
        "wwwroot/js/pages/record-detail.js", "wwwroot/js/pages/runs.js",
    };

    [Fact]
    public void 不得以btn_link當按鈕樣式_棘輪() =>
        AssertRatchet(Files("wwwroot/js", "*.js").Concat(Files("Views", "*.cshtml")), BtnLink, BtnLinkAllowed, "btn-link");

    // ── A-5 按鈕工廠：頁面模組的按鈕一律經 ui.js button()，不手寫 'btn btn-…' ──

    internal static readonly Regex HandwrittenButton = new(@"['""`]btn btn-", RegexOptions.Compiled);

    private static readonly HashSet<string> HandwrittenButtonAllowed = new(StringComparer.Ordinal)
    {
        "wwwroot/js/pages/audit.js", "wwwroot/js/pages/chat-panel.js", "wwwroot/js/pages/groups.js",
        "wwwroot/js/pages/handler-detail.js", "wwwroot/js/pages/handling-panel.js", "wwwroot/js/pages/help-manual.js",
        "wwwroot/js/pages/host-detail.js", "wwwroot/js/pages/issue-mute-modal.js", "wwwroot/js/pages/issue-owners.js",
        "wwwroot/js/pages/issue-status-reply.js", "wwwroot/js/pages/netiq-import-wizard.js", "wwwroot/js/pages/netiq.js",
        "wwwroot/js/pages/permission-changes.js", "wwwroot/js/pages/prtg-admin.js", "wwwroot/js/pages/prtg-value-readiness.js",
        "wwwroot/js/pages/record-detail.js", "wwwroot/js/pages/records.js", "wwwroot/js/pages/runs.js",
        "wwwroot/js/pages/settings.js", "wwwroot/js/pages/setup.js", "wwwroot/js/pages/user-detail.js",
        "wwwroot/js/pages/work-order-detail.js",
    };

    [Fact]
    public void 頁面模組不得手寫按鈕類別_棘輪() =>
        AssertRatchet(Files("wwwroot/js/pages", "*.js"), HandwrittenButton, HandwrittenButtonAllowed, "手寫按鈕類別");

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
}
