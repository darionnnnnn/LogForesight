using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 排程資料新鮮度服務（任務 A-3）。
/// 判定系統多久沒有成功跑過排程，供健康檢查、API 與摘要信警示共用。
/// </summary>
public class ScheduleFreshnessService
{
    private readonly BatchRunStore _runs;
    private readonly ScheduleOptionsStore _scheduleOptions;

    public ScheduleFreshnessService(BatchRunStore runs, ScheduleOptionsStore scheduleOptions)
    {
        _runs = runs;
        _scheduleOptions = scheduleOptions;
    }

    /// <summary>
    /// 計算排程資料新鮮度。
    /// </summary>
    public ScheduleFreshnessDto GetScheduleFreshness(DateTime now)
    {
        var options = _scheduleOptions.Get();
        var scheduleEnabled = options.Enabled;

        // 近 14 天的執行紀錄中，JobType != BatchRun.JobTypeAi、Trigger == "schedule"、FinishedAt != null、BatchRunStatus.UpdatedData(...) 為 true 的最新一筆之 FinishedAt。
        var runs = _runs.GetRecentRuns(14, hostNames: null);
        var latestSuccess = runs
            .Where(r => r.JobType != BatchRun.JobTypeAi &&
                        r.Trigger == "schedule" &&
                        r.FinishedAt != null &&
                        BatchRunStatus.UpdatedData(BatchRunStatus.Compute(r, now, RunMonitorService.StuckThreshold)))
            .OrderByDescending(r => r.FinishedAt)
            .FirstOrDefault();

        var lastSuccessAt = latestSuccess?.FinishedAt;

        // Stale：ScheduleEnabled 且（LastSuccessAt 為 null 或 now - LastSuccessAt > 48 小時），
        // 且 排程啟用時間（ScheduleOptions.EnabledAt，null＝很久以前）早於 now - 48 小時（剛啟用排程的站台不算過期）。
        var notRunRecently = lastSuccessAt == null || (now - lastSuccessAt.Value).TotalHours > 48;
        var scheduleUpdatedBefore48h = options.EnabledAt == null || options.EnabledAt < now.AddHours(-48);
        var stale = scheduleEnabled && notRunRecently && scheduleUpdatedBefore48h;

        // AckedUntil：ScheduleOptions.FreshnessAckUntil；Stale 為 true 但 now < AckedUntil 時 Acked = true。
        var ackedUntil = options.FreshnessAckUntil;
        var acked = stale && ackedUntil.HasValue && now < ackedUntil.Value;

        return new ScheduleFreshnessDto
        {
            ScheduleEnabled = scheduleEnabled,
            LastSuccessAt = lastSuccessAt,
            Stale = stale,
            AckedUntil = ackedUntil,
            Acked = acked
        };
    }
}
