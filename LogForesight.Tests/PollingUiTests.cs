using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 各頁輪詢一律走 core/async-guards.js 的 pollWhile：setInterval 搭配 async 工作會讓上一輪未回時
/// 又疊上新一輪請求，慢網路下在途請求只增不減。
/// </summary>
public class PollingUiTests
{
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

    /// <summary>全部頁面腳本（不設例外）。</summary>
    private static IEnumerable<string> PageFiles() =>
        Directory.GetFiles(Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages"), "*.js");

    [Fact]
    public void 頁面不得以setInterval搭配async函式輪詢()
    {
        var offenders = new List<string>();
        foreach (var file in PageFiles())
        {
            var js = File.ReadAllText(file);
            if (js.Contains("setInterval(async", StringComparison.Ordinal))
            {
                offenders.Add($"{Path.GetFileName(file)}: setInterval(async …)");
            }

            // setInterval(具名函式, …)：具名函式若是 async function 也算
            foreach (Match m in Regex.Matches(js, @"setInterval\(\s*([A-Za-z_$][\w$]*)\s*,"))
            {
                var name = m.Groups[1].Value;
                if (Regex.IsMatch(js, $@"async\s+function\s+{Regex.Escape(name)}\s*\("))
                {
                    offenders.Add($"{Path.GetFileName(file)}: setInterval({name}, …) 其中 {name} 是 async 函式");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "下列頁面以 setInterval 輪詢 async 工作，應改用 pollWhile：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
