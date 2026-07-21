using System.Diagnostics;
using LogForesight;
using NLog;

// --selftest：純驗證模式（部署到新主機時先跑這個），只測規則/趨勢/關聯三層純函數，
// 不需要 NLog / 設定檔 / 單一執行個體鎖，也不寫 history、不呼叫 AI、不讀真實 Event Log，跑完立即結束
if (args.Contains("--selftest"))
{
    var selfTestOk = SelfTestRunner.Run();
    return selfTestOk ? 0 : 1;
}

// --debug-dump：驗證期用，完整輸出每次 AI 呼叫的 prompt 與原始回應到 diag\ 目錄
// （平常的診斷 log 刻意不記錄完整內容，見 README「診斷用檔案 Log」章節）
bool debugDump = args.Contains("--debug-dump");

// 明確指定設定檔路徑並覆寫 logDir 變數為 AppContext.BaseDirectory，不依賴 NLog 自己搜尋
// nlog.config 或判斷 ${basedir}——跟 history.txt/export/appsettings.json 用同一套基準目錄邏輯，
// 不同啟動方式（捷徑、排程工作、工作目錄不同）都不會讓 log 檔案跑到非預期的位置。
// 全部包 try/catch：設定檔有問題也不該讓診斷 log 這個輔助功能擋下主流程。
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
var nlogConfigPath = Path.Combine(AppContext.BaseDirectory, "nlog.config");
try
{
    if (File.Exists(nlogConfigPath))
    {
        LogManager.Setup().LoadConfigurationFromFile(nlogConfigPath);
        LogManager.Configuration!.Variables["logDir"] = logDir;
        LogManager.ReconfigExistingLoggers();
    }
    else
    {
        Console.WriteLine($"找不到 {nlogConfigPath}，診斷 log 位置可能不是預期目錄。");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"診斷 log 初始化失敗（不影響主流程）：{ex.Message}");
}

var log = LogManager.GetCurrentClassLogger();
log.Info("診斷 log 目錄：{LogDir}", logDir);
LogManager.Flush();

// 自我檢查：明確告知這次執行 log 檔案到底有沒有寫成功，不用再靠猜的
var expectedLogFile = Path.Combine(logDir, "logforesight.log");
Console.WriteLine(File.Exists(expectedLogFile)
    ? $"診斷 log：{expectedLogFile}"
    : $"⚠ 診斷 log 未寫入 {expectedLogFile}，請檢查該目錄的寫入權限（上方若有 NLog 內部警告訊息也一併確認）。");

// 排程/背景執行時完全看不到 console，任何在 try/catch 涵蓋範圍外發生的例外（例如背景執行緒）
// 至少要留下完整堆疊到檔案 log，不能無聲無息地消失
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    log.Fatal(e.ExceptionObject as Exception, "未捕捉的例外導致程式終止");

// ── 設定 ─────────────────────────────────────────────────────────
// 趨勢比對窗口天數，也是首次執行自動建立歷史的天數（涵蓋兩個完整週期，能分辨每週固定雜訊與異常趨勢）
const int TrendWindowDays = 14;
// 歷史資料庫保留天數（需 >= TrendWindowDays），超過的舊紀錄於每次啟動時自動清除
const int RetentionDays = 90;

// 排程背景執行（無主控台）時設定編碼會擲例外，不能讓它擋下整個程式
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch
{
    // 無主控台環境，忽略
}

// 單一執行個體：排程與手動執行重疊時，後啟動者直接退出，
// 避免兩個程序同時寫 history.txt 或同時 Prune 重寫導致資料損毀
using var instanceMutex = new Mutex(initiallyOwned: true, @"Global\LogForesight", out bool isFirstInstance);
if (!isFirstInstance)
{
    Console.WriteLine("另一個 LogForesight 執行個體正在執行中，本次直接結束。");
    log.Info("另一個執行個體正在執行中，本次直接結束");
    LogManager.Shutdown();
    return 0;
}

Console.WriteLine("--- LogForesight 啟動 ---");
log.Info("===== LogForesight 啟動 =====");
var runStopwatch = Stopwatch.StartNew();

// AI API 設定由執行檔目錄的 appsettings.json 載入
var settings = AppSettings.Load();
Console.WriteLine($"AI API：{settings.Ai.BaseUrl}（逾時 {settings.Ai.TimeoutSeconds} 秒，失敗重試 {settings.Ai.RetryCount} 次）");
log.Info("AI 設定：BaseUrl={BaseUrl}, Timeout={Timeout}s, RetryCount={RetryCount}, JsonRetryCount={JsonRetryCount}, " +
         "MaxTokens={MaxTokens}, DeepDiveMaxTokens={DeepDiveMaxTokens}, FrequencyPenalty={FrequencyPenalty}, PresencePenalty={PresencePenalty}",
    settings.Ai.BaseUrl, settings.Ai.TimeoutSeconds, settings.Ai.RetryCount, settings.Ai.JsonRetryCount,
    settings.Ai.MaxTokens, settings.Ai.DeepDiveMaxTokens, settings.Ai.FrequencyPenalty, settings.Ai.PresencePenalty);

if (debugDump)
{
    Console.WriteLine("  🔍 --debug-dump 模式：完整 prompt 與 AI 回應將輸出到 diag\\ 目錄");
}

try
{

// 規則庫載入（見 docs/RULES-PLAN.md）：初次部署寫入內建種子、之後從 rules.json 載入並驗證，
// 在建立任何分析服務之前完成——KnownIssueCatalog 的靜態規則表必須先就緒，後續的聚合/分類才有意義。
var ruleStore = StorageFactory.CreateRuleStore(settings.Storage);

// --import-rules：手動把內建種子的新增/修訂規則匯入 rules.json（預設只預覽，--apply 才寫檔），
// 見 docs/RULES-PLAN.md「初次部署寫入、後續手動匯入」的決定。放在 mutex 保護內執行，
// 避免與排程執行中的分析流程同時寫規則檔；跑完直接結束，不進入每日分析流程。
if (args.Contains("--import-rules"))
{
    RuleImporter.Run(ruleStore, apply: args.Contains("--apply"), overwriteBuiltin: args.Contains("--overwrite-builtin"));
    LogManager.Shutdown();
    return 0;
}

// --suppress / --unsuppress / --list-suppressions：主機級告警抑制的最小維護指令（見 docs/RULES-PLAN.md）。
// 同樣放在 mutex 保護內、跑完即結束，不進入每日分析流程。
if (args.Contains("--suppress") || args.Contains("--unsuppress") || args.Contains("--list-suppressions"))
{
    var suppressionStoreForCli = StorageFactory.CreateSuppressionStore(settings.Storage);

    if (args.Contains("--list-suppressions"))
    {
        SuppressionCli.List(suppressionStoreForCli);
    }
    else if (args.Contains("--unsuppress"))
    {
        SuppressionCli.Unsuppress(suppressionStoreForCli, GetArgValue(args, "--unsuppress"));
    }
    else
    {
        var (ruleContentForCli, _) = RuleBootstrapper.LoadContent(ruleStore);
        var knownIds = RuleValidator.Validate(ruleContentForCli.Rules).ValidRules
            .Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SuppressionCli.Suppress(suppressionStoreForCli, knownIds,
            GetArgValue(args, "--suppress"), GetArgValue(args, "--reason"), GetArgValue(args, "--days"));
    }

    LogManager.Shutdown();
    return 0;
}

// --import-hosts / --host-list：NetIQ 主機清單的維護指令（docs/NETIQ-HOSTLIST-WEB-PLAN.md 決策 D）。
// 同樣放在 mutex 保護內、跑完即結束——匯入會改寫主機清單，不能與排程中的分析流程同時進行。
if (args.Contains("--import-hosts") || args.Contains("--host-list"))
{
    var hostStoreForCli = StorageFactory.CreateHostStore(settings.Storage, AppContext.BaseDirectory);

    var exitCode = args.Contains("--import-hosts")
        ? HostListCli.Import(hostStoreForCli, settings.NetIq, AppContext.BaseDirectory)
        : HostListCli.List(hostStoreForCli, settings.NetIq, AppContext.BaseDirectory);

    LogManager.Shutdown();
    return exitCode;
}

RuleBootstrapper.Run(ruleStore);

// 同步內建規則的原廠種子鏡像（docs/WEB-SPEC.md §2.1 Phase 4）：Web 的「回復預設」需要一份
// 使用者碰不到的原始內容才比較得出差異。放在 RuleBootstrapper 之後——那時規則庫已就緒。
try
{
    StorageFactory.CreateRuleSeedStore(settings.Storage, AppContext.BaseDirectory)
        .Sync(KnownIssueSeed.CreateRules(), KnownIssueSeed.Version);
}
catch (Exception ex)
{
    log.Warn(ex, "規則種子鏡像同步失敗（不影響本次分析）：{0}", ex.Message);
}

var suppressionStore = StorageFactory.CreateSuppressionStore(settings.Storage);
var currentHost = Environment.MachineName;
var expiredSuppressions = SuppressionFilter.ExpiredForHost(suppressionStore.LoadAll(), currentHost, DateTime.Now);
foreach (var expired in expiredSuppressions)
{
    Console.WriteLine($"  ℹ 抑制已到期，恢復告警：{expired.RuleId}（原訂於 {expired.ExpiresAt:yyyy-MM-dd} 到期，" +
                      $"原因：{expired.Reason}；未自動清理，可用 --unsuppress 或編輯 suppressions.json）");
}

// 執行紀錄（docs/WEB-SPEC.md §2.1 Phase 4）：啟動時登記、結束時回填，讓 Web 的執行監控頁
// 能回答「昨晚每台主機都跑了嗎、有沒有出問題」。掛上 NLog target 後 Warn 以上自動流入，
// 不需要在既有程式碼各處加呼叫。建立失敗不影響分析（見 BatchRunRecorder）。
IBatchRunStore? batchRunStore = null;
try
{
    batchRunStore = StorageFactory.CreateBatchRunStore(settings.Storage, AppContext.BaseDirectory);
}
catch (Exception ex)
{
    log.Warn(ex, "執行紀錄儲存初始化失敗（不影響本次分析）：{0}", ex.Message);
}

using var runRecorder = new BatchRunRecorder(batchRunStore, currentHost, args);
runRecorder.Milestone($"批次啟動（版本 {typeof(Program).Assembly.GetName().Version}）");

var eventLogService = new EventLogService();
IPromptDumper dumper = debugDump ? new FilePromptDumper() : new NullPromptDumper();
var aiService = new AIService(settings.Ai, dumper);
var historyService = StorageFactory.CreateRecordStore(settings.Storage); // 依 Storage.Type 選後端，目前只有 Jsonl
var reportSink = new FileReportSink(); // 風險報告輸出至執行檔目錄下的 export
// 登記本機於主機清單（docs/WEB-SPEC.md §2.1 Phase 1）：Web 的儀表板要能指出
// 「哪些主機已經好幾天沒回報了」，而那個判斷需要一筆「這台主機最近何時執行過」的紀錄。
// 同時取回主機 PK——**那是分析紀錄與主機的關聯鍵**（docs/NETIQ-HOSTLIST-WEB-PLAN.md），
// 所以這段必須排在分析服務建立之前。
// 刻意只呼叫 Touch——它只建立缺少的主機並更新回報時間，不碰 Web 維護的角色描述、
// 群組與負責人（批次不知道那些欄位，用空值蓋掉會把人工設定清光）。
// 失敗不得中斷分析：Web 的附屬資料寫不進去，不該讓當晚的事件分析整個停擺——
// 此時 hostId 維持 0，當晚的紀錄改由主機名稱歸戶（查詢端的 fallback 路徑）。
long currentHostId = 0;
try
{
    currentHostId = StorageFactory.CreateHostStore(settings.Storage, AppContext.BaseDirectory)
        .Touch(currentHost, DateTime.Now).HostId;
}
catch (Exception ex)
{
    log.Warn(ex, "登記主機回報時間失敗（不影響本次分析）：{0}", ex.Message);
    Console.WriteLine($"  ⚠ 登記主機回報時間失敗（不影響分析）：{ex.Message}");
}

var reportService = new RiskReportService(aiService, reportSink, settings.Ai.DeepDiveMaxTokens);
var analysisService = new LogAnalysisService(eventLogService, aiService, historyService, suppressionStore,
    settings.Analysis.ServerDescription, reportService, currentHost, currentHostId);
var permissionMonitor = new PermissionMonitorService(settings.Permissions);
var weeklyCheckupService = new WeeklyCheckupService(aiService, historyService, reportSink, suppressionStore);

// 0. 權限/角色異動檢查：與每日事件分析各自獨立，反映「本次執行當下」的權限狀態
//    （不是某個歷史日期的事），所以每次執行都做一次、不受歷史回補流程影響。
//    刻意不依賴 Security log／稽核政策，直接比對 ACL 與 Administrators 群組成員，
//    在目前沒有系統管理員權限讀取 Security log 的情況下仍能運作。
Console.WriteLine($"\n檢查權限異動（監控 {permissionMonitor.WatchedFolders.Count} 個資料夾 + 本機 Administrators 群組）...");
var permissionCheck = permissionMonitor.Check();
if (permissionCheck.Alerts.Count > 0)
{
    var original = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine("  ╔══════════════════════════════════════════════════╗");
    Console.WriteLine($"  ║  🔑 偵測到 {permissionCheck.Alerts.Count} 項權限／角色異動，請立即確認是否為授權操作！");
    foreach (var alert in permissionCheck.Alerts)
    {
        Console.WriteLine($"  ║  - {alert}");
    }
    Console.WriteLine("  ╚══════════════════════════════════════════════════╝");
    Console.ForegroundColor = original;

    // 被異動項目明細：獨立於自動檢查之外的人工防護層——逐項列出異動前後對照，
    // 讓使用者自行判斷每一筆是否為正常/授權的異動
    Console.WriteLine("\n  被異動項目明細（請逐項人工確認是否為正常異動）：");
    for (int i = 0; i < permissionCheck.Details.Count; i++)
    {
        var d = permissionCheck.Details[i];
        Console.WriteLine($"    {i + 1}. {d.Target}｜{d.ChangeType}");
        Console.WriteLine($"       異動前：{d.Before}");
        Console.WriteLine($"       異動後：{d.After}");
    }

    var reportSb = new System.Text.StringBuilder();
    reportSb.AppendLine("LogForesight 權限異動報告");
    reportSb.AppendLine($"檢查時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    reportSb.AppendLine();
    reportSb.AppendLine("■ 異動告警");
    foreach (var alert in permissionCheck.Alerts)
    {
        reportSb.AppendLine($"- {alert}");
    }
    reportSb.AppendLine();
    reportSb.AppendLine("■ 被異動項目明細（請逐項人工確認是否為正常/授權的異動）");
    for (int i = 0; i < permissionCheck.Details.Count; i++)
    {
        var d = permissionCheck.Details[i];
        reportSb.AppendLine($"{i + 1}. 對象：{d.Target}");
        reportSb.AppendLine($"   異動類型：{d.ChangeType}");
        reportSb.AppendLine($"   異動前：{d.Before}");
        reportSb.AppendLine($"   異動後：{d.After}");
        reportSb.AppendLine("   └ 請確認：此異動是否為您或授權人員的操作？若否，可能為入侵或誤設定，建議立即調查。");
        reportSb.AppendLine();
    }

    var permissionFileName = $"{DateTime.Today:yyyy-MM-dd}_權限異動.txt";
    var permissionReportRef = await reportSink.WriteAsync(ReportKind.Permission, host: "", permissionFileName, reportSb.ToString());
    Console.WriteLine($"  📄 權限異動報告（含逐項明細）：{permissionReportRef.Value}");

    // 雙軌寫入（docs/WEB-SPEC.md §2.1 Phase 3）：上面的 console 告警與 txt 報告是既有輸出、
    // 一字未改；這裡另外把每筆異動寫成結構化紀錄，供 Web 的「權限異動待辦」逐筆確認。
    // 沒有這一軌，那一頁在 JSONL 前期就沒有任何資料可顯示。
    // 失敗不得中斷分析：Web 的附屬資料寫不進去，不該讓當晚的事件分析停擺。
    try
    {
        var permissionChangeStore = StorageFactory.CreatePermissionChangeStore(settings.Storage, AppContext.BaseDirectory);
        var detectedAt = DateTime.Now;

        permissionChangeStore.AppendChanges(permissionCheck.Details.Select((detail, index) => new PermissionChangeRecord
        {
            ChangeId = Guid.NewGuid().ToString("N"),
            HostName = currentHost,
            DetectedAt = detectedAt,
            Target = detail.Target,
            ChangeType = detail.ChangeType,
            Before = detail.Before,
            After = detail.After,
            // Alerts 與 Details 由 PermissionCheckResult.Add 成對加入，索引一一對應
            AlertText = index < permissionCheck.Alerts.Count ? permissionCheck.Alerts[index] : string.Empty
        }));

        Console.WriteLine($"  ✓ 已寫入 {permissionCheck.Details.Count} 筆權限異動供 Web 逐筆確認");
    }
    catch (Exception ex)
    {
        log.Warn(ex, "權限異動的結構化寫入失敗（不影響本次分析與既有報告）：{0}", ex.Message);
        Console.WriteLine($"  ⚠ 權限異動的結構化寫入失敗（既有報告不受影響）：{ex.Message}");
    }
}
else
{
    Console.WriteLine("  未偵測到權限異動。");
}

// 1. 清理超過保留天數的歷史紀錄，避免資料庫無限增長
var pruned = historyService.Prune(RetentionDays);
if (pruned > 0)
{
    Console.WriteLine($"已清除 {pruned} 筆超過 {RetentionDays} 天的歷史紀錄。");
}

// 2. 找出趨勢窗口內缺漏的日子（首次執行 = 整個窗口都缺；平常 = 只有昨天）
var yesterday = DateTime.Today.AddDays(-1);
var missingDates = Enumerable.Range(1, TrendWindowDays)
    .Select(offset => DateTime.Today.AddDays(-offset))
    .Where(date => !historyService.HasRecord(date))
    .OrderBy(date => date)
    .ToList();

if (missingDates.Count == 0)
{
    Console.WriteLine($"\n{yesterday:yyyy-MM-dd} 已有分析紀錄，跳過（同一天重複執行不會產生重複資料）。");
    log.Info("{Date:yyyy-MM-dd} 已有分析紀錄，本次跳過", yesterday);
}
else
{
    // 3. 一次倒序掃描取回整個缺漏區間的事件，三個日誌來源平行掃描，並回傳資料完整性中繼資料
    //   （哪些來源保留的歷史不足以涵蓋整個區間、Security 本次是否可讀）。
    //   抓取全部前置：後面的 AI 分析迴圈只從記憶體取資料，不會每分析完一天才回頭抓下一天。
    var rangeStart = missingDates[0];
    Console.WriteLine($"\n平行掃描 System/Application/Security，取得 {rangeStart:yyyy-MM-dd} ~ {yesterday:yyyy-MM-dd} 的事件...");
    var scanResult = await eventLogService.ScanRangeFromAllAsync(rangeStart, DateTime.Today);
    var logsByDate = scanResult.Entries
        .GroupBy(l => l.TimeGenerated.Date)
        .ToDictionary(g => g.Key, g => g.ToList());
    Console.WriteLine($"共取得 {scanResult.Entries.Count} 筆事件。");

    if (scanResult.SecurityAvailable == false)
    {
        Console.WriteLine("  ⚠ Security log 本次無法讀取（需系統管理員權限），入侵跡象相關偵測將標記為未檢查。");
    }

    if (missingDates.Count > 1)
    {
        Console.WriteLine($"偵測到歷史資料有缺漏，回補 {missingDates.Count} 天（每天皆完整 AI 分析，由最舊到最新，後面的日期能參照前面累積的歷史）。");
        Console.WriteLine("（能回補多久取決於 Event Log 的保留量，太舊的事件可能已被覆蓋）");
    }

    // 4. 逐日分析：趨勢比對依賴前面日期寫入的歷史，因此分析本身必須依序執行
    var results = new List<DailyAnalysisRecord>();
    var elapsedByDate = new Dictionary<DateTime, TimeSpan>();
    foreach (var date in missingDates)
    {
        Console.WriteLine($"\n[{date:yyyy-MM-dd}] 分析中（含 AI 判讀）...");
        var dayStopwatch = Stopwatch.StartNew();

        var logs = logsByDate.TryGetValue(date, out var dayLogs) ? dayLogs : new List<EventLogEntryData>();
        var dataIncomplete = scanResult.IsDateIncomplete(date);
        var record = await analysisService.AnalyzeDayAsync(date, logs, historyDays: TrendWindowDays,
            dataIncomplete: dataIncomplete, securityLogAvailable: scanResult.SecurityAvailable);
        results.Add(record);

        dayStopwatch.Stop();
        elapsedByDate[date] = dayStopwatch.Elapsed;
        runRecorder.RecordDayAnalyzed();

        // AiAnalyzed=false 有兩種意義：低風險日「刻意不呼叫」（正常）與呼叫失敗的降級（異常）。
        // 只有後者該計入 AI 失敗——把刻意跳過算成失敗，會讓執行監控在完全安靜的日子
        // 也顯示「有警告」，狼來了幾次之後就沒有人再看那個顏色了。
        if (record.AiAnalyzed || record.RiskLevel != "低")
        {
            runRecorder.RecordAiCall(record.AiAnalyzed);
        }
        PrintResult(record, verbose: date == yesterday);
        Console.WriteLine($"  ⏱ 本日耗時：{FormatElapsed(dayStopwatch.Elapsed)}");
    }

    runRecorder.Milestone($"逐日分析完成：{results.Count} 天");

    // 5. 執行結果總表：讓使用者一眼看到「哪幾天有問題、該打開哪個報告檔、花了多久」
    Console.WriteLine("\n══════════ 本次執行結果 ══════════");
    foreach (var r in results)
    {
        Console.WriteLine($"  {r.Date:yyyy-MM-dd}  風險【{r.RiskLevel}】  耗時 {FormatElapsed(elapsedByDate[r.Date])}" +
                          (r.ReportFile != null ? $"  → {r.ReportFile}" : ""));
    }

    var riskyCount = results.Count(r => r.ReportFile != null);
    if (riskyCount > 0)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n  需要關注：{riskyCount} 天判定有風險，問題說明、AI 深入分析與原始 log 已輸出至上列報告檔。");
        Console.ForegroundColor = original;
    }
    else
    {
        Console.WriteLine("\n  所有日期風險等級為低，無需特別處置。");
    }

    log.Info("本次執行結果：{Results}", string.Join(" | ", results.Select(r => $"{r.Date:MM-dd}={r.RiskLevel}")));
}

// 6. 體檢：週期性回顧（獨立於每日分析），距上次體檢達 CheckupIntervalDays 天（含補跑）就執行
//    （2026-07-20 重設計：due-date 輪巡取代固定星期幾，見 docs/PLAN.md「核心設計決策 B」）。
//    以「昨天」為體檢基準日——那是最近一筆已完整分析並寫入歷史的一天。
if (weeklyCheckupService.ShouldRun(DateTime.Today, settings.Analysis.CheckupIntervalDays))
{
    Console.WriteLine($"\n執行體檢（週期性回顧，以 {yesterday:yyyy-MM-dd} 為基準）...");
    var checkupStopwatch = Stopwatch.StartNew();
    var checkup = await weeklyCheckupService.RunAsync(yesterday, settings.Analysis.CheckupIntervalDays, settings.Analysis.ServerDescription);

    if (!checkup.Completed)
    {
        // AI 失敗：不寫入歷史，下次執行時補跑機制會重試（不消耗本期體檢額度）
        Console.WriteLine($"  ⚠ 體檢未完成（{checkup.Conclusion}），未寫入歷史，下次執行將自動重試。");
    }
    else
    {
        historyService.AttachWeeklyCheckup(yesterday, checkup);

        if (checkup.HasFindings)
        {
            var original = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  📋 體檢有發現：{checkup.Conclusion}");
            if (checkup.ReportFile != null)
            {
                Console.WriteLine($"  📄 體檢報告：{checkup.ReportFile}");
            }
            Console.ForegroundColor = original;
        }
        else
        {
            Console.WriteLine($"  體檢完成，無累積性異常。（{checkup.Conclusion}）");
        }
    }
    Console.WriteLine($"  ⏱ 體檢耗時：{FormatElapsed(checkupStopwatch.Elapsed)}");
    log.Info("體檢：基準日={Date:yyyy-MM-dd}, 完成={Completed}, 有發現={HasFindings}, 耗時={ElapsedMs}ms",
        yesterday, checkup.Completed, checkup.HasFindings, checkupStopwatch.ElapsedMilliseconds);
}

    Console.WriteLine($"\n歷史資料庫：{historyService.Location}");
    Console.WriteLine($"總執行時間：{FormatElapsed(runStopwatch.Elapsed)}");
    Console.WriteLine("--- 執行結束 ---");
    log.Info("===== 執行結束，總耗時 {ElapsedMs}ms =====", runStopwatch.ElapsedMilliseconds);
    runRecorder.Milestone("執行結束");
    runRecorder.Finish(exitCode: 0);
    LogManager.Shutdown(); // 確保緩衝的 log 都寫入檔案再結束程序
    return 0;
}
catch (Exception ex)
{
    // 執行紀錄的回填由 runRecorder 的 using 負責：例外往外傳時 using 產生的 finally
    // 會先執行 Dispose()，以 exit code 1 回填。因此掛掉的執行在監控頁顯示為「失敗」
    // 而不是停在「執行中」——後者正好會把最需要注意的狀態藏起來。
    // 全域防護：任何未預期的錯誤都要留下訊息並回報非零 exit code，
    // 讓工作排程器（勾選「工作失敗時通知」或檢查 LastTaskResult）能監控到
    Console.WriteLine($"\n執行失敗：{ex}");
    Console.WriteLine($"總執行時間：{FormatElapsed(runStopwatch.Elapsed)}");
    log.Fatal(ex, "執行失敗，總耗時 {ElapsedMs}ms", runStopwatch.ElapsedMilliseconds);
    LogManager.Shutdown();
    return 1;
}

/// <summary>取 --flag value 形式的參數值；flag 不存在或後面沒有值時回傳 null</summary>
static string? GetArgValue(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static string FormatElapsed(TimeSpan span) =>
    span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小時 {span.Minutes} 分 {span.Seconds} 秒"
    : span.TotalMinutes >= 1 ? $"{span.Minutes} 分 {span.Seconds} 秒"
    : $"{span.Seconds} 秒";

/// <summary>風險等級的行動語意對照，讓 console 不用另外解讀「中」「高」代表要做什麼（2026-07-20 AI 角色轉換）</summary>
static string RiskActionZh(string riskLevel) => riskLevel switch
{
    "高" => "需要立即處理",
    "中" => "本週內確認",
    "低" => "無需動作",
    _ => "狀態未知"
};

static void PrintResult(DailyAnalysisRecord record, bool verbose = false)
{
    Console.WriteLine($"  錯誤 {record.ErrorCount} 筆、警告 {record.WarningCount} 筆、稽核事件 {record.AuditEventCount} 筆，" +
                      $"風險等級：{record.RiskLevel}（{RiskActionZh(record.RiskLevel)}）");
    if (record.Headline.Length > 0)
    {
        Console.WriteLine($"  {record.Headline}");
    }

    if (record.DataIncomplete)
    {
        Console.WriteLine("  ⚠ 本日部分事件來源保留歷史不足以涵蓋整天，統計數字可能偏低（非真實反映當日狀況）。");
    }

    // Security 無權限時逐條列出因此停用的偵測項目——覆蓋率誠實申報，不是一句「讀取失敗」帶過
    if (record.UncoveredChecks.Count > 0)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("  ⚠ 本次未能檢查的項目（權限或來源限制，非「已檢查且無異常」）：");
        foreach (var check in record.UncoveredChecks)
        {
            Console.WriteLine($"    - {check}");
        }
        Console.ForegroundColor = original;
    }

    // 主機級抑制（見 docs/RULES-PLAN.md）：本日有告警被抑制時列出摘要，讓使用者知道「有東西被關掉了」
    // 而不是完全看不到——偵測與紀錄照常，只是不吵、不拉風險，摘要本身不受此限制
    var suppressedIssues = record.TopIssues.Where(i => i.Suppressed).ToList();
    if (suppressedIssues.Count > 0)
    {
        var summary = string.Join("、", suppressedIssues.Select(i => $"{i.RuleId} x{i.Count}"));
        Console.WriteLine($"  🔕 本日 {suppressedIssues.Count} 條告警已抑制（{summary}）");
    }

    // 高風險或命中 Critical 規則時，用醒目的紅色橫幅提醒使用者（被抑制的問題不佔用這個橫幅）
    var criticalIssues = record.TopIssues.Where(i => i.Severity == IssueSeverity.Critical && !i.Suppressed).ToList();
    if (record.RiskLevel == "高" || criticalIssues.Count > 0)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════════════╗");
        Console.WriteLine($"  ║  ⚠ 警告：{record.Date:yyyy-MM-dd} 偵測到需要立即關注的問題！");
        if (record.Headline.Length > 0)
        {
            Console.WriteLine($"  ║  {record.Headline}");
        }
        foreach (var issue in criticalIssues)
        {
            Console.WriteLine($"  ║  [{issue.Category}] {issue.Source} EventId {issue.EventId} x{issue.Count}");
            Console.WriteLine($"  ║    → {issue.KnownIssue}");
        }
        Console.WriteLine("  ╚══════════════════════════════════════════════════╝");
        Console.ForegroundColor = original;
    }

    // 跨 log 關聯訊號：已知攻擊鏈/故障鏈組合，最重要的線索，紅色醒目顯示
    if (record.CorrelationAlerts.Count > 0)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  🔗 關聯訊號（程式比對出的攻擊鏈/故障鏈組合）：");
        foreach (var alert in record.CorrelationAlerts)
        {
            Console.WriteLine($"    - {alert}");
        }
        Console.ForegroundColor = original;
    }

    // 程式比對歷史後發現的頻率異常（首次出現、頻率上升、總量突增），用黃色提醒
    if (record.TrendAlerts.Count > 0)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n  ⚠ 頻率異常／慢速惡化（與近期歷史比對）：");
        foreach (var alert in record.TrendAlerts)
        {
            Console.WriteLine($"    - {alert}");
        }
        Console.ForegroundColor = original;
    }

    if (record.AiAnalyzed && (verbose || record.RiskLevel == "高" || criticalIssues.Count > 0 || record.TrendAlerts.Count > 0))
    {
        Console.WriteLine($"\n  白話說明：{record.Summary}");
        if (record.TrendAssessment.Length > 0)
        {
            Console.WriteLine($"  趨勢：{record.TrendAssessment}");
        }
        if (record.Action.Length > 0)
        {
            Console.WriteLine($"  現在該做：{record.Action}");
        }
    }
    else if (!record.AiAnalyzed && verbose)
    {
        Console.WriteLine($"  {record.Summary}");
    }

    // 有輸出風險報告時明確指引檔案位置，讓使用者知道去哪看細節
    if (record.ReportFile != null)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n  📄 詳細風險報告（含 AI 深入分析與原始 log）：{record.ReportFile}");
        Console.ForegroundColor = original;
    }
}
