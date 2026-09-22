using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

public class PagePrimaryActionUiTests
{
    private static string View(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "LogForesight.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "LogForesight.Web", "Views", "Pages", name));
    }

    private static string BeforeModal(string html)
    {
        var match = Regex.Match(html, "<div[^>]*class=\"[^\"]*\\bmodal\\b", RegexOptions.IgnoreCase);
        return match.Success ? html[..match.Index] : html;
    }

    private static int PrimaryCount(string html) => Regex.Matches(html,
        "<(?:button|a)\\b[^>]*class=\"[^\"]*\\bbtn-primary\\b[^\"]*\"", RegexOptions.IgnoreCase).Count;

    [Fact]
    public void 主機頁面選取列不與新增主機競爭()
    {
        var shell = BeforeModal(View("Hosts.cshtml"));
        Assert.Equal(1, PrimaryCount(shell));
        var batch = Regex.Match(shell, "<button[^>]*id=\"btn-batch-groups\"[^>]*>");
        Assert.True(batch.Success);
        Assert.Contains("btn-outline-primary", batch.Value);
    }

    [Fact]
    public void Netiq設定頁籤同時只顯示一個主要動作()
    {
        var html = BeforeModal(View("Netiq.cshtml"));
        var start = html.IndexOf("data-panel=\"config\"", StringComparison.Ordinal);
        var end = html.IndexOf("data-panel=\"import\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var config = html[start..end];
        Assert.Equal(1, PrimaryCount(config));
        Assert.Contains("id=\"netiq-options-save\"", config);
        Assert.Contains("btn-outline-primary", config);
    }

    [Fact]
    public void 群組頁籤各有一個主要動作_同一彈窗查找降階()
    {
        var html = View("Groups.cshtml");
        var shell = BeforeModal(html);
        var first = shell.IndexOf("data-panel=\"user-groups\"", StringComparison.Ordinal);
        var second = shell.IndexOf("data-panel=\"host-groups\"", first, StringComparison.Ordinal);
        var third = shell.IndexOf("data-panel=\"access\"", second, StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first && third > second);
        Assert.Equal(1, PrimaryCount(shell[first..second]));
        Assert.Equal(1, PrimaryCount(shell[second..third]));
        Assert.Equal(0, PrimaryCount(shell[third..]));
        var search = Regex.Match(html, "<button[^>]*id=\"members-search\"[^>]*>");
        var apply = Regex.Match(html, "<button[^>]*id=\"members-apply\"[^>]*>");
        Assert.True(search.Success && apply.Success);
        Assert.Contains("btn-outline-primary", search.Value);
        Assert.Contains("btn-primary", apply.Value);
    }
}
