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
    private readonly int? _rerunDays;
    /// <summary>權限異動去重鍵快照，每輪執行載入一次（見 PermissionChangeStore.GetDedupeKeys）</summary>
    private ConcurrentDictionary<string, byte> _permissionKeys = new();
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
    /// <param name="rerunDays">重新分析回望天數</param>
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
        RerunMode rerunMode = RerunMode.None,
        int? rerunDays = null)
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
        _rerunDays = rerunDays;
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

        // 權限異動去重鍵：整份 log 只讀這一次，之後各主機日共用（平行處理下用 TryAdd 佔位）
        // 必須依本次執行實際採用的回望天數（＋一週緩衝）推算查詢起點，絕不能引用編譯期絕對上限常數（MaxBackfillDaysLimit=365）：
        // 正式環境權限異動每日近十萬筆，若用常數推算會一次撈回 372 天數千萬筆去重鍵，將記憶體撐爆。更早的列不可能在本輪被重寫。
        var lookbackDays = ResolveLookbackDays(_netiqOptions.BackfillDays);
        var dedupeSince = DateTime.Today.AddDays(-(lookbackDays + 7));
        _permissionKeys = new ConcurrentDictionary<string, byte>(
            (_permissionChangeStore?.GetDedupeKeys(dedupeSince) ?? new HashSet<string>()).Select(k => new KeyValuePair<string, byte>(k, 0)),
            StringComparer.Ordinal);

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

        // AI 與搜尋脫鉤（docs/archive/FEEDBACK-12-PLAN.md §3）：搜尋＋統計主線把需要 AI 的主機日丟進
        // 有界佇列，單一背景消費者依序（FIFO）取出跑 AI，搜尋不再被 AI 拖住。無論下面的搜尋
        // 迴圈正常結束還是被取消，finally 都要讓消費者知道不會再有新項目並等它收尾——
        // 否則消費者會永遠卡在 ReadAllAsync 等下一筆，執行永遠收不了尾。
        var aiQueue = new AiFollowupQueue<AiFollowupJob>(AiQueueCapacity);
        var consumerTask = ConsumeAiQueueAsync(aiQueue, result, trendWindowDays, ct);

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
                    await RunServerAsync(sentinel, targets, trendWindowDays, result, aiQueue, suppressionSnapshot, serverCt);
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
            aiQueue.Complete();
            await consumerTask;
            FlushHostTouches();
        }

        _console.WriteLine($"\n── NetIQ 機房分析結果：已完成跳過 {result.HostsSkippedUpToDate} 台" +
                          $"、本次分析 {result.HostDaysAnalyzed} 個主機日、失敗 {result.HostsFailed} 個主機日" +
                          (result.RerunDaysAnalyzed > 0 || result.RerunDaysRetained > 0
                              ? "、" + HostDayPostProcessor.RerunSummary(result.RerunDaysAnalyzed, result.RerunDaysRetained)
                              : "") +
                          (result.AiQueued > 0
                              ? $"、AI 分析 {result.AiCompleted} 件完成（放棄 {result.AiAbandoned}，下次執行自動補跑）"
                              : "") +
                          " ──");

        // 消費者內部把取消當下每個放棄的工作項都優雅地記錄下來（AiAbandoned）而不拋出，
        // 但取消發生在 AI 段仍然是「使用者按了停止」——這裡要讓 OperationCanceledException
        // 照既有慣例繼續往外傳，AnalysisOrchestrator 的 BatchRunRecorder（using 範圍）才會把
        // 這次執行回填成「已停止」而不是「完成」。不這樣做的話，AI 段被取消時整趟執行會被
        // 誤報成成功完成，使用者看不出還有工作沒做完。
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
        AiFollowupQueue<AiFollowupJob> aiQueue, List<RuleSuppression> suppressionSnapshot, CancellationToken ct)
    {
        var windowsTargets = targets.Where(t => string.Equals(t.Os, WebHost.OsWindows, StringComparison.OrdinalIgnoreCase)).ToList();
        var linuxTargets = targets.Where(t => string.Equals(t.Os, WebHost.OsLinux, StringComparison.OrdinalIgnoreCase)).ToList();

        if (windowsTargets.Count > 0)
        {
            await RunServerOsGroupAsync(sentinel, windowsTargets, WebHost.OsWindows, trendWindowDays, result, aiQueue, suppressionSnapshot, ct);
        }
        if (linuxTargets.Count > 0)
        {
            await RunServerOsGroupAsync(sentinel, linuxTargets, WebHost.OsLinux, trendWindowDays, result, aiQueue, suppressionSnapshot, ct);
        }

        FlushHostTouches();
    }

    private async Task RunServerOsGroupAsync(
        Sentinel sentinel, List<NetiqTarget> targets, string os, int trendWindowDays, NetiqPipelineResult result,
        AiFollowupQueue<AiFollowupJob> aiQueue, List<RuleSuppression> suppressionSnapshot, CancellationToken ct)
    {
        var osLabel = os.Equals(WebHost.OsLinux, StringComparison.OrdinalIgnoreCase) ? "Linux" : "Windows";
        _console.WriteLine($"\n[{sentinel.Name}] {targets.Count} 台 {osLabel} 主機");

        // 首次與非首次統一套用 BackfillDays（docs/archive/FEEDBACK-3-PLAN.md #1）：不再區分
        // 「該主機是否已有任何紀錄」——2000 台規模下不管是首次登錄還是排程漏跑，
        // 對 Sentinel 做大量歷史日查詢都不現實，一律以管理者設定的回補窗口為準
        var lookback = ResolveLookbackDays(_netiqOptions.BackfillDays);

        var plans = new List<HostPlan>();
        var orphanJobs = new List<AiFollowupJob>();
        foreach (var target in targets)
        {
            var hostKey = new HostKey { HostId = target.HostId, HostName = target.HostName };
            var store = _backend.RecordStore(hostKey);
            var missingDates = MissingDateFinder.Find(store, lookback, requireAi: _onlyMissingOrFailed, useAi: _useAi);

            var rerunLookback = ResolveLookbackDays(_rerunDays ?? lookback);
            var rerunDates = _rerunMode != RerunMode.None
                ? RerunDateFinder.Find(store, _backend.IssueHandlingStore(), target.HostName, rerunLookback, _rerunMode)
                : new List<DateTime>();
            var targetDates = missingDates.Union(rerunDates).OrderBy(d => d).ToList();

            // 群組成員資格計畫階段解析一次（回饋十四輪 A3）：取代原本每主機日各自呼叫一次
            // HostStore.Get（整份主機清單 JSON blob 反序列化、無快取）——2000 台×14 天等於
            // 28,000 次全量讀。同一個值也給下面孤兒補跑的 retryService 用，本來就是同一台
            // 主機同一次執行內的同一份群組成員資格，沒有理由查兩次；IsHighVolume（回饋十四輪
            // B1，二分重查跨日記憶）順便從同一次 Get 一併取得，不額外多查。
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

            // AiPending 孤兒補跑（docs/archive/FEEDBACK-12-PLAN.md §3.10）：與「今天有沒有缺漏日」無關——
            // 主機可能今天已經補齊缺漏日，但上次執行被取消時留下的某一天還卡在「AI 排隊中」，
            // 兩者互不影響，各自獨立檢查同一個 lookback 窗口（與 MissingDateFinder 同一個範圍：
            // 今天往回數 lookback 天，不含今天）。
            var missingSet = targetDates.ToHashSet();
            var pendingRecords = store.ReadRecent(DateTime.Today.AddDays(-1), lookback)
                .Where(r => (_onlyMissingOrFailed ? HostDayPostProcessor.NeedsBackfill(r, _useAi) : r.AiPending) && !missingSet.Contains(r.Date.Date))
                .ToList();
            foreach (var pending in pendingRecords)
            {
                var retryService = new LogAnalysisService(
                    _eventLogService, _aiService, store, _suppressionStore,
                    target.RoleDesc, _reportService, target.HostName, target.HostId, _riskyEventStore,
                    hostGroupIds: groupIds, suppressionSnapshot: suppressionSnapshot);
                orphanJobs.Add(new AiFollowupJob(retryService, store, target.IpAddress, pending.Date, sentinel.Name,
                    WorkItem: null, RetryRecord: pending));
            }
        }

        if (plans.Count == 0 && orphanJobs.Count == 0)
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
                // 這層批次間平行是安全的：不會有同一台主機同時被兩個批次處理，AI 佇列的
                // 「同主機前一天已定案」FIFO 前提也不受影響（前提只約束同一台主機的日期序）。
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
                            await RunBatchDayAsync(client, sentinel.Name, batch, date, os, trendWindowDays, result, aiQueue, suppressionSnapshot, batchCt);
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

        // 孤兒補跑殿後（docs/archive/FEEDBACK-12-PLAN.md §3.10）：排在這台 Sentinel 的一般缺漏日之後，
        // 不搶當日主線的 AI 佇列順位
        foreach (var job in orphanJobs)
        {
            ct.ThrowIfCancellationRequested();
            _console.WriteLine($"  [{sentinel.Name}] [{job.Ip}] {job.Date:yyyy-MM-dd} AiPending 孤兒補跑排隊中");

            // 同 AnalyzeHostDayAsync 的背壓顯示（回饋十三輪 A7）：孤兒補跑排在殿後，
            // 更容易撞上前面一般缺漏日已經把佇列排滿的情況
            if (!aiQueue.TryEnqueue(job))
            {
                await EnqueueUnderBackpressureAsync(aiQueue, job, sentinel.Name, result, ct);
            }
            // 計數放在 EnqueueAsync 成功之後（同 AnalyzeHostDayAsync 的理由）：取消若發生在
            // 背壓等待中，這筆工作項並未真的進到佇列，提前計數會讓 AiQueued 與
            // AiCompleted+AiAbandoned 永久對不上。
            result.AddAiQueued();
        }
    }

    /// <summary>背壓期間的進度回報間隔。AI 一件動輒數十秒，太密只是重複同一個數字。</summary>
    private const int BackpressureReportIntervalMs = 2000;

    /// <summary>AI 佇列容量。正式執行一律用預設值；開放覆寫是為了讓測試能以極小容量
    /// 重現背壓路徑，不必真的塞滿 200 件。</summary>
    internal int AiQueueCapacity { get; set; } = AiFollowupQueue<AiFollowupJob>.Capacity;

    /// <summary>
    /// 佇列滿載時的入列等待（回饋二十七輪作業 B）。
    ///
    /// 舊版在這裡回報的是「主機日」數字，而且只在進入背壓那一瞬間報一次——搜尋主線此時已停，
    /// 那個數字到等待結束都不會變，畫面看起來就是卡死（使用者回饋②：AI 佇列 200 件卡著，
    /// 背景明明在跑卻看不出進度）。改成回報 AI 消化進度並在等待期間持續更新：
    /// 分子是已完成＋已放棄的 AI 件數、分母是已排入的件數，數字會隨每件 AI 跑完而前進。
    /// </summary>
    private async Task EnqueueUnderBackpressureAsync(
        AiFollowupQueue<AiFollowupJob> aiQueue, AiFollowupJob job, string sentinelName,
        NetiqPipelineResult result, CancellationToken ct)
    {
        _console.WriteLine($"  ⏸ [{sentinelName}] AI 佇列已滿（{AiQueueCapacity} 件），" +
                           $"搜尋暫停等待消化中…（佇列尚有 {aiQueue.PendingCount} 件）");

        ReportAiDigestProgress(result);

        var enqueue = aiQueue.EnqueueAsync(job, ct).AsTask();
        while (!enqueue.IsCompleted)
        {
            // 等待間隔本身不掛 ct：ct 取消時 enqueue 會自己結束迴圈並在下面的 await 拋出，
            // 這裡再掛一次只會多一個要處理的取消例外
            await Task.WhenAny(enqueue, Task.Delay(BackpressureReportIntervalMs, CancellationToken.None));
            ReportAiDigestProgress(result);
        }

        await enqueue;   // 取消與例外原樣往外傳（呼叫端依賴 OperationCanceledException 走孤兒補跑）

        _progress?.Report("netiq", result.HostDaysDone, result.HostDaysTotal);
    }

    /// <summary>AI 消化進度：與 netiq-ai 軌同一組數字，背壓期間畫面才不會退回主機日語意。</summary>
    private void ReportAiDigestProgress(NetiqPipelineResult result) =>
        _progress?.Report("netiq-backpressure", result.AiCompleted + result.AiAbandoned, result.AiQueued);

    private async Task RunBatchDayAsync(
        ISentinelSearchClient client, string sentinelName, HostPlan[] batch, DateTime date, string os,
        int trendWindowDays, NetiqPipelineResult result, AiFollowupQueue<AiFollowupJob> aiQueue,
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

        SentinelSearchResult searchResult;
        try
        {
            searchResult = await client.SearchAsync(
                new SentinelSearchRequest(filter, start, end, projectionFields), ct);
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
                var mid = batch.Length / 2;
                await RunBatchDayAsync(client, sentinelName, batch[..mid], date, os, trendWindowDays, result, aiQueue, suppressionSnapshot, ct);
                await RunBatchDayAsync(client, sentinelName, batch[mid..], date, os, trendWindowDays, result, aiQueue, suppressionSnapshot, ct);
                return;
            }

            // 已收斂到單台仍截斷：真的沒辦法再切了，誠實標記資料不完整
            _console.WriteLine($"  ⚠ [{sentinelName}] {date:yyyy-MM-dd} 查詢結果被截斷" +
                              $"（found={searchResult.Found}，取回={searchResult.Events.Count}），標記資料不完整");

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

        var eventsByIp = searchResult.Events
            .GroupBy(e => e.Fields.GetValueOrDefault(SentinelFieldMap.HostIp, string.Empty), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var totalSkipped = 0;
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

            await AnalyzeHostDayAsync(
                plan, date, mapped, searchResult.Truncated, displayName,
                hostReported: hostRawEvents.Count > 0, trendWindowDays, result, sentinelName, aiQueue, suppressionSnapshot, ct);

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
    /// 統計段（docs/archive/FEEDBACK-12-PLAN.md §3.4）：聚合/規則/趨勢/關聯計算完立刻寫入，不等 AI——
    /// 這是本輪脫鉤的核心，NetIQ 搜尋不再因為 AI 拖住下一個日期。需要 AI 時把工作項連同
    /// 這裡建立的 <see cref="LogAnalysisService"/> 實例（AI 段要用同一個 historyService/
    /// reportService/serverDescription 綁定）一起丟進 <paramref name="aiQueue"/>，
    /// 由 <see cref="ConsumeAiQueueAsync"/> 背景消化。
    /// </summary>
    private async Task AnalyzeHostDayAsync(
        HostPlan plan, DateTime date, List<EventLogEntryData> events, bool dataIncomplete,
        string? displayName, bool hostReported, int trendWindowDays, NetiqPipelineResult result, string sentinelName,
        AiFollowupQueue<AiFollowupJob> aiQueue, List<RuleSuppression> suppressionSnapshot, CancellationToken ct)
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
        // 全部共用既有實例，不重複建構。這個實例會被 AiWorkItem 需要 AI 時一併帶進佇列，
        // AI 段用同一個實例呼叫 CompleteAiAsync（見下方 AiFollowupJob）。
        // hostGroupIds（回饋十三輪 F）：抑制的 Group 範圍判定要知道這台主機的群組成員資格，
        // 回饋十四輪 A3 改讀計畫階段已解析好的 plan.GroupIds，不再每主機日查一次 HostStore。
        var analysisService = new LogAnalysisService(
            _eventLogService, _aiService, plan.Store, _suppressionStore,
            target.RoleDesc, _reportService, target.HostName, target.HostId,
            hostGroupIds: plan.GroupIds, suppressionSnapshot: suppressionSnapshot);

        // 記錄是否已成功寫入（見下方 catch(OperationCanceledException) 的判斷依據）——
        // BuildStatisticalRecordAsync 本身被取消時 record 仍是 null，維持既有「未寫入歷史，
        // 下次視為缺漏日重新處理」語意；record 非 null 代表 plan.Store.Append 已成功，
        // 之後任何取消（目前唯一來源是 aiQueue.EnqueueAsync 的背壓等待）都不該被當成分析失敗。
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
            // RecordAiCallIfApplicable 移到 ConsumeAiQueueAsync：AI 呼叫本身發生在 AI 段，
            // 計數要跟著移動，否則執行監控的「AI 呼叫」統計會統計到還沒真正發生的事。
            var logContext = $"[{sentinelName}] [{target.IpAddress}] ";
            HostDayPostProcessor.AttachCase(_caseCoordinator, target.HostName, date, record.TopIssues, logContext);
            HostDayPostProcessor.ReplaceRiskyEvents(
                _riskyEventStore, _rawEventRetentionDays, date, record.TopIssues, events, target.HostId, logContext);
            HostDayPostProcessor.RecordPermissionChanges(
                _permissionChangeStore, _permissionKeys, target.HostName, target.Os, events, date, logContext, _permissionMappings);

            // AiWorkItem.Logs 的窄化（回饋十三輪 B1）已移進 BuildStatisticalRecordAsync 本身
            // （回饋十四輪 A2）：workItem 拿到手時 Logs 已經是 RiskyEventSelector 的選取結果
            // （≤500 筆），不必在這裡事後補——見 AiWorkItem.cs 與 RiskyEventSelector 的文件。

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
                _console.WriteLine($"  [{sentinelName}] [{target.IpAddress}] {date:yyyy-MM-dd} 統計完成，AI 分析排隊中");
                var job = new AiFollowupJob(analysisService, plan.Store, target.IpAddress, date, sentinelName, workItem, RetryRecord: null);

                // 佇列已滿時在這裡背壓等待（docs/archive/FEEDBACK-12-PLAN.md §3.2）——AI 落後太多時
                // 讓搜尋主線自然暫停，不是無限堆積記憶體。回饋十三輪 A7：先非阻塞探測一次，
                // 探測到會背壓就先切一個獨立 phase 讓畫面誠實顯示「暫停中」，不然使用者看到的
                // 就是進度條卡住不動——與使用者回饋②（AI 落後看起來像當掉）同一種外觀症狀。
                //
                // TryEnqueue（Channel.TryWrite）不看 ct——有空位時一定成功，即使 token 已取消。
                // 這裡必須先手動檢查一次，否則「取消發生在排入 AI 佇列當下」（有空位的情況）
                // 會被 TryEnqueue 靜默吞掉、不再拋出，AnalyzeHostDayAsync 外層就分不出
                // 「取消但統計已寫入」與「正常排隊成功」，孤兒補跑機制也不會被觸發。
                ct.ThrowIfCancellationRequested();
                if (!aiQueue.TryEnqueue(job))
                {
                    await EnqueueUnderBackpressureAsync(aiQueue, job, sentinelName, result, ct);
                }
                // 計數放在 EnqueueAsync 成功之後：取消若發生在背壓等待中，這筆工作項其實
                // 沒有真的進到佇列，AddAiQueued 若在等待之前就算會與 AiCompleted+AiAbandoned
                // 永久對不上（消費者永遠看不到這筆），netiq-ai 進度條分母也會卡住少 1 件永遠跑不完。
                result.AddAiQueued();
            }
            else
            {
                _console.WriteLine($"  [{sentinelName}] [{target.IpAddress}] {date:yyyy-MM-dd} 風險【{record.RiskLevel}】" +
                                  (record.ReportFile != null ? " → 已產生風險報告" : ""));
            }

            // 統計完成即算 done，AI 不在內（docs/archive/FEEDBACK-12-PLAN.md §3.7）：進度條分子分母
            // 只反映搜尋+統計的進度，不會被 AI 卡住不動
            _progress?.Report("netiq", result.HostDaysDone, result.HostDaysTotal);
        }
        catch (OperationCanceledException) when (record != null)
        {
            // 統計紀錄已成功寫入（plan.Store.Append 已跑過），取消發生在那之後——目前唯一
            // 來源是 aiQueue.EnqueueAsync 佇列滿載時的背壓等待。這不是分析失敗，記錄本身完好、
            // AiPending 維持 true，下次執行由孤兒補跑機制自動接手（§3.10），不能落進下面
            // AddFailed 分支——那裡的「未寫入紀錄」對這筆而言是假的，會誤植一個其實資料完好
            // 的主機日成失敗，讓執行監控顯示錯誤的失敗清單。
            _console.WriteLine($"  ⚠ [{sentinelName}] [{target.IpAddress}] {date:yyyy-MM-dd} " +
                              "已取消排入 AI 佇列（統計紀錄已完整寫入，下次執行自動補跑）");
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

    /// <summary>
    /// AI 待處理佇列的工作項（docs/archive/FEEDBACK-12-PLAN.md §3.4/§3.10）：帶著統計段已建好的
    /// <see cref="LogAnalysisService"/> 實例（AI 段要用同一個 historyService/reportService
    /// 綁定重讀歷史）與寫回目的地（<paramref name="Store"/>）。兩種來源二擇一：
    /// <paramref name="WorkItem"/> 非 null＝這次搜尋新算出的主機日；<paramref name="RetryRecord"/>
    /// 非 null＝上次執行取消留下的 AiPending 孤兒紀錄補跑（§3.10，殿後排在同一台 Sentinel
    /// 的一般缺漏日之後）。兩者恰好其中一個非 null，不會同時發生也不會都是 null。
    /// </summary>
    private sealed record AiFollowupJob(
        LogAnalysisService AnalysisService, IAnalysisRecordStore Store, string Ip, DateTime Date, string SentinelName,
        AiWorkItem? WorkItem, DailyAnalysisRecord? RetryRecord)
    {
        public bool IsRetry => RetryRecord != null;
    }

    /// <summary>
    /// AI 段的單一背景消費者（docs/archive/FEEDBACK-12-PLAN.md §3.4）：依 FIFO 順序取出工作項，
    /// 逐一呼叫 <see cref="LogAnalysisService.CompleteAiAsync"/> 並用
    /// <see cref="IAnalysisRecordStore.AttachAiResult"/> 寫回——單一消費者天然序列化，
    /// 保證同一台主機的日期依入列順序處理，讓隔日 prompt 引用前一天 AI 摘要的語意成立。
    ///
    /// 讀取用 <see cref="CancellationToken.None"/>：即使執行被取消，仍要把佇列裡已經在途的
    /// 項目逐一讀出並記為放棄（而不是讓 ReadAllAsync 本身因取消直接拋出、佇列裡剩下的項目
    /// 完全沒有機會被記錄）。是否放棄由迴圈內自行檢查 <paramref name="ct"/> 決定。
    /// </summary>
    private async Task ConsumeAiQueueAsync(
        AiFollowupQueue<AiFollowupJob> queue, NetiqPipelineResult result, int trendWindowDays, CancellationToken ct)
    {
        try
        {
            await foreach (var job in queue.ReadAllAsync(CancellationToken.None))
            {
                var tag = job.IsRetry ? "補跑" : "AI 分析";

                if (ct.IsCancellationRequested)
                {
                    // 執行被取消：不再啟動新的 AI 呼叫。統計紀錄已完整寫入，AiPending 維持
                    // true，下次執行由孤兒補跑機制自動接手（§3.10），這裡不需要做任何補救
                    result.AddAiAbandoned();
                    _console.WriteLine($"  ⚠ [{job.SentinelName}] [{job.Ip}] {job.Date:yyyy-MM-dd} " +
                                      $"執行已取消，{tag}待下次執行補跑（統計紀錄已完整寫入）");
                    continue;
                }

                try
                {
                    var outcome = job.IsRetry
                        ? await job.AnalysisService.RetryAiAsync(job.RetryRecord!, trendWindowDays, ct)
                        : await job.AnalysisService.CompleteAiAsync(job.WorkItem!, ct);
                    job.Store.AttachAiResult(job.Date, outcome);

                    // 與統計段同一個判準（原 HostDayPostProcessor.RecordAiCallIfApplicable），
                    // 只是移到 AI 呼叫真正發生的這裡——_useAi 在此必為 true（能入列就代表
                    // BuildStatisticalRecordAsync 判定 needsAi，那必然蘊含 useAi 全域開啟）
                    if (outcome.AiAnalyzed || outcome.RiskLevel != RiskLevels.Low)
                    {
                        _runRecorder.RecordAiCall(outcome.AiAnalyzed);
                    }

                    result.AddAiCompleted();
                    _console.WriteLine($"  [{job.SentinelName}] [{job.Ip}] {job.Date:yyyy-MM-dd} {tag}完成，" +
                                      $"風險【{outcome.RiskLevel}】" + (outcome.ReportFile != null ? " → 已產生風險報告" : ""));
                }
                catch (OperationCanceledException)
                {
                    result.AddAiAbandoned();
                    _console.WriteLine($"  ⚠ [{job.SentinelName}] [{job.Ip}] {job.Date:yyyy-MM-dd} " +
                                      $"{tag}中途取消，待下次執行補跑（統計紀錄已完整寫入）");
                }
                catch (Exception ex)
                {
                    // 與統計段同一個失敗邊界哲學：AI 段任何一步意外失敗都不該讓其餘排隊項目
                    // 停擺——已寫入的統計紀錄維持 AiPending=true，下次執行由孤兒補跑機制接手
                    result.AddAiAbandoned();
                    Log.Warn(ex, "[{Server}] [{Ip}] {Date} {Tag}失敗", job.SentinelName, job.Ip, job.Date, tag);
                    _console.WriteLine($"  ✗ [{job.SentinelName}] [{job.Ip}] {job.Date:yyyy-MM-dd} {tag}失敗：{ex.Message}" +
                                      "（統計紀錄已完整寫入，下次執行自動重試 AI 段）");
                }

                // 搜尋全部完成、佇列還有件時，這裡接手成為使用者看到的進度（docs/archive/FEEDBACK-12-PLAN.md §3.7）
                _progress?.Report("netiq-ai", result.AiCompleted + result.AiAbandoned, result.AiQueued);
            }
        }
        catch (Exception ex)
        {
            // 安全網：消費者本身不該拋出未處理例外——RunAsync 用 finally await 這個 Task，
            // 未預期的例外會在那裡重新拋出並蓋掉原本的錯誤（例如取消），這裡先攔下記錄
            Log.Error(ex, "NetIQ AI 消費者迴圈意外中止");
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
    private int _aiQueued;
    private int _aiCompleted;
    private int _aiAbandoned;
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

    /// <summary>本次排進 AI 待處理佇列的主機日數（docs/archive/FEEDBACK-12-PLAN.md §3.4）——
    /// 統計段判定需要 AI 就算一件，不論最後是否成功完成</summary>
    public int AiQueued => _aiQueued;

    /// <summary>AI 段成功跑完並附掛結果的主機日數（含 AI 呼叫本身失敗但仍完成降級寫回的情況——
    /// 「完成」指的是這件工作項有跑到底，不是 AI 一定成功）</summary>
    public int AiCompleted => _aiCompleted;

    /// <summary>本次執行未能跑完 AI 段的主機日數（執行被取消，或 AI 段本身意外拋出未預期例外）：
    /// 統計紀錄已完整寫入，只是 AiPending 維持 true，下次執行由孤兒補跑機制自動接手（§3.10）</summary>
    public int AiAbandoned => _aiAbandoned;

    internal void AddSkipped() => Interlocked.Increment(ref _hostsSkippedUpToDate);

    internal void AddAnalyzed() => Interlocked.Increment(ref _hostDaysAnalyzed);

    internal void AddFailed(int count = 1) => Interlocked.Add(ref _hostsFailed, count);

    internal void AddToTotal(int count) => Interlocked.Add(ref _hostDaysTotal, count);

    internal void AddAiQueued() => Interlocked.Increment(ref _aiQueued);

    internal void AddAiCompleted() => Interlocked.Increment(ref _aiCompleted);

    internal void AddAiAbandoned() => Interlocked.Increment(ref _aiAbandoned);

    internal void AddRerunAnalyzed() => Interlocked.Increment(ref _rerunDaysAnalyzed);

    internal void AddRerunRetained() => Interlocked.Increment(ref _rerunDaysRetained);

    internal void AddWarning(string message)
    {
        lock (_warnings) _warnings.Add(message);
    }
}
