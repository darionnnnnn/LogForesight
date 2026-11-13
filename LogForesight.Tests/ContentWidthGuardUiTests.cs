using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 內容區寬度上限的守門（docs/DESIGN-SYSTEM.md §8.4）：只有單欄表單頁（設定、初始設定）限寬，
/// 其餘頁面（含詳情頁）隨視窗滿版。2K 以上的螢幕上，限寬的詳情頁右側會空出一整條。
/// 實際的寬度由 ui-shots 的「版面-詳情頁滿版」情境量測。
/// </summary>
public sealed class ContentWidthGuardUiTests
{
    private static readonly string[] BoundedViews = { "Settings.cshtml", "Setup.cshtml" };

    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return Path.Combine(dir!.FullName, "LogForesight.Web", "Views", "Pages");
    }

    private static bool IsBounded(string file) =>
        Regex.IsMatch(File.ReadAllText(file, Encoding.UTF8), @"ViewData\[""ContentWidth""\]\s*=\s*""bounded""");

    [Fact]
    public void 只有表單頁限寬()
    {
        var bounded = Directory.GetFiles(PagesDir(), "*.cshtml").Where(IsBounded)
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(BoundedViews.OrderBy(n => n, StringComparer.Ordinal), bounded);
    }
}
