using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
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
/// <param name="PendingSamples">累積器中的樣本及已結算但待資料庫重試的列數</param>
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
    private static readonly TimeSpan TimelineSliceDeadline = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private volatile bool _scopeRefreshRequested;
    private int _recentStateQueueHttpDeferred;

    private static readonly Regex IntervalRegex = new(@"^(\d+(?:\.\d+)?)\s*([a-zA-Z]+)$", RegexOptions.Compiled);

    private readonly ISystemSettingsStore _settingsStore;
    private readonly StorageBackend _backend;
    private readonly SchedulerRunState _schedulerRunState;
    private readonly PrtgStructureSyncService _structureSync;
    private readonly PrtgBackfillService _backfill;
    private readonly IHostApplicationLifetime _lifetime;

    private readonly PrtgSnapshotAccumulator _accumulator = new();
    private readonly PrtgTrustedSnapshotParser _trustedSnapshotParser = new();

    /// <summary>每次 Drain 都有獨立穩定 ID；提交結果不明時原批重試，不能把新列混入同一 ID。</summary>
    private readonly PrtgSnapshotJournal _journal;
    private bool _journalLoaded;
    private string? _journalEndpoint;
    private string? _journalError;
    private sealed class JournalWriteException : IOException { }
    private readonly List<PrtgSnapshotJournal.Batch> _pendingWrite = new();
    private int _pendingRows;

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

    /// <summary>鏡像補抓與近期狀態查詢每輪必須讓出採樣；測試可縮短期限。</summary>
    internal TimeSpan ScopeWorkBudget { get; set; } = TimeSpan.FromSeconds(30);

    internal PrtgSamplingActivity SamplingActivity { get; set; } = PrtgSamplingActivity.Shared;

    private DateTime? _targetRefreshHour;
    private string? _targetWhitelistFingerprint;
    private HashSet<long>? _targetObjids;
    private Dictionary<long, string>? _sensorTypes;


    /// <summary>每輪範圍補抓最多處理的裝置數（由 objid 小到大），其餘留給下一輪。</summary>
    internal const int MaxBackfillDevicesPerTick = 50;

    private static readonly IRunConsole SilentConsole = new SilentRunConsole();

    private readonly IHostStore _hosts;
    private readonly ISentinelStore _sentinels;
    private readonly PrtgProbeRunState _probeState;
    private readonly PrtgSensorTimelineConsumer _timelineConsumer;
    internal Func<CancellationToken, Task<PrtgSensorTimelineTick>> TimelineTick { get; set; }
    internal Action? BeforeOperationScopeCapture { get; set; }
    private readonly PrtgSnapshotDiagnosticsStore _diagnosticsStore;
    public PrtgSnapshotDiagnosticsService Diagnostics { get; }
    internal Action? ScopeRefreshAdmissionAccepted { get; set; }

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
    private Action? _operationCheckpoint;
    private string? _clientFingerprint;
    private string? _admissionPlanFingerprint;
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
        var persistedAdmission = new PrtgCapacityAdmissionPlanStore(
            _backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey)).ReadCurrent(DateTimeOffset.UtcNow);
        if (persistedAdmission is not null) PrtgRequestBudget.Shared.SetAdmissionPlan(persistedAdmission);
        _journal = new PrtgSnapshotJournal(_backend);
        _schedulerRunState = schedulerRunState ?? throw new ArgumentNullException(nameof(schedulerRunState));
        _structureSync = structureSyncService ?? throw new ArgumentNullException(nameof(structureSyncService));
        _backfill = backfillService ?? throw new ArgumentNullException(nameof(backfillService));
        _timelineConsumer = new PrtgSensorTimelineConsumer(_settingsStore, _backend,
            () => _schedulerRunState.IsRunning || _probeState.Snapshot().IsRunning ||
                _structureSync.IsRunning || _backfill.GetStatus().IsRunning,
            settings => PrtgClientFactory.Create(settings), SamplingActivity);
        TimelineTick = _timelineConsumer.TickAsync;
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _diagnosticsStore = new PrtgSnapshotDiagnosticsStore(_backend.Blob("prtg_snapshot_diagnostics_v1"), () =>
        {
            var current = _settingsStore.Get();
            return Math.Min(current.PrtgRetentionDays, current.RetentionDays);
        });
        Diagnostics = new PrtgSnapshotDiagnosticsService(_diagnosticsStore,
            (from, to) => _backend.PrtgStore().GetSnapshotValueCoverage(from, to));
        RecordDiagnostic(Now(), "startup", "startup-partial-hour");

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
        _targetRefreshHour = null;
        _targetWhitelistFingerprint = null;
        _targetObjids = null;
        _sensorTypes = null;
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

    public PrtgSnapshotStatus GetStatus()
    {
        int pendingRows;
        lock (_pendingWrite) pendingRows = _pendingRows;
        return new(
            LastSuccessAt: _lastSuccessAt,
            LastSensorCount: _lastSensorCount,
            IntervalMinutes: _effectiveIntervalMinutes,
            ConsecutiveFailures: _consecutiveFailures,
            PendingSamples: _accumulator.SampleCount + pendingRows,
            LastSkipReason: _lastSkipReason);
    }

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

        var timelineTask = RunTimelineAsync(stoppingToken);
        try
        {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 採樣優先；未完成的範圍更新不能一直占用整個服務迴圈。
                await TickAsync(stoppingToken);
                if (_scopeRefreshRequested)
                {
                    _scopeRefreshRequested = false;
                    await ScopeRefreshTickAsync(stoppingToken);
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
        finally
        {
            await timelineTask;
            _journal.Dispose(); // 所有輪詢確實結束後才釋放跨程序擁有權。
        }
    }

    private async Task RunTimelineAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(5);
            using var slice = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            slice.CancelAfter(TimelineSliceDeadline);
            try { delay = (await TimelineTick(slice.Token)).Delay; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { Log.Warn("PRTG timeline 本輪達五分鐘上限；已提交水位保留供下輪續行"); }
            catch (Exception ex) { Log.Warn(ex, "PRTG timeline 背景工作本輪失敗；保留已提交逐 sensor 水位"); }
            try { await Task.Delay(delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
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

        // Queue scope reconciliation is local metadata work. It must run even when the
        // snapshot/profile plan does not admit HTTP, so removed business devices can be
        // stopped promptly. Keep the same settings/scope fence used by the admitted work;
        // transport calls remain below the joint-admission checks.
        var queueReconciled = false;
        RecentStateChangeScopeContext? queueContext = null;
        try
        {
            await WithOperationScopeAsync(settings, ct, _ =>
            {
                _operationCheckpoint?.Invoke();
                queueContext = ReconcileRecentStateChangesForCurrentScope(settings);
                _operationCheckpoint?.Invoke();
                queueReconciled = true;
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _scopeRefreshRequested = true;
            WriteOutput("[PRTG快照] 設定或監看範圍已變更，本輪本機 Queue 整理停止；下輪重新檢查。", LogLevel.Info);
            return;
        }
        if (!queueReconciled)
        {
            _scopeRefreshRequested = true;
            return;
        }

        var capacitySelection = ResolveSnapshotCapacitySelection(settings, DateTime.Now);
        if (!SnapshotCapacityAllows(capacitySelection, settings, out var capacityEstimate))
        {
            // Scope discovery/backfill and recent-state collection also issue snapshot-shaped
            // table calls. Keep the request pending until the same measured capacity gate used
            // by the regular snapshot cadence admits this effective scope.
            _scopeRefreshRequested = true;
            NoteCapacitySkip(capacitySelection, capacityEstimate);
            return;
        }
        if (capacitySelection.SensorObjids.Count == 0)
        {
            _admissionPlanFingerprint = null;
            _scopeRefreshRequested = true;
            NoteSkip("snapshot-empty-scope-idle", "目前有效快照範圍為空；本輪只完成本機 Queue 整理，沒有啟動 HTTP。", trackPause: false, targetCount: 0);
            return;
        }
        if (!PrtgCapacityRuntimeAdmission.TryGetCurrent(_backend, _hosts, settings, capacitySelection,
            out var admission, out var admissionReason))
        {
            _scopeRefreshRequested = true;
            NoteJointCapacitySkip(capacitySelection, admissionReason);
            return;
        }
        _admissionPlanFingerprint = admission!.Fingerprint;

        ScopeRefreshAdmissionAccepted?.Invoke();
        await WithOperationScopeAsync(settings, ct, async token =>
        {
            // Admission happened before the operation captured its cancellation/version fence.
            // Re-resolve inside that fence and require the exact admitted proof to still match.
            _operationCheckpoint?.Invoke();
            var operationSelection = ResolveSnapshotCapacitySelection(settings, DateTime.Now);
            if (!SameCapacitySelection(capacitySelection, operationSelection))
            {
                _scopeRefreshRequested = true;
                NoteCapacityScopeChanged(operationSelection);
                return;
            }
            if (!SnapshotCapacityAllows(operationSelection, settings, out var operationEstimate))
            {
                _scopeRefreshRequested = true;
                NoteCapacitySkip(operationSelection, operationEstimate);
                return;
            }
            if (!PrtgCapacityRuntimeAdmission.TryGetCurrent(_backend, _hosts, settings, operationSelection,
                out var operationAdmission, out var operationAdmissionReason) || operationAdmission?.Fingerprint != admission?.Fingerprint)
            {
                _scopeRefreshRequested = true;
                NoteJointCapacitySkip(operationSelection, operationAdmissionReason);
                return;
            }
            var currentQueueContextMatches = queueContext is not null &&
                queueContext.SourceIdentityHash == SourceIdentityHash(settings) &&
                queueContext.ScopeVersionHash == ScopeVersionHash(new PrtgScopeRevisionReader(_backend, _hosts).Read()) &&
                queueContext.LocalToday == Now().Date;
            if (!currentQueueContextMatches)
            {
                _scopeRefreshRequested = true;
                NoteCapacityScopeChanged(operationSelection);
                return;
            }
            _admissionPlanFingerprint = operationAdmission!.Fingerprint;

            await RunBoundedScopeWorkAsync(token, async slice =>
            {
                await BackfillScopeSensorsAsync(settings, queueContext!, slice);
                _operationCheckpoint?.Invoke();
                var refreshedSelection = ResolveSnapshotCapacitySelection(settings, DateTime.Now);
                if (!SnapshotCapacityAllows(refreshedSelection, settings, out var refreshedEstimate))
                {
                    _scopeRefreshRequested = true;
                    NoteCapacitySkip(refreshedSelection, refreshedEstimate);
                    return;
                }
                await FetchRecentStateChangesAsync(settings, slice);
            });
        });
    }

    private async Task RunBoundedScopeWorkAsync(CancellationToken parent, Func<CancellationToken, Task> work)
    {
        using var slice = CancellationTokenSource.CreateLinkedTokenSource(parent);
        slice.CancelAfter(ScopeWorkBudget);
        try { await work(slice.Token); }
        catch (OperationCanceledException) when (slice.IsCancellationRequested && !parent.IsCancellationRequested)
        {
            _scopeRefreshRequested = true;
            _targetRefreshHour = null;
            WriteOutput("[PRTG快照] 本輪範圍補抓已達期限，未完成項留待下一輪；繼續日常採樣。", LogLevel.Warn);
        }
    }

    private async Task WithOperationScopeAsync(SystemSettings settings, CancellationToken parent, Func<CancellationToken, Task> work)
    {
        using var operation = new PrtgOperationScope(settings, _settingsStore.Get, parent, new PrtgScopeRevisionReader(_backend, _hosts).Read, "快照與範圍補抓");
        _operationCheckpoint = operation.Checkpoint;
        try
        {
            operation.Checkpoint();
            await work(operation.Token);
            operation.CompletedStage("本輪快照／補抓工作已返回");
        }
        catch (OperationCanceledException) when (!parent.IsCancellationRequested && operation.SettingsChanged)
        {
            _targetRefreshHour = null;
            _lastSkipReason = "PRTG 設定已變更，本輪快照停止；已取得的樣本保留";
            WriteOutput(_lastSkipReason, LogLevel.Info);
        }
        finally
        {
            _operationCheckpoint = null;
            if (_client != null) _client.OperationCheckpoint = null;
        }
    }

    internal async Task TickAsync(CancellationToken ct = default)
    {
        var settings = _settingsStore.Get();
        // 新樣本保存採集時的涵蓋率；此值也供尚未帶涵蓋率的程序內累積列結算使用。
        _expectedSamplesPerHour = ExpectedSamplesPerHour(settings);

        // 已結算的舊樣本是採集事實；即使之後停用 PRTG，資料庫恢復時仍須先補寫。
        // 這裡只重試待寫列，不開新的 PRTG 工作。
        if (!RestoreJournal(settings)) return;
        FlushAccumulator(Now(), all: false);
        if (_journalError != null) return;

        // 1. PrtgEnabled 為 false → 不跑。
        if (!settings.PrtgEnabled)
        {
            NoteSkip(PrtgSnapshotSkipReasonCodes.PrtgDisabled, "PRTG 擷取未啟用", trackPause: false);
            return;
        }

        // 2. 連線設定不齊（PrtgUrl 空 或 PrtgClientFactory.HasUsableCredentials(settings) 為 false）→ 不跑。
        if (string.IsNullOrWhiteSpace(settings.PrtgUrl) || !PrtgClientFactory.HasUsableCredentials(settings))
        {
            NoteSkip(PrtgSnapshotSkipReasonCodes.ConnectionNotConfigured, "PRTG 連線設定不齊", trackPause: false);
            return;
        }

        // 其他作業只可短暫讓出一個策略採樣間隔，不能讓採樣永久等待。
        // 已有採樣時以最近嘗試為期限起點；首次啟動則從首次等待開始。
        var maintenance = _structureSync.IsRunning
            ? (PrtgSnapshotSkipReasonCodes.StructureSyncActive, "結構同步執行中，暫停快照")
            : _backfill.GetStatus().IsRunning
                ? (PrtgSnapshotSkipReasonCodes.BackfillActive, "歷史回填執行中，暫停快照")
                : IsFetchRunningPrtgPhase()
                    ? (PrtgSnapshotSkipReasonCodes.NightlyFetchPrtgPhase, "夜間取數正在 PRTG 階段，暫停快照")
                    : ((string, string)?)null;
        if (maintenance is { } busy)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1,
                PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy).SnapshotIntervalMinutes));
            var waitingFrom = _lastAttemptAt ?? _skipSince ?? Now();
            if (Now() - waitingFrom < interval)
            {
                NoteSkip(busy.Item1, busy.Item2);
                return;
            }
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

        // 採樣取得優先權時中止背景歷史頁；整個區段結束後才准入下一頁。
        using var samplingPriority = SamplingActivity.BeginSampling();

        // 7. 快照之前先補抓新進取數範圍、鏡像還沒有感測器的裝置（自帶 try/catch，不進退避）。
        //    跟著快照間隔走、不每分鐘跑：取數範圍計算要讀整份對應與鏡像，沒必要比快照更頻繁。
        BeforeOperationScopeCapture?.Invoke();
        await WithOperationScopeAsync(settings, ct, async token =>
        {
            // A prior tick's proof is never reusable as authority for this tick's work.
            // Every HTTP path below needs admission for the current fenced selection.
            _admissionPlanFingerprint = null;
            // Re-read the source binding after PrtgOperationScope captures settings/revisions.
            // A source generation change after the first restore must stop before any PRTG HTTP.
            if (!RestoreJournal(settings)) return;
            _operationCheckpoint?.Invoke();
            var queueContext = ReconcileRecentStateChangesForCurrentScope(settings);
            _operationCheckpoint?.Invoke();

            PrtgSnapshotTargetSelection preflightSelection;
            PrtgSnapshotCapacityEstimate preflightEstimate;
            try
            {
                preflightSelection = ResolveSnapshotCapacitySelection(settings, now);
                if (!SnapshotCapacityAllows(preflightSelection, settings, out preflightEstimate))
                {
                    NoteCapacitySkip(preflightSelection, preflightEstimate);
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (JournalWriteException) { return; }
            catch (Exception ex) { RecordFailure(ex); return; }

            if (preflightSelection.SensorObjids.Count == 0)
            {
                // Empty effective scope is an idle, local-only pass: reconcile queue metadata
                // and let ExecuteSnapshot flush any completed accumulator without HTTP.
                _admissionPlanFingerprint = null;
                await ExecuteSnapshotAsync(settings, now, token, preflightSelection);
                return;
            }

            if (!PrtgCapacityRuntimeAdmission.TryGetCurrent(_backend, _hosts, settings, preflightSelection,
                out var preflightAdmission, out var admissionReason, allowBoundedSingleBatchRecovery: true))
            {
                _admissionPlanFingerprint = null;
                _scopeRefreshRequested = true;
                NoteJointCapacitySkip(preflightSelection, admissionReason);
                return;
            }
            _operationCheckpoint?.Invoke();
            _admissionPlanFingerprint = preflightAdmission!.Fingerprint;
            var boundedRecovery = admissionReason == PrtgCapacityRuntimeAdmission.BoundedSingleBatchRecoveryReason;

            // Sample the exact, already-admitted mirror scope first. A bounded backfill can
            // expand that scope; it cannot borrow this pass's capacity proof for later work.
            var snapshotAllowed = false;
            try
            {
                snapshotAllowed = await ExecuteSnapshotAsync(settings, now, token, preflightSelection,
                    preflightAdmission.Fingerprint, allowBoundedSingleBatchRecovery: boundedRecovery);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (JournalWriteException) { /* 待寫錯誤已有獨立狀態，不算 PRTG 連線失敗。 */ }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
            if (!snapshotAllowed) return;
            if (boundedRecovery)
            {
                NoteBoundedSnapshotRecovery(preflightSelection);
                return;
            }

            if (maintenance is null && !_probeState.Snapshot().IsRunning)
            {
                await RunBoundedScopeWorkAsync(token, async slice =>
                {
                    // The successful snapshot used the admitted mirror selection. Any sensor
                    // rows added here are eligible only after a fresh pass proves their scope.
                    _targetRefreshHour = null;
                    _targetWhitelistFingerprint = null;
                    _targetObjids = null;
                    _sensorTypes = null;
                    await BackfillScopeSensorsAsync(settings, queueContext, slice);
                    _operationCheckpoint?.Invoke();

                    var refreshedSelection = ResolveSnapshotCapacitySelection(settings, Now());
                    if (!SameCapacitySelection(preflightSelection, refreshedSelection))
                    {
                        _scopeRefreshRequested = true;
                        if (!SnapshotCapacityAllows(refreshedSelection, settings, out var refreshedEstimate))
                            NoteCapacitySkip(refreshedSelection, refreshedEstimate);
                        else
                            NoteCapacityScopeChanged(refreshedSelection);
                        return;
                    }
                    if (!SnapshotCapacityAllows(refreshedSelection, settings, out var estimate))
                    {
                        _scopeRefreshRequested = true;
                        NoteCapacitySkip(refreshedSelection, estimate);
                        return;
                    }
                    if (!PrtgCapacityRuntimeAdmission.TryGetCurrent(_backend, _hosts, settings,
                        refreshedSelection, out var currentAdmission, out var currentAdmissionReason) ||
                        currentAdmission?.Fingerprint != preflightAdmission.Fingerprint)
                    {
                        _scopeRefreshRequested = true;
                        NoteJointCapacitySkip(refreshedSelection, currentAdmissionReason);
                        return;
                    }

                    _admissionPlanFingerprint = currentAdmission!.Fingerprint;
                    await FetchRecentStateChangesAsync(settings, slice);
                });
            }
            else
            {
                _scopeRefreshRequested = true;
            }
        });
    }

    /// <summary>
    /// 前置條件不通過：記下原因；由「通過」轉「不通過」時記下暫停開始時間。
    /// 暫停中原因改變不重設開始時間（恢復時印最後一個原因）。
    /// 「未啟用／設定不齊」不算暫停（trackPause=false）：那段期間本來就沒有在取樣，
    /// 啟用當天印「暫停 43200 分鐘、coverage 偏低」是假訊息；要量的是取數、同步、回填佔用造成的暫停。
    /// </summary>
    private void NoteSkip(string reasonCode, string reason, bool trackPause = true, int? targetCount = null)
    {
        _lastSkipReason = reason;
        RecordDiagnostic(Now(), "skip", reason, targetCount ?? _targetObjids?.Count ?? 0, reasonCode: reasonCode);
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

    private async Task<bool> ExecuteSnapshotAsync(SystemSettings settings, DateTime now, CancellationToken ct,
        PrtgSnapshotTargetSelection? expectedSelection = null, string? expectedAdmissionFingerprint = null,
        bool allowBoundedSingleBatchRecovery = false)
    {
        var capacitySelection = ResolveSnapshotCapacitySelection(settings, now);
        if (expectedSelection is not null && !SameCapacitySelection(expectedSelection, capacitySelection))
        {
            _scopeRefreshRequested = true;
            NoteCapacityScopeChanged(capacitySelection);
            return false;
        }
        if (!SnapshotCapacityAllows(capacitySelection, settings, out var capacityEstimate))
        {
            _scopeRefreshRequested = true;
            NoteCapacitySkip(capacitySelection, capacityEstimate);
            return false;
        }
        var targets = capacitySelection.SensorObjids;
        var emptyScope = targets.Count == 0;
        string? admissionFingerprint = null;
        if (!emptyScope)
        {
            if (!PrtgCapacityRuntimeAdmission.TryGetCurrent(_backend, _hosts, settings, capacitySelection,
                out var admission, out var admissionReason, allowBoundedSingleBatchRecovery))
            {
                _scopeRefreshRequested = true;
                NoteJointCapacitySkip(capacitySelection, admissionReason);
                return false;
            }
            if (expectedAdmissionFingerprint is not null && admission!.Fingerprint != expectedAdmissionFingerprint)
            {
                _scopeRefreshRequested = true;
                NoteJointCapacitySkip(capacitySelection, "admitted-plan-changed");
                return false;
            }
            admissionFingerprint = admission!.Fingerprint;
        }
        _admissionPlanFingerprint = admissionFingerprint;
        if (emptyScope)
            NoteSkip("snapshot-empty-scope-idle", "目前有效快照範圍為空；已完成無 HTTP 的空閒快照。", trackPause: false, targetCount: 0);
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        lock (_pendingWrite)
        {
            if (_accumulator.EntryCount + _pendingRows + targets.Count > PrtgSnapshotJournal.MaxRows ||
                _journal.SavedBytes + ((long)targets.Count + 1) * PrtgSnapshotJournal.ReservedBytesPerRow > PrtgSnapshotJournal.MaxBytes)
            {
                JournalFailed(new InvalidDataException("待寫空間不足以容納下一輪快照；等待資料庫恢復"));
                return true;
            }
        }
        var tally = new SnapshotTally();
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
        var trustStrategy = new PrtgTrustedSamplingStrategyStateStore(
            _backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey))
            .GetCurrent(policy, strategyName, strategyMinutes, DateTime.UtcNow);

        // Only each in-flight batch reads its profiles/identities. No fleet-sized
        // profile dictionary or per-sensor metadata HTTP requests enter this cadence.

        if (targets.Count == 0)
        {
            // 沒有目標：不打 PRTG，照樣記成功並寫出已到整點的列
        }
        else
        {
            await FetchFilteredAsync(settings, targets, now, tally, policy, trustStrategy, capacitySelection, ct);
        }

        if (tally.UnparsedIntervals > 0)
        {
            var msg = $"{tally.UnparsedIntervals} 顆感測器的掃描間隔無法解析，以 60 秒計";
            WriteOutput(msg, LogLevel.Warn);
        }

        RecordSuccess(settings, tally.Added, now);

        FlushAccumulator(now, all: false);
        return true;
    }

    private void NoteBoundedSnapshotRecovery(PrtgSnapshotTargetSelection selection)
    {
        _lastSkipReason = "容量證據恢復中：本輪只執行一個 50-ID 以下的 timeout-bounded 快照；一般範圍工作須等 5 筆新鮮同形成功樣本。";
        RecordDiagnostic(Now(), "recovery", _lastSkipReason, selection.SensorObjids.Count,
            reasonCode: "snapshot-capacity-bounded-recovery");
    }

    private PrtgSnapshotTargetSelection ResolveSnapshotCapacitySelection(SystemSettings settings, DateTime now)
    {
        // Target scope can change within an hour. Refresh the shared base target cache and sensor
        // types on every admission check so pilots and executing work use current mappings.
        var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var activeMappedDeviceCount = RefreshTargets(settings, now, currentHour);

        var baseTargets = (_targetObjids ?? new HashSet<long>()).OrderBy(id => id).ToArray();
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var store = _backend.PrtgStore();
        var guardSensors = policy.Ready(settings.PrtgUrl) && settings.PrtgResourceGuardEnabled
            ? PrtgResourceGuardTargets.Resolve(new PrtgMirrorGuardSource(store), settings,
                _sentinels.GetAll(), SilentConsole, _addressResolver).SensorObjids
            : Array.Empty<long>();
        var targets = PrtgSnapshotTargetResolver.SelectEffectiveTargets(baseTargets, policy, settings, guardSensors);
        return PrtgSnapshotTargetResolver.CreateSelection(targets, activeMappedDeviceCount, _backend, settings, policy);
    }

    private static bool SameCapacitySelection(PrtgSnapshotTargetSelection left,
        PrtgSnapshotTargetSelection right) =>
        string.Equals(left.ScopeFingerprint, right.ScopeFingerprint, StringComparison.Ordinal) &&
        string.Equals(left.EndpointFingerprint, right.EndpointFingerprint, StringComparison.Ordinal) &&
        string.Equals(left.RequestShapeFingerprint, right.RequestShapeFingerprint, StringComparison.Ordinal);

    private void NoteCapacityScopeChanged(PrtgSnapshotTargetSelection selection) =>
        NoteSkip("snapshot-capacity-scope-changed",
            $"容量判定後有效範圍已變更：目前 {selection.SensorObjids.Count} 顆；本輪未啟動HTTP，保留補抓請求，下一輪會按新範圍重估。",
            trackPause: false, targetCount: selection.SensorObjids.Count);

    private bool SnapshotCapacityAllows(PrtgSnapshotTargetSelection selection, SystemSettings settings,
        out PrtgSnapshotCapacityEstimate estimate)
    {
        var strategy = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        estimate = PrtgSnapshotCapacityEvaluator.Evaluate(selection.SensorObjids.Count, strategy,
            selection.ScopeFingerprint, selection.EndpointFingerprint, selection.RequestShapeFingerprint,
            new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Read(),
            DateTimeOffset.UtcNow);
        // Up to one real 50-ID batch remains an intentionally bounded cold-start pilot path.
        return selection.SensorObjids.Count <= PrtgSnapshotCapacityEvaluator.BatchSize ||
            estimate.Status == PrtgSnapshotCapacityStatus.CapacityQualified;
    }

    private void NoteCapacitySkip(PrtgSnapshotTargetSelection selection, PrtgSnapshotCapacityEstimate estimate)
    {
        var exceeded = estimate.Status == PrtgSnapshotCapacityStatus.CapacityExceeded;
        var estimateText = estimate.EstimatedSeconds is { } seconds
            ? $"目前模型估算 {seconds:0.0} 秒／{estimate.CompletionWindowSeconds:0} 秒視窗，需保留 25% 餘裕。"
            : $"目前沒有足夠的同形新鮮樣本（{estimate.FreshMatchingFullBatchSamples}/5）。";
        var reason = exceeded ? "容量估算超出固定完成期限" : "完整快照容量尚未驗證";
        NoteSkip(exceeded ? "snapshot-capacity-exceeded" : "snapshot-capacity-unverified",
            $"{reason}：目前有效範圍 {selection.SensorObjids.Count} 顆；{estimateText}正式全範圍快照與範圍補抓已暫停，已接收樣本與待寫佇列保留。請縮小有效範圍或先停用正式取數，以目前來源、策略與範圍完成 5 筆同形 50-ID pilot，再確認模型可行後重新啟用。",
            trackPause: false, targetCount: selection.SensorObjids.Count);
    }

    private void NoteJointCapacitySkip(PrtgSnapshotTargetSelection selection, string reason)
    {
        var message = $"Joint PRTG admission is Waiting ({reason}); snapshot/profile evidence and the current source/scope plan must match before requests resume.";
        _lastSkipReason = message;
        RecordDiagnostic(Now(), "skip", message, selection.SensorObjids.Count,
            reasonCode: "joint-capacity-admission-waiting");
        _skipSince ??= Now();
    }

    /// <summary>
    /// 分批模式：依 objid 排序後每 <see cref="PrtgResourceGuardProbe.MaxBatchSize"/> 顆一個 filter_objid 請求。
    /// 單批失敗（非取消）只記數、其餘照做；全部批次都失敗才往外擲，交給既有退避。
    /// </summary>
    // Match the shared snapshot lane so workers do not occupy the profile's reserved slot.
    internal const int MaximumConcurrentSnapshotBatches = 3;
    internal const int MaximumSnapshotBatchResponseBytes = 512 * 1024;

    private async Task FetchFilteredAsync(
        SystemSettings settings, IReadOnlyList<long> targets, DateTime now, SnapshotTally tally,
        PrtgMonitoringPolicy trustPolicy,
        PrtgTrustedSamplingStrategyContext trustStrategy, PrtgSnapshotTargetSelection capacitySelection,
        CancellationToken ct)
    {
        var batchSize = PrtgResourceGuardProbe.MaxBatchSize;
        var batchCount = (targets.Count + batchSize - 1) / batchSize;
        var failedBatches = 0;
        var requested = 0;
        Exception? lastError = null;

        var client = GetClient(settings);
        using var roundDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fixedWindow = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy) == PrtgFetchStrategy.Aggressive
            ? PrtgSnapshotCapacityEvaluator.AggressiveWindow : PrtgSnapshotCapacityEvaluator.ConservativeWindow;
        roundDeadline.CancelAfter(fixedWindow);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, batchCount), new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumConcurrentSnapshotBatches,
                CancellationToken = roundDeadline.Token
            }, async (batchIndex, batchToken) =>
            {
                var batch = targets.Skip(batchIndex * batchSize).Take(batchSize).ToList();
                long capacityStartedTimestamp = 0;
                long capacityCompletedTimestamp = 0;
                var capacityOutcome = "failed";
                var capacityResponseValid = false;
                try
                {
                    var beforeProfiles = trustStrategy.Ready
                        ? new PrtgTrustedSamplingProfileStore(_backend).GetMany(batch)
                        : new Dictionary<long, PrtgTrustedSamplingProfile>();
                    var beforeIdentities = beforeProfiles.Count == 0 ? new Dictionary<long, PrtgResourceIdentity>()
                        : _backend.PrtgStore().GetResourceIdentities(beforeProfiles.Keys);
                    RecordDiagnostic(now, "attempt", targets: targets.Count);
                    capacityStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    var json = await client.GetBoundedJsonAsync(
                        "api/table.json?content=sensors&columns=" + PrtgSnapshotTargetResolver.SnapshotColumns
                        + PrtgResourceGuardProbe.BuildObjidFilter(batch), MaximumSnapshotBatchResponseBytes, batchToken);
                    capacityCompletedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        PrtgSnapshotCapacityResponseValidator.Validate(json, batch);
                        capacityResponseValid = true;
                    }
                    catch (Exception validationError) when (validationError is InvalidDataException or System.Text.Json.JsonException)
                    {
                        // Preserve the existing bounded snapshot parser behavior, but do not treat
                        // an incomplete or malformed table response as capacity evidence.
                    }
                    var receivedAtUtc = DateTime.UtcNow;
                    RecordDiagnostic(now, "success", targets: targets.Count);
                    // 只收本批要求的 objid：PRTG 若忽略 filter_objid 會每批都回整站，
                    // 不擋的話同一顆感測器一輪會被重複累加幾十次，而「取回少於要求」的警告也不會響
                    var profileIds = batch.Where(beforeProfiles.ContainsKey).ToArray();
                    var currentProfiles = profileIds.Length == 0
                        ? new Dictionary<long, PrtgTrustedSamplingProfile>()
                        : new PrtgTrustedSamplingProfileStore(_backend).GetMany(profileIds);
                    var currentIdentities = profileIds.Length == 0
                        ? new Dictionary<long, PrtgResourceIdentity>()
                        : _backend.PrtgStore().GetResourceIdentities(profileIds);
                    var currentPolicy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                    var currentSettings = _settingsStore.Get();
                    var currentStrategy = new PrtgTrustedSamplingStrategyStateStore(
                        _backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(
                            currentPolicy, PrtgFetchStrategy.Normalize(currentSettings.PrtgFetchStrategy),
                            PrtgFetchStrategy.Profile(currentSettings.PrtgFetchStrategy).SnapshotIntervalMinutes,
                            DateTime.UtcNow);
                    var trustFence = SameTrustedPolicyInputs(trustPolicy, currentPolicy) &&
                        profileIds.All(id => beforeProfiles[id].MetadataDigest == currentProfiles.GetValueOrDefault(id)?.MetadataDigest &&
                            beforeIdentities.GetValueOrDefault(id)?.Epoch == currentIdentities.GetValueOrDefault(id)?.Epoch &&
                            beforeIdentities.GetValueOrDefault(id)?.Generation == currentIdentities.GetValueOrDefault(id)?.Generation &&
                            beforeIdentities.GetValueOrDefault(id)?.ChannelGeneration == currentIdentities.GetValueOrDefault(id)?.ChannelGeneration &&
                            beforeIdentities.ContainsKey(id) && currentIdentities.ContainsKey(id)) &&
                        trustPolicy.Ready(currentSettings.PrtgUrl) &&
                        trustStrategy.Ready && currentStrategy.Ready &&
                        trustStrategy.StrategyFingerprint == currentStrategy.StrategyFingerprint &&
                        trustStrategy.EffectiveFromHourUtc == currentStrategy.EffectiveFromHourUtc;
                    ParseAndCheckpoint(json, now, tally, batch.ToHashSet(), trustFence,
                        trustPolicy, currentProfiles, currentIdentities, currentStrategy,
                        receivedAtUtc);
                    capacityOutcome = capacityResponseValid ? "success" : "failed";
                    Interlocked.Add(ref requested, batch.Count);
                }
                catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
                {
                    capacityOutcome = !ct.IsCancellationRequested ? "timeout" : "failed";
                    capacityCompletedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    throw;
                }
                catch (JournalWriteException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedBatches);
                    lastError = ex;
                    if (capacityStartedTimestamp != 0)
                        capacityCompletedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                finally
                {
                    // Only real full 50-ID requests of the exact runtime shape can qualify capacity.
                    // Partial final batches remain useful work but cannot masquerade as full-batch evidence.
                    if (batch.Count == capacitySelection.CapacitySampleBatchSize && capacityStartedTimestamp != 0)
                    {
                        try
                        {
                            var currentSettings = _settingsStore.Get();
                            var currentPolicy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                            var currentSelection = PrtgSnapshotTargetResolver.CreateSelection(targets,
                                capacitySelection.ActiveMappedDeviceCount, _backend, currentSettings, currentPolicy);
                            if (currentSelection.ScopeFingerprint == capacitySelection.ScopeFingerprint &&
                                currentSelection.EndpointFingerprint == capacitySelection.EndpointFingerprint &&
                                currentSelection.RequestShapeFingerprint == capacitySelection.RequestShapeFingerprint)
                            {
                                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(capacityStartedTimestamp,
                                    capacityCompletedTimestamp == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : capacityCompletedTimestamp).TotalMilliseconds;
                                new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Record(
                                    new PrtgSnapshotCapacitySample(capacitySelection.ScopeFingerprint,
                                        capacitySelection.EndpointFingerprint, capacitySelection.RequestShapeFingerprint,
                                        DateTimeOffset.UtcNow, (long)Math.Ceiling(Math.Max(0, elapsed)), batch.Count,
                                        capacityOutcome));
                            }
                        }
                        catch (Exception evidenceError)
                        {
                            Log.Warn(evidenceError, "[PRTG快照] 容量樣本無法保存；不影響此輪快照結果。");
                        }
                    }
                }
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && roundDeadline.IsCancellationRequested)
        {
            RecordDiagnostic(now, "skip", "capacity-window-incomplete", targets.Count,
                reasonCode: "snapshot-capacity-window-incomplete");
            throw new PrtgClientException($"PRTG 快照未能在固定 {fixedWindow.TotalMinutes:0} 分鐘容量窗口內完成；已接受批次保留，未完成範圍保持資料缺口。請先以估算與同形實測樣本確認容量。");
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
    /// 解析一份 table.json 回應並逐列累積（分批模式唯一解析入口）。
    /// </summary>
    /// <returns>回應的 treesize（沒有時 null）與 sensors 陣列長度</returns>
    private (long? TreeSize, int Total) ParseSnapshotResponse(string json, DateTime now, SnapshotTally tally, IReadOnlySet<long> accept,
        bool trustFence, PrtgMonitoringPolicy trustPolicy,
        IReadOnlyDictionary<long, PrtgTrustedSamplingProfile> trustProfiles,
        IReadOnlyDictionary<long, PrtgResourceIdentity> currentIdentities,
        PrtgTrustedSamplingStrategyContext trustStrategy, DateTime receivedAtUtc)
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
            AccumulateSensorRow(el, now, tally, accept, trustFence, trustPolicy,
                trustProfiles, currentIdentities, trustStrategy, receivedAtUtc);
        }

        return (treeSize, sensorsArr.GetArrayLength());
    }

    /// <summary>單列：只收 accept 內的感測器（本批要求的 objid），換算（流量類轉每小時量）後進累積器。</summary>
    private void AccumulateSensorRow(JsonElement el, DateTime now, SnapshotTally tally, IReadOnlySet<long> accept,
        bool trustFence, PrtgMonitoringPolicy trustPolicy,
        IReadOnlyDictionary<long, PrtgTrustedSamplingProfile> trustProfiles,
        IReadOnlyDictionary<long, PrtgResourceIdentity> currentIdentities,
        PrtgTrustedSamplingStrategyContext trustStrategy, DateTime receivedAtUtc)
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

        if (!lastValueRaw.HasValue || !double.IsFinite(lastValueRaw.Value)) return;

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
        string? sensorType = null;
        if (_sensorTypes != null &&
            _sensorTypes.TryGetValue(objid.Value, out sensorType) &&
            PrtgVolumeSensorTypes.IsVolume(sensorType))
        {
            sampleValue = lastValueRaw.Value * 3600.0 / intervalSeconds;
        }
        else
        {
            sampleValue = lastValueRaw.Value;
        }

        if (!double.IsFinite(sampleValue)) return;
        if (trustFence && trustProfiles.TryGetValue(objid.Value, out var profile) &&
            currentIdentities.TryGetValue(objid.Value, out var currentIdentity) &&
            trustPolicy.SensorIds.Contains(objid.Value))
        {
            var asOfUtc = DateTime.UtcNow;
            var resolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, currentIdentity,
                trustPolicy, objid.Value, sensorType ?? "", trustStrategy, receivedAtUtc, asOfUtc);
            if (resolution.Context is { } trustedContext)
            {
                var trustedDisposition = _trustedSnapshotParser.AddToAccumulator(el.GetRawText(), trustedContext,
                    _accumulator, out _);
                if (trustedDisposition is PrtgTrustedSampleDisposition.Accepted or
                    PrtgTrustedSampleDisposition.Replaced or PrtgTrustedSampleDisposition.Duplicate)
                {
                    if (trustedDisposition != PrtgTrustedSampleDisposition.Duplicate) tally.Added++;
                    return;
                }
                return; // Deferred／rejected 樣本不可落入診斷 Add 而清掉既有可信 slot。
            }
        }
        if (_accumulator.TryAddDiagnostic(objid.Value, now, sampleValue,
            _expectedSamplesPerHour > 0 ? 100.0 / _expectedSamplesPerHour : null)) tally.Added++;
    }

    /// <summary>一輪快照的計數。欄位而非屬性：間隔解析以 ref 累加。</summary>
    private sealed class SnapshotTally
    {
        public int Matched;
        public int Added;
        public int UnparsedIntervals;
    }

    private sealed record RecentStateChangeScopeContext(
        HashSet<long> BusinessDeviceObjids,
        string SourceIdentityHash,
        string ScopeVersionHash,
        DateTime LocalToday,
        DateTime ReconciledAtUtc);

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
    private async Task<IReadOnlyList<long>> BackfillScopeSensorsAsync(SystemSettings settings,
        RecentStateChangeScopeContext queueContext, CancellationToken ct)
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
                var queueItems = pending.Where(queueContext.BusinessDeviceObjids.Contains).Distinct().ToDictionary(id => id,
                    id => new PrtgRecentStateChangeQueueItem(id, queueContext.LocalToday.AddDays(-1), queueContext.LocalToday,
                        queueContext.SourceIdentityHash, queueContext.ScopeVersionHash,
                        queueContext.ReconciledAtUtc, queueContext.ReconciledAtUtc,
                        Attempts: 0, LeaseOwner: null, LeaseExpiresAtUtc: null, CompletedAtUtc: null));
                // 補抓寫入的列 SyncedAt 是當下時間（沿用 mapper），晚於任何已開始的結構同步起點，
                // 不會被那趟「未刷新即刪除」清掉
                result = await fetch.BackfillSensorsForDevicesAsync(pending, settings.PrtgFetchConcurrency, ct,
                    requireCompleteDevice: true, recentStateQueueItems: queueItems);
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

    private RecentStateChangeScopeContext ReconcileRecentStateChangesForCurrentScope(SystemSettings settings)
    {
        // The defer bit suppresses HTTP only in the pass that changed queue metadata.
        // A capacity or scope fence may end that pass before the queue drain, so discard
        // any stale bit before reconciling the next pass.
        Interlocked.Exchange(ref _recentStateQueueHttpDeferred, 0);
        var store = _backend.PrtgStore();
        var businessDeviceObjids = ComputeBusinessScopeDevices(store);
        var sourceIdentityHash = SourceIdentityHash(settings);
        var scopeRevision = new PrtgScopeRevisionReader(_backend, _hosts).Read();
        var scopeVersionHash = ScopeVersionHash(scopeRevision);
        var localToday = Now().Date;
        var reconciliationAtUtc = DateTime.UtcNow;
        var reconciled = store.ReconcileRecentStateChanges(businessDeviceObjids, sourceIdentityHash,
            scopeVersionHash, localToday.AddDays(-1), localToday, reconciliationAtUtc);
        if (reconciled > 0) Interlocked.Exchange(ref _recentStateQueueHttpDeferred, 1);
        var stoppedQueue = store.ReadRecentStateChangeQueueStopSummary();
        if (stoppedQueue?.AtUtc == reconciliationAtUtc && stoppedQueue.StoppedCount > 0)
            WriteOutput($"[PRTG快照] 監看範圍變更，已停止 {stoppedQueue.StoppedCount} 台離開業務範圍的狀態補抓工作（{stoppedQueue.Reason}）。", LogLevel.Info);
        return new RecentStateChangeScopeContext(businessDeviceObjids, sourceIdentityHash,
            scopeVersionHash, localToday, reconciliationAtUtc);
    }

    private async Task FetchRecentStateChangesAsync(SystemSettings settings, CancellationToken ct)
    {
        try { await DrainRecentStateChangesAsync(settings, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            WriteOutput($"[PRTG快照] 狀態補抓佇列處理失敗（不影響快照）：{ex.Message}", LogLevel.Warn);
        }
    }

    private async Task DrainRecentStateChangesAsync(SystemSettings settings, CancellationToken ct)
    {
        var store = _backend.PrtgStore();
        var businessDeviceObjids = ComputeBusinessScopeDevices(store);
        var sourceIdentityHash = SourceIdentityHash(settings);
        var scopeReader = new PrtgScopeRevisionReader(_backend, _hosts);
        var scopeRevision = scopeReader.Read();
        var scopeVersionHash = ScopeVersionHash(scopeRevision);
        var localToday = Now().Date;
        var nowUtc = DateTime.UtcNow;
        var reboundRows = store.ReconcileRecentStateChanges(businessDeviceObjids, sourceIdentityHash, scopeVersionHash,
            localToday.AddDays(-1), localToday, nowUtc);
        if (reboundRows > 0)
        {
            NoteRecentStateQueueDeferred("來源或業務範圍剛更新");
            return; // 來源或範圍不符時先更新佇列，下一輪才可向新來源發 HTTP。
        }
        if (Interlocked.Exchange(ref _recentStateQueueHttpDeferred, 0) != 0)
        {
            NoteRecentStateQueueDeferred("本輪剛完成範圍整理");
            return;
        }

        var owner = Guid.NewGuid().ToString("N");
        var leases = store.ClaimRecentStateChanges(owner, nowUtc, TimeSpan.FromMinutes(2), take: 50);
        for (var leaseIndex = 0; leaseIndex < leases.Count; leaseIndex++)
        {
            var lease = leases[leaseIndex];
            if (ct.IsCancellationRequested)
            {
                for (var pendingIndex = leaseIndex; pendingIndex < leases.Count; pendingIndex++)
                    store.ReleaseRecentStateChangeLease(leases[pendingIndex], DateTime.UtcNow);
                ct.ThrowIfCancellationRequested();
            }
            if (!businessDeviceObjids.Contains(lease.Item.DeviceObjid) ||
                lease.Item.SourceIdentityHash != sourceIdentityHash || lease.Item.BusinessScopeVersion != scopeVersionHash)
            {
                store.RetryRecentStateChange(lease, DateTime.UtcNow, TimeSpan.FromSeconds(30));
                continue;
            }

            var operationRevision = store.ReadCatalogueDataRevision();
            using var operationToken = CancellationTokenSource.CreateLinkedTokenSource(ct);
            void Checkpoint()
            {
                operationToken.Token.ThrowIfCancellationRequested();
                var currentSettings = _settingsStore.Get();
                if (SourceIdentityHash(currentSettings) != lease.Item.SourceIdentityHash ||
                    ScopeVersionHash(scopeReader.Read()) != lease.Item.BusinessScopeVersion ||
                    store.ReadCatalogueDataRevision() != operationRevision)
                {
                    operationToken.Cancel();
                    operationToken.Token.ThrowIfCancellationRequested();
                }
            }

            try
            {
                var client = GetClient(settings);
                client.OperationCheckpoint = () =>
                {
                    _operationCheckpoint?.Invoke();
                    Checkpoint();
                };
                var fetch = new PrtgFetchService(client, store,
                    new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)), SilentConsole,
                    PrtgSensorTypeCategoryMap.ParseOverrides(settings.PrtgSensorTypeCategoryOverrides).Map);
                var result = await fetch.FetchStateChangesRangeAsync(lease.Item.FromLocalDate, lease.Item.ToLocalDate,
                    new[] { lease.Item.DeviceObjid }, settings.PrtgFetchConcurrency, operationToken.Token);
                Checkpoint();
                if (result.Converged && result.QueriedObjects == 1 && result.FailedObjects == 0 && result.Pages > 0)
                {
                    if (!store.AcknowledgeRecentStateChange(lease))
                        WriteOutput($"[PRTG快照] 裝置 {lease.Item.DeviceObjid} 狀態補抓完成，但佇列 lease 已改變；保留佇列供重試。", LogLevel.Warn);
                }
                else
                {
                    store.RetryRecentStateChange(lease, DateTime.UtcNow);
                    WriteOutput($"[PRTG快照] 裝置 {lease.Item.DeviceObjid} 狀態補抓未收斂，保留佇列稍後重試。", LogLevel.Warn);
                }
            }
            catch (OperationCanceledException)
            {
                store.RetryRecentStateChange(lease, DateTime.UtcNow, TimeSpan.FromSeconds(15));
                for (var pendingIndex = leaseIndex + 1; pendingIndex < leases.Count; pendingIndex++)
                    store.ReleaseRecentStateChangeLease(leases[pendingIndex], DateTime.UtcNow);
                throw;
            }
            catch (Exception ex)
            {
                store.RetryRecentStateChange(lease, DateTime.UtcNow);
                WriteOutput($"[PRTG快照] 裝置 {lease.Item.DeviceObjid} 狀態補抓失敗（不影響快照）：{ex.Message}", LogLevel.Warn);
            }
            finally
            {
                if (_client != null) _client.OperationCheckpoint = _operationCheckpoint;
            }
        }
    }

    private void NoteRecentStateQueueDeferred(string cause)
    {
        NoteSkip("recent-state-queue-deferred",
            $"狀態補抓 Queue {cause}；本輪未送出 messages 請求，尚未完成工作保留至下一輪，並依當前來源、範圍與容量證據重新准入。",
            trackPause: false);
    }

    private static string SourceIdentityHash(SystemSettings settings)
    {
        var source = string.Join("\u001f", settings.PrtgUrl ?? string.Empty, settings.PrtgAuthMode ?? string.Empty,
            settings.PrtgUsername ?? string.Empty, settings.PrtgApiTokenEnc ?? string.Empty,
            settings.PrtgPasswordEnc ?? string.Empty, settings.PrtgPasshashEnc ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static string ScopeVersionHash(string revision) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision)));

    private HashSet<long> ComputeBusinessScopeDevices(EfPrtgStore store)
    {
        // 持久化近期狀態交接沿用 PrtgScopeDevices 的已對應／衝突／人工業務裝置範圍，但排除僅供守門使用的裝置。
        // 不呼叫停用守門的 Compute：該路徑仍會掃描全部鏡像感測器，以尋找守門裝置並保留其資料列。
        var mapRows = store.GetLatestHostMap();
        var businessDevices = mapRows.Where(row => row.MapStatus == PrtgMapStatus.Ok)
            .Select(row => row.DeviceObjid).ToHashSet();
        var activeHosts = _hosts.GetAll().Where(host => host.Active && host.MergedInto == null).ToArray();
        var hostsById = activeHosts.Select(host => host.HostId).ToHashSet();
        var hostIdsByIp = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in activeHosts)
        {
            var ip = _addressResolver.Resolve(host.IpAddress);
            if (ip == null) continue;
            if (!hostIdsByIp.TryGetValue(ip, out var ids)) hostIdsByIp[ip] = ids = [];
            ids.Add(host.HostId);
        }
        foreach (var row in mapRows.Where(row => row.MapStatus == PrtgMapStatus.Conflict))
        {
            if (row.HostId.HasValue)
            {
                businessDevices.Add(row.DeviceObjid);
                continue;
            }
            var ip = _addressResolver.Resolve(row.Ip);
            if (ip != null && hostIdsByIp.ContainsKey(ip)) businessDevices.Add(row.DeviceObjid);
        }
        foreach (var manual in store.GetManualMaps())
            if (hostsById.Contains(manual.HostId)) businessDevices.Add(manual.DeviceObjid);
        return businessDevices;
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
    /// 首輪恢復已落盤樣本；之後阻止不同來源位址的樣本混寫。
    /// </summary>
    private bool RestoreJournal(SystemSettings settings)
    {
        lock (_pendingWrite)
        {
            try
            {
                _journal.AcquireOwnership();
                var binding = PrtgSnapshotJournal.CaptureBindingPair(_backend, settings.PrtgUrl);
                var endpoint = binding.Current;
                if (!_journalLoaded)
                {
                    var state = _journal.Load(endpoint, Now(), binding.Legacy);
                    _journal.EnableIncremental(endpoint, Now(), binding.Legacy);
                    if (state != null)
                    {
                        _accumulator.Restore(state.Accumulator);
                        _pendingWrite.AddRange(state.Pending);
                        _pendingRows = state.Pending.Sum(batch => batch.Rows.Count);
                    }
                    _journalEndpoint = endpoint;
                    _journalLoaded = true;
                }
                if (endpoint != _journalEndpoint)
                {
                    if (_accumulator.SampleCount > 0 || _pendingWrite.Count > 0)
                        throw new InvalidDataException("PRTG 來源位址或來源世代變更，舊樣本保留待處理；不寫入新身分");
                    _journal.EnableIncremental(endpoint, Now(), binding.Legacy);
                    _journalEndpoint = endpoint;
                }
                _journalError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                JournalFailed(ex);
                return false;
            }
        }
    }

    private bool SaveJournal()
    {
        try
        {
            var delta = _accumulator.CaptureDelta();
            _journal.AppendDelta(_journalEndpoint!, delta, null, null, Now());
            _accumulator.CommitDelta(delta);
            _journalError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            JournalFailed(ex);
            return false;
        }
    }

    private void JournalFailed(Exception ex)
    {
        var detail = ex switch
        {
            JsonException => "復原檔格式不完整或已損壞；請維護管理者先備份並檢查，勿直接刪除。",
            UnauthorizedAccessException => "服務帳號無法存取復原目錄；請檢查資料目錄的讀寫與替換權限。",
            InvalidDataException => ex.Message,
            _ => "本機待寫檔案無法讀寫；請檢查磁碟空間、檔案鎖定與服務帳號權限。"
        };
        var reason = $"快照待寫復原檔無法使用，已停止採集並保留資料：{detail}";
        if (_journalError != reason)
        {
            WriteOutput(reason, LogLevel.Warn);
            Log.Warn(ex, "PRTG 快照復原檔失敗詳細原因");
        }
        _journalError = reason;
        _lastSkipReason = reason;
        RecordDiagnostic(Now(), "write-failure", "local-journal-unavailable", _targetObjids?.Count ?? 0);
    }

    private void FlushAccumulator(DateTime now, bool all)
    {
        lock (_pendingWrite)
        {
            var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            var asOfUtc = DateTime.UtcNow;
            var rows = all ? _accumulator.PreviewDrainAll(_expectedSamplesPerHour, now)
                : _accumulator.PreviewDrainBeforeSources(hour, asOfUtc, _expectedSamplesPerHour, now);
            var keys = all ? _accumulator.KeysForDrainAll() : _accumulator.KeysForDrainBeforeSources(hour, asOfUtc);
            WriteSampledRowsCore(rows, keys);
        }
    }

    private (long? TreeSize, int Total) ParseAndCheckpoint(string json, DateTime now, SnapshotTally tally,
        IReadOnlySet<long> accept, bool trustFence, PrtgMonitoringPolicy trustPolicy,
        IReadOnlyDictionary<long, PrtgTrustedSamplingProfile> trustProfiles,
        IReadOnlyDictionary<long, PrtgResourceIdentity> currentIdentities,
        PrtgTrustedSamplingStrategyContext trustStrategy, DateTime receivedAtUtc)
    {
        lock (_pendingWrite)
        {
            var result = ParseSnapshotResponse(json, now, tally, accept, trustFence,
                trustPolicy, trustProfiles, currentIdentities, trustStrategy, receivedAtUtc);
            if (!SaveJournal()) throw new JournalWriteException();
            return result;
        }
    }

    /// <summary>結算列先記入復原檔再提交 SQL，資料庫失敗保留穩定批次 ID。</summary>
    private void WriteSampledRows(IReadOnlyList<PrtgValueRow> rows) =>
        WriteSampledRowsCore(rows, Array.Empty<PrtgSnapshotAccumulator.CheckpointKey>());

    private void WriteSampledRowsCore(IReadOnlyList<PrtgValueRow> rows, IReadOnlyList<PrtgSnapshotAccumulator.CheckpointKey> drainKeys)
    {
        lock (_pendingWrite)
        {
            if (!_journalLoaded && !RestoreJournal(_settingsStore.Get())) return;
            var added = rows.Count > 0
                ? new[] { new PrtgSnapshotJournal.Batch(Guid.NewGuid().ToString("N"), rows.ToArray()) }
                : Array.Empty<PrtgSnapshotJournal.Batch>();
            var delta = _accumulator.CaptureDelta(drainKeys);
            try
            {
                if (delta.Upserts.Count > 0 || delta.Removals.Count > 0 || added.Length > 0)
                    _journal.AppendDelta(_journalEndpoint!, delta, added, null, Now());
                _accumulator.CommitDelta(delta);
                _pendingWrite.AddRange(added);
                _pendingRows += added.Sum(batch => batch.Rows.Count);
                _journalError = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                JournalFailed(ex);
                return;
            }
            if (_pendingWrite.Count == 0) return;

            foreach (var pending in _pendingWrite.ToArray())
            {
                try
                {
                    var merged = _backend.PrtgStore().MergeSampledValues(pending.Rows, pending.Id);
                    if (merged > 0)
                        foreach (var hourGroup in pending.Rows.GroupBy(row => row.PeriodStart))
                            RecordDiagnostic(hourGroup.Key, "persisted", sampled: hourGroup.Count());
                    try
                    {
                        _journal.AppendAck(_journalEndpoint!, pending.Id, Now());
                    }
                    catch (Exception journalEx) when (journalEx is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                    {
                        JournalFailed(journalEx);
                        break;
                    }
                    _pendingWrite.Remove(pending);
                    _pendingRows -= pending.Rows.Count;
                    _journalError = null;
                }
                catch (Exception ex)
                {
                    foreach (var hourGroup in pending.Rows.GroupBy(row => row.PeriodStart))
                        RecordDiagnostic(hourGroup.Key, "write-failure", "database-write-failed", _targetObjids?.Count ?? 0, hourGroup.Count());
                    var msg = $"快照樣本寫入資料庫失敗，{_pendingRows} 列留待下次重試：{ex.Message}";
                    WriteOutput(msg, LogLevel.Warn);
                    Log.Error(ex, "PRTG 快照樣本寫入資料庫失敗");
                    break;
                }
            }
        }
    }

    private int RefreshTargets(SystemSettings settings, DateTime now, DateTime currentHour)
    {
        var store = _backend.PrtgStore();
        var today = now.Date;
        // 與校準同一個來源：錨點今天、回看 30 天的最近一次對應
        var (_, hostMaps) = store.GetLatestHostMapWithDate(30, today);
        var activeHostIds = _hosts.GetAll().Where(h => h.Active && !h.MergedInto.HasValue)
            .Select(h => h.HostId).ToHashSet();
        var okDeviceIds = hostMaps
            .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue && activeHostIds.Contains(m.HostId.Value))
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
        _targetWhitelistFingerprint = WhitelistFingerprint(settings.PrtgSensorTypeWhitelist);
        return okDeviceIds.Count;
    }

    private static string WhitelistFingerprint(IReadOnlyCollection<string>? whitelist) =>
        string.Join("\n", (whitelist ?? Array.Empty<string>())
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));

    private static bool SameTrustedPolicyInputs(PrtgMonitoringPolicy left, PrtgMonitoringPolicy right) =>
        left.SourceGeneration == right.SourceGeneration && left.EndpointHint == right.EndpointHint &&
        left.RawTimestampTimeZoneId == right.RawTimestampTimeZoneId &&
        left.AnalysisTimeZoneId == right.AnalysisTimeZoneId &&
        left.TimeBasisEvidenceReference == right.TimeBasisEvidenceReference &&
        left.SourceTimeZoneId == right.SourceTimeZoneId && left.SourceCultureName == right.SourceCultureName &&
        left.HostIds.Order().SequenceEqual(right.HostIds.Order()) &&
        left.SensorIds.Order().SequenceEqual(right.SensorIds.Order());

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

    private void RecordDiagnostic(DateTime at, string outcome, string? reason = null, int targets = 0, int sampled = 0,
        string? reasonCode = null)
    {
        try { _diagnosticsStore.Record(at, outcome, reason, targets, sampled, reasonCode); }
        catch (Exception ex) { Log.Warn(ex, "PRTG 快照診斷統計寫入失敗（不影響取樣）"); }
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
            if (!RestoreJournal(_settingsStore.Get())) return;
            if (_journalLoaded) FlushAccumulator(now, all: true);
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
            created.RequestPurpose = PrtgRequestPurpose.Snapshot;
            created.AdmissionPlanFingerprint = _admissionPlanFingerprint;
            _client?.Dispose();
            _client = created;
            _clientFingerprint = fingerprint;
        }
        // Connection reuse is keyed by transport settings, while shared admission is renewed
        // independently. Refresh the authorization proof for every use of the cached client.
        _client.AdmissionPlanFingerprint = _admissionPlanFingerprint;
        _client.OperationCheckpoint = _operationCheckpoint;
        return _client;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        DisposeClient();
        if (ExecuteTask == null || ExecuteTask.IsCompleted) _journal.Dispose();
    }

    public override void Dispose()
    {
        DisposeClient();
        base.Dispose();
        if (ExecuteTask == null || ExecuteTask.IsCompleted) _journal.Dispose();
        else _ = ExecuteTask.ContinueWith(_ => _journal.Dispose(), TaskScheduler.Default);
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
