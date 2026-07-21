using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>問題查詢與風險日詳情（docs/WEB-SPEC.md §9.2、§9.3）</summary>
public interface IRecordQueryService
{
    PagedResult<RecordListItemDto> Search(RecordSearchRequest request);

    RecordDetailDto GetDetail(long hostId, DateTime date);

    /// <summary>報告全文；沒有報告或已被清理時回 null</summary>
    string? GetReport(long hostId, DateTime date);

    HostDetailDto GetHostDetail(long hostId, int days);
}

public class RecordQueryService : IRecordQueryService
{
    private readonly IRecordRepository _repository;
    private readonly IReportReader _reports;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IHostGroupStore _hostGroups;
    private readonly IVisibilityService _visibility;
    private readonly IRecordHandlingStore _handlings;

    public RecordQueryService(
        IRecordRepository repository,
        IReportReader reports,
        IHostStore hosts,
        IUserStore users,
        IHostGroupStore hostGroups,
        IVisibilityService visibility,
        IRecordHandlingStore handlings)
    {
        _repository = repository;
        _reports = reports;
        _hosts = hosts;
        _users = users;
        _hostGroups = hostGroups;
        _visibility = visibility;
        _handlings = handlings;
    }

    public PagedResult<RecordListItemDto> Search(RecordSearchRequest request)
    {
        var filter = BuildFilter(request);
        var records = _repository.Query(filter);

        // 處理狀態存在另一份資料（handling.json），一次撈起來在記憶體 join——
        // 逐筆查會變成 N 次讀取，而 JSONL 後端的每次讀取都是一次檔案解析
        var handlings = LoadHandlings(records);

        if (request.Statuses is { Count: > 0 })
        {
            var wanted = request.Statuses.ToHashSet(StringComparer.OrdinalIgnoreCase);
            records = records
                .Where(r => wanted.Contains(StatusOf(handlings, r)))
                .ToList();
        }

        if (request.Overdue == true)
        {
            records = records
                .Where(r =>
                {
                    var handling = FindHandling(handlings, r);
                    return handling?.DueDate.HasValue == true &&
                           handling.DueDate.Value.Date < DateTime.Today &&
                           HandlingStatuses.Unresolved.Contains(handling.Status);
                })
                .ToList();
        }

        // 緊急程度排序（§DB-PLAN E 節定案）：風險層級 → 有無關聯訊號 → 日期新到舊。
        // 全部可從既有欄位算出，不需要額外欄位
        var ordered = records
            .OrderByDescending(r => RiskRank(r.RiskLevel))
            .ThenByDescending(r => r.CorrelationAlerts.Count > 0)
            .ThenByDescending(r => r.Date)
            .ToList();

        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var page = Math.Max(request.Page, 1);

        var hostsByName = _hosts.GetAll()
            .ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);

        return new PagedResult<RecordListItemDto>
        {
            Items = ordered.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(r => ToListItem(r, hostsByName, FindHandling(handlings, r)))
                .ToList(),
            Page = page,
            PageSize = pageSize,
            Total = ordered.Count
        };
    }

    private List<RecordHandling> LoadHandlings(List<DailyAnalysisRecord> records)
    {
        if (records.Count == 0) return new List<RecordHandling>();

        var hostNames = records.Select(r => r.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var from = records.Min(r => r.Date);
        var to = records.Max(r => r.Date);

        return _handlings.GetMany(hostNames, from, to);
    }

    private static RecordHandling? FindHandling(List<RecordHandling> handlings, DailyAnalysisRecord record) =>
        handlings.FirstOrDefault(h =>
            string.Equals(h.HostName, record.Host, StringComparison.OrdinalIgnoreCase) &&
            h.Date.Date == record.Date.Date);

    /// <summary>從未處理過的風險日視為 open——待辦清單必須包含它們，否則新問題不會出現在待辦裡</summary>
    private static string StatusOf(List<RecordHandling> handlings, DailyAnalysisRecord record) =>
        FindHandling(handlings, record)?.Status ?? HandlingStatuses.Open;

    public RecordDetailDto GetDetail(long hostId, DateTime date)
    {
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

        var host = _hosts.Get(hostId);

        return new RecordDetailDto
        {
            HostId = hostId,
            HostName = host?.HostName ?? record.Host,
            HostRoleDesc = host?.RoleDesc ?? "",
            Date = record.Date.ToString("yyyy-MM-dd"),
            RiskLevel = record.RiskLevel,
            Headline = record.Headline,
            Summary = record.Summary,
            TrendAssessment = record.TrendAssessment,
            Action = record.Action,
            AiAnalyzed = record.AiAnalyzed,
            ErrorCount = record.ErrorCount,
            WarningCount = record.WarningCount,
            AuditEventCount = record.AuditEventCount,
            TopIssues = record.TopIssues.Select(ToIssueDto).ToList(),
            Categories = CategoryAggregator.Aggregate(record.TopIssues).Select(ToCategoryDto).ToList(),
            TrendAlerts = record.TrendAlerts,
            CorrelationAlerts = record.CorrelationAlerts,
            DeepDives = record.DeepDives.Select(d => new DeepDiveDto
            {
                Category = d.Category.ToString(),
                Findings = d.Findings.Select(f => new DeepDiveFindingDto
                {
                    Problem = f.Problem,
                    Impact = f.Impact,
                    LikelyCauses = f.LikelyCauses,
                    NextSteps = f.NextSteps
                }).ToList()
            }).ToList(),
            DataIncomplete = record.DataIncomplete,
            SecurityLogAvailable = record.SecurityLogAvailable,
            UncoveredChecks = record.UncoveredChecks,
            HasReport = !string.IsNullOrWhiteSpace(record.ReportFile),
            WeeklyCheckup = record.WeeklyCheckup == null ? null : new WeeklyCheckupDto
            {
                CheckupDate = record.WeeklyCheckup.CheckupDate.ToString("yyyy-MM-dd"),
                HasFindings = record.WeeklyCheckup.HasFindings,
                Conclusion = record.WeeklyCheckup.Conclusion
            }
        };
    }

    public string? GetReport(long hostId, DateTime date)
    {
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

        return string.IsNullOrWhiteSpace(record.ReportFile) ? null : _reports.Read(record.ReportFile);
    }

    public HostDetailDto GetHostDetail(long hostId, int days)
    {
        _visibility.EnsureVisible(hostId);

        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        var from = DateTime.Today.AddDays(-days + 1);
        var records = _repository.Query(new RecordQueryFilter
        {
            HostNames = new[] { host.HostName },
            From = from
        }).ToDictionary(r => r.Date.Date);

        // 逐日填格：**沒有紀錄的日子也要有格子**——那代表「這天沒分析」，
        // 與「這天分析過、沒風險」是完全不同的意義，畫面上必須分得出來
        var timeline = new List<TimelineDayDto>();
        for (var date = from; date <= DateTime.Today; date = date.AddDays(1))
        {
            records.TryGetValue(date.Date, out var record);
            timeline.Add(new TimelineDayDto
            {
                Date = date.ToString("yyyy-MM-dd"),
                HasRecord = record != null,
                RiskLevel = record?.RiskLevel,
                Headline = record?.Headline ?? "",
                HasCoverageGap = record != null && HasCoverageGap(record)
            });
        }

        var groups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var users = _users.GetAll().ToDictionary(u => u.UserId);

        var latestCheckup = records.Values
            .Where(r => r.WeeklyCheckup != null)
            .OrderByDescending(r => r.Date)
            .Select(r => r.WeeklyCheckup!)
            .FirstOrDefault();

        return new HostDetailDto
        {
            HostId = host.HostId,
            HostName = host.HostName,
            RoleDesc = host.RoleDesc,
            IpAddress = host.IpAddress,
            NetiqServer = host.NetiqServer,
            LastReportAt = host.LastReportAt,
            GroupNames = host.GroupIds.Select(id => groups.TryGetValue(id, out var g) ? g.GroupName : $"(已刪除:{id})").ToList(),
            OwnerNames = host.OwnerUserIds.Select(id => users.TryGetValue(id, out var u) ? u.DisplayName : $"(已刪除:{id})").ToList(),
            Timeline = timeline,
            LatestCheckup = latestCheckup == null ? null : new WeeklyCheckupDto
            {
                CheckupDate = latestCheckup.CheckupDate.ToString("yyyy-MM-dd"),
                HasFindings = latestCheckup.HasFindings,
                Conclusion = latestCheckup.Conclusion
            }
        };
    }

    private RecordQueryFilter BuildFilter(RecordSearchRequest request)
    {
        var filter = new RecordQueryFilter
        {
            From = request.From,
            To = request.To,
            RiskLevels = request.RiskLevels is { Count: > 0 } ? request.RiskLevels : null,
            EventId = request.EventId,
            Source = request.Source
        };

        if (request.HostIds is { Count: > 0 })
        {
            filter.HostNames = request.HostIds
                .Select(id => _repository.ResolveHostName(id))
                .Where(name => name != null)
                .Select(name => name!)
                .ToList();
        }

        if (request.Categories is { Count: > 0 })
        {
            var categories = new List<IssueCategory>();
            foreach (var name in request.Categories)
            {
                if (Enum.TryParse<IssueCategory>(name, ignoreCase: true, out var category))
                    categories.Add(category);
            }
            if (categories.Count > 0) filter.Categories = categories;
        }

        if (!string.IsNullOrWhiteSpace(request.Severity) &&
            Enum.TryParse<IssueSeverity>(request.Severity, ignoreCase: true, out var severity))
        {
            filter.MinSeverity = severity;
        }

        return filter;
    }

    private RecordListItemDto ToListItem(
        DailyAnalysisRecord record,
        IReadOnlyDictionary<string, WebHost> hostsByName,
        RecordHandling? handling)
    {
        hostsByName.TryGetValue(record.Host, out var host);

        var status = handling?.Status ?? HandlingStatuses.Open;

        return new RecordListItemDto
        {
            HostId = host?.HostId ?? 0,
            HostName = record.Host,
            Date = record.Date.ToString("yyyy-MM-dd"),
            RiskLevel = record.RiskLevel,
            Headline = record.Headline,
            ErrorCount = record.ErrorCount,
            WarningCount = record.WarningCount,
            Categories = CategoryAggregator.Aggregate(record.TopIssues)
                .Select(c => c.Category.ToString())
                .ToList(),
            HasCorrelation = record.CorrelationAlerts.Count > 0,
            HasCoverageGap = HasCoverageGap(record),
            AiAnalyzed = record.AiAnalyzed,
            HandlingStatus = status,
            HandlingStatusText = HandlingStatusText(status),
            HandlerName = handling?.HandlerId.HasValue == true
                ? _users.Get(handling.HandlerId.Value)?.DisplayName
                : null,
            IsOverdue = handling?.DueDate.HasValue == true &&
                        handling.DueDate.Value.Date < DateTime.Today &&
                        HandlingStatuses.Unresolved.Contains(status)
        };
    }

    private static string HandlingStatusText(string status) => status switch
    {
        HandlingStatuses.Open => "未處理",
        HandlingStatuses.InProgress => "處理中",
        HandlingStatuses.Resolved => "已處理",
        HandlingStatuses.WontFix => "不處理",
        HandlingStatuses.FalsePositive => "誤報",
        HandlingStatuses.KnownNoise => "已知雜訊",
        _ => status
    };

    /// <summary>
    /// 涵蓋率缺口：資料不完整，或 Security log 沒讀到。
    /// README 的「沒告警 ≠ 沒問題，是沒看」在 Web 上必須同樣顯眼，
    /// 否則使用者會把「沒看到」誤讀成「沒發生」。
    /// </summary>
    private static bool HasCoverageGap(DailyAnalysisRecord record) =>
        record.DataIncomplete || record.SecurityLogAvailable == false;

    private static IssueDto ToIssueDto(LogIssueSignature issue) => new()
    {
        LogName = issue.LogName,
        Source = issue.Source,
        EventId = issue.EventId,
        Count = issue.Count,
        Category = issue.Category.ToString(),
        Severity = issue.Severity.ToString(),
        KnownIssue = issue.KnownIssue,
        FirstSeen = issue.FirstSeen,
        LastSeen = issue.LastSeen,
        DistinctMessageCount = issue.DistinctMessageCount,
        KeyDetails = issue.KeyDetails,
        SampleMessages = issue.SampleMessages,
        Suppressed = issue.Suppressed,
        Trend = issue.Trend.ToString(),
        TrendText = BuildTrendText(issue)
    };

    /// <summary>
    /// 趨勢的白話描述在後端組好（含比對數字），前端不再自行拼裝——
    /// 同一份規則若兩邊各寫一次，遲早出現「清單說頻率上升、詳情說重複發生」。
    /// </summary>
    private static string BuildTrendText(LogIssueSignature issue)
    {
        var parts = new List<string>();

        switch (issue.Trend)
        {
            case IssueTrend.New:
                parts.Add("首次出現");
                break;
            case IssueTrend.Rising:
                parts.Add("頻率上升");
                break;
            case IssueTrend.Recurring:
                parts.Add("重複發生");
                break;
            case IssueTrend.Declining:
                parts.Add("頻率下降");
                break;
        }

        if (issue.PreviousDayCount.HasValue) parts.Add($"前一日 {issue.PreviousDayCount} 次");
        if (issue.HistoryDailyAverage.HasValue) parts.Add($"歷史平均 {issue.HistoryDailyAverage.Value:0.#} 次");
        if (issue.DaysSeenInHistory > 0) parts.Add($"近期出現 {issue.DaysSeenInHistory} 天");

        return string.Join("、", parts);
    }

    private static CategorySummaryDto ToCategoryDto(CategorySummary summary) => new()
    {
        Category = summary.Category.ToString(),
        IssueCount = summary.IssueCount,
        TotalEvents = summary.TotalEvents,
        MaxSeverity = summary.MaxSeverity.ToString(),
        CriticalCount = summary.CriticalCount,
        HighCount = summary.HighCount,
        MediumCount = summary.MediumCount,
        LowCount = summary.LowCount
    };

    private static int RiskRank(string riskLevel) => riskLevel switch
    {
        "高" => 3,
        "中" => 2,
        "低" => 1,
        _ => 0
    };
}

public class RecordSearchRequest
{
    public List<long>? HostIds { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public List<string>? RiskLevels { get; set; }
    public List<string>? Categories { get; set; }
    public string? Severity { get; set; }
    public int? EventId { get; set; }
    public string? Source { get; set; }

    /// <summary>處理狀態篩選（待辦清單的下鑽目標）</summary>
    public List<string>? Statuses { get; set; }

    /// <summary>只看逾期未處理</summary>
    public bool? Overdue { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
