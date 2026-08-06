using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>
/// 問題查詢的清單視角（docs/WEB-SPEC.md §9.2）：明細／依主機／依日期／依問題。
/// 單日詳情與主機詳情見 <see cref="RecordDetailQueryService"/>。
/// </summary>
public class RecordListQueryService
{
    private readonly IRecordRepository _repository;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IRecordHandlingStore _handlings;
    private readonly IIssueHandlingStore _issueHandlings;
    private readonly IIssueCaseStore _cases;
    private readonly ISystemSettingsStore _settings;

    public RecordListQueryService(
        IRecordRepository repository,
        IHostStore hosts,
        IUserStore users,
        IRecordHandlingStore handlings,
        IIssueHandlingStore issueHandlings,
        IIssueCaseStore cases,
        ISystemSettingsStore settings)
    {
        _repository = repository;
        _hosts = hosts;
        _users = users;
        _handlings = handlings;
        _issueHandlings = issueHandlings;
        _cases = cases;
        _settings = settings;
    }

    public PagedResult<RecordListItemDto> Search(RecordSearchRequest request)
    {
        var filter = BuildFilter(request);
        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize);

        // Statuses／Overdue 篩選依賴處理狀態（handling.json／issue_handling.json），那不在 SQL 裡，
        // 必須先算出候選集裡「每一筆」的日狀態才能篩選——天生無法只看某一頁。沒有這兩個條件時
        // 才能把排序＋分頁整個下推給 SQL（docs/archive/HISTORY.md P1-2），只為「這一頁」載入
        // 處理狀態，這是 2000 台規模下清單頁最常見瀏覽情境（不勾狀態篩選）的效能關鍵路徑。
        var needsHandlingFilter = request.Statuses is { Count: > 0 } || request.Overdue == true || request.Unassigned;

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

        if (request.Unassigned)
        {
            // 未指派（§5/§10）：無有效處理人＝日層級無 HandlerId 且無案件涵蓋（與清單處理人欄
            // 的 case fallback 同語意）。案件為整個候選集載入（未指派是刻意的窄查詢，非常見瀏覽路徑）
            var allOpenCases = LoadOpenCases(records, lookup);
            records = records.Where(r => IsUnassigned(r, lookup, handlings, allOpenCases)).ToList();
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
    /// 本頁涉及主機的全部進行中案件（docs/archive/FEEDBACK-4-PLAN.md §0.4-D／Q5）：清單「處理人」欄
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
    /// 問題查詢「依問題」視角（docs/archive/FEEDBACK-4-PLAN.md §4）：一列一個問題（Source＋EventId，
    /// 與 <see cref="RecordQueryHelpers.GroupIssuesBySignature"/> 同一個分組鍵），回答「這個問題影響
    /// 多大範圍、誰在處理」——依主機／依日期是「這台主機／這一天怎麼樣」，這裡反過來看「這個問題本身」。
    /// </summary>
    public PagedResult<IssueGroupDto> SearchByIssue(RecordSearchRequest request)
    {
        var records = _repository.Query(BuildFilter(request));
        var lookup = new HostLookup(_hosts.GetAll());
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();

        // 處理狀態與案件**整批載入一次**（docs/SCALE-ISSUE-FIRST-PLAN.md N3）。
        //
        // 改版前 BuildIssueGroup 是逐群組呼叫的，而它內部各有一次 GetMany——
        // 1000 種問題就是 2000 次查詢（blob 時代更是 2000 次整份反序列化，
        // 2000 台環境下實測單次查詢超過 45 分鐘未返回）。
        // **這正是需求「主視角改成問題」要用的那個畫面**，卻是全站最慢的一條路徑。
        var allHandlings = LoadIssueHandlings(records, lookup);
        var allCases = LoadOpenCases(records, lookup);

        // 逐群組再從整批結果裡取——以 (主機, 問題鍵) 建索引，群組內查找 O(1)
        var handlingsByHost = allHandlings
            .GroupBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var casesByHost = allCases
            .GroupBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 密度的分母＝查詢期間天數。篩選未指定日期時退回紀錄本身的跨度——
        // 「2/90 天」的意義完全取決於分母是什麼，不能讓它變成一個沒有定義的數字
        var periodDays = request.From.HasValue && request.To.HasValue
            ? Math.Max(1, (request.To.Value.Date - request.From.Value.Date).Days + 1)
            : records.Count == 0 ? 1 : Math.Max(1, (records.Max(r => r.Date.Date) - records.Min(r => r.Date.Date)).Days + 1);

        var groups = RecordQueryHelpers.GroupIssuesBySignature(records)
            .Select(g => BuildIssueGroup(g, lookup, unhandledSeverities, handlingsByHost, casesByHost, periodDays))
            .ToList();

        // 問題嚴重度過濾（docs/archive/FEEDBACK-8-PLAN.md #5）：上方「風險層級」chips 篩的是日風險等級
        // （已在 BuildFilter 套用到 records），但依問題視角一列一個問題，顯示的「嚴重度」是
        // 問題層級的 High/Medium/Low——高風險日裡本來就可能同時有低嚴重度的問題，不疊加這層
        // 過濾的話，使用者勾選「高＋中」後清單仍會看到「低」，觀感就是「篩選沒生效」。
        // 疊加而非取代：日風險過濾維持不變，這裡只讓結果更窄，不會撈回被日風險濾掉的問題。
        if (request.RiskLevels is { Count: > 0 } riskLevels)
        {
            var allowedSeverities = riskLevels.SelectMany(MapRiskLevelToSeverities).ToHashSet();
            groups = groups.Where(g => Enum.TryParse<IssueSeverity>(g.MaxSeverity, out var s) && allowedSeverities.Contains(s)).ToList();
        }

        // 處理概況三態過濾（§10）：篩的是群組層級的「處理概況」（open/in_progress/resolved）
        if (request.Statuses is { Count: > 0 } statuses)
        {
            var wanted = statuses.ToHashSet(StringComparer.OrdinalIgnoreCase);
            groups = groups.Where(g => wanted.Contains(g.GroupStatus)).ToList();
        }

        // 未指派過濾（§5/§10）：處理人清單為空＝這個問題沒有任何主機被指派
        if (request.Unassigned)
        {
            groups = groups.Where(g => g.Handlers.Count == 0).ToList();
        }

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
    /// 日風險等級（高/中/低）→ 問題嚴重度集合（docs/archive/FEEDBACK-8-PLAN.md #5）。Critical 併入
    /// 「高」：docs/archive/HISTORY.md #1（B1 三級化）後規則不再產出 Critical（嚴重度封頂 High，
    /// 改看 ElevatesDayRisk 旗標判定高風險日），Critical 只可能是三級化前的歷史資料——
    /// 概念上等同今日的「高」，勾選「高」時仍要看得到。
    /// </summary>
    private static IEnumerable<IssueSeverity> MapRiskLevelToSeverities(string riskLevel) => riskLevel switch
    {
        RiskLevels.High => new[] { IssueSeverity.High, IssueSeverity.Critical },
        RiskLevels.Medium => new[] { IssueSeverity.Medium },
        RiskLevels.Low => new[] { IssueSeverity.Low },
        _ => Array.Empty<IssueSeverity>()
    };

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
        IReadOnlySet<IssueSeverity> unhandledSeverities,
        IReadOnlyDictionary<string, List<IssueHandling>> handlingsByHost,
        IReadOnlyDictionary<string, List<IssueCase>> casesByHost,
        int periodDays)
    {
        var hostNames = g.Select(x => HostNameOf(lookup, x.Record)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var from = g.Min(x => x.Record.Date);
        var to = g.Max(x => x.Record.Date);
        var issueKeys = g.Select(x => IssueSignatureKey.For(x.Issue)).ToHashSet(StringComparer.Ordinal);

        // 自整批結果過濾，不再逐群組打 store（N3）——語意與改版前逐群組查詢逐位相同：
        // 同樣是「這些主機、這個期間、這些問題鍵」的交集
        var issueHandlings = hostNames
            .SelectMany(name => handlingsByHost.TryGetValue(name, out var rows) ? rows : Enumerable.Empty<IssueHandling>())
            .Where(h => issueKeys.Contains(h.IssueKey) && h.Date.Date >= from.Date && h.Date.Date <= to.Date)
            .ToList();
        var openCases = hostNames
            .SelectMany(name => casesByHost.TryGetValue(name, out var rows) ? rows : Enumerable.Empty<IssueCase>())
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
                if (openCase.HandlerId.HasValue) handlerIds.Add(openCase.HandlerId.Value);
                // 觀察到期（docs/archive/FEEDBACK-8-PLAN.md #4）：問題仍在發生，計入未處理——處理人仍留在
                // Handlers 清單（人還是那個人，只是這個問題現在該重新處理了，不是「沒人管」）
                if (IssueHandlingStatuses.IsObservationExpired(openCase.Status, openCase.DueDate, DateTime.Today))
                    unhandled++;
                else
                    processing++;
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
            else if (handling != null && IssueHandlingStatuses.IsObservationActive(handling.Status, handling.DueDate, DateTime.Today)) processing++;
            else if (handling != null && IssueHandlingStatuses.IsObservationExpired(handling.Status, handling.DueDate, DateTime.Today)) unhandled++;
            else if (!unhandledSeverities.Contains(latestForHost.Issue.Severity)) resolved++;
            else unhandled++;
        }

        var handlers = handlerIds
            .Select(id => new { Id = id, User = _users.Get(id) })
            .Where(x => !string.IsNullOrEmpty(x.User?.DisplayName))
            .OrderBy(x => x.User!.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new IssueGroupHandlerDto { HandlerId = x.Id, DisplayName = x.User!.DisplayName, Account = x.User.Account })
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

            // 時間形狀（§10.3）：全由本群組既有的紀錄推導，不必額外查詢
            FirstSeen = from.ToString("yyyy-MM-dd"),
            ActiveDays = g.Select(x => x.Record.Date.Date).Distinct().Count(),
            PeriodDays = periodDays,
            DaysSinceLastSeen = Math.Max(0, (DateTime.Today - latest.Record.Date.Date).Days),
            ElevatesDayRisk = g.Any(x => x.Issue.ElevatesDayRisk),

            KnownIssue = latest.Issue.KnownIssue,
            HandlingSummary = BuildHandlingSummary(unhandled, processing, resolved),
            // 群組層級的處理概況三態（§10 篩選用）：有未處理→open；否則有處理中→in_progress；否則 resolved
            GroupStatus = unhandled > 0 ? HandlingStatuses.Open
                : processing > 0 ? HandlingStatuses.InProgress
                : HandlingStatuses.Resolved,
            Handlers = handlers
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
    /// 紀錄對應的**現行**主機名稱——處理狀態以現行名稱為鍵（DayHandlingCommandService 一律由 hostId 解析），
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

    public List<IssueClusterDto> ClusterSignatures(RecordSearchRequest request)
    {
        var records = _repository.Query(BuildFilter(request));
        var lookup = new HostLookup(_hosts.GetAll());

        return RecordQueryHelpers.GroupIssuesBySignature(records)
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

    /// <summary>未指派（§5/§10）：日層級無處理人且無進行中案件涵蓋當日任一問題——與清單處理人欄
    /// 的案件 fallback（ToListItem）同語意，只是這裡回布林用於過濾。</summary>
    private bool IsUnassigned(DailyAnalysisRecord record, HostLookup lookup, List<RecordHandling> handlings, List<IssueCase> openCases)
    {
        if (FindHandling(handlings, lookup, record)?.HandlerId != null) return false;
        if (record.TopIssues.Count == 0) return true;
        var name = lookup.For(record)?.HostName ?? record.Host;
        var issueKeys = record.TopIssues.Select(IssueSignatureKey.For).ToHashSet(StringComparer.Ordinal);
        return !openCases.Any(c =>
            c.HandlerId.HasValue &&
            string.Equals(c.HostName, name, StringComparison.OrdinalIgnoreCase) &&
            issueKeys.Contains(c.IssueKey));
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

        // 處理人 fallback（docs/archive/FEEDBACK-4-PLAN.md §0.4-D／Q5）：日層級有值時優先；否則若當日
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
        var handlerUser = handlerId.HasValue ? _users.Get(handlerId.Value) : null;
        var handlerDisplayName = handlerUser?.DisplayName;

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
            HandlerName = handlerDisplayName,
            HandlerAccount = handlerUser?.Account,
            HandlerFromCase = handlerFromCase,
            IsOverdue = isOverdue
        };
    }

    /// <summary>對外三態文字（#12）：呼叫端一律先經 HandlingStatuses.ExternalOf，這裡只需覆蓋三態</summary>
    private static string HandlingStatusText(string status) => status switch
    {
        HandlingStatuses.Open => "未處理",
        HandlingStatuses.InProgress => "處理中",
        HandlingStatuses.Resolved => "已處理",
        _ => status
    };
}
