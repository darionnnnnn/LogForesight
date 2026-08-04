using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// 機房分析 pipeline（docs/archive/HISTORY.md 決策 B2、§4；docs/BACKLOG.md Phase 4）：
/// 逐 Sentinel、逐日、批次向 Sentinel 取事件，映射後餵進與本機路徑完全相同的
/// <see cref="LogAnalysisService"/>——五層偵測/AI/報告零改動重用（見
/// <c>SentinelPipelineContractTests</c> 的合約驗證）。
///
/// **只支援 Windows 主機**：Linux 那台 Sentinel 尚未接入，沒有真實 probe 樣本
/// （docs/BACKLOG.md），Linux 主機在這裡明確標示「尚未支援」而不是靜默略過。
///
/// **當日續跑免費取得**：完成標記＝該主機當日已有分析紀錄（<see cref="IAnalysisRecordReader.HasRecord"/>，
/// 各主機的 record store 已按 owner host 隔離）。凌晨排程跑到一半掛掉，白天重跑只會補上
/// 還沒完成的主機/日期，不會重複分析——與本機模式的缺漏日回補同一機制，不是另外設計的功能。
/// </summary>
public class NetiqPipelineService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>單一 event-search job 的 IP 批次上限。第一、二輪 probe 實證 10/50/100 個
    /// repip 子句皆被接受、耗時無明顯差異，取中間值——批次越大單日查詢次數越少，
    /// 但單一 job 逾時/失敗時受影響的主機也越多，50 是安全邊際下的實務選擇。</summary>
    internal const int IpBatchSize = 50;

    private readonly StorageBackend _backend;
    private readonly NetiqOptions _netiqOptions;
    private readonly ISentinelStore _sentinels;
    private readonly IHostStore _hosts;
    private readonly EventLogService _eventLogService;
    private readonly AIService _aiService;
    private readonly ISuppressionStore _suppressionStore;
    private readonly RiskReportService _reportService;
    private readonly BatchRunRecorder _runRecorder;
    private readonly IssueCaseCoordinator _caseCoordinator;
    private readonly IRunConsole _console;
    private readonly IRiskyEventStore? _riskyEventStore;
    private readonly int _riskyEventRetentionDays;
    private readonly bool _useAi;
    private readonly IRunProgress? _progress;

    /// <param name="console">輸出去哪裡（docs/archive/FEEDBACK-8-PLAN.md #2）：原本整支寫死 Console.WriteLine，
    /// console 批次專案退場後這些輸出沒有任何地方接收——排程跑到 NetIQ 段（整晚執行的大宗）時
    /// Web 狀態卡的訊息其實是凍結的。改走與本機路徑相同的 <see cref="IRunConsole"/>。</param>
    /// <param name="riskyEventStore">風險 log 暫存（docs/archive/WEB-SCHEDULER-PLAN.md §2）；null＝不寫暫存（測試情境）</param>
    /// <param name="riskyEventRetentionDays">暫存保留天數——超過保留期的回補日跳過寫入
    /// （見 <see cref="RiskyEventSelector.WithinRetention"/>），與本機路徑同一個閘門</param>
    /// <param name="useAi">false＝AI 未設定，本次以統計模式跑（不呼叫 AI），與本機路徑同一個
    /// 開關（docs/archive/FEEDBACK-7-PLAN.md）；本 pipeline 每次執行都重新建構，用建構參數而不是
    /// 每個方法都加一個參數傳遞</param>
    /// <param name="progress">進度回報（docs/archive/FEEDBACK-8-PLAN.md #2）；null＝不回報（測試預設不傳）</param>
    public NetiqPipelineService(
        StorageBackend backend, NetiqOptions netiqOptions,
        ISentinelStore sentinels, IHostStore hosts, EventLogService eventLogService,
        AIService aiService, ISuppressionStore suppressionStore, RiskReportService reportService,
        BatchRunRecorder runRecorder, IssueCaseCoordinator caseCoordinator, IRunConsole console,
        IRiskyEventStore? riskyEventStore = null, int riskyEventRetentionDays = 14, bool useAi = true,
        IRunProgress? progress = null)
    {
        _backend = backend;
        _netiqOptions = netiqOptions;
        _sentinels = sentinels;
        _hosts = hosts;
        _eventLogService = eventLogService;
        _aiService = aiService;
        _suppressionStore = suppressionStore;
        _reportService = reportService;
        _runRecorder = runRecorder;
        _caseCoordinator = caseCoordinator;
        _console = console;
        _riskyEventStore = riskyEventStore;
        _riskyEventRetentionDays = riskyEventRetentionDays;
        _useAi = useAi;
        _progress = progress;
    }

    /// <param name="hostList">今晚要查詢的主機（<see cref="HostListSelection"/>）；
    /// <see cref="HostListResult.Warnings"/> 已在呼叫端印過，這裡不重複。</param>
    /// <param name="trendWindowDays">已有歷史紀錄的主機，正常情況下只檢查這麼多天內的缺漏
    /// （與本機模式同一個 TrendWindowDays 常數，由呼叫端傳入避免整個程式碼庫出現第二個
    /// 寫死的 14）。</param>
    public async Task<NetiqPipelineResult> RunAsync(HostListResult hostList, int trendWindowDays, CancellationToken ct = default)
    {
        var result = new NetiqPipelineResult();
        if (hostList.TotalHosts == 0)
        {
            return result;
        }

        var maxParallel = Math.Max(1, _netiqOptions.MaxParallelServers);
        _console.WriteLine($"\n══════════ NetIQ 機房分析（{hostList.TotalHosts} 台主機，" +
                          $"{hostList.ByServer.Count} 台 Sentinel，平行度 {maxParallel}）══════════");

        // 各台 Sentinel 轄下主機互不重疊、各自獨立的 SentinelClient 連線，跨台平行不破壞
        // 「同一台主機同一天內依序處理」的趨勢比對前提（該限制只在單一主機內成立）。
        // MaxDegreeOfParallelism 由管理者設定，設 1 等同完全依序處理（docs/archive/FEEDBACK-3-PLAN.md #2）。
        // 取消交給 ParallelOptions.CancellationToken 統一處理，body 內不必再自行檢查。
        await Parallel.ForEachAsync(hostList.ByServer, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallel,
            CancellationToken = ct
        }, async (entry, serverCt) =>
        {
            var (serverName, targets) = entry;

            var sentinel = _sentinels.FindByName(serverName);
            if (sentinel == null)
            {
                _console.WriteLine($"  ⚠ Sentinel「{serverName}」設定已消失，略過（轄下 {targets.Count} 台主機本次不查詢）");
                result.AddWarning($"Sentinel「{serverName}」設定已消失，轄下 {targets.Count} 台主機本次不查詢");
                return;
            }
            if (!sentinel.CanDiscover)
            {
                _console.WriteLine($"  ⚠ Sentinel「{serverName}」查詢帳密未設定，略過（轄下 {targets.Count} 台主機本次不查詢）");
                result.AddWarning($"Sentinel「{serverName}」查詢帳密未設定，轄下 {targets.Count} 台主機本次不查詢");
                return;
            }

            var windowsTargets = targets.Where(t => string.Equals(t.Os, WebHost.OsWindows, StringComparison.OrdinalIgnoreCase)).ToList();
            var linuxCount = targets.Count - windowsTargets.Count;
            if (linuxCount > 0)
            {
                _console.WriteLine($"  ℹ Sentinel「{serverName}」轄下 {linuxCount} 台 Linux 主機，" +
                                  "取數管線尚未支援，本次不查詢（見 docs/BACKLOG.md）");
                result.AddWarning($"Sentinel「{serverName}」的 {linuxCount} 台 Linux 主機本次不查詢（Linux 取數尚未支援）");
            }
            if (windowsTargets.Count == 0)
            {
                return;
            }

            try
            {
                await RunServerAsync(sentinel, windowsTargets, trendWindowDays, result, serverCt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 失敗隔離：一台 Sentinel 整台失聯（如連線位址設定壞掉）不該讓其餘 Sentinel
                // 完全沒被查詢——這正是 HostListResult.Warnings 一貫的「不能靜默略過」原則
                Log.Warn(ex, "[{Server}] NetIQ 分析失敗，轄下主機本次未完成", serverName);
                _console.WriteLine($"  ✗ Sentinel「{serverName}」整體失敗：{ex.Message}（轄下 {windowsTargets.Count} 台主機本次未完成，下次執行自動重試缺漏日）");
                result.AddWarning($"Sentinel「{serverName}」整體失敗：{ex.Message}");
                result.AddFailed(windowsTargets.Count);
            }
        });

        _console.WriteLine($"\n── NetIQ 機房分析結果：已完成跳過 {result.HostsSkippedUpToDate} 台" +
                          $"、本次分析 {result.HostDaysAnalyzed} 個主機日、失敗 {result.HostsFailed} 個主機日 ──");
        return result;
    }

    /// <summary>
    /// 回補天數計算（docs/archive/FEEDBACK-3-PLAN.md #1）：不超過管理者設定的 BackfillDays——
    /// 首次執行與缺漏日回補一視同仁，不再有「首次深度回補」的例外路徑。
    /// 若 BackfillDays 設得比趨勢窗口還大，仍以趨勢窗口為準——回補比趨勢分析
    /// 實際會用到的更多天沒有意義，多查的天數只是白費 Sentinel 查詢額度。
    /// 抽成獨立純函式方便單元測試，不需要建構整個 pipeline 的相依物件。
    /// </summary>
    internal static int ResolveLookbackDays(int backfillDays, int trendWindowDays) =>
        Math.Min(backfillDays, trendWindowDays);

    private async Task RunServerAsync(
        Sentinel sentinel, List<NetiqTarget> targets, int trendWindowDays, NetiqPipelineResult result, CancellationToken ct)
    {
        _console.WriteLine($"\n[{sentinel.Name}] {targets.Count} 台 Windows 主機");

        // 首次與非首次統一套用 BackfillDays（docs/archive/FEEDBACK-3-PLAN.md #1）：不再區分
        // 「該主機是否已有任何紀錄」——2000 台規模下不管是首次登錄還是排程漏跑，
        // 對 Sentinel 做大量歷史日查詢都不現實，一律以管理者設定的回補窗口為準
        var lookback = ResolveLookbackDays(_netiqOptions.BackfillDays, trendWindowDays);

        var plans = new List<HostPlan>();
        foreach (var target in targets)
        {
            var hostKey = new HostKey { HostId = target.HostId, HostName = target.HostName };
            var store = _backend.RecordStore(hostKey);
            var missingDates = Enumerable.Range(1, lookback)
                .Select(offset => DateTime.Today.AddDays(-offset))
                .Where(date => !store.HasRecord(date))
                .OrderBy(date => date)
                .ToList();

            if (missingDates.Count == 0)
            {
                result.AddSkipped();
                continue;
            }
            plans.Add(new HostPlan(target, store, missingDates));
        }

        if (plans.Count == 0)
        {
            _console.WriteLine($"  [{sentinel.Name}] 今日全數主機皆已有分析紀錄，跳過。");
            return;
        }

        // 進度分母（docs/archive/FEEDBACK-8-PLAN.md #2）：各 Sentinel 平行掃描完才知道各自要補幾天，
        // 這裡累加進共享的 HostDaysTotal，分母隨掃描進度自然變大
        result.AddToTotal(plans.Sum(p => p.MissingDates.Count));
        _progress?.Report("netiq", result.HostDaysDone, result.HostDaysTotal);

        var allDates = plans.SelectMany(p => p.MissingDates).Distinct().OrderBy(d => d).ToList();
        var server = SentinelConnectionFactory.ToConnectable(sentinel);

        await using var client = new SentinelClient(server, _netiqOptions);

        // 缺漏日跨主機遞增：同一天內所有需要這天的主機一次查完（批次化），
        // 但不同天之間依序處理——同一台主機的趨勢比對需要前面日期已經寫入的歷史
        foreach (var date in allDates)
        {
            ct.ThrowIfCancellationRequested();
            var hostsNeedingDate = plans.Where(p => p.MissingDates.Contains(date)).ToList();

            foreach (var batch in hostsNeedingDate.Chunk(IpBatchSize))
            {
                await RunBatchDayAsync(client, sentinel.Name, batch, date, trendWindowDays, result, ct);
            }
        }
    }

    private async Task RunBatchDayAsync(
        SentinelClient client, string sentinelName, HostPlan[] batch, DateTime date,
        int trendWindowDays, NetiqPipelineResult result, CancellationToken ct)
    {
        var ips = batch.Select(p => p.Target.IpAddress).ToList();
        // 當地日 00:00 → 翌日 00:00，轉 UTC 交給 SentinelClient；用 DateTimeOffset 建構子
        // 讓 .NET 依系統時區規則正確處理 DST 轉換，不手動硬編位移
        var start = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
        var end = new DateTimeOffset(date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(date.AddDays(1)));

        string filter;
        try
        {
            filter = SentinelQueryBuilder.BuildWindowsFilter(ips, KnownIssueCatalog.Rules);
        }
        catch (ArgumentException)
        {
            return; // ips 不可能為空（Chunk 保證每批至少 1 筆），這裡只是防禦性守住
        }

        SentinelSearchResult searchResult;
        try
        {
            searchResult = await client.SearchAsync(
                new SentinelSearchRequest(filter, start, end, SentinelFieldMap.Q1ProjectionFields), ct);
        }
        catch (SentinelClientException ex)
        {
            _console.WriteLine($"  ✗ [{sentinelName}] {date:yyyy-MM-dd} 批次查詢失敗（{batch.Length} 台）：{ex.Message}");
            Log.Warn(ex, "[{Server}] {Date} 批次查詢失敗", sentinelName, date);
            result.AddFailed(batch.Length);
            _progress?.Report("netiq", result.HostDaysDone, result.HostDaysTotal);
            return;
        }

        if (searchResult.Truncated)
        {
            // 整批共用一個 job，無法判斷截斷影響哪幾台——寧可全部標記資料不完整，
            // 也不要有主機的統計數字看起來正常、其實被悄悄截斷（誠實申報原則）
            _console.WriteLine($"  ⚠ [{sentinelName}] {date:yyyy-MM-dd} 查詢結果被截斷" +
                              $"（found={searchResult.Found}，取回={searchResult.Events.Count}），本批 {batch.Length} 台皆標記資料不完整");
        }

        var eventsByIp = searchResult.Events
            .GroupBy(e => e.Fields.GetValueOrDefault(SentinelFieldMap.HostIp, string.Empty), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var totalSkipped = 0;
        foreach (var plan in batch)
        {
            var hostRawEvents = eventsByIp.TryGetValue(plan.Target.IpAddress, out var raw) ? raw : new List<SentinelEvent>();
            var (mapped, skipped) = SentinelEventMapper.MapAll(hostRawEvents);
            totalSkipped += skipped;

            // sn（回報此事件的主機名稱）回填 DisplayName——NetIQ 主機以 IP 登錄，
            // 光看清單認不出是哪台機器，這是既有 TouchNetiq 設計就支援的欄位
            var displayName = hostRawEvents
                .Select(e => e.Fields.GetValueOrDefault(SentinelFieldMap.HostName))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            await AnalyzeHostDayAsync(
                plan, date, mapped, searchResult.Truncated, displayName,
                hostReported: hostRawEvents.Count > 0, trendWindowDays, result, sentinelName);
        }

        if (totalSkipped > 0)
        {
            _console.WriteLine($"  ⚠ [{sentinelName}] {date:yyyy-MM-dd} 有 {totalSkipped} 筆事件解析失敗（欄位缺席/格式異常），已略過");
        }
    }

    private async Task AnalyzeHostDayAsync(
        HostPlan plan, DateTime date, List<EventLogEntryData> events, bool dataIncomplete,
        string? displayName, bool hostReported, int trendWindowDays, NetiqPipelineResult result, string sentinelName)
    {
        var target = plan.Target;

        // 每台主機各自一個 LogAnalysisService 實例：historyService（owner-host 隔離）與
        // host/hostId 綁定同一台，但 eventLogService/aiService/reportService/suppressionStore
        // 全部共用既有實例，不重複建構
        var analysisService = new LogAnalysisService(
            _eventLogService, _aiService, plan.Store, _suppressionStore,
            target.RoleDesc, _reportService, target.HostName, target.HostId);

        try
        {
            // securityLogAvailable 固定 true、channels 固定 null：三輪 probe 已確認 Sentinel
            // 收得到 Security 頻道事件（本機模式的「讀取權限被拒」概念在此不適用）；
            // Defender/RDP Operational 頻道在 Sentinel 端的覆蓋現況**尚未驗證**
            // （docs/BACKLOG.md 未決事項 #3，留待試點核對），v1 誠實的作法是不宣稱
            // 「已檢查且無異常」——channels=null 时 UncoveredChecks 不會列出這兩個頻道，
            // 這是已知的 v1 限制而非遺漏，待試點確認覆蓋現況後再決定要不要正式申報「不適用」。
            var record = await analysisService.AnalyzeDayAsync(
                date, events, useAi: _useAi, historyDays: trendWindowDays, dataIncomplete: dataIncomplete,
                securityLogAvailable: true, channels: null);

            result.AddAnalyzed();
            _runRecorder.RecordDayAnalyzed();

            // 問題案件批次逐日掛接、風險 log 暫存、AI 呼叫計數：與本機路徑同一個失敗邊界哲學，
            // 任一步失敗只記警告，不讓這台主機這天的分析結果作廢（見 HostDayPostProcessor）
            var logContext = $"[{sentinelName}] [{target.IpAddress}] ";
            HostDayPostProcessor.AttachCase(_caseCoordinator, target.HostName, date, record.TopIssues, logContext);
            HostDayPostProcessor.ReplaceRiskyEvents(
                _riskyEventStore, _riskyEventRetentionDays, date, record.TopIssues, events, target.HostId, logContext);
            HostDayPostProcessor.RecordAiCallIfApplicable(_runRecorder, _useAi, record);

            // 只在該主機當日真的有事件進 Sentinel 時才回填 LastReportAt（docs/NETIQ-API-REFERENCE.md §4.4：
            // 「整台主機近 24h 零事件＝無資料來源告警，沿用既有無回報機制」）——零事件也 Touch 的話，
            // 轉送已掛掉的主機會永遠顯示為正常回報，正是「沒查 ≠ 沒事」要防的靜默盲區。
            // 代價是「當日剛好沒有任何 watchlist/錯誤事件」的安靜主機兩天後會被標無回報——
            // 這個訊號值得人工看一眼（確認是真安靜還是轉送掛了），試點階段再依誤報率校準。
            if (hostReported)
            {
                _hosts.TouchNetiq(target.HostId, displayName, DateTime.Now);
            }

            _console.WriteLine($"  [{sentinelName}] [{target.IpAddress}] {date:yyyy-MM-dd} 風險【{record.RiskLevel}】" +
                              (record.ReportFile != null ? $" → {record.ReportFile}" : ""));
            _progress?.Report("netiq", result.HostDaysDone, result.HostDaysTotal);
        }
        catch (Exception ex)
        {
            result.AddFailed();
            Log.Warn(ex, "[{Server}] [{Ip}] {Date} 分析失敗", sentinelName, target.IpAddress, date);
            _console.WriteLine($"  ✗ [{sentinelName}] [{target.IpAddress}] {date:yyyy-MM-dd} 分析失敗：{ex.Message}" +
                              "（未寫入紀錄，下次執行自動重試）");
            _progress?.Report("netiq", result.HostDaysDone, result.HostDaysTotal);
            // 刻意不寫入歷史：下次執行的缺漏日判定（HasRecord）會自動把這天當缺漏重新處理，
            // 與本機模式的既有回補機制同一套邏輯，不需要另外設計重試旗標
        }
    }

    private sealed record HostPlan(NetiqTarget Target, IAnalysisRecordStore Store, List<DateTime> MissingDates);
}

/// <summary>
/// 單次 NetIQ 機房分析的執行結果總結，供 console 輸出與 BatchRunRecorder 里程碑使用。
///
/// 多台 Sentinel 平行處理後（docs/archive/FEEDBACK-3-PLAN.md #2），這幾個計數與 Warnings
/// 會被多個平行執行的 Task 同時寫入——內部一律經 Interlocked／lock 更新，呼叫端不再
/// 直接 ++/+=/Add，避免散落的非原子更新在平行情境下掉計數。
/// </summary>
public sealed class NetiqPipelineResult
{
    private int _hostsSkippedUpToDate;
    private int _hostDaysAnalyzed;
    private int _hostsFailed;
    private int _hostDaysTotal;
    private readonly List<string> _warnings = new();

    /// <summary>今日已有分析紀錄、本次跳過的主機數（當日續跑的核心：不重複分析）</summary>
    public int HostsSkippedUpToDate => _hostsSkippedUpToDate;

    /// <summary>本次成功分析的「主機×日」次數（不是主機數——回補多天時一台主機可能算好幾次）</summary>
    public int HostDaysAnalyzed => _hostDaysAnalyzed;

    /// <summary>失敗的「主機×日」次數（含批次查詢失敗與單台分析失敗）</summary>
    public int HostsFailed => _hostsFailed;

    /// <summary>
    /// 本次需要分析的「主機×日」總數（docs/archive/FEEDBACK-8-PLAN.md #2，進度條分母）：
    /// 各 Sentinel 平行掃描後才知道各自轄下要補幾天，隨掃描逐步累加——只會變大、不會倒退，
    /// 呼叫端（狀態卡進度條）看到的分母會隨掃描進度自然變準。
    /// </summary>
    public int HostDaysTotal => _hostDaysTotal;

    /// <summary>已處理（含成功與失敗）的「主機×日」次數——進度條分子</summary>
    public int HostDaysDone => _hostDaysAnalyzed + _hostsFailed;

    /// <summary>需要人工留意的項目（Sentinel 失聯、Linux 主機不支援等）——不是可以吞掉的雜訊</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    internal void AddSkipped() => Interlocked.Increment(ref _hostsSkippedUpToDate);

    internal void AddAnalyzed() => Interlocked.Increment(ref _hostDaysAnalyzed);

    internal void AddFailed(int count = 1) => Interlocked.Add(ref _hostsFailed, count);

    internal void AddToTotal(int count) => Interlocked.Add(ref _hostDaysTotal, count);

    internal void AddWarning(string message)
    {
        lock (_warnings) _warnings.Add(message);
    }
}
