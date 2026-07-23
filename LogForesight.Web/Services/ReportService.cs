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

    public ReportService(IRecordRepository repository, IHostStore hosts)
    {
        _repository = repository;
        _hosts = hosts;
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

        var ranked = BuildHostRanking(records);

        var dto = new ReportSummaryDto
        {
            From = from.ToString("yyyy-MM-dd"),
            To = to.ToString("yyyy-MM-dd"),
            Kpi = BuildKpi(records, previousRecords),
            Trend = BuildTrend(records, from, to),
            Categories = BuildCategories(records),
            HostRanking = ranked.Take(HostRankingLimit).ToList(),
            RankedHostCount = ranked.Count,
            Others = BuildOthers(ranked)
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
            HighRiskDays = records.Count(r => r.RiskLevel == "高"),
            HighRiskDaysPrevious = previous.Count(r => r.RiskLevel == "高"),
            AffectedHosts = records
                .Where(r => r.RiskLevel is "高" or "中")
                .Select(r => r.Host)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            AffectedHostsPrevious = previous
                .Where(r => r.RiskLevel is "高" or "中")
                .Select(r => r.Host)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            CoverageGapDays = records.Count(r => r.DataIncomplete || r.SecurityLogAvailable == false)
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
                HighRisk = dayRecords?.Count(r => r.RiskLevel == "高") ?? 0,
                MediumRisk = dayRecords?.Count(r => r.RiskLevel == "中") ?? 0,
                ErrorCount = dayRecords?.Sum(r => r.ErrorCount) ?? 0
            });
        }

        return points;
    }

    private static List<DashboardCategoryDto> BuildCategories(List<DailyAnalysisRecord> records)
    {
        var merged = CategoryAggregator.Merge(
            records.SelectMany(r => CategoryAggregator.Aggregate(r.TopIssues)));

        var hostsPerCategory = records
            .SelectMany(r => r.TopIssues.Select(i => new { i.Category, r.Host }))
            .GroupBy(x => x.Category)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count());

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
            AffectedHosts = hostsPerCategory.TryGetValue(c.Category, out var count) ? count : 0
        }).ToList();
    }

    private List<DashboardHostDto> BuildHostRanking(List<DailyAnalysisRecord> records)
    {
        var hostsByName = _hosts.GetAll().ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);

        return records
            .GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DashboardHostDto
            {
                HostId = hostsByName.TryGetValue(group.Key, out var host) ? host.HostId : 0,
                HostName = group.Key,
                HighRiskDays = group.Count(r => r.RiskLevel == "高"),
                MediumRiskDays = group.Count(r => r.RiskLevel == "中"),
                CorrelationDays = group.Count(r => r.CorrelationAlerts.Count > 0),
                LatestRiskLevel = group.OrderByDescending(r => r.Date).First().RiskLevel,
                LatestHeadline = group.OrderByDescending(r => r.Date).First().Headline
            })
            .Where(h => h.HighRiskDays > 0 || h.MediumRiskDays > 0)
            .OrderByDescending(h => h.HighRiskDays)
            .ThenByDescending(h => h.CorrelationDays)
            .ThenByDescending(h => h.MediumRiskDays)
            .ToList();   // 全量回傳，Top N 與「其他」彙總在 GetSummary 切分
    }
}
