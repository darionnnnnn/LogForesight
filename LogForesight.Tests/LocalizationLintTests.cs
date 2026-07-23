using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 防止簡體字混進 Web 介面文字（docs 需求：台灣繁體中文＋資訊業慣用詞）。
/// 掃 Views 的 .cshtml 與 wwwroot/js 的 .js——這些是使用者看得到的文字來源。
/// AI 動態產出（history.txt）不在此列，那是資料不是原始碼，用詞由 prompt 規範（PromptGuidelines）。
///
/// 字集只收「繁簡字形不同、不可能出現在正確繁體中文裡」的高信心簡體字，避免誤判繁簡同形字。
/// </summary>
public class LocalizationLintTests
{
    // 高信心簡體字（各自的繁體寫法不同，正確繁體文字不會用到這些字形）
    private const string SimplifiedChars =
        "网络软盘数据认户务显删执录设备终权风险规则页节线关过这时说现见观门问间" +
        "单双变书车标导统计让证识语讯论测级检处应资编缓缩属击图题样别类脑两协华觉齐";

    [Fact]
    public void Web介面文字不得含簡體字()
    {
        var webRoot = Path.Combine(FindRepoRoot(), "LogForesight.Web");
        var charset = SimplifiedChars.ToHashSet();

        var files = Directory.EnumerateFiles(Path.Combine(webRoot, "Views"), "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(webRoot, "wwwroot", "js"), "*.js", SearchOption.AllDirectories));

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var bad = lines[i].Where(charset.Contains).Distinct().ToArray();
                if (bad.Length > 0)
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} 含簡體字 [{new string(bad)}]：{lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "發現簡體字，請改用台灣繁體：\n" + string.Join("\n", offenders));
    }

    /// <summary>從測試組件輸出目錄往上找到含 LogForesight.sln 的專案根目錄</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }
}
