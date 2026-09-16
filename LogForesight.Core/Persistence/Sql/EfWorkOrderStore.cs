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
    internal const int SourceMaxLength = 255;
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
    internal static string SourceKeyOf(string source) => Truncate(source, SourceMaxLength).ToUpperInvariant();

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
            Escalated = r.Escalated
        });
    }

    /// <summary>成員計數的查詢本體（抽出供兩後端 SQL 翻譯測試）</summary>
    internal static IQueryable<MemberCountRow> BuildCountMembersQuery(LfDbContext ctx, List<long> ids) =>
        ctx.IssueCases.AsNoTracking()
            .Where(c => c.WorkOrderId != null && ids.Contains(c.WorkOrderId.Value))
            .GroupBy(c => c.WorkOrderId!.Value)
            .Select(g => new MemberCountRow
            {
                WorkOrderId = g.Key,
                Total = g.Count(),
                Active = g.Count(c => c.ClosedAt == null),
                Escalated = g.Count(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.Escalated)
            });

    internal sealed class MemberCountRow
    {
        public long WorkOrderId { get; init; }
        public int Total { get; init; }
        public int Active { get; init; }
        public int Escalated { get; init; }
    }

    public List<HandlerLoad> LoadBoard()
    {
        using var ctx = _contextFactory();

        // 單一查詢：進行中單依處理人分組，成員數以關聯子查詢併進同一句 SQL
        return BuildLoadBoardQuery(ctx).ToList();
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
    internal static IQueryable<HandlerLoad> BuildLoadBoardQuery(LfDbContext ctx) =>
        ctx.WorkOrders.AsNoTracking()
            .Where(w => w.ClosedAt == null)
            .GroupBy(w => w.HandlerId)
            .Select(g => new HandlerLoad
            {
                HandlerId = g.Key,
                ActiveWorkOrders = g.Count(),
                UnrepliedWorkOrders = g.Count(w => w.LastReplyAt == null),
                OldestActiveCreatedAt = g.Min(w => w.CreatedAt),
                ActiveMembers = ctx.IssueCases.Count(c =>
                    c.ClosedAt == null &&
                    ctx.WorkOrders.Any(o => o.WorkOrderId == c.WorkOrderId && o.ClosedAt == null && o.HandlerId == g.Key))
            });

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
