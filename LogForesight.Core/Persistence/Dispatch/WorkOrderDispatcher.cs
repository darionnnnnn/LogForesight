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

    /// <summary>Skip 時的原因：muted／gate_suppressed／gate_noise／gate_severity／gate_dismissed／disabled／no_candidate</summary>
    public string? SkipReason { get; init; }

    /// <summary>no_candidate 的細分：no_pool／all_paused／no_visibility</summary>
    public string? NoCandidateDetail { get; init; }

    /// <summary>AttachTo 時要掛進的單</summary>
    public long? WorkOrderId { get; init; }

    public long? HandlerId { get; init; }

    /// <summary>CreateFor 時新單的 <see cref="WorkOrderOrigins"/></summary>
    public string? Origin { get; init; }

    /// <summary>
    /// 非 Skip 時命中的步驟：<see cref="WorkOrderDispatcher.StepAttach"/>（④ 續掛）、
    /// <see cref="WorkOrderOrigins.OwnerRule"/>（⑤）、<see cref="WorkOrderOrigins.AutoDispatch"/>（⑥）。
    /// ⑤⑥ 掛進該人既有單時也是 AttachTo，只看 Kind 分不出來，歷程動作靠這欄決定。
    /// </summary>
    public string? Step { get; init; }

    /// <summary>本次決策中被擋下的續掛：不論最後由誰接手（或沒人接），都要把原單標示「續掛已停止」</summary>
    public IReadOnlyList<BlockedContinuation> BlockedContinuations { get; internal set; } = Array.Empty<BlockedContinuation>();

    /// <summary>⑥ 自動派工選中的處理人正是這台主機這個問題最近 30 天內以 resolved 結案的處理人（延續性偏好命中）＝復發</summary>
    public bool Recurrence { get; init; }
}

/// <summary>被擋下的一筆續掛（單、處理人、<see cref="ContinuationStopReasons"/> 原因代碼）</summary>
public sealed record BlockedContinuation(long WorkOrderId, long HandlerId, string Reason);

/// <summary>
/// 派工決策純函式：給定一趟執行的 <see cref="DispatchContext"/>，判斷一個（主機, 問題）
/// 該續掛哪張既有單、交給問題負責人、自動派給負載最輕的人，或略過。
/// 不修改脈絡——呼叫端決定是否 <see cref="DispatchContext.Commit"/>。
/// </summary>
public static class WorkOrderDispatcher
{
    public const string SkipUnavailable = "unavailable";
    public const string SkipMuted = "muted";
    public const string SkipSuppressed = "gate_suppressed";
    public const string SkipNoise = "gate_noise";
    public const string SkipSeverity = "gate_severity";
    public const string SkipDismissed = "gate_dismissed";
    public const string SkipDisabled = "disabled";

    /// <summary>決策步驟：④ 續掛（⑤⑥ 沿用 <see cref="WorkOrderOrigins"/> 的值）</summary>
    public const string StepAttach = "attach";
    public const string SkipNoCandidate = "no_candidate";

    public const string NoCandidateNoPool = "no_pool";
    public const string NoCandidateAllPaused = "all_paused";
    public const string NoCandidateNoVisibility = "no_visibility";

    /// <summary>資格不符的原因：不在派工池（只有自動派工要求在池內，不會寫進單）</summary>
    internal const string IneligibleNotInPool = "not_in_pool";

    public static DispatchDecision Decide(DispatchContext ctx, WebHost host, LogIssueSignature issue, DateTime recordDate)
    {
        // 派工脈絡建立失敗：本趟不派工，不查任何資料
        if (ctx.Unavailable) return Skip(SkipUnavailable);

        // ⓪ 靜音
        if (ctx.IsMuted(issue.Source, issue.EventId, recordDate)) return Skip(SkipMuted);

        // 閘門 1～3
        if (issue.Suppressed) return Skip(SkipSuppressed);
        if (ctx.IsNoise(host.HostName, IssueSignatureKey.For(issue))) return Skip(SkipNoise);
        if (!ctx.UnhandledSeverities.Contains(issue.Severity)) return Skip(SkipSeverity);

        // 閘門 4：最近一件以不處理類結案→不再打擾（續掛、負責人、自動派工一律適用）
        if (ctx.IsDismissed(host.HostName, IssueSignatureKey.For(issue))) return Skip(SkipDismissed);

        var activeOrders = ctx.ActiveOrdersFor(issue.Source, issue.EventId);

        // ④ 續掛：可續掛且範圍涵蓋此主機、處理人符合資格（啟用、有處理能力、未暫停、看得到這台）的進行中單，
        //    取處理人負載最輕者（進行中成員數→單數），同分才取建立最晚者。群組分攤會產生同問題多張可續掛單，
        //    只取建立最晚者會把新主機全掛給同一人；本趟決策經 Commit 累計負載，同一趟多台新主機會自然分散。
        //    資格不符的單不續掛、記進 BlockedContinuations，決策往下走 ⑤⑥（被擋與否不影響其他單與後續步驟）。
        var blocked = new List<BlockedContinuation>();
        var attachableOrders = new List<WorkOrder>();
        foreach (var order in activeOrders.Where(o => o.AutoAttach && ScopeCovers(o, host)))
        {
            var reason = IneligibleReason(ctx, order.HandlerId, host, requirePool: false);
            if (reason == null) attachableOrders.Add(order);
            else blocked.Add(new BlockedContinuation(order.WorkOrderId, order.HandlerId, reason));
        }

        var attachable = attachableOrders
            .OrderBy(o => ctx.LoadOf(o.HandlerId).ActiveMembers)
            .ThenBy(o => ctx.LoadOf(o.HandlerId).ActiveWorkOrders)
            .ThenByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.WorkOrderId)
            .FirstOrDefault();
        if (attachable != null)
        {
            return WithBlocked(new DispatchDecision
            {
                Kind = DispatchDecisionKind.AttachTo,
                WorkOrderId = attachable.WorkOrderId,
                HandlerId = attachable.HandlerId,
                Step = StepAttach
            }, blocked);
        }

        // ⑤ 問題負責人（基本資格；不要求在派工池、不檢查可見主機——管理者明確指定的負責人，系統不替他判斷看不看得到）
        var profile = ctx.ProfileFor(issue.Source, issue.EventId);
        if (profile != null)
        {
            var owners = profile.OwnerUserIds
                .Distinct()
                .Where(id => IneligibleReason(ctx, id, null, requirePool: false) == null)
                .ToList();
            if (owners.Count > 0)
            {
                var ownerId = SelectHandler(ctx, owners, issue.Source, issue.EventId, continuityHandlerId: null);
                return WithBlocked(AssignTo(ownerId, activeOrders, WorkOrderOrigins.OwnerRule, false), blocked);
            }
        }

        // ⑥ 自動派工（基本資格＋在派工池＋看得到這台）
        if (!ctx.AutoDispatchEnabled) return WithBlocked(Skip(SkipDisabled), blocked);

        var candidates = ctx.Pool.ByUserId.Values
            .Where(c => IneligibleReason(ctx, c.UserId, host, requirePool: true) == null)
            .Select(c => c.UserId)
            .ToList();
        if (candidates.Count == 0)
        {
            var detail = ctx.Pool.PoolMemberCount == 0 ? NoCandidateNoPool
                : ctx.Pool.ActivePoolMemberCount == 0 ? NoCandidateAllPaused
                : NoCandidateNoVisibility;
            return WithBlocked(new DispatchDecision
            {
                Kind = DispatchDecisionKind.Skip,
                SkipReason = SkipNoCandidate,
                NoCandidateDetail = detail
            }, blocked);
        }

        var continuityHandlerId = ctx.ContinuityHandlerFor(host.HostName, issue.Source, issue.EventId);
        var handlerId = SelectHandler(ctx, candidates, issue.Source, issue.EventId, continuityHandlerId);
        var recurrence = continuityHandlerId == handlerId;
        return WithBlocked(AssignTo(handlerId, activeOrders, WorkOrderOrigins.AutoDispatch, recurrence), blocked);
    }

    private static DispatchDecision WithBlocked(DispatchDecision decision, List<BlockedContinuation> blocked)
    {
        if (blocked.Count > 0) decision.BlockedContinuations = blocked;
        return decision;
    }

    /// <summary>
    /// 「這個人現在能不能接這個問題」的唯一判定（續掛、問題負責人、自動派工共用）。符合資格回 null，否則回原因代碼：
    /// <list type="bullet">
    /// <item>不在候選人快照（已停用，或不具處理能力）→ <see cref="ContinuationStopReasons.HandlerInactive"/></item>
    /// <item>暫停接單 → <see cref="ContinuationStopReasons.HandlerPaused"/></item>
    /// <item><paramref name="requirePool"/>（只有自動派工）且不在派工池 → <see cref="IneligibleNotInPool"/></item>
    /// <item><paramref name="mustSee"/> 非 null（續掛、自動派工）且看不到該主機 → <see cref="ContinuationStopReasons.HostNotVisible"/></item>
    /// </list>
    /// 「問題負責人」路徑傳 <c>mustSee: null</c>：管理者明確指定的負責人不檢查可見主機（刻意設計）。
    /// </summary>
    internal static string? IneligibleReason(DispatchContext ctx, long userId, WebHost? mustSee, bool requirePool) =>
        IneligibleReason(ctx.Pool, userId, mustSee?.HostId, requirePool);

    /// <summary>
    /// 同上，只看候選人池快照（不需要整份 <see cref="DispatchContext"/>）：就緒檢查要回答「這台主機現在有沒有人接得到」，
    /// 與決策共用這一個判定，不另寫資格條件。
    /// </summary>
    internal static string? IneligibleReason(DispatchCandidatePool candidatePool, long userId, long? mustSeeHostId, bool requirePool)
    {
        if (!candidatePool.ByUserId.TryGetValue(userId, out var candidate)) return ContinuationStopReasons.HandlerInactive;
        if (candidate.Paused) return ContinuationStopReasons.HandlerPaused;
        if (requirePool && !candidate.InPool) return IneligibleNotInPool;
        if (mustSeeHostId != null && !candidate.VisibleHostIds.Contains(mustSeeHostId.Value)) return ContinuationStopReasons.HostNotVisible;
        return null;
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
    private static DispatchDecision AssignTo(long handlerId, IReadOnlyList<WorkOrder> activeOrders, string origin, bool recurrence)
    {
        var existing = activeOrders
            .Where(o => o.HandlerId == handlerId)
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.WorkOrderId)
            .FirstOrDefault();

        return existing != null
            ? new DispatchDecision { Kind = DispatchDecisionKind.AttachTo, WorkOrderId = existing.WorkOrderId, HandlerId = handlerId, Step = origin, Recurrence = recurrence }
            : new DispatchDecision { Kind = DispatchDecisionKind.CreateFor, HandlerId = handlerId, Origin = origin, Step = origin, Recurrence = recurrence };
    }

    private static DispatchDecision Skip(string reason) =>
        new() { Kind = DispatchDecisionKind.Skip, SkipReason = reason };
}
