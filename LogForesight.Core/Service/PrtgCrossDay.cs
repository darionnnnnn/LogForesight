using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG finding 的跨日判定（純函式，唯一一份）：標註「近 14 日第 N 次、連續第 M 日」、
/// 重複或連續時升一級嚴重度；長期 Down 保留故障風險，由通知層處理重複提醒。
/// 不走 TrendAnalyzer——PRTG 規則是單日判定，跨日語意只在這裡補。
/// </summary>
public static class PrtgCrossDay
{
    /// <summary>
    /// 就地修改簽章。<paramref name="hitDates"/> 是各 EventKey 過去的命中日期（可含窗口外或當日以後的日期，
    /// 這裡只取 <c>day-14 ≤ d &lt; day</c>）。<paramref name="steady"/> 為持續 Warning 且數值持平的證據（見 <see cref="SteadyCandidates"/>），
    /// 省略時行為與先前完全相同。
    /// </summary>
    /// <returns>Escalated＝實際改變嚴重度的筆數；Chronic＝判為長期 Down 的筆數。</returns>
    public static (int Escalated, int Chronic) Apply(
        IReadOnlyList<LogIssueSignature> signatures,
        IReadOnlyDictionary<string, HashSet<DateTime>> hitDates,
        DateTime day,
        IReadOnlyDictionary<string, PrtgSteadyEvidence>? steady = null)
    {
        var r = ApplyDetailed(signatures, hitDates, day, steady);
        return (r.Escalated, r.Chronic);
    }

    public readonly record struct CrossDayResult(int Escalated, int Chronic, int SteadyDowngraded);

    /// <summary>同 <see cref="Apply"/>，另回報「持平降級」筆數。</summary>
    public static CrossDayResult ApplyDetailed(
        IReadOnlyList<LogIssueSignature> signatures,
        IReadOnlyDictionary<string, HashSet<DateTime>> hitDates,
        DateTime day,
        IReadOnlyDictionary<string, PrtgSteadyEvidence>? steady = null)
    {
        var escalated = 0;
        var chronic = 0;
        var steadyDowngraded = 0;
        var today = day.Date;

        foreach (var sig in signatures)
        {
            if (!PrtgFindingMapper.IsPrtg(sig)) continue;
            if (PrtgFindingMapper.TryGetRuleCode(sig.Source, out var ruleCode)
                && PrtgRuleCatalog.IsValueTrend(ruleCode))
                continue;

            var window = HitWindow(hitDates, sig.EventKey, today);
            var hits = window.Count + 1;
            var consecutive = ConsecutiveDays(window, today);

            if (hits < 2) continue;

            AppendDetail(sig, $"；近 {PrtgRuleCatalog.CrossDayWindowDays} 日第 {hits} 次，連續第 {consecutive} 日");

            // 持續 Warning 且數值持平、不在危險區：降一級、不套跨日升級；條件任一不成立則走原本流程
            if (steady != null && IsSteadyWarningCandidate(sig, consecutive)
                && steady.TryGetValue(sig.EventKey, out var evidence) && evidence.IsSteady)
            {
                sig.Severity = sig.Severity switch
                {
                    IssueSeverity.High => IssueSeverity.Medium,
                    IssueSeverity.Medium => IssueSeverity.Low,
                    _ => sig.Severity
                };
                AppendDetail(sig, $"；已持續 {consecutive} 日，數值持平（近 {PrtgRuleCatalog.SteadyWarningWindowDays} 日已用 {evidence.FirstUsedPercent:0.#}%→{evidence.LastUsedPercent:0.#}%）");
                steadyDowngraded++;
                continue;
            }

            var isDown = PrtgFindingMapper.TryGetRuleCode(sig.Source, out var code)
                         && string.Equals(code, PrtgRuleEvaluator.RuleDown, StringComparison.OrdinalIgnoreCase);
            if (consecutive >= PrtgRuleCatalog.EscalateConsecutiveDays || hits >= PrtgRuleCatalog.EscalateHitsInWindow)
            {
                var raised = sig.Severity switch
                {
                    IssueSeverity.Low => IssueSeverity.Medium,
                    IssueSeverity.Medium => IssueSeverity.High,
                    _ => sig.Severity
                };
                if (raised != sig.Severity)
                {
                    sig.Severity = raised;
                    escalated++;
                }
            }
            if (isDown && consecutive >= PrtgRuleCatalog.ChronicDownDays)
            {
                AppendDetail(sig, $"；已連續 {consecutive} 日，故障尚未恢復，請確認處置狀態");
                chronic++;
            }
        }

        return new CrossDayResult(escalated, chronic, steadyDowngraded);
    }

    /// <summary>
    /// 需要查數值的候選：warning、未升日風險、歷史顯示連續 ≥ <see cref="PrtgRuleCatalog.SteadyWarningMinConsecutiveDays"/> 日。
    /// 連續日數與 <see cref="Apply"/> 共用 <see cref="ConsecutiveDays"/>。回傳 EventKey → 連續日數。
    /// </summary>
    public static IReadOnlyDictionary<string, int> SteadyCandidates(
        IEnumerable<LogIssueSignature> signatures,
        IReadOnlyDictionary<string, HashSet<DateTime>> hitDates,
        DateTime day)
    {
        var today = day.Date;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sig in signatures)
        {
            if (!PrtgFindingMapper.IsPrtg(sig)) continue;
            var consecutive = ConsecutiveDays(HitWindow(hitDates, sig.EventKey, today), today);
            if (IsSteadyWarningCandidate(sig, consecutive)) result[sig.EventKey] = consecutive;
        }
        return result;
    }

    private static bool IsSteadyWarningCandidate(LogIssueSignature sig, int consecutive) =>
        consecutive >= PrtgRuleCatalog.SteadyWarningMinConsecutiveDays
        && !sig.ElevatesDayRisk
        && PrtgFindingMapper.TryGetRuleCode(sig.Source, out var code)
        && string.Equals(code, PrtgRuleEvaluator.RuleWarning, StringComparison.OrdinalIgnoreCase);

    private static HashSet<DateTime> HitWindow(
        IReadOnlyDictionary<string, HashSet<DateTime>> hitDates, string eventKey, DateTime today)
    {
        var windowStart = today.AddDays(-PrtgRuleCatalog.CrossDayWindowDays);
        var window = new HashSet<DateTime>();
        if (hitDates.TryGetValue(eventKey, out var all))
        {
            foreach (var d in all)
            {
                var date = d.Date;
                if (date >= windowStart && date < today) window.Add(date);
            }
        }
        return window;
    }

    /// <summary>連續第 M 日（含當日）：從當日往前數，歷史窗口內連續命中的日數 + 1。</summary>
    private static int ConsecutiveDays(HashSet<DateTime> window, DateTime today)
    {
        var consecutive = 1;
        while (window.Contains(today.AddDays(-consecutive))) consecutive++;
        return consecutive;
    }

    private static void AppendDetail(LogIssueSignature sig, string text)
    {
        if (sig.SampleMessages.Count == 0)
        {
            sig.SampleMessages.Add(text.TrimStart('；'));
        }
        else
        {
            sig.SampleMessages[0] += text;
        }
    }
}

/// <summary>
/// 持續 Warning 的數值證據（窗口內每日代表值轉成已用百分比後的摘要）。
/// 由 <see cref="PrtgDailyPipeline"/> 以 <see cref="PrtgValueAssessment"/> 的結果算好，<see cref="PrtgCrossDay"/> 只做判定。
/// </summary>
public sealed record PrtgSteadyEvidence(PrtgValueSemantic Semantic, int UsableDays, double FirstUsedPercent, double LastUsedPercent)
{
    /// <summary>磁碟／記憶體以外的語意、可用日不足、上升超過容許值或進入危險區都回 false。</summary>
    public bool IsSteady
    {
        get
        {
            double? danger = Semantic switch
            {
                PrtgValueSemantic.DiskPercentFree => PrtgRuleCatalog.SteadyWarningDiskDangerUsedPercent,
                PrtgValueSemantic.MemoryPercentFree => PrtgRuleCatalog.SteadyWarningMemoryDangerUsedPercent,
                _ => null
            };
            return danger is { } limit
                   && UsableDays >= PrtgRuleCatalog.SteadyWarningMinUsableDays
                   && LastUsedPercent - FirstUsedPercent <= PrtgRuleCatalog.SteadyWarningMaxRisePoints
                   && LastUsedPercent < limit;
        }
    }

    /// <summary>
    /// 從數值評估列取證據；語意不是磁碟／記憶體、沒有可用日、或最後一個可用日早於評估日前一天回 null——
    /// 數值已經斷了幾天（例如快照停擺）時，幾天前的值不能代表現在，不得據此降級。
    /// </summary>
    public static PrtgSteadyEvidence? FromRow(PrtgValueAssessmentRow row, DateOnly completedDay)
    {
        if (row.Semantic is not (PrtgValueSemantic.DiskPercentFree or PrtgValueSemantic.MemoryPercentFree)) return null;
        if (row.Reason == PrtgValueReason.MappingInvalid || row.Daily.Count == 0) return null;
        var ordered = row.Daily.OrderBy(d => d.Day).ToList();
        if (ordered[^1].Day < completedDay.AddDays(-PrtgRuleCatalog.SteadyWarningMaxStaleDays)) return null;
        var semantic = row.Semantic.Value;
        return new PrtgSteadyEvidence(semantic, ordered.Count,
            PrtgValueAssessment.UsedPercent(semantic, ordered[0].Value), PrtgValueAssessment.UsedPercent(semantic, ordered[^1].Value));
    }
}
