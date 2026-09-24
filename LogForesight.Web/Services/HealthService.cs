using System.Reflection;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
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
    private readonly StorageBackend _backend;
    private readonly SchedulerRunState _runState;
    private readonly TopIssueBackfiller _backfiller;
    private readonly MailNotificationService _mail;
    private readonly ScheduleFreshnessService _freshness;
    private readonly IssueFirstSeenSeedHostedService? _firstSeenSeedService;
    private readonly PrtgSnapshotHostedService? _snapshotService;

    public HealthService(
        StorageBackend backend,
        SchedulerRunState runState,
        TopIssueBackfiller backfiller,
        MailNotificationService mail,
        ScheduleFreshnessService freshness,
        IssueFirstSeenSeedHostedService? firstSeenSeedService = null,
        PrtgSnapshotHostedService? snapshotService = null)
    {
        _backend = backend;
        _runState = runState;
        _backfiller = backfiller;
        _mail = mail;
        _freshness = freshness;
        _firstSeenSeedService = firstSeenSeedService;
        _snapshotService = snapshotService;
    }

    /// <summary>組建版本（Directory.Build.props 的 Version＋commit）</summary>
    private static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    public HealthDto GetLiveness()
    {
        var storageOk = ProbeStorage(out _);
        return new HealthDto
        {
            Status = storageOk ? HealthStatuses.Ok : HealthStatuses.Down,
            Version = Version,
            StorageOk = storageOk
        };
    }

    /// <summary>取得排程資料新鮮度（任務 A-3）</summary>
    public ScheduleFreshnessDto GetScheduleFreshness(DateTime now) => _freshness.GetScheduleFreshness(now);

    public HealthDetailDto GetDetail()
    {
        var storageOk = ProbeStorage(out var storageError);
        var performance = _backend.Performance.Snapshot();
        if (!storageOk)
        {
            // Storage-backed status providers can fail independently after the probe. Return the
            // minimal diagnostic DTO here so a failed probe never triggers another database read.
            return new HealthDetailDto
            {
                Status = HealthStatuses.Down,
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
                PrtgSnapshot = null
            };
        }

        var migration = _backend.HandlingMigrator.State;
        var permMigration = _backend.PermissionChangeMigrator.State;
        var freshness = _freshness.GetScheduleFreshness(DateTime.Now);
        var prtgEnabled = new SystemSettingsStore(_backend.Blob("system_settings")).Get().PrtgEnabled;
        var prtgSnapshot = BuildPrtgSnapshotHealth(prtgEnabled, DateTime.Now);

        // 診斷頁只有一行進度可顯示：主／子軌取捨（子進度優先）由 LatestActivity 單點決定
        // （回饋十四輪 UI-6 體檢，與 /api/run-activity 同一個選擇邏輯）——只讀主進度的話，
        // AI 佇列消化階段這裡會停在搜尋階段的凍結數字，看起來像分析卡死。
        var analysisActivity = _runState.LatestActivity();

        var firstSeenProgress = _firstSeenSeedService?.Progress;
        var firstSeenFailed = firstSeenProgress?.IsFailed == true;

        // 密碼欄位解不開時各功能只會靜默當成未設定——必須在這裡看得到
        var cryptoKeyMismatch = CryptoKeyBootstrapper.KeyMismatch;
        var cryptoDecryptFailure = CryptoHelper.DecryptFailureSeen;

        // 「慢操作占比過高」或「首見日合併連續失敗達上限」不等於壞掉，但它是使用者開始抱怨之前唯一的先行指標——
        // 因此獨立成 degraded 狀態，而不是併進 ok。排程資料過期且未確認靜音時亦視為 degraded（任務 A-3）
        var degraded = (performance.TotalOperations > 0 &&
                       performance.SlowOperations * 100.0 / performance.TotalOperations >= DegradedSlowRatioPercent)
                       || firstSeenFailed
                       || (freshness.Stale && !freshness.Acked)
                       || prtgSnapshot?.Warning == true
                       || cryptoKeyMismatch || cryptoDecryptFailure;

        return new HealthDetailDto
        {
            Status = degraded ? HealthStatuses.Degraded : HealthStatuses.Ok,
            Version = Version,
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
            AnalysisRunning = _runState.IsRunning,
            AnalysisTrigger = _runState.Trigger,
            AnalysisStartedAt = _runState.StartedAt,
            AnalysisPhase = analysisActivity.Phase,
            AnalysisDone = analysisActivity.Done,
            AnalysisTotal = analysisActivity.Total,
            LastRunSucceeded = _runState.LastOutcome?.Success,
            LastRunEndedAt = _runState.LastOutcome?.EndedAt,

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

            SuspendedMailRecipients = _mail.GetSuspendedRecipients(),

            CryptoKeySource = CryptoHelper.KeySource,
            CryptoKeyMismatch = cryptoKeyMismatch,
            CryptoDecryptFailure = cryptoDecryptFailure
        };
    }

    private PrtgSnapshotHealthDto? BuildPrtgSnapshotHealth(bool enabled, DateTime now)
    {
        if (!enabled) return new() { State = "disabled", Message = "PRTG 未啟用；快照不參與系統健康判定。" };
        if (_snapshotService == null) return new() { State = "unknown", Message = "目前無法取得快照診斷資料。" };

        try
        {
            return BuildPrtgSnapshotHealth(enabled, _snapshotService.Diagnostics.ReadRecent(now, 24));
        }
        catch
        {
            return new() { State = "unknown", Message = "快照診斷暫時無法讀取；不影響其他健康項目。" };
        }
    }

    public static PrtgSnapshotHealthDto BuildPrtgSnapshotHealth(bool enabled, IReadOnlyList<PrtgSnapshotHourDiagnostic> recent)
    {
        if (!enabled) return new() { State = "disabled", Message = "PRTG 未啟用；快照不參與系統健康判定。" };
        if (recent.Count == 0)
            return new() { State = "unknown", Message = "尚無完整小時診斷資料。" };
        try
        {
            var history = recent.TakeLast(24).ToArray();
            var latest = history.TakeLast(2).ToArray();
            var output = new PrtgSnapshotHealthDto
            {
                RecentHours = latest.Select(h => new PrtgSnapshotHourHealthDto
                {
                    Hour = h.Hour,
                    State = h.State.ToString().ToLowerInvariant(),
                    Targets = h.Targets,
                    AvailableValues = h.AvailableValues,
                    WriteFailures = h.WriteFailures
                }).ToList()
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

    /// <summary>慢操作占比達此百分比即視為 degraded</summary>
    private const double DegradedSlowRatioPercent = 5.0;

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
            error = ex.Message;
            return false;
        }
    }
}

public static class HealthStatuses
{
    public const string Ok = "ok";
    public const string Degraded = "degraded";
    public const string Down = "down";
}
