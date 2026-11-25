using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
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
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
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
    private readonly MailNotificationService _mail;

    private readonly TimeProvider _clock;

    /// <summary>目前規則（「不再打擾」30 天解除要看問題目前規則的嚴重度；必填，不退回靜態表）</summary>
    private readonly IKnownIssueRuleStore _rules;

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
        MailNotificationService mail,
        IKnownIssueRuleStore rules,
        TimeProvider? clock = null)
    {
        _rules = rules;
        _clock = clock ?? TimeProvider.System;
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
        _mail = mail;
    }

    // ── 建單：預覽／落盤 ──────────────────────────────────────────────────────

    public WorkOrderPreviewDto Preview(CreateWorkOrderRequest req)
    {
        var plan = Plan(req);

        var (page, _) = Paging.Normalize(req.Page, PreviewPageSize, PreviewPageSize);
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
                ManuallyExcluded = h.Resolved.ManuallyExcluded,
                ClosedReason = h.Resolved.Exclusion?.Reason,
                ClosedStatus = h.Resolved.Exclusion?.Status,
                ClosedByAccount = h.Resolved.Exclusion?.ByAccount,
                ClosedAt = h.Resolved.Exclusion?.At
            })
            .ToList();

        // 期間內主機日：與依問題視角同一個範圍、同一個彙總（該問題那一列的 DayCount）
        // 靜音不排除（不用 Scope.Exclusion）：派工決策以紀錄日判定靜音，已在派工脈絡處理
        // 只查單一問題，不對期間內全部問題做彙總
        var estimatedHostDays = _aggregates.IssueHostDayCount(IssueExclusion.None, plan.Source, plan.EventId, plan.Scope.From, plan.Scope.To,
            plan.Scope.HostIds, plan.Scope.VisibleSeverities, plan.Scope.DayRiskLevels);

        return new WorkOrderPreviewDto
        {
            // 以實際會寫入的成員計（Effective）：他人處理中且未勾改派的主機會被略過，算進來預覽就與落盤對不上
            AffectedHosts = plan.Hosts.Count(h => h.Effective.Count > 0),
            AffectedMembers = plan.Hosts.Sum(h => h.Effective.Count),
            EstimatedHostDays = estimatedHostDays,
            NoiseExcludedHosts = plan.NoiseExcludedHosts,
            ManuallyExcludedHosts = plan.ManuallyExcludedHosts,
            ClosedSkippedHosts = plan.ClosedSkippedHosts,
            DismissedSkippedHosts = plan.DismissedSkippedHosts,
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
            AssigneeCannotHandle = plan.CannotHandle,
            SuggestedDueDate = SuggestedDueDateOf(plan)
        };
    }

    /// <summary>
    /// 建議期限（預覽用，P-1）：該問題在「會寫入的主機」上最近一次出現的最高嚴重度，自今天（站台時鐘）起算預設天數
    /// （<see cref="DefaultDueDays"/>，與夜間派工同一個常數來源）；低、沒有會寫入的主機或推導不到嚴重度為 null。
    /// 只是預填，建立時仍以請求的 <c>DueDate</c> 為準。
    /// </summary>
    private DateTime? SuggestedDueDateOf(CreatePlan plan)
    {
        var hostIds = plan.Hosts.Where(h => h.Effective.Count > 0).Select(h => h.Resolved.Host.HostId).ToHashSet();
        if (hostIds.Count == 0) return null;

        var occurrences = _aggregates.LatestOccurrences(
            IssueExclusion.None, new[] { (plan.Source, plan.EventId) }, plan.Scope.From, plan.Scope.To, hostIds.ToList());
        var ranks = occurrences.Where(o => hostIds.Contains(o.HostId)).Select(o => o.SeverityRank).ToList();
        if (ranks.Count == 0) return null;
        return DefaultDueDays.DueDateFor(LegacySeverityRank.ToSeverity(ranks.Max()), _clock.LocalToday());
    }

    public CreateWorkOrderResultDto Create(CreateWorkOrderRequest req)
    {
        var plan = Plan(req);
        if (plan.Hosts.All(h => h.Resolved.Members.Count == 0))
            throw DomainException.Validation(plan.ClosedSkippedHosts + plan.DismissedSkippedHosts > 0
                ? "符合條件的主機都已處理完畢，或被「不再自動派工」擋住；要重新交辦請在預覽勾選這些主機的「重新交辦」。"
                : "找不到任何符合條件、且您有權限的主機。");

        var actor = _currentUser.ToWorkOrderActor(_clock);
        var scopeKind = req.ScopeKind;
        var scopeGroupIds = scopeKind == WorkOrderScopes.Groups ? req.GroupIds!.Distinct().ToList() : new List<long>();
        // 手動排除過主機就不續掛：被排除的主機之後再出現會被續掛加回，與排除的意圖矛盾
        var autoAttach = scopeKind != WorkOrderScopes.Hosts && req.AutoAttach && req.ExcludeHostIds is not { Count: > 0 };

        var orders = new List<WorkOrderCreatedDto>();
        var raceSkipped = new List<WorkOrderConflict>();
        var overlong = new List<WorkOrderOverlongDto>();
        var daySyncPending = 0;
        var closedByCoordinator = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var closedByCoordinatorNames = new List<string>();
        // 協調器逐成員略過的已結案（主機＋簽章）：同一台的其他成員仍可能寫入，恢復判定要逐成員，不能整台移除
        var closedMembers = new List<(string HostName, string IssueKey)>();
        // 實際寫入的（主機, 簽章）：不再打擾的成員被重開後，只有真的建了案件的才恢復
        var written = new HashSet<(string HostName, string IssueKey)>(HostIssueSignatureKeyComparer.Instance);
        foreach (var allocation in plan.Allocations)
        {
            var outcome = _coordinator.Create(new WorkOrderCreateRequest
            {
                Source = plan.Source, EventId = plan.EventId, IssueLabel = plan.IssueLabel,
                HandlerId = allocation.Handler.UserId, Origin = WorkOrderOrigins.Manual,
                ScopeKind = scopeKind, ScopeGroupIds = scopeGroupIds, AutoAttach = autoAttach,
                Note = req.Note, DueDate = req.DueDate,
                ReassignConflicts = req.ReassignConflicts,
                Members = allocation.Hosts.SelectMany(h => h.Effective).ToList(),
                Actor = actor
            });

            daySyncPending += outcome.DaySync.PendingCases;
            raceSkipped.AddRange(outcome.SkippedConflicts);
            overlong.AddRange(OverlongDtos(outcome));
            foreach (var closed in outcome.SkippedClosed)
            {
                if (closedByCoordinator.Add(HostNameKey.Of(closed.HostName))) closedByCoordinatorNames.Add(closed.HostName);
                closedMembers.Add((HostNameKey.Of(closed.HostName), closed.IssueKey));
            }
            if (outcome.WorkOrderId == 0) continue;
            foreach (var member in allocation.Hosts.SelectMany(h => h.Effective))
                written.Add((HostNameKey.Of(member.HostName), member.IssueKey));

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

        foreach (var conflict in raceSkipped) written.Remove((HostNameKey.Of(conflict.HostName), conflict.IssueKey));
        foreach (var closed in closedMembers) written.Remove(closed);
        RestoreDismissed(plan, written);

        var handlerNames = plan.Allocations.Select(a => NameOf(a.Handler)).ToList();
        var addedHosts = plan.Allocations.Sum(a => a.Hosts.Count);
        _audit.Record(
            action: AuditActions.WorkOrderCreate,
            summary: $"交辦『{plan.IssueLabel}』給 {NameFormat.Join(handlerNames)}：" +
                     $"新建 {orders.Count(o => o.CreatedOrder)} 張、併入 {orders.Count(o => !o.CreatedOrder)} 張，" +
                     $"加入 {addedHosts} 台（略過 {skipped.Count} 台、雜訊排除 {plan.NoiseExcludedHosts} 台）" +
                     (plan.ClosedSkippedHosts + plan.DismissedSkippedHosts > 0
                         ? $"，已結案略過 {plan.ClosedSkippedHosts} 台、不再打擾略過 {plan.DismissedSkippedHosts} 台"
                         : ""),
            targetKind: TargetKind,
            targetId: $"{plan.Source}/{plan.EventId}",
            detail: new
            {
                plan.Source, plan.EventId,
                WorkOrderIds = orders.Select(o => o.WorkOrderId).ToList(),
                PerHandler = plan.Allocations.Select(a => new { Handler = a.Handler.Account, Hosts = a.Hosts.Count }).ToList(),
                Skipped = skipped.Count,
                NoiseExcluded = plan.NoiseExcludedHosts,
                ClosedSkipped = plan.ClosedSkippedHosts,
                DismissedSkipped = plan.DismissedSkippedHosts
            });

        foreach (var order in orders)
        {
            NotifyHandler(WorkOrderOutboxKinds.Created, order.WorkOrderId, order.HandlerId, immediate: req.NotifyNow);
        }

        return new CreateWorkOrderResultDto
        {
            Orders = orders,
            Skipped = skipped,
            OverlongSkipped = overlong,
            NoiseExcludedHosts = plan.NoiseExcludedHosts,
            ClosedSkippedHosts = plan.ClosedSkippedHosts + closedByCoordinator.Count,
            DismissedSkippedHosts = plan.DismissedSkippedHosts,
            ClosedSkippedHostNames = SkippedHostNames(plan, ClosedReasons.Closed, closedByCoordinatorNames),
            DismissedSkippedHostNames = SkippedHostNames(plan, ClosedReasons.Dismissed, Array.Empty<string>()),
            DaySyncPendingCases = daySyncPending,
            AssigneeNoAccess = plan.NoAccess.Take(NoAccessListLimit).ToList(),
            AssigneeNoAccessTotal = plan.NoAccess.Count,
            AssigneeCannotHandle = plan.CannotHandle,
            HandlerHasNoEmail = plan.Allocations.Any(a => string.IsNullOrWhiteSpace(a.Handler.Email))
        };
    }

    /// <summary>
    /// 重新交辦「不再打擾」的成員後恢復：建案成功的成員，把被擋的那件舊案件 <see cref="IssueCase.BlocksRedispatch"/> 寫成 false
    /// （做法與 <see cref="DismissedIssueService.Restore"/> 相同），並寫一筆恢復稽核。只在 <see cref="Create"/> 呼叫，預覽不寫資料。
    /// </summary>
    private void RestoreDismissed(CreatePlan plan, HashSet<(string HostName, string IssueKey)> written)
    {
        var restored = new List<IssueCase>();
        foreach (var host in plan.Hosts)
        {
            foreach (var (issueKey, blocked) in host.Resolved.ReopenedDismissed)
            {
                if (!written.Contains((HostNameKey.Of(host.Resolved.Host.HostName), issueKey))) continue;
                var fresh = _cases.Get(blocked.CaseId);
                if (fresh == null || fresh.BlocksRedispatch == false) continue;
                fresh.BlocksRedispatch = false;
                restored.Add(fresh);
            }
        }
        if (restored.Count == 0) return;

        _cases.SaveMany(restored);
        var hostNames = restored.Select(c => c.HostName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
        _audit.Record(
            action: AuditActions.WorkOrderDismissRestore,
            summary: $"重新交辦恢復自動派工：問題 {plan.IssueLabel}（{hostNames.Count} 台）",
            targetKind: TargetKind,
            targetId: $"{plan.Source}/{plan.EventId}",
            detail: new { Issue = plan.IssueLabel, Hosts = hostNames.Count, HostNames = hostNames, CaseIds = restored.Select(c => c.CaseId).ToList() });
    }

    /// <summary>協調器略過的超長問題鍵，轉成給使用者看的說明（只含主機、長度與上限，不含例外原文）</summary>
    private static List<WorkOrderOverlongDto> OverlongDtos(WorkOrderMemberOutcome outcome) =>
        outcome.SkippedOverlong.Select(o => new WorkOrderOverlongDto
        {
            HostName = o.HostName,
            Message = $"主機「{o.HostName}」的問題簽章過長（{o.KeyLength} 字，上限 {o.MaxLength} 字），未建立案件。"
        }).ToList();

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

        var resolved = ResolveMembers(order.SourceName, order.EventId.Value, scope, new HashSet<long>(), new HashSet<long>());
        var members = resolved.SelectMany(r => r.Members).ToList();
        if (members.Count == 0)
            throw DomainException.Validation("找不到任何符合條件、且您有權限的主機。");

        var outcome = _coordinator.Append(id, members, req.ReassignConflicts, _currentUser.ToWorkOrderActor(_clock));
        var noiseExcluded = resolved.Count(r => r.NoiseExcluded);

        _audit.Record(
            action: AuditActions.WorkOrderAppend,
            summary: $"交辦單 {id}『{LabelOf(order)}』追加主機：新案件 {outcome.NewCases} 件、改連 {outcome.LinkedExisting} 件、" +
                     $"改派 {outcome.Reassigned} 件（略過 {outcome.SkippedConflicts.Count} 件、雜訊排除 {noiseExcluded} 台、無權限忽略 {ignored} 台）",
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new
            {
                WorkOrderId = id, outcome.NewCases, outcome.LinkedExisting, outcome.Reassigned,
                Skipped = outcome.SkippedConflicts.Count, NoiseExcluded = noiseExcluded, Ignored = ignored
            });

        var added = outcome.NewCases + outcome.LinkedExisting + outcome.Reassigned;
        if (added > 0)
        {
            _mail.EnqueueWorkOrderNotice(WorkOrderOutboxKinds.Appended, id, order.HandlerId, _currentUser.UserId, _currentUser.Account, memberDelta: added);
        }

        return new AppendWorkOrderResultDto
        {
            WorkOrderId = id,
            NewCases = outcome.NewCases,
            LinkedExisting = outcome.LinkedExisting,
            Reassigned = outcome.Reassigned,
            SkippedConflicts = outcome.SkippedConflicts.Count,
            OverlongSkipped = OverlongDtos(outcome),
            NoiseExcludedHosts = noiseExcluded,
            IgnoredHosts = ignored,
            DaySyncPendingCases = outcome.DaySync.PendingCases
        };
    }

    public WorkOrderMoveResultDto Reassign(long id, ReassignWorkOrderRequest req)
    {
        var order = GetOrder(id);
        var handler = ResolveActiveHandler(req.HandlerId);
        if (handler.UserId == order.HandlerId)
            throw DomainException.Validation($"{NameOf(handler)} 已經是這張交辦單的處理人。");

        var result = _coordinator.Reassign(id, handler.UserId, _currentUser.ToWorkOrderActor(_clock));

        _audit.Record(
            action: AuditActions.WorkOrderReassign,
            summary: $"交辦單 {id}『{LabelOf(order)}』改派給 {NameOf(handler)}：移動 {result.MovedCases} 件" +
                     (result.MergedIntoExisting ? $"，併入單號 {result.TargetWorkOrderId}" : ""),
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new
            {
                WorkOrderId = id, result.PreviousHandlerId, NewHandler = handler.Account,
                result.TargetWorkOrderId, result.MergedIntoExisting, result.MovedCases
            });

        NotifyMovedTarget(result, handler.UserId, immediate: req.NotifyNow);
        NotifyHandler(WorkOrderOutboxKinds.Transferred, id, result.PreviousHandlerId, immediate: req.NotifyNow);

        return ToMoveResult(id, result, handler);
    }

    public WorkOrderMoveResultDto Split(long id, SplitWorkOrderRequest req)
    {
        var order = GetOrder(id);
        var handler = ResolveActiveHandler(req.HandlerId);

        var result = _coordinator.Split(id, req.CaseIds, handler.UserId, _currentUser.ToWorkOrderActor(_clock));

        _audit.Record(
            action: AuditActions.WorkOrderSplit,
            summary: $"交辦單 {id}『{LabelOf(order)}』拆出 {result.MovedCases} 件給 {NameOf(handler)}" +
                     (result.MergedIntoExisting ? $"，併入單號 {result.TargetWorkOrderId}" : $"，新單號 {result.TargetWorkOrderId}"),
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new
            {
                WorkOrderId = id, result.PreviousHandlerId, NewHandler = handler.Account,
                result.TargetWorkOrderId, result.MergedIntoExisting, result.MovedCases, CaseIds = req.CaseIds
            });

        NotifyMovedTarget(result, handler.UserId);
        NotifyHandler(WorkOrderOutboxKinds.Transferred, id, result.PreviousHandlerId);

        return ToMoveResult(id, result, handler);
    }

    public WorkOrderCloseResultDto Cancel(long id, CancelWorkOrderRequest req)
    {
        var reason = RequireReason(req.Reason, "取消交辦單要填原因。");
        var order = GetOrder(id);

        var result = _coordinator.Cancel(id, reason, _currentUser.ToWorkOrderActor(_clock));

        _audit.Record(
            action: AuditActions.WorkOrderCancel,
            summary: $"取消交辦單 {id}『{LabelOf(order)}』：{result.ClosedCases} 件案件調回未處理（原因：{reason}）",
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new { WorkOrderId = id, result.ClosedCases, Reason = reason, result.HandlerId });

        NotifyHandler(WorkOrderOutboxKinds.Cancelled, id, result.HandlerId, reason: reason);

        return new WorkOrderCloseResultDto
        {
            WorkOrderId = id, ClosedCases = result.ClosedCases, DaySyncPendingCases = result.DaySync.PendingCases
        };
    }

    /// <summary>退回處理人：把「無法處理」的成員退回處理中，說明必填（≤500）；通知處理人</summary>
    public WorkOrderReturnResultDto Return(long id, ReturnWorkOrderRequest req)
    {
        var note = RequireReason(req.Note, "退回處理人要填說明。");
        var order = GetOrder(id);

        var result = _coordinator.Return(id, _currentUser.ToWorkOrderActor(_clock), note);

        _audit.Record(
            action: AuditActions.WorkOrderReturn,
            summary: $"退回交辦單 {id}『{LabelOf(order)}』給處理人：{result.ReturnedCases} 件改回處理中（說明：{note}）",
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new { WorkOrderId = id, result.ReturnedCases, Note = note, result.HandlerId });

        NotifyHandler(WorkOrderOutboxKinds.Returned, id, result.HandlerId, reason: note);

        return new WorkOrderReturnResultDto
        {
            WorkOrderId = id, ReturnedCases = result.ReturnedCases, DaySyncPendingCases = result.DaySync.PendingCases
        };
    }

    public WorkOrderCloseResultDto AdminClose(long id, AdminCloseWorkOrderRequest req)
    {
        if (!IssueHandlingStatuses.IsClosed(req.Status))
            throw DomainException.Validation("代為結案的狀態必須是結案類（已處理／不處理／誤報／已知雜訊）。");
        var reason = RequireReason(req.Reason, "代為結案要填原因。");
        var order = GetOrder(id);

        // 「已處理」不是不處理類，勾選值一律忽略；只有不處理類且勾選才擋住之後的派工
        var blocksRedispatch = req.Status != IssueHandlingStatuses.Resolved && req.BlockRedispatch;
        var result = _coordinator.AdminClose(id, req.Status, reason, _currentUser.ToWorkOrderActor(_clock), blocksRedispatch);

        _audit.Record(
            action: AuditActions.WorkOrderAdminClose,
            summary: $"代為結案交辦單 {id}『{LabelOf(order)}』：{result.ClosedCases} 件標為 {req.Status}（原因：{reason}）",
            targetKind: TargetKind,
            targetId: id.ToString(),
            detail: new { WorkOrderId = id, result.ClosedCases, req.Status, Reason = reason, result.HandlerId });

        // 通知處理人：結案狀態、原因，以及問題再出現會不會重新派工（沿用每人一封的摘要）
        _mail.EnqueueWorkOrderNotice(WorkOrderOutboxKinds.AdminClosed, id, result.HandlerId, _currentUser.UserId, _currentUser.Account,
            reason: reason, status: req.Status, blocksRedispatch: blocksRedispatch);

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
        // 主機範圍沒選主機時，下游的範圍解析會把「沒指定」當成「不限」而對全部可見主機建案
        if (req.ScopeKind == WorkOrderScopes.Hosts && req.HostIds is not { Count: > 0 })
            throw DomainException.Validation("交辦範圍為指定主機時，請至少選擇一台主機。");
        if (req.Note is { Length: > NoteMaxLength })
            throw DomainException.Validation($"說明不可超過 {NoteMaxLength} 字。");
        if (req.DueDate is { } due && due.Date < _clock.LocalToday())
            throw DomainException.Validation("預計完成日不可早於今天。");

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
        var reopen = req.ReopenHostIds == null ? new HashSet<long>() : req.ReopenHostIds.ToHashSet();
        var hosts = ResolveMembers(source, eventId, scope, excluded, reopen)
            .Select(r => new PlannedHost(r))
            .ToList();

        // 4. 衝突：一次取剩餘主機的進行中案件
        var withMembers = hosts.Where(h => h.Resolved.Members.Count > 0).ToList();
        var existing = new Dictionary<(string HostName, string IssueKey), IssueCase>(HostIssueSignatureKeyComparer.Instance);
        if (withMembers.Count > 0)
        {
            foreach (var group in _cases.GetOpenMany(withMembers.Select(h => h.Resolved.Host.HostName).ToList(), source, eventId)
                .GroupBy(c => (HostNameKey.Of(c.HostName), c.IssueKey), HostIssueSignatureKeyComparer.Instance))
            {
                existing[group.Key] = group
                    .OrderByDescending(c => c.UpdatedAt)
                    .ThenBy(c => c.IssueKey, StringComparer.Ordinal)
                    .ThenBy(c => c.CaseId, StringComparer.Ordinal)
                    .First();
            }
        }
        IssueCase? ExistingOf(WorkOrderMember m) =>
            existing.TryGetValue((HostNameKey.Of(m.HostName), m.IssueKey), out var c) ? c : null;

        // 5. 分派（以主機為單位；主機已依名稱升冪）
        var assignedMembers = candidates.ToDictionary(c => c.UserId, _ => 0);
        var loads = req.AssignMode == AssignGroup && req.SplitMode == SplitByLoad
            ? _orders.LoadBoard(WorkOrderQueries.DisabledHostKeys(_hosts.GetAll())).ToDictionary(l => l.HandlerId, l => l.ActiveMembers)
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
            // 虛擬負載只計實際會寫入的成員：被略過的衝突主機不該把後續主機推給別人
            assignedMembers[handler.UserId] += host.Effective.Count;
        }

        // 衝突摘要：他人（有處理人）的進行中案件，依原處理人計主機數
        var conflicts = hosts
            .Where(h => h.ConflictHandlerId.HasValue)
            .GroupBy(h => h.ConflictHandlerId!.Value)
            .Select(g => new WorkOrderConflictDto
            {
                HandlerId = g.Key,
                HandlerName = NameFormat.UserLabel(_displayNames, usersById.GetValueOrDefault(g.Key)),
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
            // 名稱每位處理人算一次：顯示名稱規則每次都讀系統設定，逐台呼叫在 4000 台時要數秒
            var candidateName = NameOf(candidate);
            noAccess.AddRange(allocated
                .Where(h => !visible.Contains(h.Resolved.Host.HostId))
                .Select(h => new AssigneeNoAccessDto { HostName = h.Resolved.Host.HostName, HandlerName = candidateName }));

            if (!_capabilities.Resolve(candidate).Contains(Capability.Handle))
                cannotHandle.Add(new AssigneeCannotHandleDto { HandlerName = NameOf(candidate), HostCount = allocated.Count });
        }

        var nameCache = new Dictionary<long, string>();
        return new CreatePlan
        {
            Source = source,
            EventId = eventId,
            IssueLabel = IssueNames.ForRules(KnownIssueCatalog.ResolveRules(_rules), source, eventId),
            Scope = scope,
            Hosts = hosts,
            Allocations = allocations,
            PausedMembersExcluded = paused,
            NoiseExcludedHosts = hosts.Count(h => h.Resolved.NoiseExcluded),
            ManuallyExcludedHosts = hosts.Count(h => h.Resolved.ManuallyExcluded),
            ClosedSkippedHosts = hosts.Count(h => h.Resolved.Exclusion?.Reason == ClosedReasons.Closed),
            DismissedSkippedHosts = hosts.Count(h => h.Resolved.Exclusion?.Reason == ClosedReasons.Dismissed),
            Conflicts = conflicts,
            NoAccess = noAccess,
            CannotHandle = cannotHandle,
            // 依 id 記住已算過的名稱：略過主機清單逐台取原處理人名稱，顯示名稱規則每次都讀系統設定
            NameOf = id => nameCache.TryGetValue(id, out var cached)
                ? cached
                : nameCache[id] = NameFormat.UserLabel(_displayNames, usersById.GetValueOrDefault(id))
        };
    }

    /// <summary>
    /// 出現點解析與排除的唯一一份（建單計畫與追加共用）：一次 LatestOccurrences 取「每台主機 × 完整簽章 ×
    /// 最近出現日」，每個簽章一個成員（TriggerDate＝最近出現日）；手動排除整台；已知雜訊記憶命中的
    /// （主機, 簽章）逐成員扣除，一台主機全部成員都被扣才算雜訊排除一台。
    /// 雜訊之後再扣兩類「已經有人下過結論」的成員（除非該主機在 <paramref name="reopenHostIds"/>）：
    /// 被「不再打擾」擋住的（<see cref="DispatchContext.DismissedKeys"/>，派工閘門同一支判定），以及最近一次出現當天
    /// 已被處理人結案的（<see cref="OccurrenceStatusResolver"/>：沒有進行中案件、逐日處理列是結案類；
    /// 「沒標記但嚴重度不在需處理名單」的預設已處理不算）。兩類都要排除，否則建出的案件逐日同步會略過已結案的日子，
    /// 變成沒有逐日列的案件。一台主機的成員全部被扣才算整台排除，並帶出原因與結案資訊供預覽顯示。回傳依主機名稱升冪。
    /// </summary>
    private List<ResolvedHost> ResolveMembers(
        string source, int eventId, IssueScope scope, IReadOnlySet<long> excludeHostIds, IReadOnlySet<long> reopenHostIds)
    {
        // 靜音不排除（不用 scope.Exclusion）：派工決策以紀錄日判定靜音，已在派工脈絡處理
        var occurrences = _aggregates.LatestOccurrences(
            IssueExclusion.None, new[] { (source, eventId) }, scope.From, scope.To, scope.HostIds, scope.VisibleSeverities, scope.DayRiskLevels);
        if (occurrences.Count == 0) return new List<ResolvedHost>();

        var hostsById = _hosts.GetAll().ToDictionary(h => h.HostId);
        var noise = _noiseMarks.GetAll()
            .Select(m => (HostNameKey.Of(m.HostName), m.IssueKey))
            .ToHashSet(HostIssueSignatureKeyComparer.Instance);
        var label = IssueNames.ForRules(KnownIssueCatalog.ResolveRules(_rules), source, eventId);

        // 每台主機：去重後的簽章（最近出現者）→ 扣掉雜訊後的出現點
        var perHost = occurrences
            .Where(o => hostsById.ContainsKey(o.HostId))
            .GroupBy(o => o.HostId)
            .Select(g =>
            {
                var host = hostsById[g.Key];
                if (excludeHostIds.Contains(host.HostId))
                    return (Host: host, Manual: true, Kept: new List<HostIssueOccurrence>());
                var kept = g
                    .GroupBy(o => o.IssueKey, IssueSignatureKeyComparer.Instance)
                    .Select(k => k.OrderByDescending(o => o.LastSeen).First())
                    .OrderBy(o => o.IssueKey, StringComparer.Ordinal)
                    .Where(o => !noise.Contains((HostNameKey.Of(host.HostName), o.IssueKey)))
                    .ToList();
                return (Host: host, Manual: false, Kept: kept);
            })
            .ToList();

        // 結案類判定：只對還在候選的出現點做（批次載入，查詢次數與主機數無關）。勾了重新交辦的主機也要判，
        // 才知道哪些成員要標記重開
        var judged = perHost.Where(x => !x.Manual && x.Kept.Count > 0).ToList();
        var statusByHost = new Dictionary<long, Dictionary<string, ResolvedOccurrence>>();
        var noWritableDays = new HashSet<(string HostName, string IssueKey)>(HostIssueSignatureKeyComparer.Instance);
        var dismissedByHost = new Dictionary<string, Dictionary<string, IssueCase>>(StringComparer.OrdinalIgnoreCase);
        if (judged.Count > 0)
        {
            var releaseRules = DismissalRelease.LookupFrom(KnownIssueCatalog.ResolveRules(_rules));
            var today = _clock.LocalToday();
            var casesByHost = _cases.GetMany(judged.Select(x => x.Host.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                .GroupBy(c => HostNameKey.Of(c.HostName))
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var x in judged)
            {
                if (!casesByHost.TryGetValue(HostNameKey.Of(x.Host.HostName), out var hostCases)) continue;
                var byKey = DispatchContext.DismissedCases(hostCases, today, releaseRules)
                    .ToDictionary(c => c.IssueKey, c => c, IssueSignatureKeyComparer.Instance);
                if (byKey.Count > 0) dismissedByHost[x.Host.HostName] = byKey;
            }

            foreach (var r in _query.ResolveOccurrenceStatuses(judged.SelectMany(x => x.Kept).ToList(), scope.From, scope.To))
            {
                if (!statusByHost.TryGetValue(r.Host.HostId, out var byKey))
                    statusByHost[r.Host.HostId] = byKey = new Dictionary<string, ResolvedOccurrence>(IssueSignatureKeyComparer.Instance);
                byKey.TryAdd(r.Occurrence.IssueKey, r);
            }

            // 最近出現日已結案還不夠：期間內任一天未結案就照常建案（只寫未結案的日子），所以再問協調器同一份
            // 「沒有可寫入逐日列」的判定，批次一次。兩個條件都成立才算已結案
            var latestClosed = statusByHost.Values
                .SelectMany(d => d.Values)
                .Where(IsLatestDayClosed)
                .Select(r => (r.Host.HostName, r.Occurrence.IssueKey))
                .ToList();
            if (latestClosed.Count > 0) noWritableDays = _coordinator.KeysWithoutWritableDays(latestClosed);
        }

        return perHost
            .Select(x =>
            {
                var host = x.Host;
                if (x.Manual)
                    return new ResolvedHost(host, new List<WorkOrderMember>(), NoiseExcluded: false, ManuallyExcluded: true);

                var reopened = reopenHostIds.Contains(host.HostId);
                var members = new List<WorkOrderMember>();
                var reopenedDismissed = new List<(string IssueKey, IssueCase Case)>();
                HostExclusion? exclusion = null;
                dismissedByHost.TryGetValue(host.HostName, out var dismissed);
                statusByHost.TryGetValue(host.HostId, out var statuses);

                foreach (var o in x.Kept)
                {
                    var member = new WorkOrderMember
                    {
                        HostName = host.HostName, IssueKey = o.IssueKey, IssueLabel = label, TriggerDate = o.LastSeen
                    };
                    IssueCase? blocked = null;
                    dismissed?.TryGetValue(o.IssueKey, out blocked);
                    ResolvedOccurrence? resolved = null;
                    statuses?.TryGetValue(o.IssueKey, out resolved);
                    var isClosed = resolved != null && IsLatestDayClosed(resolved)
                                   && noWritableDays.Contains((host.HostName, o.IssueKey));

                    if (blocked == null && !isClosed)
                    {
                        members.Add(member);
                        continue;
                    }

                    if (reopened)
                    {
                        // 勾了重新交辦：照常納入，並標記要把觸發日改回處理中；不再打擾的記下舊案件，建案成功後恢復
                        member.Reopen = true;
                        members.Add(member);
                        if (blocked != null) reopenedDismissed.Add((o.IssueKey, blocked));
                        continue;
                    }

                    // 不再打擾優先於已結案（它是更具體的原因：處理人判定不處理，且擋住之後的派工）
                    exclusion ??= blocked != null
                        ? new HostExclusion(ClosedReasons.Dismissed, blocked.Status,
                            AccountOf(resolved?.Handling) ?? NullIfBlank(blocked.DaySyncIntent?.ActorAccount), blocked.ClosedAt)
                        : new HostExclusion(ClosedReasons.Closed, resolved!.Handling!.Status,
                            AccountOf(resolved.Handling), AtOf(resolved.Handling));
                }

                var noiseExcluded = x.Kept.Count == 0;
                return new ResolvedHost(host, members, NoiseExcluded: noiseExcluded, ManuallyExcluded: false,
                    Exclusion: members.Count == 0 && !noiseExcluded ? exclusion : null,
                    ReopenedDismissedCases: reopenedDismissed);
            })
            .OrderBy(r => r.Host.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 略過主機名清單（建立結果用）：計畫判定該原因略過的主機，加上協調器保底再擋下的；依名稱排序、不分大小寫去重，
    /// 只列前 <see cref="NoAccessListLimit"/> 筆（與 AssigneeNoAccess 同上限，總數看計數欄位）。
    /// </summary>
    private static List<string> SkippedHostNames(CreatePlan plan, string reason, IEnumerable<string> extra) =>
        plan.Hosts
            .Where(h => h.Resolved.Exclusion?.Reason == reason)
            .Select(h => h.Resolved.Host.HostName)
            .Concat(extra)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(NoAccessListLimit)
            .ToList();

    /// <summary>最近出現日已被處理人結案：沒有進行中案件、逐日處理列是結案類（「沒標記的預設已處理」不算）</summary>
    private static bool IsLatestDayClosed(ResolvedOccurrence r) =>
        r is { OpenCase: null, Handling: not null } && IssueHandlingStatuses.IsClosed(r.Handling.Status);

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string? AccountOf(IssueHandling? handling) => NullIfBlank(handling?.ActorAccount);

    private static DateTime? AtOf(IssueHandling handling) => handling.UpdatedAt == default ? null : handling.UpdatedAt;

    // ── 共用 ────────────────────────────────────────────────────────────────

    private void NotifyHandler(string kind, long workOrderId, long recipientUserId, string? reason = null, bool immediate = false)
    {
        _mail.EnqueueWorkOrderNotice(kind, workOrderId, recipientUserId, _currentUser.UserId, _currentUser.Account, reason: reason, immediate: immediate);
    }

    /// <summary>改派／拆出後通知目標處理人：新建的單是「建立」，併入目標處理人既有的單是「追加」（帶移入台數）。</summary>
    private void NotifyMovedTarget(WorkOrderMoveResult result, long targetHandlerId, bool immediate = false)
    {
        if (result.MergedIntoExisting)
            _mail.EnqueueWorkOrderNotice(WorkOrderOutboxKinds.Appended, result.TargetWorkOrderId, targetHandlerId,
                _currentUser.UserId, _currentUser.Account, memberDelta: result.MovedCases, immediate: immediate);
        else
            NotifyHandler(WorkOrderOutboxKinds.Created, result.TargetWorkOrderId, targetHandlerId, immediate: immediate);
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

    private WorkOrderMoveResultDto ToMoveResult(long id, WorkOrderMoveResult result, WebUser handler) => new()
    {
        WorkOrderId = id,
        TargetWorkOrderId = result.TargetWorkOrderId,
        MergedIntoExisting = result.MergedIntoExisting,
        MovedCases = result.MovedCases,
        PreviousHandlerId = result.PreviousHandlerId,
        HandlerHasNoEmail = string.IsNullOrWhiteSpace(handler.Email),
        AssigneeCannotHandle = _capabilities.Resolve(handler).Contains(Capability.Handle)
            ? new List<AssigneeCannotHandleDto>()
            : new List<AssigneeCannotHandleDto> { new() { HandlerName = NameOf(handler), HostCount = result.MovedCases } }
    };

    private string NameOf(WebUser user) => _displayNames.WithAccount(user.DisplayName, user.Account);

    /// <summary>
    /// <paramref name="Exclusion"/>：整台因結案類處理被排除（成員全被扣）的原因與結案資訊，否則 null。
    /// <paramref name="ReopenedDismissed"/>：勾選重新交辦而納入的「不再打擾」成員（問題鍵 → 被擋住的舊案件），建案成功後恢復用。
    /// </summary>
    private sealed record ResolvedHost(WebHost Host, List<WorkOrderMember> Members, bool NoiseExcluded, bool ManuallyExcluded,
        HostExclusion? Exclusion = null, IReadOnlyList<(string IssueKey, IssueCase Case)>? ReopenedDismissedCases = null)
    {
        public IReadOnlyList<(string IssueKey, IssueCase Case)> ReopenedDismissed =>
            ReopenedDismissedCases ?? Array.Empty<(string, IssueCase)>();
    }

    /// <summary>結案類排除的原因（<see cref="ClosedReasons"/>）與結案資訊；取不到的欄位為 null</summary>
    private sealed record HostExclusion(string Reason, string? Status, string? ByAccount, DateTime? At);

    private static class ClosedReasons
    {
        public const string Closed = "closed";
        public const string Dismissed = "dismissed";
    }

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
        public required int ClosedSkippedHosts { get; init; }
        public required int DismissedSkippedHosts { get; init; }
        public required List<WorkOrderConflictDto> Conflicts { get; init; }
        public required List<AssigneeNoAccessDto> NoAccess { get; init; }
        public required List<AssigneeCannotHandleDto> CannotHandle { get; init; }
        public required Func<long, string> NameOf { get; init; }
    }

    /// <summary>稽核與畫面用的交辦單名稱：以單上的來源與事件重新產生（事件編號 0 附規則名稱），全站同一個出口</summary>
    private string LabelOf(WorkOrder order) => order.DisplayLabel(KnownIssueCatalog.ResolveRules(_rules));
}

