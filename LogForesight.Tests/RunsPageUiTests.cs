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

        // AI 卡：兩顆啟動鈕改為隱藏，不再只是 disabled
        Assert.Contains("document.getElementById('schedule-ai-run-now')?.classList.toggle('d-none', status.isRunning)", js);
        Assert.Contains("document.getElementById('schedule-ai-force-rerun')?.classList.toggle('d-none', status.isRunning)", js);
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
        Assert.Contains("status.isRunning || prtgModuleEnabled === false", js);
        Assert.Contains("startButton.disabled = prtgModuleEnabled === false", js);

        // 立即執行前的提醒：只在「連線已設定但未啟用」時問，沒設定 PRTG 的站台不該每次被問
        Assert.Contains("prtgModuleEnabled === false && prtgConnectionConfigured", js);
        Assert.Contains("hasPrtgConnection", js);
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
