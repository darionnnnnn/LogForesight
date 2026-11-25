using System.Reflection;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Core.Time;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services.Mail;

namespace LogForesight.Web.Services;

/// <summary>
/// 站台健康狀態（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）。
///
/// **為什麼需要**：改版前要回答「站台現在是不是壞了／變慢了」，只能上伺服器翻 `logs\web.log`。
/// 企業級部署要能被監控系統輪詢，而且要在**沒有人登入**的情況下就答得出「活著沒有」——
/// 資料庫掛掉時最需要這支端點，那時多半也登不進去。
///
/// 兩層資訊、兩種對象：
///   - <see cref="GetLiveness"/>：匿名可讀，只回「活著／不正常」與版本。**刻意不含任何內部細節**
///     ——連線字串、資料表、使用者數都是可用來探查系統的資訊，健康檢查不該成為洩漏管道。
///   - <see cref="GetDetail"/>：需 <c>Maintain</c>，給維運人員診斷用的完整資訊。
/// </summary>
public class HealthService
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly StorageBackend _backend;
    private readonly SchedulerRunState _runState;
    private readonly TopIssueBackfiller _backfiller;
    private readonly MailNotificationService _mail;
    private readonly ScheduleFreshnessService _freshness;
    private readonly IssueFirstSeenSeedHostedService? _firstSeenSeedService;
    private readonly PrtgSnapshotHostedService? _snapshotService;
    private readonly HostDayWorkflowRecoveryHostedService? _recoveryService;
    private readonly IReadOnlyList<ICacheStatistics> _caches;
    private readonly TimeProvider _clock;
    private readonly IAiProbeService? _aiProbe;

    public HealthService(
        StorageBackend backend,
        SchedulerRunState runState,
        TopIssueBackfiller backfiller,
        MailNotificationService mail,
        ScheduleFreshnessService freshness,
        IssueFirstSeenSeedHostedService? firstSeenSeedService = null,
        PrtgSnapshotHostedService? snapshotService = null,
        IEnumerable<ICacheStatistics>? caches = null,
        HostDayWorkflowRecoveryHostedService? recoveryService = null,
        TimeProvider? clock = null,
        IAiProbeService? aiProbe = null)
    {
        _aiProbe = aiProbe;
        _clock = clock ?? TimeProvider.System;
        _recoveryService = recoveryService;
        _backend = backend;
        _runState = runState;
        _backfiller = backfiller;
        _mail = mail;
        _freshness = freshness;
        _firstSeenSeedService = firstSeenSeedService;
        _snapshotService = snapshotService;
        _caches = caches?.ToList() ?? new List<ICacheStatistics>();
    }

    /// <summary>測試用：注入固定的記憶體快照（正式碼不設，走 <see cref="MemorySnapshot.Capture"/>）。</summary>
    internal Func<MemorySnapshot>? MemoryCaptureOverride { get; set; }

    /// <summary>擷取記憶體快照並轉成 DTO；不碰資料庫，任何讀取失敗都由 Capture 自行吞掉。</summary>
    private (MemorySnapshotDto Dto, double? UsageRatio) CaptureMemory()
    {
        var snap = MemoryCaptureOverride?.Invoke() ?? MemorySnapshot.Capture(
            _backend.TryReadSqliteHeap,
            () => NLog.LogManager.Configuration?.LoggingRules.Count,
            () => _caches.Select(c => c.GetStatistics()));
        var ratio = snap.HardLimitUsageRatio;
        return (new MemorySnapshotDto
        {
            CapturedAt = snap.CapturedAt,
            WorkingSetBytes = snap.WorkingSetBytes,
            PeakWorkingSetBytes = snap.PeakWorkingSetBytes,
            PrivateBytes = snap.PrivateBytes,
            PeakPagedBytes = snap.PeakPagedBytes,
            ManagedHeapBytes = snap.ManagedHeapBytes,
            GcHeapSizeBytes = snap.GcHeapSizeBytes,
            GcCommittedBytes = snap.GcCommittedBytes,
            GcFragmentedBytes = snap.GcFragmentedBytes,
            LohSizeBytes = snap.LohSizeBytes,
            HardLimitBytes = snap.HardLimitBytes,
            Gen2Collections = snap.Gen2Collections,
            IsServerGc = snap.IsServerGc,
            GcConserveMemory = snap.GcConserveMemory,
            GcHeapHardLimitPercent = snap.GcHeapHardLimitPercent,
            SqliteHeapBytes = snap.SqliteHeapBytes,
            SqliteHeapHighwaterBytes = snap.SqliteHeapHighwaterBytes,
            NlogRuleCount = snap.NlogRuleCount,
            HardLimitUsagePercent = ratio is { } r ? Math.Round(r * 100, 1) : null,
            Caches = snap.Caches.Select(c => new CacheStatisticsDto
            {
                Name = c.Name,
                Entries = c.Entries,
                Elements = c.Elements,
                Hits = c.Hits,
                Misses = c.Misses,
                Evictions = c.Evictions,
                OversizeRejected = c.OversizeRejected
            }).ToList()
        }, ratio);
    }

    /// <summary>已登入者看的組建版本（含 commit）</summary>
    private static string Version => BuildVersion.Current.Label;

    /// <summary>
    /// 從主機清單判定站台主機是否已停用（健康頁提示用）。讀不到清單時不提示：這只是提示，不該讓健康頁失敗。
    /// </summary>
    private bool IsLocalHostDisabled()
    {
        try
        {
            return new HostStore(_backend.Blob("hosts")).GetAll() is var hosts && LocalHostPolicy.IsDisabled(hosts, Environment.MachineName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.EntityFrameworkCore.DbUpdateException or System.Data.Common.DbException)
        {
            return false;
        }
    }

    public HealthDto GetLiveness()
    {
        var storageOk = ProbeStorage(out _);
        return new HealthDto
        {
            Status = storageOk ? HealthStatuses.Ok : HealthStatuses.Down,
            // 匿名端點：只給版本號，commit 不對外（已登入者看 detail）
            Version = BuildVersion.Current.Number,
            StorageOk = storageOk
        };
    }

    /// <summary>取得排程資料新鮮度（任務 A-3）</summary>
    public ScheduleFreshnessDto GetScheduleFreshness(DateTime now) => _freshness.GetScheduleFreshness(now);

    /// <summary>
    /// 完整診斷。算出新結果後順手更新告示列（<see cref="GetAttention"/>）的快取——兩者同一份判定，
    /// 健康頁已經正常時導覽告示不該再掛著舊的警示直到快取過期。
    /// </summary>
    public HealthDetailDto GetDetail()
    {
        var detail = BuildDetail();
        StoreAttention(detail);
        return detail;
    }

    private HealthDetailDto BuildDetail()
    {
        var storageOk = ProbeStorage(out var storageError);
        var performance = _backend.Performance.Snapshot();
        var (memory, memoryRatio) = CaptureMemory();
        if (!storageOk)
        {
            // Storage-backed status providers can fail independently after the probe. Return the
            // minimal diagnostic DTO here so a failed probe never triggers another database read.
            return new HealthDetailDto
            {
                Status = HealthStatuses.Down,
                StatusReasons = HealthAttention.Evaluate(new HealthAttentionInput { StorageDown = true }, DateTime.Now).ToList(),
                Version = Version,
                StorageOk = false,
                StorageError = storageError,
                SlowThresholdMs = performance.ThresholdMs,
                TotalOperations = performance.TotalOperations,
                SlowOperations = performance.SlowOperations,
                SlowestMs = performance.SlowestMs,
                SlowestOperation = performance.SlowestOperation,
                LastSlowAt = performance.LastSlowAt,
                TopSlowOperations = performance.TopSlowOperations
                    .Select(o => new SlowOperationDto
                    {
                        Operation = o.Operation,
                        Count = o.Count,
                        MaxMs = o.MaxMs,
                        LastAt = o.LastAt
                    })
                    .ToList(),
                PrtgFreshness = null,
                PrtgSnapshot = null,
                Memory = memory
            };
        }

        var migration = _backend.HandlingMigrator.State;
        var permMigration = _backend.PermissionChangeMigrator.State;
        var freshness = _freshness.GetScheduleFreshness(DateTime.Now);
        var healthSettings = new SystemSettingsStore(_backend.Blob("system_settings")).Get();
        var prtgEnabled = healthSettings.PrtgEnabled;
        var prtgSnapshot = BuildPrtgSnapshotHealth(prtgEnabled, healthSettings.PrtgFetchStrategy, DateTime.Now);

        // 診斷頁只有一行進度可顯示：主／子軌取捨（子進度優先）由 LatestActivity 單點決定
        // （回饋十四輪 UI-6 體檢，與 /api/run-activity 同一個選擇邏輯）——只讀主進度的話，
        // AI 佇列消化階段這裡會停在搜尋階段的凍結數字，看起來像分析卡死。
        // 同一份快照：與 EndRun 的整批清空不會交錯成混合狀態
        var runSnapshot = _runState.Snapshot();

        var firstSeenProgress = _firstSeenSeedService?.Progress;
        var firstSeenFailed = firstSeenProgress?.IsFailed == true;

        // 密碼欄位解不開時各功能只會靜默當成未設定——必須在這裡看得到
        var cryptoKeyMismatch = CryptoKeyBootstrapper.KeyMismatch;
        var cryptoDecryptFailure = CryptoHelper.DecryptFailureSeen;

        // 「慢操作占比過高」或「首見日合併連續失敗達上限」不等於壞掉，但它是使用者開始抱怨之前唯一的先行指標——
        // 因此獨立成 degraded 狀態，而不是併進 ok。排程資料過期且未確認靜音時亦視為 degraded（任務 A-3）。
        // 整體狀態的判定只有 HealthAttention.Evaluate 一份：原因清單為空＝ok，否則 degraded；
        // 健康頁頂端與導覽告示列讀的都是這份原因清單，不各自再算。
        var memoryWarning = memoryRatio >= DegradedMemoryUsageRatio;
        var recovery = _recoveryService?.GetWaitingSummary();
        var retentionFailure = RetentionPruner.LastFailure(_backend);
        var prtgSyncWarnings = prtgEnabled ? ReadPrtgSyncWarnings() : Array.Empty<PrtgSyncWarning>();
        var suspendedRecipients = _mail.GetSuspendedRecipients();
        var outboxDrops = new WorkOrderOutboxDropStore(_backend.Blob(WorkOrderOutboxDropStore.BlobKey)).Get();
        // AI 探測：讀上次結果、在背景更新（不等探測）；健康頁每次載入順便讓結果保持新鮮
        _aiProbe?.RefreshInBackground();
        var aiProbe = _aiProbe?.LatestResult;
        var reasons = HealthAttention.Evaluate(new HealthAttentionInput
        {
            InvalidMailSchedules = InvalidMailSchedules(healthSettings),
            UnpinnedCertificateBypass = UnpinnedCertificateBypass(healthSettings,
                new NetiqOptionsStore(_backend.Blob("netiq_options")).Get(),
                hasActiveSentinel: new SentinelStore(_backend.Blob("sentinels")).GetAll().Any(x => x.Active)),
            CryptoKeyUnavailable = LogForesight.Core.CryptoHelper.SiteKeyUnavailable,
            SlowOperationsHigh = performance.TotalOperations > 0 &&
                                 performance.SlowOperations * 100.0 / performance.TotalOperations >= DegradedSlowRatioPercent,
            FirstSeenFailed = firstSeenFailed,
            ScheduleStaleAndNotAcked = freshness.Stale && !freshness.Acked,
            PrtgSnapshotWarning = prtgSnapshot?.Warning == true,
            PrtgSnapshotMessage = prtgSnapshot?.Message,
            MemoryWarning = memoryWarning,
            CryptoKeyMismatch = cryptoKeyMismatch,
            CryptoDecryptFailure = cryptoDecryptFailure,
            Migrations = new[]
            {
                new MigrationHealth("處理狀態", migration.State, migration.StartedAt, migration.LastError),
                new MigrationHealth("權限異動", permMigration.State, permMigration.StartedAt, permMigration.LastError)
            },
            PrtgFreshnessSuspicious = prtgEnabled
                ? PrtgFreshnessDto.FromStore(new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)))
                    .Where(f => f.Suspicious)
                    .Select(f => (PrtgFreshnessLabel(f.Category), f.ZeroStreak))
                    .ToList()
                : Array.Empty<(string, int)>(),
            HostDayRecoveryNeedsAttention = recovery?.NeedsAttention ?? 0,
            LastRunSucceeded = runSnapshot.LastOutcome?.Success,
            SuspendedMailRecipients = suspendedRecipients,
            RetentionFailure = retentionFailure,
            OutboxRecentDropped = outboxDrops.RecentDropped,
            OutboxLastDroppedAt = outboxDrops.LastDroppedAt,
            AiUnreachable = aiProbe?.Status == AiProbeStatus.Failed,
            PrtgSyncWarnings = prtgSyncWarnings
        }, DateTime.Now);
        var degraded = reasons.Count > 0;

        return new HealthDetailDto
        {
            Status = degraded ? HealthStatuses.Degraded : HealthStatuses.Ok,
            StatusReasons = reasons.ToList(),
            AiProbeStatus = aiProbe?.Status.ToString(),
            AiProbeDetail = aiProbe?.Detail,
            RetentionFailedSegments = retentionFailure?.Segments.ToList() ?? new List<string>(),
            PrtgSyncWarnings = prtgSyncWarnings.Select(w => w.Message).ToList(),
            Version = Version,
            BuildShaKnown = BuildVersion.Current.ShaKnown,
            LocalHostDisabled = IsLocalHostDisabled(),
            StorageOk = storageOk,
            StorageError = storageError,
            ScheduleFreshness = freshness,
            PrtgFreshness = prtgEnabled
                ? PrtgFreshnessDto.FromStore(new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)))
                : null,
            PrtgSnapshot = prtgSnapshot,
            SlowThresholdMs = performance.ThresholdMs,
            TotalOperations = performance.TotalOperations,
            SlowOperations = performance.SlowOperations,
            SlowestMs = performance.SlowestMs,
            SlowestOperation = performance.SlowestOperation,
            LastSlowAt = performance.LastSlowAt,

            // 最慢前幾支（回饋四十五輪 B6）：單一最慢值無法回答「要去看哪一頁」
            TopSlowOperations = performance.TopSlowOperations
                .Select(o => new SlowOperationDto
                {
                    Operation = o.Operation,
                    Count = o.Count,
                    MaxMs = o.MaxMs,
                    LastAt = o.LastAt
                })
                .ToList(),
            AnalysisRunning = runSnapshot.IsRunning,
            AnalysisTrigger = runSnapshot.Trigger,
            AnalysisStartedAt = runSnapshot.StartedAt,
            AnalysisPhase = runSnapshot.ActivityPhase,
            AnalysisDone = runSnapshot.ActivityDone,
            AnalysisTotal = runSnapshot.ActivityTotal,
            LastRunSucceeded = runSnapshot.LastOutcome?.Success,
            LastRunEndedAt = runSnapshot.LastOutcome?.EndedAt,

            // 回填未完成期間問題聚合的數字會偏低但看起來正常——必須看得到（P4）
            BackfillInProgress = _backfiller.Progress.InProgress,
            BackfillDone = _backfiller.Progress.Done,
            BackfillTotal = _backfiller.Progress.Total,
            SourceKeyBackfillComplete = _backfiller.IssueSourceKeyReady,
            SourceKeyBackfillDone = _backfiller.SourceKeyProgress.Done,
            SourceKeyBackfillTotal = _backfiller.SourceKeyProgress.Total,
            RiskySourceKeyBackfillComplete = _backfiller.RiskySourceKeyProgress.Completed,
            RiskySourceKeyBackfillDone = _backfiller.RiskySourceKeyProgress.Done,
            RiskySourceKeyBackfillTotal = _backfiller.RiskySourceKeyProgress.Total,
            SourceMergePreview = _backfiller.SourceMergePreview.Select(g => new SourceMergePreviewDto
            {
                SourceKey = g.SourceKey,
                EventId = g.EventId,
                Names = g.Names.ToList()
            }).ToList(),

            // 遷移未完成時處理狀態是唯讀的——這是唯一能看出「為什麼標記不了」的地方
            MigrationState = migration.State,
            MigrationBlocksWrites = migration.ShouldBlockWrites,
            MigrationDoneParts = new[] { migration.IssueHandlingDone, migration.IssueCasesDone, migration.RecordHandlingDone }
                .Count(x => x),
            MigrationError = migration.LastError,

            // 權限異動遷移狀態
            PermissionChangeMigrationState = permMigration.State,
            PermissionChangeMigrationBlocksWrites = permMigration.ShouldBlockWrites,
            PermissionChangeMigratedRows = permMigration.MigratedRows,
            PermissionChangeMigrationError = permMigration.LastError,

            // 問題機房首見日的背景合併（首次合併記錄浮水印，重啟跳過；連續失敗達上限反映為 degraded）
            IssueFirstSeenSeedState = firstSeenProgress?.State ?? IssueFirstSeenSeedStates.NotStarted,
            IssueFirstSeenSeedFailures = firstSeenProgress?.Failures ?? 0,
            IssueFirstSeenSeedError = firstSeenProgress?.LastError,

            SuspendedMailRecipients = suspendedRecipients,
            MailContributionLedger = _mail.GetContributionLedgerStatus(),

            // 主機日恢復：目前仍在等待的列（含排隊等逐列恢復的）與其中需要人看的列（內容無效、主機編號無效、超過單列上限）
            HostDayRecovery = recovery is { } waiting
                ? new HostDayRecoveryHealthDto { Waiting = waiting.Waiting, NeedsAttention = waiting.NeedsAttention, ByReason = waiting.ByReason.ToDictionary(pair => pair.Key, pair => pair.Value) }
                : null,

            CryptoKeySource = CryptoHelper.KeySource,
            CryptoKeyMismatch = cryptoKeyMismatch,
            CryptoDecryptFailure = cryptoDecryptFailure,
            CryptoKeyLocationWarning = CryptoKeyBootstrapper.KeyLocationWarning,

            Memory = memory,
            MemoryWarning = memoryWarning
        };
    }

    /// <summary>
    /// 導覽告示列用的精簡結果：整體狀態與原因清單，與 <see cref="GetDetail"/> 同一份判定。
    /// 內容與使用者無關（全站狀態），授權在控制器（Maintain 或 DevMonitor）。
    /// </summary>
    public HealthAttentionDto GetAttention()
    {
        var now = _clock.UtcNow();
        lock (_attentionLock)
        {
            if (_attentionCache != null && now - _attentionCachedAt < AttentionCacheTtl && now >= _attentionCachedAt)
                return _attentionCache;
        }

        // 在鎖外算：完整診斷會碰 DB 與 PRTG 新鮮度，不能讓所有請求排隊等同一把鎖（GetDetail 算完會順手更新快取）。
        // 同時進來的請求合併成一次診斷（SingleFlight）：換頁時導覽列、儀表板同時問，不各跑一輪完整診斷。
        return _attentionFlight.Run("attention", () =>
        {
            // 前一個合併可能剛寫入快取並結束：再看一次，避免緊接著重算
            lock (_attentionLock)
            {
                var again = _clock.UtcNow();
                if (_attentionCache != null && again - _attentionCachedAt < AttentionCacheTtl && again >= _attentionCachedAt)
                    return _attentionCache;
            }
            var detail = GetDetail();
            return new HealthAttentionDto { Status = detail.Status, Reasons = detail.StatusReasons };
        });
    }

    private void StoreAttention(HealthDetailDto detail)
    {
        var fresh = new HealthAttentionDto { Status = detail.Status, Reasons = detail.StatusReasons };
        var now = _clock.UtcNow();
        lock (_attentionLock)
        {
            _attentionCache = fresh;
            _attentionCachedAt = now;
        }
    }

    /// <summary>
    /// 告示列結果的行程內快取存活時間。導覽 <c>loadHealthBanner</c> 每進一頁就打一次，
    /// 沒有快取等於每次換頁跑一輪完整診斷（DB 探測、PRTG 新鮮度等）。
    /// 快取鍵不含使用者／可見主機：內容是同一份全站健康狀態，與呼叫者無關（授權在控制器，見上），
    /// 所以單一份快取不會把 A 看得到的東西端給 B；設定與狀態變動最多延遲這段時間反映在告示列。
    /// </summary>
    private static readonly TimeSpan AttentionCacheTtl = TimeSpan.FromSeconds(30);

    private readonly object _attentionLock = new();
    private readonly SingleFlight _attentionFlight = new();
    private HealthAttentionDto? _attentionCache;
    private DateTime _attentionCachedAt;

    /// <summary>PRTG 擷取紀錄類別的中文名（與前端 <c>prtgFreshnessLabel</c> 同一份對照；未知類別原樣）</summary>
    private static string PrtgFreshnessLabel(string category) => category switch
    {
        PrtgFreshnessStore.Devices => "裝置",
        PrtgFreshnessStore.Sensors => "感測器",
        PrtgFreshnessStore.StateChanges => "狀態變更",
        PrtgFreshnessStore.Snapshot => "即時快照",
        PrtgFreshnessStore.Values => "歷史數值",
        _ => category
    };

    /// <summary>最近一次 PRTG 同步結果的警告；讀不到就當沒有（只是補充資訊，不讓健康頁失敗）。</summary>
    private IReadOnlyList<PrtgSyncWarning> ReadPrtgSyncWarnings()
    {
        try
        {
            return new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey)).GetOrNull()?.Warnings
                   ?? new List<PrtgSyncWarning>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException
                                      or Microsoft.EntityFrameworkCore.DbUpdateException or System.Data.Common.DbException)
        {
            return Array.Empty<PrtgSyncWarning>();
        }
    }

    private PrtgSnapshotHealthDto? BuildPrtgSnapshotHealth(bool enabled, string? fetchStrategy, DateTime now)
    {
        if (!enabled) return new() { State = "disabled", Message = "PRTG 未啟用；快照不參與系統健康判定。" };
        if (_snapshotService == null) return new() { State = "unknown", Message = "目前無法取得快照診斷資料。" };

        var alert = PrtgSnapshotAlert.Evaluate(enabled, fetchStrategy, _snapshotService.GetStatus(), now);
        try
        {
            return BuildPrtgSnapshotHealth(enabled, _snapshotService.Diagnostics.ReadRecent(now, 24), alert);
        }
        catch
        {
            // 診斷讀不到時警示仍要出：停擺不能因為診斷文件讀失敗而沉默
            return alert.Any
                ? BuildPrtgSnapshotHealth(enabled, Array.Empty<PrtgSnapshotHourDiagnostic>(), alert)
                : new() { State = "unknown", Message = "快照診斷暫時無法讀取；不影響其他健康項目。" };
        }
    }

    /// <param name="alert">保守策略下的立即／逾時警示（見 <see cref="PrtgSnapshotAlert"/>）；null＝不適用。</param>
    public static PrtgSnapshotHealthDto BuildPrtgSnapshotHealth(bool enabled, IReadOnlyList<PrtgSnapshotHourDiagnostic> recent,
        PrtgSnapshotAlert? alert = null)
    {
        if (!enabled) return new() { State = "disabled", Message = "PRTG 未啟用；快照不參與系統健康判定。" };
        if (alert?.Stopped == true)
            return new()
            {
                State = "stopped",
                Warning = true,
                Message = "快照已停止：待寫檔無法使用，採集中斷。請到 PRTG 維護 > 鏡像狀態查看原因，可封存舊待寫資料並重新開始。",
                RecentHours = recent.TakeLast(2).Select(HourDto).ToList()
            };
        if (alert?.NoRecentSuccess == true)
            return new()
            {
                State = "no-recent-success",
                Warning = true,
                Message = $"快照已超過 {(int)PrtgSnapshotAlert.NoSuccessThreshold.TotalHours} 小時沒有成功取樣（自 {alert.SilentSince:yyyy-MM-dd HH:mm} 起）；請到 PRTG 維護 > 鏡像狀態查看。",
                RecentHours = recent.TakeLast(2).Select(HourDto).ToList()
            };
        if (recent.Count == 0)
            return new() { State = "unknown", Message = "尚無完整小時診斷資料。" };
        try
        {
            var history = recent.TakeLast(24).ToArray();
            var latest = history.TakeLast(2).ToArray();
            var output = new PrtgSnapshotHealthDto
            {
                RecentHours = latest.Select(HourDto).ToList()
            };
            if (latest.Length == 2 && latest.All(h => h.State == PrtgSnapshotHourState.NoTargets))
            {
                output.State = "no-targets";
                output.Message = "目前沒有快照目標；這不代表資料健康或故障。";
                return output;
            }

            bool expectedPause(PrtgSnapshotHourDiagnostic h) => h.Skips > 0 &&
                (h.ReasonCodes.ContainsKey(PrtgSnapshotSkipReasonCodes.NightlyFetchPrtgPhase)
                    || h.ReasonCodes.ContainsKey(PrtgSnapshotSkipReasonCodes.StructureSyncActive)
                    || h.ReasonCodes.ContainsKey(PrtgSnapshotSkipReasonCodes.BackfillActive)
                    || h.ReasonCodes.ContainsKey(PrtgSnapshotSkipReasonCodes.MaintenanceWindow)
                    || h.ReasonCodes.ContainsKey(PrtgSnapshotSkipReasonCodes.MaintenanceConfirmed));
            if (latest.Length == 2 && latest.All(expectedPause))
            {
                output.State = "paused";
                output.Message = "快照在預期的互斥或已確認維護時段暫停，不列為失敗。";
                return output;
            }

            bool startupPartial(PrtgSnapshotHourDiagnostic h) =>
                h.Reasons.ContainsKey("startup-partial-hour");
            var hasKnownTargets = history.Take(Math.Max(0, history.Length - 2)).Any(h => h.Targets > 0);
            var anomalies = latest.Reverse().TakeWhile(h => !startupPartial(h) && !expectedPause(h) &&
                ((h.Targets > 0 && (h.State == PrtgSnapshotHourState.Insufficient || h.WriteFailures > 0)) ||
                 (hasKnownTargets && h.Targets == 0 && h.State == PrtgSnapshotHourState.Unknown))).Count();
            output.ConsecutiveAnomalousHours = anomalies;
            output.Warning = anomalies >= 2;
            if (output.Warning)
            {
                output.State = "insufficient";
                output.Message = "連續兩個完整小時快照資料不足或寫入失敗；可到 PRTG 維護頁鏡像區檢查目標與資料。";
            }
            else if (latest[^1].State == PrtgSnapshotHourState.Healthy)
            {
                output.State = "healthy";
                output.Message = "最近完整小時的快照資料可用。";
            }
            else if (latest[^1].State == PrtgSnapshotHourState.OkCovered)
            {
                output.State = "covered";
                output.Message = "數值已由完整歷史資料覆蓋；這不單獨證明快照執行成功。";
            }
            else if (latest[^1].State == PrtgSnapshotHourState.ReportedWriteCountMetTarget)
            {
                output.State = "reported-write-count-met-target";
                output.Message = "回報寫入量達目標，逐顆覆蓋未證明。累計寫入列數可能因重試或覆寫而重複計數。";
            }
            else
            {
                output.State = "unknown";
                output.Message = "完整小時資料不足以判定；暫不列為系統故障。";
            }
            return output;
        }
        catch
        {
            return new() { State = "unknown", Message = "快照診斷暫時無法讀取；不影響其他健康項目。" };
        }
    }

    private static PrtgSnapshotHourHealthDto HourDto(PrtgSnapshotHourDiagnostic h) => new()
    {
        Hour = h.Hour,
        State = h.State.ToString().ToLowerInvariant(),
        Targets = h.Targets,
        AvailableValues = h.AvailableValues,
        WriteFailures = h.WriteFailures
    };

    /// <summary>慢操作占比達此百分比即視為 degraded</summary>
    private const double DegradedSlowRatioPercent = 5.0;

    /// <summary>託管堆占可用記憶體上限的比例達此值即視為 degraded（上限未知時不判定）</summary>
    internal const double DegradedMemoryUsageRatio = 0.85;

    /// <summary>
    /// 資料庫可達性探測：刻意用**最輕的一次讀取**（讀一個必然存在或必然不存在的 blob key），
    /// 不做 count 或 schema 查詢——健康檢查每分鐘被輪詢，本身不該成為負載。
    /// </summary>
    private bool ProbeStorage(out string? error)
    {
        try
        {
            _backend.Blob("__health_probe__").Read();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // 訊息只給 Maintain 看得到的 detail 端點（見類別註解），liveness 不回傳
            Log.Warn(ex, "健康檢查：資料庫可達性探測失敗");
            error = "資料庫讀取失敗；詳細原因見記錄檔。";
            return false;
        }
    }

    /// <summary>已啟用的郵件摘要裡，時刻或星期解析不了的項目（與寄送端同一個解析函式）</summary>
    /// <summary>開了忽略憑證錯誤又沒填指紋的連線；PRTG 未啟用、沒有啟用中的 Sentinel 時不算（不會連線）</summary>
    internal static IReadOnlyList<string> UnpinnedCertificateBypass(
        LogForesight.Core.Models.SystemSettings settings, NetiqOptions netiq, bool hasActiveSentinel)
    {
        var names = new List<string>();
        if (settings.PrtgEnabled &&
            ServerCertificateTrust.IsUnpinnedBypass(settings.PrtgTrustedCertificateSha256, settings.PrtgIgnoreSslErrors))
            names.Add("PRTG");
        if (hasActiveSentinel &&
            ServerCertificateTrust.IsUnpinnedBypass(netiq.TrustedCertificateSha256, netiq.AllowInvalidCertificates))
            names.Add("Sentinel");
        return names;
    }

    internal static IReadOnlyList<string> InvalidMailSchedules(LogForesight.Core.Models.SystemSettings settings)
    {
        var invalid = new List<string>();
        if (!settings.MailEnabled) return invalid;
        if (settings.MailDailyEnabled && !InputParsing.TryParseClockTime(settings.MailDailyTime, out _)) invalid.Add("每日摘要時刻");
        if (settings.MailWeeklyEnabled && (!InputParsing.TryParseClockTime(settings.MailWeeklyTime, out _)
                || !InputParsing.TryParseDefinedEnum<DayOfWeek>(settings.MailWeeklyDayOfWeek, out _)))
            invalid.Add("每週摘要時刻或星期");
        if (settings.MailNotifyWorkOrders && (settings.MailWorkOrderDigestTimes ?? new List<string>()).Any(x => !InputParsing.TryParseClockTime(x, out _)))
            invalid.Add("交辦摘要時刻");
        return invalid;
    }
}

public static class HealthStatuses
{
    public const string Ok = "ok";
    public const string Degraded = "degraded";
    public const string Down = "down";

}
