using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

public sealed class HelpNavigationConsistencyTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 手冊引用的側欄名稱須存在於實際導覽且問題章節與頁面同名()
    {
        var root = Root();
        var web = Path.Combine(root, "LogForesight.Web");
        var nav = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "core", "layout.js"));
        var labels = Regex.Matches(nav, @"\blabel:\s*'([^']+)'", RegexOptions.CultureInvariant)
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var helpDir = Path.Combine(web, "HelpContent");

        foreach (var file in Directory.GetFiles(helpDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            var content = File.ReadAllText(file);
            foreach (Match reference in Regex.Matches(content,
                @"「(?:系統管理|系統|監控作業)\s*>\s*([^>」]+)", RegexOptions.CultureInvariant))
            {
                var label = reference.Groups[1].Value.Trim();
                Assert.True(labels.Contains(label), $"{Path.GetFileName(file)} 引用不存在的導覽「{label}」");
                Assert.False(reference.Value.Contains("系統管理 > 排程作業", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} 把排程作業放錯分組");
            }
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(helpDir, "manifest.json")));
        var issueChapter = manifest.RootElement.GetProperty("chapters").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "issue-owners");
        var title = issueChapter.GetProperty("title").GetString();
        var issuePage = File.ReadAllText(Path.Combine(web, "Views", "Pages", "IssueOwners.cshtml"));
        Assert.Contains($"label: '{title}'", nav);
        Assert.Contains($"ViewData[\"Title\"] = \"{title}\"", issuePage);
    }
}
