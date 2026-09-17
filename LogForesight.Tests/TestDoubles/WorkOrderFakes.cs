using Microsoft.EntityFrameworkCore;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IWorkOrderStore"/> 的記憶體替身，語意比照 <see cref="EfWorkOrderStore"/>：
/// Get 回傳複本（整列覆寫語意）、Save 以 UpdatedAt 做併發檢查、部分唯一索引
/// （同處理人＋source 大寫鍵＋EventId 至多一張進行中單，違反擲 <see cref="DbUpdateException"/>）。
/// 成員計數與結案掃描讀建構子注入的同一個 <see cref="FakeIssueCaseStore"/>。
/// </summary>
internal class FakeWorkOrderStore : IWorkOrderStore
{
    private readonly IIssueCaseStore _cases;
    private readonly List<WorkOrder> _orders = new();
    private readonly List<WorkOrderEvent> _events = new();
    private long _nextId = 1;
    private long _nextEventId = 1;
    private long _versionTicks = new DateTime(2026, 1, 1).Ticks;

    public FakeWorkOrderStore(IIssueCaseStore cases) => _cases = cases;

    /// <summary>接下來幾次 Save 要擲併發例外（注入用）</summary>
    public int FailNextSaves { get; set; }

    /// <summary>下一次 Insert 真正寫入前執行一次（模擬兩人同時建單）</summary>
    public Action? BeforeNextInsert { get; set; }

    public int SaveCalls { get; private set; }

    public IReadOnlyList<WorkOrder> All => _orders.Select(Clone).ToList();

    public WorkOrder? Get(long workOrderId)
    {
        var row = _orders.FirstOrDefault(o => o.WorkOrderId == workOrderId);
        return row == null ? null : Clone(row);
    }

    public WorkOrder? GetActiveFor(long handlerId, string source, int eventId)
    {
        var row = _orders.FirstOrDefault(o => o.HandlerId == handlerId && o.ClosedAt == null && SameIssue(o, source, eventId));
        return row == null ? null : Clone(row);
    }

    public List<WorkOrder> GetActiveByIssue(string source, int eventId) =>
        _orders.Where(o => o.ClosedAt == null && SameIssue(o, source, eventId)).Select(Clone).ToList();

    public List<WorkOrder> GetActiveByHandler(long handlerId) =>
        _orders.Where(o => o.HandlerId == handlerId && o.ClosedAt == null).Select(Clone).ToList();

    public List<WorkOrder> GetAllActive() =>
        _orders.Where(o => o.ClosedAt == null).Select(Clone).ToList();

    public virtual long Insert(WorkOrder order)
    {
        var hook = BeforeNextInsert;
        BeforeNextInsert = null;
        hook?.Invoke();

        EnsureUnique(order, selfId: 0);
        order.WorkOrderId = _nextId++;
        order.UpdatedAt = NextVersion();
        _orders.Add(Clone(order));
        return order.WorkOrderId;
    }

    public virtual void Save(WorkOrder order)
    {
        SaveCalls++;
        if (FailNextSaves > 0)
        {
            FailNextSaves--;
            throw new DbUpdateConcurrencyException("注入的併發衝突");
        }

        var index = _orders.FindIndex(o => o.WorkOrderId == order.WorkOrderId);
        if (index < 0 || _orders[index].UpdatedAt != order.UpdatedAt)
            throw new DbUpdateConcurrencyException("交辦單已被其他人更新");

        EnsureUnique(order, order.WorkOrderId);
        order.UpdatedAt = NextVersion();
        _orders[index] = Clone(order);
    }

    public virtual void AppendEvent(WorkOrderEvent evt)
    {
        evt.EventId = _nextEventId++;
        _events.Add(new WorkOrderEvent
        {
            EventId = evt.EventId, WorkOrderId = evt.WorkOrderId, Action = evt.Action,
            ActorId = evt.ActorId, ActorAccount = evt.ActorAccount, MemberDelta = evt.MemberDelta,
            Note = evt.Note, CreatedAt = evt.CreatedAt
        });
    }

    public List<WorkOrderEvent> ListEvents(long workOrderId) =>
        _events.Where(e => e.WorkOrderId == workOrderId).OrderBy(e => e.CreatedAt).ThenBy(e => e.EventId).ToList();

    public Dictionary<long, WorkOrderMemberCounts> CountMembers(IReadOnlyCollection<long> workOrderIds)
    {
        var result = new Dictionary<long, WorkOrderMemberCounts>();
        foreach (var id in workOrderIds.Distinct())
        {
            var members = _cases.GetByWorkOrder(id, 0, int.MaxValue);
            // 同 EF 版 GROUP BY：零成員的單不出現在結果裡
            if (members.Count == 0) continue;
            var active = members.Count(c => c.ClosedAt == null);
            result[id] = new WorkOrderMemberCounts
            {
                Total = members.Count,
                Active = active,
                Closed = members.Count - active,
                Escalated = members.Count(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.Escalated),
                InProgress = members.Count(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.InProgress),
                Observing = members.Count(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.Observing),
                Open = members.Count(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.Open),
                Overdue = members.Count(c => WorkOrderQueries.IsOverdue(c, DateTime.Today)),
                DaySyncPending = members.Count(c => c.DaySyncPending)
            };
        }
        return result;
    }

    public Dictionary<(string SourceKey, int EventId), int> CountAssignedHostsByIssue(
        IReadOnlyCollection<(string Source, int EventId)> issues)
    {
        var result = new Dictionary<(string SourceKey, int EventId), int>();
        if (issues.Count == 0) return result;

        var normalized = issues
            .Select(i => (SourceKey: EfWorkOrderStore.SourceKeyOf(i.Source), i.EventId))
            .Distinct()
            .ToList();

        foreach (var (sourceKey, eventId) in normalized)
        {
            var openCases = _cases.GetOpenByIssue(sourceKey, eventId);
            var count = openCases
                .Where(c => c.WorkOrderId != null)
                .Select(c => HostNameKey.Of(c.HostName))
                .Distinct()
                .Count();

            if (count > 0)
            {
                result[(sourceKey, eventId)] = count;
            }
        }

        return result;
    }

    /// <summary>同 EF 版：篩選、排序（同值依單號降冪）、分頁</summary>
    public WorkOrderPage QueryOrders(WorkOrderQuery q)
    {
        WorkOrderQueries.Validate(q);
        var today = DateTime.Today;

        IEnumerable<WorkOrder> query = _orders;
        if (q.HandlerIds != null) query = query.Where(o => q.HandlerIds.Contains(o.HandlerId));
        if (q.Source != null) query = query.Where(o => o.SourceName != null && o.SourceName.ToUpperInvariant() == q.Source.ToUpperInvariant());
        if (q.EventId != null) query = query.Where(o => o.EventId == q.EventId);
        if (q.PausedKeys != null && q.PausedMode == WorkOrderQueries.PausedOnly)
            query = query.Where(o => KeyIn(o, q.PausedKeys));
        else if (q.PausedKeys != null && q.PausedMode == WorkOrderQueries.PausedExclude)
            query = query.Where(o => !KeyIn(o, q.PausedKeys));
        if (q.OnlyKeys != null) query = query.Where(o => KeyIn(o, q.OnlyKeys));

        List<IssueCase> ActiveMembers(WorkOrder o) =>
            _cases.GetByWorkOrder(o.WorkOrderId, 0, int.MaxValue).Where(c => c.ClosedAt == null).ToList();

        query = q.Status switch
        {
            WorkOrderQueries.StatusActive => query.Where(o => o.ClosedAt == null),
            WorkOrderQueries.StatusEscalated => query.Where(o => o.ClosedAt == null
                && ActiveMembers(o).Any(c => c.Status == IssueHandlingStatuses.Escalated)),
            WorkOrderQueries.StatusOverdue => query.Where(o => o.ClosedAt == null
                && ActiveMembers(o).Any(c => WorkOrderQueries.IsOverdue(c, today))),
            WorkOrderQueries.StatusUnreplied => query.Where(o => o.ClosedAt == null && o.LastReplyAt == null),
            WorkOrderQueries.StatusClosed => query.Where(o => o.ClosedAt != null),
            _ => query
        };

        var filtered = query.ToList();
        IEnumerable<WorkOrder> sorted = q.Sort switch
        {
            WorkOrderQueries.SortMembersDesc => filtered
                .OrderByDescending(o => ActiveMembers(o).Count)
                .ThenByDescending(o => o.WorkOrderId),
            WorkOrderQueries.SortUnrepliedOldest => filtered
                .OrderBy(o => o.LastReplyAt == null ? 0 : 1)
                .ThenBy(o => o.CreatedAt)
                .ThenByDescending(o => o.WorkOrderId),
            _ => filtered
                .OrderByDescending(o => o.CreatedAt)
                .ThenByDescending(o => o.WorkOrderId)
        };

        return new WorkOrderPage
        {
            Items = sorted.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).Select(Clone).ToList(),
            Total = filtered.Count
        };
    }

    /// <summary>同 EF 版：母體＝進行中單＋近 7 日結案單，依處理人分組</summary>
    public List<HandlerLoad> LoadBoard()
    {
        var today = DateTime.Today;
        var closedSince = today.AddDays(-HandlerLoad.ClosedWindowDays);

        return _orders.Where(o => o.ClosedAt == null || o.ClosedAt >= closedSince)
            .GroupBy(o => o.HandlerId)
            .Select(g =>
            {
                var active = g.Where(o => o.ClosedAt == null).ToList();
                var activeMembers = active
                    .SelectMany(o => _cases.GetByWorkOrder(o.WorkOrderId, 0, int.MaxValue))
                    .Where(c => c.ClosedAt == null)
                    .ToList();
                return new HandlerLoad
                {
                    HandlerId = g.Key,
                    ActiveWorkOrders = active.Count,
                    UnrepliedWorkOrders = active.Count(o => o.LastReplyAt == null),
                    ClosedLast7Days = g.Count(o => o.ClosedAt != null),
                    OldestActiveCreatedAt = active.Count == 0 ? null : active.Min(o => o.CreatedAt),
                    ActiveMembers = activeMembers.Count,
                    OverdueMembers = activeMembers.Count(c => WorkOrderQueries.IsOverdue(c, today))
                };
            })
            .ToList();
    }

    /// <summary>進行中、問題欄非 null 且組合鍵在集合內（同 EF 版的暫停／限定鍵條件）</summary>
    private static bool KeyIn(WorkOrder o, IReadOnlyCollection<string> keys) =>
        o.ClosedAt == null && o.SourceName != null && o.EventId != null
        && keys.Contains(IssueExclusion.CompositeKey(o.SourceName, o.EventId.Value));

    /// <summary>同 EF 版：只算該處理人的進行中單（排除暫停單）；沒有進行中單回全 0</summary>
    public WorkOrderHandlerSummary HandlerSummary(long handlerId, IReadOnlyCollection<string> pausedKeys)
    {
        var today = DateTime.Today;
        var all = _orders.Where(o => o.HandlerId == handlerId && o.ClosedAt == null).ToList();
        var active = all.Where(o => !KeyIn(o, pausedKeys)).ToList();
        var members = active
            .SelectMany(o => _cases.GetByWorkOrder(o.WorkOrderId, 0, int.MaxValue))
            .Where(c => c.ClosedAt == null)
            .ToList();
        return new WorkOrderHandlerSummary
        {
            ActiveWorkOrders = active.Count,
            UnrepliedWorkOrders = active.Count(o => o.LastReplyAt == null),
            ActiveMembers = members.Count,
            OverdueMembers = members.Count(c => WorkOrderQueries.IsOverdue(c, today)),
            PausedWorkOrders = all.Count - active.Count
        };
    }

    public int PruneClosed(int retentionDays)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var ids = _orders.Where(o => o.ClosedAt != null && o.ClosedAt < cutoff).Select(o => o.WorkOrderId).ToHashSet();
        _events.RemoveAll(e => ids.Contains(e.WorkOrderId));
        return _orders.RemoveAll(o => ids.Contains(o.WorkOrderId));
    }

    public List<long> FindActiveWithoutActiveMembers(int take) =>
        _orders.Where(o => o.ClosedAt == null
                           && !_cases.GetByWorkOrder(o.WorkOrderId, 0, int.MaxValue).Any(c => c.ClosedAt == null))
            .Select(o => o.WorkOrderId)
            .OrderBy(id => id)
            .Take(take)
            .ToList();

    private void EnsureUnique(WorkOrder order, long selfId)
    {
        if (order.SourceName == null || order.EventId == null || order.ClosedAt != null) return;
        if (_orders.Any(o => o.WorkOrderId != selfId && o.HandlerId == order.HandlerId && o.ClosedAt == null
                             && SameIssue(o, order.SourceName, order.EventId.Value)))
            throw new DbUpdateException("違反部分唯一索引：同處理人同問題已有進行中交辦單");
    }

    private static bool SameIssue(WorkOrder o, string source, int eventId) =>
        o.SourceName != null && o.EventId == eventId
        && o.SourceName.ToUpperInvariant() == source.ToUpperInvariant();

    private DateTime NextVersion()
    {
        _versionTicks += TimeSpan.TicksPerSecond;
        return new DateTime(_versionTicks);
    }

    private static WorkOrder Clone(WorkOrder o) => new()
    {
        WorkOrderId = o.WorkOrderId, SourceName = o.SourceName, EventId = o.EventId, IssueLabel = o.IssueLabel,
        HandlerId = o.HandlerId, Origin = o.Origin, ScopeKind = o.ScopeKind, ScopeGroupIds = o.ScopeGroupIds.ToList(),
        AutoAttach = o.AutoAttach, Note = o.Note, DueDate = o.DueDate, CreatedById = o.CreatedById,
        CreatedByAccount = o.CreatedByAccount, CreatedAt = o.CreatedAt, LastAppendedAt = o.LastAppendedAt,
        LastReplyAt = o.LastReplyAt, ClosedAt = o.ClosedAt, ClosedReason = o.ClosedReason, UpdatedAt = o.UpdatedAt
    };
}
