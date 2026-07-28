using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 儀表板與報表共用的統計組裝（docs/SHARED-STANDARDS-PLAN.md S4/S5）。取代原本
/// DashboardService／ReportService 各自維護的一份幾乎逐行相同的類別統計與主機排行邏輯——
/// 兩處遲早會在某次修改中漂移成不同的數字，尤其是嚴重度可見性（S1）接上之後兩邊都要同步改。
///
/// 呼叫端的 <paramref name="records"/> 必須已經過 <see cref="Repositories.IRecordRepository"/>
/// 的可見範圍與嚴重度可見性過濾——本類別不做任何過濾判斷，只負責聚合與排序。
/// </summary>
public static class RecordStatsBuilder
{
    /// <summary>
    /// 依風險類別彙總。逐日彙總後合併：CategoryAggregator 是兩個儲存後端共用的同一份規則
    /// （§10.3），儀表板與明細頁因此不可能算出不同的數字。
    /// </summary>
    public static List<DashboardCategoryDto> BuildCategoryCards(List<DailyAnalysisRecord> records)
    {
        var perDay = records.SelectMany(r => CategoryAggregator.Aggregate(r.TopIssues));
        var merged = CategoryAggregator.Merge(perDay);

        var hostsPerCategory = records
            .SelectMany(r => r.TopIssues.Select(i => new { i.Category, r.Host }))
            .GroupBy(x => x.Category)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        return merged.Select(c => new DashboardCategoryDto
        {
            Category = c.Category.ToString(),
            IssueCount = c.IssueCount,
            TotalEvents = c.TotalEvents,
            MaxSeverity = c.MaxSeverity.ToString(),
            CriticalCount = c.CriticalCount,
            HighCount = c.HighCount,
            MediumCount = c.MediumCount,
            LowCount = c.LowCount,
            ElevatesCount = c.ElevatesCount,
            AffectedHosts = hostsPerCategory.TryGetValue(c.Category, out var count) ? count : 0
        }).ToList();
    }

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
}
