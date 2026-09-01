using Xunit;

namespace LogForesight.Tests;

public class PrtgAdminPageUiTests
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

    [Fact]
    public void Prtg維護頁骨架與路由選單配置正確()
    {
        var root = FindRepoRoot();

        // 1. PagesController.cs 含 "/admin/prtg" 與 Prtg()
        var pagesControllerPath = Path.Combine(root, "LogForesight.Web", "Controllers", "PagesController.cs");
        Assert.True(File.Exists(pagesControllerPath), $"找不到檔案: {pagesControllerPath}");
        var pagesControllerContent = File.ReadAllText(pagesControllerPath);
        Assert.Contains("\"/admin/prtg\"", pagesControllerContent);
        Assert.Contains("Prtg()", pagesControllerContent);

        // 2. Views/Pages/Prtg.cshtml 檔案存在且含 prtg-admin.js
        var prtgCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(prtgCshtmlPath), $"找不到檔案: {prtgCshtmlPath}");
        var prtgCshtmlContent = File.ReadAllText(prtgCshtmlPath);
        Assert.Contains("prtg-admin.js", prtgCshtmlContent);

        // 3. layout.js 含 /admin/prtg 且該行含 requires: 'Maintain'
        var layoutJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "core", "layout.js");
        Assert.True(File.Exists(layoutJsPath), $"找不到檔案: {layoutJsPath}");
        var layoutLines = File.ReadAllLines(layoutJsPath);
        var prtgNavLine = Array.Find(layoutLines, l => l.Contains("/admin/prtg"));
        Assert.NotNull(prtgNavLine);
        Assert.Contains("requires: 'Maintain'", prtgNavLine);

        // 4. Prtg.cshtml 含 id="prtg-tabs"
        Assert.Contains("id=\"prtg-tabs\"", prtgCshtmlContent);
    }
}
