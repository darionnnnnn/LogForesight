using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 問題排行的組裝（docs/SCALE-ISSUE-FIRST-PLAN.md P4／§10）：儀表板「重點問題」卡與
/// 報表「問題排行」共用同一份投影，兩頁的數字因此必然一致。
///
/// **取代的是什麼**：<c>RecordStatsBuilder.BuildIssueRanking</c> 過去在**記憶體**對
/// 整段期間的紀錄做 GroupBy——6000 台 × 30 天約 18 萬筆紀錄、近 GB 的 ContentJson，
/// 儀表板每次載入都做一遍，報表還要再做一次前期對比（雙倍）。現在走
/// <see cref="IIssueAggregateQuery"/> 的一句 GROUP BY。
///
/// **順帶修掉一個既有的不一致**（規劃 §8.1 缺陷 2）：舊實作用 <c>record.Host</c>
/// （紀錄自帶的原始名稱快照）算主機數，而依問題視角用映射到存活主機的名稱——
/// 環境裡有合併過的主機時，儀表板與依問題視角的主機數會對不上，
/// 而 WEB-SPEC §9.1 宣稱「兩頁數字必然一致」。改走 lf_top_issues 的
/// **存活主機 id** 之後，同一台實體機器不再被算成兩台。
/// </summary>
public class IssueRankingBuilder
{
    private readonly IIssueAggregateQuery _aggregates;

    public IssueRankingBuilder(IIssueAggregateQuery aggregates)
    {
        _aggregates = aggregates;
    }

    /// <summary>
    /// 期間內的問題排行。<paramref name="visibleHostIds"/> 為可見範圍
    /// （null＝不限制；空集合＝零結果，與查詢層同一套授權語意）。
    /// <paramref name="totalHosts"/> 供影響率使用，0 時影響率為 0。
    /// </summary>
    public List<IssueRankingDto> Build(
        DateTime from, DateTime to, IReadOnlyCollection<long>? visibleHostIds, int totalHosts,
        IReadOnlyDictionary<string, IssueHandlingRollup>? handlingByIssue = null)
    {
        var periodDays = Math.Max(1, (to.Date - from.Date).Days + 1);

        // 前期對比用**等長**的前一個期間——拿一週跟一個月比毫無意義（沿用報表既有規則）
        var previousTo = from.Date.AddDays(-1);
        var previousFrom = previousTo.AddDays(-periodDays + 1);
        var previous = _aggregates.Aggregate(previousFrom, previousTo, visibleHostIds)
            .ToDictionary(a => (a.Source, a.EventId));

        var today = DateTime.Today;

        return _aggregates.Aggregate(from, to, visibleHostIds)
            .Select(a =>
            {
                previous.TryGetValue((a.Source, a.EventId), out var prev);
                var rollup = LookupRollup(handlingByIssue, a);

                return new IssueRankingDto
                {
                    Source = a.Source,
                    EventId = a.EventId,
                    Category = a.Category,
                    MaxSeverity = ((IssueSeverity)a.MaxSeverityRank).ToString(),
                    HostCount = a.HostCount,
                    DayCount = a.DayCount,
                    TotalCount = (int)Math.Min(a.TotalCount, int.MaxValue),

                    FirstSeen = a.FirstSeen.ToString("yyyy-MM-dd"),
                    LastSeen = a.LastSeen.ToString("yyyy-MM-dd"),
                    ActiveDays = a.ActiveDays,
                    PeriodDays = periodDays,
                    DaysSinceLastSeen = Math.Max(0, (today - a.LastSeen.Date).Days),
                    HostRatio = totalHosts > 0 ? (double)a.HostCount / totalHosts : 0,
                    ElevatesDayRisk = a.ElevatesDayRisk,

                    PreviousHostCount = prev?.HostCount ?? 0,
                    PreviousTotalCount = (int)Math.Min(prev?.TotalCount ?? 0, int.MaxValue),
                    IsNew = prev == null,

                    OpenHostCount = rollup?.OpenHostCount ?? 0,
                    ResolvedHostCount = rollup?.ResolvedHostCount ?? 0
                };
            })
            // 預設排序維持既有語意（最高嚴重度 → 主機數 → 總次數），呼叫端要換維度時自行重排——
            // 三張問句式卡片各有各的排序（§10.4），排序規則屬於呈現層決策，不寫死在這裡
            .OrderByDescending(i => Enum.TryParse<IssueSeverity>(i.MaxSeverity, out var s) ? (int)s : -1)
            .ThenByDescending(i => i.HostCount)
            .ThenByDescending(i => i.TotalCount)
            .ToList();
    }

    /// <summary>
    /// 處理狀態以**完整簽章**為鍵，而排行以 (Source, EventId) 為單位——
    /// 一個群組底下可能有多個完整簽章，任一個有未處理主機就算未處理（規劃 §8.1 缺陷 1）。
    /// </summary>
    private static IssueHandlingRollup? LookupRollup(
        IReadOnlyDictionary<string, IssueHandlingRollup>? byIssueKey, IssueAggregate aggregate)
    {
        if (byIssueKey == null || aggregate.IssueKeys.Count == 0) return null;

        IssueHandlingRollup? merged = null;
        foreach (var key in aggregate.IssueKeys)
        {
            if (!byIssueKey.TryGetValue(key, out var one)) continue;
            merged = merged == null
                ? one
                : new IssueHandlingRollup(
                    merged.OpenHostCount + one.OpenHostCount,
                    merged.ResolvedHostCount + one.ResolvedHostCount);
        }
        return merged;
    }
}

/// <summary>單一問題簽章的處理概況彙總（§10.6：全部有結論的問題不進重點清單）</summary>
public sealed record IssueHandlingRollup(int OpenHostCount, int ResolvedHostCount);
