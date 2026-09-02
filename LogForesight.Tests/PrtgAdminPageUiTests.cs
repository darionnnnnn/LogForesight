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

    [Fact]
    public void SettingsCshtml不再包含已搬走元素Id()
    {
        var root = FindRepoRoot();
        var settingsCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Settings.cshtml");
        Assert.True(File.Exists(settingsCshtmlPath), $"找不到檔案: {settingsCshtmlPath}");
        var content = File.ReadAllText(settingsCshtmlPath);

        Assert.DoesNotContain("prtg-url", content);
        Assert.DoesNotContain("prtg-auth-mode", content);
        Assert.DoesNotContain("prtg-test-btn", content);
        Assert.DoesNotContain("prtg-mirror-section", content);
        Assert.DoesNotContain("prtg-backfill-section", content);
        Assert.DoesNotContain("prtg-probe", content);
        Assert.DoesNotContain("prtg-sensor-type-whitelist", content);
        Assert.DoesNotContain("prtg-fetch-concurrency", content);
        Assert.DoesNotContain("prtg-backfill-days", content);
        Assert.DoesNotContain("prtg-retention-days", content);
        Assert.DoesNotContain("prtg-timeout-seconds", content);
        Assert.DoesNotContain("prtg-ignore-ssl", content);
    }

    [Fact]
    public void PrtgCshtml包含擷取參數與連線元素Id()
    {
        var root = FindRepoRoot();
        var prtgCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(prtgCshtmlPath), $"找不到檔案: {prtgCshtmlPath}");
        var content = File.ReadAllText(prtgCshtmlPath);

        Assert.Contains("prtg-sensor-type-whitelist", content);
        Assert.Contains("prtg-fetch-concurrency", content);
        Assert.Contains("prtg-backfill-days", content);
        Assert.Contains("prtg-retention-days", content);
        Assert.Contains("prtg-timeout-seconds", content);
        Assert.Contains("prtg-ignore-ssl", content);
    }

    [Fact]
    public void SettingsJs不再包含已搬走欄位Payload鍵名()
    {
        var root = FindRepoRoot();
        var settingsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "settings.js");
        Assert.True(File.Exists(settingsJsPath), $"找不到檔案: {settingsJsPath}");
        var content = File.ReadAllText(settingsJsPath);

        Assert.DoesNotContain("prtgEnabled", content);
        Assert.DoesNotContain("prtgUrl", content);
        Assert.DoesNotContain("prtgApiToken", content);
        Assert.DoesNotContain("prtgClearToken", content);
        Assert.DoesNotContain("prtgRetentionDays", content);
        Assert.DoesNotContain("prtgFetchConcurrency", content);
        Assert.DoesNotContain("prtgBackfillDays", content);
        Assert.DoesNotContain("prtgTimeoutSeconds", content);
        Assert.DoesNotContain("prtgIgnoreSslErrors", content);
        Assert.DoesNotContain("prtgSensorTypeWhitelist", content);
    }

    [Fact]
    public void Prtg維護頁包含鏡像狀態與環境探測()
    {
        var root = FindRepoRoot();
        var prtgCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(prtgCshtmlPath), $"找不到檔案: {prtgCshtmlPath}");
        var cshtmlContent = File.ReadAllText(prtgCshtmlPath);

        Assert.Contains("prtg-mirror-section", cshtmlContent);
        Assert.Contains("prtg-probe", cshtmlContent);

        var prtgAdminJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(prtgAdminJsPath), $"找不到檔案: {prtgAdminJsPath}");
        var jsContent = File.ReadAllText(prtgAdminJsPath);

        Assert.Contains("prtg-mirror", jsContent);
        Assert.Contains("prtg-probe/status", jsContent);
    }

    [Fact]
    public void Runs排程頁包含Prtg開關與歷史回填()
    {
        var root = FindRepoRoot();
        var runsCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        Assert.True(File.Exists(runsCshtmlPath), $"找不到檔案: {runsCshtmlPath}");
        var cshtmlContent = File.ReadAllText(runsCshtmlPath);

        Assert.Contains("id=\"prtg-enabled\"", cshtmlContent);
        Assert.Contains("prtg-backfill-section", cshtmlContent);

        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        Assert.Contains("/api/admin/settings/prtg-enabled", jsContent);
        Assert.DoesNotContain("prtgEnabled:", jsContent);
    }

    [Fact]
    public void Hosts主機清單頁包含Prtg篩選Chip與邏輯()
    {
        var root = FindRepoRoot();
        var hostsCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Hosts.cshtml");
        Assert.True(File.Exists(hostsCshtmlPath), $"找不到檔案: {hostsCshtmlPath}");
        var cshtmlContent = File.ReadAllText(hostsCshtmlPath);
        Assert.Contains("host-prtg-chips", cshtmlContent);

        var hostsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "hosts.js");
        Assert.True(File.Exists(hostsJsPath), $"找不到檔案: {hostsJsPath}");
        var jsContent = File.ReadAllText(hostsJsPath);
        Assert.Contains("prtgMap", jsContent);
    }

    [Fact]
    public void HostDetail與PrtgAdmin頁包含Prtg區塊與人工對應端點呼叫()
    {
        var root = FindRepoRoot();
        var hostDetailCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "HostDetail.cshtml");
        Assert.True(File.Exists(hostDetailCshtmlPath), $"找不到檔案: {hostDetailCshtmlPath}");
        var hostDetailCshtmlContent = File.ReadAllText(hostDetailCshtmlPath);
        Assert.Contains("host-prtg", hostDetailCshtmlContent);

        var hostDetailJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "host-detail.js");
        Assert.True(File.Exists(hostDetailJsPath), $"找不到檔案: {hostDetailJsPath}");
        var hostDetailJsContent = File.ReadAllText(hostDetailJsPath);
        Assert.Contains("/prtg", hostDetailJsContent);

        var prtgAdminJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(prtgAdminJsPath), $"找不到檔案: {prtgAdminJsPath}");
        var prtgAdminJsContent = File.ReadAllText(prtgAdminJsPath);
        Assert.Contains("prtg-manual-map", prtgAdminJsContent);
    }
}
