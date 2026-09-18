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
}

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
public sealed class NightlyDispatch
{
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

    public NightlyDispatch(WorkOrderCoordinator coordinator, DispatchContext ctx, IHostStore hosts)
    {
        _coordinator = coordinator;
        _ctx = ctx;
        _hosts = hosts;
    }

    public void DispatchDay(string hostName, DateTime date, IReadOnlyList<LogIssueSignature> unassigned, DateTime occurredAt)
    {
        if (unassigned.Count == 0) return;

        var host = _hosts.FindByName(hostName);
        if (host == null)
        {
            Log.Warn("夜間派工：找不到主機「{Host}」，{Date:yyyy-MM-dd} 的 {Count} 個問題不派工", hostName, date, unassigned.Count);
            return;
        }

        lock (_ctx.Gate)
        {
            var members = new List<(LogIssueSignature Issue, long WorkOrderId, long HandlerId, string Step)>();
            var recurrences = new List<bool>();
            foreach (var issue in unassigned)
            {
                var decision = WorkOrderDispatcher.Decide(_ctx, host, issue, date.Date);
                switch (decision.Kind)
                {
                    case DispatchDecisionKind.Skip:
                        _ctx.Commit(decision, issue.Source, issue.EventId);
                        continue;

                    case DispatchDecisionKind.CreateFor:
                    {
                        var (order, created) = _coordinator.EnsureNightlyOrder(decision, issue, occurredAt);
                        // 採用的既有單若脈絡還不知道（本趟建脈絡之後才出現），一樣登記，後續同人同問題才會直接掛入
                        if (created || _ctx.ActiveOrdersFor(issue.Source, issue.EventId).All(o => o.WorkOrderId != order.WorkOrderId))
                            _ctx.RegisterOrder(order);
                        if (created) _createdOrders.Add(order.WorkOrderId);
                        _handlerByOrder[order.WorkOrderId] = order.HandlerId;
                        _labelByOrder[order.WorkOrderId] = order.IssueLabel;

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

                members.Add((issue, decision.WorkOrderId!.Value, decision.HandlerId!.Value, decision.Step!));
                recurrences.Add(decision.Recurrence);
                _ctx.Commit(decision, issue.Source, issue.EventId);
            }

            _coordinator.WriteNightlyMembers(host, date, members, occurredAt);

            for (var i = 0; i < members.Count; i++)
            {
                var (issue, workOrderId, handlerId, _) = members[i];
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
    }

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
        if (Count(WorkOrderDispatcher.SkipUnavailable) > 0) text += "；派工資料讀取失敗，本趟未派工";
        return text;
    }

    public NightlyDispatchSummary FlushRun(DateTime occurredAt)
    {
        lock (_ctx.Gate)
        {
            foreach (var (workOrderId, added) in _addedByOrder.OrderBy(p => p.Key))
                _coordinator.RecordNightlyAppended(workOrderId, added, occurredAt);

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

            var summary = new NightlyDispatchSummary
            {
                CreatedOrders = _createdOrders.Count,
                AttachedMembers = _addedByOrder.Values.Sum(),
                SkipCounts = new Dictionary<string, int>(_ctx.SkipCounts, StringComparer.Ordinal),
                PerHandler = perHandler
            };

            _addedByOrder.Clear();
            _recurrenceByOrder.Clear();
            _handlerByOrder.Clear();
            _createdOrders.Clear();
            _labelByOrder.Clear();
            return summary;
        }
    }
}
