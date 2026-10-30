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
    private readonly BatchRunStore _batchRuns;
    private readonly DailyRecordBackfiller _backfiller;
    private readonly ISuppressionStore? _suppressionStore;
    private readonly IAiService? _injectedAiService;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly HostDayWorkflowService? _workflow;
    private readonly IWebAiService _webAi;
    private readonly IAiRequestGate? _aiGate;

    public AiAnalysisHostedService(
        ScheduleOptionsStore optionsStore,
        SchedulerRunState schedulerRunState,
        AiAnalysisRunState runState,
        IAnalysisRecordQuery recordQuery,
        StorageBackend storageBackend,
        ISystemSettingsStore systemSettingsStore,
        DataVersionStamp dataVersion,
        BatchRunStore batchRuns,
        DailyRecordBackfiller backfiller,
        IWebAiService webAi,
        ISuppressionStore? suppressionStore = null,
        IAiService? aiService = null,
        IHostApplicationLifetime? lifetime = null,
        HostDayWorkflowService? workflow = null,
        IAiRequestGate? aiGate = null)
    {
        _webAi = webAi;
        _optionsStore = optionsStore;
        _schedulerRunState = schedulerRunState;
        _runState = runState;
        _dataVersion = dataVersion;
        _recordQuery = recordQuery;
        _storageBackend = storageBackend;
        _systemSettingsStore = systemSettingsStore;
        _batchRuns = batchRuns;
        _backfiller = backfiller;
        _suppressionStore = suppressionStore;
        _injectedAiService = aiService;
        _lifetime = lifetime;
        _workflow = workflow;
        _aiGate = aiGate;

        // 站台關閉時對進行中的 AI 執行發出優雅停止
        _lifetime?.ApplicationStopping.Register(() => _runState.TryCancel());

        // 取數執行一發佈當日 PRTG finding 就立刻開跑（回饋第 40 輪批次E）：
        // 不等自己下一輪 60 秒輪詢，使用者按下「立即執行」後兩邊才是真的同時在動。
        _schedulerRunState.PrtgFindingsBecameReady += OnFetchFollowUp;
        _schedulerRunState.RunEnded += OnFetchFollowUp;
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

    /// <summary>
    /// 取數發佈 PRTG finding 或取數結束時的即時觸發（跟隨取數訊號來源：PrtgFindingsBecameReady 與 RunEnded）。
    ///
    /// 這裡刻意**不看 AI 執行窗口**：跟隨取數是「取數跑到哪、判讀跟到哪」，
    /// 窗口是給背景消化積壓用的。窗口內外都要跟上，否則使用者手動執行一趟，
    /// AI 得等到窗口開了才動。
    /// 其餘前置條件（AI 已設定、存量校正完成、有待補、沒有正在跑）照樣要成立。
    /// </summary>
    private void OnFetchFollowUp()
    {
        // 事件在取數執行的執行緒上發出，這裡不能同步等 AI 跑完。
        _ = Task.Run(async () =>
        {
            try
            {
                if (_runState.IsRunning) return;
                // 取數剛寫入新資料：待補件數不能沿用取數前留下的 30 秒快取
                _runState.InvalidatePendingAiCache();
                // 前置條件與輪詢同一份判定（docs/WEB-SPEC.md §9.10「閒置原因」），只差不看窗口
                var reason = BlockingIdleReason(DateTime.Now, _optionsStore.Get(), () => _runState.GetPendingAiCount(() => _recordQuery.CountPendingAi()),
                    out _, requireWindow: false);
                _runState.SetIdleReason(reason);
                if (reason != null) return;
                await TriggerRunAsync(forceRerun: false, trigger: BatchRunTriggers.FetchFollowUp);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PRTG finding 就緒後的 AI 即時觸發失敗（背景輪詢仍會接手）");
            }
        });
    }

    /// <summary>單次輪詢檢查與觸發，供常態輪詢與單元測試直接呼叫</summary>
    internal async Task TickAsync(CancellationToken stoppingToken = default)
    {
        var options = _optionsStore.Get();

        if (_runState.IsRunning)
        {
            // 窗口 End 到點：對排程自動觸發的進行中執行發優雅停止（停在單筆邊界）。
            // 手動觸發不受窗口限制。
            if (_runState.Trigger == BatchRunTriggers.Schedule &&
                !ScheduleCalculator.IsWithinAnyWindow(DateTime.Now, options.AiWindows) &&
                _runState.TryCancel())
            {
                Log.Info("AI 執行窗口已結束，對自動觸發的 AI 執行發出優雅停止（停在單筆邊界）。");
            }
            return;
        }

        // 跟隨取數期間資料持續落地，待補件數要實查；閒置的背景輪詢才沿用狀態 API 的 30 秒快取
        if (_schedulerRunState.IsFollowingFetch(DateTime.Now)) _runState.InvalidatePendingAiCache();
        var reason = BlockingIdleReason(DateTime.Now, options, () => _runState.GetPendingAiCount(() => _recordQuery.CountPendingAi()), out var inWindow);
        if (reason != null)
        {
            _runState.SetIdleReason(reason);
            return;
        }

        _runState.SetIdleReason(null);

        await TriggerRunAsync(forceRerun: false, trigger: inWindow ? BatchRunTriggers.Schedule : BatchRunTriggers.FetchFollowUp, externalCt: stoppingToken);
    }

    /// <summary>
    /// 自動開跑前置條件的唯一判定（輪詢與狀態 API 共用，docs/WEB-SPEC.md §9.10「閒置原因」）。
    /// 回 null＝條件都成立、可以開跑；<paramref name="inWindow"/> 供輪詢決定觸發來源。
    /// 待補件數延後到真的需要時才查（AI 未設定時不必碰資料庫）。
    /// </summary>
    private string? BlockingIdleReason(DateTime now, ScheduleOptions options, Func<int> pendingCount, out bool inWindow,
        bool requireWindow = true)
    {
        inWindow = false;
        // AI 沒有獨立的啟用開關：服務設定好就一律啟用（回饋第 40 輪批次E）。
        if (!_webAi.Available) return AiIdleReasons.Disabled;

        // 存量校正回填完成前不自動開跑（體檢輪）：ExtractVersion 推進後 ai_pending 的存量
        // 尚未校正，這時掃到的「待補」大量是早已分析完的舊列——搶跑等於把整庫重跑一遍，
        // 正是存量校正要避免的事。手動觸發不受此限（使用者自行判斷）。
        if (!_backfiller.Progress.Completed) return AiIdleReasons.BackfillPending;

        // 跟隨取數的即時觸發（OnFetchFollowUp）不看窗口：取數跑到哪、判讀跟到哪
        if (requireWindow)
        {
            inWindow = ScheduleCalculator.IsWithinAnyWindow(now, options.AiWindows);
            if (!inWindow && !_schedulerRunState.IsFollowingFetch(now)) return AiIdleReasons.OutsideWindow;
        }

        // 有待補資料才自動開跑
        return pendingCount() == 0 ? AiIdleReasons.NoPending : null;
    }

    /// <summary>
    /// 狀態 API 讀取當下的閒置原因（執行中回 null）。不用上一輪輪詢留下的值：輪詢 60 秒一次，
    /// 中間條件變了（例如存量校正剛完成）畫面就會同時顯示互相矛盾的兩句話。
    /// 「等取數」不擋開跑（較舊的待補照樣處理），所以只在其他條件都成立時才報。
    /// </summary>
    public string? CurrentIdleReason(Func<int> pendingCount)
    {
        if (_runState.IsRunning) return null;
        var reason = BlockingIdleReason(DateTime.Now, _optionsStore.Get(), pendingCount, out _);
        return reason ?? (_schedulerRunState.IsWaitingForFindings() ? AiIdleReasons.WaitingFetch : null);
    }

    /// <summary>
    /// 觸發 AI 分析執行。支援手動觸發與自動輪詢觸發，可指定是否強制重新分析。
    /// 回傳 true 代表成功開始執行，false 代表目前已有其他執行進行中。
    /// </summary>
    public async Task<bool> TriggerRunAsync(bool forceRerun = false, string trigger = BatchRunTriggers.Schedule, CancellationToken externalCt = default)
    {
        if (forceRerun && _runState.IsRunning)
        {
            // 執行順序固定：優雅停止當前 AI 執行 → 取得 gate → 整批重標 → 重新開始。
            // 停止在前是消滅競態（正在分析的那筆若在重標後完成，AttachAiResult 會把重標
            // 洗掉，變成「用舊規則的結果卻標成已完成」）；gate 在重標**之前**是避免副作用
            // 先落地（體檢輪）：若先重標再搶 gate 而被別的觸發搶走，API 回「已有執行進行中」，
            // 但整庫已被重標、幾十萬列的重跑已成定局。
            _runState.TryCancel();
            await _runState.WaitForCompletionAsync(TimeSpan.FromSeconds(10));
        }

        var totalPending = _recordQuery.CountPendingAi();
        if (!_runState.TryBeginRun(trigger, totalPending, out var runCts))
        {
            return false;
        }

        if (forceRerun)
        {
            // gate 已在手上才重標；重標後的實際待補總數由處理迴圈每批的 CountPendingAi 更新進度
            BatchResetAiPending();
        }

        var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            startSignal.TrySetResult(true);
            bool success = true;
            bool stopped = false;
            string? failureMessage = null;
            var counters = new LoopCounters();

            // AI 執行也進執行紀錄（批次D 定案）：逐筆視角可辨識「AI 分析」作業與其件數；
            // 主機×日總表以 JobType 排除它（RunMonitorService）。
            var args = new[] { forceRerun ? "ai --force-rerun" : "ai" };
            using BatchRunRecorder recorder = new(_batchRuns, Environment.MachineName, args, trigger, runCts.Token, jobType: BatchRun.JobTypeAi);
            _runState.SetCurrentBatchRunId(recorder.RunId);

            try
            {
                await ExecuteProcessingLoopAsync(runCts.Token, counters);
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested)
            {
                failureMessage = "執行已停止（優雅停止）。";
                success = false;
                stopped = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AI 分析排程執行時發生未預期錯誤：{Msg}", ex.Message);
                failureMessage = ex.Message;
                success = false;
            }
            finally
            {
                // ExecuteProcessingLoopAsync handles cancellation at the parallel/item boundary and
                // may return normally after observing the run token. Treat that as a stopped run,
                // while leaving completed results intact and unfinished records pending.
                if (runCts.IsCancellationRequested)
                {
                    failureMessage = "執行已停止（優雅停止）。";
                    success = false;
                    stopped = true;
                }

                recorder.DaysAnalyzed = counters.Done;
                // 實際呼叫過 AI 的筆數：成功且非 Skipped（Skipped 不呼叫 AI）加上失敗者
                recorder.AiCalls = counters.Done - counters.Skipped + counters.Failed;
                recorder.AiFailures = counters.Failed;
                recorder.Stopped = stopped;
                recorder.Finish(success ? 0 : 1);

                _runState.EndRun(success, failureMessage);

                // 本輪結束後若取數仍在跑、當日 PRTG finding 又還沒到齊，剩下的待補是被
                // 完整性閘門擋住的（見 ExecuteProcessingLoopAsync）。「有待補卻不動」最像壞掉，
                // 畫面要說得出原因。
                if (_schedulerRunState.IsWaitingForFindings())
                {
                    _runState.SetIdleReason(AiIdleReasons.WaitingFetch);
                }
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
    /// 2. 近日層新→舊、積壓層七日一塊、塊由新到舊、塊內同主機舊→新（<see cref="SelectNextBatch"/>）
    /// 3. 不設回望天數上限，每批重新選（批次大小 50）
    /// 4. 依 AiConcurrency 平行處理，同主機序列（按主機分片、片內序列、片間平行）
    /// 5. 失敗維持 ai_pending = true，不中斷整批
    /// </summary>
    /// <summary>處理迴圈的成果計數（執行紀錄用：完成件數／失敗件數）</summary>
    internal sealed class LoopCounters
    {
        public int Done;
        public int Failed;
        /// <summary>低風險不需 AI、已清除待補的件數（已含在 <see cref="Done"/> 內）</summary>
        public int Skipped;
    }

    internal Task ExecuteProcessingLoopAsync(CancellationToken ct) => ExecuteProcessingLoopAsync(ct, new LoopCounters());

    /// <summary>近日層：紀錄日 ≥ 今天減這麼多天的待補屬於「跟隨取數」，日期新→舊。</summary>
    internal const int RecentLayerDays = 2;

    /// <summary>積壓層一塊的天數：塊由新到舊、塊內同主機舊→新（docs/WEB-SPEC.md §9.10）。</summary>
    internal const int BacklogBlockDays = 7;

    /// <summary>單次查詢的列數上限：本輪失敗者仍待補，查詢要多取這麼多列才能越過它們。</summary>
    private const int MaxQueryBatchSize = 1000;

    /// <summary>下一批要處理的待補與它的處理方向（true＝同主機舊→新）。</summary>
    internal sealed record PendingBatch(IReadOnlyList<DailyAnalysisRecord> Records, bool OldestFirst);

    private static (long HostId, string Host, DateTime Date) AttemptKey(DailyAnalysisRecord r) => (r.HostId, r.Host, r.Date.Date);

    /// <summary>
    /// 選下一批待補（每批重新計算，近日層新進的待補下一批就能插隊）：
    /// 1. **完整性閘門**：日期 ≥ <see cref="SchedulerRunState.FindingsWaitCutoff"/> 的不能判讀——PRTG finding
    ///    會影響日風險與 AI 敘述，太早判讀等於讓 AI 看缺了 PRTG 訊號的半份資料。finding 一發佈或取數未執行時
    ///    cutoff 為 null（全部合格），AI 因此能與取數並行。
    /// 2. **近日層**（今天減 <see cref="RecentLayerDays"/> 天以內）日期新→舊：取數跑到哪、判讀跟到哪。
    /// 3. **積壓層**：最新一筆合格待補所在的七日塊，塊內同主機舊→新——prompt 的【近期歷史】會引用
    ///    前一天的 AI 結論，舊的先寫回，新的才看得到；塊由新到舊，強制重新分析時最近幾週先完成。
    /// 本輪已嘗試而失敗的待補不再選（同一輪內不空轉，跨輪重試由下次輪詢重開一輪自然發生）。
    /// </summary>
    internal PendingBatch? SelectNextBatch(IReadOnlySet<(long HostId, string Host, DateTime Date)> attempted)
    {
        var cutoff = _schedulerRunState.FindingsWaitCutoff()?.Date;
        var recentStart = DateTime.Today.AddDays(-RecentLayerDays);
        var querySize = Math.Min(BatchSize + attempted.Count, MaxQueryBatchSize);

        List<DailyAnalysisRecord> Eligible(PendingAiQuery query) =>
            _recordQuery.QueryPendingAi(querySize, query).Where(r => !attempted.Contains(AttemptKey(r))).ToList();

        if (cutoff is not { } blocked || blocked > recentStart)
        {
            var recent = Eligible(new PendingAiQuery(From: recentStart, ToExclusive: cutoff));
            if (recent.Count > 0) return new PendingBatch(recent.Take(BatchSize).ToList(), OldestFirst: false);
        }

        var backlogEnd = cutoff is { } c && c < recentStart ? c : recentStart;
        var anchor = Eligible(new PendingAiQuery(ToExclusive: backlogEnd)).FirstOrDefault();
        if (anchor == null) return null;

        var blockEnd = anchor.Date.Date.AddDays(1);
        var block = Eligible(new PendingAiQuery(From: blockEnd.AddDays(-BacklogBlockDays), ToExclusive: blockEnd, OldestFirst: true));
        return new PendingBatch(block.Take(BatchSize).ToList(), OldestFirst: true);
    }

    internal async Task ExecuteProcessingLoopAsync(CancellationToken ct, LoopCounters counters)
    {
        int totalDone = 0;

        // 本輪已嘗試而未成功的主機日（體檢輪）：失敗紀錄保持 pending，不記住的話每撈一批都會把
        // 同一批失敗者原樣重試。成功者寫回後已不是待補，從集合移除，查詢只需多取失敗者的列數。
        var attempted = new HashSet<(long HostId, string Host, DateTime Date)>();

        // 一輪只解析一次 AI 服務與主機作業系統對照（閘道已負責節流）。
        // 設定在輪中變更不即時生效，下一輪才採用；也不在每個分片、每批重讀主機表。
        var aiService = _injectedAiService ?? ResolveAiService();
        var hostOsMap = BuildHostOsMap();

        // 待補總數：只在迴圈開頭實查一次，之後以「本批前總數 - 本批完成數」估算
        // （CountPendingAi 在實機要 7～15 秒，不再每批查兩次）。
        int? pendingEstimate = null;

        while (!ct.IsCancellationRequested)
        {
            var options = _optionsStore.Get();
            var concurrency = Math.Clamp(options.AiConcurrency, 1, 8);

            PendingBatch? next;
            lock (attempted) { next = SelectNextBatch(attempted); }
            if (next == null || next.Records.Count == 0)
            {
                // 全庫已無待補，或剩餘待補要嘛在取數當前處理範圍內、要嘛本輪已嘗試失敗——下次輪詢再重試
                var remaining = _recordQuery.CountPendingAi();
                if (remaining > 0)
                    Log.Info("剩餘待補（{Count} 筆）皆為取數處理範圍內或本輪已嘗試失敗者，本輪 AI 分析結束。", remaining);
                break;
            }

            // 併發：依 AiConcurrency 平行處理，同一台主機必須序列（按主機分片、片內序列、片間平行），
            // 片內方向跟著所屬層（見 SelectNextBatch）。
            var oldestFirst = next.OldestFirst;
            var groups = next.Records
                .GroupBy(r => r.HostId != 0 ? r.HostId.ToString() : r.Host)
                .Select(g => oldestFirst ? g.OrderBy(r => r.Date).ToList() : g.OrderByDescending(r => r.Date).ToList());
            // 每片的第一筆就是該主機在這批最先處理的那天
            var hostGroups = (oldestFirst ? groups.OrderBy(g => g[0].Date) : groups.OrderByDescending(g => g[0].Date)).ToList();

            var pendingTotal = pendingEstimate ??= _recordQuery.CountPendingAi();
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
                    var analysisService = CreateAnalysisServiceForHost(hostKey, hostStore, aiService, hostOsMap);

                    // 片內序列：方向已在分片時決定（近日層新→舊、積壓層舊→新）
                    foreach (var record in group)
                    {
                        if (token.IsCancellationRequested) break;
                        lock (attempted) { attempted.Add(AttemptKey(record)); }

                        long workflowVersion = 0;
                        try
                        {
                            // 補跑輸入必須是**完整紀錄**（體檢輪）：QueryPendingAi 是不讀 ContentJson
                            // 的輕量投影——TrendAlerts／AuditEventCount 沒填、CorrelationAlerts 是
                            // "(lightweight)" 佔位字串，直接餵 RetryAiAsync 會把佔位字串當成
                            // 高嚴重度關聯訊號組進 prompt、趨勢警示整批消失。輕量投影只做
                            // 佇列與排序，進到要花一次 AI 呼叫的這裡，多讀一列完整資料不算成本。
                            var full = _recordQuery.GetOne(new[] { hostKey }, record.Date);
                            if (full == null || full.DetailPruned || full.RiskReview != null ||
                                full.TopIssues.Any(LogForesight.Core.Service.PrtgFindingMapper.IsPrtg) && !full.CanSupplementWithPrtg())
                            {
                                // 紀錄已消失（保留期清理）或詳情已清（AI 輸入無法重建）：跳過，
                                // 查詢端已排除 detail_pruned，這裡是取到殘根時的防禦
                                Log.Warn("主機 {Host} {Date:yyyy-MM-dd} 的完整紀錄不可得（已清理），跳過 AI 補跑", record.Host, record.Date);
                                continue;
                            }

                            if (_workflow != null)
                            {
                                try
                                {
                                    _workflow.RestoreFromRecord(full, aiEnabled: true);
                                    workflowVersion = _workflow.BeginAi(record.HostId, record.Date);
                                }
                                catch (Exception workflowError)
                                {
                                    Log.Warn(workflowError, "AI workflow metadata recovery failed; source analysis remains available");
                                }
                            }

                            var outcome = await analysisService.RetryAiAsync(full, TrendWindowDays, token);
                            // Providers may finish or return a result after cancellation. Do not
                            // persist an in-flight result once the user has stopped this run.
                            token.ThrowIfCancellationRequested();

                            // AI 呼叫成功：寫回結果並清 ai_pending
                            if (outcome.AiAnalyzed || outcome.Skipped)
                            {
                                if (_workflow != null && workflowVersion > 0)
                                {
                                    try
                                    {
                                        var current = _recordQuery.GetOne(new[] { hostKey }, record.Date);
                                        if (current == null ||
                                            HostDayWorkflowFingerprint.ForRecord(current) != HostDayWorkflowFingerprint.ForRecord(full) ||
                                            !_workflow.IsCurrentAiVersion(record.HostId, record.Date, workflowVersion))
                                        {
                                            CompleteWorkflowAiAttempt(record.HostId, record.Date, workflowVersion,
                                                "AI 判定版本已更新，舊分析不再有效。");
                                            Log.Info("主機 {Host} {Date:yyyy-MM-dd} 的判定版本已更新，拒絕舊 AI 回寫", record.Host, record.Date);
                                            continue;
                                        }
                                    }
                                    catch (Exception workflowError)
                                    {
                                        CompleteWorkflowAiAttempt(record.HostId, record.Date, workflowVersion,
                                            workflowError.Message);
                                        Log.Warn(workflowError, "AI version guard failed; refusing to risk writing a stale result");
                                        continue;
                                    }
                                }
                                var expectedPrtgFingerprint = outcome.InputPrtgFingerprint ??
                                    HostDayWorkflowFingerprint.PrtgInputFingerprint(full);
                                var expectedDecisionFingerprint = HostDayWorkflowFingerprint.ForRecord(full);
                                if (!hostStore.TryAttachAiResult(record.Date, outcome, full.RecordId,
                                        expectedDecisionFingerprint, expectedPrtgFingerprint))
                                {
                                    CompleteWorkflowAiAttempt(record.HostId, record.Date, workflowVersion,
                                        "正式資料已變更，舊 AI 分析不再有效。");
                                    Log.Info("主機 {Host} {Date:yyyy-MM-dd} 的正式 PRTG 證據已變更，拒絕舊 AI 回寫",
                                        record.Host, record.Date);
                                    continue;
                                }
                                if (_workflow != null && workflowVersion > 0)
                                    _workflow.CompleteAi(record.HostId, record.Date, workflowVersion, success: true);
                                // 寫回後已不是待補；之後若因新證據重標，下一批照常再選得到
                                lock (attempted) { attempted.Remove(AttemptKey(record)); }
                                var doneNow = Interlocked.Increment(ref totalDone);
                                Interlocked.Increment(ref counters.Done);
                                Interlocked.Increment(ref batchSuccessCount);
                                if (outcome.Skipped) Interlocked.Increment(ref counters.Skipped);
                                // 用 Increment 的回傳值、且不碰分母：分片是並行的，
                                // 「讀分母再寫回」會讓兩條分片的數字互相蓋（見 ReportProgressDone）
                                _runState.ReportProgressDone(doneNow, $"主機 {record.Host} {record.Date:yyyy-MM-dd} AI 完成");
                            }
                            else
                            {
                                // AI 未成功：該筆維持 ai_pending = true（下輪重試），記警告，不得中斷整批
                                if (_workflow != null && workflowVersion > 0)
                                    _workflow.CompleteAi(record.HostId, record.Date, workflowVersion, success: false, outcome.Headline);
                                Interlocked.Increment(ref counters.Failed);
                                Log.Warn("主機 {Host} {Date:yyyy-MM-dd} AI 呼叫未成功（{Headline}），維持待補",
                                    record.Host, record.Date, outcome.Headline);
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            CompleteWorkflowAiAttempt(record.HostId, record.Date, workflowVersion, "cancelled");
                            break;
                        }
                        catch (Exception ex)
                        {
                            // AI 呼叫失敗：該筆維持 ai_pending = true（下輪重試），記警告，不得中斷整批。
                            CompleteWorkflowAiAttempt(record.HostId, record.Date, workflowVersion, ex.Message);
                            Interlocked.Increment(ref counters.Failed);
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

            // 失敗防線（與 attempted 集合互補）：本批全滅且總數未動時直接結束本輪，
            // 不必等 attempted 把候選濾乾才停。只有「本批全滅」才需要實查一次確認；
            // 有成功者就用估算值（本批前總數 - 本批完成數）當下一批的進度基準。
            if (batchSuccessCount == 0)
            {
                var remainingPending = _recordQuery.CountPendingAi();
                if (remainingPending == pendingTotal)
                {
                    Log.Warn("本批待補紀錄均未能成功完成 AI 分析，暫停本輪處理以防迴圈重試。");
                    break;
                }
                pendingEstimate = remainingPending;
            }
            else
            {
                // 估算歸零時下一批改實查，避免新進待補讓進度分母卡在 0
                var estimate = pendingTotal - batchSuccessCount;
                pendingEstimate = estimate > 0 ? estimate : null;
            }
        }

        if (counters.Skipped > 0)
            Log.Info("低風險 {Skipped} 筆不需 AI，已清除待補", counters.Skipped);
    }

    private void CompleteWorkflowAiAttempt(long hostId, DateTime date, long workflowVersion, string failure)
    {
        if (_workflow == null || workflowVersion <= 0) return;

        try
        {
            var current = _workflow.Get(hostId, date);
            if (current is not
                {
                    Parent: WorkflowLegState.Succeeded,
                    Ai: WorkflowLegState.Running
                } || current.DecisionVersion != workflowVersion || current.AiInputVersion != workflowVersion)
                return;

            var record = _recordQuery.GetOne(
                new[] { new HostKey { HostId = hostId, HostName = current.HostName } }, date);
            if (record == null || !record.AiPending) return;

            // Keep the workflow version fence: an obsolete attempt must never complete a newer decision.
            _workflow.CompleteAi(hostId, date, workflowVersion, success: false, failure: failure);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "無法收尾主機 {HostId} {Date:yyyy-MM-dd} 的 AI workflow 狀態", hostId, date);
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
    public int BatchResetAiPending()
    {
        try
        {
            var count = _recordQuery.MarkAllForAiRerun();
            _runState.InvalidatePendingAiCache();
            return count;
        }
        catch (Exception ex)
        {
            // 這裡在 TriggerRunAsync 的 try/finally 之外，EndRun 不會被呼叫；
            // 不在此釋放閘門會讓 IsRunning 永遠為 true，直到站台重啟。
            _runState.EndRun(false, $"強制重新分析的整批重標失敗：{ex.Message}");
            throw;
        }
    }

    /// <summary>一輪開始時建一次 HostId → 作業系統對照（C8b），分片不再各自重讀主機表</summary>
    private Dictionary<long, string> BuildHostOsMap()
    {
        var map = new Dictionary<long, string>();
        foreach (var h in new HostStore(_storageBackend.Blob("hosts")).GetAll())
            map[h.HostId] = h.Os;
        return map;
    }

    private LogAnalysisService CreateAnalysisServiceForHost(HostKey hostKey, IAnalysisRecordStore hostStore,
        IAiService aiService, IReadOnlyDictionary<long, string> hostOsMap)
    {
        var riskyEventStore = _storageBackend.RiskyEventStore();
        // 主機作業系統：取不到時預設 Windows（與舊行為一致）
        var hostOs = hostOsMap.TryGetValue(hostKey.HostId, out var os) && os != null ? os : WebHost.OsWindows;
        var reportService = new RiskReportService(aiService, _storageBackend.ReportStore(),
            ruleProvider: () => KnownIssueCatalog.ResolveRules(new KnownIssueRuleStore(_storageBackend.Blob("rules"))),
            hostOs: hostOs);
        // 包裝層包在外面（含 DI 注入的規則頁 store）：AI 補寫報告的已抑制段也要看得到靜音項目
        var suppressionStore = new MuteAwareSuppressionStore(
            _suppressionStore ?? new SuppressionStore(_storageBackend.Blob("suppressions")),
            new IssueOwnerStore(_storageBackend.Blob("issue_owners")));

        return new LogAnalysisService(
            new EventLogService(),
            aiService,
            hostStore,
            suppressionStore,
            serverDescription: "",
            reportService: reportService,
            host: hostKey.HostName,
            hostId: hostKey.HostId,
            riskyEventStore: riskyEventStore,
            hostOs: hostOs);
    }

    internal virtual IAiService ResolveAiService()
    {
        var settings = new AppSettings();
        RuntimeSettingsResolver.ApplySystemSettingsOverrides(settings, _systemSettingsStore);
        // 每輪處理只解析一次，該輪所有分片共用；跨實例的併發上限與互動優先由全站閘道負責
        return new AIService(settings.Ai, dumper: null, usageMeter: new AiUsageStore(_storageBackend.Blob(AiUsageStore.BlobKey)),
            gate: _aiGate);
    }
}
