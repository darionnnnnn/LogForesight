using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG finding 的跨日判定（純函式，唯一一份）：標註「近 14 日第 N 次、連續第 M 日」、
/// 重複或連續時升一級嚴重度、連續太久的 down 視為沒人移除的死 sensor 而不再拉高日風險。
/// 不走 TrendAnalyzer——PRTG 規則是單日判定，跨日語意只在這裡補。
/// </summary>
public static class PrtgCrossDay
{
    /// <summary>
    /// 就地修改簽章。<paramref name="hitDates"/> 是各 EventKey 過去的命中日期（可含窗口外或當日以後的日期，
    /// 這裡只取 <c>day-14 ≤ d &lt; day</c>）。
    /// </summary>
    /// <returns>Escalated＝實際改變嚴重度的筆數；Chronic＝判為長期 Down 的筆數。</returns>
    public static (int Escalated, int Chronic) Apply(
        IReadOnlyList<LogIssueSignature> signatures,
        IReadOnlyDictionary<string, HashSet<DateTime>> hitDates,
        DateTime day)
    {
        var escalated = 0;
        var chronic = 0;
        var today = day.Date;
        var windowStart = today.AddDays(-PrtgRuleCatalog.CrossDayWindowDays);

        foreach (var sig in signatures)
        {
            if (!PrtgFindingMapper.IsPrtg(sig)) continue;

            var window = new HashSet<DateTime>();
            if (hitDates.TryGetValue(sig.EventKey, out var all))
            {
                foreach (var d in all)
                {
                    var date = d.Date;
                    if (date >= windowStart && date < today) window.Add(date);
                }
            }

            var hits = window.Count + 1;
            var consecutive = 1;
            while (window.Contains(today.AddDays(-consecutive))) consecutive++;

            if (hits < 2) continue;

            AppendDetail(sig, $"；近 {PrtgRuleCatalog.CrossDayWindowDays} 日第 {hits} 次，連續第 {consecutive} 日");

            var isDown = PrtgFindingMapper.TryGetRuleCode(sig.Source, out var code)
                         && string.Equals(code, PrtgRuleEvaluator.RuleDown, StringComparison.OrdinalIgnoreCase);
            if (isDown && consecutive >= PrtgRuleCatalog.ChronicDownDays)
            {
                // 不再拉日風險：關掉重大旗標，嚴重度封頂「中」（高嚴重度仍會把日風險拉到「中」）
                sig.ElevatesDayRisk = false;
                if (sig.Severity > IssueSeverity.Medium) sig.Severity = IssueSeverity.Medium;
                AppendDetail(sig, $"；已連續 {consecutive} 日，建議在 PRTG 暫停該 sensor 或建立抑制");
                chronic++;
                continue;
            }

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
        }

        return (escalated, chronic);
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
