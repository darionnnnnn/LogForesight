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
    private readonly ISystemSettingsService _settingsService;
    private readonly IVisibilityService _visibility;
    private readonly IIssueAggregateQuery _aggregates;
    private readonly OccurrenceStatusResolver _statusResolver;

    /// <summary>問題負責人（回饋十八輪批次F）：「依問題」視角順帶顯示負責人 badge，
    /// 讓「這個問題歸誰」在主視角一眼可見。可為 null——測試組裝不注入時該欄位維持空清單。</summary>
    private readonly IIssueOwnerStore? _issueOwners;

    public RecordListQueryService(
        IRecordRepository repository,
        IHostStore hosts,
        IUserStore users,
        IRecordHandlingStore handlings,
        IIssueHandlingStore issueHandlings,
        IIssueCaseStore cases,
        ISystemSettingsStore settings,
        ISystemSettingsService settingsService,
        IVisibilityService visibility,
        IIssueAggregateQuery aggregates,
        OccurrenceStatusResolver statusResolver,
        IIssueOwnerStore? issueOwners = null)
    {
        _repository = repository;
        _hosts = hosts;
        _users = users;
        _handlings = handlings;
        _issueHandlings = issueHandlings;
        _cases = cases;
        _settings = settings;
        _settingsService = settingsService;
        _visibility = visibility;
        _aggregates = aggregates;
        _statusResolver = statusResolver;
        _issueOwners = issueOwners;
    }

    public PagedResult<RecordListItemDto> Search(RecordSearchRequest request)
    {
        var filter = BuildFilter(request);
        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize);

        // Statuses／Overdue／Unassigned 篩選依賴處理狀態（lf_record_handling／lf_issue_handling／
        // lf_issue_cases 三張表 join 出來的推導結果），必須先算出候選集裡**每一筆**的日狀態才能篩選
        // ——天生無法只看某一頁。沒有這三個條件時才能把排序＋分頁整個下推給 SQL
        // （docs/archive/HISTORY.md P1-2），只為「這一頁」載入處理狀態，這是 2000 台規模下清單頁
        // 最常見瀏覽情境（不勾狀態篩選）的效能關鍵路徑；三個條件任一啟用時退回 QueryLightweight
        // 整批撈回（回饋十九輪批次E3，只帶輕量欄位，不是整份 ContentJson）。
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

        // 全面 SQL 化（回饋十九輪批次E3）：這條慢速路徑必須整批撈回才能逐筆算處理狀態，
        // 不能像上面的快速路徑一樣把分頁下推給 SQL——但撈回的不必是整份 ContentJson，
        // 改用 QueryLightweight 只帶處理狀態判定/分類/風險等級需要的欄位
        var records = _repository.QueryLightweight(filter);

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
        // 全面 SQL 化（回饋十九輪批次E2）：改版前整批載入期間內全部紀錄再於記憶體 GroupBy，
        // 與 SearchByIssue 改版前同一種代價（問題數 × 主機數 × 天數）。改由
        // lf_daily_records／lf_top_issues 兩句 GROUP BY 回答，語意與篩選條件與 E1 對齊。
        var hostIds = ResolveVisibleHostIds(request);
        var (from, to) = ResolveDateRange(request);
        var (riskLevels, categories, minSeverity) = ParseAggregateFilters(request);
        var visibleSeverities = ResolveVisibleSeverities();

        var aggregates = _aggregates.AggregateByHost(
            from, to, hostIds, riskLevels, categories, request.EventId, request.Source, minSeverity, visibleSeverities);
        var hostsById = _hosts.GetAll().ToDictionary(h => h.HostId);

        var groups = aggregates
            .Select(a => new RecordHostGroupDto
            {
                HostId = a.HostId,
                HostName = hostsById.TryGetValue(a.HostId, out var h) ? h.HostName : string.Empty,
                HighRiskDays = a.HighRiskDays,
                MediumRiskDays = a.MediumRiskDays,
                LowRiskDays = a.LowRiskDays,
                CorrelationDays = a.CorrelationDays,
                Categories = a.Categories.ToList(),
                LatestDate = a.LatestDate.ToString("yyyy-MM-dd"),
                LatestRiskLevel = a.LatestRiskLevel,
                LatestHeadline = a.LatestHeadline
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
        // 全面 SQL 化（回饋十九輪批次E2），語意與篩選條件與 SearchByHost／E1 對齊
        var hostIds = ResolveVisibleHostIds(request);
        var (from, to) = ResolveDateRange(request);
        var (riskLevels, categories, minSeverity) = ParseAggregateFilters(request);
        var visibleSeverities = ResolveVisibleSeverities();

        var aggregates = _aggregates.AggregateByDate(
            from, to, hostIds, riskLevels, categories, request.EventId, request.Source, minSeverity, visibleSeverities);

        var groups = aggregates
            .Select(a => new RecordDateGroupDto
            {
                Date = a.Date.ToString("yyyy-MM-dd"),
                HighRiskHosts = a.HighRiskHosts,
                MediumRiskHosts = a.MediumRiskHosts,
                LowRiskHosts = a.LowRiskHosts,
                CorrelationHosts = a.CorrelationHosts,
                Categories = a.Categories.ToList(),
                HostCount = a.HostCount
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
        // 全面 SQL 化（回饋十九輪批次E1）：改版前把期間內全部紀錄整批查回記憶體再 GroupBy——
        // 這正是需求「主視角改成問題」要用的那個畫面，卻是全站最慢的一條路徑（N3，2000 台環境下
        // 實測單次查詢超過 45 分鐘未返回）。現在改由 lf_top_issues 一次 GROUP BY 回答分組彙總，
        // 只在「最近一次出現快照」這一層（相對小的候選集：問題數 × 主機數，不是問題數 × 主機數 × 天數）
        // 才落回記憶體判定處理狀態。
        //
        // 這裡繞過 _repository.Query 的 ApplyVisibility，改直接呼叫 SQL 聚合查詢，
        // 所以可見範圍要自己算好再傳下去——空集合＝零結果，與既有的授權語意一致。
        var hostIds = ResolveVisibleHostIds(request);
        var (from, to) = ResolveDateRange(request);
        var visibleSeverities = ResolveVisibleSeverities();

        var aggregates = _aggregates.Aggregate(from, to, hostIds, visibleSeverities);

        if (request.EventId.HasValue)
            aggregates = aggregates.Where(a => a.EventId == request.EventId.Value).ToList();

        if (!string.IsNullOrWhiteSpace(request.Source))
            aggregates = aggregates.Where(a => string.Equals(a.Source, request.Source, StringComparison.OrdinalIgnoreCase)).ToList();

        // 風險類型過濾（回饋十三輪新增項1）：這裡的 Category 已經是問題層級（一個 (Source,EventId)
        // 恆定一個類別），不像舊版 BuildFilter 的記錄層過濾需要另外疊加一層
        if (request.Categories is { Count: > 0 } wantedCategories)
        {
            var allowedCategories = wantedCategories.ToHashSet(StringComparer.OrdinalIgnoreCase);
            aggregates = aggregates.Where(a => allowedCategories.Contains(a.Category)).ToList();
        }

        // 問題嚴重度過濾（docs/archive/FEEDBACK-8-PLAN.md #5）：這裡篩的是問題層級的期間內最高嚴重度
        // （Aggregate 已套用 LegacySeverityRank 正規化，不會再看到 Critical）
        if (request.RiskLevels is { Count: > 0 } riskLevels)
        {
            var allowedSeverities = riskLevels.SelectMany(MapRiskLevelToSeverities).ToHashSet();
            aggregates = aggregates.Where(a => allowedSeverities.Contains((IssueSeverity)a.MaxSeverityRank)).ToList();
        }

        // 嚴重度門檻（報表「類別×嚴重度」下鑽用，docs/WEB-SPEC.md）：語意同 RecordQueryFilter.MinSeverity
        // 「紀錄中任一問題簽章達到此嚴重度」，這裡的等價物是「問題期間內最高嚴重度達到門檻」
        if (!string.IsNullOrWhiteSpace(request.Severity) &&
            Enum.TryParse<IssueSeverity>(request.Severity, ignoreCase: true, out var minSeverity))
        {
            aggregates = aggregates.Where(a => a.MaxSeverityRank >= (int)minSeverity).ToList();
        }

        if (aggregates.Count == 0) return Paginate(new List<IssueGroupDto>(), request);

        // 密度的分母＝查詢期間天數。篩選未指定日期時退回候選問題本身的跨度——
        // 「2/90 天」的意義完全取決於分母是什麼，不能讓它變成一個沒有定義的數字
        var periodDays = request.From.HasValue && request.To.HasValue
            ? Math.Max(1, (request.To.Value.Date - request.From.Value.Date).Days + 1)
            : Math.Max(1, (aggregates.Max(a => a.LastSeen.Date) - aggregates.Min(a => a.FirstSeen.Date)).Days + 1);

        // 問題負責人 badge（回饋十八輪批次F）與使用者字典：一次批次載入，避免逐群組各自查詢
        // （BuildIssueGroup 時代 N+1 的既有教訓，見批次A/D 的 issueOwnersByKey／usersById 設計）
        var issueOwnersByKey = IssueProfile.IndexByKey(_issueOwners?.GetAll() ?? new List<IssueProfile>());
        var usersById = _users.GetAll().ToDictionary(u => u.UserId);

        // 處理狀態的候選集只到「篩選後留下的問題 × 可見主機」這一層（不是問題 × 主機 × 天數），
        // 與 OccurrenceStatusResolver 共用批次D 已驗證過的骨架（IssueHandlingRollupQuery 同款）
        var issues = aggregates.Select(a => (a.Source, a.EventId)).ToList();
        var occurrences = _aggregates.LatestOccurrences(issues, from, to, hostIds, visibleSeverities);
        var resolved = _statusResolver.Resolve(occurrences, from, to);

        // 依 (Source,EventId) 分桶——完整簽章鍵可能帶 Linux 的 EventKey 尾段（5 段），
        // 這裡刻意收斂回 4 段：Aggregate 本來就是依 (Source,EventId) 分組（未含 EventKey，
        // 見 IssueSignatureKey 的既有限制），兩邊分組粒度要對得上
        var resolvedByIssue = resolved
            .Select(r => (Sig: IssueSignatureKey.TryParseSignature(r.Occurrence.IssueKey), Resolved: r))
            .Where(x => x.Sig.HasValue)
            .GroupBy(x => x.Sig!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Resolved).ToList());

        // 機房級基準線（§G1）與 fleet 首見（§G4）：與 IssueRankingBuilder 共用同一個計算/介面，
        // 兩頁的「vs 基準」數字必然一致；只對本頁篩選後留下的問題查，不是整份表
        var (baselineFrom, baselineTo) = IssueBaselineCalculator.Window(to);
        var baselines = IssueBaselineCalculator.Compute(
            _aggregates.DailyHostCounts(issues, baselineFrom, baselineTo, hostIds));
        var fleetFirstSeen = _aggregates.FirstSeenFor(issues);

        var groups = aggregates
            .Select(a => BuildIssueGroup(
                a,
                resolvedByIssue.TryGetValue((a.Source, a.EventId), out var occs) ? occs : new List<ResolvedOccurrence>(),
                periodDays, issueOwnersByKey, usersById, baselines, fleetFirstSeen))
            .ToList();

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
    /// 單一問題分組的彙總 DTO（回饋十九輪批次E1，SQL 聚合結果版）。處理概況三態已由
    /// <see cref="OccurrenceStatusResolver"/> 逐主機判定好，這裡只負責彙整成三態計數與 DTO——
    /// 判定規則本身（案件優先／觀察到期／預設嚴重度）與改版前 BuildIssueGroup 完全共用
    /// <see cref="IssueGroupStatusResolver"/>，行為不變，只是資料來源從整批紀錄換成 SQL 快照。
    /// </summary>
    private IssueGroupDto BuildIssueGroup(
        IssueAggregate aggregate,
        List<ResolvedOccurrence> occurrences,
        int periodDays,
        IReadOnlyDictionary<(string SourceUpper, int EventId), List<long>> issueOwnersByKey,
        IReadOnlyDictionary<long, WebUser> usersById,
        IReadOnlyDictionary<(string SourceKey, int EventId), IssueBaselineCalculator.Baseline> baselines,
        IReadOnlyDictionary<(string SourceKey, int EventId), DateTime> fleetFirstSeen)
    {
        // 同一台主機在這個 (Source,EventId) 底下可能有多筆快照（Linux 的 EventKey 尾段被
        // TryParseSignature 收斂掉了）——每台主機只看它自己最近一次出現的那筆，
        // 與改版前「取該主機在群組內最新一筆紀錄」的既有語意相同
        var perHost = occurrences
            .GroupBy(o => o.Host.HostId)
            .Select(g => g.OrderByDescending(o => o.Occurrence.LastSeen).First())
            .ToList();

        int unhandled = 0, processing = 0, resolvedCount = 0;
        var handlerIds = new HashSet<long>();

        foreach (var occ in perHost)
        {
            // 處理人仍留在 Handlers 清單（人還是那個人，只是這個問題現在該重新處理了，
            // 不是「沒人管」）——這一點與狀態判定分開處理，不受 IssueGroupStatusResolver 影響
            if (occ.OpenCase?.HandlerId.HasValue == true) handlerIds.Add(occ.OpenCase.HandlerId.Value);

            switch (occ.Status)
            {
                case HostIssueStatus.Open: unhandled++; break;
                case HostIssueStatus.Processing: processing++; break;
                case HostIssueStatus.Resolved: resolvedCount++; break;
            }
        }

        // 已知問題說明：取整組（不分主機）最近一次出現那筆的標記，與改版前「群組內最新一筆
        // 紀錄的 KnownIssue」語意相同
        var latestKnownIssue = occurrences.OrderByDescending(o => o.Occurrence.LastSeen).FirstOrDefault()?.Occurrence.KnownIssue;

        var baselineKey = (SourceKey: aggregate.Source.ToUpperInvariant(), aggregate.EventId);
        baselines.TryGetValue(baselineKey, out var baseline);
        fleetFirstSeen.TryGetValue(baselineKey, out var firstSeenInFleet);

        var handlers = handlerIds
            .Select(id => new { Id = id, User = usersById.GetValueOrDefault(id) })
            .Where(x => !string.IsNullOrEmpty(x.User?.DisplayName))
            .OrderBy(x => x.User!.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new IssueGroupHandlerDto { HandlerId = x.Id, DisplayName = x.User!.DisplayName, Account = x.User.Account })
            .ToList();

        return new IssueGroupDto
        {
            Source = aggregate.Source,
            EventId = aggregate.EventId,
            Category = aggregate.Category,
            MaxSeverity = ((IssueSeverity)aggregate.MaxSeverityRank).ToString(),
            HostCount = aggregate.HostCount,
            DayCount = aggregate.DayCount,
            TotalCount = (int)aggregate.TotalCount,
            LastSeen = aggregate.LastSeen.ToString("yyyy-MM-dd"),

            // 時間形狀（§10.3）：全由 SQL 聚合結果既有的欄位帶出，不必額外查詢
            FirstSeen = aggregate.FirstSeen.ToString("yyyy-MM-dd"),
            ActiveDays = aggregate.ActiveDays,
            PeriodDays = periodDays,
            // 距今天數以分析資料的錨點（昨天）為準，不是真實今天（回饋十九輪批次C）：
            // 分析永遠只產到昨天，用真實今天算會讓「昨天發生的問題」顯示成「1 天前」，
            // 跟儀表板／報表的口徑（IssueRankingBuilder 同一批次的修法）對不起來
            DaysSinceLastSeen = Math.Max(0, (DateTime.Today.AddDays(-1) - aggregate.LastSeen.Date).Days),
            ElevatesDayRisk = aggregate.ElevatesDayRisk,

            KnownIssue = latestKnownIssue,
            HandlingSummary = BuildHandlingSummary(unhandled, processing, resolvedCount),
            // 群組層級的處理概況三態（§10 篩選用）：有未處理→open；否則有處理中→in_progress；否則 resolved
            GroupStatus = unhandled > 0 ? HandlingStatuses.Open
                : processing > 0 ? HandlingStatuses.InProgress
                : HandlingStatuses.Resolved,
            Handlers = handlers,
            IssueOwnerNames = issueOwnersByKey.TryGetValue(IssueProfile.KeyOf(aggregate.Source, aggregate.EventId), out var ownerIds)
                ? ownerIds.Select(id => usersById.GetValueOrDefault(id))
                    .Where(u => u is { Active: true })
                    .Select(u => NameFormat.WithAccount(u!.DisplayName, u.Account))
                    .ToList()
                : new List<string>(),

            BaselineMedianHostCount = baseline.MedianHostCount,
            BaselineLatestHostCount = baseline.LatestHostCount,
            BaselineDeviationMultiplier = baseline.DeviationMultiplier,
            BaselineOccurrenceDays = baseline.OccurrenceDays,
            FleetFirstSeen = (firstSeenInFleet == default ? aggregate.FirstSeen : firstSeenInFleet).ToString("yyyy-MM-dd")
        };
    }

    /// <summary>
    /// 依問題視角的可見主機範圍（回饋十九輪批次E1）：這裡繞過 _repository.Query，
    /// 所以要自己把 <see cref="IVisibilityService.GetVisibleHostIds"/> 與請求的 HostIds／GroupIds
    /// 交集起來——語意與 BuildFilter 的 HostIds／GroupIds 聯集邏輯相同，只是多疊一層可見範圍。
    /// 永遠回傳明確清單（空集合＝零結果），不回傳 null（與既有授權慣例一致）。
    /// </summary>
    private List<long> ResolveVisibleHostIds(RecordSearchRequest request)
    {
        var visible = _visibility.GetVisibleHostIds();

        var hostIds = new HashSet<long>(request.HostIds ?? Enumerable.Empty<long>());
        if (request.GroupIds is { Count: > 0 })
        {
            var wantedGroups = request.GroupIds.ToHashSet();
            foreach (var host in _hosts.GetAll().Where(h => h.GroupIds.Any(wantedGroups.Contains)))
                hostIds.Add(host.HostId);
        }

        return hostIds.Count == 0 ? visible.ToList() : visible.Where(hostIds.Contains).ToList();
    }

    /// <summary>期間未指定時退回全部歷史——SQL 聚合查詢需要具體的上下限，不能像 RecordQueryFilter
    /// 那樣用 null 代表不限制</summary>
    private static (DateTime From, DateTime To) ResolveDateRange(RecordSearchRequest request) =>
        (request.From ?? DateTime.MinValue, request.To ?? DateTime.Today);

    /// <summary>
    /// SQL 聚合查詢共用的 RiskLevels／Categories／MinSeverity 解析（回饋十九輪批次E2）：
    /// 與 <see cref="BuildFilter"/> 對同一批 request 欄位的既有解析規則相同，避免兩份拷貝各自漂移。
    /// </summary>
    private static (IReadOnlySet<string>? RiskLevels, IReadOnlySet<IssueCategory>? Categories, IssueSeverity? MinSeverity)
        ParseAggregateFilters(RecordSearchRequest request)
    {
        var riskLevels = request.RiskLevels is { Count: > 0 } ? request.RiskLevels.ToHashSet() : null;

        IReadOnlySet<IssueCategory>? categories = null;
        if (request.Categories is { Count: > 0 })
        {
            var parsed = new HashSet<IssueCategory>();
            foreach (var name in request.Categories)
            {
                if (Enum.TryParse<IssueCategory>(name, ignoreCase: true, out var category)) parsed.Add(category);
            }
            if (parsed.Count > 0) categories = parsed;
        }

        IssueSeverity? minSeverity = null;
        if (!string.IsNullOrWhiteSpace(request.Severity) &&
            Enum.TryParse<IssueSeverity>(request.Severity, ignoreCase: true, out var parsedSeverity))
        {
            minSeverity = parsedSeverity;
        }

        return (riskLevels, categories, minSeverity);
    }

    /// <summary>
    /// SiteHidden 模式的問題嚴重度可見性（docs/archive/HISTORY.md S1）——這裡繞過
    /// <see cref="IRecordRepository"/> 的單一咽喉，必須自己把 <see cref="ISystemSettingsService.GetVisibleSeverities"/>
    /// 轉成 SQL 聚合查詢要的型別再傳下去，否則 SiteHidden 模式下應該被隱藏的問題會在
    /// 依問題／依主機／依日期視角重新冒出來。null＝DefaultHidden 模式，不限制。
    /// </summary>
    private IReadOnlySet<IssueSeverity>? ResolveVisibleSeverities()
    {
        var visible = _settingsService.GetVisibleSeverities();
        if (visible == null) return null;

        return visible
            .Select(s => Enum.TryParse<IssueSeverity>(s, ignoreCase: true, out var severity) ? severity : (IssueSeverity?)null)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToHashSet();
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

    /// <summary>
    /// 跨主機同簽章聚類（AI 歸納用，回饋十九輪批次E6）：欄位是 <see cref="IssueAggregate"/>
    /// 的現成真子集（Source／EventId／HostCount／TotalCount），改走 <see cref="IIssueAggregateQuery.Aggregate"/>
    /// 不必另建聚合查詢——與 SearchByIssue（批次E1）同一套過濾邏輯。
    /// </summary>
    public List<IssueClusterDto> ClusterSignatures(RecordSearchRequest request)
    {
        var hostIds = ResolveVisibleHostIds(request);
        var (from, to) = ResolveDateRange(request);
        var visibleSeverities = ResolveVisibleSeverities();

        var aggregates = _aggregates.Aggregate(from, to, hostIds, visibleSeverities);

        if (request.EventId.HasValue)
            aggregates = aggregates.Where(a => a.EventId == request.EventId.Value).ToList();

        if (!string.IsNullOrWhiteSpace(request.Source))
            aggregates = aggregates.Where(a => string.Equals(a.Source, request.Source, StringComparison.OrdinalIgnoreCase)).ToList();

        if (request.Categories is { Count: > 0 } wantedCategories)
        {
            var allowedCategories = wantedCategories.ToHashSet(StringComparer.OrdinalIgnoreCase);
            aggregates = aggregates.Where(a => allowedCategories.Contains(a.Category)).ToList();
        }

        if (request.RiskLevels is { Count: > 0 } riskLevels)
        {
            var allowedSeverities = riskLevels.SelectMany(MapRiskLevelToSeverities).ToHashSet();
            aggregates = aggregates.Where(a => allowedSeverities.Contains((IssueSeverity)a.MaxSeverityRank)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(request.Severity) &&
            Enum.TryParse<IssueSeverity>(request.Severity, ignoreCase: true, out var minSeverity))
        {
            aggregates = aggregates.Where(a => a.MaxSeverityRank >= (int)minSeverity).ToList();
        }

        return aggregates
            // 只保留跨主機的——單台出現的不叫「共通」，AI 歸納那些沒有價值
            .Where(a => a.HostCount > 1)
            .Select(a => new IssueClusterDto
            {
                Source = a.Source,
                EventId = a.EventId,
                HostCount = a.HostCount,
                TotalCount = (int)a.TotalCount
            })
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
            AiPending = record.AiPending,
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
