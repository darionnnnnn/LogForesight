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
        if (!options.Enabled) return;
        if (_runState.IsRunning) return; // 手動觸發正在跑，本次輪詢略過，不排隊

        var now = DateTime.Now;
        var recentScheduleTriggerTimes = _batchRunStore
            .GetRecentRuns(RecentRunsLookbackDays, null)
            .Where(r => r.Trigger == "schedule")
            .Select(r => r.StartedAt);

        if (!ScheduleCalculator.ShouldTriggerNow(now, options.Windows, recentScheduleTriggerTimes)) return;

        var request = new RunRequest { Scope = RunScope.Full, Trigger = "schedule", DebugDump = options.DebugDump };
        await TriggerRunAsync(request);
    }

    /// <summary>
    /// 觸發一次執行（排程輪詢與手動觸發 API 共用）。已有執行中時回 false（拒絕，不排隊）；
    /// 跨行程 Mutex 逾時內拿不到（有 console 執行個體正在跑）也回 false。
    /// </summary>
    public async Task<bool> TriggerRunAsync(RunRequest request)
    {
        if (!_runState.TryBeginRun(request.Trigger ?? "manual", out var runCts))
        {
            return false;
        }

        try
        {
            var acquired = await _mutexGate.RunExclusiveAsync(async () =>
            {
                var settings = BuildAppSettings();
                var dataRoot = settings.Storage.ResolveDataRoot();
                var retention = RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _systemSettingsStore);

                var orchestrator = new AnalysisOrchestrator();
                var console = new WebRunConsole(_runState);
                var result = await orchestrator.RunAsync(request, settings, dataRoot, retention, console, runCts.Token);

                if (!result.Success)
                    Log.Warn("觸發來源 {Trigger} 的執行未成功：{Message}", request.Trigger, result.FailureMessage);
            }, MutexTimeout);

            if (!acquired)
            {
                Log.Warn("取得執行鎖逾時（可能有 console 執行個體正在跑），本次觸發（{Trigger}）略過。", request.Trigger);
            }
            return acquired;
        }
        finally
        {
            _runState.EndRun();
        }
    }

    private AppSettings BuildAppSettings() => new()
    {
        Storage = _webSettings.Storage,
        Ai = _webSettings.Ai,
        Analysis = _webSettings.Analysis,
        Permissions = _webSettings.Permissions
    };
}
