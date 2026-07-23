using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>問題查詢與風險日詳情（docs/WEB-SPEC.md §9.2、§9.3）</summary>
public interface IRecordQueryService
{
    PagedResult<RecordListItemDto> Search(RecordSearchRequest request);

    /// <summary>依主機彙總（日期合併）——同一組篩選，換一個角度看</summary>
    PagedResult<RecordHostGroupDto> SearchByHost(RecordSearchRequest request);

    /// <summary>依日期彙總（主機合併）</summary>
    PagedResult<RecordDateGroupDto> SearchByDate(RecordSearchRequest request);

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

        // 紀錄 → 存活主機的索引：合併過的主機，舊識別下的紀錄要歸到存活主機，
        // 處理狀態（以現行主機名稱為鍵）與清單的連結才對得上
        var lookup = new HostLookup(_hosts.GetAll());

        // 處理狀態存在另一份資料（handling.json），一次撈起來在記憶體 join——
        // 逐筆查會變成 N 次讀取，而 JSONL 後端的每次讀取都是一次檔案解析
        var handlings = LoadHandlings(records, lookup);

        if (request.Statuses is { Count: > 0 })
        {
            var wanted = request.Statuses.ToHashSet(StringComparer.OrdinalIgnoreCase);
            records = records
                .Where(r => wanted.Contains(StatusOf(handlings, lookup, r)))
                .ToList();
        }

        if (request.Overdue == true)
        {
            records = records
                .Where(r =>
                {
                    var handling = FindHandling(handlings, lookup, r);
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

        return new PagedResult<RecordListItemDto>
        {
            Items = ordered.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(r => ToListItem(r, lookup, FindHandling(handlings, lookup, r)))
                .ToList(),
            Page = page,
            PageSize = pageSize,
            Total = ordered.Count
        };
    }

    public PagedResult<RecordHostGroupDto> SearchByHost(RecordSearchRequest request)
    {
        var records = _repository.Query(BuildFilter(request));
        var lookup = new HostLookup(_hosts.GetAll());

        var groups = records
            .Select(r => new { Record = r, Host = lookup.For(r) })
            .GroupBy(x => x.Host?.HostName ?? x.Record.Host, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.Record.Date).First();
                return new RecordHostGroupDto
                {
                    HostId = latest.Host?.HostId ?? 0,
                    HostName = latest.Host?.HostName ?? latest.Record.Host,
                    HighRiskDays = g.Count(x => x.Record.RiskLevel == "高"),
                    MediumRiskDays = g.Count(x => x.Record.RiskLevel == "中"),
                    LowRiskDays = g.Count(x => x.Record.RiskLevel == "低"),
                    CorrelationDays = g.Count(x => x.Record.CorrelationAlerts.Count > 0),
                    Categories = CategoryAggregator.Aggregate(g.SelectMany(x => x.Record.TopIssues))
                        .Select(c => c.Category.ToString()).ToList(),
                    LatestDate = latest.Record.Date.ToString("yyyy-MM-dd"),
                    LatestRiskLevel = latest.Record.RiskLevel,
                    LatestHeadline = latest.Record.Headline
                };
            })
            // 緊急程度：高風險日 → 關聯訊號日 → 中風險日（與明細排序、儀表板排行同一套）
            .OrderByDescending(h => h.HighRiskDays)
            .ThenByDescending(h => h.CorrelationDays)
            .ThenByDescending(h => h.MediumRiskDays)
            .ToList();

        return Paginate(groups, request);
    }

    public PagedResult<RecordDateGroupDto> SearchByDate(RecordSearchRequest request)
    {
        var records = _repository.Query(BuildFilter(request));
        var lookup = new HostLookup(_hosts.GetAll());

        var groups = records
            .Select(r => new { Record = r, HostName = lookup.For(r)?.HostName ?? r.Host })
            .GroupBy(x => x.Record.Date.Date)
            .Select(g => new RecordDateGroupDto
            {
                Date = g.Key.ToString("yyyy-MM-dd"),
                HighRiskHosts = g.Count(x => x.Record.RiskLevel == "高"),
                MediumRiskHosts = g.Count(x => x.Record.RiskLevel == "中"),
                LowRiskHosts = g.Count(x => x.Record.RiskLevel == "低"),
                CorrelationHosts = g.Count(x => x.Record.CorrelationAlerts.Count > 0),
                Categories = CategoryAggregator.Aggregate(g.SelectMany(x => x.Record.TopIssues))
                    .Select(c => c.Category.ToString()).ToList(),
                HostCount = g.Select(x => x.HostName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            })
            .OrderByDescending(d => d.Date)
            .ToList();

        return Paginate(groups, request);
    }

    /// <summary>彙總視角的分頁：先群組再分頁（分頁在記憶體，資料量與明細視角同級）</summary>
    private static PagedResult<T> Paginate<T>(List<T> items, RecordSearchRequest request)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var page = Math.Max(request.Page, 1);

        return new PagedResult<T>
        {
            Items = items.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = items.Count
        };
    }

    private List<RecordHandling> LoadHandlings(List<DailyAnalysisRecord> records, HostLookup lookup)
    {
        if (records.Count == 0) return new List<RecordHandling>();

        var hostNames = records.Select(r => HostNameOf(lookup, r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var from = records.Min(r => r.Date);
        var to = records.Max(r => r.Date);

        return _handlings.GetMany(hostNames, from, to);
    }

    /// <summary>
    /// 紀錄對應的**現行**主機名稱——處理狀態以現行名稱為鍵（HandlingService 一律由 hostId 解析），
    /// 所以合併前寫在舊識別下的紀錄要用存活主機的名稱去找，否則它們的處理狀態會全部看起來像未處理。
    /// 主機列查無對應時退回紀錄自帶的名稱快照。
    /// </summary>
    private static string HostNameOf(HostLookup lookup, DailyAnalysisRecord record) =>
        lookup.For(record)?.HostName ?? record.Host;

    private static RecordHandling? FindHandling(
        List<RecordHandling> handlings, HostLookup lookup, DailyAnalysisRecord record) =>
        handlings.FirstOrDefault(h =>
            string.Equals(h.HostName, HostNameOf(lookup, record), StringComparison.OrdinalIgnoreCase) &&
            h.Date.Date == record.Date.Date);

    /// <summary>從未處理過的風險日視為 open——待辦清單必須包含它們，否則新問題不會出現在待辦裡</summary>
    private static string StatusOf(List<RecordHandling> handlings, HostLookup lookup, DailyAnalysisRecord record) =>
        FindHandling(handlings, lookup, record)?.Status ?? HandlingStatuses.Open;

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
        // 別名展開：這台主機若併入過其他主機，時間軸要涵蓋合併前的那段歷史，
        // 否則「風險時間軸」會在合併日之前整片空白，看起來像沒有分析過
        var records = _repository.Query(new RecordQueryFilter
        {
            Hosts = _repository.ResolveHostKeys(hostId),
            From = from
        })
            // 一天可能有兩筆：合併當天存活主機與墓碑各分析過一次。時間軸一天一格，
            // 取存活主機那筆（沒有才退而取其一）——直接 ToDictionary 會因重複鍵整頁爆掉
            .GroupBy(r => r.Date.Date)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault(r => r.HostId == hostId) ?? g.First());

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
            // 每台主機展開成「本身＋已併入它的墓碑列」，篩選某台主機時才看得到它合併前的歷史
            filter.Hosts = request.HostIds
                .SelectMany(id => _repository.ResolveHostKeys(id))
                .DistinctBy(k => k.HostId)
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
        HostLookup lookup,
        RecordHandling? handling)
    {
        // 顯示與連結一律指向**存活**主機：合併之後，舊識別的紀錄若還掛著舊 id，
        // 使用者點進去會落到已停用的墓碑列
        var host = lookup.For(record);

        var status = handling?.Status ?? HandlingStatuses.Open;

        return new RecordListItemDto
        {
            HostId = host?.HostId ?? 0,
            HostName = host?.HostName ?? record.Host,
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

        // 防衛已存的舊紀錄：TrendAnalyzer 修正前寫入的行可能標成 New 卻帶著昨日次數
        // （首次出現的判定曾誤用可靠歷史，把「只在不完整日出現過」當成首次）。history.txt
        // 不會回填，這裡遇到矛盾就改述為重複發生，不讓畫面自打嘴巴。
        var effectiveTrend = issue.Trend == IssueTrend.New && issue.PreviousDayCount > 0
            ? IssueTrend.Recurring
            : issue.Trend;

        switch (effectiveTrend)
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
