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
    /// 依風險類別彙總。逐日彙總後合併：CategoryAggregator 是兩個儲存後端共用的同一份規則
    /// （§10.3），儀表板與明細頁因此不可能算出不同的數字。
    ///
    /// <paramref name="visibleSeverities"/>（回饋十三輪新增項3）：卡片彙總一律排除未勾選的問題嚴重度，
    /// 不論 <see cref="SystemSettings.SeverityDisplayMode"/> 是 DefaultHidden 還是 SiteHidden。
    /// 這與風險日詳情頁刻意不同——詳情頁在 DefaultHidden 模式下送出完整資料，靠前端「顯示已隱藏
    /// 層級」按鈕讓使用者自行點開；但儀表板／報表的類別卡沒有這個手動展開的 UI，繼續套用
    /// 「DefaultHidden＝不過濾」的話，被使用者在設定頁取消勾選的嚴重度（預設即不含 Low）
    /// 會在這裡原封不動地繼續出現——尤其「其他」類別本來就是低嚴重度雜訊的大宗，
    /// 最容易讓人以為「明明關掉了低風險顯示，其他類別怎麼還在顯示」。
    /// </summary>
    public static List<DashboardCategoryDto> BuildCategoryCards(
        List<DailyAnalysisRecord> records, HashSet<IssueSeverity> visibleSeverities)
    {
        var perDay = records.SelectMany(r => CategoryAggregator.Aggregate(VisibleIssues(r, visibleSeverities)));
        var merged = CategoryAggregator.Merge(perDay);

        var hostsPerCategory = records
            .SelectMany(r => VisibleIssues(r, visibleSeverities).Select(i => new { i.Category, r.Host }))
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

    private static IEnumerable<LogIssueSignature> VisibleIssues(DailyAnalysisRecord r, HashSet<IssueSeverity> visibleSeverities) =>
        r.TopIssues.Where(i => visibleSeverities.Contains(i.Severity));

    /// <summary>
    /// 問題排行（docs/archive/FEEDBACK-11-PLAN.md §8）：儀表板「重點問題」卡與報表「問題排行」共用。
    ///
    /// 分組鍵沿用 <see cref="RecordQueryHelpers.GroupIssuesBySignature"/>（Source＋EventId），
    /// 與問題查詢「依問題」視角是同一把鍵——下鑽過去看到的必須是同一個問題，不能這裡分一種、
    /// 那裡分另一種。排序＝最高嚴重度 → 主機數 → 總次數，同依問題視角的預設排序。
    ///
    /// 與 <see cref="BuildHostRanking"/> 同樣**回傳完整排序清單**，Top N 的切法留給呼叫端
    /// （儀表板取 5、報表取 10＋「其他」彙總）。
    /// </summary>
    public static List<IssueRankingDto> BuildIssueRanking(List<DailyAnalysisRecord> records) =>
        RecordQueryHelpers.GroupIssuesBySignature(records)
            .Select(g => new IssueRankingDto
            {
                Source = g.Key.Source,
                EventId = g.Key.EventId,
                Category = g.First().Issue.Category.ToString(),
                MaxSeverity = g.Max(x => x.Issue.Severity).ToString(),
                HostCount = g.Select(x => x.Record.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                DayCount = g.Select(x => (x.Record.Host, x.Record.Date)).Distinct().Count(),
                TotalCount = g.Sum(x => x.Issue.Count)
            })
            .OrderByDescending(i => Enum.TryParse<IssueSeverity>(i.MaxSeverity, out var s) ? (int)s : -1)
            .ThenByDescending(i => i.HostCount)
            .ThenByDescending(i => i.TotalCount)
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
}
