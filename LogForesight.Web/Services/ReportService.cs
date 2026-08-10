using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>報表（docs/WEB-SPEC.md §9.6）——主管的主要畫面</summary>
public class ReportService
{
    private readonly IRecordRepository _repository;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;
    private readonly HandlingHistoryQueryService _handling;
    private readonly IssueRankingBuilder _issueRanking;
    private readonly ISystemSettingsStore _settings;

    public ReportService(
        IRecordRepository repository, IHostStore hosts, IVisibilityService visibility,
        HandlingHistoryQueryService handling, IssueRankingBuilder issueRanking, ISystemSettingsStore settings)
    {
        _repository = repository;
        _hosts = hosts;
        _visibility = visibility;
        _handling = handling;
        _issueRanking = issueRanking;
        _settings = settings;
    }

    public ReportSummaryDto GetSummary(DateTime from, DateTime to, string? handlingScope = null)
    {
        if (to < from) (from, to) = (to, from);

        var scope = HandlingHistoryQueryService.HandlingScopes.Normalize(handlingScope);
        var records = _handling.FilterByScope(_repository.Query(new RecordQueryFilter { From = from, To = to }), scope);

        // 與前一個「等長」期間比較：主管要的不是數字本身，是「變好還是變壞」。
        // 等長才可比——拿一週跟一個月比毫無意義。前期套用同一 scope，比較才有意義（§5）
        var span = (to.Date - from.Date).Days + 1;
        var previousTo = from.Date.AddDays(-1);
        var previousFrom = previousTo.AddDays(-span + 1);
        var previousRecords = _handling.FilterByScope(
            _repository.Query(new RecordQueryFilter { From = previousFrom, To = previousTo }), scope);

        var hostsByName = _hosts.GetAll().ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);
        var ranked = RecordStatsBuilder.BuildHostRanking(records, hostsByName);

        // 問題排行走 SQL 端聚合（P4）：與儀表板同一個 IssueRankingBuilder，兩頁數字必然一致。
        // **不受 handlingScope 篩選影響**——scope 是日層級的過濾（先過濾再聚合），
        // 而問題聚合是跨主機跨日的獨立投影；把 scope 套上去會讓「這個問題影響幾台」
        // 變成「符合這個處理狀態的日子裡影響幾台」，那是另一個問題的答案
        var visibleHosts = _visibility.GetVisibleHosts();
        var issueRanked = _issueRanking.Build(
            from, to, visibleHosts.Select(h => h.HostId).ToList(), visibleHosts.Count);

        var dto = new ReportSummaryDto
        {
            From = from.ToString("yyyy-MM-dd"),
            To = to.ToString("yyyy-MM-dd"),
            HandlingScope = scope,
            Kpi = BuildKpi(records, previousRecords),
            Trend = BuildTrend(records, from, to),
            Categories = RecordStatsBuilder.BuildCategoryCards(records, _settings.Get().ParseUnhandledSeverities()),
            HostRanking = ranked.Take(HostRankingLimit).ToList(),
            RankedHostCount = ranked.Count,
            Others = BuildOthers(ranked),
            // 問題排行（§8-2）：與主機排行同一張卡的另一個模式，同一份聚合投影（儀表板也用它），
            // 兩頁的數字因此必然一致。上限與主機排行同 10 筆＋「其他」彙總
            IssueRanking = issueRanked.Take(HostRankingLimit).ToList(),
            RankedIssueCount = issueRanked.Count,
            IssueOthers = BuildIssueOthers(issueRanked),
            // #6 管理者指標：與儀表板同一來源（IVisibilityService／HandlingHistoryQueryService.GetTodo），
            // 兩頁的「主機總數」「處理進度」數字才不會各算各的
            TotalHosts = visibleHosts.Count,
            // 背景整理中時問題排行的數字會偏低但看起來正常——必須說出來（G2）
            IssueStatsPending = _issueRanking.StatsPending().Pending,
            IssueStatsPendingHint = _issueRanking.StatsPending().Hint,
            Handling = _handling.GetTodo(records)
        };

        return dto;
    }

    /// <summary>排行榜顯示上限——超過的併入「其他 N 台」彙總條，保住圖表可讀性又不讓尾端主機隱形</summary>
    private const int HostRankingLimit = 10;

    private static HostRankingOthersDto? BuildOthers(List<DashboardHostDto> ranked)
    {
        var others = ranked.Skip(HostRankingLimit).ToList();
        if (others.Count == 0) return null;

        return new HostRankingOthersDto
        {
            HostCount = others.Count,
            HighRiskDays = others.Sum(h => h.HighRiskDays),
            MediumRiskDays = others.Sum(h => h.MediumRiskDays)
        };
    }

    /// <summary>問題排行的「其他 N 個問題」彙總（§8-2）：尾端問題不隱形，同主機排行的作法</summary>
    private static IssueRankingOthersDto? BuildIssueOthers(List<IssueRankingDto> ranked)
    {
        var others = ranked.Skip(HostRankingLimit).ToList();
        if (others.Count == 0) return null;

        return new IssueRankingOthersDto
        {
            IssueCount = others.Count,
            TotalCount = others.Sum(i => i.TotalCount),
            HostCount = others.Sum(i => i.HostCount)
        };
    }

    private static ReportKpiDto BuildKpi(
        List<DailyAnalysisRecord> records, List<DailyAnalysisRecord> previous)
    {
        return new ReportKpiDto
        {
            TotalIssues = records.Sum(r => r.TopIssues.Count),
            TotalIssuesPrevious = previous.Sum(r => r.TopIssues.Count),
            HighRiskDays = records.Count(r => r.RiskLevel == RiskLevels.High),
            HighRiskDaysPrevious = previous.Count(r => r.RiskLevel == RiskLevels.High),
            AffectedHosts = records
                .Where(r => RiskLevels.IsActionable(r.RiskLevel))
                .Select(r => r.Host)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            AffectedHostsPrevious = previous
                .Where(r => RiskLevels.IsActionable(r.RiskLevel))
                .Select(r => r.Host)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            CoverageGapDays = records.Count(r => r.HasCoverageGap)
        };
    }

    /// <summary>逐日趨勢。**沒有紀錄的日子補 0 而不是略過**，否則折線圖會把空白日連成一條斜線，看起來像平滑變化</summary>
    private static List<ReportTrendPointDto> BuildTrend(
        List<DailyAnalysisRecord> records, DateTime from, DateTime to)
    {
        var byDate = records.GroupBy(r => r.Date.Date).ToDictionary(g => g.Key, g => g.ToList());

        var points = new List<ReportTrendPointDto>();
        for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
        {
            byDate.TryGetValue(date, out var dayRecords);
            points.Add(new ReportTrendPointDto
            {
                Date = date.ToString("yyyy-MM-dd"),
                HighRisk = dayRecords?.Count(r => r.RiskLevel == RiskLevels.High) ?? 0,
                MediumRisk = dayRecords?.Count(r => r.RiskLevel == RiskLevels.Medium) ?? 0,
                ErrorCount = dayRecords?.Sum(r => r.ErrorCount) ?? 0
            });
        }

        return points;
    }

}
