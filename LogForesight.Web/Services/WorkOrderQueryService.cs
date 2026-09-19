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
    private const string DeletedName = "（已刪除）";
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

    /// <summary>「靜音到期恢復」的回看天數：最近一個已結束區間的迄日落在 [今天−7, 今天−1]</summary>
    private const int ResumedWindowDays = 7;

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
        IUserGroupStore userGroups)
    {
        _userGroups = userGroups;
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
        // 檢視者自己的可見主機數：只用在本人頁的空狀態分流（側欄徽章不需要，不算）
        // 只經案件授與看得到的主機也算「有授權」：只算群組授權會把這種人誤判成「尚未被授權任何主機」
        dto.VisibleHostCount = _visibility.GetVisibleHostIds().Count + _visibility.GetCaseGrantHostNames().Count;
        return dto;
    }

    /// <summary>
    /// 側欄徽章（目前使用者）：與 <see cref="HandlerSummary"/> 同一份 store 摘要。
    /// ServerAdmin（UserId &lt;= 0）不會是任何單的處理人，回全 0、不擲。
    /// </summary>
    public HandlerSummaryDto MyBadge() =>
        _currentUser.UserId <= 0 ? new HandlerSummaryDto() : SummaryOf(_currentUser.UserId);

    private HandlerSummaryDto SummaryOf(long userId)
    {
        var s = _orders.HandlerSummary(userId, _exclusions.Current().CurrentlyMutedCompositeKeys);
        return new HandlerSummaryDto
        {
            ActiveWorkOrders = s.ActiveWorkOrders, ActiveMembers = s.ActiveMembers,
            OverdueMembers = s.OverdueMembers, UnrepliedWorkOrders = s.UnrepliedWorkOrders,
            PausedWorkOrders = s.PausedWorkOrders
        };
    }

    /// <summary>處理人視角的授權：本人，或具 Assign／ViewAll；否則 403</summary>
    private void AuthorizeHandlerView(long userId)
    {
        if (userId == _currentUser.UserId) return;
        if (_currentUser.Has(Capability.Assign) || _currentUser.Has(Capability.ViewAll)) return;
        throw DomainException.Forbidden("沒有檢視這位處理人交辦清單的權限。");
    }

    /// <summary>清單查詢與組裝的唯一一份：交辦單一次、本頁計數一次、使用者一次</summary>
    private WorkOrderListDto QueryList(WorkOrderListRequest req, string defaultPausedMode, IssueExclusion exclusion)
    {
        if (!WorkOrderQueries.OrderStatuses.Contains(req.Status))
            throw DomainException.Validation($"不支援的狀態篩選「{req.Status}」。");
        if (!WorkOrderQueries.Sorts.Contains(req.Sort))
            throw DomainException.Validation($"不支援的排序「{req.Sort}」。");
        var pausedMode = string.IsNullOrWhiteSpace(req.Paused) ? defaultPausedMode : req.Paused.Trim();
        if (!WorkOrderQueries.PausedModes.Contains(pausedMode))
            throw DomainException.Validation($"不支援的暫停篩選「{req.Paused}」。");

        var page = Math.Max(1, req.Page);
        var pageSize = req.PageSize < 1 ? DefaultPageSize : Math.Min(req.PageSize, WorkOrderQueries.MaxOrderPageSize);

        var users = _users.GetAll().ToDictionary(u => u.UserId);

        IReadOnlyCollection<long>? handlerIds = req.HandlerId.HasValue ? new[] { req.HandlerId.Value } : null;
        if (req.HandlerInactive)
        {
            var inactive = users.Values.Where(u => !u.Active).Select(u => u.UserId).ToHashSet();
            handlerIds = handlerIds == null ? inactive : handlerIds.Where(inactive.Contains).ToList();
        }
        if (req.GroupId.HasValue)
        {
            // 回饋第 47 輪定案 48：處理人屬於該使用者群組；群組不存在或無成員＝空集合＝查無
            var groupId = req.GroupId.Value;
            var members = users.Values.Where(u => u.GroupIds.Contains(groupId)).Select(u => u.UserId).ToHashSet();
            handlerIds = handlerIds == null ? members : handlerIds.Where(members.Contains).ToList();
        }

        var result = _orders.QueryOrders(new WorkOrderQuery
        {
            HandlerIds = handlerIds,
            Source = string.IsNullOrWhiteSpace(req.Source) ? null : req.Source.Trim(),
            EventId = req.EventId,
            Status = req.Status,
            Sort = req.Sort,
            Page = page,
            PageSize = pageSize,
            PausedKeys = exclusion.CurrentlyMutedCompositeKeys,
            PausedMode = pausedMode,
            OnlyKeys = req.ResumedFromMute ? ResumedFromMuteKeys(exclusion) : null
        });

        var counts = _orders.CountMembers(result.Items.Select(o => o.WorkOrderId).ToList());
        var rules = KnownIssueCatalog.ResolveRules(_rules);

        return new WorkOrderListDto
        {
            Items = result.Items.Select(o => FillRow(new WorkOrderRowDto(), o, users, counts, rules, exclusion)).ToList(),
            Total = result.Total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>清單篩選用的使用者群組選項（回饋第 47 輪定案 48）：只列啟用中群組，依名稱不分大小寫排序</summary>
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
        var counts = _orders.CountMembers(new[] { id });
        var rules = KnownIssueCatalog.ResolveRules(_rules);
        var groups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);

        var dto = FillRow(new WorkOrderDetailDto(), order, users, counts, rules, _exclusions.Current());
        dto.Note = order.Note;
        dto.CreatedByAccount = order.CreatedByAccount;
        dto.ScopeGroups = order.ScopeGroupIds.Select(gid => new WorkOrderScopeGroupDto
        {
            GroupId = gid,
            GroupName = groups.TryGetValue(gid, out var g) ? g.GroupName : DeletedName
        }).ToList();
        dto.ViewerIsHandler = order.HandlerId == _currentUser.UserId;
        dto.ViewerCanAssign = _currentUser.Has(Capability.Assign);
        return dto;
    }

    public WorkOrderMemberPageDto Members(long id, string status, int page, int pageSize)
    {
        var order = Authorize(id);
        if (!WorkOrderQueries.MemberStatuses.Contains(status))
            throw DomainException.Validation($"不支援的成員狀態篩選「{status}」。");

        page = Math.Max(1, page);
        pageSize = pageSize < 1 ? DefaultMemberPageSize : Math.Min(pageSize, WorkOrderQueries.MaxMemberPageSize);

        var hosts = _hosts.GetAll();
        var filtered = !_currentUser.Has(Capability.ViewAll) && order.HandlerId != _currentUser.UserId;

        IReadOnlyCollection<string>? hostKeys = null;
        if (filtered)
        {
            var visible = _visibility.GetVisibleHostIds();
            hostKeys = hosts.Where(h => visible.Contains(h.HostId)).Select(h => HostNameKey.Of(h.HostName)).Distinct().ToList();
        }

        var (items, total) = _cases.QueryMembers(new WorkOrderMemberQuery
        {
            WorkOrderId = id, Status = status, HostNameKeys = hostKeys, Page = page, PageSize = pageSize
        });

        var hidden = 0;
        if (filtered)
        {
            var unfiltered = _cases.QueryMembers(new WorkOrderMemberQuery
            {
                WorkOrderId = id, Status = status, HostNameKeys = null, Page = 1, PageSize = 1
            }).Total;
            hidden = unfiltered - total;
        }

        var hostIdByKey = hosts
            .GroupBy(h => HostNameKey.Of(h.HostName))
            .ToDictionary(g => g.Key, g => g.First().HostId);
        var today = DateTime.Today;

        return new WorkOrderMemberPageDto
        {
            Items = items.Select(c => new WorkOrderMemberDto
            {
                CaseId = c.CaseId,
                HostId = hostIdByKey.TryGetValue(HostNameKey.Of(c.HostName), out var hostId) ? hostId : null,
                HostName = c.HostName,
                IssueKey = c.IssueKey,
                Status = c.Status,
                DueDate = c.DueDate,
                Overdue = WorkOrderQueries.IsOverdue(c, today),
                FirstLinkedDate = c.FirstLinkedDate,
                LastLinkedDate = c.LastLinkedDate,
                DaySyncPending = c.DaySyncPending,
                Cancelled = c.Cancelled,
                ClosedAt = c.ClosedAt
            }).ToList(),
            Total = total,
            HiddenMemberCount = hidden,
            Page = page,
            PageSize = pageSize
        };
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
                Note = e.Note,
                CreatedAt = e.CreatedAt
            })
            .ToList();
    }

    // ── 內部 ────────────────────────────────────────────────────────────────

    /// <summary>逐單授權的唯一一處：不存在 404；非 Assign／ViewAll 且非處理人 403</summary>
    private WorkOrder Authorize(long id)
    {
        var order = _orders.Get(id) ?? throw DomainException.NotFound("找不到這張交辦單。");
        if (_currentUser.Has(Capability.Assign) || _currentUser.Has(Capability.ViewAll)) return order;
        if (order.HandlerId == _currentUser.UserId) return order;
        throw DomainException.Forbidden("沒有檢視這張交辦單的權限。");
    }

    private T FillRow<T>(T dto, WorkOrder o, Dictionary<long, WebUser> users,
        Dictionary<long, WorkOrderMemberCounts> counts, List<KnownIssueRule> rules, IssueExclusion exclusion) where T : WorkOrderRowDto
    {
        users.TryGetValue(o.HandlerId, out var handler);
        var c = counts.TryGetValue(o.WorkOrderId, out var found) ? found : new WorkOrderMemberCounts();

        dto.WorkOrderId = o.WorkOrderId;
        dto.Source = o.SourceName;
        dto.EventId = o.EventId;
        dto.IssueLabel = o.IssueLabel;
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
        dto.Counts = new WorkOrderCountsDto
        {
            Total = c.Total, Active = c.Active, Closed = c.Closed, InProgress = c.InProgress, Observing = c.Observing,
            Open = c.Open, Escalated = c.Escalated, Overdue = c.Overdue, DaySyncPending = c.DaySyncPending
        };
        dto.DueDate = o.DueDate;
        dto.CreatedAt = o.CreatedAt;
        dto.LastAppendedAt = o.LastAppendedAt;
        dto.LastReplyAt = o.LastReplyAt;
        dto.UnrepliedDays = o.LastReplyAt == null && o.ClosedAt == null
            ? (DateTime.Today - o.CreatedAt.Date).Days
            : null;
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

    /// <summary>暫停判定的唯一一份：進行中、問題欄非 null，且問題目前靜音中（SQL 端對應 <c>WorkOrderQuery.PausedKeys</c>）</summary>
    private static bool IsPaused(WorkOrder o, IssueExclusion exclusion) =>
        o.ClosedAt == null && o.SourceName != null && o.EventId != null
        && exclusion.IsCurrentlyMuted(o.SourceName, o.EventId.Value);

    private static List<MuteSpan> SpansOf(IssueExclusion exclusion, string source, int eventId)
    {
        var key = source.ToUpperInvariant();
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
        users.TryGetValue(userId, out var user) ? _displayNames.WithAccount(user.DisplayName, user.Account) : DeletedName;
}
