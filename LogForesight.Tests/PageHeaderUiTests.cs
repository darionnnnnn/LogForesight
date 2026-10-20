using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 56 輪 F1：頁首元件（ui.js renderPageHeader）的守門。
/// topbar 是每頁唯一的 h1；頁內頁首只放一句副標與頁面層級動作（DESIGN-SYSTEM §6b、WEB-SPEC §8.2）。
/// </summary>
public class PageHeaderUiTests
{
    /// <summary>
    /// 套用頁首的頁（cshtml 檔名 → 對應頁模組）。其餘頁不套用的理由：
    /// 頂部已是 .lf-toolbar 篩選列（Audit／Records／Hosts／Users）、
    /// 頁面層級動作鈕在頁籤或卡片內（Groups／Netiq／Prtg／Settings／Runs／WorkOrders／Setup）、
    /// 頂部即主體內容（Dashboard／Reports／HelpManual／WorkOrderDetail），
    /// 或為獨立殼頁（Login／Error／AccessDenied）。
    /// </summary>
    private static readonly Dictionary<string, string> AppliedPages = new()
    {
        ["HandlerDetail.cshtml"] = "handler-detail.js",
        ["HostDetail.cshtml"] = "host-detail.js",
        ["Imports.cshtml"] = "imports.js",
        ["IssueOwners.cshtml"] = "issue-owners.js",
        ["PermissionChanges.cshtml"] = "permission-changes.js",
        ["RecordDetail.cshtml"] = "record-detail.js",
        ["Rules.cshtml"] = "rules.js",
        ["UserDetail.cshtml"] = "user-detail.js",
    };

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return Path.Combine(dir!.FullName, "LogForesight.Web");
    }

    private static IEnumerable<string> PageFiles() =>
        Directory.GetFiles(Path.Combine(WebRoot(), "Views", "Pages"), "*.cshtml");

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { WebRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void 頁面檔案不得再有_h1_topbar是唯一標題()
    {
        var files = PageFiles().ToList();
        Assert.True(files.Count >= 20, "頁面檔案數量異常，可能路徑錯誤");

        foreach (var file in files)
        {
            Assert.DoesNotContain("<h1", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void 套用頁首的頁_cshtml有容器且頁模組呼叫renderPageHeader()
    {
        foreach (var (view, module) in AppliedPages)
        {
            var html = Read("Views", "Pages", view);
            Assert.Contains("id=\"page-header\"", html);

            var js = Read("wwwroot", "js", "pages", module);
            Assert.Contains("renderPageHeader(", js);
            // 必須由 core/ui.js 匯入，不得各頁自己重寫
            Assert.Matches(new Regex(@"import\s*\{[^}]*\brenderPageHeader\b[^}]*\}\s*from\s*'\.\./core/ui\.js'"), js);
        }
    }

    [Fact]
    public void 清單外的頁不得出現頁首容器()
    {
        foreach (var file in PageFiles())
        {
            var name = Path.GetFileName(file);
            if (AppliedPages.ContainsKey(name)) continue;
            Assert.DoesNotContain("id=\"page-header\"", File.ReadAllText(file));
        }
    }

    [Fact]
    public void 套用頁首的頁_頁面層級動作不再留在舊的_hint_或卡片標頭()
    {
        // IssueOwners 的「新增規則」已搬進頁首動作；cshtml 不得再有同 id 的按鈕（會變兩顆同 id）
        Assert.DoesNotContain("id=\"issue-owner-new\"", Read("Views", "Pages", "IssueOwners.cshtml"));
        // RecordDetail 的「下一筆未處理」同理
        Assert.DoesNotContain("id=\"next-unhandled\"", Read("Views", "Pages", "RecordDetail.cshtml"));
    }

    [Fact]
    public void 頁首元件與樣式存在()
    {
        var ui = Read("wwwroot", "js", "core", "ui.js");
        Assert.Contains("export function renderPageHeader", ui);
        Assert.Contains("lf-page-header__actions", ui);
        Assert.Contains("lf-page-header__subtitle", ui);

        var css = Read("wwwroot", "css", "site.css");
        Assert.Contains(".lf-page-header", css);
        Assert.Contains(".lf-page-header__actions", css);
    }
}
