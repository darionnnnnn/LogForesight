using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// AI 分析排程背景服務（批次D2）：常駐輪詢，獨立於 NetIQ 取數排程之外。
/// 取數排程僅負責落地資料並標記 <c>ai_pending = true</c>，本服務常駐消化這些待補紀錄。
/// 自成一個併發 1 的 gate（<see cref="AiAnalysisRunState"/>），與取數排程互不互斥。
/// </summary>
public class AiAnalysisHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private const int TrendWindowDays = 14;
    private const int BatchSize = 50;

    private readonly ScheduleOptionsStore _optionsStore;
    private readonly SchedulerRunState _schedulerRunState;
    private readonly AiAnalysisRunState _runState;
    private readonly DataVersionStamp _dataVersion;
    private readonly IAnalysisRecordQuery _recordQuery;
    private readonly StorageBackend _storageBackend;
    private readonly ISystemSettingsStore _systemSettingsStore;
    private readonly ISuppressionStore? _suppressionStore;
    private readonly IAiService? _injectedAiService;
    private readonly IHostApplicationLifetime? _lifetime;

    public AiAnalysisHostedService(
        ScheduleOptionsStore optionsStore,
        SchedulerRunState schedulerRunState,
        AiAnalysisRunState runState,
        IAnalysisRecordQuery recordQuery,
        StorageBackend storageBackend,
        ISystemSettingsStore systemSettingsStore,
        DataVersionStamp dataVersion,
        ISuppressionStore? suppressionStore = null,
        IAiService? aiService = null,
        IHostApplicationLifetime? lifetime = null)
    {
        _optionsStore = optionsStore;
        _schedulerRunState = schedulerRunState;
        _runState = runState;
        _dataVersion = dataVersion;
        _recordQuery = recordQuery;
        _storageBackend = storageBackend;
        _systemSettingsStore = systemSettingsStore;
        _suppressionStore = suppressionStore;
        _injectedAiService = aiService;
        _lifetime = lifetime;

        // 站台關閉時對進行中的 AI 執行發出優雅停止
        _lifetime?.ApplicationStopping.Register(() => _runState.TryCancel());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等待站台初始化完成，避免搶跑
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
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AI 分析排程輪詢發生未預期錯誤（不影響下次輪詢）");
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

    /// <summary>單次輪詢檢查與觸發，供常態輪詢與單元測試直接呼叫</summary>
    internal async Task TickAsync(CancellationToken stoppingToken = default)
    {
        var options = _optionsStore.Get();

        if (_runState.IsRunning)
        {
            // 窗口 End 到點：對排程自動觸發的進行中執行發優雅停止（停在單筆邊界）。
            // 手動觸發不受窗口限制。
            if (_runState.Trigger == "schedule" &&
                !ScheduleCalculator.IsWithinAnyWindow(DateTime.Now, options.AiWindows) &&
                _runState.TryCancel())
            {
                Log.Info("AI 執行窗口已結束，對自動觸發的 AI 執行發出優雅停止（停在單筆邊界）。");
            }
            return;
        }

        if (!options.AiEnabled) return;

        // 必須在執行窗口內
        if (!ScheduleCalculator.IsWithinAnyWindow(DateTime.Now, options.AiWindows)) return;

        // 有待補資料才自動開跑
        var pendingCount = _recordQuery.CountPendingAi();
        if (pendingCount == 0) return;

        await TriggerRunAsync(forceRerun: false, trigger: "schedule", externalCt: stoppingToken);
    }

    /// <summary>
    /// 觸發 AI 分析執行。支援手動觸發與自動輪詢觸發，可指定是否強制重新分析。
    /// 回傳 true 代表成功開始執行，false 代表目前已有其他執行進行中。
    /// </summary>
    public async Task<bool> TriggerRunAsync(bool forceRerun = false, string trigger = "schedule", CancellationToken externalCt = default)
    {
        if (forceRerun)
        {
            // 執行順序固定：優雅停止當前 AI 執行 → 整批重標 → 重新開始。
            // 這個順序是用來消滅競態的（正在分析的那筆若在重標後完成，
            // AttachAiResult 會把重標洗掉，變成「用舊規則的結果卻標成已完成」）。
            if (_runState.IsRunning)
            {
                _runState.TryCancel();
                await _runState.WaitForCompletionAsync(TimeSpan.FromSeconds(10));
            }

            BatchResetAiPending();
        }

        var totalPending = _recordQuery.CountPendingAi();
        if (!_runState.TryBeginRun(trigger, totalPending, out var runCts))
        {
            return false;
        }

        var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            startSignal.TrySetResult(true);
            bool success = true;
            string? failureMessage = null;

            try
            {
                await ExecuteProcessingLoopAsync(runCts.Token);
            }
            catch (OperationCanceledException)
            {
                failureMessage = "執行已停止（優雅停止）。";
                success = false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AI 分析排程執行時發生未預期錯誤：{Msg}", ex.Message);
                failureMessage = ex.Message;
                success = false;
            }
            finally
            {
                _runState.EndRun(success, failureMessage);
                // AI 補寫改變了紀錄內容，儀表板／報表快取要失效（批次F）——背景執行不走
                // HTTP 管線，不會被那條中介軟體涵蓋。
                _dataVersion.Bump();
            }
        });

        return await startSignal.Task;
    }

    /// <summary>
    /// AI 批次處理主迴圈：
    /// 1. 完整性閘門篩選合格主機日
    /// 2. 日期新→舊順序處理
    /// 3. 不設回望天數上限，一批查完查下一批（批次大小 50）
    /// 4. 依 AiConcurrency 平行處理，同主機序列（按主機分片、片內序列、片間平行）
    /// 5. 失敗維持 ai_pending = true，不中斷整批
    /// </summary>
    internal async Task ExecuteProcessingLoopAsync(CancellationToken ct)
    {
        int currentQueryBatchSize = BatchSize;
        int totalDone = 0;

        while (!ct.IsCancellationRequested)
        {
            var options = _optionsStore.Get();
            var concurrency = Math.Clamp(options.AiConcurrency, 1, 8);

            var rawPending = _recordQuery.QueryPendingAi(currentQueryBatchSize);
            if (rawPending.Count == 0)
            {
                // 全庫已無待補
                break;
            }

            // 1. 完整性閘門：只處理「資料已完整落地」的主機日。
            // 判定方式：取數排程執行中（SchedulerRunState.IsRunning == true）時，跳過今天與昨天兩天的待補。
            // 理由：取數正在寫入的必然是最近的日期（今天與昨天）；既有 SchedulerRunState 僅記錄進度數值與執行中旗標，
            // 無主機日細部範圍，依規格退而求其次在取數執行中時跳過今天與昨天兩天的待補，取數完成後即可正常被撿到。
            // 不為此新增跨服務共享狀態物件。
            bool isSchedulerRunning = _schedulerRunState.IsRunning;
            var cutoff = DateTime.Today.AddDays(-1); // 昨天

            var eligible = isSchedulerRunning
                ? rawPending.Where(r => r.Date.Date < cutoff).ToList()
                : rawPending;

            if (eligible.Count == 0)
            {
                // 當前撈取的待補全為今天/昨天。若查詢筆數達上限，加大查詢批次以尋找更舊的合格待補
                if (rawPending.Count == currentQueryBatchSize && currentQueryBatchSize < 1000)
                {
                    currentQueryBatchSize += BatchSize;
                    continue;
                }

                // 全庫剩餘待補皆在取數窗口內，本輪暫停等待取數完成落地
                Log.Info("目前待補紀錄皆在取數排程當前處理範圍內（今天/昨天），本輪 AI 分析暫停等待資料落地。");
                break;
            }

            currentQueryBatchSize = BatchSize;
            var toProcess = eligible.Take(BatchSize).ToList();

            // 2. 通過閘門者依 QueryPendingAi 既有的日期新→舊順序處理。
            // 併發：依 AiConcurrency 平行處理，但同一台主機必須序列（按主機分片、片內序列、片間平行）。
            // 理由：同一主機的隔日分析會引用前一天的 AI 摘要。
            var hostGroups = toProcess
                .GroupBy(r => r.HostId != 0 ? r.HostId.ToString() : r.Host)
                .OrderByDescending(g => g.Max(r => r.Date))
                .ToList();

            var pendingTotal = _recordQuery.CountPendingAi();
            _runState.ReportProgress(totalDone, totalDone + pendingTotal, $"AI 白話分析補寫中（併發 {concurrency}）");

            int batchSuccessCount = 0;

            try
            {
                await Parallel.ForEachAsync(hostGroups, new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = ct
                }, async (group, token) =>
                {
                    var first = group.First();
                    var hostKey = new HostKey { HostId = first.HostId, HostName = first.Host };
                    var hostStore = _storageBackend.RecordStore(hostKey);
                    var analysisService = CreateAnalysisServiceForHost(hostKey, hostStore);

                    // 片內序列：同一主機依日期新→舊依序處理
                    foreach (var record in group.OrderByDescending(r => r.Date))
                    {
                        if (token.IsCancellationRequested) break;

                        try
                        {
                            var outcome = await analysisService.RetryAiAsync(record, TrendWindowDays, token);

                            // AI 呼叫成功：寫回結果並清 ai_pending
                            if (outcome.AiAnalyzed)
                            {
                                hostStore.AttachAiResult(record.Date, outcome);
                                Interlocked.Increment(ref totalDone);
                                Interlocked.Increment(ref batchSuccessCount);
                                _runState.ReportProgress(totalDone, _runState.ProgressTotal, $"主機 {record.Host} {record.Date:yyyy-MM-dd} AI 完成");
                            }
                            else
                            {
                                // AI 未成功：該筆維持 ai_pending = true（下輪重試），記警告，不得中斷整批
                                Log.Warn("主機 {Host} {Date:yyyy-MM-dd} AI 呼叫未成功（{Headline}），維持待補",
                                    record.Host, record.Date, outcome.Headline);
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            // AI 呼叫失敗：該筆維持 ai_pending = true（下輪重試），記警告，不得中斷整批。
                            Log.Warn(ex, "主機 {Host} {Date:yyyy-MM-dd} AI 分析失敗，維持待補：{Msg}",
                                record.Host, record.Date, ex.Message);
                        }
                    }
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            if (ct.IsCancellationRequested) break;

            // 失敗防線：若本批候選處理後成功數為 0，且待補總數未變化，避免無限死循環，暫停本輪等待下次輪詢
            var remainingPending = _recordQuery.CountPendingAi();
            if (remainingPending == pendingTotal && batchSuccessCount == 0)
            {
                Log.Warn("本批待補紀錄均未能成功完成 AI 分析，暫停本輪處理以防迴圈重試。");
                break;
            }
        }
    }

    /// <summary>
    /// 強制重新分析：把全庫紀錄整批重標 ai_pending = true。
    /// SQL 端整批 UPDATE，不逐筆讀寫、不重寫 ContentJson，不清空既有 AI 文字。
    /// </summary>
    /// <summary>
    /// 強制重新分析的整批重標。判準與 SQL 端整批 UPDATE 的理由見
    /// <see cref="IAnalysisRecordQuery.MarkAllForAiRerun"/>——持久層的事交給持久層，
    /// 這裡不直接碰 DbContext（StorageBackend 是本專案唯一的持久層路由點）。
    /// </summary>
    public int BatchResetAiPending() => _recordQuery.MarkAllForAiRerun();

    private LogAnalysisService CreateAnalysisServiceForHost(HostKey hostKey, IAnalysisRecordStore hostStore)
    {
        var aiService = _injectedAiService ?? ResolveAiService();
        var riskyEventStore = _storageBackend.RiskyEventStore();
        var reportService = new RiskReportService(aiService, _storageBackend.ReportStore());
        var suppressionStore = _suppressionStore ?? new SuppressionStore(_storageBackend.Blob("suppressions"));

        return new LogAnalysisService(
            new EventLogService(),
            aiService,
            hostStore,
            suppressionStore,
            serverDescription: "",
            reportService: reportService,
            host: hostKey.HostName,
            hostId: hostKey.HostId,
            riskyEventStore: riskyEventStore);
    }

    private IAiService ResolveAiService()
    {
        var settings = new AppSettings();
        RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _systemSettingsStore);
        return new AIService(settings.Ai, dumper: null, usageMeter: new AiUsageStore(_storageBackend.Blob(AiUsageStore.BlobKey)));
    }
}
