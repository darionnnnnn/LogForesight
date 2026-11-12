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
}
