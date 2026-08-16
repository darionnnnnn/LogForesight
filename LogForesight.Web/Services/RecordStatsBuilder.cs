using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 儀表板與報表共用的統計組裝（docs/archive/HISTORY.md S4/S5）。取代原本
/// DashboardService／ReportService 各自維護的一份幾乎逐行相同的類別統計與主機排行邏輯——
/// 兩處遲早會在某次修改中漂移成不同的數字，尤其是嚴重度可見性（S1）接上之後兩邊都要同步改。
///
/// 呼叫端的 <paramref name="records"/> 必須已經過 <see cref="Repositories.IRecordRepository"/>
/// 的可見範圍與嚴重度可見性過濾——本類別不做任何過濾判斷，只負責聚合與排序。
/// </summary>
public static class RecordStatsBuilder
{
    /// <summary>
    /// 依風險類別彙總（回饋十九輪批次D，取代原本對 <c>records</c> 在記憶體用
    /// <c>CategoryAggregator</c> 逐日彙總再合併的版本——那條路徑撈的是整段期間的
    /// <c>DailyAnalysisRecord</c>，改走 <see cref="IIssueAggregateQuery.AggregateByCategory"/>
    /// 的 SQL 聚合，儀表板與報表共用同一個查詢方法，數字不可能漂移）。
    ///
    /// 排序：最高嚴重度（依風險資訊去重口徑）→ 風險資訊數，與依問題視角/重點問題卡同一套慣例。
    /// </summary>
    public static List<DashboardCategoryDto> BuildCategoryCards(IReadOnlyList<CategoryAggregate> aggregates) =>
        aggregates
            .Select(a => new DashboardCategoryDto
            {
                Category = a.Category,
                RiskItemCount = a.RiskItemCount,
                CumulativeCount = a.CumulativeCount,
                TotalEvents = a.TotalEvents,
                HighCount = a.HighCount,
                MediumCount = a.MediumCount,
                LowCount = a.LowCount,
                ElevatesCount = a.ElevatesCount,
                AffectedHosts = a.AffectedHosts
            })
            .OrderByDescending(c => c.HighCount > 0 ? 2 : c.MediumCount > 0 ? 1 : 0)
            .ThenByDescending(c => c.RiskItemCount)
            .ToList();

    /// <summary>
    /// 主機告警排行：緊急程度＝高風險日數 → 有關聯訊號的日數 → 中風險日數（§DB-PLAN E 節）。
    /// 回傳**完整排序清單**，不做 Top N 切分——儀表板只取前 10，報表另切 Top10＋「其他」彙總，
    /// 兩邊切分方式不同，留給呼叫端自行決定。
    /// </summary>
    public static List<DashboardHostDto> BuildHostRanking(
        List<DailyAnalysisRecord> records, IReadOnlyDictionary<string, WebHost> hostsByName)
    {
        return records
            .GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DashboardHostDto
            {
                HostId = hostsByName.TryGetValue(group.Key, out var host) ? host.HostId : 0,
                HostName = group.Key,
                HighRiskDays = group.Count(r => r.RiskLevel == RiskLevels.High),
                MediumRiskDays = group.Count(r => r.RiskLevel == RiskLevels.Medium),
                CorrelationDays = group.Count(r => r.CorrelationAlerts.Count > 0),
                LatestRiskLevel = group.OrderByDescending(r => r.Date).First().RiskLevel,
                LatestHeadline = group.OrderByDescending(r => r.Date).First().Headline
            })
            .Where(h => h.HighRiskDays > 0 || h.MediumRiskDays > 0)
            .OrderByDescending(h => h.HighRiskDays)
            .ThenByDescending(h => h.CorrelationDays)
            .ThenByDescending(h => h.MediumRiskDays)
            .ToList();
    }

    public static List<DashboardHostDto> BuildHostRanking(
        IReadOnlyList<HostRiskAggregate> hosts, IReadOnlyDictionary<long, WebHost> hostsById)
    {
        return hosts
            .Where(h => h.HighRiskDays > 0 || h.MediumRiskDays > 0)
            .Select(h => new DashboardHostDto
            {
                HostId = h.HostId,
                HostName = hostsById.TryGetValue(h.HostId, out var wh) ? wh.HostName : h.HostId.ToString(),
                HighRiskDays = h.HighRiskDays,
                MediumRiskDays = h.MediumRiskDays,
                CorrelationDays = h.CorrelationDays,
                LatestRiskLevel = h.LatestRiskLevel,
                LatestHeadline = h.LatestHeadline
            })
            .OrderByDescending(h => h.HighRiskDays)
            .ThenByDescending(h => h.CorrelationDays)
            .ThenByDescending(h => h.MediumRiskDays)
            .ToList();
    }
}
