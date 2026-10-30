using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// severity_rank 轉嚴重度只能經 <c>LegacySeverityRank.ToSeverity</c>（第 59 輪 E-8）：
/// 裸 <c>(IssueSeverity)rank</c> 轉型不會把舊資料的 Critical=3 正規化成 High，
/// 讀到舊資料的頁面會出現第四種嚴重度、可見性篩選與派工判定都對不上。
/// </summary>
public class SeverityCastGuardTests
{
    private static readonly Regex BareRankCast =
        new(@"\(IssueSeverity\)\s*\(?\s*[A-Za-z_.]*rank\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>唯一允許裸轉型的出口：LegacySeverityRank.ToSeverity 先 Normalize 再轉列舉</summary>
    private static bool IsAllowedExit(string file, string line) =>
        Path.GetFileName(file) == "KnownIssueCatalog.cs" && line.Contains("(IssueSeverity)Normalize(");

    [Theory]
    [InlineData("var s = (IssueSeverity)rank;", true)]
    [InlineData("var s = (IssueSeverity)(x.SeverityRank);", true)]
    [InlineData("var s = (IssueSeverity) row.MaxSeverityRank;", true)]
    [InlineData("var s = (IssueSeverity)x.severityrank;", true)]
    [InlineData("var s = (IssueSeverity)Normalize(rank);", false)]
    [InlineData("var s = (IssueSeverity)level;", false)]
    public void 正規式涵蓋各種寫法且不分大小寫(string line, bool expected) =>
        Assert.Equal(expected, BareRankCast.IsMatch(line));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [Fact]
    public void 正式碼不得以裸轉型把排名轉成嚴重度()
    {
        var root = FindRepoRoot();
        var files = new[] { "LogForesight.Core", "LogForesight.Web" }
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(files.Count > 100, $"掃描檔數異常：{files.Count}");

        var hits = files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)))
            .Where(x => BareRankCast.IsMatch(x.line) && !IsAllowedExit(x.f, x.line))
            .Select(x => $"{Path.GetRelativePath(root, x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(hits.Count == 0, "請改用 LegacySeverityRank.ToSeverity：" + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }
}
