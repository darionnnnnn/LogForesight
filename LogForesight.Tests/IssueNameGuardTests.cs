using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// K5 守門：正式碼不得再有第二個問題名稱產生點——三個舊函式名與「來源 空白 事件編號」的插值拼法都不能出現，
/// 一律走 <c>IssueNames.For</c>（或有完整簽章時的 <c>SourceEventLabel</c>）。
/// </summary>
public class IssueNameGuardTests
{
    // 插值裡「來源類 空白 事件編號類」：{issue.Source} {issue.EventId}、{source} {eventId}、{x.SourceName} {x.EventId}
    private static readonly Regex SourceSpaceEventId = new(
        @"\{[A-Za-z_.]*[sS]ource[A-Za-z_.]*\} \{[A-Za-z_.]*[eE]ventId[A-Za-z_.]*\}", RegexOptions.Compiled);

    private static readonly Regex OldFunctions = new(
        @"HandlingTextHelpers\.IssueLabel\(|\bstring LabelOf\(string source|\bLabelOf\(\s*[A-Za-z_.]*[sS]ource", RegexOptions.Compiled);

    // 前端：頁面不得自拼「來源 (編號)」「來源 編號」，一律用後端 DTO 的 issueLabel／label／displayLabel
    private static readonly Regex JsSourceEventId = new(
        @"\$\{[\w.?]*[sS]ource\w*\}\s*[（(]?\$\{[\w.?]*[eE]ventId\}", RegexOptions.Compiled);

    [Fact]
    public void 前端守門規則抓得到反例()
    {
        Assert.Matches(JsSourceEventId, "name.textContent = `${issue.source} (${issue.eventId})`;");
        Assert.Matches(JsSourceEventId, "`${item.sourceName} ${item.eventId}`");
        Assert.Matches(JsSourceEventId, "`${group.source}（${group.eventId}）`");
        // 不誤傷：查詢字串與只用其中一個欄位
        Assert.DoesNotMatch(JsSourceEventId, "params.set('source', group.source);");
        Assert.DoesNotMatch(JsSourceEventId, "`${group.issueLabel}`");
    }

    [Fact]
    public void 前端頁面沒有自拼來源與事件編號的問題名稱()
    {
        var js = Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(js, "*.js", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (JsSourceEventId.IsMatch(lines[i])) offenders.Add($"{Path.GetRelativePath(js, file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, "問題名稱請用後端 DTO 的 issueLabel：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void 守門規則抓得到反例()
    {
        Assert.Matches(SourceSpaceEventId, "IssueLabel = $\"{source} {eventId}\",");
        Assert.Matches(SourceSpaceEventId, "var k = ($\"{issue.Source} {issue.EventId}\", x);");
        Assert.Matches(SourceSpaceEventId, "$\"{existing.SourceName} {existing.EventId}\"");
        Assert.Matches(OldFunctions, "HandlingTextHelpers.IssueLabel(issue)");
        Assert.Matches(OldFunctions, "private static string LabelOf(string source, int eventId) => x;");
        Assert.Matches(OldFunctions, "IssueLabel = LabelOf(s.Source, s.EventId),");
        // 不誤傷：括號與全形括號的格式、SourceEventLabel
        Assert.DoesNotMatch(SourceSpaceEventId, "$\"{source} ({eventId})\"");
        Assert.DoesNotMatch(OldFunctions, "ContinuationStopReasons.LabelOf(o.ContinuationStoppedReason)");
    }

    [Fact]
    public void 正式碼沒有第二個問題名稱產生點()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var project in new[] { "LogForesight.Core", "LogForesight.Web" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (SourceSpaceEventId.IsMatch(lines[i]) || OldFunctions.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0, "問題名稱請走 IssueNames.For：\n" + string.Join("\n", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }
}
