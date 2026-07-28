using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>報表（docs/WEB-SPEC.md §9.6）——主管的主要畫面</summary>
public interface IReportService
{
    ReportSummaryDto GetSummary(DateTime from, DateTime to);

    /// <summary>跨主機同簽章查詢：這個 Event ID 在哪些主機、哪些日子出現過</summary>
    List<SignatureHitDto> FindSignature(int eventId, string? source);
}

public class ReportService : IReportService
{
    private readonly IRecordRepository _repository;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;
    private readonly IHandlingService _handling;

    public ReportService(
        IRecordRepository repository, IHostStore hosts, IVisibilityService visibility, IHandlingService handling)
    {
        _repository = repository;
        _hosts = hosts;
        _visibility = visibility;
        _handling = handling;
    }

    public ReportSummaryDto GetSummary(DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);

        var records = _repository.Query(new RecordQueryFilter { From = from, To = to });

        // 與前一個「等長」期間比較：主管要的不是數字本身，是「變好還是變壞」。
        // 等長才可比——拿一週跟一個月比毫無意義
        var span = (to.Date - from.Date).Days + 1;
        var previousTo = from.Date.AddDays(-1);
        var previousFrom = previousTo.AddDays(-span + 1);
        var previousRecords = _repository.Query(new RecordQueryFilter { From = previousFrom, To = previousTo });

        var hostsByName = _hosts.GetAll().ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);
        var ranked = RecordStatsBuilder.BuildHostRanking(records, hostsByName);

        var dto = new ReportSummaryDto
        {
            From = from.ToString("yyyy-MM-dd"),
            To = to.ToString("yyyy-MM-dd"),
            Kpi = BuildKpi(records, previousRecords),
            Trend = BuildTrend(records, from, to),
            Categories = RecordStatsBuilder.BuildCategoryCards(records),
            HostRanking = ranked.Take(HostRankingLimit).ToList(),
            RankedHostCount = ranked.Count,
            Others = BuildOthers(ranked),
            // #6 管理者指標：與儀表板同一來源（IVisibilityService／HandlingService.GetTodo），
            // 兩頁的「主機總數」「處理進度」數字才不會各算各的
            TotalHosts = _visibility.GetVisibleHosts().Count,
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

    public List<SignatureHitDto> FindSignature(int eventId, string? source)
    {
        var records = _repository.Query(new RecordQueryFilter
        {
            EventId = eventId,
            Source = string.IsNullOrWhiteSpace(source) ? null : source
        });

        var hostsByName = _hosts.GetAll().ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);

        return records
            .SelectMany(record => record.TopIssues
                .Where(i => i.EventId == eventId &&
                            (string.IsNullOrWhiteSpace(source) ||
                             string.Equals(i.Source, source, StringComparison.OrdinalIgnoreCase)))
                .Select(issue => new SignatureHitDto
                {
                    HostId = hostsByName.TryGetValue(record.Host, out var host) ? host.HostId : 0,
                    HostName = record.Host,
                    Date = record.Date.ToString("yyyy-MM-dd"),
                    Count = issue.Count,
                    Severity = issue.Severity.ToString(),
                    Category = issue.Category.ToString(),
                    KnownIssue = issue.KnownIssue
                }))
            .OrderByDescending(h => h.Date)
            .ThenBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
