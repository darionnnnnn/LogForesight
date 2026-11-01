using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

public sealed class CorrelationPatternIdsTests
{
    [Fact]
    public void 關聯模式共二十三種_Windows十八_Linux二_PRTG三且不含來源對齊()
    {
        Assert.Equal(23, CorrelationPatternIds.All.Length);
        Assert.Equal(18, CorrelationPatternIds.All.Count(id => !id.StartsWith("linux-", StringComparison.Ordinal) &&
            !id.StartsWith("prtg-", StringComparison.Ordinal)));
        Assert.Equal(2, CorrelationPatternIds.All.Count(id => id.StartsWith("linux-", StringComparison.Ordinal)));
        Assert.Equal(3, CorrelationPatternIds.All.Count(id => id.StartsWith("prtg-", StringComparison.Ordinal)));
        Assert.All(CorrelationPatternIds.All, id => Assert.DoesNotContain("evidence-aligned", id));
        Assert.False(CorrelationPatternIds.IsValid("prtg-storage-evidence-aligned"));
    }

    /// <summary>
    /// 文件與說明書寫的「N 種關聯模式」必須等於 <see cref="CorrelationPatternIds.All"/> 的實際數量。
    /// 只掃明確的「模式總數」句型（不是通用數字掃描）：新增或退役模式時，漏改文件會在這裡紅。
    /// </summary>
    [Fact]
    public void 文件與說明書寫的關聯模式總數等於實際數量()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        var root = dir!.FullName;
        var files = new List<string>
        {
            Path.Combine(root, "docs", "DETECTION-SPEC.md"),
            Path.Combine(root, "docs", "RULES-SPEC.md"),
        };
        files.AddRange(Directory.GetFiles(Path.Combine(root, "LogForesight.Web", "HelpContent"), "*.md"));

        var patterns = new[]
        {
            new System.Text.RegularExpressions.Regex(@"(\d+)\s*種(?:攻擊鏈|關聯|組合|.{0,12}模式)"),
            new System.Text.RegularExpressions.Regex(@"合計\s*(\d+)\s*(?:個|種)(?:關聯)?(?:模式|Pattern)"),
            new System.Text.RegularExpressions.Regex(@"跨來源模式，共\s*(\d+)\s*個"),
        };

        var expected = CorrelationPatternIds.All.Length;
        var hits = new List<(string File, int Line, int Number)>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var regex in patterns)
                {
                    foreach (System.Text.RegularExpressions.Match m in regex.Matches(lines[i]))
                    {
                        hits.Add((Path.GetFileName(file), i + 1, int.Parse(m.Groups[1].Value)));
                    }
                }
            }
        }

        Assert.Contains(hits, h => h.File == "DETECTION-SPEC.md");   // 句子被刪掉就騙過守門的話等於沒守
        var wrong = hits.Where(h => h.Number != expected).ToList();
        Assert.True(wrong.Count == 0,
            $"關聯模式實際共 {expected} 種，但文件寫的數字不同：" +
            string.Join("；", wrong.Select(h => $"{h.File}:{h.Line} 寫 {h.Number}")));
    }
}
