using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 階段 P-1 新增的 store 查詢：符合條件的全部單（全體排序用）、多張單指定動作的事件（被退回判定用）、
/// 多張單的全部成員精簡欄位（嚴重度最後已知值用）。SQLite 合約測試（結果）＋SQL Server <c>ToQueryString</c> 翻譯測試（未連線實際 SQL Server）。
/// </summary>
public sealed class WorkOrderPriorityStoreTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 11, 20);

    private readonly EfSqliteFixture _fx = new();
    private int _seq;

    public void Dispose() => _fx.Dispose();

    private EfIssueCaseStore Cases() => new(_fx.NewContext);
    private EfWorkOrderStore Orders() => new(_fx.NewContext, new FixedClock(Today.AddHours(10)));

    private sealed class FixedClock(DateTime local) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    }

    private long NewOrder(int eventId, long handler = 1, int ageDays = 5, DateTime? closed = null) => Orders().Insert(new WorkOrder
    {
        SourceName = "disk", EventId = eventId, IssueLabel = $"disk {eventId}", HandlerId = handler, Origin = WorkOrderOrigins.Manual,
        ScopeKind = WorkOrderScopes.Hosts, CreatedByAccount = "admin", CreatedAt = Today.AddDays(-ageDays), ClosedAt = closed
    });

    private void AddMember(long orderId, string host, string status, int eventId, DateTime? closed = null, DateTime? due = null)
    {
        Cases().Save(new IssueCase
        {
            CaseId = $"case-{++_seq:D4}", HostName = host, IssueKey = IssueSignatureKey.For("System", "disk", eventId, LogEntryType.Error),
            IssueLabel = $"disk {eventId}", Status = status, HandlerId = 1, DueDate = due, FirstLinkedDate = Today.AddDays(-3),
            LastLinkedDate = Today.AddDays(-3), CreatedAt = Today.AddDays(-3), CreatedByAccount = "admin", UpdatedAt = Today.AddDays(-3),
            ClosedAt = closed, WorkOrderId = orderId
        });
    }

    // ── FilterOrders ─────────────────────────────────────────────────────────

    [Fact]
    public void FilterOrders_回全部符合的單_超過單頁上限也不截斷_建立時間新到舊_單號降冪()
    {
        var ids = Enumerable.Range(0, 105).Select(i => NewOrder(1000 + i, ageDays: 1 + i / 3)).ToList();
        NewOrder(9000, ageDays: 2, closed: Today.AddDays(-1));

        var active = Orders().FilterOrders(new WorkOrderQuery { Status = WorkOrderQueries.StatusActive });
        var all = Orders().FilterOrders(new WorkOrderQuery { Status = WorkOrderQueries.StatusAll, Page = 3, PageSize = 7 });

        Assert.Equal(105, active.Count);
        Assert.Equal(106, all.Count);   // 分頁參數被忽略
        Assert.Equal(active.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.WorkOrderId).Select(o => o.WorkOrderId),
            active.Select(o => o.WorkOrderId));
        Assert.Equal(ids.OrderBy(i => i), active.Select(o => o.WorkOrderId).OrderBy(i => i));
    }

    [Fact]
    public void FilterOrders_套用與清單相同的篩選_處理人_待我處理_已結案()
    {
        var mine = NewOrder(1, handler: 7);
        var others = NewOrder(2, handler: 8);
        var closed = NewOrder(3, handler: 7, closed: Today.AddDays(-1));
        AddMember(mine, "h1", IssueHandlingStatuses.InProgress, 1);
        AddMember(others, "h2", IssueHandlingStatuses.InProgress, 2);
        var obsOnly = NewOrder(4, handler: 7);
        AddMember(obsOnly, "h3", IssueHandlingStatuses.Observing, 4, due: Today.AddDays(3));

        var handler7 = Orders().FilterOrders(new WorkOrderQuery { HandlerIds = new long[] { 7 }, Status = WorkOrderQueries.StatusActive });
        var todo = Orders().FilterOrders(new WorkOrderQuery { HandlerIds = new long[] { 7 }, Status = WorkOrderQueries.StatusTodo });
        var closedOnly = Orders().FilterOrders(new WorkOrderQuery { Status = WorkOrderQueries.StatusClosed });

        Assert.Equal(new[] { mine, obsOnly }.OrderBy(i => i), handler7.Select(o => o.WorkOrderId).OrderBy(i => i));
        Assert.Equal(new[] { mine }, todo.Select(o => o.WorkOrderId));
        Assert.Equal(new[] { closed }, closedOnly.Select(o => o.WorkOrderId));
        Assert.DoesNotContain(others, handler7.Select(o => o.WorkOrderId));
    }

    // ── ListEventsOf ─────────────────────────────────────────────────────────

    [Fact]
    public void ListEventsOf_只回指定動作與指定單_依時間與事件序升冪_沒有事件的單不出現()
    {
        var o1 = NewOrder(1);
        var o2 = NewOrder(2);
        var o3 = NewOrder(3);
        var store = Orders();
        void Evt(long id, string action, int hour, string? note = null) =>
            store.AppendEvent(new WorkOrderEvent { WorkOrderId = id, Action = action, ActorAccount = "x", CreatedAt = Today.AddHours(hour), Note = note });
        Evt(o1, WorkOrderEventActions.Created, 1);
        Evt(o1, WorkOrderEventActions.Replied, 5);
        Evt(o1, WorkOrderEventActions.Returned, 3, "退回");
        Evt(o2, WorkOrderEventActions.Appended, 2);
        Evt(o3, WorkOrderEventActions.Returned, 4, "別張單");

        var result = store.ListEventsOf(new[] { o1, o2 }, new[] { WorkOrderEventActions.Returned, WorkOrderEventActions.Replied });

        var only = Assert.Single(result);
        Assert.Equal(o1, only.Key);
        Assert.Equal(new[] { WorkOrderEventActions.Returned, WorkOrderEventActions.Replied }, only.Value.Select(e => e.Action));
        Assert.Equal("退回", only.Value[0].Note);
        Assert.Empty(store.ListEventsOf(Array.Empty<long>(), new[] { WorkOrderEventActions.Returned }));
        Assert.Empty(store.ListEventsOf(new[] { o1 }, Array.Empty<string>()));
    }

    // ── GetMemberSummaries ───────────────────────────────────────────────────

    [Fact]
    public void GetMemberSummaries_含已結案成員_只回指定單_精簡欄位仍帶判定要的值()
    {
        var o1 = NewOrder(1);
        var o2 = NewOrder(2);
        AddMember(o1, "h1", IssueHandlingStatuses.InProgress, 1, due: Today.AddDays(2));
        AddMember(o1, "h2", IssueHandlingStatuses.Resolved, 1, closed: Today.AddDays(-1));
        AddMember(o2, "h3", IssueHandlingStatuses.InProgress, 2);

        var members = Cases().GetMemberSummaries(new[] { o1 });

        Assert.Equal(new[] { "h1", "h2" }, members.Select(c => c.HostName).OrderBy(x => x));
        var first = members.Single(c => c.HostName == "h1");
        Assert.Equal(Today.AddDays(2), first.DueDate);
        Assert.Equal(("disk", 1), (first.SourceName, first.EventId));
        Assert.NotNull(members.Single(c => c.HostName == "h2").ClosedAt);
        Assert.Empty(Cases().GetMemberSummaries(Array.Empty<long>()));
    }

    // ── SQL Server／SQLite 翻譯（未連線實際 SQL Server）────────────────────────

    private static LfDbContext Context(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        return new LfDbContext(builder.Options);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void 兩後端翻譯_全部符合的單_篩選與排序都在SQL端(string provider)
    {
        using var ctx = Context(provider);

        var sql = EfWorkOrderStore.BuildFilterAllQuery(ctx,
            new WorkOrderQuery { Status = WorkOrderQueries.StatusTodo, HandlerIds = new long[] { 1 }, ExcludedHostKeys = new[] { "OFF" } }, Today).ToQueryString();

        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FETCH", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOP(", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void 兩後端翻譯_多張單指定動作的事件_單號與動作IN清單_依時間排序(string provider)
    {
        using var ctx = Context(provider);

        var sql = EfWorkOrderStore.BuildEventsOfQuery(ctx, new List<long> { 1, 2 }, new List<string> { "returned", "replied" }).ToQueryString();

        Assert.Contains("IN (", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("created_at", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void 兩後端翻譯_多張單的全部成員精簡查詢_不限進行中_不讀PRTG證據與逐日意圖(string provider)
    {
        using var ctx = Context(provider);

        var sql = EfIssueCaseStore.BuildMemberSummaryQuery(ctx, new long[] { 1, 2 }).ToQueryString();

        Assert.Contains("work_order_id", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("closed_at\" IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[closed_at] IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prtg_evidence_json", sql);
        Assert.DoesNotContain("day_sync_intent", sql);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void 兩後端翻譯_最近出現點查詢帶出重大旗標欄位(string provider)
    {
        using var ctx = Context(provider);
        var q = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= Today.AddDays(-6) && x.RecordDate <= Today);

        var (latest, _) = EfIssueAggregateQuery.BuildLatestOccurrenceQueries(q, new HashSet<string> { "DISK" });

        Assert.Contains("elevates_day_risk", latest.ToQueryString(), StringComparison.OrdinalIgnoreCase);
    }
}
