using System.Text.RegularExpressions;
using LogForesight.Core.Service;
using System.Diagnostics;
using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 交辦單查詢 API：清單、詳情、成員、時間軸。
///
/// **逐單授權在這裡、不在 controller**：單號可列舉，所以詳情／成員／時間軸每次都檢查
/// 「具 Assign 或 ViewAll，或是該單處理人」；單不存在 404、存在但無權 403。
/// 成員另依檢視者的可見範圍過濾（ViewAll 與處理人看全部），並回報被濾掉的數量，
/// 讓畫面分得出「沒看到」與「不存在」。
/// </summary>
public class WorkOrderQueryService
{
    private const int DefaultPageSize = 20;
    private const int DefaultMemberPageSize = 50;
    private const string SystemActorName = "系統";

    private readonly IWorkOrderStore _orders;
    private readonly IIssueCaseStore _cases;
    private readonly IUserStore _users;
    private readonly IHostStore _hosts;
    private readonly IHostGroupStore _hostGroups;
    private readonly IKnownIssueRuleStore _rules;
    private readonly IVisibilityService _visibility;
    private readonly ICurrentUser _currentUser;
    private readonly IUserDisplayNameService _displayNames;
    private readonly IIssueExclusionSource _exclusions;
    private readonly IUserGroupStore _userGroups;
    private readonly IAnalysisRecordQuery _records;

    /// <summary>「靜音到期恢復」的回看天數：最近一個已結束區間的迄日落在 [今天−7, 今天−1]</summary>
    private const int ResumedWindowDays = 7;

    private readonly TimeProvider _clock;
    private readonly UserCapabilityResolver? _capabilities;
    private readonly IIssueHandlingStore? _issueHandlings;
    private readonly IIssueAggregateQuery? _aggregates;

    public WorkOrderQueryService(
        IWorkOrderStore orders,
        IIssueCaseStore cases,
        IUserStore users,
        IHostStore hosts,
        IHostGroupStore hostGroups,
        IKnownIssueRuleStore rules,
        IVisibilityService visibility,
        ICurrentUser currentUser,
        IUserDisplayNameService displayNames,
        IIssueExclusionSource exclusions,
        IUserGroupStore userGroups,
        IAnalysisRecordQuery records,
        TimeProvider? clock = null,
        UserCapabilityResolver? capabilities = null,
        IIssueHandlingStore? issueHandlings = null,
        IIssueAggregateQuery? aggregates = null)
    {
        _aggregates = aggregates;
        _issueHandlings = issueHandlings;
        _capabilities = capabilities;
        _clock = clock ?? TimeProvider.System;
        _userGroups = userGroups;
        _records = records;
        _orders = orders;
        _cases = cases;
        _users = users;
        _hosts = hosts;
        _hostGroups = hostGroups;
        _rules = rules;
        _visibility = visibility;
        _currentUser = currentUser;
        _displayNames = displayNames;
        _exclusions = exclusions;
    }

    // ── 清單 ────────────────────────────────────────────────────────────────

    /// <summary>清單（能力由 controller 標註把關）</summary>
    public WorkOrderListDto List(WorkOrderListRequest req) => QueryList(req, WorkOrderQueries.PausedInclude, _exclusions.Current());

    /// <summary>
    /// 某處理人的交辦清單：本人，或具 Assign／ViewAll 才看得到；處理人條件固定為 userId（忽略請求帶的值），
    /// 查詢與組裝同 <see cref="List"/>。暫停單預設排除（請求明確帶 <c>only</c>／<c>include</c> 時依請求）。
    /// </summary>
    public WorkOrderListDto ListForHandler(long userId, WorkOrderListRequest req)
    {
        AuthorizeHandlerView(userId);
        req.HandlerId = userId;
        return QueryList(req, WorkOrderQueries.PausedExclude, _exclusions.Current());
    }

    /// <summary>某處理人的進行中摘要；授權同 <see cref="ListForHandler"/></summary>
    public HandlerSummaryDto HandlerSummary(long userId)
    {
        AuthorizeHandlerView(userId);
        var user = _users.Get(userId) ?? throw DomainException.NotFound("找不到這位使用者。");
        var dto = SummaryOf(userId);
        // 工作頁標頭：主清單不再呼叫 workload，處理人名稱改由摘要一併帶回
        dto.DisplayName = _displayNames.Of(user.DisplayName);
        dto.Account = user.Account;
        dto.Active = user.Active;
        dto.HandlerPaused = user.DispatchPaused;
        // 檢視者自己的可見主機數：只用在本人頁的空狀態分流（側欄徽章不需要，不算）
        // 只經案件授與看得到的主機也算「有授權」：只算群組授權會把這種人誤判成「尚未被授權任何主機」
        dto.VisibleHostCount = _visibility.GetVisibleHostIds().Count + _visibility.GetCaseGrantHostNames().Count;
        FillTodayCounts(dto, userId);
        return dto;
    }

    /// <summary>
    /// 「今天需要你處理」的五個數字（P-1）：只有處理人工作頁的摘要端點算（側欄徽章每次輪詢都呼叫，不加這幾趟查詢）。
    /// 母體＝這位處理人進行中、非暫停的單，與清單各列同一份 <see cref="BuildFacts"/>，所以各數字＝清單各列同名欄位相加。
    /// </summary>
    private void FillTodayCounts(HandlerSummaryDto dto, long userId)
    {
        var exclusion = _exclusions.Current();
        var orders = _orders.GetActiveByHandler(userId).Where(o => !IsPaused(o, exclusion)).ToList();
        if (orders.Count == 0) return;

        var facts = BuildFacts(orders, DisabledHostKeys(), SeverityScope.None, out _);
        dto.NewTodayHosts = facts.Values.Sum(f => f.NewToday);
        dto.RecurrenceHosts = facts.Values.Sum(f => f.Recurrence);
        dto.ReturnedMembers = facts.Values.Sum(f => f.ReturnedMembers);
        dto.ObservationExpiredHosts = facts.Values.Sum(f => f.Expired);
        dto.DueSoonMembers = facts.Values.Sum(f => f.DueSoonMembers);
    }

    /// <summary>
    /// 側欄徽章（目前使用者）：與 <see cref="HandlerSummary"/> 同一份 store 摘要。
    /// ServerAdmin（UserId &lt;= 0）不會是任何單的處理人，回全 0、不擲。
    /// </summary>
    public HandlerSummaryDto MyBadge() =>
        _currentUser.UserId <= 0 ? new HandlerSummaryDto() : SummaryOf(_currentUser.UserId);

    private HandlerSummaryDto SummaryOf(long userId)
    {
        // 停用主機上的成員不算待辦（與負載看板、總覽同一口徑）
        var s = _orders.HandlerSummary(userId, _exclusions.Current().CurrentlyMutedCompositeKeys, DisabledHostKeys());
        return new HandlerSummaryDto
        {
            ActiveWorkOrders = s.ActiveWorkOrders, ActiveMembers = s.ActiveMembers,
            OverdueMembers = s.OverdueMembers, UnrepliedWorkOrders = s.UnrepliedWorkOrders,
            PausedWorkOrders = s.PausedWorkOrders,
            TodoHosts = s.TodoMembers, ObservingHosts = s.ObservingMembers, EscalatedHosts = s.EscalatedMembers
        };
    }

    /// <summary>處理人視角的授權：本人，或具 Assign／ViewAll；否則 403</summary>
    internal void AuthorizeHandlerView(long userId)
    {
        if (userId == _currentUser.UserId) return;
        if (_currentUser.Has(Capability.Assign) || _currentUser.Has(Capability.ViewAll)) return;
        throw DomainException.Forbidden("沒有檢視這位處理人交辦清單的權限。");
    }

    /// <summary>
    /// 清單查詢與組裝的唯一一份：交辦單一次、本頁計數一次、使用者一次。
    /// 排序與篩選分兩條路：資料庫端能排的三個鍵且沒有標記篩選＝SQL 排序＋分頁（只組本頁）；
    /// 要讀取時推導（處理優先序、期限最近、嚴重度）或有標記篩選（被退回、今天新增、復發…）＝先取符合條件的全體、算好各單的推導欄位、
    /// 在記憶體篩選與排序後才分頁（排序跨頁成立，不是只排當頁）。兩條路組出來的列欄位相同。
    /// </summary>
    private WorkOrderListDto QueryList(WorkOrderListRequest req, string defaultPausedMode, IssueExclusion exclusion)
    {
        if (!WorkOrderQueries.OrderStatuses.Contains(req.Status))
            throw DomainException.Validation($"不支援的狀態篩選「{req.Status}」。");
        if (!WorkOrderQueries.AllSorts.Contains(req.Sort))
            throw DomainException.Validation($"不支援的排序「{req.Sort}」。");
        var flag = NormalizeFlag(req.Flag);
        var pausedMode = string.IsNullOrWhiteSpace(req.Paused) ? defaultPausedMode : req.Paused.Trim();
        if (!WorkOrderQueries.PausedModes.Contains(pausedMode))
            throw DomainException.Validation($"不支援的暫停篩選「{req.Paused}」。");

        var (page, pageSize) = Paging.Normalize(req.Page, req.PageSize < 1 ? DefaultPageSize : req.PageSize, WorkOrderQueries.MaxOrderPageSize);

        var users = _users.GetAll().ToDictionary(u => u.UserId);
        var disabledKeys = DisabledHostKeys();

        IReadOnlyCollection<long>? handlerIds = req.HandlerId.HasValue ? new[] { req.HandlerId.Value } : null;
        if (req.HandlerInactive)
        {
            // 與續掛停止原因 handler_inactive 同義（處理人已停用，或已不具處理能力——派工端以不在候選人快照判定）：
            // 帳號停用，或解析出的能力不含 Handle。測試組裝沒注入解析器時只看帳號停用
            var inactive = users.Values
                .Where(u => !u.Active || (_capabilities != null && !_capabilities.Resolve(u).Contains(Capability.Handle)))
                .Select(u => u.UserId).ToHashSet();
            handlerIds = handlerIds == null ? inactive : handlerIds.Where(inactive.Contains).ToList();
        }
        if (req.GroupId.HasValue)
        {
            // 回饋第 47 輪定案 48：處理人屬於該使用者群組；群組不存在或無成員＝空集合＝查無
            var groupId = req.GroupId.Value;
            var members = users.Values.Where(u => u.GroupIds.Contains(groupId)).Select(u => u.UserId).ToHashSet();
            handlerIds = handlerIds == null ? members : handlerIds.Where(members.Contains).ToList();
        }

        var derived = flag != null || WorkOrderQueries.DerivedSorts.Contains(req.Sort);
        var query = new WorkOrderQuery
        {
            HandlerIds = handlerIds,
            Source = string.IsNullOrWhiteSpace(req.Source) ? null : req.Source.Trim(),
            EventId = req.EventId,
            Status = req.Status,
            Sort = derived ? WorkOrderQueries.SortCreatedDesc : req.Sort,
            Page = page,
            PageSize = pageSize,
            PausedKeys = exclusion.CurrentlyMutedCompositeKeys,
            ExcludedHostKeys = disabledKeys,
            PausedMode = pausedMode,
            OnlyKeys = req.ResumedFromMute ? ResumedFromMuteKeys(exclusion) : null,
            UnreadOnly = req.UnreadOnly
        };

        List<WorkOrder> pageItems;
        int total;
        Dictionary<long, OrderFacts> facts;
        if (!derived)
        {
            var result = _orders.QueryOrders(query);
            pageItems = result.Items;
            total = result.Total;
            facts = BuildFacts(pageItems, disabledKeys, SeverityScope.WithFallback, out _);
        }
        else
        {
            var all = _orders.FilterOrders(query);
            var needSeverity = req.Sort is WorkOrderQueries.SortPriority or WorkOrderQueries.SortSeverity;
            facts = BuildFacts(all, disabledKeys, needSeverity ? SeverityScope.ActiveOnly : SeverityScope.None, out _);
            IEnumerable<WorkOrder> kept = all;
            if (flag != null) kept = kept.Where(o => FlagMatches(flag, facts[o.WorkOrderId]));
            var ordered = SortOrders(kept, req.Sort, facts, req.Sort == WorkOrderQueries.SortMembersDesc
                ? _orders.CountMembers(all.Select(o => o.WorkOrderId).ToList(), disabledKeys) : null).ToList();
            total = ordered.Count;
            pageItems = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            // 排序只用進行中成員的嚴重度；本頁的列再補「沒有進行中成員時取全部成員最後已知值」
            AddSeverityFallback(pageItems, facts);
        }

        var counts = _orders.CountMembers(pageItems.Select(o => o.WorkOrderId).ToList(), disabledKeys);
        var rules = KnownIssueCatalog.ResolveRules(_rules);

        return new WorkOrderListDto
        {
            Items = pageItems.Select(o => FillRow(new WorkOrderRowDto(), o, users, counts, facts, rules, exclusion)).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>標記篩選鍵正規化（不分大小寫、去頭尾空白）；空白＝不篩；不認得的值回驗證錯誤</summary>
    private static string? NormalizeFlag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        return WorkOrderQueries.Flags.FirstOrDefault(f => string.Equals(f, text, StringComparison.OrdinalIgnoreCase))
               ?? throw DomainException.Validation($"不支援的標記篩選「{raw}」。");
    }

    private static bool FlagMatches(string flag, OrderFacts f) => flag switch
    {
        WorkOrderQueries.FlagReturned => f.ReturnedMembers > 0,
        WorkOrderQueries.FlagNewToday => f.NewToday > 0,
        WorkOrderQueries.FlagRecurrence => f.Recurrence > 0,
        WorkOrderQueries.FlagObservationExpired => f.Expired > 0,
        WorkOrderQueries.FlagDueSoon => f.DueSoonMembers > 0,
        _ => false
    };

    /// <summary>嚴重度排序鍵：高＞中＞低；同級重大旗標在前；無法推導（null）排最後</summary>
    private static int SeverityKey(OrderFacts f) =>
        f.Severity is not { } s ? -1 : (int)s * 2 + (f.Critical ? 1 : 0);

    /// <summary>
    /// 全體排序（唯一一份）：同值一律再依建立時間新到舊、單號降冪，分頁才穩定。
    /// 三個推導鍵的定義見 <see cref="WorkOrderQueries.SortPriority"/>、<see cref="WorkOrderQueries.SortDueSoon"/>、<see cref="WorkOrderQueries.SortSeverity"/>；
    /// 其餘三個（建立時間、成員數、未回覆）與資料庫端排序同義，有標記篩選時走這裡。
    /// </summary>
    private static IEnumerable<WorkOrder> SortOrders(IEnumerable<WorkOrder> orders, string sort,
        Dictionary<long, OrderFacts> facts, Dictionary<long, WorkOrderMemberCounts>? counts)
    {
        OrderFacts F(WorkOrder o) => facts[o.WorkOrderId];
        return sort switch
        {
            WorkOrderQueries.SortPriority => orders
                .OrderByDescending(o => F(o).ReturnedMembers > 0)
                .ThenByDescending(o => F(o).OverdueMembers > 0)
                .ThenByDescending(o => SeverityKey(F(o)))
                .ThenBy(o => F(o).EarliestDue ?? DateTime.MaxValue)
                .ThenByDescending(o => o.CreatedAt)
                .ThenByDescending(o => o.WorkOrderId),
            WorkOrderQueries.SortDueSoon => orders
                .OrderBy(o => F(o).EarliestDue ?? DateTime.MaxValue)
                .ThenByDescending(o => o.CreatedAt)
                .ThenByDescending(o => o.WorkOrderId),
            WorkOrderQueries.SortSeverity => orders
                .OrderByDescending(o => SeverityKey(F(o)))
                .ThenByDescending(o => o.CreatedAt)
                .ThenByDescending(o => o.WorkOrderId),
            WorkOrderQueries.SortMembersDesc => orders
                .OrderByDescending(o => counts != null && counts.TryGetValue(o.WorkOrderId, out var c) ? c.Active : 0)
                .ThenByDescending(o => o.WorkOrderId),
            WorkOrderQueries.SortUnrepliedOldest => orders
                .OrderBy(o => o.LastReplyAt == null ? 0 : 1)
                .ThenBy(o => o.CreatedAt)
                .ThenByDescending(o => o.WorkOrderId),
            _ => orders.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.WorkOrderId)
        };
    }

    /// <summary>清單篩選用的使用者群組選項（回饋第 47 輪定案 48）：只列啟用中群組，依名稱不分大小寫排序</summary>
    /// <summary>未讀回覆的單數（側欄徽章用；能力由 controller 標註把關）。SQL 端 COUNT，不把單拉回記憶體</summary>
    public WorkOrderUnreadCountDto UnreadCount() => new() { Count = _orders.CountUnreadReplies(), Escalated = _orders.CountUnreadRepliesWithEscalated() };

    /// <summary>
    /// 記錄「管理者已看過這張單」：只有具 Assign 的使用者才寫入（ViewAll 唯讀者與處理人本人開詳情不算「已讀」）。
    /// 詳情的 GET 不寫任何東西，已讀由前端載入詳情成功後另行呼叫。單不存在 404、無權檢視 403。
    /// </summary>
    public void MarkSeen(long id)
    {
        var order = Authorize(id);
        if (!_currentUser.Has(Capability.Assign)) return;
        _orders.MarkAdminSeen(order.WorkOrderId, _clock.LocalNow());
    }

    public List<HandlerGroupOptionDto> ListHandlerGroups() =>
        _userGroups.GetAll()
            .Where(g => g.Active)
            .OrderBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HandlerGroupOptionDto { GroupId = g.GroupId, GroupName = g.GroupName, DispatchPool = g.DispatchPool })
            .ToList();

    // ── 詳情／成員／時間軸 ───────────────────────────────────────────────────

    public WorkOrderDetailDto Get(long id)
    {
        var order = Authorize(id);
        var users = _users.GetAll().ToDictionary(u => u.UserId);
        var allHosts = _hosts.GetAll();
        var counts = _orders.CountMembers(new[] { id }, WorkOrderQueries.DisabledHostKeys(allHosts));
        var rules = KnownIssueCatalog.ResolveRules(_rules);
        var groups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);

        var hostStats = BuildFacts(new[] { order }, WorkOrderQueries.DisabledHostKeys(allHosts), SeverityScope.WithFallback, out var activeLive);
        var dto = FillRow(new WorkOrderDetailDto(), order, users, counts, hostStats, rules, _exclusions.Current());
        // 單頭的案件編號清單跟成員清單一樣只給檢視者看得到的主機（受限檢視者不能靠它取得看不到的案件）
        var quietPool = activeLive;
        if (!_currentUser.Has(Capability.ViewAll) && order.HandlerId != _currentUser.UserId)
        {
            var visible = _visibility.GetVisibleHostIds();
            var visibleKeys = allHosts.Where(h => visible.Contains(h.HostId)).Select(h => HostNameKey.Of(h.HostName)).ToHashSet(StringComparer.Ordinal);
            quietPool = activeLive.Where(c => visibleKeys.Contains(HostNameKey.Of(c.HostName))).ToList();
        }
        dto.ObservationExpiredQuietCaseIds = QuietExpiredCaseIds(quietPool, allHosts);
        dto.Note = order.Note;
        dto.CreatedByAccount = order.CreatedByAccount;
        dto.ScopeGroups = order.ScopeGroupIds.Select(gid => new WorkOrderScopeGroupDto
        {
            GroupId = gid,
            GroupName = groups.TryGetValue(gid, out var g) ? g.GroupName : NameFormat.Deleted
        }).ToList();
        dto.ViewerIsHandler = order.HandlerId == _currentUser.UserId;
        dto.ViewerCanReply = order.HandlerId == _currentUser.UserId
            && _currentUser.Has(Capability.Handle) && order.ClosedAt == null;
        dto.ViewerIsPreviousHandler = order.PreviousHandlerId == _currentUser.UserId;
        dto.ViewerCanAssign = _currentUser.Has(Capability.Assign);
        dto.HandlerUnseenMemberCount = order.ClosedAt == null
            ? HandlerUnseenMembers(order, counts.TryGetValue(id, out var c) ? c.Active : 0, allHosts)
            : 0;
        return dto;
    }

    /// <summary>
    /// 處理人目前看不到的進行中成員台數：進行中且不在已停用主機上的成員（<paramref name="activeCount"/>）減去落在處理人可見主機上的。
    /// 讀取時計算、不存欄位；可見範圍用 <see cref="IVisibilityService.GetVisibleHostIdsFor"/>（群組授權、主機負責人、問題負責人，
    /// 不含案件授與——與夜間派工的候選人快照同一規則，「看不到」就是「新主機不會再續掛給他」）。
    /// </summary>
    private int HandlerUnseenMembers(WorkOrder order, int activeCount, IReadOnlyCollection<WebHost> hosts)
    {
        if (activeCount == 0) return 0;

        var visible = _visibility.GetVisibleHostIdsFor(order.HandlerId);
        var visibleKeys = hosts.Where(h => h.Active && visible.Contains(h.HostId))
            .Select(h => HostNameKey.Of(h.HostName)).Distinct().ToList();
        var seen = visibleKeys.Count == 0
            ? 0
            : _cases.QueryMembers(new WorkOrderMemberQuery
            {
                WorkOrderId = order.WorkOrderId, Status = WorkOrderQueries.StatusActive, HostNameKeys = visibleKeys, Page = 1, PageSize = 1
            }).Total;
        return Math.Max(0, activeCount - seen);
    }

    /// <summary>已停用主機的 host_name_key：徽章、處理人摘要、總覽台數共用的排除集合</summary>
    private IReadOnlyCollection<string> DisabledHostKeys() => WorkOrderQueries.DisabledHostKeys(_hosts.GetAll());

    /// <summary>成員端點一次最多比對的主機名個數（貼上清單的上限）</summary>
    public const int MaxMemberHostNames = 500;

    /// <summary>
    /// 成員分頁。hostNames＝主機名篩選（完全相符、不分大小寫，以 host_name_key 比對；最多 <see cref="MaxMemberHostNames"/> 個，超過回驗證錯誤）；
    /// 與可見範圍取交集：受限檢視者只會看到自己可見的主機，名稱篩選不能擴大範圍。
    /// </summary>
    public WorkOrderMemberPageDto Members(long id, string status, int page, int pageSize, IReadOnlyCollection<string>? hostNames = null)
    {
        var order = Authorize(id);
        if (!WorkOrderQueries.MemberStatuses.Contains(status))
            throw DomainException.Validation($"不支援的成員狀態篩選「{status}」。");

        (page, pageSize) = Paging.Normalize(page, pageSize < 1 ? DefaultMemberPageSize : pageSize, WorkOrderQueries.MaxMemberPageSize);

        var nameKeys = NormalizeHostNameFilter(hostNames);

        var hosts = _hosts.GetAll();
        var filtered = !_currentUser.Has(Capability.ViewAll) && order.HandlerId != _currentUser.UserId;

        IReadOnlyCollection<string>? hostKeys = nameKeys;
        if (filtered)
        {
            var visible = _visibility.GetVisibleHostIds();
            var visibleKeys = hosts.Where(h => visible.Contains(h.HostId)).Select(h => HostNameKey.Of(h.HostName)).Distinct().ToList();
            hostKeys = nameKeys == null ? visibleKeys : visibleKeys.Intersect(nameKeys, StringComparer.Ordinal).ToList();
        }

        var (items, total) = _cases.QueryMembers(new WorkOrderMemberQuery
        {
            WorkOrderId = id, Status = status, HostNameKeys = hostKeys, Page = page, PageSize = pageSize
        });

        var relatedOrdersByCase = RelatedPrtgOrders(order, items);

        // 有主機名篩選時隱藏數一律 0：以「篩選後未過濾數 − 可見數」回報，受限檢視者可拿它探測某台看不到的主機是否在單內。
        // 沒有名稱篩選時才回報（與單的整體成員數對得上，不指到特定主機）。
        var hidden = 0;
        if (filtered && nameKeys == null)
        {
            var unfiltered = _cases.QueryMembers(new WorkOrderMemberQuery
            {
                WorkOrderId = id, Status = status, HostNameKeys = null, Page = 1, PageSize = 1
            }).Total;
            hidden = unfiltered - total;
        }

        var disabledKeys = WorkOrderQueries.DisabledHostKeys(hosts).ToHashSet();
        var hostIdByKey = hosts
            .GroupBy(h => HostNameKey.Of(h.HostName))
            .ToDictionary(g => g.Key, g => g.First().HostId);
        var today = _clock.LocalToday();

        // 復發與第 N 次：本頁成員的前案一次取回（不設結案時間下限，「上次回覆」要看到上一件不論多久以前）；觀察期間再發生日數 K 同樣一次批次算
        var (recurrence, priorDismissals) = RecurrenceOf(items, boundedWindow: false);
        var priorAccounts = _users.GetAll().ToDictionary(u => u.UserId, u => u.Account);
        var observedDays = ObservationRecurrence.Compute(items, today, _issueHandlings, _records, hosts);

        var memberDtos = items.Select(c => new WorkOrderMemberDto
        {
                CaseId = c.CaseId,
                PrtgEvidence = c.PrtgEvidence,
                PrtgEvidenceParentStatus = EvidenceParentStatus(c),
                PrtgEvidenceRecordPath = c.PrtgEvidence != null && hostIdByKey.TryGetValue(HostNameKey.Of(c.HostName), out var evidenceHostId) &&
                    _visibility.GetVisibleHostIds().Contains(evidenceHostId) && !_visibility.IsCaseGrantOnly(evidenceHostId)
                    ? $"/records/{evidenceHostId}/{c.PrtgEvidence.RecordDate:yyyy-MM-dd}" : null,
                HostId = hostIdByKey.TryGetValue(HostNameKey.Of(c.HostName), out var hostId) ? hostId : null,
                HostName = c.HostName,
                HostDisabled = disabledKeys.Contains(HostNameKey.Of(c.HostName)),
                IssueKey = c.IssueKey,
                Status = c.Status,
                Note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note,
                DueDate = c.DueDate,
                Overdue = WorkOrderQueries.IsOverdue(c, today),
                FirstLinkedDate = c.FirstLinkedDate,
                LastLinkedDate = c.LastLinkedDate,
                DaySyncPending = c.DaySyncPending,
                Cancelled = c.Cancelled,
                ClosedAt = c.ClosedAt,
                RelatedWorkOrderId = relatedOrdersByCase.TryGetValue(c.CaseId, out var relatedId) ? relatedId : null,
                IsRecurrence = recurrence.GetValueOrDefault(c.CaseId, RecurrenceInfo.None).IsRecurrence,
                RecurrenceCount = recurrence.GetValueOrDefault(c.CaseId, RecurrenceInfo.None).Count,
                Occurrence = recurrence.GetValueOrDefault(c.CaseId, RecurrenceInfo.None).Occurrence,
                LastReply = LastReplyOf(recurrence.GetValueOrDefault(c.CaseId, RecurrenceInfo.None).Previous),
                PriorDismissal = priorDismissals.TryGetValue(c.CaseId, out var prior)
                    ? new PriorDismissalDto
                    {
                        ClosedAt = prior.ClosedAt!.Value, Status = prior.Status,
                        Account = prior.HandlerId is long hid && priorAccounts.TryGetValue(hid, out var account)
                            ? account : prior.DaySyncIntent?.ActorAccount
                    }
                    : null,
                ObservedRecurDays = observedDays.GetValueOrDefault(c.CaseId)
        }).ToList();

        AttachDiskTrendFindings(order, items, memberDtos);

        return new WorkOrderMemberPageDto
        {
            Items = memberDtos,
            Total = total,
            HiddenMemberCount = hidden,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>主機名篩選正規化成 host_name_key 集合：去空白、去重；沒有任何名稱＝不篩選（null）；超過上限回驗證錯誤</summary>
    private static List<string>? NormalizeHostNameFilter(IReadOnlyCollection<string>? hostNames)
    {
        var names = (hostNames ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        if (names.Count == 0) return null;
        if (names.Count > MaxMemberHostNames)
            throw DomainException.Validation($"主機名篩選最多 {MaxMemberHostNames} 個，目前 {names.Count} 個，請分批查詢。");
        return names.Select(HostNameKey.Of).Distinct(StringComparer.Ordinal).ToList();
    }

    private string? EvidenceParentStatus(IssueCase issueCase)
    {
        if (issueCase.PrtgEvidence == null) return null;
        var host = _hosts.FindByName(issueCase.HostName);
        if (host == null) return "host-deleted";
        var parent = _records.GetOne([new HostKey { HostId = host.HostId, HostName = host.HostName }], issueCase.PrtgEvidence.RecordDate);
        if (parent == null) return "deleted-or-not-retained";
        if (parent.DetailPruned) return "detail-pruned";
        return parent.TopIssues.Any(i => i.EventKey == issueCase.PrtgEvidence.EventKey) ? "available" : "reanalysed-without-finding";
    }

    /// <summary>Warning 進行中成員：從受限主機最近 30 日落盤資料補回已去重趨勢 finding 證據。</summary>
    private void AttachDiskTrendFindings(WorkOrder order, IReadOnlyList<IssueCase> cases, List<WorkOrderMemberDto> members)
    {
        if (order.ClosedAt != null || order.EventId != 0
            || !TryPrtgPairCode(order.SourceName, out var code) || code != "warning") return;

        var byCase = members.ToDictionary(m => m.CaseId, StringComparer.Ordinal);
        var targets = cases
            .Where(c => c.ClosedAt == null && !c.Cancelled && c.HandlerId == order.HandlerId
                && c.WorkOrderId == order.WorkOrderId
                && TryPrtgCaseSensor(c.IssueKey, "warning", out _))
            .Select(c => (Case: c, Member: byCase[c.CaseId], Sensor: PrtgSensor(c.IssueKey)))
            .Where(x => x.Member.HostId is > 0 && x.Sensor.Length > 0)
            .ToList();
        if (targets.Count == 0) return;

        var from = _clock.LocalToday().AddDays(-29);
        var findings = _records.QueryDiskTrendEvidence(
                targets.Select(x => (x.Member.HostId!.Value, x.Sensor)).Distinct().Take(100).ToArray(),
                from, _clock.LocalToday())
            .Where(x => x.HostId > 0 && targets.Any(t => t.Member.HostId == x.HostId
                && string.Equals(t.Sensor, x.SensorId, StringComparison.Ordinal)))
            .GroupBy(x => (x.HostId, Sensor: x.SensorId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.RecordDate).First());

        foreach (var target in targets)
        {
            if (!findings.TryGetValue((target.Member.HostId!.Value, target.Sensor), out var latest)) continue;

            target.Member.DiskTrend = new WorkOrderDiskTrendEvidenceDto
            {
                RecordDate = latest.RecordDate,
                SensorId = target.Sensor,
                Detail = latest.Detail
            };
        }
    }

    private static string PrtgSensor(string issueKey)
    {
        var signature = IssueSignatureKey.TryParseFull(issueKey);
        if (signature == null) return string.Empty;
        var parts = signature.Value.EventKey.Split(':');
        return parts.Length >= 3 ? parts[2] : string.Empty;
    }

    /// <summary>
    /// 找出本頁案件在同主機、同 sensor 的 Warning／disk_free_trend 配對。只讀本頁可見主機，
    /// 只連結同處理人的活動單；避免從關聯欄位洩漏隱藏主機或其他處理人的單號。
    /// </summary>
    private Dictionary<string, long> RelatedPrtgOrders(WorkOrder order, IReadOnlyList<IssueCase> pageCases)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (order.ClosedAt != null || order.EventId != 0 || pageCases.Count == 0
            || !TryPrtgPairCode(order.SourceName, out var orderCode))
            return result;

        var counterpartCode = orderCode == "warning" ? "disk_free_trend" : "warning";
        var activeOrders = _orders.GetActiveByHandler(order.HandlerId)
            .Where(o => o.EventId == 0
                && TryPrtgPairCode(o.SourceName, out var candidateCode)
                && candidateCode == counterpartCode)
            .ToDictionary(o => o.WorkOrderId);
        if (activeOrders.Count == 0) return result;

        var hostNames = pageCases.Select(c => c.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var activeCases = _cases.GetOpenMany(hostNames)
            .Where(c => c.HandlerId == order.HandlerId
                && c.WorkOrderId.HasValue && activeOrders.ContainsKey(c.WorkOrderId.Value))
            .ToList();

        foreach (var current in pageCases)
        {
            if (current.ClosedAt != null || current.HandlerId != order.HandlerId || current.WorkOrderId != order.WorkOrderId
                || !TryPrtgCaseSensor(current.IssueKey, orderCode, out var sensorId)) continue;

            var match = activeCases.FirstOrDefault(candidate =>
                string.Equals(candidate.HostName, current.HostName, StringComparison.OrdinalIgnoreCase)
                && TryPrtgCaseSensor(candidate.IssueKey, counterpartCode, out var otherSensorId)
                && string.Equals(sensorId, otherSensorId, StringComparison.Ordinal));
            if (match?.WorkOrderId is long relatedId) result[current.CaseId] = relatedId;
        }

        return result;
    }

    private static bool TryPrtgPairCode(string? source, out string code)
    {
        code = string.Empty;
        if (!PrtgFindingMapper.TryGetRuleCode(source, out var parsed)) return false;
        if (!string.Equals(parsed, "warning", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed, "disk_free_trend", StringComparison.OrdinalIgnoreCase)) return false;
        code = parsed.ToLowerInvariant();
        return true;
    }

    private static bool TryPrtgCaseSensor(string? issueKey, string expectedCode, out string sensorId)
    {
        sensorId = string.Empty;
        var signature = IssueSignatureKey.TryParseFull(issueKey);
        if (signature == null
            || !string.Equals(signature.Value.LogName, PrtgFindingMapper.PrtgLogName, StringComparison.OrdinalIgnoreCase)
            || signature.Value.EventId != 0 || signature.Value.EntryType != LogEntryType.Warning
            || !string.Equals(signature.Value.Source, $"PRTG:{expectedCode}", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = signature.Value.EventKey.Split(':');
        if (parts.Length < 3 || !string.Equals(parts[0], "prtg", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[1], expectedCode, StringComparison.OrdinalIgnoreCase) || parts[2].Length == 0) return false;
        sensorId = parts[2];
        return true;
    }

    public List<WorkOrderEventDto> Timeline(long id)
    {
        Authorize(id);
        var users = _users.GetAll().ToDictionary(u => u.UserId);

        return _orders.ListEvents(id)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.EventId)
            .Select(e => new WorkOrderEventDto
            {
                Action = e.Action,
                ActionText = WorkOrderTextHelpers.ActionText(e.Action),
                ActorName = e.ActorId.HasValue ? NameOf(users, e.ActorId.Value) : SystemActorName,
                MemberDelta = e.MemberDelta,
                Note = DisplayNote(e.Action, e.Note, users),
                CreatedAt = e.CreatedAt
            })
            .ToList();
    }

    /// <summary>改派事件的 note 存的是「原處理人 id→新處理人 id」；對外換成名稱（含已停用者），前端不必再查人</summary>
    private static readonly Regex ReassignNote = new(@"^(\d+)→(\d+)$", RegexOptions.Compiled);

    private string? DisplayNote(string action, string? note, Dictionary<long, WebUser> users)
    {
        if (action != WorkOrderEventActions.Reassigned || note == null) return note;
        var m = ReassignNote.Match(note);
        if (!m.Success) return note;
        return $"{NameOf(users, long.Parse(m.Groups[1].Value))} → {NameOf(users, long.Parse(m.Groups[2].Value))}";
    }

    // ── 內部 ────────────────────────────────────────────────────────────────

    /// <summary>逐單授權的唯一一處：不存在 404；非 Assign／ViewAll 且非處理人（含最近一次被改派走的原處理人，唯讀）403</summary>
    private WorkOrder Authorize(long id)
    {
        var order = _orders.Get(id) ?? throw DomainException.NotFound("找不到這張交辦單。");
        if (_currentUser.Has(Capability.Assign) || _currentUser.Has(Capability.ViewAll)) return order;
        if (order.HandlerId == _currentUser.UserId) return order;
        if (order.PreviousHandlerId == _currentUser.UserId) return order;
        throw DomainException.Forbidden("沒有檢視這張交辦單的權限。");
    }

    private T FillRow<T>(T dto, WorkOrder o, Dictionary<long, WebUser> users,
        Dictionary<long, WorkOrderMemberCounts> counts, Dictionary<long, OrderFacts> hostStats, List<KnownIssueRule> rules,
        IssueExclusion exclusion) where T : WorkOrderRowDto
    {
        users.TryGetValue(o.HandlerId, out var handler);
        var c = counts.TryGetValue(o.WorkOrderId, out var found) ? found : new WorkOrderMemberCounts();

        dto.WorkOrderId = o.WorkOrderId;
        dto.Source = o.SourceName;
        dto.EventId = o.EventId;
        dto.IssueLabel = PrtgFindingMapper.DisplayStoredLabel(o.IssueLabel);
        dto.PlainExplanation = o.SourceName != null && o.EventId != null
            ? KnownIssueCatalog.PlainExplanationFor(rules, o.SourceName, o.EventId.Value)
            : null;
        dto.HandlerId = o.HandlerId;
        dto.HandlerName = NameOf(users, o.HandlerId);
        dto.HandlerActive = handler?.Active == true;
        dto.HandlerPaused = handler?.DispatchPaused == true;
        dto.Origin = o.Origin;
        dto.ScopeKind = o.ScopeKind;
        dto.AutoAttach = o.AutoAttach;
        if (o.ClosedAt == null && o.ContinuationStoppedReason != null)
        {
            dto.ContinuationStopped = true;
            dto.ContinuationStoppedReason = o.ContinuationStoppedReason;
            dto.ContinuationStoppedLabel = ContinuationStopReasons.LabelOf(o.ContinuationStoppedReason);
            dto.ContinuationStoppedAt = o.ContinuationStoppedAt;
        }
        dto.Counts = new WorkOrderCountsDto
        {
            Total = c.Total, Active = c.Active, ActiveOnDisabledHosts = c.ActiveOnDisabledHosts, Closed = c.Closed, InProgress = c.InProgress, Observing = c.Observing,
            Open = c.Open, Escalated = c.Escalated, Overdue = c.Overdue, DaySyncPending = c.DaySyncPending
        };
        var hs = hostStats.GetValueOrDefault(o.WorkOrderId);
        dto.ActiveHosts = hs.Active;
        dto.TotalHosts = hs.Total;
        dto.RecurrenceHosts = hs.Recurrence;
        dto.PriorDismissalHosts = hs.PriorDismissal;
        dto.DismissalReleases = o.SourceName != null && o.EventId != null
            && DismissalRelease.AppliesTo(rules, o.SourceName, o.EventId.Value);
        dto.ObservingHosts = hs.Observing;
        dto.ObservationExpiredHosts = hs.Expired;
        dto.TodoHosts = hs.Todo;
        dto.NewTodayHosts = hs.NewToday;
        dto.ReturnedMembers = hs.ReturnedMembers;
        dto.Severity = hs.Severity?.ToString();
        dto.Critical = hs.Critical;
        dto.DueDate = o.DueDate;
        dto.CreatedAt = o.CreatedAt;
        dto.LastAppendedAt = o.LastAppendedAt;
        dto.LastReplyAt = o.LastReplyAt;
        dto.UnrepliedDays = o.LastReplyAt == null && o.ClosedAt == null
            ? (_clock.LocalToday() - o.CreatedAt.Date).Days
            : null;
        dto.UnreadReply = o.HasUnreadReply;
        dto.ClosedAt = o.ClosedAt;
        dto.ClosedReason = o.ClosedReason;

        dto.Paused = IsPaused(o, exclusion);
        if (o.ClosedAt == null && o.SourceName != null && o.EventId != null)
        {
            var spans = SpansOf(exclusion, o.SourceName, o.EventId.Value);
            if (dto.Paused)
                dto.MutedUntil = spans.FirstOrDefault(s => MuteInterval.Covers(s.From, s.To, exclusion.Today))?.To.ToString("yyyy-MM-dd");
            else
                dto.ResumedFromMuteAt = ResumedAt(spans, exclusion.Today)?.ToString("yyyy-MM-dd");
        }
        return dto;
    }

    /// <summary>
    /// 一張單讀取時推導的列欄位（單頭、清單列、排序與標記篩選共用的唯一一份）。
    /// 主機統計類（Active…Todo、NewToday）是不重複主機數；ReturnedMembers、OverdueMembers、DueSoonMembers 是成員（案件）數。
    /// </summary>
    /// <param name="EarliestDue">進行中且處理中成員的最早期限；沒有時退回單本身的期限（進行中的單）；都沒有為 null（排序時排最後）</param>
    /// <param name="Severity">單內進行中成員最近一次出現的最高嚴重度（已正規化）；推導不到為 null</param>
    private readonly record struct OrderFacts(
        int Active, int Total, int Recurrence, int PriorDismissal, int Observing, int Expired, int Todo,
        int NewToday, int ReturnedMembers, int OverdueMembers, int DueSoonMembers, DateTime? EarliestDue,
        IssueSeverity? Severity, bool Critical);

    /// <summary>嚴重度推導的範圍：不算／只算有進行中成員的單（全體排序用）／連「沒有進行中成員」的單也補全部成員最後已知值（本頁與單頭）</summary>
    private enum SeverityScope { None, ActiveOnly, WithFallback }

    /// <summary>
    /// 各單的推導欄位：歷來主機數一句 GROUP BY、進行中成員一次取回（精簡欄位）、前案候選一次取回、退回／回覆事件一次取回、
    /// 嚴重度的最近出現點一次取回，再於記憶體以 <see cref="IssueRecurrence"/>、<see cref="IssueHandlingStatuses"/> 與
    /// <see cref="WorkOrderQueries"/> 的共用判定彙整——查詢次數不隨成員數增長。
    /// 停用主機上的成員不計入進行中相關的數字（與 <see cref="WorkOrderCountsDto"/> 同口徑），歷來主機數照舊含全部。
    /// <paramref name="activeLive"/> 回傳參與統計的進行中成員（供單頭另外算觀察到期的成員）。
    /// </summary>
    private Dictionary<long, OrderFacts> BuildFacts(
        IReadOnlyCollection<WorkOrder> orders, IReadOnlyCollection<string> disabledKeys, SeverityScope severityScope,
        out List<IssueCase> activeLive)
    {
        activeLive = new List<IssueCase>();
        var result = new Dictionary<long, OrderFacts>();
        if (orders.Count == 0) return result;

        var totals = _cases.CountDistinctHosts(orders.Select(o => o.WorkOrderId).ToList());
        var activeIds = orders.Where(o => o.ClosedAt == null).Select(o => o.WorkOrderId).ToList();
        var disabled = disabledKeys.ToHashSet(StringComparer.Ordinal);
        if (activeIds.Count > 0)
            activeLive = _cases.GetActiveMemberSummaries(activeIds)
                .Where(c => c.WorkOrderId != null && !disabled.Contains(HostNameKey.Of(c.HostName)))
                .ToList();

        var (recurrence, priorDismissals) = RecurrenceOf(activeLive, boundedWindow: true);
        var today = _clock.LocalToday();
        var byOrder = activeLive.GroupBy(c => c.WorkOrderId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var returned = ReturnedMembersOf(orders, byOrder);
        var severity = severityScope == SeverityScope.None
            ? new Dictionary<long, (IssueSeverity Severity, bool Critical)>()
            : SeverityOf(orders, byOrder, severityScope == SeverityScope.WithFallback);
        foreach (var o in orders)
        {
            var members = byOrder.GetValueOrDefault(o.WorkOrderId) ?? new List<IssueCase>();
            int Hosts(Func<IssueCase, bool> match) =>
                members.Where(match).Select(c => HostNameKey.Of(c.HostName)).Distinct(StringComparer.Ordinal).Count();
            var sev = severity.TryGetValue(o.WorkOrderId, out var found) ? found : ((IssueSeverity Severity, bool Critical)?)null;
            result[o.WorkOrderId] = new OrderFacts(
                Active: Hosts(_ => true),
                Total: totals.GetValueOrDefault(o.WorkOrderId),
                Recurrence: Hosts(c => recurrence.GetValueOrDefault(c.CaseId, RecurrenceInfo.None).IsRecurrence),
                PriorDismissal: Hosts(c => priorDismissals.ContainsKey(c.CaseId)),
                Observing: Hosts(c => IssueHandlingStatuses.IsObservationActive(c.Status, c.DueDate, today)),
                Expired: Hosts(c => IssueHandlingStatuses.IsObservationExpired(c.Status, c.DueDate, today)),
                Todo: Hosts(c => WorkOrderQueries.IsTodoMember(c, today)),
                NewToday: Hosts(c => c.CreatedAt.Date == today.Date),
                ReturnedMembers: returned.GetValueOrDefault(o.WorkOrderId),
                OverdueMembers: members.Count(c => WorkOrderQueries.IsOverdue(c, today)),
                DueSoonMembers: members.Count(c => WorkOrderQueries.IsDueSoon(c, today)),
                EarliestDue: EarliestDueOf(o, members),
                Severity: sev?.Severity, Critical: sev?.Critical ?? false);
        }
        return result;
    }

    /// <summary>最早期限：進行中且處理中成員的最早期限；沒有就退回進行中單本身的期限（單結案了為 null）</summary>
    private static DateTime? EarliestDueOf(WorkOrder o, List<IssueCase> activeMembers)
    {
        var memberDue = activeMembers
            .Where(c => c.Status == IssueHandlingStatuses.InProgress && c.DueDate != null)
            .Select(c => (DateTime?)c.DueDate!.Value.Date)
            .Min();
        return memberDue ?? (o.ClosedAt == null ? o.DueDate?.Date : null);
    }

    /// <summary>
    /// 被退回的成員數（只有進行中的單有）：這張單**最近一筆**退回事件之後沒有處理人回覆事件，才算被退回；
    /// 數量＝進行中成員中仍是處理中、說明是該次退回說明的（退回時成員說明被設成退回說明；之後夜間續掛新增的成員說明不同，不算）。
    /// 事件一次取回（動作只取退回與回覆兩種）；兩筆事件同一時間點時以事件序比大小。
    /// </summary>
    private Dictionary<long, int> ReturnedMembersOf(IReadOnlyCollection<WorkOrder> orders, Dictionary<long, List<IssueCase>> activeByOrder)
    {
        var result = new Dictionary<long, int>();
        var ids = orders.Where(o => o.ClosedAt == null).Select(o => o.WorkOrderId).ToList();
        if (ids.Count == 0) return result;

        var events = _orders.ListEventsOf(ids, new[] { WorkOrderEventActions.Returned, WorkOrderEventActions.Replied });
        foreach (var (orderId, list) in events)
        {
            var lastReturn = list.LastOrDefault(e => e.Action == WorkOrderEventActions.Returned);
            if (lastReturn == null) continue;
            var repliedAfter = list.Any(e => e.Action == WorkOrderEventActions.Replied
                && (e.CreatedAt > lastReturn.CreatedAt || (e.CreatedAt == lastReturn.CreatedAt && e.EventId > lastReturn.EventId)));
            if (repliedAfter) continue;

            var count = (activeByOrder.GetValueOrDefault(orderId) ?? new List<IssueCase>())
                .Count(c => c.Status == IssueHandlingStatuses.InProgress && SameReturnNote(c.Note, lastReturn.Note));
            if (count > 0) result[orderId] = count;
        }
        return result;
    }

    /// <summary>成員說明是不是某次退回說明（事件說明超長時被截斷，成員說明為其延伸也算）</summary>
    private static bool SameReturnNote(string? memberNote, string? eventNote) =>
        memberNote != null && eventNote != null
        && (string.Equals(memberNote, eventNote, StringComparison.Ordinal)
            || (memberNote.Length > eventNote.Length && memberNote.StartsWith(eventNote, StringComparison.Ordinal)));

    /// <summary>
    /// 嚴重度與重大旗標：成員（主機, 完整簽章）最近一次出現點（<see cref="IIssueAggregateQuery.LatestOccurrences"/>，所有單合併成一次查詢）
    /// 的嚴重度取最大（舊資料 Critical 先正規化為 High），重大旗標＝任何一個命中；進行中成員優先，
    /// <paramref name="withFallback"/> 為 true 時沒有進行中成員的單改用全部成員（含已結案）的最後已知值。
    /// 沒有注入彙總查詢、成員對不到主機或沒有出現點者不出現在結果裡（畫面顯示「—」）。
    /// </summary>
    private Dictionary<long, (IssueSeverity Severity, bool Critical)> SeverityOf(
        IReadOnlyCollection<WorkOrder> orders, Dictionary<long, List<IssueCase>> activeByOrder, bool withFallback)
    {
        var result = new Dictionary<long, (IssueSeverity, bool)>();
        if (_aggregates == null || orders.Count == 0) return result;

        var membersByOrder = new Dictionary<long, List<IssueCase>>();
        var fallbackIds = new List<long>();
        foreach (var o in orders)
        {
            if (activeByOrder.TryGetValue(o.WorkOrderId, out var active) && active.Count > 0) membersByOrder[o.WorkOrderId] = active;
            else if (withFallback) fallbackIds.Add(o.WorkOrderId);
        }
        if (fallbackIds.Count > 0)
        {
            foreach (var g in _cases.GetMemberSummaries(fallbackIds).Where(c => c.WorkOrderId != null && !c.Cancelled).GroupBy(c => c.WorkOrderId!.Value))
                membersByOrder[g.Key] = g.ToList();
        }

        var all = membersByOrder.Values.SelectMany(x => x).Where(c => c.SourceName != null && c.EventId != null).ToList();
        if (all.Count == 0) return result;

        var hostIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var h in _hosts.GetAll()) hostIdByKey.TryAdd(HostNameKey.Of(h.HostName), h.HostId);
        long HostIdOf(IssueCase c) => hostIdByKey.GetValueOrDefault(HostNameKey.Of(c.HostName));

        var hostIds = all.Select(HostIdOf).Where(id => id != 0).Distinct().ToList();
        if (hostIds.Count == 0) return result;
        var issues = all.Select(c => (c.SourceName!, c.EventId!.Value)).Distinct().ToList();
        // 範圍從最早的成員建立日往前一個月到今天：只要最近一次，範圍太窄會漏掉建單前就開始出現的問題
        var from = all.Min(c => c.CreatedAt).Date.AddDays(-30);
        // 不套用「可見嚴重度」：可見嚴重度是全站顯示政策（系統設定，不分檢視者），管的是「哪些問題會被列進問題清單」；
        // 交辦單是已經建立的案件，名稱、成員數、期限與處理優先序（P-1）本來就不受它限制，嚴重度欄與處理優先序必須同一個來源，
        // 若這裡把被隱藏的等級視為推導不到，清單會顯示「—」卻仍照該等級排序／預設期限。建單當下的母體已由 ResolveMembers 套用可見嚴重度。
        // 也不依檢視者而異，所以不會把 A 看得到的東西端給 B。
        var occurrences = _aggregates.LatestOccurrences(IssueExclusion.None, issues, from, _clock.LocalToday(), hostIds);
        var byHost = new Dictionary<long, Dictionary<string, HostIssueOccurrence>>();
        foreach (var occ in occurrences)
        {
            if (!byHost.TryGetValue(occ.HostId, out var inner))
                byHost[occ.HostId] = inner = new Dictionary<string, HostIssueOccurrence>(IssueSignatureKeyComparer.Instance);
            inner.TryAdd(occ.IssueKey, occ);
        }

        foreach (var (orderId, members) in membersByOrder)
        {
            int? maxRank = null;
            var critical = false;
            foreach (var c in members)
            {
                if (!byHost.TryGetValue(HostIdOf(c), out var inner) || !inner.TryGetValue(c.IssueKey, out var occ)) continue;
                var rank = LegacySeverityRank.Normalize(occ.SeverityRank);
                if (maxRank == null || rank > maxRank) maxRank = rank;
                critical |= occ.ElevatesDayRisk;
            }
            if (maxRank != null) result[orderId] = (LegacySeverityRank.ToSeverity(maxRank.Value), critical);
        }
        return result;
    }

    /// <summary>本頁的列補嚴重度：全體排序只用進行中成員，沒有進行中成員的單在這裡改取全部成員的最後已知值</summary>
    private void AddSeverityFallback(IReadOnlyCollection<WorkOrder> pageOrders, Dictionary<long, OrderFacts> facts)
    {
        var missing = pageOrders.Where(o => facts[o.WorkOrderId].Severity == null && facts[o.WorkOrderId].Active == 0).ToList();
        if (missing.Count == 0) return;
        foreach (var (orderId, sev) in SeverityOf(missing, new Dictionary<long, List<IssueCase>>(), withFallback: true))
            facts[orderId] = facts[orderId] with { Severity = sev.Severity, Critical = sev.Critical };
    }

    /// <summary>
    /// 對 <paramref name="targets"/> 的復發判定（<see cref="IssueRecurrence.Evaluate"/>）與「先前判定不處理」推導
    /// （<see cref="DismissalRelease.PriorDismissalsOf"/>，與派工閘門同一個解除條件）：同主機同問題的前案候選只取一次，兩個判定共用。
    /// <paramref name="boundedWindow"/>＝true 時前案只取結案日不早於「最早一件的建立日往前一個回看窗」與「不再打擾生效日」兩者較早者——
    /// 復發與次數只看回看窗內，更早的前案不影響結果（清單統計用）；false＝全取（成員清單要「上次回覆」與第 N 次）。
    /// </summary>
    private (Dictionary<string, RecurrenceInfo> Recurrence, Dictionary<string, IssueCase> PriorDismissals) RecurrenceOf(
        IReadOnlyCollection<IssueCase> targets, bool boundedWindow)
    {
        if (targets.Count == 0)
            return (new Dictionary<string, RecurrenceInfo>(StringComparer.Ordinal), new Dictionary<string, IssueCase>(StringComparer.Ordinal));

        var hostNames = targets.Select(c => c.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var issues = targets.Where(c => c.SourceName != null && c.EventId != null)
            .Select(c => (c.SourceName!, c.EventId!.Value)).Distinct().ToList();
        DateTime? since = boundedWindow
            ? new[] { targets.Min(c => c.CreatedAt).Date.AddDays(-IssueRecurrence.WindowDays), DismissalRelease.EffectiveFrom }.Min()
            : null;
        var history = _cases.GetClosedHistory(hostNames, issues, since);
        var releaseRules = DismissalRelease.LookupFrom(KnownIssueCatalog.ResolveRules(_rules));
        return (IssueRecurrence.Evaluate(targets, history), DismissalRelease.PriorDismissalsOf(targets, history, releaseRules));
    }

    private static WorkOrderLastReplyDto? LastReplyOf(IssueCase? previous) =>
        previous?.ClosedAt == null
            ? null
            : new WorkOrderLastReplyDto
            {
                Date = previous.ClosedAt.Value, Status = previous.Status,
                Note = string.IsNullOrWhiteSpace(previous.Note) ? null : previous.Note
            };

    /// <summary>觀察到期、且觀察期間確定沒有再發生（K 為 0）的進行中成員編號；K 為無資料（null）的不列</summary>
    private List<string> QuietExpiredCaseIds(IReadOnlyCollection<IssueCase> activeLive, IReadOnlyCollection<WebHost> hosts)
    {
        var today = _clock.LocalToday();
        var expired = activeLive.Where(c => IssueHandlingStatuses.IsObservationExpired(c.Status, c.DueDate, today)).ToList();
        if (expired.Count == 0) return new List<string>();

        var days = ObservationRecurrence.Compute(expired, today, _issueHandlings, _records, hosts);
        return expired
            .Where(c => days.TryGetValue(c.CaseId, out var k) && k == 0)
            .OrderBy(c => HostNameKey.Of(c.HostName), StringComparer.Ordinal).ThenBy(c => c.CaseId, StringComparer.Ordinal)
            .Select(c => c.CaseId)
            .ToList();
    }

    /// <summary>暫停判定的唯一一份：進行中、問題欄非 null，且問題目前靜音中（SQL 端對應 <c>WorkOrderQuery.PausedKeys</c>）</summary>
    private static bool IsPaused(WorkOrder o, IssueExclusion exclusion) =>
        o.ClosedAt == null && o.SourceName != null && o.EventId != null
        && exclusion.IsCurrentlyMuted(o.SourceName, o.EventId.Value);

    private static List<MuteSpan> SpansOf(IssueExclusion exclusion, string source, int eventId)
    {
        var key = WorkOrderIssueKey.SourceKeyOf(source);
        return exclusion.Spans.Where(s => s.EventId == eventId && s.SourceKey == key).ToList();
    }

    /// <summary>最近一個已結束區間的迄日落在 [今天−7, 今天−1] 時回傳迄日＋1 天；呼叫端須先確認問題不在目前靜音中</summary>
    private static DateTime? ResumedAt(IEnumerable<MuteSpan> spans, DateTime today)
    {
        var ended = spans.Where(s => s.To.Date < today.Date).Select(s => (DateTime?)s.To.Date).Max();
        if (ended == null || ended.Value < today.Date.AddDays(-ResumedWindowDays)) return null;
        return ended.Value.AddDays(1);
    }

    /// <summary>「靜音到期、近 7 日恢復」的問題組合鍵（供 <c>WorkOrderQuery.OnlyKeys</c> 在 SQL 端篩選）</summary>
    private static List<string> ResumedFromMuteKeys(IssueExclusion exclusion) =>
        exclusion.Spans
            .GroupBy(s => (s.SourceKey, s.EventId))
            .Where(g => !exclusion.CurrentlyMuted.Contains(g.Key) && ResumedAt(g, exclusion.Today) != null)
            .Select(g => IssueExclusion.CompositeKey(g.Key.SourceKey, g.Key.EventId))
            .ToList();

    private string NameOf(Dictionary<long, WebUser> users, long userId) =>
        NameFormat.UserLabel(_displayNames, users.GetValueOrDefault(userId));
}
