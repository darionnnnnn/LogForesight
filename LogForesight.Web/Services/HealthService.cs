using System.Reflection;
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
    private readonly IssueFirstSeenSeedHostedService? _firstSeenSeedService;

    public HealthService(
        StorageBackend backend,
        SchedulerRunState runState,
        TopIssueBackfiller backfiller,
        MailNotificationService mail,
        IssueFirstSeenSeedHostedService? firstSeenSeedService = null)
    {
        _backend = backend;
        _runState = runState;
        _backfiller = backfiller;
        _mail = mail;
        _firstSeenSeedService = firstSeenSeedService;
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

    public HealthDetailDto GetDetail()
    {
        var storageOk = ProbeStorage(out var storageError);
        var performance = _backend.Performance.Snapshot();
        var migration = _backend.HandlingMigrator.State;

        // 診斷頁只有一行進度可顯示：主／子軌取捨（子進度優先）由 LatestActivity 單點決定
        // （回饋十四輪 UI-6 體檢，與 /api/run-activity 同一個選擇邏輯）——只讀主進度的話，
        // AI 佇列消化階段這裡會停在搜尋階段的凍結數字，看起來像分析卡死。
        var analysisActivity = _runState.LatestActivity();

        var firstSeenProgress = _firstSeenSeedService?.Progress;
        var firstSeenFailed = firstSeenProgress?.IsFailed == true;

        // 「慢操作占比過高」或「首見日合併連續失敗達上限」不等於壞掉，但它是使用者開始抱怨之前唯一的先行指標——
        // 因此獨立成 degraded 狀態，而不是併進 ok
        var degraded = (performance.TotalOperations > 0 &&
                       performance.SlowOperations * 100.0 / performance.TotalOperations >= DegradedSlowRatioPercent)
                       || firstSeenFailed;

        return new HealthDetailDto
        {
            Status = !storageOk ? HealthStatuses.Down : degraded ? HealthStatuses.Degraded : HealthStatuses.Ok,
            Version = Version,
            StorageOk = storageOk,
            StorageError = storageError,
            SlowThresholdMs = performance.ThresholdMs,
            TotalOperations = performance.TotalOperations,
            SlowOperations = performance.SlowOperations,
            SlowestMs = performance.SlowestMs,
            SlowestOperation = performance.SlowestOperation,
            LastSlowAt = performance.LastSlowAt,
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

            // 遷移未完成時處理狀態是唯讀的——這是唯一能看出「為什麼標記不了」的地方
            MigrationState = migration.State,
            MigrationBlocksWrites = migration.ShouldBlockWrites,
            MigrationDoneParts = new[] { migration.IssueHandlingDone, migration.IssueCasesDone, migration.RecordHandlingDone }
                .Count(x => x),
            MigrationError = migration.LastError,

            // 問題機房首見日的背景合併（首次合併記錄浮水印，重啟跳過；連續失敗達上限反映為 degraded）
            IssueFirstSeenSeedState = firstSeenProgress?.State ?? IssueFirstSeenSeedStates.NotStarted,
            IssueFirstSeenSeedFailures = firstSeenProgress?.Failures ?? 0,
            IssueFirstSeenSeedError = firstSeenProgress?.LastError,

            SuspendedMailRecipients = _mail.GetSuspendedRecipients()
        };
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
