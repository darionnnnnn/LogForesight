using Xunit;

namespace LogForesight.Tests;

public class RunsPageUiTests
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
    public void RunsJs包含三軌完成旗標與文案()
    {
        var root = FindRepoRoot();
        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        Assert.Contains("localCompleted", jsContent);
        Assert.Contains("netiqCompleted", jsContent);
        Assert.Contains("prtgCompleted", jsContent);
        Assert.Contains("已取 ", jsContent);
        Assert.Contains("已完成 ", jsContent);
    }

    [Fact]
    public void RunsCshtml狀態文字預設包含載入中()
    {
        var root = FindRepoRoot();
        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        Assert.True(File.Exists(cshtmlPath), $"找不到檔案: {cshtmlPath}");
        var lines = File.ReadAllLines(cshtmlPath);

        // 狀態文字初值必須是「載入中…」而非空白或「閒置」——狀態查詢與 options／ai-status
        // 同時發出、不等彼此，初值寫成閒置的話進站會先看到「沒有執行中」，過一下才跳出執行中。
        // 取數與 AI 兩張狀態卡各一個。
        var runStateLine = Array.Find(lines, l => l.Contains("id=\"schedule-run-state\""));
        Assert.NotNull(runStateLine);
        Assert.Contains("載入中", runStateLine);

        var aiStateLine = Array.Find(lines, l => l.Contains("id=\"schedule-ai-run-state\""));
        Assert.NotNull(aiStateLine);
        Assert.Contains("載入中", aiStateLine);
    }

    [Fact]
    public void UpdateProgressBar函式本體內不含特定Phase字串()
    {
        var root = FindRepoRoot();
        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        const string startToken = "function updateProgressBar(";
        const string endToken = "function renderScheduleProgress(";
        var startIndex = jsContent.IndexOf(startToken, StringComparison.Ordinal);
        var endIndex = jsContent.IndexOf(endToken, StringComparison.Ordinal);

        Assert.True(startIndex >= 0, "找不到 function updateProgressBar(");
        Assert.True(endIndex > startIndex, "找不到 function renderScheduleProgress(");

        var updateProgressBarBody = jsContent.Substring(startIndex, endIndex - startIndex);
        Assert.DoesNotContain("prtg-triggered", updateProgressBarBody);
    }

    [Fact]
    public void RunsJs包含三路成果欄位與四種徽章文字()
    {
        var root = FindRepoRoot();
        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        // 欄位名稱
        Assert.Contains("localDaysAnalyzed", jsContent);
        Assert.Contains("netiqDaysAnalyzed", jsContent);
        Assert.Contains("prtgOutcome", jsContent);

        // 四種徽章文字
        Assert.Contains("未啟用", jsContent);
        Assert.Contains("成功", jsContent);
        Assert.Contains("部分失敗", jsContent);
        Assert.Contains("失敗", jsContent);
    }

    /// <summary>
    /// AI 分析沒有獨立的啟用開關（回饋第 40 輪批次E）：服務設定好就一律啟用，
    /// 取數產出結果後立刻跟上。畫面因此不該再出現「排程未啟用」那套字眼與勾選框，
    /// 閒置說明改由閒置原因對照表承擔。
    /// </summary>
    [Fact]
    public void AI分析沒有啟用開關且閒置說明仍在()
    {
        var root = FindRepoRoot();
        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        var jsContent = File.ReadAllText(runsJsPath);

        // 閒置說明的容器與對照表仍在——那是「為什麼現在沒在跑」的唯一出口
        Assert.Contains("ai-schedule-disabled-hint", jsContent);
        Assert.Contains("AI_IDLE_REASON_TEXT", jsContent);

        // 舊的啟用開關與其文案必須整組消失，否則畫面會出現按不動或說謊的控制項
        Assert.DoesNotContain("AI 分析排程未啟用", jsContent);
        Assert.DoesNotContain("schedule-ai-enabled", jsContent);
        Assert.DoesNotContain("aiEnabled", jsContent);

        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        var cshtmlContent = File.ReadAllText(cshtmlPath);

        Assert.Contains("ai-schedule-disabled-hint", cshtmlContent);
        Assert.DoesNotContain("schedule-ai-enabled", cshtmlContent);
    }

    [Fact]
    public void RunsJs包含pausedReason且RunsCshtml包含暫停徽章容器()
    {
        var root = FindRepoRoot();
        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(runsJsPath), $"找不到檔案: {runsJsPath}");
        var jsContent = File.ReadAllText(runsJsPath);

        Assert.Contains("pausedReason", jsContent);

        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        Assert.True(File.Exists(cshtmlPath), $"找不到檔案: {cshtmlPath}");
        var cshtmlContent = File.ReadAllText(cshtmlPath);

        Assert.Contains("schedule-paused-badge", cshtmlContent);
    }

    /// <summary>
    /// 異常彙總那一欄的資料來源是 BatchRun.HostName＝**跑批次的站台**，不是被分析的主機。
    /// 標成「影響主機」會讓管理者把站台名誤讀成出問題的主機——本輪回饋 2.5
    /// 「本機執行一直有錯誤」的誤判成因之一就是這個標題。這條釘住它不被改回去。
    /// </summary>
    [Fact]
    public void 異常彙總欄位標題為執行站台而非影響主機()
    {
        var root = FindRepoRoot();
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(jsPath), $"找不到檔案: {jsPath}");
        var js = File.ReadAllText(jsPath);

        Assert.Contains("title: '執行站台', sortKey: 'affectedHosts'", js);
        Assert.DoesNotContain("title: '影響主機'", js);
    }
    /// <summary>
    /// 批次G1：phase 字面值集中在 RunPhases 之後，前端標籤表的完整性由這條測試守住。
    /// 過去 Core／Web／JS 三層各寫裸字串，新增一個 phase 只要漏改前端，
    /// 畫面就直接把裸 phase 字串印給使用者——沒有任何訊號會提醒你。
    /// </summary>
    [Fact]
    public void 每個進度phase在前端都有標籤與單位()
    {
        var root = FindRepoRoot();
        // 對照表在 core/run-phases.js（排程作業頁與 PRTG 維護頁共用同一份）
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "core", "run-phases.js"));

        var labelTable = ExtractObjectLiteral(js, "PROGRESS_PHASE_LABEL");
        var unitTable = ExtractObjectLiteral(js, "PROGRESS_PHASE_UNIT");

        foreach (var phase in LogForesight.Core.Service.RunPhases.ProgressTracks)
        {
            // 比對**鍵本身**而非子字串：`Contains("prtg-sync")` 在對照表只有
            // 'prtg-sync-devices' 時仍為真，那正好繞過這條測試要擋的漏改。
            Assert.True(ContainsKey(labelTable, phase),
                $"phase「{phase}」在 core/run-phases.js 的 PROGRESS_PHASE_LABEL 沒有對應文案，畫面會印出裸字串");

            // 單位的 fallback 是「主機日」，對本機／NetIQ 正確、對 PRTG 是錯的
            // （PRTG 的粒度是 sensor／device／筆），所以只對 PRTG 類要求。
            if (phase.StartsWith("prtg-", StringComparison.Ordinal))
            {
                Assert.True(ContainsKey(unitTable, phase),
                    $"phase「{phase}」在 core/run-phases.js 的 PROGRESS_PHASE_UNIT 沒有對應單位，會 fallback 成錯誤的「主機日」");
            }
        }
    }

    /// <summary>批次G1：訊號類 phase 不是進度，不得混進進度軌清單。</summary>
    [Fact]
    public void 訊號類phase不列為進度軌()
    {
        var tracks = LogForesight.Core.Service.RunPhases.ProgressTracks;

        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.LocalDone, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.NetiqDone, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.PrtgDone, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.GuardPaused, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.GuardResumed, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.PrtgFindingsReady, tracks);
        Assert.DoesNotContain(LogForesight.Core.Service.RunPhases.PrtgDateRange, tracks);

        // 但它們都要在 All 裡（All 是「全部字面值」的單一清單）
        foreach (var phase in tracks)
        {
            Assert.Contains(phase, LogForesight.Core.Service.RunPhases.All);
        }
    }

    /// <summary>
    /// 物件字面值裡有沒有這個**鍵**。JS 的鍵可能加引號也可能不加（`local: '…'` 與
    /// `'prtg-sync': '…'` 兩種寫法本檔都有），兩種都要認。
    /// </summary>
    private static bool ContainsKey(string objectLiteral, string key) =>
        objectLiteral.Contains($"'{key}':", StringComparison.Ordinal)
        || objectLiteral.Contains($"\"{key}\":", StringComparison.Ordinal)
        || System.Text.RegularExpressions.Regex.IsMatch(
            objectLiteral, $@"(^|[,{{\s]){System.Text.RegularExpressions.Regex.Escape(key)}\s*:");

    /// <summary>取出 `const NAME = { ... };` 的物件字面值內容。</summary>
    private static string ExtractObjectLiteral(string js, string name)
    {
        var start = js.IndexOf($"const {name} = {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"core/run-phases.js 找不到 {name}");

        var open = js.IndexOf('{', start);
        var close = js.IndexOf("};", open, StringComparison.Ordinal);
        Assert.True(close > open, $"{name} 的物件字面值沒有正確結束");

        return js[open..close];
    }
    /// <summary>
    /// 批次F：頁籤置頂、四個面板同層手足、三張狀態卡平行。
    /// **`#runs-tabs` 與 `[data-panel]` 必須是同一層手足**——bindTabs 用 tabsEl.parentElement
    /// 找面板，中間插入包裝層會讓面板在容器外找不到（瀏覽器實測踩過的坑）。
    /// </summary>
    [Fact]
    public void Runs頁面骨架為頁籤置頂與平行狀態卡()
    {
        var root = FindRepoRoot();
        var cshtml = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml"));

        // 四個面板（多了「排程設定」）
        foreach (var panel in new[] { "summary", "errors", "list", "settings" })
        {
            Assert.Contains($"data-panel=\"{panel}\"", cshtml);
        }
        Assert.Contains("data-tab=\"settings\"", cshtml);

        // 三張平行狀態卡
        Assert.Contains("lf-run-status-grid", cshtml);
        Assert.Contains("id=\"schedule-run-card\"", cshtml);
        Assert.Contains("id=\"schedule-ai-card\"", cshtml);
        Assert.Contains("id=\"prtg-status-card\"", cshtml);

        // 卡中卡與 inline style 都不該再出現
        Assert.DoesNotContain("style=\"background: var(--lf-gray-50);\"", cshtml);
        Assert.DoesNotContain("style=\"height: 6px;\"", cshtml);
        Assert.DoesNotContain("style=\"max-width: 120px;\"", cshtml);

        // 回填輸出預設收合（它是全頁最高的單一元素）
        // 收合容器要真的掛上 collapse 行為，不是頁面任何一處出現 collapse 就算
        Assert.Contains("class=\"collapse mt-2\" id=\"prtg-backfill-output-wrap\"", cshtml);
        Assert.Contains("data-bs-target=\"#prtg-backfill-output-wrap\"", cshtml);

        // 頁籤與面板同層：頁籤結束標籤之後、第一個 data-panel 之前不得出現新的容器 div
        var tabsEnd = cshtml.IndexOf("</ul>", cshtml.IndexOf("id=\"runs-tabs\"", StringComparison.Ordinal), StringComparison.Ordinal);
        var firstPanel = cshtml.IndexOf("data-panel=\"summary\"", StringComparison.Ordinal);
        Assert.True(tabsEnd > 0 && firstPanel > tabsEnd);
    }

    /// <summary>批次F：新版狀態卡用到的樣式都要在 site.css 定義，否則版面直接散掉。</summary>
    [Fact]
    public void Runs頁面樣式在site中已定義()
    {
        var root = FindRepoRoot();
        var css = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "css", "site.css"));

        foreach (var cls in new[] { ".lf-run-status-grid", ".lf-kv", ".lf-run-progress", ".lf-run-tracks", ".lf-input-narrow" })
        {
            Assert.Contains(cls, css);
        }
    }

    /// <summary>
    /// 批次F：DevMonitor 只能看不能改——但要**看得到設定值**。
    /// 整塊隱藏會讓「目前設定是什麼」也看不到，而那正是 dev 需要的資訊。
    /// </summary>
    [Fact]
    public void 無維護權限時設定改為唯讀而非整塊隱藏()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"));
        var cshtml = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml"));

        Assert.Contains("schedule-readonly-hint", cshtml);
        Assert.Contains("schedule-readonly-hint", js);
        Assert.Contains("el.disabled = true", js);
    }

    /// <summary>
    /// 三張狀態卡等高（回饋第 40 輪批次A）：grid 用 stretch、卡片是 flex column、
    /// 按鈕列靠 `lf-run-actions` 貼底。三者缺一，卡片就會各自貼齊自己的內容高度而高低不齊。
    /// </summary>
    [Fact]
    public void 狀態卡等高的三個必要條件都在()
    {
        var root = FindRepoRoot();
        var css = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "css", "site.css"));

        Assert.Contains("align-items: stretch", css);
        Assert.DoesNotContain("align-items: start", css.Split(".lf-run-status-grid")[1].Split('}')[0]);
        Assert.Contains(".lf-run-status-grid > .lf-card", css);
        Assert.Contains("margin-top: auto", css);

        var cshtml = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml"));
        // 三張卡的按鈕列都要掛上貼底的類別，漏一張那張的按鈕就會浮在中間
        Assert.Equal(3, CountOccurrences(cshtml, "lf-run-actions"));
    }

    /// <summary>
    /// PRTG 的進度軌搬到自己的卡（回饋第 40 輪批次A）：那條軌的內容全是 PRTG 的事，
    /// 與總開關、結構同步狀態、歷史回填放在一起才讀得出因果。
    /// </summary>
    [Fact]
    public void PRTG卡承載每日擷取軌與同步狀態()
    {
        var root = FindRepoRoot();
        var cshtml = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml"));

        var prtgCardStart = cshtml.IndexOf("id=\"prtg-status-card\"", StringComparison.Ordinal);
        Assert.True(prtgCardStart > 0, "找不到 PRTG 狀態卡");
        var prtgCard = cshtml[prtgCardStart..];

        // PRTG 軌與同步入口都在這張卡裡
        Assert.Contains("schedule-prtg-progress-wrap", prtgCard);
        Assert.Contains("prtg-sync-start", prtgCard);
        Assert.Contains("prtg-sync-summary", prtgCard);
        Assert.Contains("prtg-module-state", prtgCard);
        Assert.Contains("prtg-backfill-start", prtgCard);
        Assert.Contains("prtg-sync-cancel", prtgCard);

        // 取數卡裡不該再有 PRTG 軌
        var runCardStart = cshtml.IndexOf("id=\"schedule-run-card\"", StringComparison.Ordinal);
        var runCard = cshtml[runCardStart..prtgCardStart];
        Assert.DoesNotContain("schedule-prtg-progress", runCard);

        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"));
        Assert.Contains("renderPrtgSyncSummary", js);
        Assert.Contains("prtg-structure-sync/cancel", js);
        // 停止鈕的顯示切換走 d-none，與 data-maintain-only 的隱藏同一個 class——
        // 輪詢必須看權限旗標，否則會把唯讀使用者不該看到的停止鈕露出來
        Assert.Contains("cancelBtn && canMaintainSchedule", js);
        Assert.Contains("renderPrtgModuleState", js);
        // 「尚未同步」與「同步到 0 筆」要分得出來
        Assert.Contains("尚未同步", js);
    }

    /// <summary>
    /// 三張卡的動作鈕互斥（回饋第 41 輪批次G）：執行中只留「停止」，閒置只留啟動類。
    /// 兩顆並排時使用者得自己判斷哪顆有效；灰掉的鈕仍佔位、讀起來像「壞了」，所以一律用 d-none 切換。
    /// </summary>
    [Fact]
    public void 排程頁三張卡的動作鈕以顯示隱藏互斥而非灰掉()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"));

        // 取數卡：立即執行在執行中要藏起來（原本永遠可見）
        Assert.Contains("runNowButton?.classList.toggle('d-none', status.isRunning)", js);
        Assert.Contains("stopButton?.classList.toggle('d-none', !status.canStop)", js);

        // AI 卡：兩顆啟動鈕改為隱藏，不再只是 disabled。
        // 回饋四十五輪 A2/C1、C2 後隱藏條件擴充成三因子的 OR（`hideAiStart`），條件本身的斷言
        // 在 AI卡啟動鈕隱藏條件含三個因子；這裡守的是「用 d-none 而非 disabled」。
        Assert.Contains("document.getElementById('schedule-ai-run-now')?.classList.toggle('d-none', hideAiStart)", js);
        Assert.Contains("document.getElementById('schedule-ai-force-rerun')?.classList.toggle('d-none', hideAiStart)", js);
        Assert.Contains("hideAiStart = status.isRunning ||", js);
        Assert.DoesNotContain("runNowBtn.disabled = status.isRunning", js);
        Assert.DoesNotContain("forceBtn.disabled = status.isRunning", js);
    }

    /// <summary>
    /// PRTG 卡在模組未啟用時要把同步與回填灰掉並指路（回饋第 41 輪批次F5）。
    /// 兩處輪詢（同步狀態、回填狀態）都會重設同一顆按鈕的 disabled，
    /// 少接一處就會在下一次輪詢把閘打開——所以兩處都要看得到模組開關。
    /// </summary>
    [Fact]
    public void PRTG未啟用時同步與回填按鈕在所有寫入點都被閘住()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"));

        // renderPrtgModuleState 一次設定兩顆
        Assert.Contains("'prtg-sync-start', 'prtg-backfill-start'", js);
        Assert.Contains("prtg-disabled-hint", js);

        // 兩處輪詢各自也要看模組開關，否則會把閘打開
        // 回饋四十五輪 A2/C4：三態收緊成只認明確 true（null 未知一律視為未啟用）
        Assert.Contains("status.isRunning || prtgModuleEnabled !== true", js);
        Assert.Contains("startButton.disabled = prtgModuleEnabled !== true", js);

        // 立即執行前的提醒：只在「連線已設定但未啟用」時問，沒設定 PRTG 的站台不該每次被問
        Assert.Contains("prtgModuleEnabled !== true && prtgConnectionConfigured", js);
        Assert.Contains("hasPrtgConnection", js);
    }

    [Fact]
    public void 排程頁PRTG同步摘要包含來源標示()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"));
        Assert.Contains("lastSource", js);
        Assert.Contains("夜間取數", js);
        Assert.Contains("尚未同步", js);
    }

    [Fact]
    public void RunsJs包含PRTG回望提示與進度天數標示()
    {
        var root = FindRepoRoot();
        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"));

        Assert.Contains("prtgFetchStrategy", js);
        Assert.Contains("PRTG 將逐日查詢歷史值", js);
        Assert.Contains("PRTG 回望範圍", js);
        Assert.Contains("prtgDayIndex", js);
        Assert.Contains("第 ${", js);
    }

    /// <summary>
    /// 歷史回填可停止、翻狀態變更期間有進度、停止後狀態文字說得出「已停止」（docs/WEB-SPEC.md §9.10）。
    /// 停止鈕照結構同步停止鈕的寫法：輪詢切 d-none 時必須看權限旗標。
    /// </summary>
    [Fact]
    public void 歷史回填有停止鈕_狀態變更讀取進度與已停止文字()
    {
        var root = FindRepoRoot();
        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        var cshtml = File.ReadAllText(cshtmlPath);
        Assert.Contains("class=\"btn btn-sm btn-outline-danger d-none\" id=\"prtg-backfill-cancel\" data-maintain-only", cshtml);

        // Runs.cshtml 在 dev 就帶 UTF-8 BOM，改檔不得把它弄掉
        var head = File.ReadAllBytes(cshtmlPath).Take(3).ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, head);

        var js = File.ReadAllText(Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"));
        Assert.Contains("prtg-backfill/cancel", js);
        Assert.Contains("readingStateChanges", js);
        Assert.Contains("已停止", js);
        Assert.Contains("讀取狀態變更：${formatNumber(read)} / 約 ${formatNumber(total)} 筆", js);
        Assert.Contains("已送出停止，回填會在目前這一步結束後停下", js);
        // 權限守門：同步與回填兩顆停止鈕的輪詢切換都要看 canMaintainSchedule
        Assert.Equal(2, CountOccurrences(js, "cancelBtn && canMaintainSchedule"));
    }

    // ── 回饋四十五輪 A2：排程作業頁按鈕顯示規則與防護 ───────────────────────────
    // 以下測試一律「先擷取目標函式／容器主體再斷言」：全檔 Assert.Contains 只要檔案任一角落
    // 出現過就通過，偵測不到「條件寫在錯的函式裡」。

    /// <summary>
    /// 從 `宣告token` 起，以大括號配對擷取該函式（或事件處理）的主體。
    /// 字串／樣板字面值裡的大括號會干擾配對，這裡只需粗略範圍，故忽略字串內容但排除註解行不必要。
    /// </summary>
    private static string ExtractBlock(string js, string declToken)
    {
        var start = js.IndexOf(declToken, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var open = js.IndexOf('{', start);
        if (open < 0) return string.Empty;

        var depth = 0;
        for (var i = open; i < js.Length; i++)
        {
            if (js[i] == '{') depth++;
            else if (js[i] == '}')
            {
                depth--;
                if (depth == 0) return js[start..(i + 1)];
            }
        }
        return string.Empty;
    }

    /// <summary>擷取包住 <paramref name="innerMarker"/> 的最內層 &lt;div ...&gt;…&lt;/div&gt; 容器片段。</summary>
    private static string ExtractDivContaining(string html, string innerMarker, string openMarker)
    {
        var inner = html.IndexOf(innerMarker, StringComparison.Ordinal);
        if (inner < 0) return string.Empty;

        var start = html.LastIndexOf(openMarker, inner, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var depth = 0;
        var i = start;
        while (i < html.Length)
        {
            var nextOpen = html.IndexOf("<div", i, StringComparison.Ordinal);
            var nextClose = html.IndexOf("</div>", i, StringComparison.Ordinal);
            if (nextClose < 0) return string.Empty;

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                i = nextOpen + 4;
            }
            else
            {
                depth--;
                if (depth == 0) return html[start..(nextClose + 6)];
                i = nextClose + 6;
            }
        }
        return string.Empty;
    }

    private static string ReadRunsJs() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "runs.js"));

    private static string ReadRunsCshtml() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "Views", "Pages", "Runs.cshtml"));

    /// <summary>C1＋C2：AI 卡兩顆啟動鈕的隱藏條件＝AI 自己執行中 ∪ 取數執行中 ∪ AI 未設定。</summary>
    [Fact]
    public void AI卡啟動鈕隱藏條件含三個因子()
    {
        var js = ReadRunsJs();
        var body = ExtractBlock(js, "function applyAiScheduleStatus(");
        Assert.False(string.IsNullOrWhiteSpace(body), "擷取不到 applyAiScheduleStatus 主體");

        // 三個因子都要在同一個 OR 運算式裡。AI 可用性是三態：null＝還沒問到，
        // 此時**不得**隱藏（把「還不知道」畫成「已知是關的」），所以比對的是明確的 false。
        Assert.Contains("status.isRunning || fetchRunning || aiAvailable === false", body);
        Assert.DoesNotContain("|| !aiAvailable", body);
        // 取數狀態未知（null）時不算執行中——只認明確 true
        Assert.Contains("fetchScheduleRunning === true", body);
        // 兩顆啟動鈕都吃這個結果
        Assert.Contains("getElementById('schedule-ai-run-now')?.classList.toggle('d-none', hideAiStart)", body);
        Assert.Contains("getElementById('schedule-ai-force-rerun')?.classList.toggle('d-none', hideAiStart)", body);

        // 取數狀態由取數卡的渲染寫入，不另打 API
        var fetchBody = ExtractBlock(js, "function applyScheduleStatus(");
        Assert.False(string.IsNullOrWhiteSpace(fetchBody), "擷取不到 applyScheduleStatus 主體");
        Assert.Contains("fetchScheduleRunning = status.isRunning;", fetchBody);
    }

    /// <summary>C3：說明元素在 AI 卡按鈕列容器內，且由 runs.js 的說明渲染函式負責。</summary>
    [Fact]
    public void AI卡按鈕列內有說明元素且由渲染函式驅動()
    {
        var cshtml = ReadRunsCshtml();
        var actions = ExtractDivContaining(
            cshtml, "id=\"schedule-ai-force-rerun\"",
            "<div class=\"d-flex flex-wrap align-items-center gap-2 lf-run-actions\" data-maintain-only>");
        Assert.False(string.IsNullOrWhiteSpace(actions), "擷取不到 AI 卡的 .lf-run-actions 容器");
        Assert.Contains("id=\"schedule-ai-run-now\"", actions);
        Assert.Contains("id=\"schedule-ai-actions-hint\"", actions);
        // 連結不得寫死 / 開頭
        Assert.Contains("@Url.Content(\"~/admin/settings\")", actions);
        Assert.DoesNotContain("href=\"/", actions);
        Assert.DoesNotContain("style=", actions);

        var js = ReadRunsJs();
        var hintBody = ExtractBlock(js, "function renderAiActionsHint(");
        Assert.False(string.IsNullOrWhiteSpace(hintBody), "擷取不到 renderAiActionsHint 主體");
        Assert.Contains("schedule-ai-actions-hint", hintBody);
        // 說明列同樣只在「明確知道未設定」時出現，未知時不顯示
        Assert.Contains("aiAvailable === false", hintBody);
        Assert.DoesNotContain("if (!aiAvailable)", hintBody);
        Assert.Contains("fetchRunning", hintBody);
        Assert.Contains("AI 服務未設定", hintBody);
        Assert.Contains("取數執行中", hintBody);

        // 說明由 AI 卡渲染時一起套用
        var aiBody = ExtractBlock(js, "function applyAiScheduleStatus(");
        Assert.Contains("renderAiActionsHint(fetchRunning)", aiBody);
    }

    /// <summary>C5：兩顆停止鈕的 click 處理各自有 withBusy 防連點。</summary>
    [Fact]
    public void 兩顆停止鈕的click處理都有withBusy()
    {
        var js = ReadRunsJs();

        var stopBody = ExtractBlock(js, "document.getElementById('schedule-stop')?.addEventListener('click'");
        Assert.False(string.IsNullOrWhiteSpace(stopBody), "擷取不到 schedule-stop 的 click 處理");
        Assert.Contains("withBusy(", stopBody);
        Assert.Contains("restore()", stopBody);

        var aiStopBody = ExtractBlock(js, "document.getElementById('schedule-ai-stop')?.addEventListener('click'");
        Assert.False(string.IsNullOrWhiteSpace(aiStopBody), "擷取不到 schedule-ai-stop 的 click 處理");
        Assert.Contains("withBusy(", aiStopBody);
        Assert.Contains("restore()", aiStopBody);
    }

    /// <summary>
    /// C4：PRTG 啟用旗標三態收緊——只認明確 true。
    /// 改動前有 7 個判斷點（6 個 `=== false` ＋ 1 個 `=== true`），本輪把那 6 個改成 `!== true`，
    /// 判斷點總數維持 7。
    /// </summary>
    [Fact]
    public void PRTG啟用旗標判斷只認true()
    {
        var js = ReadRunsJs();

        Assert.Equal(0, CountOccurrences(js, "prtgModuleEnabled === false"));
        Assert.Equal(6, CountOccurrences(js, "prtgModuleEnabled !== true"));
        Assert.Equal(1, CountOccurrences(js, "prtgModuleEnabled === true"));

        // 判斷點總數（不含宣告與 renderPrtgModuleState 的指派）
        var judgements = System.Text.RegularExpressions.Regex.Matches(
            js, @"prtgModuleEnabled\s*(===|!==)\s*(true|false)").Count;
        Assert.Equal(7, judgements);

        // 輪詢寫入點與點擊時的第二道檢查都要改到
        var syncPoll = ExtractBlock(js, "async function refreshPrtgSyncStatus(");
        Assert.False(string.IsNullOrWhiteSpace(syncPoll), "擷取不到 refreshPrtgSyncStatus 主體");
        Assert.Contains("prtgModuleEnabled !== true", syncPoll);

        var syncBind = ExtractBlock(js, "function bindPrtgSync(");
        Assert.False(string.IsNullOrWhiteSpace(syncBind), "擷取不到 bindPrtgSync 主體");
        Assert.Contains("prtgModuleEnabled !== true", syncBind);

        var backfillBind = ExtractBlock(js, "function bindPrtgBackfill(");
        Assert.False(string.IsNullOrWhiteSpace(backfillBind), "擷取不到 bindPrtgBackfill 主體");
        Assert.Contains("prtgModuleEnabled !== true", backfillBind);
    }

    /// <summary>C6：強制重跑 modal 在窗口內有新執行開始時停用確認鈕，且文案帶出觸發者。</summary>
    [Fact]
    public void 強制重跑modal有防護與觸發者文案()
    {
        var js = ReadRunsJs();
        var body = ExtractBlock(js, "function renderAiRerunModal(");
        Assert.False(string.IsNullOrWhiteSpace(body), "擷取不到 renderAiRerunModal 主體");

        // 「非執行中 → 執行中」的轉折才封鎖，且只在 modal 開著時
        Assert.Contains("aiRerunModalOpen", body);
        Assert.Contains("!wasAiScheduleRunning", body);
        Assert.Contains("confirmBtn.disabled = blocked", body);
        Assert.Contains("schedule-ai-rerun-blocked", body);
        // 帶出觸發者，AI 沒在跑時不顯示
        Assert.Contains("triggerText", body);
        Assert.Contains("status.isRunning", body);

        // 關閉 modal 後恢復可用
        var hidden = ExtractBlock(js, "aiRerunModalEl?.addEventListener('hidden.bs.modal'");
        Assert.False(string.IsNullOrWhiteSpace(hidden), "擷取不到 modal 關閉處理");
        Assert.Contains("confirmBtn.disabled = false", hidden);

        // AI 狀態輪詢每一輪都要套用
        var aiBody = ExtractBlock(js, "function applyAiScheduleStatus(");
        Assert.Contains("renderAiRerunModal(status)", aiBody);

        var cshtml = ReadRunsCshtml();
        Assert.Contains("id=\"schedule-ai-rerun-current\"", cshtml);
        Assert.Contains("id=\"schedule-ai-rerun-blocked\"", cshtml);
    }

    /// <summary>本輪新增的頂層函式各只定義一次（同名重複定義會無聲覆蓋前者）。</summary>
    [Fact]
    public void 本輪新增的頂層函式各只定義一次()
    {
        var js = ReadRunsJs();
        foreach (var name in new[] { "renderAiActionsHint", "renderAiRerunModal" })
        {
            var count = System.Text.RegularExpressions.Regex.Matches(
                js, $@"^function {name}\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
            Assert.True(count == 1, $"{name} 應只定義一次，實得 {count}");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
