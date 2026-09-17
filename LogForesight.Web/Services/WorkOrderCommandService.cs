using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services.Mail;

namespace LogForesight.Web.Services;

/// <summary>
/// 交辦單命令 API（/api/work-orders）：依篩選建單（預覽＋落盤）、追加、改派、拆單、取消、代為結案。
///
/// 這裡只做驗證、範圍解析、分攤、稽核；所有「動到交辦單」的寫入一律交給 <see cref="WorkOrderCoordinator"/>。
///
/// **預覽與建單共用同一個計畫函式（<see cref="Plan"/>）**：畫面說會略過或排除的主機，
/// 落盤時不可能被寫入——兩邊只呼叫它一次，送給協調器的成員就是計畫裡「會寫入」的那一份。
///
/// **主機母體與依問題視角同口徑**：範圍一律經 <see cref="RecordListQueryService.ResolveIssueScope"/>，
/// 不另寫可見範圍／期間／嚴重度／日風險等級的解析。
///
/// 查詢次數不隨主機數線性增長：出現點、衝突、雜訊記憶、負載各一次；併入與可見範圍每位處理人一次。
/// </summary>
public class WorkOrderCommandService
{
    private const int PreviewPageSize = 100;
    private const int NoAccessListLimit = 50;
    private const int NoteMaxLength = 1000;
    private const int ReasonMaxLength = 500;

    private const string AssignSingle = "single";
    private const string AssignGroup = "group";
    private const string SplitByLoad = "byLoad";
    private const string SplitRoundRobin = "roundRobin";

    private const string TargetKind = "work_order";

    private readonly RecordListQueryService _query;
    private readonly IIssueAggregateQuery _aggregates;
    private readonly WorkOrderCoordinator _coordinator;
    private readonly IWorkOrderStore _orders;
    private readonly IIssueCaseStore _cases;
    private readonly INoiseMarkStore _noiseMarks;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IVisibilityService _visibility;
    private readonly UserCapabilityResolver _capabilities;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUserDisplayNameService _displayNames;
    private readonly IKnownIssueRuleStore _rules;
    private readonly MailNotificationService _mail;

    public WorkOrderCommandService(
        RecordListQueryService query,
        IIssueAggregateQuery aggregates,
        WorkOrderCoordinator coordinator,
        IWorkOrderStore orders,
        IIssueCaseStore cases,
        INoiseMarkStore noiseMarks,
        IHostStore hosts,
        IUserStore users,
        IVisibilityService visibility,
        UserCapabilityResolver capabilities,
        ICurrentUser currentUser,
        IAuditService audit,
        IUserDisplayNameService displayNames,
        IKnownIssueRuleStore rules,
        MailNotificationService mail)
    {
        _query = query;
        _aggregates = aggregates;
        _coordinator = coordinator;
        _orders = orders;
        _cases = cases;
        _noiseMarks = noiseMarks;
        _hosts = hosts;
        _users = users;
        _visibility = visibility;
        _capabilities = capabilities;
        _currentUser = currentUser;
        _audit = audit;
        _displayNames = displayNames;
        _rules = rules;
        _mail = mail;
    }

    // ── 建單：預覽／落盤 ──────────────────────────────────────────────────────

    public WorkOrderPreviewDto Preview(CreateWorkOrderRequest req)
    {
        var plan = Plan(req);

        var page = Math.Max(1, req.Page);
        var hosts = plan.Hosts
            .Skip((page - 1) * PreviewPageSize)
            .Take(PreviewPageSize)
            .Select(h => new WorkOrderPreviewHostDto
            {
                HostId = h.Resolved.Host.HostId,
                HostName = h.Resolved.Host.HostName,
                AllocatedHandlerId = h.Effective.Count > 0 ? h.Handler!.UserId : null,
                ExistingHandlerName = h.ConflictHandlerId.HasValue ? plan.NameOf(h.ConflictHandlerId.Value) : null,
                NoiseExcluded = h.Resolved.NoiseExcluded,
                ManuallyExcluded = h.Resolved.ManuallyExcluded
            })
            .ToList();

        // 期間內主機日：與依問題視角同一個範圍、同一個彙總（該問題那一列的 DayCount）
        // 靜音不排除（不用 Scope.Exclusion）：派工決策以紀錄日判定靜音，已在派工脈絡處理
        var aggregate = _aggregates.Aggregate(IssueExclusion.None, plan.Scope.From, plan.Scope.To, plan.Scope.HostIds,
                plan.Scope.VisibleSeverities, plan.Scope.DayRiskLevels)
            .FirstOrDefault(a => a.EventId == plan.EventId && string.Equals(a.Source, plan.Source, StringComparison.OrdinalIgnoreCase));

        return new WorkOrderPreviewDto
        {
            AffectedHosts = plan.Hosts.Count(h => h.Resolved.Members.Count > 0),
            AffectedMembers = plan.Hosts.Sum(h => h.Resolved.Members.Count),
            EstimatedHostDays = aggregate == null ? 0 : aggregate.DayCount,
            NoiseExcludedHosts = plan.NoiseExcludedHosts,
            ManuallyExcludedHosts = plan.ManuallyExcludedHosts,
            PausedMembersExcluded = plan.PausedMembersExcluded,
            Conflicts = plan.Conflicts,
            Allocation = plan.Allocations
                .Select(a => new WorkOrderAllocationDto
                {
                    HandlerId = a.Handler.UserId,
                    HandlerName = NameOf(a.Handler),
                    HostCount = a.Hosts.Count,
                    MergeIntoWorkOrderId = a.MergeIntoWorkOrderId
                })
                .ToList(),
            Hosts = hosts,
            TotalHosts = plan.Hosts.Count,
            Page = page,
            PageSize = PreviewPageSize,
            AssigneeNoAccess = plan.NoAccess.Take(NoAccessListLimit).ToList(),
            AssigneeNoAccessTotal = plan.NoAccess.Count,
            AssigneeCannotHandle = plan.CannotHandle
        };
    }

    public CreateWorkOrderResultDto Create(CreateWorkOrderRequest req)
    {
        var plan = Plan(req);
        if (plan.Hosts.All(h => h.Resolved.Members.Count == 0))
            throw DomainException.Validation("找不到任何符合條件、且您有權限的主機。");

        var actor = NewActor();
        var scopeKind = req.ScopeKind;
        var scopeGroupIds = scopeKind == WorkOrderScopes.Groups ? req.GroupIds!.Distinct().ToList() : new List<long>();
        var autoAttach = scopeKind != WorkOrderScopes.Hosts && req.AutoAttach;

        var orders = new List<WorkOrderCreatedDto>();
        var raceSkipped = new List<WorkOrderConflict>();
        var daySyncPending = 0;
        foreach (var allocation in plan.Allocations)
        {
            var outcome = Guard(() => _coordinator.Create(new WorkOrderCreateRequest
            {
                Source = plan.Source, EventId = plan.EventId, IssueLabel = plan.IssueLabel,
                HandlerId = allocation.Handler.UserId, Origin = WorkOrderOrigins.Manual,
                ScopeKind = scopeKind, ScopeGroupIds = scopeGroupIds, AutoAttach = autoAttach,
                Note = req.Note, DueDate = req.DueDate,
                ReassignConflicts = req.ReassignConflicts,
                Members = allocation.Hosts.SelectMany(h => h.Effective).ToList(),
                Actor = actor
            }));

            daySyncPending += outcome.DaySync.PendingCases;
            raceSkipped.AddRange(outcome.SkippedConflicts);
            if (outcome.WorkOrderId == 0) continue;

            orders.Add(new WorkOrderCreatedDto
            {
                WorkOrderId = outcome.WorkOrderId,
                HandlerId = allocation.Handler.UserId,
                HandlerName = NameOf(allocation.Handler),
                CreatedOrder = outcome.CreatedOrder,
                NewCases = outcome.NewCases,
                LinkedExisting = outcome.LinkedExisting,
                Reassigned = outcome.Reassigned
            });
        }

        // 略過＝計畫判定的略過主機；協調器在計畫與寫入之間發現的新衝突（別人剛好搶先建案）一併列出
        var skipped = plan.Hosts
            .Where(h => h.Skipped)
            .Select(h => new WorkOrderSkippedDto
            {
                HostName = h.Resolved.Host.HostName,
                ExistingHandlerName = h.ConflictHandlerId.HasValue ? plan.NameOf(h.ConflictHandlerId.Value) : null
            })
            .ToList();
        var listed = skipped.Select(s => s.HostName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in raceSkipped)
        {
            if (!listed.Add(conflict.HostName)) continue;
            skipped.Add(new WorkOrderSkippedDto
            {
                HostName = conflict.HostName,
                ExistingHandlerName = conflict.HandlerId.HasValue ? plan.NameOf(conflict.HandlerId.Value) : null
            });
        }

        var handlerNames = plan.Allocations.Select(a => NameOf(a.Handler)).ToList();
        var addedHosts = plan.Allocations.Sum(a => a.Hosts.Count);
        _audit.Record(
            action: AuditActions.WorkOrderCreate,
            summary: $"交辦『{plan.IssueLabel}』給 {NameFormat.Join(handlerNames)}：" +
                     $"新建 {orders.Count(o => o.CreatedOrder)} 張、併入 {orders.Count(o => !o.CreatedOrder)} 張，" +
                     $"加入 {addedHosts} 台（略過 {skipped.Count} 台、雜訊排除 {plan.NoiseExcludedHosts} 台）",
            targetKind: TargetKind,
            targetId: $"{plan.Source}/{plan.EventId}",
            detail: new
            {
                plan.Source, plan.EventId,
                WorkOrderIds = orders.Select(o => o.WorkOrderId).ToList(),
                PerHandler = plan.Allocations.Select(a => new { Handler = a.Handler.Account, Hosts = a.Hosts.Count }).ToList(),
                Skipped = skipped.Count,
                NoiseExcluded = plan.NoiseExcludedHosts
            });

        var rules = KnownIssueCatalog.ResolveRules(_rules);
        var allocationMap = plan.Allocations.ToDictionary(a => a.Handler.UserId);
        foreach (var order in orders)
        {
            var hostNames = allocationMap.TryGetValue(order.HandlerId, out var alloc)
                ? alloc.Hosts.Select(h => h.Resolved.Host.HostName).ToList()
                : (IReadOnlyList<string>)Array.Empty<string>();
            NotifyHandler(WorkOrderNoticeKinds.Created, order.WorkOrderId, plan.IssueLabel, plan.Source, plan.EventId,
                order.HandlerId, hostNames.Count, hostNames, req.Note, req.DueDate, null, rules);
        }

        return new CreateWorkOrderResultDto
        {
            Orders = orders,
            Skipped = skipped,
            NoiseExcludedHosts = plan.NoiseExcludedHosts,
            DaySyncPendingCases = daySyncPending,
            AssigneeNoAccess = plan.NoAccess.Take(NoAccessListLimit).ToList(),
            AssigneeNoAccessTotal = plan.NoAccess.Count,
            AssigneeCannotHandle = plan.CannotHandle
        };
    }

    // ── 單一單的操作 ─────────────────────────────────────────────────────────

    public AppendWorkOrderResultDto Append(long id, AppendWorkOrderRequest req)
    {
        var order = GetOrder(id);
        if (order.ClosedAt != null)
            throw DomainException.Validation($"交辦單 {id} 已結案，不能追加主機。");
        if (order.SourceName == null || order.EventId == null)
            throw DomainException.Validation($"交辦單 {id} 不是單一問題單，不能依問題追加主機。");
        if (req.HostIds.Count == 0)
            throw DomainException.Validation("請至少選擇一台主機。");

        var requested = req.HostIds.Distinct().ToList();
        var scope = _query.ResolveIssueScope(new RecordSearchRequest { HostIds = requested, From = req.From, To = req.To });
        var visible = scope.HostIds.ToHashSet();
        var ignored = requested.Count(hostId => !visible.Contains(hostId));

        var resolved = ResolveMembers(order.SourceName, order.EventId.Value, scope, new HashSet<long>());
        var members = resolved.SelectMany(r => r.Members).ToList();
        if (members.Count == 0)
            throw DomainException.Validation("找不到任何符合條件、且您有權限的主機。");

        var outcome = Guard(() => _coordinator.Append(id, members, req.ReassignConflicts, NewActor()));
        var noiseExcluded = resolved.Count(r => r.NoiseExcluded);

        _audit.Record(
            action: AuditActions.WorkOrderAppend,
            summary: $"交辦單 {id}『{order.IssueLabel}』追加主機：新案件 {outcome.NewCases} 件、改連 {outcome.LinkedExisting} 件、" +
                     $"改派 {outcome.Reassigned} 件（略過 {outcome.SkippedConflicts.Count} 件、雜訊排除 {noiseExcluded} 台、無權限忽略 {ignored} 台）",
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new
            {
                WorkOrderId = id, outcome.NewCases, outcome.LinkedExisting, outcome.Reassigned,
                Skipped = outcome.SkippedConflicts.Count, NoiseExcluded = noiseExcluded, Ignored = ignored
            });

        return new AppendWorkOrderResultDto
        {
            WorkOrderId = id,
            NewCases = outcome.NewCases,
            LinkedExisting = outcome.LinkedExisting,
            Reassigned = outcome.Reassigned,
            SkippedConflicts = outcome.SkippedConflicts.Count,
            NoiseExcludedHosts = noiseExcluded,
            IgnoredHosts = ignored,
            DaySyncPendingCases = outcome.DaySync.PendingCases
        };
    }

    public WorkOrderMoveResultDto Reassign(long id, ReassignWorkOrderRequest req)
    {
        var order = GetOrder(id);
        var handler = ResolveActiveHandler(req.HandlerId);

        var result = Guard(() => _coordinator.Reassign(id, handler.UserId, NewActor()));

        _audit.Record(
            action: AuditActions.WorkOrderReassign,
            summary: $"交辦單 {id}『{order.IssueLabel}』改派給 {NameOf(handler)}：移動 {result.MovedCases} 件" +
                     (result.MergedIntoExisting ? $"，併入單號 {result.TargetWorkOrderId}" : ""),
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new
            {
                WorkOrderId = id, result.PreviousHandlerId, NewHandler = handler.Account,
                result.TargetWorkOrderId, result.MergedIntoExisting, result.MovedCases
            });

        var rules = KnownIssueCatalog.ResolveRules(_rules);
        NotifyHandler(WorkOrderNoticeKinds.Created, result.TargetWorkOrderId, order.IssueLabel, order.SourceName, order.EventId,
            handler.UserId, result.MovedCases, Array.Empty<string>(), order.Note, order.DueDate, null, rules);
        NotifyHandler(WorkOrderNoticeKinds.Transferred, id, order.IssueLabel, order.SourceName, order.EventId,
            result.PreviousHandlerId, result.MovedCases, Array.Empty<string>(), null, null, null, rules);

        return ToMoveResult(id, result, handler);
    }

    public WorkOrderMoveResultDto Split(long id, SplitWorkOrderRequest req)
    {
        var order = GetOrder(id);
        var handler = ResolveActiveHandler(req.HandlerId);

        var result = Guard(() => _coordinator.Split(id, req.CaseIds, handler.UserId, NewActor()));

        _audit.Record(
            action: AuditActions.WorkOrderSplit,
            summary: $"交辦單 {id}『{order.IssueLabel}』拆出 {result.MovedCases} 件給 {NameOf(handler)}" +
                     (result.MergedIntoExisting ? $"，併入單號 {result.TargetWorkOrderId}" : $"，新單號 {result.TargetWorkOrderId}"),
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new
            {
                WorkOrderId = id, result.PreviousHandlerId, NewHandler = handler.Account,
                result.TargetWorkOrderId, result.MergedIntoExisting, result.MovedCases, CaseIds = req.CaseIds
            });

        var rules = KnownIssueCatalog.ResolveRules(_rules);
        NotifyHandler(WorkOrderNoticeKinds.Created, result.TargetWorkOrderId, order.IssueLabel, order.SourceName, order.EventId,
            handler.UserId, result.MovedCases, Array.Empty<string>(), order.Note, order.DueDate, null, rules);
        NotifyHandler(WorkOrderNoticeKinds.Transferred, id, order.IssueLabel, order.SourceName, order.EventId,
            result.PreviousHandlerId, result.MovedCases, Array.Empty<string>(), null, null, null, rules);

        return ToMoveResult(id, result, handler);
    }

    public WorkOrderCloseResultDto Cancel(long id, CancelWorkOrderRequest req)
    {
        var reason = RequireReason(req.Reason, "取消交辦單要填原因。");
        var order = GetOrder(id);

        var result = Guard(() => _coordinator.Cancel(id, reason, NewActor()));

        _audit.Record(
            action: AuditActions.WorkOrderCancel,
            summary: $"取消交辦單 {id}『{order.IssueLabel}』：{result.ClosedCases} 件案件調回未處理（原因：{reason}）",
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new { WorkOrderId = id, result.ClosedCases, Reason = reason, result.HandlerId });

        NotifyHandler(WorkOrderNoticeKinds.Cancelled, id, order.IssueLabel, order.SourceName, order.EventId,
            result.HandlerId, result.ClosedCases, Array.Empty<string>(), null, null, reason, KnownIssueCatalog.ResolveRules(_rules));

        return new WorkOrderCloseResultDto
        {
            WorkOrderId = id, ClosedCases = result.ClosedCases, DaySyncPendingCases = result.DaySync.PendingCases
        };
    }

    public WorkOrderCloseResultDto AdminClose(long id, AdminCloseWorkOrderRequest req)
    {
        if (!IssueHandlingStatuses.IsClosed(req.Status))
            throw DomainException.Validation("代為結案的狀態必須是結案類（已處理／不處理／誤報／已知雜訊）。");
        var reason = RequireReason(req.Reason, "代為結案要填原因。");
        var order = GetOrder(id);

        var result = Guard(() => _coordinator.AdminClose(id, req.Status, reason, NewActor()));

        _audit.Record(
            action: AuditActions.WorkOrderAdminClose,
            summary: $"代為結案交辦單 {id}『{order.IssueLabel}』：{result.ClosedCases} 件標為 {req.Status}（原因：{reason}）",
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new { WorkOrderId = id, result.ClosedCases, req.Status, Reason = reason, result.HandlerId });

        return new WorkOrderCloseResultDto
        {
            WorkOrderId = id, ClosedCases = result.ClosedCases, DaySyncPendingCases = result.DaySync.PendingCases
        };
    }

    // ── 計畫函式（預覽與落盤共用的唯一一份）──────────────────────────────────

    private CreatePlan Plan(CreateWorkOrderRequest req)
    {
        // 1. 驗證（在任何查詢之前）
        if (string.IsNullOrWhiteSpace(req.Source))
            throw DomainException.Validation("請指定問題來源。");
        if (req.EventId == null)
            throw DomainException.Validation("請指定問題事件 ID。");
        if (req.ScopeKind is not (WorkOrderScopes.All or WorkOrderScopes.Groups or WorkOrderScopes.Hosts))
            throw DomainException.Validation($"不支援的交辦範圍「{req.ScopeKind}」。");
        if (req.ScopeKind == WorkOrderScopes.Groups && req.GroupIds is not { Count: > 0 })
            throw DomainException.Validation("交辦範圍為主機群組時，請至少選擇一個主機群組。");
        if (req.Note is { Length: > NoteMaxLength })
            throw DomainException.Validation($"說明不可超過 {NoteMaxLength} 字。");

        var users = _users.GetAll();
        var usersById = users.ToDictionary(u => u.UserId);

        List<WebUser> candidates;
        var paused = 0;
        switch (req.AssignMode)
        {
            case AssignSingle:
                if (req.HandlerId == null)
                    throw DomainException.Validation("請指定處理人。");
                candidates = new List<WebUser> { ResolveActiveHandler(req.HandlerId.Value) };
                break;
            case AssignGroup:
                if (req.GroupId == null)
                    throw DomainException.Validation("請指定要分攤的使用者群組。");
                if (req.SplitMode is not (SplitByLoad or SplitRoundRobin))
                    throw DomainException.Validation($"不支援的分攤方式「{req.SplitMode}」。");
                var members = users.Where(u => u.Active && u.GroupIds.Contains(req.GroupId.Value)).ToList();
                paused = members.Count(u => u.DispatchPaused);
                candidates = members
                    .Where(u => !u.DispatchPaused)
                    .OrderBy(u => u.Account, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (candidates.Count == 0)
                    throw DomainException.Validation("此群組沒有可分派的成員。");
                break;
            default:
                throw DomainException.Validation($"不支援的分派方式「{req.AssignMode}」。");
        }

        var source = req.Source.Trim();
        var eventId = req.EventId.Value;

        // 2～3. 範圍、出現點、手動排除、雜訊排除
        var scope = _query.ResolveIssueScope(new RecordSearchRequest
        {
            HostIds = req.HostIds, GroupIds = req.GroupIds, From = req.From, To = req.To
        });
        var excluded = req.ExcludeHostIds == null ? new HashSet<long>() : req.ExcludeHostIds.ToHashSet();
        var hosts = ResolveMembers(source, eventId, scope, excluded)
            .Select(r => new PlannedHost(r))
            .ToList();

        // 4. 衝突：一次取剩餘主機的進行中案件
        var withMembers = hosts.Where(h => h.Resolved.Members.Count > 0).ToList();
        var existing = new Dictionary<(string HostName, string IssueKey), IssueCase>();
        if (withMembers.Count > 0)
        {
            foreach (var openCase in _cases.GetOpenMany(withMembers.Select(h => h.Resolved.Host.HostName).ToList(), source, eventId))
                existing.TryAdd((openCase.HostName.ToUpperInvariant(), openCase.IssueKey), openCase);
        }
        IssueCase? ExistingOf(WorkOrderMember m) =>
            existing.TryGetValue((m.HostName.ToUpperInvariant(), m.IssueKey), out var c) ? c : null;

        // 5. 分派（以主機為單位；主機已依名稱升冪）
        var assignedMembers = candidates.ToDictionary(c => c.UserId, _ => 0);
        var loads = req.AssignMode == AssignGroup && req.SplitMode == SplitByLoad
            ? _orders.LoadBoard().ToDictionary(l => l.HandlerId, l => l.ActiveMembers)
            : new Dictionary<long, int>();
        var allocationIndex = 0;
        foreach (var host in withMembers)
        {
            // 未要求改派時，全部簽章都已有進行中案件的主機不參與群組分攤（處理人未定，一律視為他人）
            if (req.AssignMode == AssignGroup && !req.ReassignConflicts
                && host.Resolved.Members.All(m => ExistingOf(m) != null))
            {
                host.ConflictHandlerId = host.Resolved.Members.Select(m => ExistingOf(m)!.HandlerId).FirstOrDefault(h => h.HasValue);
                continue;
            }

            WebUser handler;
            if (req.AssignMode == AssignSingle)
                handler = candidates[0];
            else if (req.SplitMode == SplitRoundRobin)
                handler = candidates[allocationIndex++ % candidates.Count];
            else
                handler = candidates
                    .OrderBy(c => loads.GetValueOrDefault(c.UserId) + assignedMembers[c.UserId])
                    .First(); // candidates 已依 Account 升冪，OrderBy 穩定排序＝同分依帳號

            assignedMembers[handler.UserId] += host.Resolved.Members.Count;
            host.Handler = handler;

            // 成員分類與協調器同規則：無案件＝新建；同處理人＝改連；他人＝改派或略過
            foreach (var member in host.Resolved.Members)
            {
                var openCase = ExistingOf(member);
                if (openCase != null && openCase.HandlerId != handler.UserId)
                {
                    if (host.ConflictHandlerId == null) host.ConflictHandlerId = openCase.HandlerId;
                    if (!req.ReassignConflicts) continue;
                }
                host.Effective.Add(member);
            }
        }

        // 衝突摘要：他人（有處理人）的進行中案件，依原處理人計主機數
        var conflicts = hosts
            .Where(h => h.ConflictHandlerId.HasValue)
            .GroupBy(h => h.ConflictHandlerId!.Value)
            .Select(g => new WorkOrderConflictDto
            {
                HandlerId = g.Key,
                HandlerName = usersById.TryGetValue(g.Key, out var u) ? NameOf(u) : NameFormat.OrDeleted(null, g.Key),
                HostCount = g.Count()
            })
            .OrderBy(c => c.HandlerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 6～7. 併入與提示：每位被分到的處理人各查一次
        var allocations = new List<PlannedAllocation>();
        var noAccess = new List<AssigneeNoAccessDto>();
        var cannotHandle = new List<AssigneeCannotHandleDto>();
        foreach (var candidate in candidates)
        {
            var allocated = hosts.Where(h => h.Effective.Count > 0 && h.Handler!.UserId == candidate.UserId).ToList();
            if (allocated.Count == 0) continue;

            var merge = _orders.GetActiveFor(candidate.UserId, source, eventId);
            allocations.Add(new PlannedAllocation(candidate, allocated, merge == null ? null : merge.WorkOrderId));

            var visible = _visibility.GetVisibleHostIdsFor(candidate.UserId);
            noAccess.AddRange(allocated
                .Where(h => !visible.Contains(h.Resolved.Host.HostId))
                .Select(h => new AssigneeNoAccessDto { HostName = h.Resolved.Host.HostName, HandlerName = NameOf(candidate) }));

            if (!_capabilities.Resolve(candidate).Contains(Capability.Handle))
                cannotHandle.Add(new AssigneeCannotHandleDto { HandlerName = NameOf(candidate), HostCount = allocated.Count });
        }

        return new CreatePlan
        {
            Source = source,
            EventId = eventId,
            IssueLabel = $"{source} {eventId}",
            Scope = scope,
            Hosts = hosts,
            Allocations = allocations,
            PausedMembersExcluded = paused,
            NoiseExcludedHosts = hosts.Count(h => h.Resolved.NoiseExcluded),
            ManuallyExcludedHosts = hosts.Count(h => h.Resolved.ManuallyExcluded),
            Conflicts = conflicts,
            NoAccess = noAccess,
            CannotHandle = cannotHandle,
            NameOf = id => usersById.TryGetValue(id, out var u) ? NameOf(u) : NameFormat.OrDeleted(null, id)
        };
    }

    /// <summary>
    /// 出現點解析與排除的唯一一份（建單計畫與追加共用）：一次 LatestOccurrences 取「每台主機 × 完整簽章 ×
    /// 最近出現日」，每個簽章一個成員（TriggerDate＝最近出現日）；手動排除整台；已知雜訊記憶命中的
    /// （主機, 簽章）逐成員扣除，一台主機全部成員都被扣才算雜訊排除一台。回傳依主機名稱升冪。
    /// </summary>
    private List<ResolvedHost> ResolveMembers(string source, int eventId, IssueScope scope, IReadOnlySet<long> excludeHostIds)
    {
        // 靜音不排除（不用 scope.Exclusion）：派工決策以紀錄日判定靜音，已在派工脈絡處理
        var occurrences = _aggregates.LatestOccurrences(
            IssueExclusion.None, new[] { (source, eventId) }, scope.From, scope.To, scope.HostIds, scope.VisibleSeverities, scope.DayRiskLevels);
        if (occurrences.Count == 0) return new List<ResolvedHost>();

        var hostsById = _hosts.GetAll().ToDictionary(h => h.HostId);
        var noise = _noiseMarks.GetAll()
            .Select(m => (m.HostName.ToUpperInvariant(), m.IssueKey))
            .ToHashSet();
        var label = $"{source} {eventId}";

        return occurrences
            .Where(o => hostsById.ContainsKey(o.HostId))
            .GroupBy(o => o.HostId)
            .Select(g =>
            {
                var host = hostsById[g.Key];
                if (excludeHostIds.Contains(host.HostId))
                    return new ResolvedHost(host, new List<WorkOrderMember>(), NoiseExcluded: false, ManuallyExcluded: true);

                var all = g
                    .GroupBy(o => o.IssueKey, StringComparer.Ordinal)
                    .Select(k => k.OrderByDescending(o => o.LastSeen).First())
                    .OrderBy(o => o.IssueKey, StringComparer.Ordinal)
                    .ToList();
                var kept = all
                    .Where(o => !noise.Contains((host.HostName.ToUpperInvariant(), o.IssueKey)))
                    .Select(o => new WorkOrderMember
                    {
                        HostName = host.HostName, IssueKey = o.IssueKey, IssueLabel = label, TriggerDate = o.LastSeen
                    })
                    .ToList();
                return new ResolvedHost(host, kept, NoiseExcluded: kept.Count == 0, ManuallyExcluded: false);
            })
            .OrderBy(r => r.Host.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── 共用 ────────────────────────────────────────────────────────────────

    private void NotifyHandler(
        string kind, long workOrderId, string issueLabel, string? source, int? eventId,
        long recipientUserId, int hostCount, IReadOnlyList<string> hostNames, string? note, DateTime? dueDate, string? reason,
        List<KnownIssueRule> resolvedRules)
    {
        if (recipientUserId == _currentUser.UserId) return;
        var user = _users.Get(recipientUserId);
        if (user == null) return;

        var plainExplanation = source != null && eventId != null
            ? KnownIssueCatalog.PlainExplanationFor(resolvedRules, source, eventId.Value)
            : null;

        _ = _mail.NotifyWorkOrderAsync(new WorkOrderNotice(
            kind, workOrderId, issueLabel, plainExplanation, hostCount, hostNames,
            note, dueDate, user.Account, user.Email, _currentUser.Account, reason));
    }

    private WorkOrder GetOrder(long id) =>
        _orders.Get(id) ?? throw DomainException.NotFound($"找不到交辦單 {id}。");

    private WebUser ResolveActiveHandler(long handlerId)
    {
        var user = _users.Get(handlerId) ?? throw DomainException.NotFound("找不到指定的處理人。");
        if (!user.Active)
            throw DomainException.Validation($"{NameOf(user)} 的帳號已停用，無法指派。");
        return user;
    }

    private static string RequireReason(string? reason, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw DomainException.Validation(missingMessage);
        var trimmed = reason.Trim();
        if (trimmed.Length > ReasonMaxLength)
            throw DomainException.Validation($"原因不可超過 {ReasonMaxLength} 字。");
        return trimmed;
    }

    private WorkOrderActor NewActor() => new()
    {
        ActorId = _currentUser.UserId > 0 ? _currentUser.UserId : null,
        ActorAccount = _currentUser.Account,
        OccurredAt = DateTime.Now
    };

    /// <summary>協調器以 ArgumentException／InvalidOperationException 表達資料不符，轉成使用者看得懂的驗證錯誤</summary>
    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            throw DomainException.Validation(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw DomainException.Validation(ex.Message);
        }
    }

    private WorkOrderMoveResultDto ToMoveResult(long id, WorkOrderMoveResult result, WebUser handler) => new()
    {
        WorkOrderId = id,
        TargetWorkOrderId = result.TargetWorkOrderId,
        MergedIntoExisting = result.MergedIntoExisting,
        MovedCases = result.MovedCases,
        PreviousHandlerId = result.PreviousHandlerId,
        AssigneeCannotHandle = _capabilities.Resolve(handler).Contains(Capability.Handle)
            ? new List<AssigneeCannotHandleDto>()
            : new List<AssigneeCannotHandleDto> { new() { HandlerName = NameOf(handler), HostCount = result.MovedCases } }
    };

    private string NameOf(WebUser user) => _displayNames.WithAccount(user.DisplayName, user.Account);

    private sealed record ResolvedHost(WebHost Host, List<WorkOrderMember> Members, bool NoiseExcluded, bool ManuallyExcluded);

    private sealed class PlannedHost
    {
        public PlannedHost(ResolvedHost resolved) => Resolved = resolved;

        public ResolvedHost Resolved { get; }

        /// <summary>分到的處理人；null＝未參與分攤（排除或整台略過）</summary>
        public WebUser? Handler { get; set; }

        /// <summary>實際送給協調器寫入的成員</summary>
        public List<WorkOrderMember> Effective { get; } = new();

        /// <summary>他人進行中案件的處理人（顯示用；第一個遇到的）</summary>
        public long? ConflictHandlerId { get; set; }

        /// <summary>有成員、但一個都不會寫入</summary>
        public bool Skipped => Resolved.Members.Count > 0 && Effective.Count == 0;
    }

    private sealed record PlannedAllocation(WebUser Handler, List<PlannedHost> Hosts, long? MergeIntoWorkOrderId);

    private sealed class CreatePlan
    {
        public required string Source { get; init; }
        public required int EventId { get; init; }
        public required string IssueLabel { get; init; }
        public required IssueScope Scope { get; init; }
        public required List<PlannedHost> Hosts { get; init; }
        public required List<PlannedAllocation> Allocations { get; init; }
        public required int PausedMembersExcluded { get; init; }
        public required int NoiseExcludedHosts { get; init; }
        public required int ManuallyExcludedHosts { get; init; }
        public required List<WorkOrderConflictDto> Conflicts { get; init; }
        public required List<AssigneeNoAccessDto> NoAccess { get; init; }
        public required List<AssigneeCannotHandleDto> CannotHandle { get; init; }
        public required Func<long, string> NameOf { get; init; }
    }
}
