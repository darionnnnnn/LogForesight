namespace LogForesight.Core.Persistence;

public enum DispatchDecisionKind
{
    Skip,
    AttachTo,
    CreateFor
}

/// <summary>派工決策結果（不含任何寫入；呼叫端依此建單／掛單或略過）</summary>
public sealed class DispatchDecision
{
    public DispatchDecisionKind Kind { get; init; }

    /// <summary>Skip 時的原因：muted／gate_suppressed／gate_noise／gate_severity／disabled／no_candidate</summary>
    public string? SkipReason { get; init; }

    /// <summary>no_candidate 的細分：no_pool／all_paused／no_visibility</summary>
    public string? NoCandidateDetail { get; init; }

    /// <summary>AttachTo 時要掛進的單</summary>
    public long? WorkOrderId { get; init; }

    public long? HandlerId { get; init; }

    /// <summary>CreateFor 時新單的 <see cref="WorkOrderOrigins"/></summary>
    public string? Origin { get; init; }
}

/// <summary>
/// 派工決策純函式：給定一趟執行的 <see cref="DispatchContext"/>，判斷一個（主機, 問題）
/// 該續掛哪張既有單、交給問題負責人、自動派給負載最輕的人，或略過。
/// 不修改脈絡——呼叫端決定是否 <see cref="DispatchContext.Commit"/>。
/// </summary>
public static class WorkOrderDispatcher
{
    public const string SkipMuted = "muted";
    public const string SkipSuppressed = "gate_suppressed";
    public const string SkipNoise = "gate_noise";
    public const string SkipSeverity = "gate_severity";
    public const string SkipDisabled = "disabled";
    public const string SkipNoCandidate = "no_candidate";

    public const string NoCandidateNoPool = "no_pool";
    public const string NoCandidateAllPaused = "all_paused";
    public const string NoCandidateNoVisibility = "no_visibility";

    public static DispatchDecision Decide(DispatchContext ctx, WebHost host, LogIssueSignature issue, DateTime recordDate)
    {
        // ⓪ 靜音
        if (ctx.IsMuted(issue.Source, issue.EventId, recordDate)) return Skip(SkipMuted);

        // 閘門 1～3
        if (issue.Suppressed) return Skip(SkipSuppressed);
        if (ctx.IsNoise(host.HostName, IssueSignatureKey.For(issue))) return Skip(SkipNoise);
        if (!ctx.UnhandledSeverities.Contains(issue.Severity)) return Skip(SkipSeverity);

        var activeOrders = ctx.ActiveOrdersFor(issue.Source, issue.EventId);

        // ④ 續掛：可續掛且範圍涵蓋此主機的進行中單，取建立最晚者
        var attachable = activeOrders
            .Where(o => o.AutoAttach && ScopeCovers(o, host))
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.WorkOrderId)
            .FirstOrDefault();
        if (attachable != null)
        {
            return new DispatchDecision
            {
                Kind = DispatchDecisionKind.AttachTo,
                WorkOrderId = attachable.WorkOrderId,
                HandlerId = attachable.HandlerId
            };
        }

        // ⑤ 問題負責人（不要求在派工池、不檢查可見主機）
        var profile = ctx.ProfileFor(issue.Source, issue.EventId);
        if (profile != null)
        {
            var owners = profile.OwnerUserIds
                .Distinct()
                .Where(id => ctx.Pool.ByUserId.TryGetValue(id, out var c) && !c.Paused)
                .ToList();
            if (owners.Count > 0)
            {
                var ownerId = SelectHandler(ctx, owners, issue.Source, issue.EventId, continuityHandlerId: null);
                return AssignTo(ownerId, activeOrders, WorkOrderOrigins.OwnerRule);
            }
        }

        // ⑥ 自動派工
        if (!ctx.AutoDispatchEnabled) return Skip(SkipDisabled);

        var candidates = ctx.Pool.ByUserId.Values
            .Where(c => c.InPool && !c.Paused && c.VisibleHostIds.Contains(host.HostId))
            .Select(c => c.UserId)
            .ToList();
        if (candidates.Count == 0)
        {
            var detail = ctx.Pool.PoolMemberCount == 0 ? NoCandidateNoPool
                : ctx.Pool.ActivePoolMemberCount == 0 ? NoCandidateAllPaused
                : NoCandidateNoVisibility;
            return new DispatchDecision
            {
                Kind = DispatchDecisionKind.Skip,
                SkipReason = SkipNoCandidate,
                NoCandidateDetail = detail
            };
        }

        var handlerId = SelectHandler(ctx, candidates, issue.Source, issue.EventId,
            ctx.ContinuityHandlerFor(host.HostName, issue.Source, issue.EventId));
        return AssignTo(handlerId, activeOrders, WorkOrderOrigins.AutoDispatch);
    }

    private static bool ScopeCovers(WorkOrder order, WebHost host) => order.ScopeKind switch
    {
        WorkOrderScopes.All => true,
        WorkOrderScopes.Groups => order.ScopeGroupIds.Any(host.GroupIds.Contains),
        _ => false
    };

    /// <summary>
    /// 選人規則（⑤⑥ 共用的唯一實作）：
    /// 1. 本趟已為此問題選中、且在候選內的人 → 其中負載最輕者；
    /// 2. 延續性處理人在候選內 → 他（⑤ 傳 null 不適用）；
    /// 3. 負載最輕者。
    /// </summary>
    private static long SelectHandler(
        DispatchContext ctx, IReadOnlyCollection<long> candidates, string source, int eventId, long? continuityHandlerId)
    {
        var chosen = ctx.ChosenFor(source, eventId);
        var alreadyChosen = candidates.Where(chosen.Contains).ToList();
        if (alreadyChosen.Count > 0) return LightestLoad(ctx, alreadyChosen);

        if (continuityHandlerId is long continuity && candidates.Contains(continuity)) return continuity;

        return LightestLoad(ctx, candidates);
    }

    /// <summary>負載比較：成員數少者優先 → 單數少者優先 → 帳號不分大小寫升冪</summary>
    private static long LightestLoad(DispatchContext ctx, IEnumerable<long> userIds) =>
        userIds
            .OrderBy(id => ctx.LoadOf(id).ActiveMembers)
            .ThenBy(id => ctx.LoadOf(id).ActiveWorkOrders)
            .ThenBy(id => ctx.Pool.ByUserId[id].Account, StringComparer.OrdinalIgnoreCase)
            .First();

    /// <summary>該人此問題已有進行中單（含本趟登記的）→ 掛進去；否則建新單</summary>
    private static DispatchDecision AssignTo(long handlerId, IReadOnlyList<WorkOrder> activeOrders, string origin)
    {
        var existing = activeOrders
            .Where(o => o.HandlerId == handlerId)
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.WorkOrderId)
            .FirstOrDefault();

        return existing != null
            ? new DispatchDecision { Kind = DispatchDecisionKind.AttachTo, WorkOrderId = existing.WorkOrderId, HandlerId = handlerId }
            : new DispatchDecision { Kind = DispatchDecisionKind.CreateFor, HandlerId = handlerId, Origin = origin };
    }

    private static DispatchDecision Skip(string reason) =>
        new() { Kind = DispatchDecisionKind.Skip, SkipReason = reason };
}
