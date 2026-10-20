using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題處理狀態目錄守門：前端 core/handling-statuses.js 是狀態名稱、顏色、是否結案的唯一來源，
/// 值域必須與後端 <see cref="IssueHandlingStatuses"/> 一致；各頁不得再各寫一份清單。
/// </summary>
public class HandlingStatusCatalogUiTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string JsPath(params string[] parts) =>
        Path.Combine(new[] { FindRepoRoot(), "LogForesight.Web", "wwwroot", "js" }.Concat(parts).ToArray());

    private static string ReadCatalog()
    {
        var path = JsPath("core", "handling-statuses.js");
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>目錄每一列（以 `{ value: '...'` 起頭的一行）</summary>
    private static List<string> Rows(string js) =>
        js.Split('\n').Where(l => Regex.IsMatch(l, @"^\s*\{\s*value:\s*'")).ToList();

    [Fact]
    public void 目錄值域等於後端IssueHandlingStatuses_All()
    {
        var js = ReadCatalog();
        var values = Regex.Matches(js, @"value:\s*'([a-z_]+)'").Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.Equal(IssueHandlingStatuses.All.OrderBy(x => x), values.OrderBy(x => x));
    }

    [Fact]
    public void 目錄closed為true的集合等於後端Closed()
    {
        var js = ReadCatalog();
        var closed = Rows(js)
            .Where(l => Regex.IsMatch(l, @"closed:\s*true"))
            .Select(l => Regex.Match(l, @"value:\s*'([a-z_]+)'").Groups[1].Value)
            .ToList();

        Assert.NotEmpty(closed);
        Assert.Equal(IssueHandlingStatuses.Closed.OrderBy(x => x), closed.OrderBy(x => x));
    }

    [Fact]
    public void 各頁不再寫死狀態清單_改由目錄衍生()
    {
        foreach (var page in new[] { "issue-status-reply.js", "handling-panel.js", "work-order-detail.js", "records.js" })
        {
            var js = File.ReadAllText(JsPath("pages", page));
            Assert.DoesNotContain("value: 'wont_fix'", js);
            Assert.DoesNotContain("wont_fix:", js);
        }

        Assert.Contains("handling-statuses.js", File.ReadAllText(JsPath("pages", "issue-status-reply.js")));
        Assert.Contains("handling-statuses.js", File.ReadAllText(JsPath("pages", "handling-panel.js")));
    }
}
