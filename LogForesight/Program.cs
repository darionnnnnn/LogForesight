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

// AI API 設定由執行檔目錄的 appsettings.json 載入。
// 設定檔存在但解析失敗＝致命錯誤（見 AppSettings.Load 的語意說明）：清楚印出原因與位置後
// 以非零 exit code 結束，讓工作排程器的失敗通知抓得到；不印堆疊（那對修 JSON 沒有幫助）。
AppSettings settings;
try
{
    settings = AppSettings.Load();
}
catch (AppSettingsLoadException ex)
{
    WithColor(ConsoleColor.Red, () => Console.WriteLine($"\n{ex.Message}"));
    log.Fatal("設定檔解析失敗，啟動中止：{Message}", ex.Message);
    LogManager.Shutdown();
    return 1;
}

if (!settings.Storage.IsValidType)
{
    WithColor(ConsoleColor.Red, () => Console.WriteLine(
        $"\nStorage:Type「{settings.Storage.Type}」不受支援，僅允許 " +
        $"{string.Join(" / ", StorageSettings.ValidTypes)}（Jsonl 檔案格式已於 2026-07-24 退役）。"));
    log.Fatal("Storage:Type 設定不合格，啟動中止：{Type}", settings.Storage.Type);
    LogManager.Shutdown();
    return 1;
}

// 資料根目錄：export\ 報告與 Sqlite 預設 db 檔位置的依據（Storage.ConnectionString 未設時
// 用 {DataRoot}\logforesight.db），Web 也讀同一個根。其餘資料（分析紀錄／規則／webdata）
// 已全數入庫，不再是這個目錄下的檔案。
// Storage:DataRoot 留空＝執行檔目錄（既有部署行為，逐位元組不變）；填了才把資料搬離建置輸出目錄。
var dataRoot = settings.Storage.ResolveDataRoot();
if (!string.Equals(dataRoot, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"資料根目錄：{dataRoot}");
    log.Info("資料根目錄（Storage.DataRoot）：{DataRoot}", dataRoot);
}

// 「系統管理 > 設定」頁（DB）是 AI 位址／金鑰與補充／留存天數的事實來源；appsettings.json 的
// Ai.BaseUrl 與內建預設值只是 DB 尚未設定時的退路（開箱即用，不強制先跑過 Web 設定頁）。
var retention = new RetentionOptions();
try
{
    var systemSettings = StorageFactory.CreateSystemSettingsStore(settings.Storage, dataRoot).Get();

    // 「從未在設定頁存過」（UpdatedAt==null）與「存過但刻意清空」要分開：前者沿用
    // appsettings 的值（既有部署升級後行為不變），後者空字串＝真的停用 AI（設定頁明講留空停用），
    // AI 呼叫失敗時各日自動降級為統計模式，規則/趨勢/關聯偵測不受影響
    if (systemSettings.UpdatedAt != null)
        settings.Ai.BaseUrl = systemSettings.AiBaseUrl.Trim();
    if (CryptoHelper.IsEncrypted(systemSettings.AiApiKeyEnc))
        settings.Ai.ApiKey = CryptoHelper.Decrypt(systemSettings.AiApiKeyEnc);

    if (systemSettings.RetentionDays >= systemSettings.InitialHistoryDays)
    {
        retention = retention with { InitialHistoryDays = systemSettings.InitialHistoryDays, RetentionDays = systemSettings.RetentionDays };
    }
    else
    {
        log.Warn("系統設定的歷史資料保留天數（{RetentionDays}）小於首次回補天數（{InitialHistoryDays}），改用內建預設值。",
            systemSettings.RetentionDays, systemSettings.InitialHistoryDays);
    }

    retention = retention with { RunLogRetentionDays = systemSettings.RunLogRetentionDays, AuditRetentionDays = systemSettings.AuditRetentionDays };

    if (systemSettings.RiskyEventRetentionDays >= 1 && systemSettings.RiskyEventRetentionDays <= retention.RetentionDays)
    {
        retention = retention with { RiskyEventRetentionDays = systemSettings.RiskyEventRetentionDays };
    }
    else
    {
        log.Warn("系統設定的風險 log 暫存保留天數（{RiskyEventRetentionDays}）超出合理範圍（1~{RetentionDays}），改用內建預設值。",
            systemSettings.RiskyEventRetentionDays, retention.RetentionDays);
    }
}
catch (Exception ex)
{
    log.Warn(ex, "讀取系統設定（AI 位址／金鑰／補充留存天數）失敗，改用內建預設值：{0}", ex.Message);
}

Console.WriteLine($"AI API：{settings.Ai.BaseUrl}（逾時 {settings.Ai.TimeoutSeconds} 秒，失敗重試 {settings.Ai.RetryCount} 次）");
log.Info("AI 設定：BaseUrl={BaseUrl}, Timeout={Timeout}s, RetryCount={RetryCount}, JsonRetryCount={JsonRetryCount}, " +
         "MaxTokens={MaxTokens}, DeepDiveMaxTokens={DeepDiveMaxTokens}, FrequencyPenalty={FrequencyPenalty}, PresencePenalty={PresencePenalty}",
    settings.Ai.BaseUrl, settings.Ai.TimeoutSeconds, settings.Ai.RetryCount, settings.Ai.JsonRetryCount,
    settings.Ai.MaxTokens, settings.Ai.DeepDiveMaxTokens, settings.Ai.FrequencyPenalty, settings.Ai.PresencePenalty);

if (debugDump)
{
    Console.WriteLine("  🔍 --debug-dump 模式：完整 prompt 與 AI 回應將輸出到 diag\\ 目錄");
}

// --import-rules：手動把內建種子的新增/修訂規則匯入 rules.json（預設只預覽，--apply 才寫檔），
// 見 docs/RULES-PLAN.md「初次部署寫入、後續手動匯入」的決定。放在 mutex 保護內執行，
// 避免與排程執行中的分析流程同時寫規則檔；跑完直接結束，不進入每日分析流程。
if (args.Contains("--import-rules"))
{
    var ruleStoreForCli = StorageFactory.CreateRuleStore(settings.Storage, dataRoot);
    RuleImporter.Run(ruleStoreForCli, apply: args.Contains("--apply"), overwriteBuiltin: args.Contains("--overwrite-builtin"));
    LogManager.Shutdown();
    return 0;
}

// --netiq-probe：對已設定的 Sentinel 跑一組小規模驗證查詢，輸出可貼回對話定案欄位對應
// （docs/NETIQ-API-PLAN.md §3.5、§8 步驟 2 的閘門）。放在 mutex 保護內、跑完即結束，不進入每日分析流程。
// 可選 --sample-ip <windows主機IP> --sample-linux-ip <linux主機IP> 加跑第二輪。
if (args.Contains("--netiq-probe"))
{
    var sentinelStoreForProbe = StorageFactory.CreateSentinelStore(settings.Storage, dataRoot);
    var netiqOptionsForProbe = StorageFactory.CreateNetiqOptionsStore(settings.Storage, dataRoot).Get();
    var probeSampleIp = GetArgValue(args, "--sample-ip");
    var probeSampleLinuxIp = GetArgValue(args, "--sample-linux-ip");
    var probeExitCode = await NetiqProbeCli.RunAsync(sentinelStoreForProbe, netiqOptionsForProbe, probeSampleIp, probeSampleLinuxIp);

    LogManager.Shutdown();
    return probeExitCode;
}

// 主流程（權限檢查 → 清理 → 本機分析 → NetIQ → 體檢）已抽為 Core 的 AnalysisOrchestrator
// （docs/WEB-SCHEDULER-PLAN.md §1.4.2），console 與 Web 排程共用同一份。
var orchestrator = new AnalysisOrchestrator();
var runRequest = new RunRequest { Scope = RunScope.Full, DebugDump = debugDump, Args = args };
var result = await orchestrator.RunAsync(runRequest, settings, dataRoot, retention, new ConsoleRunConsole(), CancellationToken.None);

LogManager.Shutdown(); // 確保緩衝的 log 都寫入檔案再結束程序
return result.Success ? 0 : 1;

/// <summary>取 --flag value 形式的參數值；flag 不存在或後面沒有值時回傳 null</summary>
static string? GetArgValue(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

/// <summary>暫時切換 console 前景色執行一段輸出，結束後還原——取代原本重複多次的存/切/還原三行式</summary>
static void WithColor(ConsoleColor color, Action write)
{
    var original = Console.ForegroundColor;
    Console.ForegroundColor = color;
    try { write(); }
    finally { Console.ForegroundColor = original; }
}
