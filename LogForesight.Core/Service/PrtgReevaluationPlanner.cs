using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 缺日補評估規劃器（純函數）。
/// </summary>
public static class PrtgReevaluationPlanner
{
    public const int LookbackDays = 7;

    /// <summary>
    /// 最近 LookbackDays 天內需要補評估的 PRTG 資料日（由近到遠、不重複、不含 today-1、不早於保留期起點 today-retentionDays）。
    /// 規則：只看 JobType 不是 ai 的執行；對每個資料日 D，取 StartedAt 最晚、且 PrtgDays 含 D 的那一筆的 PrtgDayStat；
    /// 其 NeedsReevaluation 為 true → D 入選。
    /// D 沒有任何執行的 PrtgDays 含它、但存在 Trigger=="schedule" 且 Interrupted 的執行其 StartedAt.Date.AddDays(-1)==D → D 入選。
    /// </summary>
    public static IReadOnlyList<DateTime> Select(IReadOnlyList<BatchRun> recentRuns, DateTime today, int retentionDays)
    {
        if (recentRuns == null || recentRuns.Count == 0)
        {
            return Array.Empty<DateTime>();
        }

        var todayDate = today.Date;
        var minDate = todayDate.AddDays(-Math.Min(LookbackDays, retentionDays));
        var maxDate = todayDate.AddDays(-2);

        if (minDate > maxDate)
        {
            return Array.Empty<DateTime>();
        }

        var nonAiRuns = recentRuns.Where(r => r.JobType != BatchRun.JobTypeAi).ToList();

        var selected = new List<DateTime>();

        for (var date = maxDate; date >= minDate; date = date.AddDays(-1))
        {
            if (BatchRunQueries.LatestPrtgDayStat(nonAiRuns, date) is { } latest)
            {
                if (latest.Stat.NeedsReevaluation)
                {
                    selected.Add(date);
                }
            }
            else
            {
                var hasInterruptedSchedule = nonAiRuns.Any(r =>
                    r.IsScheduledFetch() &&
                    r.Interrupted &&
                    r.StartedAt.Date.AddDays(-1) == date);

                if (hasInterruptedSchedule)
                {
                    selected.Add(date);
                }
            }
        }

        return selected;
    }

    /// <summary>
    /// 最近一趟已結束的取數執行（JobType 不是 ai、FinishedAt 非空，取 StartedAt 最晚者）中，第一個 Outcome 不是 success 的逐日統計；
    /// 沒有或全部 success 回 null。
    /// </summary>
    public static PrtgDayStat? LastRunIssue(IReadOnlyList<BatchRun> recentRuns)
    {
        if (recentRuns == null || recentRuns.Count == 0)
        {
            return null;
        }

        var lastFinishedRun = recentRuns
            .Where(r => r.JobType != BatchRun.JobTypeAi && r.FinishedAt.HasValue)
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.RunId)
            .FirstOrDefault();

        if (lastFinishedRun?.PrtgDays == null || lastFinishedRun.PrtgDays.Count == 0)
        {
            return null;
        }

        return lastFinishedRun.PrtgDays.FirstOrDefault(s => s.Outcome != BatchRun.PrtgOutcomeSuccess);
    }
    /// <summary>
    /// 本趟能否沿用同日另一趟的來源同步（docs/PRTG-SPEC.md §3）：只有排程趟、Full 範圍，而且本趟每一天
    /// 最新的那筆統計都是今天開跑、來源同步完整。手動執行一律重做——按下去就是要重跑。
    /// 回傳來源同步實際發生的時刻（沿用趟會一路傳下去，不會變成沿用趟自己的時間）；不能沿用回 null。「最新」刻意不跳過失敗趟：同步失敗之後的下一個窗口要重試。
    /// </summary>
    public static DateTime? ReusableSyncFrom(string? trigger, RunScope scope, IReadOnlyList<BatchRun>? recentRuns,
        IReadOnlyList<DateTime> days, DateTime today)
    {
        if (trigger != BatchRunTriggers.Schedule || scope != RunScope.Full || recentRuns == null || days.Count == 0)
        {
            return null;
        }

        DateTime? reusedFrom = null;
        foreach (var day in days)
        {
            if (BatchRunQueries.LatestPrtgDayStat(recentRuns, day) is not { } latest ||
                !latest.Stat.SourceSynced ||
                latest.Run.StartedAt.Date != today.Date)
            {
                return null;
            }

            var syncedAt = latest.Stat.SourceSyncedAt ?? latest.Run.StartedAt;
            if (reusedFrom == null || syncedAt < reusedFrom) reusedFrom = syncedAt;
        }

        return reusedFrom;
    }
}
