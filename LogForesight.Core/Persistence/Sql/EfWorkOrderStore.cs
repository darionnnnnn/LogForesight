using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// <see cref="IWorkOrderStore"/> 的真表實作（↔ lf_work_orders ＋ lf_work_order_events）。
///
/// 問題來源一律以 <see cref="SourceKeyOf"/> 正規化後比對（同 lf_issue_first_seen 的 source_key 慣例：
/// SQLite 預設 BINARY、SQL Server 常見 CI，不正規化的話兩後端比對結果不同）。
/// <see cref="EfIssueCaseStore"/> 與 <see cref="WorkOrderBackfiller"/> 共用同一份正規化與解析。
/// </summary>
public sealed class EfWorkOrderStore : IWorkOrderStore
{
    internal const int SourceMaxLength = WorkOrderIssueKey.SourceMaxLength;
    internal const int IssueLabelMaxLength = 512;
    internal const int AccountMaxLength = 255;
    internal const int NoteMaxLength = 1000;
    internal const int CodeMaxLength = 30;

    private readonly Func<LfDbContext> _contextFactory;

    public EfWorkOrderStore(Func<LfDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>供同組件的背景整併共用同一個連線工廠（不另開 StorageBackend 工廠方法，避免掛上啟動路徑）</summary>
    internal Func<LfDbContext> ContextFactory => _contextFactory;

    /// <summary>source_key 的唯一正規化規則</summary>
    internal static string SourceKeyOf(string source) => WorkOrderIssueKey.SourceKeyOf(source);

    /// <summary>
    /// 由案件的 issue_key 解析出（source_name, source_key, event_id）；解析失敗回 null。
    /// 解析失敗時要寫什麼由呼叫端決定（store 寫入路徑維持三欄 null；背景整併寫 source_key=''）。
    /// </summary>
    internal static (string SourceName, string SourceKey, int EventId)? ParseIssueColumns(string? issueKey)
    {
        var parsed = IssueSignatureKey.TryParseSignature(issueKey);
        if (parsed == null) return null;

        var (source, eventId) = parsed.Value;
        return (Truncate(source, SourceMaxLength), SourceKeyOf(source), eventId);
    }

    internal static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOrNull(string? value, int maxLength) =>
        value == null ? null : Truncate(value, maxLength);

    public WorkOrder? Get(long workOrderId)
    {
        using var ctx = _contextFactory();
        var row = ctx.WorkOrders.AsNoTracking().FirstOrDefault(w => w.WorkOrderId == workOrderId);
        return row == null ? null : ToModel(row);
    }

    public WorkOrder? GetActiveFor(long handlerId, string source, int eventId)
    {
        var key = SourceKeyOf(source);

        using var ctx = _contextFactory();
        var row = ctx.WorkOrders.AsNoTracking().FirstOrDefault(w =>
            w.HandlerId == handlerId && w.SourceKey == key && w.EventId == eventId && w.ClosedAt == null);
        return row == null ? null : ToModel(row);
    }

    public List<WorkOrder> GetActiveByIssue(string source, int eventId)
    {
        var key = SourceKeyOf(source);

        using var ctx = _contextFactory();
        return ctx.WorkOrders.AsNoTracking()
            .Where(w => w.SourceKey == key && w.EventId == eventId && w.ClosedAt == null)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<WorkOrder> GetActiveByHandler(long handlerId)
    {
        using var ctx = _contextFactory();
        return ctx.WorkOrders.AsNoTracking()
            .Where(w => w.HandlerId == handlerId && w.ClosedAt == null)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<WorkOrder> GetAllActive()
    {
        using var ctx = _contextFactory();
        return ctx.WorkOrders.AsNoTracking()
            .Where(w => w.ClosedAt == null)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public long Insert(WorkOrder order)
    {
        order.UpdatedAt = DateTime.Now;
        var row = new WorkOrderRow();
        CopyToRow(order, row);

        using var ctx = _contextFactory();
        ctx.WorkOrders.Add(row);
        ctx.SaveChanges();

        order.WorkOrderId = row.WorkOrderId;
        return row.WorkOrderId;
    }

    public void Save(WorkOrder order)
    {
        var now = DateTime.Now;
        var row = new WorkOrderRow { WorkOrderId = order.WorkOrderId };
        CopyToRow(order, row);

        using var ctx = _contextFactory();
        ctx.WorkOrders.Attach(row);
        var entry = ctx.Entry(row);
        entry.State = EntityState.Modified;
        // 併發檢查：以呼叫端讀到的 UpdatedAt 為原值，資料庫已被別人改過就影響 0 列 → DbUpdateConcurrencyException
        entry.Property(x => x.UpdatedAt).OriginalValue = order.UpdatedAt;
        row.UpdatedAt = now;

        ctx.SaveChanges();
        order.UpdatedAt = now;
    }

    public void AppendEvent(WorkOrderEvent evt)
    {
        var row = new WorkOrderEventRow
        {
            WorkOrderId = evt.WorkOrderId,
            Action = Truncate(evt.Action, CodeMaxLength),
            ActorId = evt.ActorId,
            ActorAccount = Truncate(evt.ActorAccount, AccountMaxLength),
            MemberDelta = evt.MemberDelta,
            Note = TruncateOrNull(evt.Note, NoteMaxLength),
            CreatedAt = evt.CreatedAt
        };

        using var ctx = _contextFactory();
        ctx.WorkOrderEvents.Add(row);
        ctx.SaveChanges();
        evt.EventId = row.EventId;
    }

    public List<WorkOrderEvent> ListEvents(long workOrderId)
    {
        using var ctx = _contextFactory();
        return ctx.WorkOrderEvents.AsNoTracking()
            .Where(e => e.WorkOrderId == workOrderId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.EventId)
            .Select(e => new WorkOrderEvent
            {
                EventId = e.EventId,
                WorkOrderId = e.WorkOrderId,
                Action = e.Action,
                ActorId = e.ActorId,
                ActorAccount = e.ActorAccount,
                MemberDelta = e.MemberDelta,
                Note = e.Note,
                CreatedAt = e.CreatedAt
            })
            .ToList();
    }

    public Dictionary<long, WorkOrderMemberCounts> CountMembers(IReadOnlyCollection<long> workOrderIds)
    {
        if (workOrderIds.Count == 0) return new Dictionary<long, WorkOrderMemberCounts>();

        var ids = workOrderIds.Distinct().ToList();
        using var ctx = _contextFactory();

        // 單一 GROUP BY：看板一次要數幾十張單，逐張數就是 N 次往返
        return BuildCountMembersQuery(ctx, ids).ToList().ToDictionary(r => r.WorkOrderId, r => new WorkOrderMemberCounts
        {
            Total = r.Total,
            Active = r.Active,
            Closed = r.Total - r.Active,
            Escalated = r.Escalated,
            InProgress = r.InProgress,
            Observing = r.Observing,
            Open = r.Open,
            Overdue = r.Overdue,
            DaySyncPending = r.DaySyncPending
        });
    }

    public Dictionary<(string SourceKey, int EventId), int> CountAssignedHostsByIssue(
        IReadOnlyCollection<(string Source, int EventId)> issues)
    {
        if (issues.Count == 0) return new Dictionary<(string SourceKey, int EventId), int>();

        var normalized = issues
            .Select(i => (SourceKey: SourceKeyOf(i.Source), i.EventId))
            .Distinct()
            .ToHashSet();

        if (normalized.Count == 0) return new Dictionary<(string SourceKey, int EventId), int>();

        var eventIds = normalized.Select(i => i.EventId).Distinct().ToList();
        var matched = new List<(string SourceKey, int EventId, string HostNameKey)>();

        using var ctx = _contextFactory();
        foreach (var batch in eventIds.Chunk(500))
        {
            var rows = ctx.IssueCases.AsNoTracking()
                .Where(c => c.ClosedAt == null && c.WorkOrderId != null && c.SourceKey != null && c.EventId != null
                            && batch.Contains(c.EventId.Value))
                .Select(c => new { c.SourceKey, EventId = c.EventId!.Value, c.HostNameKey })
                .ToList();

            foreach (var r in rows)
            {
                if (normalized.Contains((r.SourceKey!, r.EventId)))
                {
                    matched.Add((r.SourceKey!, r.EventId, r.HostNameKey));
                }
            }
        }

        return matched
            .GroupBy(r => (r.SourceKey, r.EventId))
            .Select(g => new
            {
                Key = g.Key,
                Count = g.Select(r => HostNameKey.Of(r.HostNameKey)).Distinct().Count()
            })
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Key, x => x.Count);
    }

    /// <summary>成員計數的查詢本體（抽出供兩後端 SQL 翻譯測試）</summary>
    internal static IQueryable<MemberCountRow> BuildCountMembersQuery(LfDbContext ctx, List<long> ids)
    {
        var today = DateTime.Today;
        return ctx.IssueCases.AsNoTracking()
            .Where(c => c.WorkOrderId != null && ids.Contains(c.WorkOrderId.Value))
            .GroupBy(c => c.WorkOrderId!.Value)
            .Select(g => new MemberCountRow
            {
                WorkOrderId = g.Key,
                Total = g.Count(),
                Active = g.Count(c => c.ClosedAt == null),
                Escalated = g.Count(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.Escalated),
                InProgress = g.Count(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.InProgress),
                Observing = g.Count(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.Observing),
                Open = g.Count(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.Open),
                Overdue = g.Count(c => c.ClosedAt == null && c.DueDate != null && c.DueDate < today
                                       && (c.Status == IssueHandlingStatuses.InProgress || c.Status == IssueHandlingStatuses.Observing)),
                DaySyncPending = g.Count(c => c.DaySyncPending)
            });
    }

    internal sealed class MemberCountRow
    {
        public long WorkOrderId { get; init; }
        public int Total { get; init; }
        public int Active { get; init; }
        public int Escalated { get; init; }
        public int InProgress { get; init; }
        public int Observing { get; init; }
        public int Open { get; init; }
        public int Overdue { get; init; }
        public int DaySyncPending { get; init; }
    }

    public WorkOrderPage QueryOrders(WorkOrderQuery q)
    {
        WorkOrderQueries.Validate(q);
        using var ctx = _contextFactory();

        var filtered = BuildOrderFilterQuery(ctx, q, DateTime.Today);
        var total = filtered.Count();
        var rows = ApplyOrderSort(ctx, filtered, q.Sort)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToList();

        return new WorkOrderPage { Items = rows.Select(ToModel).ToList(), Total = total };
    }

    /// <summary>
    /// 清單的篩選本體（抽出供 SQL 翻譯測試）：成員相關條件一律是關聯 EXISTS 子查詢，
    /// 逾期判準與 <see cref="WorkOrderQueries.IsOverdue"/> 同義。
    /// </summary>
    internal static IQueryable<WorkOrderRow> BuildOrderFilterQuery(LfDbContext ctx, WorkOrderQuery q, DateTime today)
    {
        var query = ctx.WorkOrders.AsNoTracking();

        if (q.HandlerIds != null)
        {
            var handlerIds = q.HandlerIds.Distinct().ToList();
            query = query.Where(w => handlerIds.Contains(w.HandlerId));
        }
        if (q.Source != null)
        {
            var key = SourceKeyOf(q.Source);
            query = query.Where(w => w.SourceKey == key);
        }
        if (q.EventId != null)
        {
            var eventId = q.EventId.Value;
            query = query.Where(w => w.EventId == eventId);
        }

        // 暫停（問題目前靜音中）：組合鍵與 IssueExclusion.CompositeKey 同規則，source_key 已是大寫欄
        if (q.PausedKeys != null && q.PausedMode != WorkOrderQueries.PausedInclude)
        {
            var pausedKeys = q.PausedKeys.Distinct().ToList();
            query = q.PausedMode == WorkOrderQueries.PausedOnly
                ? query.Where(w => w.ClosedAt == null && w.SourceKey != null && w.EventId != null
                                   && pausedKeys.Contains(w.SourceKey + IssueExclusion.CompositeKeySeparator + w.EventId.Value.ToString()))
                : query.Where(w => w.ClosedAt != null || w.SourceKey == null || w.EventId == null
                                   || !pausedKeys.Contains(w.SourceKey + IssueExclusion.CompositeKeySeparator + w.EventId.Value.ToString()));
        }
        if (q.OnlyKeys != null)
        {
            var onlyKeys = q.OnlyKeys.Distinct().ToList();
            query = query.Where(w => w.ClosedAt == null && w.SourceKey != null && w.EventId != null
                                     && onlyKeys.Contains(w.SourceKey + IssueExclusion.CompositeKeySeparator + w.EventId.Value.ToString()));
        }

        return q.Status switch
        {
            WorkOrderQueries.StatusActive => query.Where(w => w.ClosedAt == null),
            WorkOrderQueries.StatusEscalated => query.Where(w => w.ClosedAt == null
                && ctx.IssueCases.Any(c => c.WorkOrderId == w.WorkOrderId && c.ClosedAt == null
                                           && c.Status == IssueHandlingStatuses.Escalated)),
            WorkOrderQueries.StatusOverdue => query.Where(w => w.ClosedAt == null
                && ctx.IssueCases.Any(c => c.WorkOrderId == w.WorkOrderId && c.ClosedAt == null
                                           && c.DueDate != null && c.DueDate < today
                                           && (c.Status == IssueHandlingStatuses.InProgress || c.Status == IssueHandlingStatuses.Observing))),
            WorkOrderQueries.StatusUnreplied => query.Where(w => w.ClosedAt == null && w.LastReplyAt == null),
            WorkOrderQueries.StatusClosed => query.Where(w => w.ClosedAt != null),
            _ => query
        };
    }

    /// <summary>排序：同值一律再依 work_order_id 降冪，分頁才穩定</summary>
    private static IQueryable<WorkOrderRow> ApplyOrderSort(LfDbContext ctx, IQueryable<WorkOrderRow> query, string sort) => sort switch
    {
        WorkOrderQueries.SortMembersDesc => query
            .OrderByDescending(w => ctx.IssueCases.Count(c => c.WorkOrderId == w.WorkOrderId && c.ClosedAt == null))
            .ThenByDescending(w => w.WorkOrderId),
        // 未回覆的單在前、依建立時間升冪；已回覆的排後（同樣依建立時間升冪）
        WorkOrderQueries.SortUnrepliedOldest => query
            .OrderBy(w => w.LastReplyAt == null ? 0 : 1)
            .ThenBy(w => w.CreatedAt)
            .ThenByDescending(w => w.WorkOrderId),
        _ => query
            .OrderByDescending(w => w.CreatedAt)
            .ThenByDescending(w => w.WorkOrderId)
    };

    /// <remarks>
    /// **刻意不排除暫停單**（問題目前靜音中的進行中單）：暫停單的主機仍在處理人名下，靜音到期即恢復，
    /// 負載若在靜音期間瞬間歸零、到期又跳回，看板與自動派工的負載計算都會跟著抖動；
    /// 而派工 ⓪ 已不會為靜音問題建單，不會因此多派。
    /// </remarks>
    public List<HandlerLoad> LoadBoard()
    {
        using var ctx = _contextFactory();

        // 單一查詢：進行中單依處理人分組，成員數以關聯子查詢併進同一句 SQL
        return BuildLoadBoardQuery(ctx).ToList();
    }

    public WorkOrderHandlerSummary HandlerSummary(long handlerId, IReadOnlyCollection<string> pausedKeys)
    {
        using var ctx = _contextFactory();

        // 單一查詢：沒有進行中單時分組為空，回全 0
        return BuildHandlerSummaryQuery(ctx, handlerId, pausedKeys).FirstOrDefault() ?? new WorkOrderHandlerSummary();
    }

    /// <summary>
    /// 處理人摘要的查詢本體（抽出供兩後端 SQL 翻譯測試）；形狀同 <see cref="BuildLoadBoardQuery"/>。
    /// 暫停單（組合鍵在 <paramref name="pausedKeys"/> 內）不計入四個既有數字，含成員子查詢。
    /// </summary>
    internal static IQueryable<WorkOrderHandlerSummary> BuildHandlerSummaryQuery(LfDbContext ctx, long handlerId, IReadOnlyCollection<string> pausedKeys)
    {
        var today = DateTime.Today;
        var keys = pausedKeys.Distinct().ToList();

        // 逾期條件與 WorkOrderQueries.IsOverdue 同義。
        // 「非暫停」寫成 source_key 或 event_id 為 null、或組合鍵不在集合內——先擋 null，NOT IN 才不會遇到 NULL 語意
        return ctx.WorkOrders.AsNoTracking()
            .Where(w => w.HandlerId == handlerId && w.ClosedAt == null)
            .GroupBy(w => w.HandlerId)
            .Select(g => new WorkOrderHandlerSummary
            {
                ActiveWorkOrders = g.Count(w => w.SourceKey == null || w.EventId == null
                    || !keys.Contains(w.SourceKey + IssueExclusion.CompositeKeySeparator + w.EventId.Value.ToString())),
                UnrepliedWorkOrders = g.Count(w => w.LastReplyAt == null && (w.SourceKey == null || w.EventId == null
                    || !keys.Contains(w.SourceKey + IssueExclusion.CompositeKeySeparator + w.EventId.Value.ToString()))),
                PausedWorkOrders = g.Count(w => w.SourceKey != null && w.EventId != null
                    && keys.Contains(w.SourceKey + IssueExclusion.CompositeKeySeparator + w.EventId.Value.ToString())),
                ActiveMembers = ctx.IssueCases.Count(c =>
                    c.ClosedAt == null &&
                    ctx.WorkOrders.Any(o => o.WorkOrderId == c.WorkOrderId && o.ClosedAt == null && o.HandlerId == g.Key
                        && (o.SourceKey == null || o.EventId == null
                            || !keys.Contains(o.SourceKey + IssueExclusion.CompositeKeySeparator + o.EventId.Value.ToString())))),
                OverdueMembers = ctx.IssueCases.Count(c =>
                    c.ClosedAt == null && c.DueDate != null && c.DueDate < today
                    && (c.Status == IssueHandlingStatuses.InProgress || c.Status == IssueHandlingStatuses.Observing) &&
                    ctx.WorkOrders.Any(o => o.WorkOrderId == c.WorkOrderId && o.ClosedAt == null && o.HandlerId == g.Key
                        && (o.SourceKey == null || o.EventId == null
                            || !keys.Contains(o.SourceKey + IssueExclusion.CompositeKeySeparator + o.EventId.Value.ToString()))))
            });
    }

    public List<long> FindActiveWithoutActiveMembers(int take)
    {
        using var ctx = _contextFactory();

        // 單句 SQL：NOT EXISTS 關聯子查詢，不先撈單再逐張數成員
        return ctx.WorkOrders.AsNoTracking()
            .Where(w => w.ClosedAt == null
                        && !ctx.IssueCases.Any(c => c.WorkOrderId == w.WorkOrderId && c.ClosedAt == null))
            .OrderBy(w => w.WorkOrderId)
            .Select(w => w.WorkOrderId)
            .Take(take)
            .ToList();
    }

    /// <summary>負載看板的查詢本體（抽出供兩後端 SQL 翻譯測試）</summary>
    internal static IQueryable<HandlerLoad> BuildLoadBoardQuery(LfDbContext ctx)
    {
        var today = DateTime.Today;
        var closedSince = today.AddDays(-HandlerLoad.ClosedWindowDays);

        // 母體＝進行中單＋近 7 日結案單，依處理人分組；進行中類計數以條件聚合、成員類以關聯子查詢併進同一句 SQL。
        // 逾期條件與 WorkOrderQueries.IsOverdue 同義
        return ctx.WorkOrders.AsNoTracking()
            .Where(w => w.ClosedAt == null || w.ClosedAt >= closedSince)
            .GroupBy(w => w.HandlerId)
            .Select(g => new HandlerLoad
            {
                HandlerId = g.Key,
                ActiveWorkOrders = g.Count(w => w.ClosedAt == null),
                UnrepliedWorkOrders = g.Count(w => w.ClosedAt == null && w.LastReplyAt == null),
                ClosedLast7Days = g.Count(w => w.ClosedAt != null),
                OldestActiveCreatedAt = g.Min(w => w.ClosedAt == null ? (DateTime?)w.CreatedAt : null),
                ActiveMembers = ctx.IssueCases.Count(c =>
                    c.ClosedAt == null &&
                    ctx.WorkOrders.Any(o => o.WorkOrderId == c.WorkOrderId && o.ClosedAt == null && o.HandlerId == g.Key)),
                OverdueMembers = ctx.IssueCases.Count(c =>
                    c.ClosedAt == null && c.DueDate != null && c.DueDate < today
                    && (c.Status == IssueHandlingStatuses.InProgress || c.Status == IssueHandlingStatuses.Observing) &&
                    ctx.WorkOrders.Any(o => o.WorkOrderId == c.WorkOrderId && o.ClosedAt == null && o.HandlerId == g.Key))
            });
    }

    /// <summary>
    /// 清除結案早於保留期的交辦單與其事件（形狀比照 <see cref="EfIssueCaseStore.Prune(int)"/>）。
    /// 進行中的單不論多舊都保留——判準是結案時間，理由同案件清理。
    /// </summary>
    public int PruneClosed(int retentionDays) => PruneClosed(retentionDays, BatchedPrune.MaxRowsPerRun, BatchedPrune.BatchSize);

    internal int PruneClosed(int retentionDays, int maxRows, int batchSize)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);

        return BatchedPrune.Run<long>(_contextFactory,
            (ctx, take) => ctx.WorkOrders
                .Where(w => w.ClosedAt != null && w.ClosedAt < cutoff)
                .OrderBy(w => w.ClosedAt)
                .Select(w => w.WorkOrderId)
                .Take(take)
                .ToList(),
            (ctx, ids) =>
            {
                // 先刪該批單的事件，再刪單——反過來的話中途失敗會留下指向不存在單的孤兒事件
                ctx.WorkOrderEvents.Where(e => ids.Contains(e.WorkOrderId)).ExecuteDelete();
                return ctx.WorkOrders.Where(w => ids.Contains(w.WorkOrderId)).ExecuteDelete();
            },
            ctx => ctx.WorkOrders.Count(w => w.ClosedAt != null && w.ClosedAt < cutoff),
            "已結案的交辦單", maxRows, batchSize);
    }

    internal static void CopyToRow(WorkOrder order, WorkOrderRow row)
    {
        row.SourceName = TruncateOrNull(order.SourceName, SourceMaxLength);
        row.SourceKey = order.SourceName == null ? null : SourceKeyOf(order.SourceName);
        row.EventId = order.EventId;
        row.IssueLabel = Truncate(order.IssueLabel, IssueLabelMaxLength);
        row.HandlerId = order.HandlerId;
        row.Origin = Truncate(order.Origin, CodeMaxLength);
        row.ScopeKind = Truncate(order.ScopeKind, CodeMaxLength);
        row.ScopeGroupIds = string.Join(",", order.ScopeGroupIds);
        row.AutoAttach = order.AutoAttach;
        row.Note = TruncateOrNull(order.Note, NoteMaxLength);
        row.DueDate = order.DueDate;
        row.CreatedById = order.CreatedById;
        row.CreatedByAccount = Truncate(order.CreatedByAccount, AccountMaxLength);
        row.CreatedAt = order.CreatedAt;
        row.LastAppendedAt = order.LastAppendedAt;
        row.LastReplyAt = order.LastReplyAt;
        row.ClosedAt = order.ClosedAt;
        row.ClosedReason = TruncateOrNull(order.ClosedReason, CodeMaxLength);
        row.UpdatedAt = order.UpdatedAt;
    }

    private static WorkOrder ToModel(WorkOrderRow row) => new()
    {
        WorkOrderId = row.WorkOrderId,
        SourceName = row.SourceName,
        EventId = row.EventId,
        IssueLabel = row.IssueLabel,
        HandlerId = row.HandlerId,
        Origin = row.Origin,
        ScopeKind = row.ScopeKind,
        ScopeGroupIds = row.ScopeGroupIds.Length == 0
            ? new List<long>()
            : row.ScopeGroupIds.Split(',').Select(long.Parse).ToList(),
        AutoAttach = row.AutoAttach,
        Note = row.Note,
        DueDate = row.DueDate,
        CreatedById = row.CreatedById,
        CreatedByAccount = row.CreatedByAccount,
        CreatedAt = row.CreatedAt,
        LastAppendedAt = row.LastAppendedAt,
        LastReplyAt = row.LastReplyAt,
        ClosedAt = row.ClosedAt,
        ClosedReason = row.ClosedReason,
        UpdatedAt = row.UpdatedAt
    };
}
