using LogForesight.Core.Service;

namespace LogForesight.Core.Persistence;

/// <summary>夜間派工一趟的彙總（E-1 郵件用 <see cref="PerHandler"/>）</summary>
public sealed class NightlyDispatchSummary
{
    public int CreatedOrders { get; init; }

    public int AttachedMembers { get; init; }

    public IReadOnlyDictionary<string, int> SkipCounts { get; init; } = new Dictionary<string, int>();

    /// <summary>處理人 → 本趟有新建或新增成員的單</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<NightlyDispatchOrderLine>> PerHandler { get; init; }
        = new Dictionary<long, IReadOnlyList<NightlyDispatchOrderLine>>();

    /// <summary>本趟派不出去的問題（無候選人，依問題＋原因彙整台數）；管理者提醒用</summary>
    public IReadOnlyList<NightlyUnassignableIssue> Unassignable { get; init; } = Array.Empty<NightlyUnassignableIssue>();
}

/// <summary>夜間派工派不出去的問題：<paramref name="Reason"/> 是 no_pool／all_paused／no_visibility</summary>
public sealed record NightlyUnassignableIssue(string IssueLabel, string Reason, int Hosts);

/// <summary>夜間派工彙總的一張單</summary>
public sealed record NightlyDispatchOrderLine(long WorkOrderId, bool CreatedThisRun, int AddedMembers, string IssueLabel)
{
    /// <summary>本趟新增成員中屬於復發的台數</summary>
    public int RecurrenceMembers { get; init; }
}

/// <summary>
/// 夜間派工：一趟執行一個實例，本機、NetIQ、PRTG 三路並行共用。每個主機日在掛接（①②③）之後，
/// 把剩下的問題交給 <see cref="WorkOrderDispatcher.Decide"/>，決策結果由 <see cref="WorkOrderCoordinator"/>
/// 寫成交辦單成員；三路匯合後 <see cref="FlushRun"/> 補記每張單的 appended 事件。
///
/// 決策、建單、寫成員整段在 <see cref="DispatchContext.Gate"/> 內：脈絡的負載增量與已選中狀態、
/// 以及「該人此問題已有進行中單」的判斷都要看到另一路剛建的單，否則兩路會各建一張。
/// </summary>
public sealed record NightlyDispatchItemOutcome(string SignatureKey, string Outcome, string? Reason = null);

public sealed class NightlyDispatch
{
    private const string SkipRelatedWarningActive = "related_warning_active";
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly WorkOrderCoordinator _coordinator;
    private readonly DispatchContext _ctx;
    private readonly IHostStore _hosts;

    // 本趟累計（皆在 _ctx.Gate 內讀寫）
    private readonly Dictionary<long, int> _addedByOrder = new();
    private readonly Dictionary<long, int> _recurrenceByOrder = new();
    private readonly Dictionary<long, long> _handlerByOrder = new();
    private readonly Dictionary<long, string> _labelByOrder = new();
    private readonly HashSet<long> _createdOrders = new();
    /// <summary>本趟被擋下的續掛：單 → 原因代碼（<see cref="ContinuationStopReasons"/>）</summary>
    private readonly Dictionary<long, string> _blockedByOrder = new();
    /// <summary>本趟成功續掛過的單</summary>
    private readonly HashSet<long> _continuedOrders = new();
    /// <summary>寫入當下才發現的途中變動（單已結案／已改派），趟末併入 SkipCounts</summary>
    private readonly Dictionary<string, int> _midrunCounts = new(StringComparer.Ordinal);
    /// <summary>本趟派不出去：（問題, 原因）→ 主機名集合（同主機多日只算一台）</summary>
    private readonly Dictionary<(string Issue, string Reason), HashSet<string>> _unassignable = new();

    public NightlyDispatch(WorkOrderCoordinator coordinator, DispatchContext ctx, IHostStore hosts)
    {
        _coordinator = coordinator;
        _ctx = ctx;
        _hosts = hosts;
    }

    public void DispatchDay(string hostName, DateTime date, IReadOnlyList<LogIssueSignature> unassigned, DateTime occurredAt,
        Action<NightlyDispatchItemOutcome>? onResult = null)
    {
        if (unassigned.Count == 0) return;

        var host = _hosts.FindByName(hostName);
        if (host == null)
        {
            Log.Warn("夜間派工：找不到主機「{Host}」，{Date:yyyy-MM-dd} 的 {Count} 個問題不派工", hostName, date, unassigned.Count);
            foreach (var issue in unassigned) Report(onResult, issue, "failed", "host-not-found");
            return;
        }

        lock (_ctx.Gate)
        {
            // 同批同 sensor 時先處理 Warning 並完成成員寫入；排序不依賴輸入順序。
            // 只有成功寫入的 Warning 才能影響後續趨勢判斷。
            var pairedWarnings = unassigned
                .Where(IsPrtgWarning)
                .Where(w => unassigned.Any(t => IsValueTrend(t) && SameSensor(w, t)))
                .ToList();
            if (pairedWarnings.Count > 0)
            {
                DispatchBatch(host, date, pairedWarnings, occurredAt, onResult);
                unassigned = unassigned.Where(i => !pairedWarnings.Contains(i)).ToList();
            }
            DispatchBatch(host, date, unassigned, occurredAt, onResult);
        }
    }

    private void DispatchBatch(WebHost host, DateTime date, IReadOnlyList<LogIssueSignature> issues, DateTime occurredAt,
        Action<NightlyDispatchItemOutcome>? onResult)
    {
        if (issues.Count == 0) return;
        var hostName = host.HostName;
        var members = new List<(LogIssueSignature Issue, long WorkOrderId, long HandlerId, string Step)>();
        var recurrences = new List<bool>();
        foreach (var issue in issues)
        {
                var relatedWarningOrderId = _ctx.ActiveWarningOrderForSensor(hostName, issue);
                var decision = WorkOrderDispatcher.Decide(_ctx, host, issue, date.Date);
                foreach (var blocked in decision.BlockedContinuations) _blockedByOrder[blocked.WorkOrderId] = blocked.Reason;
                // Keep each trend finding/case intact, but an active same-host, same-sensor Warning
                // already gives the handler an actionable order. Do this after normal gates so
                // mute, suppression, ownership and dismissal retain their existing precedence.
                if (relatedWarningOrderId != null && decision.Kind != DispatchDecisionKind.Skip)
                {
                    _ctx.Commit(new DispatchDecision
                    {
                        Kind = DispatchDecisionKind.Skip,
                        SkipReason = SkipRelatedWarningActive
                    }, issue.Source, issue.EventId);
                    Report(onResult, issue, "skipped", SkipRelatedWarningActive);
                    continue;
                }

                switch (decision.Kind)
                {
                    case DispatchDecisionKind.Skip:
                        _ctx.Commit(decision, issue.Source, issue.EventId);
                        if (decision.SkipReason == WorkOrderDispatcher.SkipNoCandidate)
                        {
                            var key = ($"{issue.Source} {issue.EventId}", decision.NoCandidateDetail ?? WorkOrderDispatcher.NoCandidateNoPool);
                            if (!_unassignable.TryGetValue(key, out var hostSet))
                                _unassignable[key] = hostSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            hostSet.Add(hostName);
                        }
                        var terminal = decision.SkipReason is WorkOrderDispatcher.SkipMuted or WorkOrderDispatcher.SkipSuppressed or
                            WorkOrderDispatcher.SkipNoise or WorkOrderDispatcher.SkipSeverity or WorkOrderDispatcher.SkipDismissed or WorkOrderDispatcher.SkipDisabled;
                        Report(onResult, issue, terminal ? "skipped" : "deferred", decision.SkipReason);
                        continue;

                    case DispatchDecisionKind.CreateFor:
                    {
                        var (order, created) = _coordinator.EnsureNightlyOrder(decision, issue, occurredAt);
                        // 採用的既有單若脈絡還不知道（本趟建脈絡之後才出現），一樣登記，後續同人同問題才會直接掛入
                        if (created || _ctx.ActiveOrdersFor(issue.Source, issue.EventId).All(o => o.WorkOrderId != order.WorkOrderId))
                            _ctx.RegisterOrder(order);
                        if (created) _createdOrders.Add(order.WorkOrderId);
                        _handlerByOrder[order.WorkOrderId] = order.HandlerId;
                        _labelByOrder[order.WorkOrderId] = RelatedLabel(order.IssueLabel, relatedWarningOrderId);

                        decision = new DispatchDecision
                        {
                            Kind = DispatchDecisionKind.AttachTo, WorkOrderId = order.WorkOrderId,
                            HandlerId = order.HandlerId, Step = decision.Step,
                            // 撞唯一索引而採用別人剛建的單時處理人可能換了——復發是對「上次修好的那個人」說的
                            Recurrence = decision.Recurrence && order.HandlerId == decision.HandlerId
                        };
                        break;
                    }
                }

                if (relatedWarningOrderId is long warningOrderId && decision.WorkOrderId is long trendOrderId
                    && !_labelByOrder.ContainsKey(trendOrderId))
                    _labelByOrder[trendOrderId] = RelatedLabel(
                        _ctx.ActiveOrdersFor(issue.Source, issue.EventId).FirstOrDefault(o => o.WorkOrderId == trendOrderId)?.IssueLabel
                        ?? $"{issue.Source}/{issue.EventId}", warningOrderId);

                members.Add((issue, decision.WorkOrderId!.Value, decision.HandlerId!.Value, decision.Step!));
                recurrences.Add(decision.Recurrence);
                _ctx.Commit(decision, issue.Source, issue.EventId);
        }

        // 途中已結案的單不寫入、途中改派的依新處理人寫入（判斷在 coordinator 鎖內，見 WriteNightlyMembers）
        var writeOutcome = _coordinator.WriteNightlyMembersDetailed(host, date, members, occurredAt);
        var writtenHandlers = writeOutcome.Handlers;

            for (var i = 0; i < members.Count; i++)
            {
                var (issue, workOrderId, plannedHandlerId, step) = members[i];
                if (writtenHandlers[i] is not long handlerId)
                {
                    if (writeOutcome.ExistingOpenIndexes.Contains(i))
                    {
                        Report(onResult, issue, "skipped", WorkOrderCoordinator.SkipOpenCaseExists);
                        _midrunCounts[WorkOrderCoordinator.SkipOpenCaseExists] = _midrunCounts.GetValueOrDefault(WorkOrderCoordinator.SkipOpenCaseExists) + 1;
                        continue;
                    }
                    Report(onResult, issue, "failed", WorkOrderCoordinator.SkipOrderClosedMidrun);
                    _midrunCounts[WorkOrderCoordinator.SkipOrderClosedMidrun] = _midrunCounts.GetValueOrDefault(WorkOrderCoordinator.SkipOrderClosedMidrun) + 1;
                    continue;
                }
                Report(onResult, issue, "delivered");
                if (step == WorkOrderDispatcher.StepAttach) _continuedOrders.Add(workOrderId);
                _ctx.InvalidateCasesFor(hostName);
                if (handlerId != plannedHandlerId)
                    _midrunCounts[WorkOrderCoordinator.HandlerChangedMidrun] = _midrunCounts.GetValueOrDefault(WorkOrderCoordinator.HandlerChangedMidrun) + 1;

                _addedByOrder[workOrderId] = _addedByOrder.GetValueOrDefault(workOrderId) + 1;
                if (recurrences[i])
                {
                    _recurrenceByOrder[workOrderId] = _recurrenceByOrder.GetValueOrDefault(workOrderId) + 1;
                }
                _handlerByOrder[workOrderId] = handlerId;
                if (!_labelByOrder.ContainsKey(workOrderId))
                {
                    var existing = _ctx.ActiveOrdersFor(issue.Source, issue.EventId).FirstOrDefault(o => o.WorkOrderId == workOrderId);
                    _labelByOrder[workOrderId] = existing != null && !string.IsNullOrEmpty(existing.IssueLabel)
                        ? existing.IssueLabel
                        : $"{issue.Source}/{issue.EventId}";
                }
        }
    }

    private static void Report(Action<NightlyDispatchItemOutcome>? receiver, LogIssueSignature issue,
        string outcome, string? reason = null)
    {
        if (receiver is null) return;
        try { receiver(new(IssueSignatureKey.For(issue), outcome, reason)); }
        catch (Exception ex) { Log.Warn(ex, "夜間派工結果已確定，但 workflow 回寫失敗；保持待補而不重複派工。"); }
    }

    private static bool IsPrtgWarning(LogIssueSignature issue) => PrtgFindingMapper.IsPrtg(issue)
        && PrtgFindingMapper.TryGetRuleCode(issue.Source, out var code)
        && string.Equals(code, "warning", StringComparison.OrdinalIgnoreCase);

    private static bool IsValueTrend(LogIssueSignature issue) => PrtgFindingMapper.IsPrtg(issue)
        && PrtgFindingMapper.TryGetRuleCode(issue.Source, out var code)
        && PrtgRuleCatalog.IsValueTrend(code);

    private static bool SameSensor(LogIssueSignature warning, LogIssueSignature trend) =>
        PrtgFindingMapper.TryGetRuleCode(warning.Source, out var warningCode)
        && PrtgFindingMapper.TryGetRuleCode(trend.Source, out var trendCode)
        && TrySensorId(warning.EventKey, warningCode, out var warningSensor)
        && TrySensorId(trend.EventKey, trendCode, out var trendSensor)
        && string.Equals(warningSensor, trendSensor, StringComparison.Ordinal);

    private static bool TrySensorId(string? eventKey, string ruleCode, out string sensorId)
    {
        sensorId = string.Empty;
        var prefix = $"prtg:{ruleCode}:";
        if (eventKey == null || !eventKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        sensorId = eventKey[prefix.Length..];
        return sensorId.Length > 0;
    }

    private static string RelatedLabel(string label, long? warningOrderId) => warningOrderId is long id
        ? $"{label}（同感測器 Warning 交辦 #{id}）"
        : label;

    /// <summary>
    /// 趟末寫進執行紀錄的一行摘要。略過原因用中文——代碼（gate_noise 之類）對看執行紀錄的管理者沒有意義。
    /// 「未開啟自動派工」不列：開關關著時每個沒人接的問題都會計一次，數字只是雜訊。
    /// </summary>
    public static string DescribeSummary(NightlyDispatchSummary summary)
    {
        int Count(string reason) => summary.SkipCounts.GetValueOrDefault(reason);

        var gate = Count(WorkOrderDispatcher.SkipSuppressed) + Count(WorkOrderDispatcher.SkipNoise)
                   + Count(WorkOrderDispatcher.SkipSeverity) + Count(WorkOrderDispatcher.SkipDismissed);
        var text = $"自動派工：建 {summary.CreatedOrders} 單／掛入 {summary.AttachedMembers} 台／無候選人 {Count(WorkOrderDispatcher.SkipNoCandidate)} 台" +
                   $"／靜音略過 {Count(WorkOrderDispatcher.SkipMuted)}／閘門略過 {gate}" +
                   $"（抑制 {Count(WorkOrderDispatcher.SkipSuppressed)}、已知雜訊 {Count(WorkOrderDispatcher.SkipNoise)}、" +
                   $"嚴重度 {Count(WorkOrderDispatcher.SkipSeverity)}、不再打擾 {Count(WorkOrderDispatcher.SkipDismissed)}）";
        if (Count(WorkOrderCoordinator.SkipOrderClosedMidrun) > 0)
            text += $"；交辦單在派工途中已結案 {Count(WorkOrderCoordinator.SkipOrderClosedMidrun)} 台";
        if (Count(WorkOrderCoordinator.SkipOpenCaseExists) > 0)
            text += $"；已有進行中案件略過 {Count(WorkOrderCoordinator.SkipOpenCaseExists)} 台";
        if (Count(WorkOrderCoordinator.HandlerChangedMidrun) > 0)
            text += $"；交辦單在派工途中已改派，已依新處理人掛入 {Count(WorkOrderCoordinator.HandlerChangedMidrun)} 台";
        if (Count(WorkOrderDispatcher.SkipUnavailable) > 0) text += "；派工資料讀取失敗，本趟未派工";
        if (Count(SkipRelatedWarningActive) > 0) text += $"；已有同感測器 Warning 交辦 {Count(SkipRelatedWarningActive)} 台";
        return text;
    }

    public NightlyDispatchSummary FlushRun(DateTime occurredAt)
    {
        lock (_ctx.Gate)
        {
            foreach (var (workOrderId, added) in _addedByOrder.OrderBy(p => p.Key))
                _coordinator.RecordNightlyAppended(workOrderId, added, occurredAt);

            // 本趟新建、最後一個成員都沒寫入的單（成員全因「決策後已有進行中案件」略過）：交給結案推導
            // 收掉（以 empty_dispatch 結案），不留零成員的進行中空單，也不列進摘要與通知。放在收尾而非當下，
            // 同一趟後面的主機仍可能掛入這張單；推導只在單真的沒有任何案件時才結案。
            foreach (var emptyOrderId in _createdOrders.Where(id => !_addedByOrder.ContainsKey(id)).OrderBy(id => id).ToList())
            {
                _coordinator.CloseEmptyDispatchOrder(emptyOrderId, occurredAt);
                _createdOrders.Remove(emptyOrderId);
            }

            FlushContinuationStops(occurredAt);

            var perHandler = _handlerByOrder
                .Where(p => _createdOrders.Contains(p.Key) || _addedByOrder.ContainsKey(p.Key))
                .GroupBy(p => p.Value)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<NightlyDispatchOrderLine>)g
                        .OrderBy(p => p.Key)
                        .Select(p => new NightlyDispatchOrderLine(p.Key, _createdOrders.Contains(p.Key), _addedByOrder.GetValueOrDefault(p.Key), _labelByOrder[p.Key])
                        {
                            RecurrenceMembers = _recurrenceByOrder.GetValueOrDefault(p.Key)
                        })
                        .ToList());

            var skipCounts = new Dictionary<string, int>(_ctx.SkipCounts, StringComparer.Ordinal);
            foreach (var (reason, count) in _midrunCounts)
                skipCounts[reason] = skipCounts.GetValueOrDefault(reason) + count;

            var summary = new NightlyDispatchSummary
            {
                CreatedOrders = _createdOrders.Count,
                AttachedMembers = _addedByOrder.Values.Sum(),
                SkipCounts = skipCounts,
                PerHandler = perHandler,
                Unassignable = _unassignable
                    .OrderBy(p => p.Key.Issue, StringComparer.Ordinal).ThenBy(p => p.Key.Reason, StringComparer.Ordinal)
                    .Select(p => new NightlyUnassignableIssue(p.Key.Issue, p.Key.Reason, p.Value.Count))
                    .ToList()
            };

            _addedByOrder.Clear();
            _recurrenceByOrder.Clear();
            _handlerByOrder.Clear();
            _createdOrders.Clear();
            _labelByOrder.Clear();
            _midrunCounts.Clear();
            _unassignable.Clear();
            _blockedByOrder.Clear();
            _continuedOrders.Clear();
            return summary;
        }
    }

    /// <summary>
    /// 趟末整理「續掛已停止」標示（在 <see cref="DispatchContext.Gate"/> 內呼叫）：
    /// 本趟被擋的單寫入原因與時間（同單被擋與成功並存時以被擋為準——仍有新主機掛不進去）；
    /// 本趟成功續掛且沒被擋的單清除標示；處理人已恢復基本資格（啟用、有處理能力、未暫停）而本趟沒被擋的單，
    /// 若標示是「已停用」「暫停接單」也一併清除（「看不到新主機」要等下一次成功續掛才清）。
    /// 標示只是提示，寫入失敗只記警告，不讓整趟派工失敗。
    /// </summary>
    private void FlushContinuationStops(DateTime occurredAt)
    {
        var updates = new List<ContinuationStopUpdate>();
        var orders = _ctx.AllActiveOrders().GroupBy(o => o.WorkOrderId).ToDictionary(g => g.Key, g => g.First());

        foreach (var (orderId, reason) in _blockedByOrder.OrderBy(p => p.Key))
        {
            updates.Add(new ContinuationStopUpdate(orderId, reason, occurredAt));
            if (orders.TryGetValue(orderId, out var order))
            {
                order.ContinuationStoppedReason = reason;
                order.ContinuationStoppedAt = occurredAt;
            }
        }

        foreach (var order in orders.Values.Where(o => o.ContinuationStoppedReason != null && !_blockedByOrder.ContainsKey(o.WorkOrderId))
                     .OrderBy(o => o.WorkOrderId).ToList())
        {
            var recovered = _continuedOrders.Contains(order.WorkOrderId)
                || (order.ContinuationStoppedReason is ContinuationStopReasons.HandlerInactive or ContinuationStopReasons.HandlerPaused
                    && WorkOrderDispatcher.IneligibleReason(_ctx, order.HandlerId, null, requirePool: false) == null);
            if (!recovered) continue;

            updates.Add(new ContinuationStopUpdate(order.WorkOrderId, null, occurredAt));
            order.ContinuationStoppedReason = null;
            order.ContinuationStoppedAt = null;
        }

        if (updates.Count == 0) return;
        try { _coordinator.RecordContinuationStops(updates); }
        catch (Exception ex) { Log.Warn(ex, "夜間派工：寫入續掛已停止標示失敗（{Count} 張單），不影響本趟派工結果。", updates.Count); }
    }
}
