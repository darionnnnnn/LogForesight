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
    private readonly IIssueAggregateQuery _aggregates;
    private readonly ISystemSettingsService _systemSettings;

    public ReportService(
        IRecordRepository repository, IHostStore hosts, IVisibilityService visibility,
        HandlingHistoryQueryService handling, IssueRankingBuilder issueRanking, ISystemSettingsStore settings,
        IIssueAggregateQuery aggregates, ISystemSettingsService systemSettings)
    {
        _repository = repository;
        _hosts = hosts;
        _visibility = visibility;
        _handling = handling;
        _issueRanking = issueRanking;
        _settings = settings;
        _aggregates = aggregates;
        _systemSettings = systemSettings;
    }

    public ReportSummaryDto GetSummary(DateTime from, DateTime to, string? handlingScope = null, string? compare = null)
    {
        if (to < from) (from, to) = (to, from);

        var (previousFrom, previousTo, actualComparisonMode) = CalculateComparisonPeriod(from, to, compare);
        var retentionThreshold = DateTime.Today.AddDays(-_settings.Get().RetentionDays);
        bool outOfRetention = previousTo < retentionThreshold;

        var scope = HandlingHistoryQueryService.HandlingScopes.Normalize(handlingScope);

        List<DailyAnalysisRecord>? recordsForTodo = null;
        var visibleHosts = _visibility.GetVisibleHosts();
        var hostIds = visibleHosts.Select(h => h.HostId).ToList();

        ReportKpiDto kpi;
        List<ReportTrendPointDto> trend;
        List<DashboardHostDto> ranked;
        HandlingTodoDto? handlingDto = null;

        var visibleDayRiskLevels = _systemSettings.GetVisibleDayRiskLevels();
        var riskLevels = RecordRepository.ResolveDayRiskLevels(visibleDayRiskLevels, null);
        var visibleSeverities = RecordRepository.ParseVisibleSeverities(_systemSettings.GetVisibleSeverities());
        var nothingVisible = riskLevels != null && riskLevels.Count == 0;

        if (scope == HandlingHistoryQueryService.HandlingScopes.All)
        {
            if (nothingVisible)
            {
                kpi = new ReportKpiDto();
                trend = BuildTrendFromAggregate(new List<TrendAggregate>(), from, to);
                ranked = new List<DashboardHostDto>();
                handlingDto = new HandlingTodoDto();
            }
            else
            {
                var kpiAgg = _aggregates.AggregateReportKpi(from, to, hostIds, riskLevels, visibleSeverities);

                var kpiAggPrev = _aggregates.AggregateReportKpi(previousFrom, previousTo, hostIds, riskLevels, visibleSeverities);

                kpi = new ReportKpiDto
                {
                    TotalIssues = kpiAgg.TotalIssues,
                    TotalIssuesPrevious = kpiAggPrev.TotalIssues,
                    HighRiskDays = kpiAgg.HighRiskDays,
                    HighRiskDaysPrevious = kpiAggPrev.HighRiskDays,
                    AffectedHosts = kpiAgg.AffectedHosts,
                    AffectedHostsPrevious = kpiAggPrev.AffectedHosts,
                    CoverageGapDays = kpiAgg.CoverageGapDays
                };

                var trendAgg = _aggregates.AggregateReportTrend(from, to, hostIds, riskLevels, visibleSeverities);
                trend = BuildTrendFromAggregate(trendAgg, from, to);

                // 排序規則與 BuildHostRanking 一致：高風險日 → 關聯日 → 中風險日。
                var hostRiskAgg = _aggregates.AggregateByHost(from, to, hostIds, riskLevels: riskLevels, visibleSeverities: visibleSeverities);
                // 用手上這份 visibleHosts 建索引，不再多打一次整份主機表（回饋二十七輪作業 F）：
                // 聚合本身就是以 hostIds（＝可見主機）為範圍查的，索引不可能需要範圍外的主機
                var hostsById = visibleHosts.ToDictionary(h => h.HostId);
                ranked = RecordStatsBuilder.BuildHostRanking(hostRiskAgg, hostsById);
                handlingDto = _handling.GetTodoByRange(from, to, riskLevels);
            }
            // 待辦走 GetTodoByRange（見下方 Handling 欄位），這條分支從頭到尾不載入任何紀錄
        }
        else
        {
            var records = _handling.FilterByScope(_repository.QueryLightweight(new RecordQueryFilter { From = from, To = to }), scope);
            recordsForTodo = records;

            var previousRecords = _handling.FilterByScope(
                _repository.QueryLightweight(new RecordQueryFilter { From = previousFrom, To = previousTo }), scope);

            // 同上：QueryLightweight 已套可見範圍（RecordRepository.ApplyVisibility），
            // 撈回來的紀錄其主機必然在 visibleHosts 裡，不必再載一次整份主機表
            var hostsByName = visibleHosts.ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);
            ranked = RecordStatsBuilder.BuildHostRanking(records, hostsByName);
            kpi = BuildKpi(records, previousRecords);
            trend = BuildTrend(records, from, to);
        }

        var allIssueRanked = _issueRanking.Build(
            from, to, hostIds, visibleHosts.Count, visibleHosts);
        // §10.6：全部主機都已有結論的問題不佔用排行版面（與儀表板重點問題卡同一套規則）
        var (issueRanked, concludedIssueCount) = IssueRankingBuilder.ExcludeConcluded(allIssueRanked);

        var statsPending = _issueRanking.StatsPending();

        var dto = new ReportSummaryDto
        {
            From = from.ToString("yyyy-MM-dd"),
            To = to.ToString("yyyy-MM-dd"),
            HandlingScope = scope,
            ComparisonMode = actualComparisonMode,
            ComparisonOutOfRetention = outOfRetention,
            Kpi = kpi,
            Trend = trend,
            // 風險類型分布走 SQL 端聚合（回饋十九輪批次D、二十輪批次B），與儀表板同一個查詢方法。
            // 刻意**不套用 handlingScope**——與下方問題排行同一個理由（見該處註解與前端
            // category-subtitle 的說明文字）：這裡是跨主機跨日的獨立投影，母體與「顯示範圍」
            // 篩的日層級處理狀態不是同一件事
            Categories = RecordStatsBuilder.BuildCategoryCards(nothingVisible
                ? new List<CategoryAggregate>()
                : _aggregates.AggregateByCategory(from, to, hostIds, visibleSeverities, riskLevels)),
            HostRanking = ranked.Take(HostRankingLimit).ToList(),
            RankedHostCount = ranked.Count,
            Others = BuildOthers(ranked),
            // 問題排行（§8-2）：與主機排行同一張卡的另一個模式，同一份聚合投影（儀表板也用它），
            // 兩頁的數字因此必然一致。上限與主機排行同 10 筆＋「其他」彙總
            IssueRanking = issueRanked.Take(HostRankingLimit).ToList(),
            RankedIssueCount = issueRanked.Count,
            IssueOthers = BuildIssueOthers(issueRanked),
            ConcludedIssueCount = concludedIssueCount,
            // #6 管理者指標：與儀表板同一來源（IVisibilityService／HandlingHistoryQueryService.GetTodo），
            // 兩頁的「主機總數」「處理進度」數字才不會各算各的
            TotalHosts = visibleHosts.Count,
            // 背景整理中時問題排行的數字會偏低但看起來正常——必須說出來（G2）
            IssueStatsPending = statsPending.Pending,
            IssueStatsPendingHint = statsPending.Hint,
            Handling = scope == HandlingHistoryQueryService.HandlingScopes.All
                ? handlingDto ?? new HandlingTodoDto()
                : _handling.GetTodo(recordsForTodo!)
        };

        return dto;
    }

    private (DateTime previousFrom, DateTime previousTo, string comparisonMode) CalculateComparisonPeriod(DateTime from, DateTime to, string? compare)
    {
        if (string.Equals(compare, "yoy", StringComparison.OrdinalIgnoreCase))
        {
            return (from.AddYears(-1), to.AddYears(-1), "yoy");
        }

        var span = (to.Date - from.Date).Days + 1;
        var previousTo = from.Date.AddDays(-1);
        var previousFrom = previousTo.AddDays(-span + 1);
        return (previousFrom, previousTo, "previous");
    }


    private static List<ReportTrendPointDto> BuildTrendFromAggregate(List<TrendAggregate> aggregates, DateTime from, DateTime to)
    {
        var byDate = aggregates.ToDictionary(a => a.Date.Date);
        var points = new List<ReportTrendPointDto>();
        for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
        {
            byDate.TryGetValue(date, out var agg);
            points.Add(new ReportTrendPointDto
            {
                Date = date.ToString("yyyy-MM-dd"),
                HighRisk = agg?.HighRisk ?? 0,
                MediumRisk = agg?.MediumRisk ?? 0,
                ErrorCount = agg?.ErrorCount ?? 0
            });
        }
        return points;
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
