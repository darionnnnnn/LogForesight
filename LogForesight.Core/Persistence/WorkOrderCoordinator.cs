using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 交辦單的唯一寫入入口：建單、追加、改派、拆單、取消、代為結案、回覆、結案推導。
/// Web API 只做授權、解析與稽核，所有「動到交辦單」的操作都呼叫這裡。
///
/// 已確認的事實（不要寫出相反的邏輯）：
///   - 案件沒有重開路徑：全專案沒有把 <see cref="IssueCase.ClosedAt"/> 清回 null 的程式碼
///     （<see cref="IssueCaseCoordinator.SyncStatus"/> 只作用於進行中案件），所以交辦單不做「成員重開→單重開」。
///   - <see cref="IWorkOrderStore.Save"/> 是整列覆寫＋UpdatedAt 併發檢查：一律 Get 讀新值→改→Save，
///     衝突時重讀重做一次，再衝突就讓例外往上拋（<see cref="UpdateOrder"/>）。
///   - 案件 store 的 Save 只在 WorkOrderId 非 null 時寫該欄：連結只會換單、不會清成 null。
///   - 整併完成前可能有 WorkOrderId == null 的進行中案件：一律當「無單」處理。
///   - 詳情頁逐筆標記走 <see cref="IssueCaseCoordinator.SyncStatus"/>，只由呼叫端以 <see cref="TouchReply"/>
///     推進回覆時間，不推導結案；成員因此全結案的單由 <see cref="SweepClosures"/>（背景掃描）補結案。
///   - 授權（回覆只准該單處理人本人）不在這裡，由 Web 層負責。
/// </summary>
public class WorkOrderCoordinator
{
    /// <summary>取單成員的分頁大小（暫定）</summary>
    private const int MemberPageSize = 500;

    private const string ReassignLogNote = "變更案件處理人";

    /// <summary>夜間派工略過原因：交辦單在派工途中已結案（取消、代為結案、移入他單）</summary>
    public const string SkipOrderClosedMidrun = "order_closed_midrun";

    /// <summary>夜間派工計數：交辦單在派工途中已改派，成員依單目前的處理人掛入</summary>
    public const string HandlerChangedMidrun = "handler_changed_midrun";

    /// <summary>
    /// 行程內互斥：Web 操作與夜間派工在同一行程、都經過本類別。會改變單狀態（結案）或處理人的公開方法，
    /// 以及夜間寫成員，「讀單狀態→寫入」整段在鎖內，避免夜間把成員掛進剛被取消／結案的單或以舊處理人寫入。
    /// Monitor 可重入（Reply 內呼叫 RecomputeClosure 不會自鎖）。
    /// </summary>
    private static readonly object OrderMutationLock = new();

    private readonly IWorkOrderStore _orders;
    private readonly IIssueCaseStore _cases;
    private readonly IIssueHandlingStore _issueHandlings;
    private readonly IssueCaseCoordinator _caseCoordinator;
    private readonly IRecordHandlingStore _handlingLog;
    private readonly IHostStore _hosts;

    public WorkOrderCoordinator(
        IWorkOrderStore orders,
        IIssueCaseStore cases,
        IIssueHandlingStore issueHandlings,
        IssueCaseCoordinator caseCoordinator,
        IRecordHandlingStore handlingLog,
        IHostStore hosts)
    {
        _orders = orders;
        _cases = cases;
        _issueHandlings = issueHandlings;
        _caseCoordinator = caseCoordinator;
        _handlingLog = handlingLog;
        _hosts = hosts;
    }

    // ── 建單／追加 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 建單（2.1）：同處理人同問題已有進行中單時不新建、改為併入（範圍聯集、續掛取 OR、備註與期限不覆蓋）。
    /// 兩人同時建同一張單撞部分唯一索引時，重查後改走併入（只重試一次）。
    /// 新建的單在成員處理失敗且零成員時自動以 cancelled 關閉，再重拋。
    /// </summary>
    public WorkOrderMemberOutcome Create(WorkOrderCreateRequest req)
    {
        lock (OrderMutationLock) return CreateLocked(req);
    }

    private WorkOrderMemberOutcome CreateLocked(WorkOrderCreateRequest req)
    {
        if (req.Members.Count == 0)
            throw new ArgumentException("WorkOrderCoordinator：交辦單至少要有一個成員。", nameof(req));

        var actor = req.Actor;

        // 有效成員（新案件＋改連＋改派）為零時不建單、不併入、不寫事件：全被略過的指派
        // 若照建，會留下零成員的進行中空單。預判與寫入共用同一份分類（ClassifyMembers）
        var preview = ClassifyMembers(req.Members, req.Source, req.EventId, req.HandlerId, null, req.ReassignConflicts);
        if (preview.EffectiveCount == 0)
        {
            ValidateMembers(req.Members, req.Source, req.EventId, "（未建立）");
            return new WorkOrderMemberOutcome
            {
                WorkOrderId = 0, CreatedOrder = false, SkippedConflicts = preview.Skipped,
                DaySync = new CaseDaySubmitResult(Inline: true, Rows: 0, PendingCases: 0)
            };
        }

        var draft = new WorkOrder
        {
            SourceName = req.Source, EventId = req.EventId, IssueLabel = req.IssueLabel,
            HandlerId = req.HandlerId, Origin = req.Origin,
            ScopeKind = req.ScopeKind, ScopeGroupIds = req.ScopeGroupIds.ToList(), AutoAttach = req.AutoAttach,
            Note = req.Note, DueDate = req.DueDate,
            CreatedById = actor.ActorId, CreatedByAccount = actor.ActorAccount, CreatedAt = actor.OccurredAt,
            LastAppendedAt = actor.OccurredAt
        };
        var (order, created) = InsertOrAdopt(draft);

        WorkOrderMemberOutcome outcome;
        HashSet<long> sourceOrders;
        try
        {
            (outcome, sourceOrders) = MembersInto(order, req.Members, req.ReassignConflicts, actor);
        }
        catch when (created && CountOf(order.WorkOrderId).Total == 0)
        {
            UpdateOrder(order.WorkOrderId, o =>
            {
                o.ClosedAt = actor.OccurredAt;
                o.ClosedReason = WorkOrderCloseReasons.Cancelled;
                return true;
            });
            AppendEvent(order.WorkOrderId, WorkOrderEventActions.Cancelled, actor, 0, "建單失敗自動關閉");
            throw;
        }

        var added = outcome.NewCases + outcome.LinkedExisting + outcome.Reassigned;
        if (created)
        {
            AppendEvent(order.WorkOrderId, WorkOrderEventActions.Created, actor, added, null);
        }
        else if (added > 0)
        {
            // 併入但沒有東西進來（成員早已在這張單上）：不寫 merged_in、不動範圍與 LastAppendedAt
            UpdateOrder(order.WorkOrderId, o =>
            {
                MergeScope(o, req.ScopeKind, req.ScopeGroupIds, req.AutoAttach);
                o.LastAppendedAt = actor.OccurredAt;
                return true;
            });
            AppendEvent(order.WorkOrderId, WorkOrderEventActions.MergedIn, actor, added, null);
        }

        outcome.CreatedOrder = created;
        RecomputeSources(sourceOrders, order.WorkOrderId, actor.OccurredAt);
        return outcome;
    }

    /// <summary>追加成員（2.2）：單不存在或已結案擲 InvalidOperationException</summary>
    public WorkOrderMemberOutcome Append(long workOrderId, List<WorkOrderMember> members, bool reassignConflicts, WorkOrderActor actor)
    {
        lock (OrderMutationLock) return AppendLocked(workOrderId, members, reassignConflicts, actor);
    }

    private WorkOrderMemberOutcome AppendLocked(long workOrderId, List<WorkOrderMember> members, bool reassignConflicts, WorkOrderActor actor)
    {
        var order = GetActive(workOrderId);

        var (outcome, sourceOrders) = MembersInto(order, members, reassignConflicts, actor);
        var added = outcome.NewCases + outcome.LinkedExisting + outcome.Reassigned;
        if (added > 0)
        {
            // 零有效成員（全被略過或早已在單上）：沒有追加任何東西，不寫事件、不推進 LastAppendedAt
            UpdateOrder(workOrderId, o =>
            {
                o.LastAppendedAt = actor.OccurredAt;
                return true;
            });
            AppendEvent(workOrderId, WorkOrderEventActions.Appended, actor, added, null);
        }

        RecomputeSources(sourceOrders, workOrderId, actor.OccurredAt);
        return outcome;
    }

    /// <summary>成員改連／改派走後的原單逐一重算結案（本單自己不算）</summary>
    private void RecomputeSources(HashSet<long> sourceOrders, long selfId, DateTime occurredAt)
    {
        foreach (var id in sourceOrders.Where(id => id != selfId).OrderBy(id => id))
            RecomputeClosure(id, occurredAt);
    }

    /// <summary>
    /// 成員處理的唯一一份（建單與追加共用）：一次 GetOpenMany 取既有進行中案件配對，
    /// 新案件一次 SubmitCaseDays（指派模式，它會存案件），改連／改派的既有案件一次 SaveMany。
    /// </summary>
    private (WorkOrderMemberOutcome Outcome, HashSet<long> SourceOrders) MembersInto(WorkOrder order, List<WorkOrderMember> members, bool reassignConflicts, WorkOrderActor actor)
    {
        if (order.SourceName == null || order.EventId == null)
            throw new InvalidOperationException($"WorkOrderCoordinator：交辦單 {order.WorkOrderId} 不是單一問題單，不能以問題成員追加。");

        var source = order.SourceName;
        var eventId = order.EventId.Value;

        ValidateMembers(members, source, eventId, order.WorkOrderId.ToString());
        var plan = ClassifyMembers(members, source, eventId, order.HandlerId, order.WorkOrderId, reassignConflicts);

        var outcome = new WorkOrderMemberOutcome { WorkOrderId = order.WorkOrderId, SkippedConflicts = plan.Skipped };
        var sourceOrders = new HashSet<long>();

        var newCases = new List<IssueCase>(plan.New.Count);
        foreach (var member in plan.New)
        {
            var day = member.TriggerDate.Date;
            newCases.Add(new IssueCase
            {
                CaseId = Guid.NewGuid().ToString("n"),
                HostName = member.HostName, IssueKey = member.IssueKey, IssueLabel = member.IssueLabel,
                Status = IssueHandlingStatuses.InProgress, HandlerId = order.HandlerId,
                Note = order.Note, DueDate = order.DueDate,
                FirstLinkedDate = day, LastLinkedDate = day,
                CreatedAt = actor.OccurredAt, CreatedByAccount = actor.ActorAccount, UpdatedAt = actor.OccurredAt,
                WorkOrderId = order.WorkOrderId
            });
        }

        var relinked = plan.Relink;
        foreach (var existing in relinked)
        {
            if (existing.WorkOrderId != null) sourceOrders.Add(existing.WorkOrderId.Value);
            existing.WorkOrderId = order.WorkOrderId;
            existing.UpdatedAt = actor.OccurredAt;
        }

        var reassigned = plan.Reassign;
        foreach (var existing in reassigned)
        {
            if (existing.WorkOrderId != null) sourceOrders.Add(existing.WorkOrderId.Value);
        }

        outcome.DaySync = _caseCoordinator.SubmitCaseDays(newCases, new CaseDayIntent
        {
            Mode = CaseDayModes.Assign, Status = IssueHandlingStatuses.InProgress,
            Note = order.Note, DueDate = order.DueDate,
            ActorId = actor.ActorId, ActorAccount = actor.ActorAccount, OccurredAt = actor.OccurredAt,
            TriggerDate = null
        }, IssueCaseCoordinator.CaseDayInlineRowLimit);

        // 改連不動逐日列（狀態沒變），只存案件
        if (relinked.Count > 0) _cases.SaveMany(relinked);
        ReassignCases(reassigned, order.HandlerId, order.WorkOrderId, actor);

        outcome.NewCases = newCases.Count;
        outcome.LinkedExisting = relinked.Count;
        outcome.Reassigned = reassigned.Count;
        return (outcome, sourceOrders);
    }

    /// <summary>成員驗證：主機存在、簽章與單的問題相符（orderLabel 只用於訊息）</summary>
    private void ValidateMembers(List<WorkOrderMember> members, string source, int eventId, string orderLabel)
    {
        // 主機驗證一次取整份主機表：逐成員 FindByName 會讓查詢次數隨成員數線性增長
        var knownHosts = _hosts.GetAll().Select(h => h.HostName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            if (!knownHosts.Contains(member.HostName))
                throw new InvalidOperationException($"WorkOrderCoordinator：找不到主機「{member.HostName}」。");

            var parsed = IssueSignatureKey.TryParseSignature(member.IssueKey);
            if (parsed == null || parsed.Value.EventId != eventId
                || !string.Equals(parsed.Value.Source, source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"WorkOrderCoordinator：成員「{member.HostName}」的問題簽章與交辦單 {orderLabel} 的問題不符。");
        }
    }

    /// <summary>成員分類的結果：只讀不寫（案件物件尚未被修改）</summary>
    private sealed record MemberPlan(
        List<WorkOrderMember> New, List<IssueCase> Relink, List<IssueCase> Reassign, List<WorkOrderConflict> Skipped)
    {
        public int EffectiveCount => New.Count + Relink.Count + Reassign.Count;
    }

    /// <summary>
    /// 成員分類的唯一一份（建單前的預判與寫入共用）：一次 GetOpenMany 取既有進行中案件配對。
    /// 無進行中案件＝新案件；同處理人＝改連（已在 workOrderId 這張單上的不算）；
    /// 他人且 reassignConflicts＝改派；他人未要求改派＝略過。workOrderId 為 null＝尚未有單。
    /// </summary>
    private MemberPlan ClassifyMembers(List<WorkOrderMember> members, string source, int eventId, long handlerId, long? workOrderId, bool reassignConflicts)
    {
        var existingByKey = new Dictionary<(string HostName, string IssueKey), IssueCase>(MemberKeyComparer.Instance);
        foreach (var openCase in _cases.GetOpenMany(members.Select(m => m.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), source, eventId))
            existingByKey.TryAdd((openCase.HostName, openCase.IssueKey), openCase);

        var plan = new MemberPlan(new List<WorkOrderMember>(), new List<IssueCase>(), new List<IssueCase>(), new List<WorkOrderConflict>());
        var seen = new HashSet<(string HostName, string IssueKey)>(MemberKeyComparer.Instance);

        foreach (var member in members)
        {
            if (!seen.Add((member.HostName, member.IssueKey))) continue;

            if (!existingByKey.TryGetValue((member.HostName, member.IssueKey), out var existing))
            {
                plan.New.Add(member);
                continue;
            }

            if (existing.HandlerId == handlerId)
            {
                if (workOrderId != null && existing.WorkOrderId == workOrderId) continue;
                plan.Relink.Add(existing);
                continue;
            }

            if (!reassignConflicts)
            {
                plan.Skipped.Add(new WorkOrderConflict
                {
                    HostName = existing.HostName, IssueKey = existing.IssueKey, HandlerId = existing.HandlerId
                });
                continue;
            }

            plan.Reassign.Add(existing);
        }

        return plan;
    }

    // ── 改派／拆單 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 整張單改派（2.4）：新處理人對同問題已有進行中單時，成員併入該單、原單以 moved 結案；
    /// 否則成員與單一起換處理人。已結案成員不動。
    /// </summary>
    public WorkOrderMoveResult Reassign(long workOrderId, long newHandlerId, WorkOrderActor actor)
    {
        lock (OrderMutationLock) return ReassignLocked(workOrderId, newHandlerId, actor);
    }

    private WorkOrderMoveResult ReassignLocked(long workOrderId, long newHandlerId, WorkOrderActor actor)
    {
        var order = GetActive(workOrderId);
        var previous = order.HandlerId;
        if (newHandlerId == previous)
            return new WorkOrderMoveResult { TargetWorkOrderId = workOrderId, PreviousHandlerId = previous };

        var members = ActiveMembers(workOrderId);
        var target = order.SourceName != null && order.EventId != null
            ? _orders.GetActiveFor(newHandlerId, order.SourceName, order.EventId.Value)
            : null;

        if (target != null && target.WorkOrderId != workOrderId)
        {
            ReassignCases(members, newHandlerId, target.WorkOrderId, actor);

            UpdateOrder(workOrderId, o =>
            {
                o.ClosedAt = actor.OccurredAt;
                o.ClosedReason = WorkOrderCloseReasons.Moved;
                return true;
            });
            AppendEvent(workOrderId, WorkOrderEventActions.Reassigned, actor, -members.Count, $"移入單號 {target.WorkOrderId}");

            UpdateOrder(target.WorkOrderId, o =>
            {
                MergeScope(o, order.ScopeKind, order.ScopeGroupIds, order.AutoAttach);
                o.LastAppendedAt = actor.OccurredAt;
                return true;
            });
            AppendEvent(target.WorkOrderId, WorkOrderEventActions.MergedIn, actor, members.Count, null);

            return new WorkOrderMoveResult
            {
                TargetWorkOrderId = target.WorkOrderId, MergedIntoExisting = true,
                MovedCases = members.Count, PreviousHandlerId = previous
            };
        }

        ReassignCases(members, newHandlerId, workOrderId, actor);
        UpdateOrder(workOrderId, o =>
        {
            o.HandlerId = newHandlerId;
            return true;
        });
        AppendEvent(workOrderId, WorkOrderEventActions.Reassigned, actor, 0, $"{previous}→{newHandlerId}");

        return new WorkOrderMoveResult
        {
            TargetWorkOrderId = workOrderId, MergedIntoExisting = false,
            MovedCases = members.Count, PreviousHandlerId = previous
        };
    }

    /// <summary>
    /// 拆單（2.5）：選中的進行中成員改派給新處理人，併入其同問題進行中單（沒有就新建）。
    /// 任一選中案件不屬於本單或已結案→整筆不做。
    /// </summary>
    public WorkOrderMoveResult Split(long workOrderId, IReadOnlyCollection<string> caseIds, long newHandlerId, WorkOrderActor actor)
    {
        lock (OrderMutationLock) return SplitLocked(workOrderId, caseIds, newHandlerId, actor);
    }

    private WorkOrderMoveResult SplitLocked(long workOrderId, IReadOnlyCollection<string> caseIds, long newHandlerId, WorkOrderActor actor)
    {
        if (caseIds.Count == 0)
            throw new ArgumentException("WorkOrderCoordinator：拆單至少要選一個案件。", nameof(caseIds));

        var order = GetActive(workOrderId);
        if (newHandlerId == order.HandlerId)
            throw new InvalidOperationException("WorkOrderCoordinator：拆單的新處理人與目前處理人相同。");

        var selected = SelectMembers(workOrderId, caseIds);

        var (target, created) = InsertOrAdopt(new WorkOrder
        {
            SourceName = order.SourceName, EventId = order.EventId, IssueLabel = order.IssueLabel,
            HandlerId = newHandlerId, Origin = order.Origin,
            ScopeKind = WorkOrderScopes.Hosts, ScopeGroupIds = new List<long>(), AutoAttach = false,
            Note = order.Note, DueDate = order.DueDate,
            CreatedById = actor.ActorId, CreatedByAccount = actor.ActorAccount, CreatedAt = actor.OccurredAt,
            LastAppendedAt = actor.OccurredAt
        });
        if (created)
        {
            AppendEvent(target.WorkOrderId, WorkOrderEventActions.Created, actor, 0, null);
        }
        else
        {
            UpdateOrder(target.WorkOrderId, o =>
            {
                o.LastAppendedAt = actor.OccurredAt;
                return true;
            });
        }

        ReassignCases(selected, newHandlerId, target.WorkOrderId, actor);
        AppendEvent(workOrderId, WorkOrderEventActions.SplitOut, actor, -selected.Count, null);
        AppendEvent(target.WorkOrderId, WorkOrderEventActions.SplitIn, actor, selected.Count, null);
        RecomputeClosure(workOrderId, actor.OccurredAt);

        return new WorkOrderMoveResult
        {
            TargetWorkOrderId = target.WorkOrderId, MergedIntoExisting = !created,
            MovedCases = selected.Count, PreviousHandlerId = order.HandlerId
        };
    }

    /// <summary>
    /// 批次改派的唯一一份（改派、拆單、成員改派共用）：逐案語意等同 <see cref="IssueCaseCoordinator.ReassignCase"/>
    /// ——只改處理人（與所屬單），逐日列不動，在 LastLinkedDate 那天記一筆 CaseReassign 歷程。
    /// 案件一次 SaveMany，不逐案讀取（逐案 GetOpen 會讓查詢次數隨案件數線性增長）。
    /// </summary>
    private void ReassignCases(IReadOnlyList<IssueCase> cases, long newHandlerId, long workOrderId, WorkOrderActor actor)
    {
        if (cases.Count == 0) return;

        foreach (var issueCase in cases)
        {
            issueCase.HandlerId = newHandlerId;
            issueCase.WorkOrderId = workOrderId;
            issueCase.UpdatedAt = actor.OccurredAt;
        }
        _cases.SaveMany(cases);

        foreach (var issueCase in cases)
        {
            _handlingLog.AppendLog(new RecordHandlingLog
            {
                HostName = issueCase.HostName, Date = issueCase.LastLinkedDate, Status = issueCase.Status,
                IssueKey = issueCase.IssueKey, IssueLabel = issueCase.IssueLabel, Note = ReassignLogNote,
                ActorId = actor.ActorId, ActorAccount = actor.ActorAccount,
                Action = HandlingActions.CaseReassign, CreatedAt = actor.OccurredAt
            });
        }
    }

    // ── 夜間派工 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 夜間派工建單：決策必須是 CreateFor。一律 Hosts＋不續掛——之後同問題的新主機由派工的
    /// 「該人此問題已有進行中單→掛入」接上，不靠範圍續掛（自動派工的處理人不一定看得到全部主機）。
    /// 撞部分唯一索引時採用既有單、不寫 created；成員數由 <see cref="RecordNightlyAppended"/> 記。
    /// </summary>
    public (WorkOrder Order, bool Created) EnsureNightlyOrder(DispatchDecision decision, LogIssueSignature issue, DateTime occurredAt)
    {
        lock (OrderMutationLock) return EnsureNightlyOrderLocked(decision, issue, occurredAt);
    }

    private (WorkOrder Order, bool Created) EnsureNightlyOrderLocked(DispatchDecision decision, LogIssueSignature issue, DateTime occurredAt)
    {
        if (decision.Kind != DispatchDecisionKind.CreateFor)
            throw new ArgumentException("WorkOrderCoordinator：夜間建單的決策必須是 CreateFor。", nameof(decision));

        var (order, created) = InsertOrAdopt(new WorkOrder
        {
            SourceName = issue.Source, EventId = issue.EventId, IssueLabel = issue.SourceEventLabel,
            HandlerId = decision.HandlerId!.Value, Origin = decision.Origin!,
            ScopeKind = WorkOrderScopes.Hosts, ScopeGroupIds = new List<long>(), AutoAttach = false,
            Note = NightlyNoteOf(decision.Origin!), DueDate = null,
            CreatedById = null, CreatedByAccount = AuditActions.SystemAccount, CreatedAt = occurredAt,
            LastAppendedAt = occurredAt
        });
        if (created)
            AppendEvent(order.WorkOrderId, WorkOrderEventActions.Created, NightlyActor(occurredAt), 0, null);
        return (order, created);
    }

    /// <summary>
    /// 夜間派工寫成員：每個成員建一件進行中案件與當日一列（只掛當日，不回溯歷史——不走
    /// <see cref="IssueCaseCoordinator.SubmitCaseDays"/>），歷程動作依決策步驟。案件、逐日列各一次 SaveMany。
    ///
    /// 派工脈絡是開跑時的快照，途中管理者可能取消／代為結案／改派。鎖內先一次查出目前進行中的單再逐一判斷：
    ///   - 單已不在進行中（結案、取消、移入他單）→ 該成員不寫入（回傳 null，呼叫端計 <see cref="SkipOrderClosedMidrun"/>）。
    ///     不必另外補救：問題沒被寫成案件，下一次夜間派工會把它當成未指派的問題重新評估。
    ///   - 單的處理人與成員記錄的不同（途中改派）→ 照寫入該單，但以單目前的處理人為準
    ///     （回傳新處理人，呼叫端計 <see cref="HandlerChangedMidrun"/>）。
    /// 回傳與 members 等長：每個成員實際寫入的處理人，null＝未寫入。
    /// </summary>
    public IReadOnlyList<long?> WriteNightlyMembers(
        WebHost host, DateTime date,
        IReadOnlyList<(LogIssueSignature Issue, long WorkOrderId, long HandlerId, string Step)> members,
        DateTime occurredAt)
    {
        lock (OrderMutationLock) return WriteNightlyMembersLocked(host, date, members, occurredAt);
    }

    private IReadOnlyList<long?> WriteNightlyMembersLocked(
        WebHost host, DateTime date,
        IReadOnlyList<(LogIssueSignature Issue, long WorkOrderId, long HandlerId, string Step)> members,
        DateTime occurredAt)
    {
        var written = new List<long?>(members.Count);
        if (members.Count == 0) return written;

        // 只重讀本批成員涉及的單（一個主機日通常只有個位數張）：每個主機日都讀全部進行中的單，
        // 在數千台×數百張進行中單的站台上是整晚重複的全表讀
        var activeHandlers = members.Select(m => m.WorkOrderId).Distinct()
            .Select(id => _orders.Get(id))
            .Where(o => o != null && o.ClosedAt == null)
            .ToDictionary(o => o!.WorkOrderId, o => o!.HandlerId);

        var day = date.Date;
        var cases = new List<IssueCase>(members.Count);
        var rows = new List<IssueHandling>(members.Count);
        var logs = new List<RecordHandlingLog>(members.Count);
        foreach (var (issue, workOrderId, _, step) in members)
        {
            if (!activeHandlers.TryGetValue(workOrderId, out var handlerId))
            {
                written.Add(null);
                continue;
            }
            written.Add(handlerId);

            var key = IssueSignatureKey.For(issue);
            var note = NightlyNoteOf(step);
            var caseId = Guid.NewGuid().ToString("n");

            cases.Add(new IssueCase
            {
                CaseId = caseId, HostName = host.HostName, IssueKey = key, IssueLabel = issue.SourceEventLabel,
                Status = IssueHandlingStatuses.InProgress, HandlerId = handlerId, Note = note,
                FirstLinkedDate = day, LastLinkedDate = day,
                CreatedAt = occurredAt, UpdatedAt = occurredAt, CreatedByAccount = string.Empty,
                WorkOrderId = workOrderId
            });
            rows.Add(new IssueHandling
            {
                HostName = host.HostName, Date = day, IssueKey = key, Status = IssueHandlingStatuses.InProgress,
                Note = note, DueDate = null, CaseId = caseId,
                ActorId = null, ActorAccount = string.Empty, UpdatedAt = occurredAt
            });
            logs.Add(new RecordHandlingLog
            {
                HostName = host.HostName, Date = day, Status = IssueHandlingStatuses.InProgress, IssueKey = key,
                IssueLabel = issue.SourceEventLabel, Note = note,
                ActorId = null, ActorAccount = string.Empty,
                Action = NightlyActionOf(step), CreatedAt = occurredAt
            });
        }

        if (cases.Count > 0)
        {
            _cases.SaveMany(cases);
            _issueHandlings.SaveMany(rows);
            _handlingLog.AppendLogs(logs);
        }
        return written;
    }

    /// <summary>夜間派工一趟結束：對本趟有新增成員的單記一筆 appended 並推進 LastAppendedAt</summary>
    public void RecordNightlyAppended(long workOrderId, int memberDelta, DateTime occurredAt)
    {
        AppendEvent(workOrderId, WorkOrderEventActions.Appended, NightlyActor(occurredAt), memberDelta, "夜間派工");
        UpdateOrder(workOrderId, o =>
        {
            o.LastAppendedAt = occurredAt;
            return true;
        });
    }

    private static WorkOrderActor NightlyActor(DateTime occurredAt) =>
        new() { ActorId = null, ActorAccount = AuditActions.SystemAccount, OccurredAt = occurredAt };

    /// <summary>夜間派工系統說明的唯一一份（建單備註、案件與逐日列備註共用）</summary>
    private static string NightlyNoteOf(string step) => step switch
    {
        WorkOrderOrigins.OwnerRule => "系統依問題檔案自動派送",
        WorkOrderOrigins.AutoDispatch => "系統自動派工",
        WorkOrderDispatcher.StepAttach => "系統依交辦單續掛",
        _ => throw new ArgumentException($"WorkOrderCoordinator：不支援的夜間派工步驟「{step}」。", nameof(step))
    };

    /// <summary>夜間派工歷程動作的唯一一份</summary>
    private static string NightlyActionOf(string step) => step switch
    {
        WorkOrderOrigins.OwnerRule => HandlingActions.OwnerAutoAssign,
        WorkOrderOrigins.AutoDispatch => HandlingActions.AutoDispatch,
        WorkOrderDispatcher.StepAttach => HandlingActions.WorkOrderAttach,
        _ => throw new ArgumentException($"WorkOrderCoordinator：不支援的夜間派工步驟「{step}」。", nameof(step))
    };

    // ── 取消／代為結案／回覆 ─────────────────────────────────────────────────

    /// <summary>取消（2.6）：進行中成員標取消並結案，逐日列只把本案件擁有、未結案的日子調回 open</summary>
    public WorkOrderCloseResult Cancel(long workOrderId, string reason, WorkOrderActor actor)
    {
        lock (OrderMutationLock) return CancelLocked(workOrderId, reason, actor);
    }

    private WorkOrderCloseResult CancelLocked(long workOrderId, string reason, WorkOrderActor actor)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("WorkOrderCoordinator：取消交辦單要填原因。", nameof(reason));

        var order = GetActive(workOrderId);
        var members = ActiveMembers(workOrderId);
        foreach (var member in members)
        {
            member.Cancelled = true;
            member.Status = IssueHandlingStatuses.Open;
            member.Note = null;
            member.DueDate = null;
            member.ClosedAt = actor.OccurredAt;
            member.UpdatedAt = actor.OccurredAt;
        }

        var daySync = _caseCoordinator.SubmitCaseDays(members, new CaseDayIntent
        {
            Mode = CaseDayModes.Cancel, Status = IssueHandlingStatuses.Open, Note = reason,
            ActorId = actor.ActorId, ActorAccount = actor.ActorAccount, OccurredAt = actor.OccurredAt
        }, IssueCaseCoordinator.CaseDayInlineRowLimit);

        CloseOrder(workOrderId, WorkOrderCloseReasons.Cancelled, WorkOrderEventActions.Cancelled, actor, -members.Count, reason);
        return new WorkOrderCloseResult { ClosedCases = members.Count, DaySync = daySync, HandlerId = order.HandlerId };
    }

    /// <summary>代為結案（2.7）：進行中成員全部標成指定的結案狀態</summary>
    public WorkOrderCloseResult AdminClose(long workOrderId, string status, string reason, WorkOrderActor actor)
    {
        lock (OrderMutationLock) return AdminCloseLocked(workOrderId, status, reason, actor);
    }

    private WorkOrderCloseResult AdminCloseLocked(long workOrderId, string status, string reason, WorkOrderActor actor)
    {
        if (!IssueHandlingStatuses.IsClosed(status))
            throw new ArgumentException($"WorkOrderCoordinator：代為結案的狀態「{status}」不是結案類。", nameof(status));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("WorkOrderCoordinator：代為結案要填原因。", nameof(reason));

        var order = GetActive(workOrderId);
        var members = ActiveMembers(workOrderId);
        var daySync = ApplyStatus(members, status, reason, null, actor);

        CloseOrder(workOrderId, WorkOrderCloseReasons.AdminClosed, WorkOrderEventActions.AdminClosed, actor, -members.Count, $"{status}：{reason}");
        return new WorkOrderCloseResult { ClosedCases = members.Count, DaySync = daySync, HandlerId = order.HandlerId };
    }

    /// <summary>
    /// 處理人回覆（2.8）：授權（只有處理人本人）由呼叫端負責，這裡只驗資料。
    /// caseIds 為 null 或空＝全部進行中成員；任一不屬於本單或已結案→整筆不做。
    /// 成功後寫一筆 replied 事件（時間軸用；逐日明細仍由逐日歷程提供）。
    /// </summary>
    public WorkOrderReplyResult Reply(long workOrderId, IReadOnlyCollection<string>? caseIds, string status, string? note, DateTime? dueDate, WorkOrderActor actor)
    {
        lock (OrderMutationLock) return ReplyLocked(workOrderId, caseIds, status, note, dueDate, actor);
    }

    private WorkOrderReplyResult ReplyLocked(long workOrderId, IReadOnlyCollection<string>? caseIds, string status, string? note, DateTime? dueDate, WorkOrderActor actor)
    {
        if (!IssueHandlingStatuses.IsValid(status))
            throw new ArgumentException($"WorkOrderCoordinator：不支援的處理狀態「{status}」。", nameof(status));

        GetActive(workOrderId);
        var members = caseIds == null || caseIds.Count == 0
            ? ActiveMembers(workOrderId)
            : SelectMembers(workOrderId, caseIds);

        var daySync = ApplyStatus(members, status, note, dueDate, actor);

        UpdateOrder(workOrderId, o =>
        {
            o.LastReplyAt = actor.OccurredAt;
            return true;
        });
        // 先寫回覆事件再推導結案：同一時間點的 closed 事件依事件序排在回覆之後
        AppendEvent(workOrderId, WorkOrderEventActions.Replied, actor, 0, ReplyNoteOf(status, note, members.Count));
        var closed = RecomputeClosure(workOrderId, actor.OccurredAt);

        return new WorkOrderReplyResult { Cases = members.Count, WorkOrderClosed = closed, DaySync = daySync };
    }

    /// <summary>
    /// 只推進 LastReplyAt、不寫事件：詳情頁逐筆標記用——逐筆各寫一筆事件會淹沒時間軸。
    /// 單不存在或已結案→直接回傳（不擲）；成員因此全結案的單仍由 <see cref="SweepClosures"/> 補結案。
    /// </summary>
    public void TouchReply(long workOrderId, DateTime occurredAt) => TryMarkReplied(workOrderId, occurredAt);

    /// <summary>推進 LastReplyAt（經併發重試）；單不存在或已結案回 false、不寫</summary>
    private bool TryMarkReplied(long workOrderId, DateTime occurredAt)
    {
        var order = _orders.Get(workOrderId);
        if (order == null || order.ClosedAt != null) return false;

        return UpdateOrder(workOrderId, o =>
        {
            if (o.ClosedAt != null) return false;
            o.LastReplyAt = occurredAt;
            return true;
        });
    }

    /// <summary>回覆事件說明的唯一一份（<see cref="Reply"/> 用）</summary>
    private static string ReplyNoteOf(string status, string? note, int caseCount) =>
        string.IsNullOrWhiteSpace(note)
            ? $"{status}（{caseCount} 台）"
            : $"{status}：{note}（{caseCount} 台）";

    /// <summary>
    /// 目標值規則的唯一一份（回覆與代為結案共用），與 <see cref="IssueCaseCoordinator.SyncStatus"/> 相同：
    /// open 視為 clearing（備註清空）；期限只在 in_progress／observing 保留；結案四態→案件結案。
    /// 逐日列以同步模式一次展開。
    /// </summary>
    private CaseDaySubmitResult ApplyStatus(List<IssueCase> members, string status, string? note, DateTime? dueDate, WorkOrderActor actor)
    {
        var clearing = status == IssueHandlingStatuses.Open;
        var effectiveNote = clearing ? null : note;
        var effectiveDueDate = status is IssueHandlingStatuses.InProgress or IssueHandlingStatuses.Observing ? dueDate : null;
        var closing = IssueHandlingStatuses.IsClosed(status);

        foreach (var member in members)
        {
            member.Status = status;
            member.Note = effectiveNote;
            member.DueDate = effectiveDueDate;
            member.UpdatedAt = actor.OccurredAt;
            if (closing) member.ClosedAt = actor.OccurredAt;
        }

        return _caseCoordinator.SubmitCaseDays(members, new CaseDayIntent
        {
            Mode = CaseDayModes.Sync, Status = status, Note = effectiveNote, DueDate = effectiveDueDate, Clearing = clearing,
            ActorId = actor.ActorId, ActorAccount = actor.ActorAccount, OccurredAt = actor.OccurredAt,
            TriggerDate = null
        }, IssueCaseCoordinator.CaseDayInlineRowLimit);
    }

    // ── 結案推導 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 結案推導（2.9）：零成員→moved；成員全結案→all_closed；否則不動。
    /// 單不存在或已結案回 false（不重複記事件）。
    /// </summary>
    public bool RecomputeClosure(long workOrderId, DateTime occurredAt)
    {
        lock (OrderMutationLock) return RecomputeClosureLocked(workOrderId, occurredAt);
    }

    private bool RecomputeClosureLocked(long workOrderId, DateTime occurredAt)
    {
        var order = _orders.Get(workOrderId);
        if (order == null || order.ClosedAt != null) return false;

        var counts = CountOf(workOrderId);
        string reason;
        if (counts.Total == 0) reason = WorkOrderCloseReasons.Moved;
        else if (counts.Active == 0) reason = WorkOrderCloseReasons.AllClosed;
        else return false;

        var saved = UpdateOrder(workOrderId, o =>
        {
            if (o.ClosedAt != null) return false;
            o.ClosedAt = occurredAt;
            o.ClosedReason = reason;
            return true;
        });
        if (!saved) return false;

        _orders.AppendEvent(new WorkOrderEvent
        {
            WorkOrderId = workOrderId, Action = WorkOrderEventActions.Closed,
            ActorId = null, ActorAccount = AuditActions.SystemAccount, MemberDelta = 0, Note = reason, CreatedAt = occurredAt
        });
        return true;
    }

    /// <summary>
    /// 結案掃描（2.10）：補上「成員在交辦單以外的路徑（詳情頁逐筆標記）全結案」的單。回傳結案數。
    /// </summary>
    public int SweepClosures(int take, DateTime occurredAt)
    {
        var closed = 0;
        foreach (var id in _orders.FindActiveWithoutActiveMembers(take))
        {
            if (RecomputeClosure(id, occurredAt)) closed++;
        }
        return closed;
    }

    // ── 共用 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 範圍聯集的唯一一份：All 優先；否則任一為 Groups 就取群組聯集（Hosts 不貢獻群組）；兩者皆 Hosts 維持 Hosts。
    /// 續掛旗標取 OR。備註與期限不在這裡（併入時不覆蓋既有單）。
    /// </summary>
    private static void MergeScope(WorkOrder target, string incomingKind, IReadOnlyCollection<long> incomingGroupIds, bool incomingAutoAttach)
    {
        target.AutoAttach = target.AutoAttach || incomingAutoAttach;

        if (target.ScopeKind == WorkOrderScopes.All || incomingKind == WorkOrderScopes.All)
        {
            target.ScopeKind = WorkOrderScopes.All;
            target.ScopeGroupIds = new List<long>();
            return;
        }

        var groups = new SortedSet<long>();
        if (target.ScopeKind == WorkOrderScopes.Groups) groups.UnionWith(target.ScopeGroupIds);
        if (incomingKind == WorkOrderScopes.Groups) groups.UnionWith(incomingGroupIds);

        if (target.ScopeKind == WorkOrderScopes.Groups || incomingKind == WorkOrderScopes.Groups)
        {
            target.ScopeKind = WorkOrderScopes.Groups;
            target.ScopeGroupIds = groups.ToList();
        }
    }

    /// <summary>新增單；同處理人同問題已有進行中單（事先查到或撞部分唯一索引後重查）則改用該單</summary>
    private (WorkOrder Order, bool Created) InsertOrAdopt(WorkOrder draft)
    {
        if (draft.SourceName != null && draft.EventId != null)
        {
            var existing = _orders.GetActiveFor(draft.HandlerId, draft.SourceName, draft.EventId.Value);
            if (existing != null) return (existing, false);
        }

        try
        {
            _orders.Insert(draft);
            return (draft, true);
        }
        catch (DbUpdateException) when (draft.SourceName != null && draft.EventId != null)
        {
            // 兩人同時建同一張：部分唯一索引擋下，重查一次改走併入；查不到就不是這個原因，原例外上拋
            var existing = _orders.GetActiveFor(draft.HandlerId, draft.SourceName, draft.EventId.Value);
            if (existing == null) throw;
            return (existing, false);
        }
    }

    private WorkOrder GetActive(long workOrderId)
    {
        var order = _orders.Get(workOrderId)
                    ?? throw new InvalidOperationException($"WorkOrderCoordinator：找不到交辦單 {workOrderId}。");
        if (order.ClosedAt != null)
            throw new InvalidOperationException($"WorkOrderCoordinator：交辦單 {workOrderId} 已結案。");
        return order;
    }

    /// <summary>單的全部進行中成員（分頁取完）</summary>
    private List<IssueCase> ActiveMembers(long workOrderId)
    {
        var result = new List<IssueCase>();
        for (var skip = 0; ; skip += MemberPageSize)
        {
            var page = _cases.GetByWorkOrder(workOrderId, skip, MemberPageSize);
            result.AddRange(page.Where(c => c.ClosedAt == null));
            if (page.Count < MemberPageSize) break;
        }
        return result;
    }

    /// <summary>指定的案件必須全是本單進行中成員，否則整筆不做</summary>
    private List<IssueCase> SelectMembers(long workOrderId, IReadOnlyCollection<string> caseIds)
    {
        var byId = ActiveMembers(workOrderId).ToDictionary(c => c.CaseId, StringComparer.Ordinal);
        var selected = new List<IssueCase>();
        foreach (var caseId in caseIds.Distinct(StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(caseId, out var member))
                throw new InvalidOperationException($"WorkOrderCoordinator：案件 {caseId} 不是交辦單 {workOrderId} 的進行中成員。");
            selected.Add(member);
        }
        return selected;
    }

    /// <summary>單張單的成員計數；store 對零成員的單不回鍵，視為全零</summary>
    private WorkOrderMemberCounts CountOf(long workOrderId) =>
        _orders.CountMembers(new[] { workOrderId }).TryGetValue(workOrderId, out var counts) ? counts : new WorkOrderMemberCounts();

    private void CloseOrder(long workOrderId, string closeReason, string action, WorkOrderActor actor, int memberDelta, string note)
    {
        UpdateOrder(workOrderId, o =>
        {
            o.ClosedAt = actor.OccurredAt;
            o.ClosedReason = closeReason;
            return true;
        });
        AppendEvent(workOrderId, action, actor, memberDelta, note);
    }

    /// <summary>
    /// 讀新值→改→存；併發衝突時重讀重做一次，再衝突讓例外上拋。
    /// mutate 回 false 代表讀到的新值已不需要變更（不存）。回傳是否有存。
    /// </summary>
    private bool UpdateOrder(long workOrderId, Func<WorkOrder, bool> mutate)
    {
        for (var attempt = 0; ; attempt++)
        {
            var fresh = _orders.Get(workOrderId)
                        ?? throw new InvalidOperationException($"WorkOrderCoordinator：找不到交辦單 {workOrderId}。");
            if (!mutate(fresh)) return false;
            try
            {
                _orders.Save(fresh);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                // 重讀重做一次
            }
        }
    }

    private void AppendEvent(long workOrderId, string action, WorkOrderActor actor, int memberDelta, string? note) =>
        _orders.AppendEvent(new WorkOrderEvent
        {
            WorkOrderId = workOrderId, Action = action,
            ActorId = actor.ActorId, ActorAccount = actor.ActorAccount,
            MemberDelta = memberDelta, Note = note, CreatedAt = actor.OccurredAt
        });

    /// <summary>成員配對鍵：主機名不分大小寫、問題鍵 Ordinal（同案件 store 語意）</summary>
    private sealed class MemberKeyComparer : IEqualityComparer<(string HostName, string IssueKey)>
    {
        public static readonly MemberKeyComparer Instance = new();

        public bool Equals((string HostName, string IssueKey) x, (string HostName, string IssueKey) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.HostName, y.HostName)
            && StringComparer.Ordinal.Equals(x.IssueKey, y.IssueKey);

        public int GetHashCode((string HostName, string IssueKey) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.HostName),
                StringComparer.Ordinal.GetHashCode(key.IssueKey));
    }
}
