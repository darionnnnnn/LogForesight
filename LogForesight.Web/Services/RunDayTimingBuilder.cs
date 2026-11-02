using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 執行總表「當天開跑」的執行時間：同一天多趟的同一路取聯集，重疊不重複算（三路並行，相加會超過實際經過時間）。
/// 異常中斷的趟不計入聯集——它的結束時刻是站台重啟時刻，含整段停機；只留開始與最後活動。
/// </summary>
internal static class RunDayTimingBuilder
{
    private static readonly string[] BranchKeys =
    {
        RunTimingPhases.Local, RunTimingPhases.Netiq, RunTimingPhases.Prtg, RunTimingPhases.Ai
    };

    /// <param name="dayRuns">該日開跑的取數與 AI 趟；沒有任何趟回 null</param>
    public static RunDayTimingDto? Build(IReadOnlyCollection<BatchRun> dayRuns)
    {
        if (dayRuns.Count == 0) return null;

        var fetchRuns = dayRuns.Where(r => r.JobType != BatchRun.JobTypeAi).ToList();
        var finished = fetchRuns.Where(r => !r.Interrupted && r.FinishedAt.HasValue).ToList();
        var interrupted = fetchRuns.Where(r => r.Interrupted).ToList();

        return new RunDayTimingDto
        {
            FirstStartedAt = fetchRuns.Count > 0 ? fetchRuns.Min(r => r.StartedAt) : null,
            LastFinishedAt = finished.Count > 0 ? finished.Max(r => r.FinishedAt) : null,
            TotalSeconds = finished.Count > 0
                ? UnionSeconds(finished.Select(r => (r.StartedAt, r.FinishedAt!.Value)))
                : null,
            InterruptedCount = interrupted.Count,
            LastInterruptedActivityAt = interrupted.Count > 0 ? interrupted.Max(LastActivity) : null,
            Branches = BranchKeys.Select(key => BuildBranch(key, dayRuns)).OfType<RunBranchTimingDto>().ToList()
        };
    }

    private static RunBranchTimingDto? BuildBranch(string key, IEnumerable<BatchRun> runs)
    {
        // 跳過的階段沒有花到時間，沒結束的沒有訖可算；全部被濾掉（含沒有階段資料的舊紀錄）時畫面顯示「—」
        var spans = runs.Where(r => !r.Interrupted && r.Phases != null)
            .SelectMany(r => r.Phases!)
            .Where(p => p.Key == key && p.FinishedAt.HasValue && p.Outcome != RunPhaseOutcomes.Skipped)
            .Select(p => (p.StartedAt, p.FinishedAt!.Value))
            .ToList();
        if (spans.Count == 0) return null;

        return new RunBranchTimingDto
        {
            Key = key,
            From = spans.Min(s => s.StartedAt),
            To = spans.Max(s => s.Value),
            Seconds = UnionSeconds(spans)
        };
    }

    private static DateTime LastActivity(BatchRun run)
    {
        var last = run.Phases?.Where(p => p.FinishedAt.HasValue).Select(p => p.FinishedAt!.Value).DefaultIfEmpty(run.StartedAt).Max();
        return last ?? run.StartedAt;
    }

    internal static int UnionSeconds(IEnumerable<(DateTime Start, DateTime End)> spans)
    {
        var total = TimeSpan.Zero;
        DateTime? currentStart = null;
        DateTime currentEnd = default;
        foreach (var (start, end) in spans.OrderBy(s => s.Start))
        {
            if (currentStart == null || start > currentEnd)
            {
                if (currentStart != null) total += currentEnd - currentStart.Value;
                currentStart = start;
                currentEnd = end;
            }
            else if (end > currentEnd)
            {
                currentEnd = end;
            }
        }
        if (currentStart != null) total += currentEnd - currentStart.Value;
        return (int)total.TotalSeconds;
    }
}
