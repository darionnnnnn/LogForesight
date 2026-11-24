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
        IIssueHandlingStore? issueHandlings = null)
    {
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
            ExcludedHostKeys = disabledKeys,
            PausedMode = pausedMode,
            OnlyKeys = req.ResumedFromMute ? ResumedFromMuteKeys(exclusion) : null,
            UnreadOnly = req.UnreadOnly
        });

        var counts = _orders.CountMembers(result.Items.Select(o => o.WorkOrderId).ToList(), disabledKeys);
        var rules = KnownIssueCatalog.ResolveRules(_rules);
        var hostStats = BuildHostStats(result.Items, disabledKeys, out _);

        return new WorkOrderListDto
        {
            Items = result.Items.Select(o => FillRow(new WorkOrderRowDto(), o, users, counts, hostStats, rules, exclusion)).ToList(),
            Total = result.Total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>清單篩選用的使用者群組選項（回饋第 47 輪定案 48）：只列啟用中群組，依名稱不分大小寫排序</summary>
    /// <summary>未讀回覆的單數（側欄徽章用；能力由 controller 標註把關）。SQL 端 COUNT，不把單拉回記憶體</summary>
    public WorkOrderUnreadCountDto UnreadCount() => new() { Count = _orders.CountUnreadReplies() };

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

        var hostStats = BuildHostStats(new[] { order }, WorkOrderQueries.DisabledHostKeys(allHosts), out var activeLive);
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

        var hidden = 0;
        if (filtered)
        {
            var unfiltered = _cases.QueryMembers(new WorkOrderMemberQuery
            {
                WorkOrderId = id, Status = status, HostNameKeys = nameKeys, Page = 1, PageSize = 1
            }).Total;
            hidden = unfiltered - total;
        }

        var disabledKeys = WorkOrderQueries.DisabledHostKeys(hosts).ToHashSet();
        var hostIdByKey = hosts
            .GroupBy(h => HostNameKey.Of(h.HostName))
            .ToDictionary(g => g.Key, g => g.First().HostId);
        var today = _clock.LocalToday();

        // 復發與第 N 次：本頁成員的前案一次取回（不設結案時間下限，「上次回覆」要看到上一件不論多久以前）；觀察期間再發生日數 K 同樣一次批次算
        var recurrence = RecurrenceOf(items, boundedWindow: false);
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
        Dictionary<long, WorkOrderMemberCounts> counts, Dictionary<long, OrderHostStats> hostStats, List<KnownIssueRule> rules,
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
        dto.ObservingHosts = hs.Observing;
        dto.ObservationExpiredHosts = hs.Expired;
        dto.TodoHosts = hs.Todo;
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

    /// <summary>一張單的主機統計（單頭、清單列用）；全部是不重複主機數</summary>
    private readonly record struct OrderHostStats(int Active, int Total, int Recurrence, int Observing, int Expired, int Todo);

    /// <summary>
    /// 本頁各單的主機統計：歷來主機數一句 GROUP BY、進行中成員一次取回（精簡欄位）、前案候選一次取回，
    /// 再於記憶體以 <see cref="IssueRecurrence"/> 與 <see cref="IssueHandlingStatuses"/> 的共用判定彙整——查詢次數不隨成員數增長。
    /// 停用主機上的成員不計入進行中相關的數字（與 <see cref="WorkOrderCountsDto"/> 同口徑），歷來主機數照舊含全部。
    /// <paramref name="activeLive"/> 回傳參與統計的進行中成員（供單頭另外算觀察到期的成員）。
    /// </summary>
    private Dictionary<long, OrderHostStats> BuildHostStats(
        IReadOnlyCollection<WorkOrder> orders, IReadOnlyCollection<string> disabledKeys, out List<IssueCase> activeLive)
    {
        activeLive = new List<IssueCase>();
        var result = new Dictionary<long, OrderHostStats>();
        if (orders.Count == 0) return result;

        var totals = _cases.CountDistinctHosts(orders.Select(o => o.WorkOrderId).ToList());
        var activeIds = orders.Where(o => o.ClosedAt == null).Select(o => o.WorkOrderId).ToList();
        var disabled = disabledKeys.ToHashSet(StringComparer.Ordinal);
        if (activeIds.Count > 0)
            activeLive = _cases.GetActiveMemberSummaries(activeIds)
                .Where(c => c.WorkOrderId != null && !disabled.Contains(HostNameKey.Of(c.HostName)))
                .ToList();

        var recurrence = RecurrenceOf(activeLive, boundedWindow: true);
        var today = _clock.LocalToday();
        var byOrder = activeLive.GroupBy(c => c.WorkOrderId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var o in orders)
        {
            var members = byOrder.GetValueOrDefault(o.WorkOrderId) ?? new List<IssueCase>();
            int Hosts(Func<IssueCase, bool> match) =>
                members.Where(match).Select(c => HostNameKey.Of(c.HostName)).Distinct(StringComparer.Ordinal).Count();
            result[o.WorkOrderId] = new OrderHostStats(
                Active: Hosts(_ => true),
                Total: totals.GetValueOrDefault(o.WorkOrderId),
                Recurrence: Hosts(c => recurrence.GetValueOrDefault(c.CaseId, RecurrenceInfo.None).IsRecurrence),
                Observing: Hosts(c => IssueHandlingStatuses.IsObservationActive(c.Status, c.DueDate, today)),
                Expired: Hosts(c => IssueHandlingStatuses.IsObservationExpired(c.Status, c.DueDate, today)),
                Todo: Hosts(c => WorkOrderQueries.IsTodoMember(c, today)));
        }
        return result;
    }

    /// <summary>
    /// 對 <paramref name="targets"/> 的復發判定（<see cref="IssueRecurrence.Evaluate"/>）：同主機同問題的前案候選一次取回。
    /// <paramref name="boundedWindow"/>＝true 時前案只取結案日不早於「最早一件的建立日往前一個回看窗」者——
    /// 復發與次數只看回看窗內，更早的前案不影響結果（清單統計用）；false＝全取（成員清單要「上次回覆」與第 N 次）。
    /// </summary>
    private Dictionary<string, RecurrenceInfo> RecurrenceOf(IReadOnlyCollection<IssueCase> targets, bool boundedWindow)
    {
        if (targets.Count == 0) return new Dictionary<string, RecurrenceInfo>(StringComparer.Ordinal);

        var hostNames = targets.Select(c => c.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var issues = targets.Where(c => c.SourceName != null && c.EventId != null)
            .Select(c => (c.SourceName!, c.EventId!.Value)).Distinct().ToList();
        DateTime? since = boundedWindow ? targets.Min(c => c.CreatedAt).Date.AddDays(-IssueRecurrence.WindowDays) : null;
        return IssueRecurrence.Evaluate(targets, _cases.GetClosedHistory(hostNames, issues, since));
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
