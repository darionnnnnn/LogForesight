using System.Text.RegularExpressions;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 版號第三碼＝最新一輪規劃的輪次（規則寫在 Directory.Build.props）。開了新一輪卻沒改版號，這裡就紅；
/// 第四碼（修訂）無法機器判定，靠慣例與收尾檢查。
/// </summary>
public class VersionRoundGuardTests
{
    private static readonly Regex PlanName = new(@"^FEEDBACK-(\d+)(?:-\d+)?-PLAN\.md$", RegexOptions.IgnoreCase);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    /// <summary>docs/ 與 docs/archive/ 內規劃檔的最大輪次；一個都找不到回 null（不當成通過）。</summary>
    internal static int? LatestPlanRound(IEnumerable<string> fileNames)
    {
        int? max = null;
        foreach (var name in fileNames)
        {
            var m = PlanName.Match(name);
            if (!m.Success) continue;
            var round = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (max == null || round > max) max = round;
        }
        return max;
    }

    internal static int? RoundOf(string versionNumber)
    {
        var parts = versionNumber.Split('.');
        return parts.Length >= 3 && int.TryParse(parts[2], out var round) ? round : null;
    }

    [Fact]
    public void 版號第三碼等於最新規劃輪次()
    {
        var docs = Path.Combine(FindRepoRoot(), "docs");
        var names = Directory.EnumerateFiles(docs, "*.md", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(Path.Combine(docs, "archive"))
                ? Directory.EnumerateFiles(Path.Combine(docs, "archive"), "*.md", SearchOption.TopDirectoryOnly)
                : Enumerable.Empty<string>())
            .Select(Path.GetFileName)
            .OfType<string>();

        var latest = LatestPlanRound(names);
        Assert.True(latest != null, "docs/ 與 docs/archive/ 都找不到 FEEDBACK-N-PLAN.md，無法核對版號");
        Assert.True(RoundOf(BuildVersion.Current.Number) == latest,
            $"版號 {BuildVersion.Current.Number} 的第三碼應為最新規劃輪次 {latest}（規則見 Directory.Build.props）");
    }

    [Fact]
    public void 找不到規劃檔時回null()
    {
        Assert.Null(LatestPlanRound(new[] { "WEB-SPEC.md", "BACKLOG.md" }));
    }

    [Theory]
    [InlineData(new[] { "FEEDBACK-69-PLAN.md", "FEEDBACK-70-PLAN.md", "FEEDBACK-9-PLAN.md" }, 70)]
    [InlineData(new[] { "FEEDBACK-63-2-PLAN.md", "FEEDBACK-62-PLAN.md" }, 63)]
    [InlineData(new[] { "feedback-71-plan.md", "WEB-FEEDBACK-80-PLAN.md" }, 71)]   // 大小寫不拘；別的前綴不算
    public void 最大輪次取數值而非字典序(string[] names, int expected)
    {
        Assert.Equal(expected, LatestPlanRound(names));
    }

    [Theory]
    [InlineData("1.0.70.0", 70)]
    [InlineData("1.0.70", 70)]
    [InlineData("1.0", null)]
    [InlineData("unknown", null)]
    public void 版號第三碼解析(string version, int? expected)
    {
        Assert.Equal(expected, RoundOf(version));
    }
}
