using System.Collections.Concurrent;
using LogForesight.Core.Models;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// 機房分析 pipeline（docs/archive/HISTORY.md 決策 B2、§4；docs/BACKLOG.md Phase 4）：
/// 逐 Sentinel、逐日、批次向 Sentinel 取事件，映射後餵進與本機路徑完全相同的
/// <see cref="LogAnalysisService"/>——五層偵測/AI/報告零改動重用（見
/// <c>SentinelPipelineContractTests</c> 的合約驗證）。
///
/// **同時支援 Windows／Linux 主機**（docs/archive/FEEDBACK-12-PLAN.md §4B，2026-08-07 起）：
/// 同一台 Sentinel 轄下的主機依 <see cref="NetiqTarget.Os"/> 分成兩組各自處理
/// （<see cref="RunServerAsync"/>）——查詢 filter／投影欄位／事件映射三處分路
/// （<see cref="SentinelQueryBuilder"/>／<see cref="SentinelFieldMap"/>／
/// <see cref="SentinelEventMapper"/>），統計/趨勢/AI/報告仍是同一條零改動重用的下游。
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

    /// <summary>
    /// 本次實際的 Sentinel 平行度（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3）。
    ///
    /// <see cref="NetiqOptions.MaxParallelServers"/> 是管理者可調的設定，但那個設定當初是在
    /// 「分析獨佔一個批次行程」的前提下訂的。分析搬進站台行程之後（Web 排程化），平行度
    /// 直接等於「同時有幾條執行緒在跟前景請求搶 thread pool 與連線」，所以設定值仍然有效，
    /// 只是被夾在 <see cref="NetiqOptions.MaxParallelServersLimit"/> 之內。
    /// 下限 1＝完全依序（docs/archive/FEEDBACK-3-PLAN.md #2 的既有語意，不變）。
    /// </summary>
    internal static int ResolveParallelism(int configured) =>
        Math.Clamp(configured, 1, NetiqOptions.MaxParallelServersLimit);

    /// <summary>
    /// 單一 Sentinel 內部的批次查詢平行度（回饋十三輪 D）。與 <see cref="ResolveParallelism"/>
    /// 同一個夾住理由，只是維度不同——見 <see cref="NetiqOptions.MaxParallelQueriesPerServer"/>。
    /// </summary>
    internal static int ResolveQueryParallelism(int configured) =>
        Math.Clamp(configured, 1, NetiqOptions.MaxParallelQueriesPerServerLimit);

    private readonly StorageBackend _backend;
    private readonly NetiqOptions _netiqOptions;
    private readonly ISentinelStore _sentinels;
    private readonly IHostStore _hosts;
    private readonly EventLogService _eventLogService;
    private readonly IAiService _aiService;
    private readonly ISuppressionStore _suppressionStore;
    private readonly RiskReportService _reportService;
    private readonly BatchRunRecorder _runRecorder;
    private readonly IssueCaseCoordinator _caseCoordinator;
    private readonly IRunConsole _console;
    private readonly IRiskyEventStore? _riskyEventStore;
    private readonly int _rawEventRetentionDays;
    private readonly bool _useAi;
    private readonly IRunProgress? _progress;
    private readonly bool _onlyMissingOrFailed;
    private readonly Func<Sentinel, ISentinelSearchClient> _clientFactory;
    private readonly PermissionChangeStore? _permissionChangeStore;
    private readonly PermissionFieldMappings? _permissionMappings;
    private readonly RerunMode _rerunMode;
    /// <summary>權限異動的主機日佔位（回饋三十四輪 A2）：只放「主機＋日期」，不放事件內容——
    /// 內容去重由 <see cref="PermissionChangeStore.GetDedupeKeysForHost"/> 逐主機日現查承擔。</summary>
    private ConcurrentDictionary<string, byte> _permissionHostDayClaims = new();
    /// <summary>待寫回的主機回報時間，key＝HostId（見 <see cref="FlushHostTouches"/>）。
    /// 執行緒安全的集合是必要的：多台 Sentinel 平行處理，會同時寫進這裡。</summary>
    private readonly ConcurrentDictionary<long, HostTouch> _hostTouches = new();

    /// <summary>單台主機待寫回的回報時間與顯示名。同一台主機重複觸發時後寫覆蓋前寫——
    /// 一趟執行內的 <c>DateTime.Now</c> 差異對「最後回報時間」沒有意義。</summary>
    private sealed record HostTouch(string? DisplayName, DateTime ReportedAt);

    /// <param name="console">輸出去哪裡（docs/archive/FEEDBACK-8-PLAN.md #2）：原本整支寫死 Console.WriteLine，
    /// console 批次專案退場後這些輸出沒有任何地方接收——排程跑到 NetIQ 段（整晚執行的大宗）時
    /// Web 狀態卡的訊息其實是凍結的。改走與本機路徑相同的 <see cref="IRunConsole"/>。</param>
    /// <param name="riskyEventStore">風險 log 暫存（docs/archive/WEB-SCHEDULER-PLAN.md §2）；null＝不寫暫存（測試情境）</param>
    /// <param name="rawEventRetentionDays">原始事件保留天數——超過保留期的回補日跳過寫入
    /// （見 <see cref="RiskyEventSelector.WithinRetention"/>），與本機路徑同一個閘門</param>
    /// <param name="useAi">false＝AI 未設定，本次以統計模式跑（不呼叫 AI），與本機路徑同一個
    /// 開關（docs/archive/FEEDBACK-7-PLAN.md）；本 pipeline 每次執行都重新建構，用建構參數而不是
    /// 每個方法都加一個參數傳遞</param>
    /// <param name="progress">進度回報（docs/archive/FEEDBACK-8-PLAN.md #2）；null＝不回報（測試預設不傳）</param>
    /// <param name="clientFactory">依 Sentinel 建立搜尋用戶端（docs/archive/FEEDBACK-12-PLAN.md §3.8-2）；
    /// null＝預設走真正的 <see cref="SentinelClient"/>（<see cref="SentinelConnectionFactory.ToConnectable"/>
    /// 轉連線資訊）。測試可覆寫成回傳假搜尋結果的替身，不必真的連 Sentinel，也不用重寫
    /// 整個 <see cref="RunAsync"/> 的批次／逐日邏輯。</param>
    /// <param name="onlyMissingOrFailed">只補跑失敗或未執行的主機（略過已成功且有 AI 分析的），預設 false</param>
    /// <param name="permissionChangeStore">權限異動 store；null＝預設由 backend 建構</param>
    /// <param name="permissionMappings">權限異動自訂欄位對應；null＝使用內建官方欄位名</param>
    /// <param name="rerunMode">重新分析模式，預設 None</param>
    public NetiqPipelineService(
        StorageBackend backend, NetiqOptions netiqOptions,
        ISentinelStore sentinels, IHostStore hosts, EventLogService eventLogService,
        IAiService aiService, ISuppressionStore suppressionStore, RiskReportService reportService,
        BatchRunRecorder runRecorder, IssueCaseCoordinator caseCoordinator, IRunConsole console,
        IRiskyEventStore? riskyEventStore = null, int? rawEventRetentionDays = null, bool useAi = true,
        IRunProgress? progress = null, Func<Sentinel, ISentinelSearchClient>? clientFactory = null,
        bool onlyMissingOrFailed = false,
        PermissionChangeStore? permissionChangeStore = null,
        PermissionFieldMappings? permissionMappings = null,
        RerunMode rerunMode = RerunMode.None)
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
        _rawEventRetentionDays = rawEventRetentionDays ?? SystemSettings.DefaultRawEventRetentionDays;
        _useAi = useAi;
        _progress = progress;
        _onlyMissingOrFailed = onlyMissingOrFailed;
        _clientFactory = clientFactory ?? (sentinel =>
            new SentinelClient(SentinelConnectionFactory.ToConnectable(sentinel), netiqOptions));
        _permissionChangeStore = permissionChangeStore ?? backend.PermissionChanges();
        _permissionMappings = permissionMappings;
        _rerunMode = rerunMode;
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

        // 權限異動去重（回饋三十四輪 A2）：跨執行的內容去重改由資料庫逐主機日現查
        // （PermissionChangeStore.GetDedupeKeysForHost），這裡只留主機日層級的佔位集合，
        // 確保平行處理下同一個主機日不會被處理兩次。舊做法是開跑時把整個查詢窗的內容去重鍵
        // 一次全載，正式環境每日近十萬筆、回望天數一拉大就是數 GB 且整趟不釋放。
        _permissionHostDayClaims = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var lookbackDays = ResolveLookbackDays(_netiqOptions.BackfillDays);

        var configured = _netiqOptions.MaxParallelServers;
        var maxParallel = ResolveParallelism(configured);
        if (maxParallel < configured)
        {
            // 夾住時要明講：管理者調了 8 卻只跑 3、畫面又不說，會被當成設定沒生效
            _console.WriteLine($"  ⓘ Sentinel 平行度設定為 {configured}，本次以 {maxParallel} 執行" +
                              "（分析與網站同行程，平行度設上限以免拖慢前景畫面）");
        }

        // 回饋十三輪 D：單一 Sentinel 內部的批次查詢平行度，與上面的跨 Sentinel 平行是正交維度，
        // 這裡先夾一次算出本次要用的值並申報，RunServerOsGroupAsync 內部重算同一個純函數取用
        // （不跨方法傳遞這個值，各自算一次，結果必然一致）。
        var configuredQueries = _netiqOptions.MaxParallelQueriesPerServer;
        var maxQueries = ResolveQueryParallelism(configuredQueries);
        if (maxQueries < configuredQueries)
        {
            _console.WriteLine($"  ⓘ 單台 Sentinel 查詢平行度設定為 {configuredQueries}，本次以 {maxQueries} 執行" +
                              "（同一個行程架構上限，不是效能旋鈕）");
        }

        _console.WriteLine($"\n══════════ NetIQ 機房分析（{hostList.TotalHosts} 台主機，" +
                          $"{hostList.ByServer.Count} 台 Sentinel，平行度 {maxParallel}" +
                          (maxQueries > 1 ? $"，單台 Sentinel 內查詢平行度 {maxQueries}" : "") +
                          $"）══════════");

        // 抑制清單 run 開始時讀一次（回饋十四輪 A3），往下傳給每台主機每一天共用——取代原本
        // 每主機日各自呼叫一次 LoadAll()（見 LogAnalysisService 建構子 suppressionSnapshot 參數
        // 說明）。快照語意：整趟 run 內所有主機看到同一份抑制清單，run 開始後才新增的抑制要
        // 下次執行才生效——這個 pipeline 本來就是每次執行重新建構（見類別文件），與本機路徑
        // 「每次分析即時查」的即時性差異只在批次規模下才有感。
        var suppressionSnapshot = _suppressionStore.LoadAll();

        try
        {
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

                try
                {
                    await RunServerAsync(sentinel, targets, trendWindowDays, result, suppressionSnapshot, serverCt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 失敗隔離：一台 Sentinel 整台失聯（如連線位址設定壞掉）不該讓其餘 Sentinel
                    // 完全沒被查詢——這正是 HostListResult.Warnings 一貫的「不能靜默略過」原則
                    Log.Warn(ex, "[{Server}] NetIQ 分析失敗，轄下主機本次未完成", serverName);
                    _console.WriteLine($"  ✗ Sentinel「{serverName}」整體失敗：{ex.Message}（轄下 {targets.Count} 台主機本次未完成，下次執行自動重試缺漏日）");
                    result.AddWarning($"Sentinel「{serverName}」整體失敗：{ex.Message}");
                    result.AddFailed(targets.Count);
                }
            });
        }
        finally
        {
            FlushHostTouches();
        }

        _console.WriteLine($"\n── NetIQ 機房分析結果：已完成跳過 {result.HostsSkippedUpToDate} 台" +
                          $"、本次分析 {result.HostDaysAnalyzed} 個主機日、失敗 {result.HostsFailed} 個主機日" +
                          (result.RerunDaysAnalyzed > 0 || result.RerunDaysRetained > 0
                              ? "、" + HostDayPostProcessor.RerunSummary(result.RerunDaysAnalyzed, result.RerunDaysRetained)
                              : "") +
                          " ──");

        ct.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>
    /// 回補天數計算（docs/archive/FEEDBACK-3-PLAN.md #1）：不超過管理者設定的 BackfillDays——
    /// 首次執行與缺漏日回補一視同仁，不再有「首次深度回補」的例外路徑。
    /// 回望窗口與趨勢基線窗口是兩件不同的事，上限夾在 <see cref="NetiqOptions.MaxBackfillDaysLimit"/>（或指定的有效上限），
    /// 不再受趨勢窗口天數限制。抽成獨立純函式方便單元測試，不需要建構整個 pipeline 的相依物件。
    /// </summary>
    internal static int ResolveLookbackDays(int backfillDays) =>
        Math.Clamp(backfillDays, 0, NetiqOptions.MaxBackfillDaysLimit);   // 0＝不回望（既有語意）；有效上限由呼叫端先夾（AnalysisOrchestrator）

    /// <summary>
    /// 依 <see cref="NetiqTarget.Os"/> 把這台 Sentinel 轄下的主機分成兩組，各自跑完整的
    /// 缺漏日掃描＋孤兒補跑流程（docs/archive/FEEDBACK-12-PLAN.md §4.4）——同一台 Sentinel 依環境事實
    /// 通常只會有單一 OS（見類別文件），但這裡不依賴此假設，兩組都有目標時依序各跑一輪
    /// （批次內部仍是各自 OS 同質，filter 只需要建一次）。
    /// </summary>
    private async Task RunServerAsync(
        Sentinel sentinel, List<NetiqTarget> targets, int trendWindowDays, NetiqPipelineResult result,
        List<RuleSuppression> suppressionSnapshot, CancellationToken ct)
    {
        var windowsTargets = targets.Where(t => string.Equals(t.Os, WebHost.OsWindows, StringComparison.OrdinalIgnoreCase)).ToList();
        var linuxTargets = targets.Where(t => string.Equals(t.Os, WebHost.OsLinux, StringComparison.OrdinalIgnoreCase)).ToList();

        if (windowsTargets.Count > 0)
        {
            await RunServerOsGroupAsync(sentinel, windowsTargets, WebHost.OsWindows, trendWindowDays, result, suppressionSnapshot, ct);
        }
        if (linuxTargets.Count > 0)
        {
            await RunServerOsGroupAsync(sentinel, linuxTargets, WebHost.OsLinux, trendWindowDays, result, suppressionSnapshot, ct);
        }

        FlushHostTouches();
    }

    private async Task RunServerOsGroupAsync(
        Sentinel sentinel, List<NetiqTarget> targets, string os, int trendWindowDays, NetiqPipelineResult result,
        List<RuleSuppression> suppressionSnapshot, CancellationToken ct)
    {
        var osLabel = os.Equals(WebHost.OsLinux, StringComparison.OrdinalIgnoreCase) ? "Linux" : "Windows";
        _console.WriteLine($"\n[{sentinel.Name}] {targets.Count} 台 {osLabel} 主機");

        // 首次與非首次統一套用 BackfillDays（docs/archive/FEEDBACK-3-PLAN.md #1）：不再區分
        // 「該主機是否已有任何紀錄」——2000 台規模下不管是首次登錄還是排程漏跑，
        // 對 Sentinel 做大量歷史日查詢都不現實，一律以管理者設定的回補窗口為準
        var lookback = ResolveLookbackDays(_netiqOptions.BackfillDays);

        var plans = new List<HostPlan>();
        foreach (var target in targets)
        {
            var hostKey = new HostKey { HostId = target.HostId, HostName = target.HostName };
            var store = _backend.RecordStore(hostKey);
            var missingDates = MissingDateFinder.Find(store, lookback, requireAi: _onlyMissingOrFailed, useAi: _useAi);

            // 缺漏日與重跑日共用同一個回望窗口（回饋三十四輪 C）：兩者找的是同一條時間軸上
            // 互斥的兩半（沒紀錄的補、有紀錄的依模式重跑），沒有任何模式需要兩個不同的天數。
            var rerunDates = _rerunMode != RerunMode.None
                ? RerunDateFinder.Find(store, _backend.IssueHandlingStore(), target.HostName, lookback, _rerunMode)
                : new List<DateTime>();
            var targetDates = missingDates.Union(rerunDates).OrderBy(d => d).ToList();

            // 群組成員資格計畫階段解析一次（回饋十四輪 A3）：取代原本每主機日各自呼叫一次
            // HostStore.Get（整份主機清單 JSON blob 反序列化、無快取）——2000 台×14 天等於
            // 28,000 次全量讀。IsHighVolume（回饋十四輪 B1，二分重查跨日記憶）順便從同一次
            // Get 一併取得，不額外多查。
            var hostRecord = _hosts.Get(target.HostId);
            var groupIds = (IReadOnlyCollection<long>?)hostRecord?.GroupIds ?? Array.Empty<long>();
            var isHighVolume = hostRecord?.IsHighVolume ?? false;

            if (targetDates.Count == 0)
            {
                result.AddSkipped();
            }
            else
            {
                plans.Add(new HostPlan(target, store, targetDates, rerunDates.ToHashSet(), groupIds, isHighVolume));
            }
        }

        if (plans.Count == 0)
        {
            _console.WriteLine(_onlyMissingOrFailed
                ? $"  [{sentinel.Name}] 今日全數主機皆已完成且具備 AI 分析，跳過。"
                : $"  [{sentinel.Name}] 今日全數主機皆已有分析紀錄，跳過。");
            return;
        }

        if (plans.Count > 0)
        {
            // 進度分母（docs/archive/FEEDBACK-8-PLAN.md #2）：各 Sentinel 平行掃描完才知道各自要補幾天，
            // 這裡累加進共享的 HostDaysTotal，分母隨掃描進度自然變大
            result.AddToTotal(plans.Sum(p => p.MissingDates.Count));
            _progress?.Report("netiq", result.HostDaysDone, result.HostDaysTotal);

            var allDates = plans.SelectMany(p => p.MissingDates).Distinct().OrderBy(d => d).ToList();

            // client pool（回饋十三輪 D）：SentinelClient 單一 instance 是單一併發佇列（見其類別
            // 文件），要平行查詢就得各自建立獨立實例——各自獨立的 SAML token、各自
            // DisposeAsync 登出。maxQueries=1（預設）時池只有一個 client，下面的
            // Parallel.ForEachAsync 退化成依序處理單一 batch，與改動前逐位相同。
            var maxQueries = ResolveQueryParallelism(_netiqOptions.MaxParallelQueriesPerServer);
            var clientPool = new ConcurrentBag<ISentinelSearchClient>(
                Enumerable.Range(0, maxQueries).Select(_ => _clientFactory(sentinel)));
            try
            {
                // 缺漏日跨主機遞增：同一天內所有需要這天的主機一次查完（批次化），
                // 但不同天之間依序處理——同一台主機的趨勢比對需要前面日期已經寫入的歷史。
                // 同一天內的批次彼此主機不重疊（Chunk 保證每台主機只落在一個批次裡），
                // 這層批次間平行是安全的：不會有同一台主機同時被兩個批次處理。
                foreach (var date in allDates)
                {
                    ct.ThrowIfCancellationRequested();
                    var hostsNeedingDate = plans.Where(p => p.MissingDates.Contains(date)).ToList();

                    // 高流量主機跨日記憶（回饋十四輪 B1）：曾在單獨查詢下仍截斷的主機直接讓它
                    // 單獨成一批，不必每天都從整批大小重新二分收斂到它（見 RunBatchDayAsync
                    // 的截斷處理）——穩態下每天只多付這 1 次查詢，而不是 log₂(批次大小) 次。
                    // 其餘主機照舊依 IpBatchSize 分批。
                    var highVolumeBatches = hostsNeedingDate.Where(p => p.IsHighVolume).Select(p => new[] { p });
                    var normalBatches = hostsNeedingDate.Where(p => !p.IsHighVolume).Chunk(IpBatchSize);
                    var batches = highVolumeBatches.Concat(normalBatches).ToList();

                    // 取消交給 ParallelOptions.CancellationToken 統一處理（同 RunAsync 頂層的既有模式）。
                    await Parallel.ForEachAsync(batches, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxQueries,
                        CancellationToken = ct
                    }, async (batch, batchCt) =>
                    {
                        // 租一個 client：並行度上限已等於池大小，池子裡永遠有東西可租——
                        // TryTake 失敗代表這個不變量被破壞了，是程式錯誤而非可預期的執行期狀況。
                        if (!clientPool.TryTake(out var client))
                            throw new InvalidOperationException("Sentinel client pool 耗盡（不應發生：並行度已受池大小節制）。");
                        try
                        {
                            await RunBatchDayAsync(client, sentinel.Name, batch, date, os, trendWindowDays, result, suppressionSnapshot, batchCt);
                        }
                        finally
                        {
                            clientPool.Add(client);
                        }
                    });
                }
            }
            finally
            {
                foreach (var client in clientPool)
                {
                    await client.DisposeAsync();
                }
            }
        }
    }

    private async Task RunBatchDayAsync(
        ISentinelSearchClient client, string sentinelName, HostPlan[] batch, DateTime date, string os,
        int trendWindowDays, NetiqPipelineResult result,
        List<RuleSuppression> suppressionSnapshot, CancellationToken ct)
    {
        var isLinux = os.Equals(WebHost.OsLinux, StringComparison.OrdinalIgnoreCase);
        var ips = batch.Select(p => p.Target.IpAddress).ToList();
        // 當地日 00:00 → 翌日 00:00，轉 UTC 交給 SentinelClient；用 DateTimeOffset 建構子
        // 讓 .NET 依系統時區規則正確處理 DST 轉換，不手動硬編位移
        var start = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
        var end = new DateTimeOffset(date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(date.AddDays(1)));

        string filter;
        try
        {
            filter = isLinux
                ? SentinelQueryBuilder.BuildLinuxFilter(ips, KnownIssueCatalog.Rules)
                : SentinelQueryBuilder.BuildWindowsFilter(ips, KnownIssueCatalog.Rules);
        }
        catch (ArgumentException)
        {
            return; // ips 不可能為空（Chunk 保證每批至少 1 筆），這裡只是防禦性守住
        }

        var projectionFields = isLinux ? SentinelFieldMap.LinuxQ1ProjectionFields : SentinelFieldMap.Q1ProjectionFields;

        // 串流分組（回饋三十四輪 A3）：以往是「整個 job 的事件先全量取回 → GroupBy 分組 → 逐主機再映射」，
        // 同一批資料同時有三份在記憶體裡（單 job 上限 10 萬筆、最多 12 個 job 並行）。改成逐頁進來就
        // 直接分進各主機的桶子，任何時刻都不存在整批的扁平清單。
        var eventsByIp = new Dictionary<string, List<SentinelEvent>>(StringComparer.OrdinalIgnoreCase);

        SentinelSearchResult searchResult;
        try
        {
            searchResult = await client.SearchAsync(
                new SentinelSearchRequest(
                    filter, start, end, projectionFields,
                    PageObserver: page =>
                    {
                        foreach (var evt in page)
                        {
                            var ip = evt.Fields.GetValueOrDefault(SentinelFieldMap.HostIp, string.Empty);
                            if (!eventsByIp.TryGetValue(ip, out var bucket))
                            {
                                bucket = new List<SentinelEvent>();
                                eventsByIp[ip] = bucket;
                            }
                            bucket.Add(evt);
                        }
                        return true;
                    },
                    StreamOnly: true),
                ct);
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
            // 截斷時二分重查（回饋十三輪 B2）：整批共用一個 job，原本無法判斷截斷影響哪幾台，
            // 只能全部連坐標記資料不完整——一台吵的主機拖垮一整批的趨勢基準（Linux 上線後
            // sshd/kernel 量級更容易撞上單一 job 的 MaxResultsPerJob 上限）。批次還能再切時，
            // 切半各自重查：真的還是截斷就繼續切，收斂到單台才誠實標記；成本只在真的截斷時付，
            // 多數情況下切一次的較小批次就不再截斷。批次序（同一天內主機互不重疊）與計數方式
            // （AddToTotal 已在上層算完、AddAnalyzed/AddFailed 在 per-plan 層）不受影響，
            // 遞迴呼叫本身就是「換一批更小的 ips 重新走一次這個函式」，不會重複計數。
            if (batch.Length > 1)
            {
                // 重查前先把這次分組好的資料放掉，否則遞迴下去時上一層的整批事件還被參照著
                eventsByIp.Clear();
                var mid = batch.Length / 2;
                await RunBatchDayAsync(client, sentinelName, batch[..mid], date, os, trendWindowDays, result, suppressionSnapshot, ct);
                await RunBatchDayAsync(client, sentinelName, batch[mid..], date, os, trendWindowDays, result, suppressionSnapshot, ct);
                return;
            }

            // 已收斂到單台仍截斷：真的沒辦法再切了，誠實標記資料不完整
            _console.WriteLine($"  ⚠ [{sentinelName}] {date:yyyy-MM-dd} 查詢結果被截斷" +
                              $"（found={searchResult.Found}，取回={searchResult.Retrieved}），標記資料不完整");

            // 跨日記憶（回饋十四輪 B1）：單獨查詢都還截斷，記下來讓明天分批直接把這台獨立成一批，
            // 不必再從整批大小重新二分收斂。只在旗標還沒設時才寫，避免同一台主機連續高流量的
            // 每一天都白白多一次 blob 寫入。
            if (!batch[0].IsHighVolume)
            {
                _hosts.SetHighVolume(batch[0].Target.HostId, true);
            }
        }
        else if (batch.Length == 1 && batch[0].IsHighVolume && searchResult.Found < _netiqOptions.MaxResultsPerJob / 2)
        {
            // 旗標清除（回饋十四輪 B1）：單獨查詢已經不再截斷，且量遠低於上限（<50%）才清除——
            // 遲滯設計防臨界主機在「單獨成批／合批再二分」間日日震盪。門檻用 Found（Sentinel
            // 回報的符合條件總數，不受 max-results 截斷影響）而非取回筆數，才是真實日流量。
            _hosts.SetHighVolume(batch[0].Target.HostId, false);
        }

        var totalSkipped = 0;
        // 同一批裡可能有兩台主機登錄同一個 IP（NetIQ 主機以 IP 登錄，重複登錄並非不可能）。
        // 用剩餘台數決定何時可以把桶子丟掉——只剩最後一台在用時才移除，
        // 否則第二台會拿到空事件、整天被當成「來源未回報」（終檢抓到）。
        var plansPerIp = batch
            .GroupBy(p => p.Target.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var plan in batch)
        {
            var hostRawEvents = eventsByIp.TryGetValue(plan.Target.IpAddress, out var raw) ? raw : new List<SentinelEvent>();
            var (mapped, skipped) = SentinelEventMapper.MapAll(hostRawEvents, os);
            totalSkipped += skipped;

            // sn（回報此事件的主機名稱）回填 DisplayName——NetIQ 主機以 IP 登錄，
            // 光看清單認不出是哪台機器，這是既有 TouchNetiq 設計就支援的欄位
            var displayName = hostRawEvents
                .Select(e => e.Fields.GetValueOrDefault(SentinelFieldMap.HostName))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var hostReported = hostRawEvents.Count > 0;

            // 這台的原始事件已經映射完，分析期間不必再留著（回饋三十四輪 A3）：
            // 移出桶子讓原始字典層在 AnalyzeHostDayAsync 執行期間就能被回收。
            // 共用同一 IP 的主機都取用過了才丟。
            if (--plansPerIp[plan.Target.IpAddress] == 0)
            {
                eventsByIp.Remove(plan.Target.IpAddress);
            }

            await AnalyzeHostDayAsync(
                plan, date, mapped, searchResult.Truncated, displayName,
                hostReported, trendWindowDays, result, sentinelName, suppressionSnapshot, ct);

            // 主機之間讓出執行緒（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3）：統計段（AI 已脫鉤，
            // §3）幾乎全程同步完成，這個 foreach 會一路把同一條 thread pool 執行緒佔到整批
            // 50 台跑完，期間前景請求只能等 thread pool 長新執行緒（每秒才補一條）。
            // Yield 讓已排隊的前景工作先跑——成本是每台一次排程往返，相對於一台主機一天的
            // 分析時間可以忽略。回饋十三輪 D 之後，MaxParallelQueriesPerServer 大於 1 時會有
            // 多個批次的這個 foreach 同時執行、各自佔用一條執行緒各自 yield——讓步的必要性
            // 隨批次平行度等比放大，不是變得沒必要，故不因批次 D 而移除或改變頻率。
            await Task.Yield();
        }

        if (totalSkipped > 0)
        {
            _console.WriteLine($"  ⚠ [{sentinelName}] {date:yyyy-MM-dd} 有 {totalSkipped} 筆事件解析失敗（欄位缺席/格式異常），已略過");
        }
    }

    /// <summary>
    /// 統計段：聚合/規則/趨勢/關聯計算完立刻寫入。取數端只負責統計分析與落地，
    /// 不再執行 AI 分析；需要 AI 的紀錄標記 AiPending=true，留待獨立 AI 排程處理。
    /// </summary>
    private async Task AnalyzeHostDayAsync(
        HostPlan plan, DateTime date, List<EventLogEntryData> events, bool dataIncomplete,
        string? displayName, bool hostReported, int trendWindowDays, NetiqPipelineResult result, string sentinelName,
        List<RuleSuppression> suppressionSnapshot, CancellationToken ct)
    {
        var target = plan.Target;
        var isRerun = plan.RerunDates.Contains(date.Date);

        // 重跑日：來源取不到資料、或這次取得的資料比當初殘缺（查詢被截斷）時保留原結果——
        // 判定與本機路徑共用同一個函式，不各寫一份
        if (isRerun && HostDayPostProcessor.ShouldRetainExistingDay(events.Count, dataIncomplete, sourceDegraded: false))
        {
            result.AddRerunRetained();
            _console.WriteLine($"  [{sentinelName}] [{target.IpAddress}] {date:yyyy-MM-dd} 來源已無事件或資料不完整，保留原分析結果");
            _progress?.Report("netiq", result.HostDaysDone, result.HostDaysTotal);
            return;
        }

        // 每台主機各自一個 LogAnalysisService 實例：historyService（owner-host 隔離）與
        // host/hostId 綁定同一台，但 eventLogService/aiService/reportService/suppressionStore
        // 全部共用既有實例，不重複建構。
        // hostGroupIds（回饋十三輪 F）：抑制的 Group 範圍判定要知道這台主機的群組成員資格，
        // 回饋十四輪 A3 改讀計畫階段已解析好的 plan.GroupIds，不再每主機日查一次 HostStore。
        var analysisService = new LogAnalysisService(
            _eventLogService, _aiService, plan.Store, _suppressionStore,
            target.RoleDesc, _reportService, target.HostName, target.HostId,
            hostGroupIds: plan.GroupIds, suppressionSnapshot: suppressionSnapshot);

        // 記錄是否已成功寫入
        DailyAnalysisRecord? record = null;

        try
        {
            // securityLogAvailable 固定 true、channels 固定 null：三輪 probe 已確認 Sentinel
            // 收得到 Security 頻道事件（本機模式的「讀取權限被拒」概念在此不適用）；
            // Defender/RDP Operational 頻道在 Sentinel 端的覆蓋現況**尚未驗證**
            // （docs/BACKLOG.md 未決事項 #3，留待試點核對），v1 誠實的作法是不宣稱
            // 「已檢查且無異常」——channels=null 时 UncoveredChecks 不會列出這兩個頻道，
            // 這是已知的 v1 限制而非遺漏，待試點確認覆蓋現況後再決定要不要正式申報「不適用」。
            AiWorkItem? workItem;
            (record, workItem) = await analysisService.BuildStatisticalRecordAsync(
                date, events, useAi: _useAi, historyDays: trendWindowDays, dataIncomplete: dataIncomplete,
                securityLogAvailable: true, channels: null, ct, hostOs: target.Os);

            if (isRerun)
            {
                plan.Store.DeleteDays(new[] { date });
                result.AddRerunAnalyzed();
            }

            plan.Store.Append(record);

            result.AddAnalyzed();
            _runRecorder.RecordDayAnalyzed();

            // 問題案件批次逐日掛接、風險 log 暫存：與本機路徑同一個失敗邊界哲學，任一步失敗
            // 只記警告，不讓這台主機這天的分析結果作廢（見 HostDayPostProcessor）。兩者只依賴
            // TopIssues 與 events，AI 不會改動這兩者，所以留在統計段——不必等 AI 完成才能做。
            var logContext = $"[{sentinelName}] [{target.IpAddress}] ";
            HostDayPostProcessor.AttachCase(_caseCoordinator, target.HostName, date, record.TopIssues, logContext);
            HostDayPostProcessor.ReplaceRiskyEvents(
                _riskyEventStore, _rawEventRetentionDays, date, record.TopIssues, events, target.HostId, logContext);
            HostDayPostProcessor.RecordPermissionChanges(
                _permissionChangeStore, _permissionHostDayClaims, target.HostName, target.Os, events, date, logContext, _permissionMappings);

            // 只在該主機當日真的有事件進 Sentinel 時才回填 LastReportAt（docs/NETIQ-API-REFERENCE.md §4.4：
            // 「整台主機近 24h 零事件＝無資料來源告警，沿用既有無回報機制」）——零事件也 Touch 的話，
            // 轉送已掛掉的主機會永遠顯示為正常回報，正是「沒查 ≠ 沒事」要防的靜默盲區。
            // 代價是「當日剛好沒有任何 watchlist/錯誤事件」的安靜主機兩天後會被標無回報——
            // 這個訊號值得人工看一眼（確認是真安靜還是轉送掛了），試點階段再依誤報率校準。
            if (hostReported)
            {
                _hostTouches[target.HostId] = new HostTouch(displayName, DateTime.Now);
            }

            if (workItem != null)
            {
                _console.WriteLine($"  [{sentinelName}] [{target.IpAddress}] {date:yyyy-MM-dd} 統計完成（待 AI 排程分析）");
            }
            else
            {
                _console.WriteLine($"  [{sentinelName}] [{target.IpAddress}] {date:yyyy-MM-dd} 風險【{record.RiskLevel}】" +
                                  (record.ReportFile != null ? " → 已產生風險報告" : ""));
            }

            // 統計完成即算 done（取數端不再處理 AI）：進度條分子分母只反映搜尋+統計的進度
            _progress?.Report("netiq", result.HostDaysDone, result.HostDaysTotal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

    /// <summary>
    /// 把累積的回報時間一次寫回主機清單。逐台 <c>TouchNetiq</c> 是整份 blob 的讀→改→寫，
    /// 3000 台 × 120 天的初次回補等於 36 萬次 4 MB 全量重寫；改為累積後在**每台 Sentinel
    /// 跑完**與 <see cref="RunAsync"/> 的 finally 各 flush 一次，一趟執行降到個位數次。
    ///
    /// 延遲寫入的代價是崩潰時丟掉尚未 flush 的回報時間，那些主機會短暫顯示為無回報；
    /// 損失上界是「一台 Sentinel 的回報時間」，而執行中途崩潰本來就代表那一批資料不完整。
    ///
    /// **逐鍵 TryRemove 而不是「複製一份再 Clear」**：多台 Sentinel 平行處理，各自跑完都會
    /// 呼叫這裡。<c>Clear</c> 會把「複製之後、清除之前」由別台 Sentinel 寫入的新項目一併刪掉，
    /// 那台主機的 LastReportAt 就這樣消失、畫面顯示成無回報，而且不會有任何錯誤訊息——
    /// 正是「沒查 ≠ 沒事」要防的靜默盲區。只移除自己真的取走的鍵才不會漏。
    /// </summary>
    private void FlushHostTouches()
    {
        if (_hostTouches.IsEmpty) return;

        var touches = new List<KeyValuePair<long, HostTouch>>();
        foreach (var hostId in _hostTouches.Keys)
        {
            if (_hostTouches.TryRemove(hostId, out var touch))
            {
                touches.Add(new KeyValuePair<long, HostTouch>(hostId, touch));
            }
        }

        if (touches.Count == 0) return;

        // 寫入失敗不讓整台 Sentinel 作廢（比照 HostDayPostProcessor 的既定哲學）：
        // blob 的行程內互斥是 per-instance 的鎖，Web 端與分析端各有一份 backend，
        // 同一份 hosts blob 真的會在 DB 層競爭、撞滿重試次數後拋出。這個例外若冒到
        // RunServerAsync 尾端，會被 Parallel.ForEachAsync 的 catch 記成「整台 Sentinel 失敗」
        // ——幾百台已經分析完成的主機被記為失敗，只因為回報時間寫不回去。
        // 失敗時把 touch 放回字典等下次 flush；已有較新的值就保留較新的，不要覆蓋回舊值。
        try
        {
            _hosts.MutateBatch(batch =>
            {
                var hostDict = batch.ToDictionary(h => h.HostId);
                foreach (var kvp in touches)
                {
                    if (hostDict.TryGetValue(kvp.Key, out var host))
                    {
                        host.LastReportAt = kvp.Value.ReportedAt;
                        if (!string.IsNullOrWhiteSpace(kvp.Value.DisplayName))
                        {
                            host.DisplayName = kvp.Value.DisplayName;
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            foreach (var kvp in touches)
            {
                // 逐欄合併而不是整筆二選一：ReportedAt 取較新，DisplayName 取非空的那個——
                // 整筆丟掉會連帶丟掉另一筆才有的顯示名稱，容錯機制自己先丟一半資料
                _hostTouches.AddOrUpdate(
                    kvp.Key,
                    kvp.Value,
                    (_, existing) => new HostTouch(
                        !string.IsNullOrWhiteSpace(existing.DisplayName) ? existing.DisplayName : kvp.Value.DisplayName,
                        existing.ReportedAt >= kvp.Value.ReportedAt ? existing.ReportedAt : kvp.Value.ReportedAt));
            }

            Log.Warn(ex, "回報時間批次寫回失敗（{Count} 台），保留待下次 flush 重試：{Msg}",
                touches.Count, ex.Message);
        }
    }

    /// <param name="GroupIds">這台主機所屬的主機群組 Id（回饋十四輪 A3）：計畫階段（<see cref="RunServerOsGroupAsync"/>）
    /// 解析一次，取代原本 <see cref="AnalyzeHostDayAsync"/> 每主機日各自呼叫一次 <c>HostStore.Get</c>——
    /// 該方法整份 blob 反序列化、無快取，2000 台×14 天等於 28,000 次全量讀。</param>
    /// <param name="IsHighVolume">這台主機上次是否曾在單獨查詢下仍被截斷（回饋十四輪 B1，
    /// <see cref="WebHost.IsHighVolume"/> 的計畫階段快照）：true 時分批（<see cref="RunServerOsGroupAsync"/>）
    /// 直接讓它單獨成一批，不必每天都從整批大小重新二分收斂到它。</param>
    private sealed record HostPlan(NetiqTarget Target, IAnalysisRecordStore Store, List<DateTime> MissingDates,
        HashSet<DateTime> RerunDates, IReadOnlyCollection<long> GroupIds, bool IsHighVolume);
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
    private int _rerunDaysAnalyzed;
    private int _rerunDaysRetained;
    private readonly List<string> _warnings = new();

    /// <summary>今日已有分析紀錄、本次跳過的主機數（當日續跑的核心：不重複分析）</summary>
    public int HostsSkippedUpToDate => _hostsSkippedUpToDate;

    /// <summary>本次成功分析的「主機×日」次數（不是主機數——回補多天時一台主機可能算好幾次）</summary>
    public int HostDaysAnalyzed => _hostDaysAnalyzed;

    /// <summary>失敗的「主機×日」次數（含批次查詢失敗與單台分析失敗）</summary>
    public int HostsFailed => _hostsFailed;

    /// <summary>重新分析完成的「主機×日」天數</summary>
    public int RerunDaysAnalyzed => _rerunDaysAnalyzed;

    /// <summary>重跑但來源無資料而保留原結果的「主機×日」天數</summary>
    public int RerunDaysRetained => _rerunDaysRetained;

    /// <summary>
    /// 本次需要分析的「主機×日」總數（docs/archive/FEEDBACK-8-PLAN.md #2，進度條分母）：
    /// 各 Sentinel 平行掃描後才知道各自轄下要補幾天，隨掃描逐步累加——只會變大、不會倒退，
    /// 呼叫端（狀態卡進度條）看到的分母會隨掃描進度自然變準。
    /// </summary>
    public int HostDaysTotal => _hostDaysTotal;

    /// <summary>已處理（含成功、失敗與保留原結果）的「主機×日」次數——進度條分子</summary>
    public int HostDaysDone => _hostDaysAnalyzed + _hostsFailed + _rerunDaysRetained;

    /// <summary>需要人工留意的項目（Sentinel 失聯、Linux 主機不支援等）——不是可以吞掉的雜訊</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    internal void AddSkipped() => Interlocked.Increment(ref _hostsSkippedUpToDate);

    internal void AddAnalyzed() => Interlocked.Increment(ref _hostDaysAnalyzed);

    internal void AddFailed(int count = 1) => Interlocked.Add(ref _hostsFailed, count);

    internal void AddToTotal(int count) => Interlocked.Add(ref _hostDaysTotal, count);

    internal void AddRerunAnalyzed() => Interlocked.Increment(ref _rerunDaysAnalyzed);

    internal void AddRerunRetained() => Interlocked.Increment(ref _rerunDaysRetained);

    internal void AddWarning(string message)
    {
        lock (_warnings) _warnings.Add(message);
    }
}
