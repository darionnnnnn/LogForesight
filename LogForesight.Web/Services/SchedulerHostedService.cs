using LogForesight.Web.Configuration;
using LogForesight.Web.Services.Mail;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 排程引擎（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.3／§1.4.4）：週期輪詢
/// <see cref="ScheduleOptionsStore"/>，命中執行窗口且該窗口本次尚未觸發過
/// （<see cref="ScheduleCalculator.ShouldTriggerNow"/>，同一函式天生涵蓋服務啟動時的漏跑補償——
/// 啟動後第一次輪詢就是在檢查「現在是否該觸發」，跟平常輪詢問的是同一個問題）時觸發一次
/// <see cref="AnalysisOrchestrator"/> 完整執行。也是手動觸發 API（<see cref="TriggerRunAsync"/>）
/// 的執行載體，兩種觸發來源共用同一個 <see cref="SchedulerRunState"/> 單一執行 gate與
/// <see cref="NamedMutexGate"/> 跨行程互斥。
///
/// <see cref="ScheduleOptions.Enabled"/>＝false（預設）時輪詢直接跳過，schtasks／console 手動
/// 執行行為不受影響——這是本功能上線時零行為變化的保證。
/// </summary>
public class SchedulerHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(5);

    /// <summary>排程觸發只看近 2 天的執行紀錄找「這個窗口本次是否已觸發」——跨午夜窗口最多回看到昨天，2 天足夠、不必整表掃</summary>
    private const int RecentRunsLookbackDays = 2;

    private readonly WebAppSettings _webSettings;
    private readonly ScheduleOptionsStore _scheduleOptionsStore;
    private readonly ISystemSettingsStore _systemSettingsStore;
    private readonly BatchRunStore _batchRunStore;
    private readonly SchedulerRunState _runState;
    private readonly AnalysisOrchestrator _orchestrator;
    private readonly NamedMutexGate _mutexGate;
    private readonly MailNotificationService _mail;
    private readonly IHostApplicationLifetime _lifetime;

    public SchedulerHostedService(
        WebAppSettings webSettings,
        ScheduleOptionsStore scheduleOptionsStore,
        ISystemSettingsStore systemSettingsStore,
        BatchRunStore batchRunStore,
        SchedulerRunState runState,
        AnalysisOrchestrator orchestrator,
        NamedMutexGate mutexGate,
        MailNotificationService mail,
        IHostApplicationLifetime lifetime)
    {
        _webSettings = webSettings;
        _scheduleOptionsStore = scheduleOptionsStore;
        _systemSettingsStore = systemSettingsStore;
        _batchRunStore = batchRunStore;
        _runState = runState;
        _orchestrator = orchestrator;
        _mutexGate = mutexGate;
        _mail = mail;
        _lifetime = lifetime;

        // 站台關閉時比照「使用者手動停止」：優雅停在主機日邊界，不留執行中的殘留紀錄
        lifetime.ApplicationStopping.Register(() => _runState.TryCancel());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓站台其餘啟動流程（規則庫初始化等，見 Program.cs「啟動時的資料準備」）先完成，避免搶跑
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "排程輪詢發生未預期錯誤（不影響下次輪詢）");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync()
    {
        // 每日／每週定時彙總（回饋十五輪批次D）：獨立於排程分析窗口之外，即使排程本身未啟用
        // 也照常檢查——通知的時間軸是「使用者想幾點收到摘要」，不是「排程窗口設在幾點」。
        // 內部自行 try/catch 到底且成功寄送與否都不影響下方的排程判斷，緊接著跑不需要額外保護。
        await _mail.CheckAndSendDailyWeeklyAsync(DateTime.Now);

        var options = _scheduleOptionsStore.Get();

        if (_runState.IsRunning)
        {
            // 窗口 End 到點（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.3）：對「排程觸發」的進行中執行發
            // 優雅停止（停在主機日邊界）。手動觸發不受時間窗限制（§1.4.4），不在這裡停。
            // 刻意放在 Enabled 檢查之前——執行中途被關掉 Enabled，這次排程執行仍該按窗口停。
            if (_runState.Trigger == "schedule" &&
                !ScheduleCalculator.IsWithinAnyWindow(DateTime.Now, options.Windows) &&
                _runState.TryCancel())
            {
                Log.Info("執行窗口已結束，對排程觸發的執行發出優雅停止（停在主機日邊界）。");
            }
            return; // 執行中不再觸發新的執行，不排隊
        }

        if (!options.Enabled) return;

        var now = DateTime.Now;
        var recentScheduleTriggerTimes = _batchRunStore
            .GetRecentRuns(RecentRunsLookbackDays, null)
            .Where(r => r.Trigger == "schedule")
            .Select(r => r.StartedAt);

        if (!ScheduleCalculator.ShouldTriggerNow(now, options.Windows, recentScheduleTriggerTimes)) return;

        await TriggerRunAsync(new RunRequest { Scope = RunScope.Full, Trigger = "schedule" });
    }

    /// <summary>
    /// 觸發一次執行（排程輪詢與手動觸發 API 共用）。已有執行中時回 false（拒絕，不排隊）；
    /// 跨行程 Mutex 逾時內拿不到（有 console 執行個體正在跑）也回 false。
    ///
    /// **只等到「確定開始」就返回**（最久 <see cref="MutexTimeout"/>）：分析本身在背景繼續，
    /// 進度由 <see cref="SchedulerRunState"/> 供狀態 API 輪詢。不能等整趟跑完——手動觸發的
    /// HTTP 請求會被掛住數小時，排程輪詢迴圈也會被卡死（窗口 End 的優雅停止就永遠輪不到）。
    /// 背景工作只碰 Singleton 依賴（store／設定／run state），與 NetiqProbeService 同一套作法。
    /// </summary>
    /// <summary>
    /// 把呼叫端的請求與目前排程設定合成實際執行用的請求（<see cref="TriggerRunAsync"/> 的純函式部分）。
    ///
    /// **新增 <see cref="RunRequest"/> 欄位時必須同步這裡**：這是逐欄重建而非複製，漏抄的欄位會
    /// 靜默失效——`OnlyMissingOrFailed` 曾因此從未生效（API 設了、稽核訊息也印了，執行端卻收不到）。
    /// 抽成獨立函式就是為了讓「欄位有沒有全部帶到」測得到。
    /// </summary>
    internal static RunRequest ComposeEffectiveRequest(RunRequest request, ScheduleOptions scheduleOptions) => new()
    {
        Scope = request.Scope,
        HostIds = request.HostIds,
        BackfillOverride = request.BackfillOverride,
        OnlyMissingOrFailed = request.OnlyMissingOrFailed,
        DebugDump = scheduleOptions.DebugDump,
        IncludeLocal = scheduleOptions.LocalAnalysisEnabled,
        Trigger = request.Trigger
    };

    public async Task<bool> TriggerRunAsync(RunRequest request)
    {
        // AI 診斷傾印一律以目前的排程設定為準（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.10）：
        // 排程輪詢與手動觸發共用同一個開關，這裡統一覆寫呼叫端傳入的值，
        // 不然「排程開了傾印、手動觸發卻沒開」的不一致會讓人以為傾印沒生效。
        // IncludeLocal 同一套作法（回饋十八輪批次D）：本機分析停用開關與排程設定同一生命週期，
        // 統一在這裡覆寫，不由呼叫端各自決定。
        var scheduleOptions = _scheduleOptionsStore.Get();
        var effectiveRequest = ComposeEffectiveRequest(request, scheduleOptions);

        if (!_runState.TryBeginRun(effectiveRequest.Trigger ?? "manual", out var runCts))
        {
            return false;
        }

        // 只回報「有沒有真的開始」：進到 mutex 保護區＝開始（set true）；逾時拿不到鎖＝沒開始（set false）
        var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            // 給使用者「看得見」的失敗回饋（docs/archive/FEEDBACK-7-PLAN.md）：原本失敗只寫 NLog，
            // 狀態 API／畫面只在 IsRunning=true 時顯示訊息，執行一旦炸掉，使用者看到的最後
            // 印象停留在「已開始執行」的 toast，只能翻 log 檔才知道其實失敗了。
            // null＝這次沒有真的開始過（mutex 逾時），不覆蓋上一筆真正跑過的結果。
            RunOutcome? outcome = null;
            try
            {
                var acquired = await _mutexGate.RunExclusiveAsync(async () =>
                {
                    startSignal.TrySetResult(true);

                    var settings = BuildAppSettings();
                    var dataRoot = settings.Storage.ResolveDataRoot();
                    var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _systemSettingsStore);

                    var console = new WebRunConsole(_runState);
                    var progress = new WebRunProgress(_runState);
                    var result = await _orchestrator.RunAsync(effectiveRequest, settings, dataRoot, retention, console, runCts.Token, progress);

                    if (!result.Success)
                        Log.Warn("觸發來源 {Trigger} 的執行未成功：{Message}", effectiveRequest.Trigger, result.FailureMessage);

                    // 有沒有任何主機日真的寫入分析結果（回饋十八輪批次B）：本機與 NetIQ 並行執行後
                    // （批次十七E），本機出問題會讓 result.Success=false（維持既有嚴格語意，見
                    // AnalysisOrchestrator 的說明），但 NetIQ 那一路可能已經對數百上千台主機完成
                    // 分析並寫入——下面的通知閘門要用這個欄位區分「整趟真的什麼都沒做」與
                    // 「一路失敗、另一路已有真實產出」，見 RunOutcome.AnyRecordsWritten 的說明。
                    outcome = new RunOutcome(result.Success, result.Success ? null : result.FailureMessage,
                        effectiveRequest.Trigger ?? "manual", DateTime.Now, RunOutcome.ComputeAnyRecordsWritten(result));
                }, MutexTimeout);

                if (!acquired)
                {
                    Log.Warn("取得執行鎖逾時（可能有 console 執行個體正在跑），本次觸發（{Trigger}）略過。", effectiveRequest.Trigger);
                    startSignal.TrySetResult(false);
                }
            }
            catch (Exception ex)
            {
                // orchestrator 內部已有全域 catch，會走到這裡的是執行環境層級的意外（儲存初始化失敗等）
                Log.Error(ex, "觸發來源 {Trigger} 的執行發生未預期錯誤", effectiveRequest.Trigger);
                startSignal.TrySetResult(false);
                outcome = new RunOutcome(false, ex.Message, effectiveRequest.Trigger ?? "manual", DateTime.Now);
            }
            finally
            {
                _runState.EndRun(outcome);
            }

            // 郵件通知（回饋十五輪批次D，回饋十六輪批次A-4 移出執行鎖）：執行摘要與高風險
            // 即時通知同一個掛載點。
            //
            // **閘門＝成功或有產出**（回饋十八輪批次B，取代原本「只在 Success 時判定」）：本機與
            // NetIQ 並行執行後，本機出問題會讓整趟 Success=false（維持批次E既有的嚴格語意），
            // 但 NetIQ 那一路（2000 台等級）可能已經完整跑完並寫入——原本的閘門會讓「本機這台
            // 環境性小問題」把已完成的全機房高風險通知一起靜音，且因為 UrgentSentKeys 沒被
            // 標記、隔天成功執行會補寄，症狀是「通知延遲一天」而非永久漏，難以察覺（見
            // docs/archive/FEEDBACK-18-PLAN.md 批次B）。改成「成功或有任何主機日寫入」就通知，
            // 執行監控頁的失敗訊號不受影響（outcome.Success 已經定案並交給上面的 EndRun）。
            //
            // **移出 RunExclusiveAsync 區塊、放在 EndRun 之後**（回饋十六輪體檢發現1）：分析結果
            // 已經落地，通知不需要持有執行鎖；原本寄信（可能上百封、每封 30 秒逾時）卡在鎖內，
            // 會讓 UI 的「執行中」狀態一路延伸到寄信結束，且鎖住排程/手動觸發整段期間。
            // 不能沿用 runCts.Token——EndRun 已經把它 Dispose 掉；改用站台停止權杖，
            // 站台正常關閉時通知會被取消（對稱於分析本身經 TryCancel 的優雅停止），
            // 平時 ApplicationStopping 未觸發，等同不取消。內部自行 try/catch 到底，
            // 不影響本次執行的成敗判定。
            if (outcome is { ShouldNotify: true })
            {
                await _mail.NotifyAfterRunAsync(_lifetime.ApplicationStopping);
            }
        });

        return await startSignal.Task;
    }

    /// <summary>
    /// 每次執行重建設定（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.2）。§12 起 Ai／Analysis／
    /// Permissions 不再來自 appsettings——這裡帶的是程式內建出廠值，實際生效值由
    /// <see cref="RuntimeSettingsResolver.ApplySystemSettingsOverrides"/> 從 DB（設定頁）疊上去；
    /// Storage 仍來自 appsettings（DB 的位置不可能存在 DB 裡）。
    /// </summary>
    private AppSettings BuildAppSettings() => new()
    {
        Storage = _webSettings.Storage
    };
}
