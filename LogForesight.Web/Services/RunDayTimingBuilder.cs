using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 執行總表「當天開跑」的執行時間：同一天多趟的同一路取聯集，重疊不重複算（三路並行，相加會超過實際經過時間）。
/// 總耗時＝取數趟與「跟隨取數」的 AI 趟（Trigger=fetch-followup）的聯集；其他 AI 趟（排程補跑、手動）另列，不進起訖與總計。
/// 異常中斷的趟不計入聯集——它的結束時刻是站台重啟時刻，含整段停機；只留開始與最後活動。
/// </summary>
internal static class RunDayTimingBuilder
{
    private static readonly string[] BranchKeys =
    {
        RunTimingPhases.Local, RunTimingPhases.Netiq, RunTimingPhases.Prtg, RunTimingPhases.Ai
    };

    /// <summary>
    /// PRTG 這一路的子階段。補數值（<c>prtg-value-fill</c>）是頂層階段、結構同步與觸發式取數是 <c>prtg</c> 的子階段，
    /// 都算 PRTG 的時間；只認 <c>prtg</c> 會讓跑了半小時的補數值在 PRTG 欄顯示「—」。
    /// </summary>
    private static readonly string[] PrtgSubKeys =
    {
        RunTimingPhases.PrtgSync, RunTimingPhases.PrtgTriggered, RunTimingPhases.PrtgValueFill
    };

    /// <param name="dayRuns">該日開跑的取數與 AI 趟；沒有任何趟回 null</param>
    public static RunDayTimingDto? Build(IReadOnlyCollection<BatchRun> dayRuns)
    {
        if (dayRuns.Count == 0) return null;

        var fetchRuns = dayRuns.Where(r => r.JobType != BatchRun.JobTypeAi).ToList();
        var aiRuns = dayRuns.Where(r => r.JobType == BatchRun.JobTypeAi).ToList();
        var followUps = aiRuns.Where(r => r.Trigger == BatchRunTriggers.FetchFollowUp).ToList();
        var otherAi = aiRuns.Where(r => r.Trigger != BatchRunTriggers.FetchFollowUp && !r.Interrupted && r.FinishedAt.HasValue).ToList();

        var counted = fetchRuns.Concat(followUps).ToList();
        var finished = counted.Where(r => !r.Interrupted && r.FinishedAt.HasValue).ToList();
        var interrupted = counted.Where(r => r.Interrupted).ToList();
        var settledFetch = fetchRuns.Where(r => !r.Interrupted).ToList();

        return new RunDayTimingDto
        {
            FirstStartedAt = counted.Count > 0 ? counted.Min(r => r.StartedAt) : null,
            LastFinishedAt = finished.Count > 0 ? finished.Max(r => r.FinishedAt) : null,
            TotalSeconds = SpanSeconds(finished),
            OtherAiRuns = otherAi.Count,
            OtherAiSeconds = SpanSeconds(otherAi),
            FetchRuns = settledFetch.Count,
            FetchRunsWithoutPhases = settledFetch.Count(r => r.Phases == null || r.Phases.Count == 0),
            InterruptedCount = interrupted.Count,
            LastInterruptedActivityAt = interrupted.Count > 0 ? interrupted.Max(LastActivity) : null,
            // 有跟隨趟時補跑不進明細，否則展開列的 AI 時間會比總計長、對不上第二行；當天只有補跑時才顯示補跑
            Branches = BranchKeys.Select(key => BuildBranch(key, followUps.Count > 0 ? dayRuns.Except(otherAi) : dayRuns))
                .OfType<RunBranchTimingDto>().ToList()
        };
    }

    private static int? SpanSeconds(IReadOnlyCollection<BatchRun> runs) =>
        runs.Count > 0 ? UnionSeconds(runs.Select(r => (r.StartedAt, r.FinishedAt!.Value))) : null;

    private static RunBranchTimingDto? BuildBranch(string key, IEnumerable<BatchRun> runs)
    {
        // 跳過的階段沒有花到時間，沒結束的沒有訖可算；全部被濾掉（含沒有階段資料的舊紀錄）時畫面顯示「—」
        var settled = runs.Where(r => !r.Interrupted).ToList();
        var keys = key == RunTimingPhases.Prtg ? PrtgSubKeys.Append(key).ToArray() : new[] { key };
        var spans = SpansOf(settled, keys);
        // AI 趟本身就是 AI 路：沒有階段資料時用整趟起訖
        if (key == RunTimingPhases.Ai)
        {
            spans.AddRange(settled.Where(r => r.JobType == BatchRun.JobTypeAi && (r.Phases == null || r.Phases.Count == 0) && r.FinishedAt.HasValue)
                .Select(r => (r.StartedAt, r.FinishedAt!.Value)));
        }
        if (spans.Count == 0) return null;

        return new RunBranchTimingDto
        {
            Key = key,
            From = spans.Min(s => s.StartedAt),
            To = spans.Max(s => s.Value),
            Seconds = UnionSeconds(spans),
            SubPhases = key != RunTimingPhases.Prtg ? new() : PrtgSubKeys.Select(sub =>
            {
                var subSpans = SpansOf(settled, new[] { sub });
                return subSpans.Count == 0 ? null : new RunBranchTimingDto
                {
                    Key = sub,
                    From = subSpans.Min(s => s.StartedAt),
                    To = subSpans.Max(s => s.Value),
                    Seconds = UnionSeconds(subSpans)
                };
            }).OfType<RunBranchTimingDto>().ToList()
        };
    }

    private static List<(DateTime StartedAt, DateTime Value)> SpansOf(IEnumerable<BatchRun> runs, string[] keys) =>
        runs.Where(r => r.Phases != null)
            .SelectMany(r => r.Phases!)
            .Where(p => keys.Contains(p.Key) && p.FinishedAt.HasValue && p.Outcome != RunPhaseOutcomes.Skipped)
            .Select(p => (p.StartedAt, p.FinishedAt!.Value))
            .ToList();

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
        // 時鐘回撥造成 end<start 的髒資料不算負時間
        foreach (var (start, end) in spans.Select(s => (s.Start, End: s.End < s.Start ? s.Start : s.End)).OrderBy(s => s.Start))
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
