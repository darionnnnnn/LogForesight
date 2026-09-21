using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Extensions.Hosting;
using NLog;
using LogLevel = NLog.LogLevel;

namespace LogForesight.Web.Services;

/// <summary>
/// PRTG 數值快照服務的狀態（供鏡像狀態端點與測試查詢）。
/// </summary>
/// <param name="LastSuccessAt">上次成功快照時間</param>
/// <param name="LastSensorCount">上次成功快照處理的感測器數量</param>
/// <param name="IntervalMinutes">目前生效間隔（含退避後）</param>
/// <param name="ConsecutiveFailures">連續失敗次數</param>
/// <param name="PendingSamples">累積器中待寫出的總樣本數量</param>
/// <param name="LastSkipReason">快照跳過原因（若因前置條件未滿足暫停）</param>
public sealed record PrtgSnapshotStatus(
    DateTime? LastSuccessAt,
    int LastSensorCount,
    int IntervalMinutes,
    int ConsecutiveFailures,
    int PendingSamples,
    string? LastSkipReason);

/// <summary>
/// PRTG 數值快照背景服務（docs/PRTG-SPEC.md §3b）：定期發送一次 table.json 取得即時快照數值，
/// 由內部累積器依小時平均後寫入 lf_prtg_values。
/// </summary>
public class PrtgSnapshotHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private volatile bool _scopeRefreshRequested;

    private static readonly Regex IntervalRegex = new(@"^(\d+(?:\.\d+)?)\s*([a-zA-Z]+)$", RegexOptions.Compiled);

    private readonly ISystemSettingsStore _settingsStore;
    private readonly StorageBackend _backend;
    private readonly SchedulerRunState _schedulerRunState;
    private readonly PrtgStructureSyncService _structureSync;
    private readonly PrtgBackfillService _backfill;
    private readonly IHostApplicationLifetime _lifetime;

    private readonly PrtgSnapshotAccumulator _accumulator = new();

    /// <summary>已從累積器取出、但還沒寫成功的整點列（資料庫暫時寫不進去時暫存，見 WriteSampledRows）。</summary>
    private readonly List<PrtgValueRow> _pendingWrite = new();
    /// <summary>待寫清單的上限：資料庫長時間寫不進去時捨棄最舊的列，不讓記憶體無限長。約 10 小時 × 2 萬顆。</summary>
    private const int MaxPendingWriteRows = 200_000;
    /// <summary>最近一次從設定算出的每小時期望樣本數（見 ExpectedSamplesPerHour）。</summary>
    private int _expectedSamplesPerHour;

    private DateTime? _lastSuccessAt;

    /// <summary>
    /// 上次實際對 PRTG 發出快照請求的時間（成功或失敗都算）。退避的間隔從這裡量，
    /// 不是從上次成功——PRTG 回不來時上次成功會永遠停在過去，間隔條件恆成立，
    /// 服務就會每一輪都打一次全量快照，退避形同虛設。
    /// </summary>
    private DateTime? _lastAttemptAt;
    private int _lastSensorCount;
    private int _effectiveIntervalMinutes;
    private int _consecutiveFailures;
    private string? _lastSkipReason;

    /// <summary>目前這段暫停的開始時間（null＝沒有暫停）。</summary>
    private DateTime? _skipSince;

    /// <summary>暫停超過這個長度，恢復時才寫執行輸出。</summary>
    private static readonly TimeSpan PauseReportThreshold = TimeSpan.FromMinutes(15);

    private DateTime? _targetRefreshHour;
    private HashSet<long>? _targetObjids;
    private Dictionary<long, string>? _sensorTypes;

    /// <summary>
    /// 目標顆數不超過它時以 filter_objid 分批查（每批 <see cref="PrtgResourceGuardProbe.MaxBatchSize"/> 顆），超過時改單發全站查詢。
    /// 取這個值的理由：超過時分批請求數（40 個以上）的往返成本多於一次全站查詢；尚無實機數據佐證，有實測再調。
    /// </summary>
    internal const int FilteredSnapshotLimit = 2000;

    /// <summary>每輪範圍補抓最多處理的裝置數（由 objid 小到大），其餘留給下一輪。</summary>
    internal const int MaxBackfillDevicesPerTick = 50;

    private static readonly IRunConsole SilentConsole = new SilentRunConsole();

    private readonly IHostStore _hosts;
    private readonly ISentinelStore _sentinels;
    private readonly PrtgProbeRunState _probeState;

    /// <summary>服務持有的單一實例：DNS 快取跨輪有效，取數範圍計算不必每輪重新解析。</summary>
    private readonly PrtgAddressResolver _addressResolver = new();

    /// <summary>
    /// 補抓過、PRTG 端確實一顆感測器都沒有的裝置（只在記憶體）：不再每輪重打。
    /// 結構同步寫過鏡像後清空，讓那時已長出感測器的裝置有機會再補。
    /// </summary>
    private readonly HashSet<long> _confirmedEmptyDevices = new();

    /// <summary>
    /// 守門覆寫清單中、向 PRTG 查所在裝置時回傳裡找不到的感測器 objid（只在記憶體）：不再每輪重查。
    /// 清空時機與 <see cref="_confirmedEmptyDevices"/> 相同（結構同步寫過鏡像後）。
    /// </summary>
    private readonly HashSet<long> _overrideObjidsNotFound = new();

    /// <summary>上次看到的感測器鏡像 synced_at 最大值（已吸收補抓自身寫入的部分）。</summary>
    private DateTime? _seenStructureSyncedAt;

    internal Func<PrtgClient>? ClientFactory { get; set; }

    /// <summary>目前重用中的 PRTG client 與建立它時的設定指紋（見 <see cref="GetClient"/>）</summary>
    private PrtgClient? _client;
    private string? _clientFingerprint;
    internal IRunConsole? Console { get; set; }
    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;
    internal PrtgSnapshotAccumulator Accumulator => _accumulator;
    /// <summary>執行輸出只留最近這麼多筆：站台長時間運行，無上限會一直長成記憶體洩漏。</summary>
    private const int MaxExecutionOutputs = 100;

    internal List<string> ExecutionOutputs { get; } = new();

    public PrtgSnapshotHostedService(
        ISystemSettingsStore systemSettingsStore,
        StorageBackend storageBackend,
        SchedulerRunState schedulerRunState,
        PrtgStructureSyncService structureSyncService,
        PrtgBackfillService backfillService,
        IHostStore hostStore,
        ISentinelStore sentinelStore,
        PrtgProbeRunState probeState,
        IHostApplicationLifetime lifetime)
        : this(systemSettingsStore, storageBackend, schedulerRunState, structureSyncService, backfillService,
               hostStore, sentinelStore, probeState, lifetime, DefaultPollInterval)
    {
    }

    public PrtgSnapshotHostedService(
        ISystemSettingsStore systemSettingsStore,
        StorageBackend storageBackend,
        SchedulerRunState schedulerRunState,
        PrtgStructureSyncService structureSyncService,
        PrtgBackfillService backfillService,
        IHostStore hostStore,
        ISentinelStore sentinelStore,
        PrtgProbeRunState probeState,
        IHostApplicationLifetime lifetime,
        TimeSpan pollInterval)
    {
        _pollInterval = pollInterval;
        _hosts = hostStore ?? throw new ArgumentNullException(nameof(hostStore));
        _probeState = probeState ?? throw new ArgumentNullException(nameof(probeState));
        _sentinels = sentinelStore ?? throw new ArgumentNullException(nameof(sentinelStore));
        _settingsStore = systemSettingsStore ?? throw new ArgumentNullException(nameof(systemSettingsStore));
        _backend = storageBackend ?? throw new ArgumentNullException(nameof(storageBackend));
        _schedulerRunState = schedulerRunState ?? throw new ArgumentNullException(nameof(schedulerRunState));
        _structureSync = structureSyncService ?? throw new ArgumentNullException(nameof(structureSyncService));
        _backfill = backfillService ?? throw new ArgumentNullException(nameof(backfillService));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));

        var settings = _settingsStore.Get();
        _effectiveIntervalMinutes = PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy).SnapshotIntervalMinutes;
        _expectedSamplesPerHour = ExpectedSamplesPerHour(settings);
        if (_effectiveIntervalMinutes <= 0)
        {
            _effectiveIntervalMinutes = PrtgFetchStrategy.ConservativeIntervalMinutes;
        }

        _lifetime.ApplicationStopping.Register(OnStopping);
    }

    /// <summary>
    /// 要求在輪詢等待中喚醒並執行範圍補抓（新主機對應或改 IP 後呼叫）。
    /// </summary>
    public virtual void RequestScopeRefresh()
    {
        _scopeRefreshRequested = true;
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已有等待中的信號
        }
    }

    /// <summary>
    /// 每小時期望樣本數以策略設定的間隔算，不用退避後的生效間隔：
    /// 退避期間實際取到的樣本本來就少，coverage 要如實變低；改用拉長後的間隔算會把 1 個樣本算成滿涵蓋，
    /// 讓取樣不足的列通過可用門檻進入基線。
    /// </summary>
    private static int ExpectedSamplesPerHour(SystemSettings settings) =>
        Math.Max(1, 60 / Math.Max(1, PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy).SnapshotIntervalMinutes));

    public PrtgSnapshotStatus GetStatus() =>
        new(
            LastSuccessAt: _lastSuccessAt,
            LastSensorCount: _lastSensorCount,
            IntervalMinutes: _effectiveIntervalMinutes,
            ConsecutiveFailures: _consecutiveFailures,
            PendingSamples: _accumulator.SampleCount,
            LastSkipReason: _lastSkipReason);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var initialWait = _pollInterval < TimeSpan.FromSeconds(10) ? _pollInterval : TimeSpan.FromSeconds(10);
            await _wakeSignal.WaitAsync(initialWait, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_scopeRefreshRequested)
                {
                    _scopeRefreshRequested = false;
                    await ScopeRefreshTickAsync(stoppingToken);
                }
                else
                {
                    await TickAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PRTG 數值快照輪詢發生未預期錯誤（不影響下次輪詢）");
            }

            try
            {
                await _wakeSignal.WaitAsync(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task ScopeRefreshTickAsync(CancellationToken ct = default)
    {
        var settings = _settingsStore.Get();
        if (!settings.PrtgEnabled) return;
        if (string.IsNullOrWhiteSpace(settings.PrtgUrl) || !PrtgClientFactory.HasUsableCredentials(settings)) return;
        if (_structureSync.IsRunning || _backfill.GetStatus().IsRunning ||
            IsFetchRunningPrtgPhase() || _probeState.Snapshot().IsRunning)
        {
            // 保留更新請求，等下一輪再試，不自行喚醒造成忙碌迴圈。
            _scopeRefreshRequested = true;
            return;
        }

        var newlyBackfilled = await BackfillScopeSensorsAsync(settings, ct);
        if (newlyBackfilled.Count > 0)
        {
            await FetchRecentStateChangesAsync(settings, newlyBackfilled, ct);
        }
    }

    internal async Task TickAsync(CancellationToken ct = default)
    {
        var settings = _settingsStore.Get();
        // 記住最近一次讀到的期望樣本數：站台停止時不再讀設定（關機路徑上資料庫未必還在）
        _expectedSamplesPerHour = ExpectedSamplesPerHour(settings);

        // 1. PrtgEnabled 為 false → 不跑。
        if (!settings.PrtgEnabled)
        {
            NoteSkip("PRTG 擷取未啟用", trackPause: false);
            return;
        }

        // 2. 連線設定不齊（PrtgUrl 空 或 PrtgClientFactory.HasUsableCredentials(settings) 為 false）→ 不跑。
        if (string.IsNullOrWhiteSpace(settings.PrtgUrl) || !PrtgClientFactory.HasUsableCredentials(settings))
        {
            NoteSkip("PRTG 連線設定不齊", trackPause: false);
            return;
        }

        // 3. 結構同步執行中（PrtgStructureSyncService.IsRunning）→ 不跑。
        if (_structureSync.IsRunning)
        {
            NoteSkip("結構同步執行中，暫停快照");
            return;
        }

        // 4. 歷史回填執行中（PrtgBackfillService 的狀態 IsRunning）→ 不跑。
        if (_backfill.GetStatus().IsRunning)
        {
            NoteSkip("歷史回填執行中，暫停快照");
            return;
        }

        // 5. 取數執行正在 PRTG 階段（SchedulerRunState 的快照裡，進度 phase 以 prtg- 開頭）→ 不跑。
        if (IsFetchRunningPrtgPhase())
        {
            NoteSkip("夜間取數正在 PRTG 階段，暫停快照");
            return;
        }

        NoteResumed();
        _lastSkipReason = null;

        // 若無退避，生效間隔隨策略設定更新
        if (_consecutiveFailures == 0)
        {
            _effectiveIntervalMinutes = PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy).SnapshotIntervalMinutes;
        }

        // 6. 距上次嘗試的間隔 < 目前生效間隔（見 §4 退避）→ 不跑。
        //    以「上次嘗試」而非「上次成功」量：失敗也要讓出間隔，退避才真的減輕 PRTG 負擔。
        var now = Now();
        if (_lastAttemptAt.HasValue && (now - _lastAttemptAt.Value) < TimeSpan.FromMinutes(_effectiveIntervalMinutes))
        {
            return;
        }

        _lastAttemptAt = now;

        // 7. 快照之前先補抓新進取數範圍、鏡像還沒有感測器的裝置（自帶 try/catch，不進退避）。
        //    跟著快照間隔走、不每分鐘跑：取數範圍計算要讀整份對應與鏡像，沒必要比快照更頻繁。
        await BackfillScopeSensorsAsync(settings, ct);

        try
        {
            await ExecuteSnapshotAsync(settings, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }

    /// <summary>
    /// 前置條件不通過：記下原因；由「通過」轉「不通過」時記下暫停開始時間。
    /// 暫停中原因改變不重設開始時間（恢復時印最後一個原因）。
    /// 「未啟用／設定不齊」不算暫停（trackPause=false）：那段期間本來就沒有在取樣，
    /// 啟用當天印「暫停 43200 分鐘、coverage 偏低」是假訊息；要量的是取數、同步、回填佔用造成的暫停。
    /// </summary>
    private void NoteSkip(string reason, bool trackPause = true)
    {
        _lastSkipReason = reason;
        if (trackPause) _skipSince ??= Now();
        else _skipSince = null;
    }

    /// <summary>
    /// 前置條件回到通過：暫停超過門檻才寫一行，讓「這段期間取樣列 coverage 偏低」看得到。
    /// 門檻內的暫停不寫——保守策略一次快照間隔就是 15 分鐘，寫了只是洗版。
    /// </summary>
    private void NoteResumed()
    {
        if (!_skipSince.HasValue) return;

        var since = _skipSince.Value;
        var now = Now();
        _skipSince = null;
        if (now - since > PauseReportThreshold)
        {
            var minutes = (int)(now - since).TotalMinutes;
            WriteOutput(
                $"快照已恢復：因「{_lastSkipReason}」暫停 {minutes} 分鐘（{since:HH:mm}～{now:HH:mm}），這段期間的取樣列 coverage 會偏低",
                LogLevel.Info);
        }
    }

    private bool IsFetchRunningPrtgPhase()
    {
        if (!_schedulerRunState.IsRunning) return false;
        if (_schedulerRunState.PrtgCompleted) return false;
        // 只看 PRTG 軌的 phase：ProgressPhase 是 NetIQ 軌的，永遠不會以 prtg- 開頭
        var prtgPhase = _schedulerRunState.PrtgProgressPhase;
        return prtgPhase != null && prtgPhase.StartsWith("prtg-", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ExecuteSnapshotAsync(SystemSettings settings, DateTime now, CancellationToken ct)
    {
        // 先確保目標集合是新的，才知道要查哪些感測器、走分批還是全站
        var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        if (_targetRefreshHour == null || _targetRefreshHour.Value != currentHour || _targetObjids == null)
        {
            RefreshTargets(settings, now, currentHour);
        }

        var targets = (_targetObjids ?? new HashSet<long>()).OrderBy(id => id).ToList();
        var tally = new SnapshotTally();

        if (targets.Count == 0)
        {
            // 沒有目標：不打 PRTG，照樣記成功並寫出已到整點的列
        }
        else if (targets.Count <= FilteredSnapshotLimit)
        {
            await FetchFilteredAsync(settings, targets, now, tally, ct);
        }
        else
        {
            var client = GetClient(settings);
            var json = await client.GetJsonAsync("api/table.json?content=sensors&columns=objid,lastvalue_raw,interval&count=50000", ct);

            var (treeSize, totalSensorsInResponse) = ParseSnapshotResponse(json, now, tally, _targetObjids ?? new HashSet<long>());
            if (treeSize.HasValue && treeSize.Value > 0 && totalSensorsInResponse < treeSize.Value)
            {
                var msg = $"[PRTG快照] 只取到 {totalSensorsInResponse} 個感測器（總數 {treeSize.Value}），快照結果可能被截斷。";
                WriteOutput(msg, LogLevel.Warn);
            }
        }

        if (tally.UnparsedIntervals > 0)
        {
            var msg = $"{tally.UnparsedIntervals} 顆感測器的掃描間隔無法解析，以 60 秒計";
            WriteOutput(msg, LogLevel.Warn);
        }

        RecordSuccess(settings, tally.Added, now);

        WriteSampledRows(_accumulator.DrainBefore(currentHour, ExpectedSamplesPerHour(settings), now));
    }

    /// <summary>
    /// 分批模式：依 objid 排序後每 <see cref="PrtgResourceGuardProbe.MaxBatchSize"/> 顆一個 filter_objid 請求。
    /// 單批失敗（非取消）只記數、其餘照做；全部批次都失敗才往外擲，交給既有退避。
    /// </summary>
    private async Task FetchFilteredAsync(
        SystemSettings settings, IReadOnlyList<long> targets, DateTime now, SnapshotTally tally, CancellationToken ct)
    {
        var batchSize = PrtgResourceGuardProbe.MaxBatchSize;
        var batchCount = (targets.Count + batchSize - 1) / batchSize;
        var failedBatches = 0;
        var requested = 0;
        Exception? lastError = null;

        var client = GetClient(settings);
        {
            for (var i = 0; i < targets.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = targets.Skip(i).Take(batchSize).ToList();
                try
                {
                    var json = await client.GetJsonAsync(
                        "api/table.json?content=sensors&columns=objid,lastvalue_raw,interval"
                        + PrtgResourceGuardProbe.BuildObjidFilter(batch), ct);
                    // 只收本批要求的 objid：PRTG 若忽略 filter_objid 會每批都回整站，
                    // 不擋的話同一顆感測器一輪會被重複累加幾十次，而「取回少於要求」的警告也不會響
                    ParseSnapshotResponse(json, now, tally, batch.ToHashSet());
                    requested += batch.Count;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedBatches++;
                    lastError = ex;
                }
            }
        }

        if (failedBatches == batchCount && lastError != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastError).Throw();
        }

        if (failedBatches > 0)
        {
            WriteOutput($"[PRTG快照] {failedBatches} 批查詢失敗，其餘已取得", LogLevel.Warn);
        }

        // 取不齊只比對成功的批次：失敗批次已在上一行出聲，不重複算成「缺」
        var received = tally.Matched;
        if (received < requested)
        {
            WriteOutput($"[PRTG快照] 要求 {requested} 顆、取回 {received} 顆，缺 {requested - received} 顆（感測器可能已在 PRTG 刪除或改變，下次結構同步後自動修正）", LogLevel.Warn);
        }
    }

    /// <summary>
    /// 解析一份 table.json 回應並逐列累積（分批與全站兩種模式共用的唯一解析入口）。
    /// </summary>
    /// <returns>回應的 treesize（沒有時 null）與 sensors 陣列長度</returns>
    private (long? TreeSize, int Total) ParseSnapshotResponse(string json, DateTime now, SnapshotTally tally, IReadOnlySet<long> accept)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PrtgClientException("PRTG 回傳內容非預期的 JSON 物件格式。");
        }

        long? treeSize = null;
        if (root.TryGetProperty("treesize", out var tsProp))
        {
            if (tsProp.ValueKind == JsonValueKind.Number && tsProp.TryGetInt64(out var tsNum))
                treeSize = tsNum;
            else if (tsProp.ValueKind == JsonValueKind.String && long.TryParse(tsProp.GetString(), out var tsStr))
                treeSize = tsStr;
        }

        if (!root.TryGetProperty("sensors", out var sensorsArr) || sensorsArr.ValueKind != JsonValueKind.Array)
        {
            return (treeSize, 0);
        }

        foreach (var el in sensorsArr.EnumerateArray())
        {
            AccumulateSensorRow(el, now, tally, accept);
        }

        return (treeSize, sensorsArr.GetArrayLength());
    }

    /// <summary>單列：只收 accept 內的感測器（全站模式＝目標集合、分批模式＝本批要求的 objid），換算（流量類轉每小時量）後進累積器。</summary>
    private void AccumulateSensorRow(JsonElement el, DateTime now, SnapshotTally tally, IReadOnlySet<long> accept)
    {
        long? objid = null;
        if (el.TryGetProperty("objid", out var objidProp))
        {
            if (objidProp.ValueKind == JsonValueKind.Number && objidProp.TryGetInt64(out var oNum))
                objid = oNum;
            else if (objidProp.ValueKind == JsonValueKind.String && long.TryParse(objidProp.GetString(), out var oStr))
                objid = oStr;
        }

        if (!objid.HasValue) return;

        if (!accept.Contains(objid.Value))
            return;

        tally.Matched++;

        double? lastValueRaw = null;
        if (el.TryGetProperty("lastvalue_raw", out var valProp))
        {
            if (valProp.ValueKind == JsonValueKind.Number && valProp.TryGetDouble(out var vNum))
                lastValueRaw = vNum;
            else if (valProp.ValueKind == JsonValueKind.String && double.TryParse(valProp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var vStr))
                lastValueRaw = vStr;
        }

        if (!lastValueRaw.HasValue) return;

        string? intervalStr = null;
        if (el.TryGetProperty("interval", out var intProp))
        {
            if (intProp.ValueKind == JsonValueKind.String)
                intervalStr = intProp.GetString();
            else if (intProp.ValueKind == JsonValueKind.Number && intProp.TryGetInt64(out var iNum))
                intervalStr = iNum.ToString(CultureInfo.InvariantCulture);
        }

        double intervalSeconds = ParseIntervalSeconds(intervalStr, ref tally.UnparsedIntervals);

        double sampleValue;
        if (_sensorTypes != null &&
            _sensorTypes.TryGetValue(objid.Value, out var sensorType) &&
            PrtgVolumeSensorTypes.IsVolume(sensorType))
        {
            sampleValue = lastValueRaw.Value * 3600.0 / intervalSeconds;
        }
        else
        {
            sampleValue = lastValueRaw.Value;
        }

        _accumulator.Add(objid.Value, now, sampleValue);
        tally.Added++;
    }

    /// <summary>一輪快照的計數（兩種模式共用）。欄位而非屬性：間隔解析以 ref 累加。</summary>
    private sealed class SnapshotTally
    {
        public int Matched;
        public int Added;
        public int UnparsedIntervals;
    }

    /// <summary>
    /// 範圍補抓：找出取數範圍 S 內「鏡像一顆感測器都沒有」且未確認為空的裝置，逐台補抓感測器。
    /// 整段失敗只出聲，不進快照的退避計數、不影響本輪快照。
    /// <para>
    /// 另處理資源守門覆寫清單中「鏡像找不到」的感測器：鏡像只含範圍內裝置，而範圍的守門項只認得
    /// 鏡像裡有的覆寫 objid——所在裝置不在主機主檔、位址又比對不到時兩邊互等，守門永遠讀不到分類而靜默失效。
    /// 這裡先向 PRTG 查這些感測器的 parentid（所在裝置），把裝置併入待補清單（不要求在範圍內、
    /// 即使鏡像已有該裝置其他感測器也補）。補抓後感測器進了鏡像，下一次 <see cref="PrtgScopeDevices.Compute"/>
    /// 的守門項就會把該裝置納入範圍，之後由結構同步持續刷新、不會被「未刷新即刪除」清掉。
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<long>> BackfillScopeSensorsAsync(SystemSettings settings, CancellationToken ct)
    {
        var newlyBackfilled = new List<long>();
        // 環境探測執行中不補抓：探測在量 PRTG 的回應時間與併發（步驟 9），疊上補抓的請求會讓量測失真。
        // 快照本身不受這道限制（它一輪只有少數請求，且探測的前置說明已涵蓋）。
        if (_probeState.Snapshot().IsRunning) return newlyBackfilled;

        try
        {
            var store = _backend.PrtgStore();

            // 「已確認為空」的清空條件：最後結構同步時間比上次記下的新＝有結構同步跑過。
            // 這個時間取裝置表（見 GetLatestStructureSyncedAt），補抓只寫感測器、不會推動它。
            var latestSynced = store.GetLatestStructureSyncedAt();
            if (latestSynced.HasValue && (!_seenStructureSyncedAt.HasValue || latestSynced.Value > _seenStructureSyncedAt.Value))
            {
                _confirmedEmptyDevices.Clear();
                _overrideObjidsNotFound.Clear();
            }
            _seenStructureSyncedAt = latestSynced;

            var scope = PrtgScopeDevices.Compute(
                store, _hosts, new PrtgMirrorGuardSource(store), settings, _sentinels.GetAll(),
                SilentConsole, _addressResolver, hostIds: null);

            var mirrorSensors = store.GetAllSensors();
            var devicesWithSensors = mirrorSensors.Select(s => s.DeviceObjid).ToHashSet();
            var scopePending = scope.DeviceObjids
                .Where(id => !devicesWithSensors.Contains(id) && !_confirmedEmptyDevices.Contains(id))
                .ToList();

            // 覆寫清單中鏡像沒有、也還沒確認查不到的感測器
            var missingOverride = new List<long>();
            if (settings.PrtgResourceGuardSensorObjids != null && settings.PrtgResourceGuardSensorObjids.Count > 0)
            {
                var mirrorObjids = mirrorSensors.Select(s => s.Objid).ToHashSet();
                missingOverride = PrtgResourceGuardTargets.ParseOverrideObjids(settings.PrtgResourceGuardSensorObjids)
                    .Where(id => !mirrorObjids.Contains(id) && !_overrideObjidsNotFound.Contains(id))
                    .OrderBy(id => id)
                    .ToList();
            }
            if (scopePending.Count == 0 && missingOverride.Count == 0) return newlyBackfilled;

            PrtgSensorBackfillResult result;
            List<long> pending;
            var client = GetClient(settings);
            {
                var overrideDevices = await LookupOverrideDevicesAsync(client, missingOverride, ct);
                // 守門覆寫清單的裝置排在前面：它們進不了鏡像時守門會靜默失效，不能被大批新進範圍的裝置擠到後面幾輪
                pending = overrideDevices.Distinct().OrderBy(id => id)
                    .Concat(scopePending.Where(id => !overrideDevices.Contains(id)).OrderBy(id => id))
                    .Take(MaxBackfillDevicesPerTick)
                    .ToList();
                if (pending.Count == 0) return newlyBackfilled;

                var fetch = new PrtgFetchService(client, store,
                    new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)), SilentConsole,
                    PrtgSensorTypeCategoryMap.ParseOverrides(settings.PrtgSensorTypeCategoryOverrides).Map);
                // 補抓寫入的列 SyncedAt 是當下時間（沿用 mapper），晚於任何已開始的結構同步起點，
                // 不會被那趟「未刷新即刪除」清掉
                result = await fetch.BackfillSensorsForDevicesAsync(pending, settings.PrtgFetchConcurrency, ct);
            }

            foreach (var id in result.EmptyDevices) _confirmedEmptyDevices.Add(id);
            // 覆寫清單的感測器補抓後仍不在鏡像（所在裝置回 0 顆、或回傳裡沒有這顆）→ 記為查不到，
            // 否則它每一輪都會被重查 parentid、重補同一台，永遠不收斂。清空時機同「已確認為空」。
            if (missingOverride.Count > 0 && result.FailedDevices.Count == 0)
            {
                var nowMirrored = store.GetAllSensors().Select(s => s.Objid).ToHashSet();
                foreach (var id in missingOverride.Where(id => !nowMirrored.Contains(id)))
                    _overrideObjidsNotFound.Add(id);
            }
            if (result.SensorsWritten > 0)
            {
                WriteOutput($"[PRTG快照] 已為 {pending.Count} 台新進取數範圍的裝置補上 {result.SensorsWritten} 個感測器", LogLevel.Info);
            }
            if (result.FailedDevices.Count > 0)
            {
                WriteOutput($"[PRTG快照] {result.FailedDevices.Count} 台新進取數範圍的裝置感測器補抓失敗，下次再試", LogLevel.Warn);
            }

            newlyBackfilled = pending.Except(result.FailedDevices).Except(result.EmptyDevices).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // WriteOutput 的 Warn 會同時寫 Log.Warn
            WriteOutput($"[PRTG快照] 新進取數範圍裝置的感測器補抓失敗（不影響本輪快照）：{ex.Message}", LogLevel.Warn);
        }

        return newlyBackfilled;
    }

    private async Task FetchRecentStateChangesAsync(SystemSettings settings, IReadOnlyList<long> newlyBackfilled, CancellationToken ct)
    {
        try
        {
            var store = _backend.PrtgStore();
            var client = GetClient(settings);
            var fetch = new PrtgFetchService(client, store,
                new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)), SilentConsole,
                PrtgSensorTypeCategoryMap.ParseOverrides(settings.PrtgSensorTypeCategoryOverrides).Map);

            var today = Now().Date;
            var fromDate = today.AddDays(-1);
            await fetch.FetchStateChangesRangeAsync(fromDate, today, newlyBackfilled, settings.PrtgFetchConcurrency, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteOutput($"[PRTG快照] 新進裝置狀態變更補抓失敗（不影響快照）：{ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// 向 PRTG 查覆寫清單感測器的所在裝置（parentid），每 <see cref="PrtgResourceGuardProbe.MaxBatchSize"/> 顆一批。
    /// 回傳中找不到的 objid 記進「已查過找不到」。請求失敗只寫一行警告並回傳已查到的部分：
    /// 不讓這段拖垮同一輪的範圍補抓（失敗的批次不記成找不到，下一輪再查）。
    /// </summary>
    private async Task<List<long>> LookupOverrideDevicesAsync(PrtgClient client, IReadOnlyList<long> objids, CancellationToken ct)
    {
        var devices = new List<long>();
        if (objids.Count == 0) return devices;

        try
        {
            for (var i = 0; i < objids.Count; i += PrtgResourceGuardProbe.MaxBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = objids.Skip(i).Take(PrtgResourceGuardProbe.MaxBatchSize).ToList();
                var json = await client.GetJsonAsync(
                    "api/table.json?content=sensors&columns=objid,parentid"
                    + PrtgResourceGuardProbe.BuildObjidFilter(batch), ct);

                var found = new HashSet<long>();
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("sensors", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            var objid = ReadLong(el, "objid");
                            var parent = ReadLong(el, "parentid");
                            // 只認本批要求的 objid：PRTG 忽略 filter 時會回整站，不能把無關裝置拉進來
                            if (!objid.HasValue || !parent.HasValue || !batch.Contains(objid.Value)) continue;
                            found.Add(objid.Value);
                            devices.Add(parent.Value);
                        }
                    }
                }

                foreach (var id in batch)
                {
                    if (!found.Contains(id)) _overrideObjidsNotFound.Add(id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteOutput($"[PRTG快照] 查詢守門覆寫清單感測器的所在裝置失敗（不影響本輪快照）：{ex.Message}", LogLevel.Warn);
        }

        return devices;
    }

    private static long? ReadLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var num)) return num;
        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var str)) return str;
        return null;
    }

    /// <summary>取數範圍與補抓過程的輸出在這條路徑上不需要（守門偵測警告不該洗進快照執行輸出）。</summary>
    private sealed class SilentRunConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    /// <summary>
    /// 把已從累積器取出的整點列寫進資料庫。寫入失敗時列留在待寫清單、下次再試——
    /// 取出的列在累積器裡已經不存在，讓例外往上丟等於整個小時的樣本無聲消失；
    /// 這也不是 PRTG 的失敗，不能進退避計數。
    /// </summary>
    private void WriteSampledRows(IReadOnlyList<PrtgValueRow> rows)
    {
        lock (_pendingWrite)
        {
            _pendingWrite.AddRange(rows);
            if (_pendingWrite.Count == 0) return;

            try
            {
                _backend.PrtgStore().MergeSampledValues(_pendingWrite);
                _pendingWrite.Clear();
            }
            catch (Exception ex)
            {
                var dropped = Math.Max(0, _pendingWrite.Count - MaxPendingWriteRows);
                if (dropped > 0) _pendingWrite.RemoveRange(0, dropped);
                var msg = $"快照樣本寫入資料庫失敗，{_pendingWrite.Count} 列留待下次重試"
                          + (dropped > 0 ? $"（已超過待寫上限，最舊的 {dropped} 列捨棄）" : "")
                          + $"：{ex.Message}";
                WriteOutput(msg, LogLevel.Warn);
                Log.Error(ex, "PRTG 快照樣本寫入資料庫失敗");
            }
        }
    }

    private void RefreshTargets(SystemSettings settings, DateTime now, DateTime currentHour)
    {
        var store = _backend.PrtgStore();
        var today = now.Date;
        // 與校準同一個來源：錨點今天、回看 30 天的最近一次對應
        var (_, hostMaps) = store.GetLatestHostMapWithDate(30, today);
        var okDeviceIds = hostMaps
            .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
            .Select(m => m.DeviceObjid)
            .Distinct()
            .ToList();

        var targets = store.GetValueFetchTargets(settings.PrtgSensorTypeWhitelist, okDeviceIds);
        _targetObjids = targets.ToHashSet();

        var statuses = store.GetSensorStatuses();
        _sensorTypes = statuses
            .GroupBy(s => s.Objid)
            .ToDictionary(g => g.Key, g => g.First().SensorType);

        _targetRefreshHour = currentHour;
    }

    private static double ParseIntervalSeconds(string? intervalStr, ref int unparsedCount)
    {
        if (!string.IsNullOrWhiteSpace(intervalStr))
        {
            var trimmed = intervalStr.Trim();
            if (double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var numSecs) && numSecs > 0)
            {
                return numSecs;
            }

            var match = IntervalRegex.Match(trimmed);
            if (match.Success)
            {
                var valStr = match.Groups[1].Value;
                var unit = match.Groups[2].Value;

                if (double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) && val > 0)
                {
                    if (string.Equals(unit, "s", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "sec", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "seconds", StringComparison.OrdinalIgnoreCase))
                    {
                        return val;
                    }
                    if (string.Equals(unit, "m", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "min", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "minutes", StringComparison.OrdinalIgnoreCase))
                    {
                        return val * 60.0;
                    }
                    if (string.Equals(unit, "h", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "hr", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "hours", StringComparison.OrdinalIgnoreCase))
                    {
                        return val * 3600.0;
                    }
                }
            }
        }

        unparsedCount++;
        return 60.0;
    }

    private void RecordSuccess(SystemSettings settings, int addedCount, DateTime now)
    {
        _lastSuccessAt = now;
        _lastSensorCount = addedCount;

        var strategyInterval = PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy).SnapshotIntervalMinutes;
        if (_effectiveIntervalMinutes != strategyInterval)
        {
            int oldInterval = _effectiveIntervalMinutes;
            _effectiveIntervalMinutes = strategyInterval;
            var msg = $"PRTG 快照成功，生效間隔由 {oldInterval} 分鐘恢復為策略設定值 {strategyInterval} 分鐘。";
            WriteOutput(msg, LogLevel.Info);
        }
        _consecutiveFailures = 0;

        // 擷取紀錄是記帳，寫不進資料庫不是 PRTG 的失敗，不能往外丟進退避計數
        try
        {
            new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)).Record(PrtgFreshnessStore.Snapshot, addedCount);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "PRTG 快照擷取紀錄寫入失敗");
        }
    }

    private void RecordFailure(Exception ex)
    {
        // 失敗的一輪丟掉重用中的 client：PrtgClient 會把帳號類憑證失敗「黏住」（防 PRTG 帳號被鎖），
        // 重用後若不丟，PRTG 端解鎖或修好帳號但站台設定沒變時，快照會永遠卡在那個失敗上
        DisposeClient();
        _consecutiveFailures++;
        if (_consecutiveFailures >= 3 && _consecutiveFailures % 3 == 0)
        {
            int oldInterval = _effectiveIntervalMinutes;
            int newInterval = Math.Min(60, oldInterval * 2);
            if (newInterval != oldInterval)
            {
                _effectiveIntervalMinutes = newInterval;
                var msg = $"PRTG 快照連續失敗達 {_consecutiveFailures} 次，生效間隔由 {oldInterval} 分鐘拉長為 {newInterval} 分鐘。";
                WriteOutput(msg, LogLevel.Warn);
            }
        }
        Log.Error(ex, "PRTG 快照執行失敗（第 {Failures} 次連續失敗）：{Message}", _consecutiveFailures, ex.Message);
    }

    private void WriteOutput(string message, NLog.LogLevel? logLevel = null)
    {
        ExecutionOutputs.Add(message);
        if (ExecutionOutputs.Count > MaxExecutionOutputs) ExecutionOutputs.RemoveAt(0);
        Console?.WriteLine(message);
        if (logLevel == NLog.LogLevel.Warn)
            Log.Warn(message);
        else if (logLevel == NLog.LogLevel.Info)
            Log.Info(message);
    }

    private void OnStopping()
    {
        try
        {
            var now = Now();
            WriteSampledRows(_accumulator.DrainAll(_expectedSamplesPerHour, now));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "站台關閉時寫出 PRTG 快照殘存樣本失敗");
        }
    }

    /// <summary>
    /// 取得重用的 PRTG client：每輪都建新的會讓 SocketsHttpHandler 的連線池與 TLS 握手每輪重來。
    /// 設定指紋（連線位址、認證方式、帳號、各憑證密文、逾時、忽略憑證錯誤）不同才換新的。
    /// 只由 ExecuteAsync 的單一迴圈呼叫，沒有併發。
    /// </summary>
    private PrtgClient GetClient(SystemSettings settings)
    {
        var fingerprint = string.Join("\u001f",
            settings.PrtgUrl ?? string.Empty,
            settings.PrtgAuthMode ?? string.Empty,
            settings.PrtgUsername ?? string.Empty,
            settings.PrtgPasswordEnc ?? string.Empty,
            settings.PrtgPasshashEnc ?? string.Empty,
            settings.PrtgApiTokenEnc ?? string.Empty,
            settings.PrtgTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings.PrtgIgnoreSslErrors ? "1" : "0");

        if (_client == null || _clientFingerprint != fingerprint)
        {
            var created = CreateClient(settings);
            _client?.Dispose();
            _client = created;
            _clientFingerprint = fingerprint;
        }
        return _client;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        DisposeClient();
    }

    public override void Dispose()
    {
        DisposeClient();
        base.Dispose();
    }

    private void DisposeClient()
    {
        _client?.Dispose();
        _client = null;
        _clientFingerprint = null;
    }

    internal virtual PrtgClient CreateClient(SystemSettings settings)
    {
        return ClientFactory != null
            ? ClientFactory()
            : PrtgClientFactory.Create(settings);
    }
}
