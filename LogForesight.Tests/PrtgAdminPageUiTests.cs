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
    public void Runs排程頁的Prtg卡顯示狀態並指路而非自帶開關()
    {
        var root = FindRepoRoot();
        var runsCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        Assert.True(File.Exists(runsCshtmlPath), $"找不到檔案: {runsCshtmlPath}");
        var cshtmlContent = File.ReadAllText(runsCshtmlPath);

        // 啟用開關已併入 PRTG 維護頁「擷取參數」的取數範圍下拉（回饋第 41 輪批次F）——
        // 兩個入口寫同一個值會互相蓋，本頁只顯示狀態並指路
        Assert.DoesNotContain("id=\"prtg-enabled\"", cshtmlContent);
        Assert.Contains("id=\"prtg-disabled-hint\"", cshtmlContent);
        Assert.Contains("admin/prtg", cshtmlContent);

        // 回填與每日擷取、模組狀態、結構同步一起放在 PRTG 狀態卡（回饋第 40 輪批次A）
        Assert.Contains("prtg-status-card", cshtmlContent);
        Assert.Contains("prtg-backfill-start", cshtmlContent);

        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        // 端點連同 SetPrtgEnabled 一起移除，前端不得再呼叫
        Assert.DoesNotContain("/api/admin/settings/prtg-enabled", jsContent);
        // 狀態文字與維護頁的下拉共用同一份標籤，不各寫一份
        Assert.Contains("prtg-scope-labels.js", jsContent);
        Assert.Contains("prtgModuleStateText", jsContent);
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

    [Fact]
    public void Calibration校準頁骨架與路由選單配置正確()
    {
        var root = FindRepoRoot();

        // 1. PagesController.cs 含 "/admin/calibration" 與 Calibration()
        var pagesControllerPath = Path.Combine(root, "LogForesight.Web", "Controllers", "PagesController.cs");
        Assert.True(File.Exists(pagesControllerPath), $"找不到檔案: {pagesControllerPath}");
        var pagesControllerContent = File.ReadAllText(pagesControllerPath);
        Assert.Contains("\"/admin/calibration\"", pagesControllerContent);
        Assert.Contains("Calibration()", pagesControllerContent);

        // 2. Views/Pages/Prtg.cshtml 檔案存在且含 prtg-admin.js
        var prtgCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(prtgCshtmlPath), $"找不到檔案: {prtgCshtmlPath}");
        var prtgCshtmlContent = File.ReadAllText(prtgCshtmlPath);
        Assert.Contains("prtg-admin.js", prtgCshtmlContent);

        // 3. layout.js 不含 /admin/calibration
        var layoutJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "core", "layout.js");
        Assert.True(File.Exists(layoutJsPath), $"找不到檔案: {layoutJsPath}");
        var layoutJsContent = File.ReadAllText(layoutJsPath);
        Assert.DoesNotContain("/admin/calibration", layoutJsContent);

        // 4. Prtg.cshtml 含四張卡的容器 id 與兩顆按鈕的 id 與 override 勾選框
        Assert.Contains("id=\"calibration-card-prtg-value-baseline\"", prtgCshtmlContent);
        Assert.Contains("id=\"calibration-card-prtg-rule-thresholds\"", prtgCshtmlContent);
        Assert.Contains("id=\"calibration-card-triggered-fetch-magnitude\"", prtgCshtmlContent);
        Assert.Contains("id=\"calibration-card-residual-credential-thresholds\"", prtgCshtmlContent);
        Assert.Contains("id=\"calibration-calc-btn\"", prtgCshtmlContent);
        Assert.Contains("id=\"calibration-export-btn\"", prtgCshtmlContent);
        Assert.Contains("id=\"calibration-override-check\"", prtgCshtmlContent);

        // 5. 四張卡都要有「門檻現值」容器：後端 CurrentThresholds 有填、DTO 有傳，
        //    少了這些容器前端就只顯示「目前多少」而看不到「需要多少」——
        //    斷鏈不會有任何編譯或測試訊號，所以在這裡釘住
        Assert.Contains("id=\"thresholds-prtg-value-baseline\"", prtgCshtmlContent);
        Assert.Contains("id=\"thresholds-prtg-rule-thresholds\"", prtgCshtmlContent);
        Assert.Contains("id=\"thresholds-triggered-fetch-magnitude\"", prtgCshtmlContent);
        Assert.Contains("id=\"thresholds-residual-credential-thresholds\"", prtgCshtmlContent);

        // 6. 前端確實消費 currentThresholds／isEligible（後端算好卻沒人讀＝白算）
        var calibrationJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-calibration.js");
        Assert.True(File.Exists(calibrationJsPath), $"找不到檔案: {calibrationJsPath}");
        var calibrationJs = File.ReadAllText(calibrationJsPath);
        Assert.Contains("itemData.currentThresholds", calibrationJs);
        Assert.Contains("item.isEligible", calibrationJs);
    }

    [Fact]
    public void Prtg頁面包含四個頁籤與連線擷取參數面板()
    {
        var root = FindRepoRoot();
        var prtgCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(prtgCshtmlPath), $"找不到檔案: {prtgCshtmlPath}");
        var content = File.ReadAllText(prtgCshtmlPath);

        Assert.Contains("data-tab=\"connection\"", content);
        Assert.Contains("data-tab=\"params\"", content);
        Assert.Contains("data-tab=\"mirror\"", content);
        Assert.Contains("data-tab=\"probe\"", content);
        Assert.Contains("data-panel=\"connection\"", content);
        Assert.Contains("data-panel=\"params\"", content);
    }

    [Fact]
    public void Prtg頁面與腳本不含未對應清單與舊版設定表單Id()
    {
        var root = FindRepoRoot();
        var prtgCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(prtgCshtmlPath), $"找不到檔案: {prtgCshtmlPath}");
        var cshtmlContent = File.ReadAllText(prtgCshtmlPath);

        var unmatchedBody = "prtg-mirror-" + "unmatched-body";
        var legacyForm = "prtg-" + "config-form";
        var legacySave = "prtg-" + "config-save";

        Assert.DoesNotContain(unmatchedBody, cshtmlContent);
        Assert.DoesNotContain($"id=\"{legacyForm}\"", cshtmlContent);
        Assert.DoesNotContain($"id=\"{legacySave}\"", cshtmlContent);

        var prtgAdminJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(prtgAdminJsPath), $"找不到檔案: {prtgAdminJsPath}");
        var jsContent = File.ReadAllText(prtgAdminJsPath);

        Assert.DoesNotContain(unmatchedBody, jsContent);
    }

    [Fact]
    public void PrtgProbeDtos不含Unmatched屬性宣告但仍含MapUnmatched()
    {
        var root = FindRepoRoot();
        var dtoPath = Path.Combine(root, "LogForesight.Web", "Models", "Dto", "PrtgProbeDtos.cs");
        Assert.True(File.Exists(dtoPath), $"找不到檔案: {dtoPath}");
        var content = File.ReadAllText(dtoPath);

        Assert.DoesNotContain(" Unmatched { get", content);
        Assert.Contains("MapUnmatched", content);
    }

    [Fact]
    public void Calibration獨立頁已刪除且轉址與選單移除()
    {
        var root = FindRepoRoot();

        // 斷言 Calibration.cshtml 檔案不存在
        var calibrationCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Calibration.cshtml");
        Assert.False(File.Exists(calibrationCshtmlPath), $"Calibration.cshtml 應該已被刪除: {calibrationCshtmlPath}");

        // layout.js 不含 /admin/calibration
        var layoutJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "core", "layout.js");
        Assert.True(File.Exists(layoutJsPath), $"找不到檔案: {layoutJsPath}");
        var layoutContent = File.ReadAllText(layoutJsPath);
        Assert.DoesNotContain("/admin/calibration", layoutContent);

        // PagesController.cs 仍含 "/admin/calibration"（轉址仍在）且含 RedirectPermanent
        var pagesControllerPath = Path.Combine(root, "LogForesight.Web", "Controllers", "PagesController.cs");
        Assert.True(File.Exists(pagesControllerPath), $"找不到檔案: {pagesControllerPath}");
        var pagesControllerContent = File.ReadAllText(pagesControllerPath);
        Assert.Contains("\"/admin/calibration\"", pagesControllerContent);
        Assert.Contains("RedirectPermanent", pagesControllerContent);
    }

    [Fact]
    public void Prtg環境探測頁籤包含校準與資料搬運卡片()
    {
        var root = FindRepoRoot();
        var prtgCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(prtgCshtmlPath), $"找不到檔案: {prtgCshtmlPath}");
        var content = File.ReadAllText(prtgCshtmlPath);

        // 斷言 Prtg.cshtml 含 calibration-calc-btn、calibration-export-btn、prtg-export-btn、prtg-import-btn 四個 id
        Assert.Contains("calibration-calc-btn", content);
        Assert.Contains("calibration-export-btn", content);
        Assert.Contains("prtg-export-btn", content);
        Assert.Contains("prtg-import-btn", content);

        // 且檔案中「資料搬運（開發用）」這個字串出現在 data-panel="probe" 之後（用 IndexOf 比較位置即可）
        var probePanelIndex = content.IndexOf("data-panel=\"probe\"", StringComparison.Ordinal);
        var dataTransferIndex = content.IndexOf("資料搬運（開發用）", StringComparison.Ordinal);
        Assert.True(probePanelIndex >= 0, "Prtg.cshtml 應包含 data-panel=\"probe\"");
        Assert.True(dataTransferIndex >= 0, "Prtg.cshtml 應包含「資料搬運（開發用）」");
        Assert.True(dataTransferIndex > probePanelIndex, "「資料搬運（開發用）」字串必須出現在 data-panel=\"probe\" 之後");
    }

    [Fact]
    public void PrtgCalibration腳本命名與PrtgAdmin引用及無XRequestedBy()
    {
        var root = FindRepoRoot();

        // 斷言 wwwroot/js/pages/prtg-calibration.js 存在、calibration.js 不存在
        var prtgCalibrationJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-calibration.js");
        var oldCalibrationJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "calibration.js");
        Assert.True(File.Exists(prtgCalibrationJsPath), $"prtg-calibration.js 應該存在: {prtgCalibrationJsPath}");
        Assert.False(File.Exists(oldCalibrationJsPath), $"舊 calibration.js 不應該存在: {oldCalibrationJsPath}");

        // prtg-admin.js 含 prtg-calibration.js；prtg-admin.js 不含 X-Requested-By
        var prtgAdminJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(prtgAdminJsPath), $"找不到檔案: {prtgAdminJsPath}");
        var prtgAdminJsContent = File.ReadAllText(prtgAdminJsPath);
        Assert.Contains("prtg-calibration.js", prtgAdminJsContent);
        Assert.DoesNotContain("X-Requested-By", prtgAdminJsContent);
    }

    [Fact]
    public void BindTabs簽章支援選用Hash且不監聽HashChange()
    {
        var root = FindRepoRoot();
        var uiJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "core", "ui.js");
        Assert.True(File.Exists(uiJsPath), $"找不到檔案: {uiJsPath}");
        var uiJs = File.ReadAllText(uiJsPath);

        // 斷言 core/ui.js 的 bindTabs 簽章含 hash = false（預設關）
        Assert.Contains("export function bindTabs(tabsEl, { onChange, hash = false } = {})", uiJs);
        // 且該函式內含 history.replaceState
        Assert.Contains("history.replaceState", uiJs);
        // 不含 addEventListener('hashchange'
        Assert.DoesNotContain("addEventListener('hashchange'", uiJs);
        Assert.DoesNotContain("addEventListener(\"hashchange\"", uiJs);
    }

    [Fact]
    public void PrtgAdmin啟用Hash且跨頁指路連結直達Params頁籤()
    {
        var root = FindRepoRoot();
        var prtgAdminJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(prtgAdminJsPath), $"找不到檔案: {prtgAdminJsPath}");
        var prtgAdminJs = File.ReadAllText(prtgAdminJsPath);
        Assert.Contains("hash: true", prtgAdminJs);

        var runsCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        Assert.True(File.Exists(runsCshtmlPath), $"找不到檔案: {runsCshtmlPath}");
        var runsCshtml = File.ReadAllText(runsCshtmlPath);
        Assert.Contains("~/admin/prtg\")#params", runsCshtml);

        var settingsCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Settings.cshtml");
        Assert.True(File.Exists(settingsCshtmlPath), $"找不到檔案: {settingsCshtmlPath}");
        var settingsCshtml = File.ReadAllText(settingsCshtmlPath);
        Assert.Contains("~/admin/prtg\")#params", settingsCshtml);
    }

    [Fact]
    public void 僅PrtgAdmin啟用Hash其餘頁面維持不變()
    {
        var root = FindRepoRoot();
        var pagesDir = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages");
        Assert.True(Directory.Exists(pagesDir), $"找不到目錄: {pagesDir}");

        var jsFiles = Directory.GetFiles(pagesDir, "*.js", SearchOption.TopDirectoryOnly);
        var filesWithHashTrue = new List<string>();

        foreach (var file in jsFiles)
        {
            var content = File.ReadAllText(file);
            if (content.Contains("bindTabs(") && content.Contains("hash: true"))
            {
                filesWithHashTrue.Add(Path.GetFileName(file));
            }
        }

        Assert.Single(filesWithHashTrue);
        Assert.Equal("prtg-admin.js", filesWithHashTrue[0]);
    }

    [Fact]
    public void Prtg頁面含衝突分頁與排除清單元素且移除舊的Objid欄位()
    {
        var root = FindRepoRoot();
        var prtgCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(prtgCshtmlPath), $"找不到檔案: {prtgCshtmlPath}");
        var content = File.ReadAllText(prtgCshtmlPath);

        Assert.Contains("prtg-conflicts-pagination", content);
        Assert.Contains("prtg-ip-excludes-body", content);
        Assert.Contains("prtg-assign-device-choice", content);
        Assert.Contains("prtg-assign-host-fixed", content);
        Assert.Contains("prtg-mirror-ip-exclude-count", content);

        // Objid 改在 modal 標題顯示，原本的唯讀輸入欄已移除
        Assert.DoesNotContain("prtg-assign-device-" + "objid", content);
    }

    [Fact]
    public void PrtgAdmin腳本接上衝突分頁與不分頁主機清單端點()
    {
        var root = FindRepoRoot();
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(jsPath), $"找不到檔案: {jsPath}");
        var js = File.ReadAllText(jsPath);

        Assert.Contains("renderPagination", js);
        Assert.Contains("prtg-host-map?status=conflict", js);
        Assert.Contains("/api/admin/hosts/all", js);
        Assert.Contains("multi-device", js);
        Assert.Contains("multi-host", js);

        // 重算警告必須被顯示出來，不能靜默丟掉
        Assert.Contains("remapWarning", js);

        // 舊的取全部主機寫法會被後端分頁夾成 200 台，不得殘留
        Assert.DoesNotContain("pageSize=" + "2000", js);
    }

    [Fact]
    public void PrtgAdmin的confirmAction一律以物件參數呼叫()
    {
        var root = FindRepoRoot();
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(jsPath), $"找不到檔案: {jsPath}");
        var js = File.ReadAllText(jsPath);

        // core/ui.js 的 confirmAction 簽章是物件參數且回傳 Promise。誤用成「字串 + callback」時
        // 會跳出空白確認框，而且 callback 內的刪除請求永遠不會送出，沒有任何編譯或執行期錯誤。
        const string token = "confirmAction(";
        var index = js.IndexOf(token, StringComparison.Ordinal);
        var occurrences = 0;

        while (index >= 0)
        {
            var cursor = index + token.Length;
            while (cursor < js.Length && char.IsWhiteSpace(js[cursor])) cursor++;

            Assert.True(cursor < js.Length, $"confirmAction( 出現在位置 {index} 之後沒有任何內容");
            Assert.True(js[cursor] == '{',
                $"位置 {index} 的 confirmAction 呼叫第一個參數是 '{js[cursor]}' 而非物件");

            occurrences++;
            index = js.IndexOf(token, cursor, StringComparison.Ordinal);
        }

        Assert.True(occurrences >= 3, $"預期至少 3 處 confirmAction 呼叫，實際 {occurrences} 處");
    }

    [Fact]
    public void 前端腳本包含排除與略過台數之屬性()
    {
        var root = FindRepoRoot();
        var hostDetailJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "host-detail.js");
        Assert.True(File.Exists(hostDetailJsPath), $"找不到檔案: {hostDetailJsPath}");
        var hostDetailJs = File.ReadAllText(hostDetailJsPath);
        Assert.Contains("ipExcluded", hostDetailJs);
        Assert.Contains("excludedIp", hostDetailJs);

        var prtgAdminJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(prtgAdminJsPath), $"找不到檔案: {prtgAdminJsPath}");
        var prtgAdminJs = File.ReadAllText(prtgAdminJsPath);
        Assert.Contains("sameIpSkippedCount", prtgAdminJs);
    }

    [Fact]
    public void 設定頁包含資源守門所有必要元素Id()
    {
        // 守門搬到設定頁（回饋第 40 輪批次F）：它節制 NetIQ 與 PRTG 兩路，不只是 PRTG 的事
        var root = FindRepoRoot();
        var settingsCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Settings.cshtml");
        Assert.True(File.Exists(settingsCshtmlPath), $"找不到檔案: {settingsCshtmlPath}");
        var content = File.ReadAllText(settingsCshtmlPath);

        Assert.Contains("prtg-guard-enabled", content);
        Assert.Contains("prtg-guard-cpu-percent", content);
        Assert.Contains("prtg-guard-memory-free-percent", content);
        Assert.Contains("prtg-guard-check-seconds", content);
        Assert.Contains("prtg-guard-pause-minutes", content);
        Assert.Contains("prtg-guard-strikes", content);
        Assert.Contains("prtg-guard-max-pause-minutes", content);
        Assert.Contains("prtg-guard-sensor-objids", content);
        Assert.Contains("prtg-guard-preview-btn", content);
        Assert.Contains("prtg-guard-preview-result", content);
    }

    [Fact]
    public void 設定頁記憶體欄位標籤包含可用二字()
    {
        // CPU 越高越糟、記憶體越低越糟，方向相反最容易寫反——標籤必須明說是「可用」
        var root = FindRepoRoot();
        var settingsCshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Settings.cshtml");
        Assert.True(File.Exists(settingsCshtmlPath), $"找不到檔案: {settingsCshtmlPath}");
        var lines = File.ReadAllLines(settingsCshtmlPath);

        var memoryLabelLine = Array.Find(lines, l => l.Contains("prtg-guard-memory-free-percent") && l.Contains("<label"));
        Assert.NotNull(memoryLabelLine);
        Assert.Contains("可用", memoryLabelLine);
    }

    [Fact]
    public void 守門模組包含資源守門預覽端點路徑()
    {
        var root = FindRepoRoot();
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-guard.js");
        Assert.True(File.Exists(jsPath), $"找不到檔案: {jsPath}");
        var js = File.ReadAllText(jsPath);

        Assert.Contains("prtg-resource-guard/preview", js);

        // 後端的 source 四個值前端都要有文案，否則使用者看不出資料是查 PRTG 還是讀鏡像
        // （批次D 新增 live／mirror-fallback 兩個值）
        Assert.Contains("override", js);
        Assert.Contains("live", js);
        Assert.Contains("mirror-fallback", js);
        Assert.Contains("auto", js);
    }

    /// <summary>
    /// 資源守門與擷取參數必須同一顆儲存鈕：分兩顆時按其中一顆，另一張卡未存的改動會在
    /// loadSettings() 重載時被覆蓋回舊值且沒有提示。同檔曾出現兩份逐字重複的守門函式
    /// （後者覆蓋前者、無任何訊號），一併釘住。
    /// </summary>
    [Fact]
    public void 資源守門與設定頁其他欄位共用儲存鈕且無重複函式()
    {
        // 守門不得有自己的儲存鈕：分兩顆時按其中一顆，另一區未存的改動會在重載時
        // 被覆蓋回舊值且沒有提示。同檔曾出現兩份逐字重複的守門函式（後者覆蓋前者、
        // 無任何訊號），一併釘住。
        var root = FindRepoRoot();
        var settingsCshtml = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Settings.cshtml"));
        var guardJs = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-guard.js"));
        var settingsJs = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "settings.js"));

        Assert.DoesNotContain("prtg-guard-" + "form", settingsCshtml);
        Assert.DoesNotContain("prtg-guard-" + "save", settingsCshtml);
        Assert.DoesNotContain("function bind" + "GuardForm", guardJs);
        Assert.Contains("prtgResourceGuardSensorObjids: collectLines('prtg-guard-sensor-objids')", guardJs);
        // 守門欄位要真的併進整包儲存，否則設定永遠存不進去
        Assert.Contains("collectGuardPayload()", settingsJs);

        foreach (var (label, js) in new[] { ("prtg-guard.js", guardJs), ("settings.js", settingsJs) })
        {
            var names = System.Text.RegularExpressions.Regex.Matches(js, @"^(?:export )?(?:async )?function (\w+)\(", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value).ToList();
            var dup = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dup.Count == 0, $"{label} 重複定義的頂層函式：" + string.Join(", ", dup));
        }
    }
    /// <summary>批次D：守門「自動偵測並填入」按鈕與取數範圍欄位的接線。</summary>
    [Fact]
    public void 守門自動偵測與取數範圍的元素與接線齊備()
    {
        var root = FindRepoRoot();
        var cshtml = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml"));
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));
        var settingsCshtml = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Settings.cshtml"));
        var guardJs = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-guard.js"));

        // 守門的自動偵測鈕在設定頁（批次F 搬遷後）
        Assert.Contains("id=\"prtg-guard-autofill-btn\"", settingsCshtml);

        // 取數範圍仍在 PRTG 維護頁
        Assert.Contains("id=\"prtg-value-fetch-scope\"", cshtml);
        Assert.Contains("id=\"prtg-value-fetch-extra-hosts\"", cshtml);
        Assert.Contains("id=\"prtg-scope-estimate-btn\"", cshtml);

        // 「關閉」與三個模式併成同一個下拉（回饋第 41 輪批次F）：選任一範圍即啟用
        Assert.Contains("value=\"off\"", cshtml);
        Assert.Contains("value=\"triggered\"", cshtml);
        // 「關閉」必須是第一個選項：它是預設值，排在後面會讓沒存過設定的站台看起來像已啟用
        var scopeSelectStart = cshtml.IndexOf("id=\"prtg-value-fetch-scope\"", StringComparison.Ordinal);
        Assert.True(scopeSelectStart >= 0, "找不到取數範圍下拉");
        var firstOption = cshtml.IndexOf("<option", scopeSelectStart, StringComparison.Ordinal);
        Assert.Contains("value=\"off\"", cshtml.Substring(firstOption, 40));
        Assert.Contains("value=\"all-mapped\"", cshtml);
        Assert.Contains("value=\"triggered-plus-list\"", cshtml);
        // 啟用旗標要跟著存，否則選了範圍也不會啟用
        Assert.Contains("prtgEnabled:", js);
        Assert.Contains("prtg-scope-labels.js", js);

        // 自動偵測必須帶 forceAuto，否則覆寫清單非空時只會把手填值原樣吐回來
        Assert.Contains("forceAuto=true", guardJs);

        // 新設定要真的進儲存 payload（後端有欄位、前端沒送＝設定永遠存不進去）
        Assert.Contains("prtgValueFetchScope:", js);
        Assert.Contains("prtgValueFetchExtraHosts:", js);

        // 估算端點的呼叫
        Assert.Contains("prtg-fetch-scope/estimate", js);
    }

    /// <summary>
    /// 「同步結構與對應」的入口在鏡像狀態頁籤（docs/PRTG-SPEC.md §5a）：
    /// 按鈕、狀態列與前端綁定三者缺一，畫面上就會出現按不動或不會更新的控制項。
    /// </summary>
    [Fact]
    public void 同步結構與對應的入口與前端綁定齊備()
    {
        var root = FindRepoRoot();

        var cshtml = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml"));
        Assert.Contains("prtg-structure-sync-btn", cshtml);
        Assert.Contains("prtg-structure-sync-status", cshtml);
        Assert.Contains("prtg-structure-sync-progress", cshtml);

        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));
        Assert.Contains("prtg-structure-sync/start", js);
        Assert.Contains("prtg-structure-sync/status", js);
        Assert.Contains("bindStructureSync()", js);
        Assert.Contains("refreshStructureSyncStatus()", js);

        // 「尚未同步」與「同步到 0 筆」必須是不同文案——兩者混在一起會讓人以為同步過了
        Assert.Contains("尚未同步", js);
    }

    /// <summary>
    /// 資源守門搬到設定頁（回饋第 40 輪批次F）：它同時節制 NetIQ 取數與 PRTG 擷取兩路，
    /// 不只是 PRTG 的事。維護頁只留指路，欄位與兩顆偵測鈕都在設定頁的資源守門頁籤。
    /// 欄位定義只有一份（prtg-guard.js），不在兩個頁面各寫一遍。
    /// </summary>
    [Fact]
    public void 資源守門在設定頁而非PRTG維護頁()
    {
        var root = FindRepoRoot();

        var settingsCshtml = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Settings.cshtml"));
        Assert.Contains("data-tab=\"guard\"", settingsCshtml);
        Assert.Contains("data-panel=\"guard\"", settingsCshtml);
        Assert.Contains("prtg-guard-enabled", settingsCshtml);
        Assert.Contains("prtg-guard-cpu-percent", settingsCshtml);
        Assert.Contains("prtg-guard-memory-free-percent", settingsCshtml);
        Assert.Contains("prtg-guard-sensor-objids", settingsCshtml);
        Assert.Contains("prtg-guard-preview-btn", settingsCshtml);
        Assert.Contains("prtg-guard-autofill-btn", settingsCshtml);
        // 方向相反最容易寫反，標籤要明說是「可用記憶體」
        Assert.Contains("可用", settingsCshtml);

        // 維護頁只剩指路，不再有任何守門欄位
        var prtgCshtml = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml"));
        Assert.DoesNotContain("prtg-guard-enabled", prtgCshtml);
        Assert.DoesNotContain("prtg-guard-cpu-percent", prtgCshtml);
        Assert.DoesNotContain("prtg-guard-preview-btn", prtgCshtml);
        Assert.Contains("資源守門", prtgCshtml);   // 指路那段
        Assert.Contains("admin/settings", prtgCshtml);

        // 欄位的載入／收集／預覽只有一份實作
        var guardJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-guard.js");
        Assert.True(File.Exists(guardJsPath), "資源守門的前端模組不存在");
        var guardJs = File.ReadAllText(guardJsPath);
        Assert.Contains("export function loadGuardFields", guardJs);
        Assert.Contains("export function collectGuardPayload", guardJs);
        Assert.Contains("export function bindGuardPreview", guardJs);

        var prtgAdminJs = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js"));
        Assert.DoesNotContain("prtg-guard", prtgAdminJs);

        var settingsJs = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "settings.js"));
        Assert.Contains("prtg-guard.js", settingsJs);
        Assert.Contains("collectGuardPayload()", settingsJs);
        Assert.Contains("loadGuardFields(current)", settingsJs);
        Assert.Contains("bindGuardPreview()", settingsJs);
    }
}
