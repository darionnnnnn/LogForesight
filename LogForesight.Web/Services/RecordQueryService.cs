using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>問題查詢與風險日詳情（docs/WEB-SPEC.md §9.2、§9.3）</summary>
public class RecordQueryService
{
    private readonly IRecordRepository _repository;
    private readonly IReportReader _reports;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IHostGroupStore _hostGroups;
    private readonly IVisibilityService _visibility;
    private readonly IRecordHandlingStore _handlings;
    private readonly IIssueHandlingStore _issueHandlings;
    private readonly IIssueCaseStore _cases;
    private readonly INoiseMarkStore _noiseMarks;
    private readonly IKnownIssueRuleStore _rules;
    private readonly ICurrentUser _currentUser;
    private readonly ISystemSettingsStore _settings;

    public RecordQueryService(
        IRecordRepository repository,
        IReportReader reports,
        IHostStore hosts,
        IUserStore users,
        IHostGroupStore hostGroups,
        IVisibilityService visibility,
        IRecordHandlingStore handlings,
        IIssueHandlingStore issueHandlings,
        IIssueCaseStore cases,
        INoiseMarkStore noiseMarks,
        IKnownIssueRuleStore rules,
        ICurrentUser currentUser,
        ISystemSettingsStore settings)
    {
        _repository = repository;
        _reports = reports;
        _hosts = hosts;
        _users = users;
        _hostGroups = hostGroups;
        _visibility = visibility;
        _handlings = handlings;
        _issueHandlings = issueHandlings;
        _cases = cases;
        _noiseMarks = noiseMarks;
        _rules = rules;
        _currentUser = currentUser;
        _settings = settings;
    }

    public PagedResult<RecordListItemDto> Search(RecordSearchRequest request)
    {
        var filter = BuildFilter(request);
        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize);

        // Statuses／Overdue 篩選依賴處理狀態（handling.json／issue_handling.json），那不在 SQL 裡，
        // 必須先算出候選集裡「每一筆」的日狀態才能篩選——天生無法只看某一頁。沒有這兩個條件時
        // 才能把排序＋分頁整個下推給 SQL（docs/HISTORY.md P1-2），只為「這一頁」載入
        // 處理狀態，這是 2000 台規模下清單頁最常見瀏覽情境（不勾狀態篩選）的效能關鍵路徑。
        var needsHandlingFilter = request.Statuses is { Count: > 0 } || request.Overdue == true;

        if (!needsHandlingFilter)
        {
            var paged = _repository.QueryPage(filter, page, pageSize, request.SortKey, request.Ascending);
            var pageLookup = new HostLookup(_hosts.GetAll());
            var pageHandlings = LoadHandlings(paged.Items, pageLookup);
            var pageIssueHandlings = LoadIssueHandlings(paged.Items, pageLookup);
            var pageOpenCases = LoadOpenCases(paged.Items, pageLookup);
            var pageUnhandledSeverities = _settings.Get().ParseUnhandledSeverities();

            DayHandlingDerivation.DayProgress PageProgress(DailyAnalysisRecord r) =>
                DeriveProgress(r, pageHandlings, pageIssueHandlings, pageLookup, pageUnhandledSeverities);

            bool PageIsOverdue(DailyAnalysisRecord r) =>
                ComputeIsOverdue(r, pageHandlings, pageIssueHandlings, pageLookup, pageUnhandledSeverities);

            return new PagedResult<RecordListItemDto>
            {
                Items = paged.Items
                    .Select(r => ToListItem(r, pageLookup, FindHandling(pageHandlings, pageLookup, r), PageProgress(r), PageIsOverdue(r), pageOpenCases))
                    .ToList(),
                Page = page,
                PageSize = pageSize,
                Total = paged.Total
            };
        }

        var records = _repository.Query(filter);

        // 紀錄 → 存活主機的索引：合併過的主機，舊識別下的紀錄要歸到存活主機，
        // 處理狀態（以現行主機名稱為鍵）與清單的連結才對得上
        var lookup = new HostLookup(_hosts.GetAll());

        // 處理狀態存在另外兩份資料（日層級 handling.json、問題層級 issue_handling.json），
        // 各一次撈起來在記憶體 join——逐筆查會變成 N 次檔案解析。日狀態由兩者推導（方案 B）
        var handlings = LoadHandlings(records, lookup);
        var issueHandlings = LoadIssueHandlings(records, lookup);
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();

        DayHandlingDerivation.DayProgress Progress(DailyAnalysisRecord r) =>
            DeriveProgress(r, handlings, issueHandlings, lookup, unhandledSeverities);

        // 逾期＝日層級 DueDate 過期且未結案，或任一問題層級「處理中」的 DueDate 過期
        // （批次套用改版後，預計完成日主要落在問題層級，逾期判定兩層都要看）
        bool IsOverdue(DailyAnalysisRecord r) =>
            ComputeIsOverdue(r, handlings, issueHandlings, lookup, unhandledSeverities);

        if (request.Statuses is { Count: > 0 })
        {
            // 對外三態篩選（#12）：畫面上的「已處理」chip 要涵蓋全部結案類
            // （不處理/誤報/已知雜訊…），不能只比對到原始的 resolved
            var wanted = request.Statuses.ToHashSet(StringComparer.OrdinalIgnoreCase);
            records = records.Where(r => wanted.Contains(HandlingStatuses.ExternalOf(Progress(r).DayStatus))).ToList();
        }

        if (request.Overdue == true)
        {
            records = records.Where(IsOverdue).ToList();
        }

        // 緊急程度排序（§DB-PLAN E 節定案）：風險層級 → 有無關聯訊號 → 日期新到舊——
        // 表頭指定排序時整段改用單欄排序（與 EfAnalysisRecordStore.QueryPage 的下推路徑同一套規則）
        var ordered = (request.SortKey switch
        {
            "date" => request.Ascending ? records.OrderBy(r => r.Date) : records.OrderByDescending(r => r.Date),
            "host" => request.Ascending
                ? records.OrderBy(r => lookup.For(r)?.HostName ?? r.Host, StringComparer.OrdinalIgnoreCase)
                : records.OrderByDescending(r => lookup.For(r)?.HostName ?? r.Host, StringComparer.OrdinalIgnoreCase),
            "risk" => request.Ascending
                ? records.OrderBy(r => RiskLevels.Rank(r.RiskLevel))
                : records.OrderByDescending(r => RiskLevels.Rank(r.RiskLevel)),
            _ => records.OrderByDescending(r => RiskLevels.Rank(r.RiskLevel))
                .ThenByDescending(r => r.CorrelationAlerts.Count > 0)
                .ThenByDescending(r => r.Date)
        }).ToList();

        var pageItems = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        // 案件只為「這一頁」載入（同 handlings/issueHandlings 的既有取捨）：慢速路徑的候選集
        // 可能是全部符合篩選的紀錄，逐筆載入案件會退化成全量掃描
        var openCases = LoadOpenCases(pageItems, lookup);

        return new PagedResult<RecordListItemDto>
        {
            Items = pageItems
                .Select(r => ToListItem(r, lookup, FindHandling(handlings, lookup, r), Progress(r), IsOverdue(r), openCases))
                .ToList(),
            Page = page,
            PageSize = pageSize,
            Total = ordered.Count
        };
    }

    /// <summary>
    /// 本頁涉及主機的全部進行中案件（docs/FEEDBACK-4-PLAN.md §0.4-D／Q5）：清單「處理人」欄
    /// fallback 用——日層級從未指派、但當日問題屬進行中案件時，顯示案件處理人。
    /// </summary>
    private List<IssueCase> LoadOpenCases(List<DailyAnalysisRecord> records, HostLookup lookup)
    {
        if (records.Count == 0) return new List<IssueCase>();

        var hostNames = records.Select(r => HostNameOf(lookup, r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return _cases.GetMany(hostNames).Where(c => c.ClosedAt == null).ToList();
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
                    HighRiskDays = g.Count(x => x.Record.RiskLevel == RiskLevels.High),
                    MediumRiskDays = g.Count(x => x.Record.RiskLevel == RiskLevels.Medium),
                    LowRiskDays = g.Count(x => x.Record.RiskLevel == RiskLevels.Low),
                    CorrelationDays = g.Count(x => x.Record.CorrelationAlerts.Count > 0),
                    Categories = CategoryAggregator.Aggregate(g.SelectMany(x => x.Record.TopIssues))
                        .Select(c => c.Category.ToString()).ToList(),
                    LatestDate = latest.Record.Date.ToString("yyyy-MM-dd"),
                    LatestRiskLevel = latest.Record.RiskLevel,
                    LatestHeadline = latest.Record.Headline
                };
            })
            .ToList();

        // 緊急程度：高風險日 → 關聯訊號日 → 中風險日（與明細排序、儀表板排行同一套）；
        // 表頭指定排序時改用單欄排序
        groups = (request.SortKey switch
        {
            "host" => request.Ascending
                ? groups.OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
                : groups.OrderByDescending(h => h.HostName, StringComparer.OrdinalIgnoreCase),
            "highRisk" => request.Ascending ? groups.OrderBy(h => h.HighRiskDays) : groups.OrderByDescending(h => h.HighRiskDays),
            "mediumRisk" => request.Ascending ? groups.OrderBy(h => h.MediumRiskDays) : groups.OrderByDescending(h => h.MediumRiskDays),
            "lowRisk" => request.Ascending ? groups.OrderBy(h => h.LowRiskDays) : groups.OrderByDescending(h => h.LowRiskDays),
            "correlation" => request.Ascending ? groups.OrderBy(h => h.CorrelationDays) : groups.OrderByDescending(h => h.CorrelationDays),
            _ => groups.OrderByDescending(h => h.HighRiskDays)
                .ThenByDescending(h => h.CorrelationDays)
                .ThenByDescending(h => h.MediumRiskDays)
        }).ToList();

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
                HighRiskHosts = g.Count(x => x.Record.RiskLevel == RiskLevels.High),
                MediumRiskHosts = g.Count(x => x.Record.RiskLevel == RiskLevels.Medium),
                LowRiskHosts = g.Count(x => x.Record.RiskLevel == RiskLevels.Low),
                CorrelationHosts = g.Count(x => x.Record.CorrelationAlerts.Count > 0),
                Categories = CategoryAggregator.Aggregate(g.SelectMany(x => x.Record.TopIssues))
                    .Select(c => c.Category.ToString()).ToList(),
                HostCount = g.Select(x => x.HostName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            })
            .ToList();

        groups = (request.SortKey switch
        {
            "hostCount" => request.Ascending ? groups.OrderBy(d => d.HostCount) : groups.OrderByDescending(d => d.HostCount),
            "highRisk" => request.Ascending ? groups.OrderBy(d => d.HighRiskHosts) : groups.OrderByDescending(d => d.HighRiskHosts),
            "mediumRisk" => request.Ascending ? groups.OrderBy(d => d.MediumRiskHosts) : groups.OrderByDescending(d => d.MediumRiskHosts),
            "lowRisk" => request.Ascending ? groups.OrderBy(d => d.LowRiskHosts) : groups.OrderByDescending(d => d.LowRiskHosts),
            "correlation" => request.Ascending ? groups.OrderBy(d => d.CorrelationHosts) : groups.OrderByDescending(d => d.CorrelationHosts),
            "date" when request.Ascending => groups.OrderBy(d => d.Date),
            _ => groups.OrderByDescending(d => d.Date)
        }).ToList();

        return Paginate(groups, request);
    }

    /// <summary>
    /// 問題查詢「依問題」視角（docs/FEEDBACK-4-PLAN.md §4）：一列一個問題（Source＋EventId，
    /// 與 <see cref="GroupIssuesBySignature"/> 同一個分組鍵），回答「這個問題影響多大範圍、
    /// 誰在處理」——依主機／依日期是「這台主機／這一天怎麼樣」，這裡反過來看「這個問題本身」。
    /// </summary>
    public PagedResult<IssueGroupDto> SearchByIssue(RecordSearchRequest request)
    {
        var records = _repository.Query(BuildFilter(request));
        var lookup = new HostLookup(_hosts.GetAll());
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();

        var groups = GroupIssuesBySignature(records)
            .Select(g => BuildIssueGroup(g, lookup, unhandledSeverities))
            .ToList();

        groups = (request.SortKey switch
        {
            "severity" => request.Ascending
                ? groups.OrderBy(i => SeverityRank(i.MaxSeverity))
                : groups.OrderByDescending(i => SeverityRank(i.MaxSeverity)),
            "hostCount" => request.Ascending ? groups.OrderBy(i => i.HostCount) : groups.OrderByDescending(i => i.HostCount),
            "dayCount" => request.Ascending ? groups.OrderBy(i => i.DayCount) : groups.OrderByDescending(i => i.DayCount),
            "totalCount" => request.Ascending ? groups.OrderBy(i => i.TotalCount) : groups.OrderByDescending(i => i.TotalCount),
            "lastSeen" => request.Ascending ? groups.OrderBy(i => i.LastSeen) : groups.OrderByDescending(i => i.LastSeen),
            // 預設：影響範圍優先——最高嚴重度 → 主機數 → 總次數（§4 定案）
            _ => groups.OrderByDescending(i => SeverityRank(i.MaxSeverity))
                .ThenByDescending(i => i.HostCount)
                .ThenByDescending(i => i.TotalCount)
        }).ToList();

        return Paginate(groups, request);
    }

    private static int SeverityRank(string severity) =>
        Enum.TryParse<IssueSeverity>(severity, out var s) ? (int)s : -1;

    /// <summary>
    /// 單一問題分組的彙總 DTO。處理概況三態計算：主機有進行中案件 → 處理中；否則看該主機
    /// 最近一次出現當天的問題層級標記——已結案 → 已處理；in_progress → 處理中；
    /// 未標記且不在「未處理計算」等級內（低風險預設不處理）→ 已處理（有結論，非待辦）；
    /// 其餘 → 未處理。與詳情頁問題層級的既有語意（IsDefaultUnhandled 等）保持一致，
    /// 只是這裡看的是「跨主機彙總」而非單一問題列。
    /// </summary>
    private IssueGroupDto BuildIssueGroup(
        IGrouping<(string Source, int EventId), (DailyAnalysisRecord Record, LogIssueSignature Issue)> g,
        HostLookup lookup,
        IReadOnlySet<IssueSeverity> unhandledSeverities)
    {
        var hostNames = g.Select(x => HostNameOf(lookup, x.Record)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var from = g.Min(x => x.Record.Date);
        var to = g.Max(x => x.Record.Date);
        var issueKeys = g.Select(x => IssueSignatureKey.For(x.Issue)).ToHashSet(StringComparer.Ordinal);

        var issueHandlings = _issueHandlings.GetMany(hostNames, from, to)
            .Where(h => issueKeys.Contains(h.IssueKey))
            .ToList();
        var openCases = _cases.GetMany(hostNames)
            .Where(c => issueKeys.Contains(c.IssueKey) && c.ClosedAt == null)
            .ToList();

        var latest = g.OrderByDescending(x => x.Record.Date).First();

        int processing = 0, resolved = 0, unhandled = 0;
        var handlerIds = new HashSet<long>();

        foreach (var hostName in hostNames)
        {
            var openCase = openCases.FirstOrDefault(c => string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase));
            if (openCase != null)
            {
                processing++;
                if (openCase.HandlerId.HasValue) handlerIds.Add(openCase.HandlerId.Value);
                continue;
            }

            var latestForHost = g
                .Where(x => string.Equals(HostNameOf(lookup, x.Record), hostName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Record.Date)
                .First();
            var key = IssueSignatureKey.For(latestForHost.Issue);
            var handling = issueHandlings.FirstOrDefault(h =>
                string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
                h.Date.Date == latestForHost.Record.Date.Date &&
                string.Equals(h.IssueKey, key, StringComparison.Ordinal));

            if (handling != null && IssueHandlingStatuses.IsClosed(handling.Status)) resolved++;
            else if (handling != null && handling.Status == IssueHandlingStatuses.InProgress) processing++;
            else if (!unhandledSeverities.Contains(latestForHost.Issue.Severity)) resolved++;
            else unhandled++;
        }

        var handlerNames = handlerIds
            .Select(id => _users.Get(id)?.DisplayName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new IssueGroupDto
        {
            Source = g.Key.Source,
            EventId = g.Key.EventId,
            Category = latest.Issue.Category.ToString(),
            MaxSeverity = g.Max(x => x.Issue.Severity).ToString(),
            HostCount = hostNames.Count,
            DayCount = g.Select(x => (Host: HostNameOf(lookup, x.Record), Day: x.Record.Date.Date)).Distinct().Count(),
            TotalCount = g.Sum(x => x.Issue.Count),
            LastSeen = latest.Record.Date.ToString("yyyy-MM-dd"),
            KnownIssue = latest.Issue.KnownIssue,
            HandlingSummary = BuildHandlingSummary(unhandled, processing, resolved),
            HandlerNames = handlerNames.Count > 3
                ? new List<string> { $"{handlerNames[0]} 等 {handlerNames.Count} 人" }
                : handlerNames
        };
    }

    /// <summary>「N 台未處理／M 台處理中」的三態摘要文字；全部有結論時省略未處理／處理中兩段</summary>
    private static string BuildHandlingSummary(int unhandled, int processing, int resolved)
    {
        var parts = new List<string>();
        if (unhandled > 0) parts.Add($"{unhandled} 台未處理");
        if (processing > 0) parts.Add($"{processing} 台處理中");
        if (resolved > 0) parts.Add($"{resolved} 台已處理");
        return parts.Count > 0 ? string.Join("／", parts) : "—";
    }

    /// <summary>彙總視角的分頁：先群組再分頁（分頁在記憶體，資料量與明細視角同級）</summary>
    private static PagedResult<T> Paginate<T>(List<T> items, RecordSearchRequest request)
    {
        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize);

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

    private List<IssueHandling> LoadIssueHandlings(List<DailyAnalysisRecord> records, HostLookup lookup)
    {
        if (records.Count == 0) return new List<IssueHandling>();

        var hostNames = records.Select(r => HostNameOf(lookup, r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return _issueHandlings.GetMany(hostNames, records.Min(r => r.Date), records.Max(r => r.Date));
    }

    /// <summary>單筆紀錄的日狀態推導（方案 B）：日層級狀態 + 當日問題層級標記，走 DayHandlingDerivation 單點規則</summary>
    private DayHandlingDerivation.DayProgress DeriveProgress(
        DailyAnalysisRecord record,
        List<RecordHandling> dayHandlings,
        List<IssueHandling> issueHandlings,
        HostLookup lookup,
        IReadOnlySet<IssueSeverity> unhandledSeverities)
    {
        var dayStatus = FindHandling(dayHandlings, lookup, record)?.Status;
        var forDay = IssueHandlingsFor(record, issueHandlings, lookup);

        return DayHandlingDerivation.Derive(record.TopIssues, forDay, dayStatus, unhandledSeverities);
    }

    /// <summary>
    /// 逾期＝日層級 DueDate 過期且未結案，或任一問題層級「處理中」的 DueDate 過期
    /// （批次套用改版後，預計完成日主要落在問題層級，逾期判定兩層都要看）。
    /// Search 的分頁快速路徑與完整路徑各自傳入自己那份 handlings/issueHandlings，共用同一份判定。
    /// </summary>
    private bool ComputeIsOverdue(
        DailyAnalysisRecord record,
        List<RecordHandling> handlings,
        List<IssueHandling> issueHandlings,
        HostLookup lookup,
        IReadOnlySet<IssueSeverity> unhandledSeverities)
    {
        var handling = FindHandling(handlings, lookup, record);
        var dayOverdue = handling?.DueDate.HasValue == true &&
                         handling.DueDate.Value.Date < DateTime.Today &&
                         DeriveProgress(record, handlings, issueHandlings, lookup, unhandledSeverities).IsUnresolved;
        return dayOverdue ||
               DayHandlingDerivation.HasOverdueIssue(IssueHandlingsFor(record, issueHandlings, lookup), DateTime.Today);
    }

    /// <summary>該紀錄當日的問題層級標記（以現行主機名稱為鍵，同 HostNameOf 的合併語意）</summary>
    private static List<IssueHandling> IssueHandlingsFor(
        DailyAnalysisRecord record, List<IssueHandling> issueHandlings, HostLookup lookup)
    {
        var name = HostNameOf(lookup, record);
        return issueHandlings
            .Where(h => string.Equals(h.HostName, name, StringComparison.OrdinalIgnoreCase) &&
                        h.Date.Date == record.Date.Date)
            .ToList();
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

    public RecordDetailDto GetDetail(long hostId, DateTime date)
    {
        var record = _repository.GetOne(hostId, date)
                     ?? throw DomainException.NotFound("找不到這筆分析紀錄，或您沒有檢視權限。");

        var host = _hosts.Get(hostId);

        // 規則命中問題的處置參考來自當前 rules.json（反映 Web 上的規則編輯），
        // 一次載入建索引，逐列查是記憶體查表。單日詳情低頻，一次檔案讀取可忽略
        var guidance = LoadGuidanceLookup();

        // 問題層級處理狀態（方案 B）：以現行主機名稱為鍵取當日已標記的問題。
        // 存整筆 IssueHandling（不只是 Status 字串）——批次套用改版後還需要 DueDate
        var hostName = host?.HostName ?? record.Host;
        var issueHandlingByKey = _issueHandlings
            .GetForDay(hostName, date)
            .GroupBy(h => h.IssueKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // 已知雜訊記憶（跨日、以主機＋簽章為鍵，見 §5.1 D-1 #3）：未標記的問題若命中記憶，
        // 前端自動顯示「已知雜訊（自動）」，不必使用者每天重標
        var noiseMarks = _noiseMarks.GetForHost(hostName)
            .ToDictionary(m => m.IssueKey, StringComparer.Ordinal);

        // 問題案件（docs/FEEDBACK-4-PLAN.md §2）：進行中案件涵蓋的問題顯示「○○○ 處理中
        // （1/10 起）」，解釋為什麼某些問題的狀態會被案件同步「自己動」
        var openCases = _cases.GetOpenForHost(hostName)
            .ToDictionary(
                c => c.IssueKey,
                c => (HandlerId: c.HandlerId, HandlerName: c.HandlerId.HasValue ? _users.Get(c.HandlerId.Value)?.DisplayName : null,
                      c.Status, FirstLinkedDate: c.FirstLinkedDate.ToString("yyyy-MM-dd")),
                StringComparer.Ordinal);

        var settings = _settings.Get();
        var unhandledSeverities = settings.ParseUnhandledSeverities();

        // 嚴重度可見性已由 RecordRepository.GetOne 強制套用（S1）：record.TopIssues 已是
        // SiteHidden 模式下的可見子集，這裡不必也不該重覆判斷 SeverityDisplayMode
        var visibleTopIssues = record.TopIssues;

        return new RecordDetailDto
        {
            HostId = hostId,
            HostName = host?.HostName ?? record.Host,
            HostDisplayName = host?.DisplayName,
            HostIpAddress = host?.IpAddress,
            HostOs = host?.Os ?? "windows",
            HostRoleDesc = host?.RoleDesc ?? "",
            Date = record.Date.ToString("yyyy-MM-dd"),
            RiskLevel = record.RiskLevel,
            RiskBasisText = FormatRiskBasis(record.RiskBasis),
            HiddenIssueCount = record.HiddenIssueCount,
            Headline = record.Headline,
            Summary = record.Summary,
            TrendAssessment = record.TrendAssessment,
            Action = record.Action,
            AiAnalyzed = record.AiAnalyzed,
            ErrorCount = record.ErrorCount,
            WarningCount = record.WarningCount,
            AuditEventCount = record.AuditEventCount,
            TopIssues = visibleTopIssues.Select(i => ToIssueDto(i, guidance, issueHandlingByKey, noiseMarks, unhandledSeverities, openCases)).ToList(),
            Categories = CategoryAggregator.Aggregate(visibleTopIssues).Select(ToCategoryDto).ToList(),
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
            },
            CanHandle = _currentUser.Has(Capability.Handle),
            UnhandledSeverities = settings.UnhandledSeverities,
            SeverityDisplayMode = settings.SeverityDisplayMode
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
        // 否則「風險時間軸」會在合併日之前整片空白，看起來像沒有分析過。
        // applyDayRiskVisibility=false（docs/FEEDBACK-3-PLAN.md #8 豁免）：時間軸與下方的
        // 重點問題彙總都要看完整證據——被「日風險等級顯示」設定藏起來的日子，時間軸若顯示成
        // 「無分析紀錄」灰格就是說謊，這裡是全站唯二不受該設定影響的查詢路徑之一
        // （另一個是 GetOne，本來就不走 filter 路徑）。
        var records = _repository.Query(new RecordQueryFilter
        {
            Hosts = _repository.ResolveHostKeys(hostId),
            From = from
        }, applyDayRiskVisibility: false)
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
                HasCoverageGap = record != null && record.HasCoverageGap
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
            DisplayName = host.DisplayName,
            RoleDesc = host.RoleDesc,
            IpAddress = host.IpAddress,
            Os = host.Os,
            NetiqServer = host.NetiqServer,
            LastReportAt = host.LastReportAt,
            GroupNames = NameFormat.ResolveNames(host.GroupIds, groups, g => g.GroupName),
            OwnerNames = NameFormat.ResolveNames(host.OwnerUserIds, users, u => u.DisplayName),
            Timeline = timeline,
            LatestCheckup = latestCheckup == null ? null : new WeeklyCheckupDto
            {
                CheckupDate = latestCheckup.CheckupDate.ToString("yyyy-MM-dd"),
                HasFindings = latestCheckup.HasFindings,
                Conclusion = latestCheckup.Conclusion
            },
            TopSignatures = BuildIssueSummary(records.Values)
        };
    }

    /// <summary>
    /// 主機詳情頁「重點問題」某一列展開後的發生明細（docs/FEEDBACK-4-PLAN.md §3）：點某個
    /// 問題（Source+EventId）看它在期間內逐日出現的頻率、間隔與各日處理狀態。與
    /// <see cref="GetHostDetail"/> 共用同一套別名展開＋<c>applyDayRiskVisibility:false</c> 豁免
    /// （時間軸與這裡都要看完整證據，不受「日風險等級顯示」設定影響）。
    /// </summary>
    public HostIssueOccurrenceDto GetHostIssueOccurrences(long hostId, string source, int eventId, int days)
    {
        _visibility.EnsureVisible(hostId);
        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        var from = DateTime.Today.AddDays(-days + 1);
        var records = _repository.Query(new RecordQueryFilter
        {
            Hosts = _repository.ResolveHostKeys(hostId),
            From = from,
            Source = source,
            EventId = eventId
        }, applyDayRiskVisibility: false)
            // 合併當天可能有存活主機與墓碑各一筆，取存活主機那筆（同 GetHostDetail 的既有處理）
            .GroupBy(r => r.Date.Date)
            .Select(g => g.FirstOrDefault(r => r.HostId == hostId) ?? g.First())
            .Where(r => r.TopIssues.Any(i => i.Source == source && i.EventId == eventId))
            .OrderBy(r => r.Date)
            .ToList();

        if (records.Count == 0) return new HostIssueOccurrenceDto();

        var hostName = host.HostName;
        // 一個 (Source,EventId) 可能對到多個完整 IssueKey（LogName/EntryType 不同）——
        // 逐日取當天實際命中的那個簽章，狀態也各自對應那個簽章的標記，不強行假設全期間同一把鍵
        var issueKeys = records
            .SelectMany(r => r.TopIssues.Where(i => i.Source == source && i.EventId == eventId))
            .Select(IssueSignatureKey.For)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet();

        var issueHandlings = _issueHandlings.GetMany(new[] { hostName }, records.Min(r => r.Date), records.Max(r => r.Date));
        var noiseMarks = _noiseMarks.GetForHost(hostName).ToDictionary(m => m.IssueKey, StringComparer.Ordinal);
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();

        var occurrences = new List<HostIssueOccurrenceDayDto>();
        foreach (var record in records)
        {
            var issue = record.TopIssues.First(i => i.Source == source && i.EventId == eventId);
            var key = IssueSignatureKey.For(issue);
            var handling = issueHandlings.FirstOrDefault(h => h.Date.Date == record.Date.Date && string.Equals(h.IssueKey, key, StringComparison.Ordinal));
            var (status, isDefaultUnhandled, noiseMark) = ResolveIssueStatus(issue, handling, noiseMarks, unhandledSeverities);

            occurrences.Add(new HostIssueOccurrenceDayDto
            {
                Date = record.Date.ToString("yyyy-MM-dd"),
                Count = issue.Count,
                RiskLevel = record.RiskLevel,
                StatusText = status.Length > 0 ? IssueStatusText(status)
                    : isDefaultUnhandled ? "不處理（預設）"
                    : noiseMark != null ? "已知雜訊（自動）"
                    : "未處理",
                FromCase = handling?.CaseId != null
            });
        }

        // 案件行：有進行中或最近結案的案件時才顯示——「上次誰處理的、怎麼結的」，
        // 即使案件已結案也保留（不是只顯示進行中）
        var relevantCase = _cases.GetMany(new[] { hostName })
            .Where(c => issueKeys.Contains(c.IssueKey))
            .OrderByDescending(c => c.ClosedAt == null)
            .ThenByDescending(c => c.UpdatedAt)
            .FirstOrDefault();

        var stats = BuildOccurrenceStats(records);
        stats.TotalCount = occurrences.Sum(o => o.Count);

        return new HostIssueOccurrenceDto
        {
            Stats = stats,
            Case = relevantCase == null ? null : new HostIssueOccurrenceCaseDto
            {
                HandlerId = relevantCase.HandlerId,
                HandlerName = relevantCase.HandlerId.HasValue ? _users.Get(relevantCase.HandlerId.Value)?.DisplayName : null,
                Status = relevantCase.Status,
                StatusText = IssueStatusText(relevantCase.Status),
                FirstLinkedDate = relevantCase.FirstLinkedDate.ToString("yyyy-MM-dd"),
                ClosedAt = relevantCase.ClosedAt
            },
            Occurrences = occurrences
        };
    }

    /// <summary>出現頻率統計：平均間隔（只出現一天時無意義，回 null）、最長連續出現天數
    /// （日曆日相鄰才算連續，缺一天就斷）</summary>
    private static HostIssueOccurrenceStatsDto BuildOccurrenceStats(List<DailyAnalysisRecord> records)
    {
        var dates = records.Select(r => r.Date.Date).Distinct().OrderBy(d => d).ToList();

        double? avgGapDays = null;
        if (dates.Count > 1)
        {
            var gaps = new List<double>();
            for (var i = 1; i < dates.Count; i++) gaps.Add((dates[i] - dates[i - 1]).TotalDays);
            avgGapDays = gaps.Average();
        }

        var longestStreak = dates.Count > 0 ? 1 : 0;
        var currentStreak = longestStreak;
        for (var i = 1; i < dates.Count; i++)
        {
            currentStreak = (dates[i] - dates[i - 1]).Days == 1 ? currentStreak + 1 : 1;
            longestStreak = Math.Max(longestStreak, currentStreak);
        }

        return new HostIssueOccurrenceStatsDto
        {
            DaysSeen = dates.Count,
            // TotalCount 由呼叫端另外從逐日結果加總覆寫（BuildOccurrenceStats 這裡沒有
            // Source/EventId 可篩選，records.TopIssues 是整日全部問題，不能直接加總）
            AvgGapDays = avgGapDays,
            LongestStreak = longestStreak,
            FirstSeen = dates.Count > 0 ? dates[0].ToString("yyyy-MM-dd") : string.Empty,
            LastSeen = dates.Count > 0 ? dates[^1].ToString("yyyy-MM-dd") : string.Empty
        };
    }

    /// <summary>
    /// 主機詳情頁「重點問題（期間彙總）」（docs/FEEDBACK-3-PLAN.md #4）：依 Source+EventId
    /// 分組——與 <see cref="ClusterSignatures"/> 共用 <see cref="GroupIssuesBySignature"/>，
    /// 這裡關心「出現幾天」而不是「幾台主機」，維度不同故各自 Select，分組鍵定義只寫一次。
    /// 排序：最高嚴重度（重→輕）→ 總次數 desc。
    /// </summary>
    private static List<HostIssueSummaryDto> BuildIssueSummary(IEnumerable<DailyAnalysisRecord> records) =>
        GroupIssuesBySignature(records)
            .Select(g =>
            {
                // 說明／分類取最近一天的：規則的已知問題文字可能事後被編輯過，
                // 各天的舊紀錄各自保留分析當下的快照，「最近一天最新」比任意挑一筆更有用
                var latest = g.OrderByDescending(x => x.Record.Date).First();
                return (MaxSeverity: g.Max(x => x.Issue.Severity), Dto: new HostIssueSummaryDto
                {
                    Source = g.Key.Source,
                    EventId = g.Key.EventId,
                    Category = latest.Issue.Category.ToString(),
                    TotalCount = g.Sum(x => x.Issue.Count),
                    DaysSeen = g.Select(x => x.Record.Date.Date).Distinct().Count(),
                    LastSeenDate = latest.Record.Date.ToString("yyyy-MM-dd"),
                    KnownIssue = latest.Issue.KnownIssue
                });
            })
            // 排序用未字串化的 enum（High=2 > Medium=1 > Low=0），字串化只在最後賦值，
            // 不繞「轉字串再解析回列舉」那種多此一舉的寫法
            .OrderByDescending(x => x.MaxSeverity)
            .ThenByDescending(x => x.Dto.TotalCount)
            .Select(x =>
            {
                x.Dto.MaxSeverity = x.MaxSeverity.ToString();
                return x.Dto;
            })
            .ToList();

    /// <summary>
    /// 攤平所有紀錄的 TopIssues，依 (Source, EventId) 分組——<see cref="ClusterSignatures"/>
    /// （跨主機）與 <see cref="BuildIssueSummary"/>（單主機跨日）共用同一個分組鍵定義，
    /// 避免兩處各自維護一份、遲早漂移不一致。
    /// </summary>
    private static IEnumerable<IGrouping<(string Source, int EventId), (DailyAnalysisRecord Record, LogIssueSignature Issue)>>
        GroupIssuesBySignature(IEnumerable<DailyAnalysisRecord> records) =>
        records
            .SelectMany(r => r.TopIssues.Select(i => (Record: r, Issue: i)))
            .GroupBy(x => (x.Issue.Source, x.Issue.EventId));

    public List<IssueClusterDto> ClusterSignatures(RecordSearchRequest request)
    {
        var records = _repository.Query(BuildFilter(request));
        var lookup = new HostLookup(_hosts.GetAll());

        return GroupIssuesBySignature(records)
            .Select(g => new IssueClusterDto
            {
                Source = g.Key.Source,
                EventId = g.Key.EventId,
                HostCount = g.Select(x => HostNameOf(lookup, x.Record)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TotalCount = g.Sum(x => x.Issue.Count)
            })
            // 只保留跨主機的——單台出現的不叫「共通」，AI 歸納那些沒有價值
            .Where(c => c.HostCount > 1)
            .OrderByDescending(c => c.HostCount)
            .ThenByDescending(c => c.TotalCount)
            .Take(5)
            .ToList();
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

        // 主機群組展開成主機 Id 集合，與明確勾選的 HostIds 取聯集——兩個條件都是「我要看這些主機」，
        // 不是互斥的兩種篩選（勾了群組又額外勾一台不在群組內的主機是常見用法）
        var hostIds = new HashSet<long>(request.HostIds ?? Enumerable.Empty<long>());
        if (request.GroupIds is { Count: > 0 })
        {
            var wantedGroups = request.GroupIds.ToHashSet();
            foreach (var host in _hosts.GetAll().Where(h => h.GroupIds.Any(wantedGroups.Contains)))
                hostIds.Add(host.HostId);
        }

        if (hostIds.Count > 0)
        {
            // 每台主機展開成「本身＋已併入它的墓碑列」，篩選某台主機時才看得到它合併前的歷史
            filter.Hosts = hostIds
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
        RecordHandling? handling,
        DayHandlingDerivation.DayProgress progress,
        bool isOverdue,
        List<IssueCase>? openCases = null)
    {
        // 顯示與連結一律指向**存活**主機：合併之後，舊識別的紀錄若還掛著舊 id，
        // 使用者點進去會落到已停用的墓碑列
        var host = lookup.For(record);
        var hostName = host?.HostName ?? record.Host;

        // 處理人 fallback（docs/FEEDBACK-4-PLAN.md §0.4-D／Q5）：日層級有值時優先；否則若當日
        // 有問題屬進行中案件，顯示案件處理人（後綴「（案件）」）——否則 2.3 情境下狀態同步了、
        // 處理人卻空白，使用者會困惑。多個問題各自屬不同處理人的案件時取第一個命中的，
        // 這種混合情境本就罕見（多半是同一次指派建的案），不特別處理
        long? handlerId = handling?.HandlerId;
        var handlerFromCase = false;
        if (!handlerId.HasValue && openCases is { Count: > 0 } && record.TopIssues.Count > 0)
        {
            var issueKeys = record.TopIssues.Select(IssueSignatureKey.For).ToHashSet(StringComparer.Ordinal);
            var openCase = openCases.FirstOrDefault(c =>
                string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
                issueKeys.Contains(c.IssueKey) && c.HandlerId.HasValue);
            if (openCase != null)
            {
                handlerId = openCase.HandlerId;
                handlerFromCase = true;
            }
        }
        var handlerDisplayName = handlerId.HasValue ? _users.Get(handlerId.Value)?.DisplayName : null;

        return new RecordListItemDto
        {
            HostId = host?.HostId ?? 0,
            HostName = hostName,
            Date = record.Date.ToString("yyyy-MM-dd"),
            RiskLevel = record.RiskLevel,
            Headline = record.Headline,
            ErrorCount = record.ErrorCount,
            WarningCount = record.WarningCount,
            Categories = CategoryAggregator.Aggregate(record.TopIssues)
                .Select(c => c.Category.ToString())
                .ToList(),
            HasCorrelation = record.CorrelationAlerts.Count > 0,
            HasCoverageGap = record.HasCoverageGap,
            AiAnalyzed = record.AiAnalyzed,
            // 對外三態（#12）：清單頁只呈現 未處理／處理中／已處理，結案類細節留給詳情頁
            HandlingStatus = HandlingStatuses.ExternalOf(progress.DayStatus),
            HandlingStatusText = HandlingStatusText(HandlingStatuses.ExternalOf(progress.DayStatus)),
            TotalIssues = progress.Total,
            ClosedIssues = progress.Closed,
            HandlerId = handlerId,
            HandlerName = handlerFromCase && handlerDisplayName != null ? $"{handlerDisplayName}（案件）" : handlerDisplayName,
            IsOverdue = isOverdue
        };
    }

    /// <summary>對外三態文字（#12）：呼叫端一律先經 HandlingStatuses.ExternalOf，這裡只需覆蓋三態</summary>
    /// <summary>
    /// 判定依據代碼 → 白話文字（docs/HISTORY.md #11）：與 LogAnalysisService
    /// 寫入的代碼格式對應（rule:.../correlation/trend/high_issue:.../medium/ai_raise）。
    /// null／未知代碼一律回 null，前端顯示通用說明，不強行湊一句解釋不出來的話。
    /// </summary>
    private static string? FormatRiskBasis(string? basis)
    {
        if (string.IsNullOrEmpty(basis)) return null;
        if (basis == "ai_raise") return "AI 判讀上調（程式判定較低）";
        if (basis == "trend") return "頻率異常規則命中";
        if (basis == "correlation") return "跨事件關聯訊號命中";
        if (basis == "medium") return "問題嚴重度判定為中風險";
        if (basis.StartsWith("rule:", StringComparison.Ordinal)) return $"重大規則命中：{basis[5..]}";
        if (basis.StartsWith("high_issue:", StringComparison.Ordinal)) return $"高嚴重度問題：{basis[11..]}";
        return null;
    }

    private static string HandlingStatusText(string status) => status switch
    {
        HandlingStatuses.Open => "未處理",
        HandlingStatuses.InProgress => "處理中",
        HandlingStatuses.Resolved => "已處理",
        _ => status
    };

    /// <summary>
    /// 規則 Id → 規則 的處置參考索引。用當前 rules.json（反映 Web 編輯）；載入失敗時退回
    /// 內建種子，詳情頁仍看得到處置參考、不會因規則檔一時讀不到就整片從缺。
    /// </summary>
    private Dictionary<string, KnownIssueRule> LoadGuidanceLookup()
    {
        var outcome = _rules.Load();
        var rules = outcome.Success && outcome.Content != null
            ? outcome.Content.Rules
            : KnownIssueSeed.CreateRules();

        // 同 Id 理論上唯一；防禦性地取第一筆，避免壞檔的重複 Id 讓 ToDictionary 整個炸掉
        return rules
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 問題層級狀態判定的單點邏輯（docs/FEEDBACK-4-PLAN.md §3）：未標記時看已知雜訊記憶／
    /// 未處理計算等級推導出「不處理（預設）」「已知雜訊（自動）」；明確標記過（含 open）一律
    /// 尊重使用者的判斷，不再套自動推導。ToIssueDto（詳情頁）與主機詳情問題發生明細
    /// （GetHostIssueOccurrences）共用，避免兩處各自維護一份判定條件遲早漂移不一致。
    /// </summary>
    private static (string Status, bool IsDefaultUnhandled, NoiseMark? NoiseMark) ResolveIssueStatus(
        LogIssueSignature issue, IssueHandling? handling, Dictionary<string, NoiseMark>? noiseMarks,
        IReadOnlySet<IssueSeverity> unhandledSeverities)
    {
        var key = IssueSignatureKey.For(issue);
        var status = handling?.Status ?? string.Empty;

        // 未標記時才看得到自動判讀：明確標記過（含 open）一律尊重使用者的判斷，不再套自動推導
        var noiseMark = status.Length == 0 && noiseMarks != null && noiseMarks.TryGetValue(key, out var m) ? m : null;
        // 未列入「未處理計算」等級的問題（「系統管理 > 設定」頁維護），未標記過時預設視為不處理
        var isDefaultUnhandled = status.Length == 0 && noiseMark == null && !unhandledSeverities.Contains(issue.Severity);

        return (status, isDefaultUnhandled, noiseMark);
    }

    private static IssueDto ToIssueDto(
        LogIssueSignature issue,
        Dictionary<string, KnownIssueRule>? guidance,
        Dictionary<string, IssueHandling>? issueHandlingByKey,
        Dictionary<string, NoiseMark>? noiseMarks,
        IReadOnlySet<IssueSeverity> unhandledSeverities,
        Dictionary<string, (long? HandlerId, string? HandlerName, string Status, string FirstLinkedDate)>? openCases = null)
    {
        var key = IssueSignatureKey.For(issue);
        var handling = issueHandlingByKey != null && issueHandlingByKey.TryGetValue(key, out var h) ? h : null;
        var (status, isDefaultUnhandled, noiseMark) = ResolveIssueStatus(issue, handling, noiseMarks, unhandledSeverities);

        (long? HandlerId, string? HandlerName, string Status, string FirstLinkedDate)? openCase =
            openCases != null && openCases.TryGetValue(key, out var c) ? c : null;

        return new IssueDto
        {
            LogName = issue.LogName,
            Source = issue.Source,
            EventId = issue.EventId,
            Count = issue.Count,
            Category = issue.Category.ToString(),
            Severity = issue.Severity.ToString(),
            ElevatesDayRisk = issue.ElevatesDayRisk,
            KnownIssue = issue.KnownIssue,
            FirstSeen = issue.FirstSeen,
            LastSeen = issue.LastSeen,
            DistinctMessageCount = issue.DistinctMessageCount,
            KeyDetails = issue.KeyDetails,
            SampleMessages = issue.SampleMessages,
            Suppressed = issue.Suppressed,
            Trend = issue.Trend.ToString(),
            TrendText = BuildTrendText(issue),
            Guidance = BuildGuidance(issue, guidance),
            IssueKey = key,
            RuleId = issue.RuleId,
            HandlingStatus = status,
            HandlingStatusText = IssueStatusText(status),
            IsDefaultUnhandled = isDefaultUnhandled,
            IsAutoNoise = noiseMark != null,
            NoiseNote = noiseMark?.Note,
            DueDate = handling?.DueDate?.ToString("yyyy-MM-dd"),
            CaseHandlerId = openCase?.HandlerId,
            CaseHandlerName = openCase?.HandlerName,
            CaseStatus = openCase?.Status,
            CaseFirstLinkedDate = openCase?.FirstLinkedDate
        };
    }

    private static string IssueStatusText(string status) => status switch
    {
        IssueHandlingStatuses.Resolved => "已處理",
        IssueHandlingStatuses.WontFix => "不處理",
        IssueHandlingStatuses.FalsePositive => "誤報",
        IssueHandlingStatuses.KnownNoise => "已知雜訊",
        IssueHandlingStatuses.InProgress => "處理中",
        IssueHandlingStatuses.Open => string.Empty,
        _ => string.Empty
    };

    /// <summary>
    /// 規則命中問題的處置參考（與 txt 報告「處置參考（知識庫）」同一份來源）。
    /// 以問題的 RuleId 反查規則——用命中當下記下的 Id 最精準（來源/EventId 反查可能因規則改動而漂移）。
    /// 無 RuleId（未命中規則的 Other）或規則無知識內容時回 null，前端就不掛展開面板。
    /// </summary>
    private static IssueGuidanceDto? BuildGuidance(LogIssueSignature issue, Dictionary<string, KnownIssueRule>? guidance)
    {
        if (guidance == null || string.IsNullOrEmpty(issue.RuleId)) return null;
        if (!guidance.TryGetValue(issue.RuleId, out var rule)) return null;

        var hasContent = !string.IsNullOrWhiteSpace(rule.PlainExplanation) ||
                         !string.IsNullOrWhiteSpace(rule.Impact) ||
                         rule.LikelyCauses.Length > 0 || rule.NextSteps.Length > 0;
        if (!hasContent) return null;

        return new IssueGuidanceDto
        {
            Explanation = rule.PlainExplanation,
            Impact = rule.Impact,
            LikelyCauses = rule.LikelyCauses.ToList(),
            NextSteps = rule.NextSteps.ToList()
        };
    }

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

        // 首次出現時「前一日 N 次」是贅述（首次出現＝前一日必為 0），不輸出——
        // 這是 §5.1 D-1 #5：畫面已經說了「首次」，不需要再用一個必然是 0 的數字佐證
        if (effectiveTrend != IssueTrend.New && issue.PreviousDayCount.HasValue)
            parts.Add($"前一日 {issue.PreviousDayCount} 次");
        if (issue.HistoryDailyAverage.HasValue) parts.Add($"歷史平均 {issue.HistoryDailyAverage.Value:0.#} 次");
        if (issue.DaysSeenInHistory > 0) parts.Add($"近期出現 {issue.DaysSeenInHistory} 天");

        return string.Join("\n", parts);
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
        LowCount = summary.LowCount,
        ElevatesCount = summary.ElevatesCount
    };
}

public class RecordSearchRequest
{
    public List<long>? HostIds { get; set; }

    /// <summary>主機群組篩選（§5.4 D-4）：展開為主機集合後與 HostIds 取聯集，
    /// 兩千台規模下「看某個部門的主機」比一台台勾選實際得多</summary>
    public List<long>? GroupIds { get; set; }
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

    /// <summary>
    /// 表頭排序（docs/WEB-SPEC.md §9.2）。null／不合法值＝維持各視角原本的預設排序。
    /// 明細視角：date | host | risk。依主機視角：host | highRisk | mediumRisk | lowRisk | correlation。
    /// 依日期視角：date | hostCount | highRisk | mediumRisk | lowRisk | correlation。
    /// </summary>
    public string? SortKey { get; set; }

    public bool Ascending { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
