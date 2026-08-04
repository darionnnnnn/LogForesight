using LogForesight.Web.Configuration;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 排程引擎（docs/WEB-SCHEDULER-PLAN.md §1.4.3／§1.4.4）：週期輪詢
/// <see cref="IScheduleOptionsStore"/>，命中執行窗口且該窗口本次尚未觸發過
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
    private readonly IScheduleOptionsStore _scheduleOptionsStore;
    private readonly ISystemSettingsStore _systemSettingsStore;
    private readonly BatchRunStore _batchRunStore;
    private readonly SchedulerRunState _runState;
    private readonly NamedMutexGate _mutexGate = new();

    public SchedulerHostedService(
        WebAppSettings webSettings,
        IScheduleOptionsStore scheduleOptionsStore,
        ISystemSettingsStore systemSettingsStore,
        BatchRunStore batchRunStore,
        SchedulerRunState runState,
        IHostApplicationLifetime lifetime)
    {
        _webSettings = webSettings;
        _scheduleOptionsStore = scheduleOptionsStore;
        _systemSettingsStore = systemSettingsStore;
        _batchRunStore = batchRunStore;
        _runState = runState;

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
        var options = _scheduleOptionsStore.Get();

        if (_runState.IsRunning)
        {
            // 窗口 End 到點（docs/WEB-SCHEDULER-PLAN.md §1.4.3）：對「排程觸發」的進行中執行發
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
    public async Task<bool> TriggerRunAsync(RunRequest request)
    {
        // AI 診斷傾印一律以目前的排程設定為準（docs/WEB-SCHEDULER-PLAN.md §1.4.10）：
        // 排程輪詢與手動觸發共用同一個開關，這裡統一覆寫呼叫端傳入的值，
        // 不然「排程開了傾印、手動觸發卻沒開」的不一致會讓人以為傾印沒生效。
        var effectiveRequest = new RunRequest
        {
            Scope = request.Scope,
            HostIds = request.HostIds,
            BackfillOverride = request.BackfillOverride,
            DebugDump = _scheduleOptionsStore.Get().DebugDump,
            Args = request.Args,
            Trigger = request.Trigger
        };

        if (!_runState.TryBeginRun(effectiveRequest.Trigger ?? "manual", out var runCts))
        {
            return false;
        }

        // 只回報「有沒有真的開始」：進到 mutex 保護區＝開始（set true）；逾時拿不到鎖＝沒開始（set false）
        var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            try
            {
                var acquired = await _mutexGate.RunExclusiveAsync(async () =>
                {
                    startSignal.TrySetResult(true);

                    var settings = BuildAppSettings();
                    var dataRoot = settings.Storage.ResolveDataRoot();
                    var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _systemSettingsStore);

                    var orchestrator = new AnalysisOrchestrator();
                    var console = new WebRunConsole(_runState);
                    var result = await orchestrator.RunAsync(effectiveRequest, settings, dataRoot, retention, console, runCts.Token);

                    if (!result.Success)
                        Log.Warn("觸發來源 {Trigger} 的執行未成功：{Message}", effectiveRequest.Trigger, result.FailureMessage);
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
            }
            finally
            {
                _runState.EndRun();
            }
        });

        return await startSignal.Task;
    }

    private AppSettings BuildAppSettings() => new()
    {
        Storage = _webSettings.Storage,
        Ai = _webSettings.Ai,
        Analysis = _webSettings.Analysis,
        Permissions = _webSettings.Permissions
    };
}
