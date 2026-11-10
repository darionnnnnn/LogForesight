using System.Text;

namespace LogForesight.Tests;

/// <summary>
/// 全站樣式表的唯一讀取點（docs/DESIGN-SYSTEM.md §7）：四個檔依 Views/Shared/_Styles.cshtml 的載入順序串接，
/// 守門測試讀到的就是瀏覽器實際套用的順序。新增或改名樣式表時只改 <see cref="Files"/>。
/// </summary>
internal static class SiteCss
{
    public static readonly string[] Files = { "base.css", "components.css", "layout.css", "pages.css" };

    public static string CssDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("找不到 LogForesight.sln，無法定位專案根目錄");
        return Path.Combine(dir.FullName, "LogForesight.Web", "wwwroot", "css");
    }

    /// <summary>四個樣式表依載入順序串接（保留原始換行）。</summary>
    public static string Read() =>
        string.Join("\n", Files.Select(f => File.ReadAllText(Path.Combine(CssDir(), f), Encoding.UTF8)));
}
