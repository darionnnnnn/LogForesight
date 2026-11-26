using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 圖示檔（wwwroot/img/icons.svg）的形狀守門。複製圖示的後方方框曾是一段畫壞的路徑（底邊與左下角零面積），
/// 圖示外框都在按鈕內、量測抓不到，畫面上看起來就是底部被截斷。這裡鎖住 Bootstrap Icons 原版的路徑。
/// </summary>
public class IconSpriteTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    [Fact]
    public void 複製圖示的後方方框是完整的外框()
    {
        var svg = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "img", "icons.svg"));
        var symbol = Regex.Match(svg, "<symbol id=\"copy\"[^>]*>(.*?)</symbol>", RegexOptions.Singleline);
        Assert.True(symbol.Success, "icons.svg 找不到 copy 圖示");
        Assert.Contains("M2 5a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1v-1h1v1a2 2 0 0 1-2 2H2a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h1v1z", symbol.Groups[1].Value);
    }

    // 以 icon: '名稱'、icon('名稱'、icons.svg#名稱 寫死的圖示名；變數傳入的（icon: iconName）不在守門範圍
    private static readonly Regex HardCodedIcon = new(
        "(?:\\bicon\\s*:\\s*|\\bicon\\(\\s*)['\"]([a-z0-9-]+)['\"]|icons\\.svg#([a-z0-9-]+)", RegexOptions.Compiled);

    /// <summary>
    /// 前端寫死的圖示名都要存在於 icons.svg：不存在的 id 在畫面上是空白，沒有任何錯誤訊息
    /// （處理人頁曾有 lock 與 x-lg 兩個）。新增圖示名時要同時補進 icons.svg。
    /// </summary>
    [Fact]
    public void 前端寫死的圖示名都存在於icons_svg()
    {
        var root = FindRepoRoot();
        var symbols = IconSpriteIds.Load(root);
        var missing = new List<string>();
        foreach (var dir in new[] { Path.Combine("wwwroot", "js"), "Views" })
        {
            var pattern = dir == "Views" ? "*.cshtml" : "*.js";
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "LogForesight.Web", dir), pattern, SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in HardCodedIcon.Matches(text))
                {
                    var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (!symbols.Contains(name))
                        missing.Add($"{name}（{Path.GetRelativePath(root, file).Replace('\\', '/')}）");
                }
            }
        }
        Assert.True(missing.Count == 0, "前端引用了 icons.svg 沒有的圖示：" + string.Join("、", missing.Distinct()));
    }
}
