using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="EfWorkOrderStore"/> 的合約測試（SQLite）：欄位往返、併發權杖、部分唯一索引、
/// source 大小寫、成員計數與負載看板（含單一查詢斷言）、保留清理、字串截斷。
/// </summary>
public class WorkOrderStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly CommandCounter _counter = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private EfWorkOrderStore Store() => new(_fx.NewContext);

    /// <summary>同一條 in-memory 連線、多掛一個命令計數攔截器的 store</summary>
    private EfWorkOrderStore CountingStore()
    {
        using var probe = _fx.NewContext();
        var connection = probe.Database.GetDbConnection();
        return new EfWorkOrderStore(() => new LfDbContext(
            new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).AddInterceptors(_counter).Options));
    }

    /// <summary>計算發出的讀取命令數</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Readers { get; set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Readers++;
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    private static WorkOrder NewOrder(long handler, string? source = "Disk", int? eventId = 153) => new()
    {
        SourceName = source,
        EventId = eventId,
        IssueLabel = source == null ? "多問題" : $"{source} {eventId}",
        HandlerId = handler,
        Origin = WorkOrderOrigins.Manual,
        ScopeKind = WorkOrderScopes.Hosts,
        CreatedByAccount = "admin",
        CreatedAt = DateTime.Now
    };

    private void AddCase(string caseId, long? workOrderId, string status, DateTime? closedAt)
    {
        using var ctx = _fx.NewContext();
        ctx.IssueCases.Add(new IssueCaseRow
        {
            CaseId = caseId, HostName = caseId, HostNameKey = caseId.ToUpperInvariant(),
            IssueKey = "System|Disk|153|1", IssueLabel = "Disk 153", Status = status,
            HandlerId = 1, CreatedAt = DateTime.Now, CreatedByAccount = "admin", UpdatedAt = DateTime.Now,
            ClosedAt = closedAt, WorkOrderId = workOrderId
        });
        ctx.SaveChanges();
    }

    [Fact]
    public void Insert回填id_Get讀回全部欄位()
    {
        var store = Store();
        var due = DateTime.Today.AddDays(5);
        var order = new WorkOrder
        {
            SourceName = "Disk", EventId = 153, IssueLabel = "Disk 153", HandlerId = 7,
            Origin = WorkOrderOrigins.OwnerRule, ScopeKind = WorkOrderScopes.Groups,
            ScopeGroupIds = new List<long> { 3, 11, 42 }, AutoAttach = true, Note = "說明",
            DueDate = due, CreatedById = 9, CreatedByAccount = "boss", CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0),
            LastAppendedAt = new DateTime(2026, 9, 2), LastReplyAt = new DateTime(2026, 9, 3),
            ClosedAt = new DateTime(2026, 9, 4), ClosedReason = WorkOrderCloseReasons.AdminClosed
        };

        var id = store.Insert(order);

        Assert.True(id > 0);
        Assert.Equal(id, order.WorkOrderId);
        var got = store.Get(id)!;
        Assert.Equal("Disk", got.SourceName);
        Assert.Equal(153, got.EventId);
        Assert.Equal("Disk 153", got.IssueLabel);
        Assert.Equal(7, got.HandlerId);
        Assert.Equal(WorkOrderOrigins.OwnerRule, got.Origin);
        Assert.Equal(WorkOrderScopes.Groups, got.ScopeKind);
        Assert.Equal(new List<long> { 3, 11, 42 }, got.ScopeGroupIds);
        Assert.True(got.AutoAttach);
        Assert.Equal("說明", got.Note);
        Assert.Equal(due, got.DueDate);
        Assert.Equal(9, got.CreatedById);
        Assert.Equal("boss", got.CreatedByAccount);
        Assert.Equal(order.CreatedAt, got.CreatedAt);
        Assert.Equal(order.LastAppendedAt, got.LastAppendedAt);
        Assert.Equal(order.LastReplyAt, got.LastReplyAt);
        Assert.Equal(order.ClosedAt, got.ClosedAt);
        Assert.Equal(WorkOrderCloseReasons.AdminClosed, got.ClosedReason);
        Assert.Equal(order.UpdatedAt, got.UpdatedAt);
    }

    [Fact]
    public void ScopeGroupIds空清單往返()
    {
        var store = Store();
        var id = store.Insert(NewOrder(1));

        Assert.Empty(store.Get(id)!.ScopeGroupIds);
    }

    [Fact]
    public void Save併發衝突擲DbUpdateConcurrencyException()
    {
        var store = Store();
        var id = store.Insert(NewOrder(1));
        var a = store.Get(id)!;
        var b = store.Get(id)!;

        a.Note = "先存";
        var before = a.UpdatedAt;
        store.Save(a);
        Assert.NotEqual(before, a.UpdatedAt);
        Assert.Equal("先存", store.Get(id)!.Note);

        b.Note = "後存";
        Assert.Throws<DbUpdateConcurrencyException>(() => store.Save(b));
        Assert.Equal("先存", store.Get(id)!.Note);
    }

    [Fact]
    public void 部分唯一索引_同處理人同問題只能有一張進行中單()
    {
        var store = Store();
        var first = NewOrder(1);
        store.Insert(first);

        Assert.Throws<DbUpdateException>(() => store.Insert(NewOrder(1, "DISK", 153)));

        first.ClosedAt = DateTime.Now;
        first.ClosedReason = WorkOrderCloseReasons.AllClosed;
        store.Save(first);

        var again = store.Insert(NewOrder(1));
        Assert.True(again > first.WorkOrderId);
    }

    [Fact]
    public void 部分唯一索引_同處理人兩張多問題單都能建立()
    {
        var store = Store();

        store.Insert(NewOrder(1, null, null));
        store.Insert(NewOrder(1, null, null));

        Assert.Equal(2, store.GetActiveByHandler(1).Count);
    }

    [Fact]
    public void source大小寫不敏感()
    {
        var store = Store();
        var id = store.Insert(NewOrder(5, "dcom", 10016));

        Assert.Equal(id, store.GetActiveFor(5, "DCOM", 10016)!.WorkOrderId);
        Assert.Single(store.GetActiveByIssue("Dcom", 10016));
        Assert.Equal("dcom", store.Get(id)!.SourceName);
        Assert.Null(store.GetActiveFor(6, "DCOM", 10016));
    }

    [Fact]
    public void CountMembers逐欄數字正確()
    {
        var store = Store();
        var o1 = store.Insert(NewOrder(1, "A", 1));
        var o2 = store.Insert(NewOrder(1, "B", 2));
        var o3 = store.Insert(NewOrder(2, "C", 3));

        AddCase("h1", o1, IssueHandlingStatuses.InProgress, null);
        AddCase("h2", o1, IssueHandlingStatuses.Escalated, null);
        AddCase("h3", o1, IssueHandlingStatuses.Resolved, DateTime.Now);
        AddCase("h4", o2, IssueHandlingStatuses.Escalated, DateTime.Now);   // 已結案的 escalated 不算
        AddCase("h5", o2, IssueHandlingStatuses.Open, null);
        AddCase("h6", o3, IssueHandlingStatuses.Resolved, DateTime.Now);
        AddCase("h7", null, IssueHandlingStatuses.Open, null);

        var counts = store.CountMembers(new[] { o1, o2, o3 });

        Assert.Equal((3, 2, 1, 1), (counts[o1].Total, counts[o1].Active, counts[o1].Closed, counts[o1].Escalated));
        Assert.Equal((2, 1, 1, 0), (counts[o2].Total, counts[o2].Active, counts[o2].Closed, counts[o2].Escalated));
        Assert.Equal((1, 0, 1, 0), (counts[o3].Total, counts[o3].Active, counts[o3].Closed, counts[o3].Escalated));
    }

    [Fact]
    public void CountMembers空ids回空字典()
    {
        Assert.Empty(Store().CountMembers(Array.Empty<long>()));
    }

    [Fact]
    public void CountMembers對50張單只發一次SQL()
    {
        var store = Store();
        var ids = new List<long>();
        for (var i = 0; i < 50; i++)
        {
            var id = store.Insert(NewOrder(1, "S" + i, i));
            ids.Add(id);
            AddCase("c" + i, id, IssueHandlingStatuses.InProgress, null);
        }

        var counting = CountingStore();
        _counter.Readers = 0;
        var counts = counting.CountMembers(ids);

        Assert.Equal(1, _counter.Readers);
        Assert.Equal(50, counts.Count);
    }

    [Fact]
    public void LoadBoard兩位處理人數字正確且已結案單不計()
    {
        var store = Store();
        var oldest = new DateTime(2026, 8, 1);
        var a1 = NewOrder(1, "A", 1); a1.CreatedAt = oldest; a1.LastReplyAt = DateTime.Now;
        var a2 = NewOrder(1, "B", 2); a2.CreatedAt = new DateTime(2026, 8, 5);
        var a3 = NewOrder(1, "C", 3); a3.CreatedAt = new DateTime(2026, 7, 1); a3.ClosedAt = DateTime.Now;
        var b1 = NewOrder(2, "A", 1); b1.CreatedAt = new DateTime(2026, 8, 9);
        var ida1 = store.Insert(a1); var ida2 = store.Insert(a2); var ida3 = store.Insert(a3); var idb1 = store.Insert(b1);

        AddCase("a1-1", ida1, IssueHandlingStatuses.InProgress, null);
        AddCase("a1-2", ida1, IssueHandlingStatuses.Resolved, DateTime.Now);
        AddCase("a2-1", ida2, IssueHandlingStatuses.Open, null);
        AddCase("a3-1", ida3, IssueHandlingStatuses.Open, null);   // 已結案單底下的進行中案件不計
        AddCase("b1-1", idb1, IssueHandlingStatuses.Open, null);
        AddCase("b1-2", idb1, IssueHandlingStatuses.Open, null);

        var counting = CountingStore();
        _counter.Readers = 0;
        var board = counting.LoadBoard().ToDictionary(l => l.HandlerId);

        Assert.Equal(1, _counter.Readers);
        Assert.Equal(2, board.Count);
        Assert.Equal(2, board[1].ActiveWorkOrders);
        Assert.Equal(2, board[1].ActiveMembers);
        Assert.Equal(1, board[1].UnrepliedWorkOrders);
        Assert.Equal(oldest, board[1].OldestActiveCreatedAt);
        Assert.Equal(1, board[2].ActiveWorkOrders);
        Assert.Equal(2, board[2].ActiveMembers);
        Assert.Equal(1, board[2].UnrepliedWorkOrders);
        Assert.Equal(new DateTime(2026, 8, 9), board[2].OldestActiveCreatedAt);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void CountMembers與LoadBoard兩後端都翻譯成單一SQL(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);

        var countSql = EfWorkOrderStore.BuildCountMembersQuery(ctx, new List<long> { 1, 2 }).ToQueryString();
        var boardSql = EfWorkOrderStore.BuildLoadBoardQuery(ctx).ToQueryString();

        Assert.Contains("GROUP BY", countSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", boardSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lf_issue_cases", boardSql);
    }

    [Fact]
    public void PruneClosed刪過期已結案單與其事件_進行中與未過期不動()
    {
        var store = Store();
        var expired = NewOrder(1, "A", 1); expired.ClosedAt = DateTime.Today.AddDays(-40);
        var recent = NewOrder(1, "B", 2); recent.ClosedAt = DateTime.Today.AddDays(-5);
        var active = NewOrder(1, "C", 3); active.CreatedAt = DateTime.Today.AddDays(-400);
        var idExpired = store.Insert(expired); var idRecent = store.Insert(recent); var idActive = store.Insert(active);
        foreach (var id in new[] { idExpired, idRecent, idActive })
            store.AppendEvent(new WorkOrderEvent { WorkOrderId = id, Action = WorkOrderEventActions.Created, ActorAccount = "a", CreatedAt = DateTime.Today.AddDays(-400) });

        var pruned = store.PruneClosed(30);

        Assert.Equal(1, pruned);
        Assert.Null(store.Get(idExpired));
        Assert.Empty(store.ListEvents(idExpired));
        Assert.NotNull(store.Get(idRecent));
        Assert.Single(store.ListEvents(idRecent));
        Assert.NotNull(store.Get(idActive));
        Assert.Single(store.ListEvents(idActive));
    }

    [Fact]
    public void ListEvents依CreatedAt升冪()
    {
        var store = Store();
        var id = store.Insert(NewOrder(1));
        store.AppendEvent(new WorkOrderEvent { WorkOrderId = id, Action = WorkOrderEventActions.Appended, ActorAccount = "a", MemberDelta = 2, CreatedAt = new DateTime(2026, 9, 2) });
        store.AppendEvent(new WorkOrderEvent { WorkOrderId = id, Action = WorkOrderEventActions.Created, ActorAccount = "a", MemberDelta = 3, Note = "n", CreatedAt = new DateTime(2026, 9, 1) });

        var events = store.ListEvents(id);

        Assert.Equal(new[] { WorkOrderEventActions.Created, WorkOrderEventActions.Appended }, events.Select(e => e.Action));
        Assert.Equal(3, events[0].MemberDelta);
        Assert.Equal("n", events[0].Note);
    }

    [Fact]
    public void 字串寫入前截斷()
    {
        var store = Store();
        var order = NewOrder(1);
        order.IssueLabel = new string('x', 700);
        order.Note = new string('n', 1500);

        var id = store.Insert(order);

        var got = store.Get(id)!;
        Assert.Equal(512, got.IssueLabel.Length);
        Assert.Equal(1000, got.Note!.Length);
    }

    [Fact]
    public void FindActiveWithoutActiveMembers_零成員與成員全結案的進行中單才列出_依id升冪()
    {
        var store = Store();
        var empty = store.Insert(NewOrder(1, "A", 1));
        var allClosed = store.Insert(NewOrder(1, "B", 2));
        AddCase("b1", allClosed, IssueHandlingStatuses.Resolved, DateTime.Now);
        var hasActive = store.Insert(NewOrder(1, "C", 3));
        AddCase("c1", hasActive, IssueHandlingStatuses.Resolved, DateTime.Now);
        AddCase("c2", hasActive, IssueHandlingStatuses.InProgress, null);
        var closedOrder = NewOrder(1, "D", 4);
        closedOrder.ClosedAt = DateTime.Now;
        closedOrder.ClosedReason = WorkOrderCloseReasons.AllClosed;
        store.Insert(closedOrder);
        // 別張單的進行中案件不能讓這張單被排除
        var another = store.Insert(NewOrder(2, "A", 1));
        AddCase("x1", another, IssueHandlingStatuses.InProgress, null);

        Assert.Equal(new List<long> { empty, allClosed }, store.FindActiveWithoutActiveMembers(10));
        Assert.Equal(new List<long> { empty }, store.FindActiveWithoutActiveMembers(1));
    }

    [Fact]
    public void FindActiveWithoutActiveMembers只發一次SQL()
    {
        var store = Store();
        for (var i = 0; i < 20; i++)
        {
            var id = store.Insert(NewOrder(1, "S" + i, i));
            AddCase("c" + i, id, IssueHandlingStatuses.Resolved, DateTime.Now);
        }

        var counting = CountingStore();
        _counter.Readers = 0;
        var ids = counting.FindActiveWithoutActiveMembers(200);

        Assert.Equal(1, _counter.Readers);
        Assert.Equal(20, ids.Count);
    }

    // ── 派工脈絡用：GetAllActive ─────────────────────────────────────────

    [Fact]
    public void GetAllActive只回進行中單_含多問題單()
    {
        var store = Store();
        var a = store.Insert(NewOrder(1));
        var b = store.Insert(NewOrder(2, source: null, eventId: null));
        var closed = NewOrder(3, "Other", 1);
        store.Insert(closed);
        closed.ClosedAt = DateTime.Now;
        store.Save(closed);

        var ids = store.GetAllActive().Select(o => o.WorkOrderId).OrderBy(x => x).ToList();

        Assert.Equal(new[] { a, b }.OrderBy(x => x), ids);
    }

    [Fact]
    public void GetAllActive替身語意同EF版()
    {
        var store = new FakeWorkOrderStore(new FakeIssueCaseStore());
        var a = store.Insert(NewOrder(1));
        var closed = NewOrder(3, "Other", 1);
        store.Insert(closed);
        closed.ClosedAt = DateTime.Now;
        store.Save(closed);

        Assert.Equal(new[] { a }, store.GetAllActive().Select(o => o.WorkOrderId));
    }

    // ── QueryOrders／擴充計數（task-47-C2a）：EF 與替身同資料雙跑 ───────────────

    private static readonly DateTime Base = new(2026, 9, 1, 8, 0, 0);

    /// <summary>
    /// 同一份資料寫進任一組 store：
    /// W1 escalated 成員、W2 逾期成員、W3 已回覆（無特殊成員）、W4 已結案、W5 成員最多且與 W6 同建立時間、
    /// W6 未回覆、W7 期限今天（不算逾期）、W8 escalated 但已結案成員（不算）。
    /// </summary>
    private static void SeedOrders(IWorkOrderStore orders, IIssueCaseStore cases, int eventBase)
    {
        long Add(string label, long handler, string source, DateTime createdAt, DateTime? replied = null, DateTime? closed = null)
        {
            var o = new WorkOrder
            {
                SourceName = source, EventId = eventBase + int.Parse(label[1..]), IssueLabel = label, HandlerId = handler,
                Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, CreatedByAccount = "admin",
                CreatedAt = createdAt, LastReplyAt = replied, ClosedAt = closed
            };
            return orders.Insert(o);
        }

        void Member(long orderId, string caseId, string host, string status, DateTime? due = null, DateTime? closed = null)
        {
            cases.Save(new IssueCase
            {
                CaseId = caseId, HostName = host, IssueKey = "System|Disk|153|2", IssueLabel = "Disk 153",
                Status = status, HandlerId = 1, DueDate = due, FirstLinkedDate = Base, LastLinkedDate = Base,
                CreatedAt = Base, CreatedByAccount = "admin", UpdatedAt = Base, ClosedAt = closed, WorkOrderId = orderId
            });
        }

        var today = DateTime.Today;
        var w1 = Add("W1", 1, "Disk", Base.AddHours(1));
        Member(w1, "w1a", "H1", IssueHandlingStatuses.Escalated);
        var w2 = Add("W2", 1, "disk", Base.AddHours(2));
        Member(w2, "w2a", "H2", IssueHandlingStatuses.InProgress, due: today.AddDays(-1));
        var w3 = Add("W3", 2, "Disk", Base.AddHours(3), replied: Base.AddHours(4));
        Member(w3, "w3a", "H3", IssueHandlingStatuses.Observing, due: today.AddDays(3));
        var w4 = Add("W4", 2, "Disk", Base.AddHours(4), closed: Base.AddDays(1));
        Member(w4, "w4a", "H4", IssueHandlingStatuses.Resolved, closed: Base.AddDays(1));
        var w5 = Add("W5", 3, "Other", Base.AddHours(5));
        Member(w5, "w5a", "H5", IssueHandlingStatuses.Open);
        Member(w5, "w5b", "H6", IssueHandlingStatuses.Open);
        Member(w5, "w5c", "H7", IssueHandlingStatuses.Open);
        Add("W6", 3, "Disk", Base.AddHours(5));
        var w7 = Add("W7", 1, "Disk", Base.AddHours(6), replied: Base.AddHours(7));
        Member(w7, "w7a", "H8", IssueHandlingStatuses.InProgress, due: today);
        var w8 = Add("W8", 1, "Disk", Base.AddHours(7));
        Member(w8, "w8a", "H9", IssueHandlingStatuses.Escalated, closed: Base.AddDays(1));
        Member(w8, "w8b", "H10", IssueHandlingStatuses.InProgress, due: today.AddDays(-2), closed: Base.AddDays(1));
    }

    private static List<string> Labels(IWorkOrderStore store, WorkOrderQuery q) =>
        store.QueryOrders(q).Items.Select(o => o.IssueLabel).ToList();

    public static IEnumerable<object[]> OrderQueries() => new[]
    {
        new object[] { "active", "created_desc", null!, null!, new[] { "W8", "W7", "W6", "W5", "W3", "W2", "W1" } },
        new object[] { "escalated", "created_desc", null!, null!, new[] { "W1" } },
        new object[] { "overdue", "created_desc", null!, null!, new[] { "W2" } },
        new object[] { "unreplied", "created_desc", null!, null!, new[] { "W8", "W6", "W5", "W2", "W1" } },
        new object[] { "closed", "created_desc", null!, null!, new[] { "W4" } },
        new object[] { "all", "created_desc", null!, null!, new[] { "W8", "W7", "W6", "W5", "W4", "W3", "W2", "W1" } },
        new object[] { "active", "members_desc", null!, null!, new[] { "W5", "W7", "W3", "W2", "W1", "W8", "W6" } },
        new object[] { "all", "unreplied_oldest", null!, null!, new[] { "W1", "W2", "W4", "W6", "W5", "W8", "W3", "W7" } },
        new object[] { "all", "created_desc", "DISK", null!, new[] { "W8", "W7", "W6", "W4", "W3", "W2", "W1" } },
        new object[] { "all", "created_desc", null!, new long[] { 2, 3 }, new[] { "W6", "W5", "W4", "W3" } },
        new object[] { "all", "created_desc", null!, Array.Empty<long>(), Array.Empty<string>() },
    };

    [Theory]
    [MemberData(nameof(OrderQueries))]
    public void QueryOrders_EF與替身同資料結果一致(string status, string sort, string? source, long[]? handlers, string[] expected)
    {
        var efCases = new EfIssueCaseStore(_fx.NewContext);
        var ef = Store();
        SeedOrders(ef, efCases, 0);
        var fakeCases = new FakeIssueCaseStore();
        var fake = new FakeWorkOrderStore(fakeCases);
        SeedOrders(fake, fakeCases, 0);

        var q = new WorkOrderQuery { Status = status, Sort = sort, Source = source, HandlerIds = handlers, Page = 1, PageSize = 100 };

        Assert.Equal(expected, Labels(ef, q));
        Assert.Equal(expected, Labels(fake, q));
        Assert.Equal(expected.Length, ef.QueryOrders(q).Total);
        Assert.Equal(expected.Length, fake.QueryOrders(q).Total);
    }

    [Fact]
    public void QueryOrders_分頁穩定_同建立時間依單號降冪_Total為全部筆數()
    {
        var efCases = new EfIssueCaseStore(_fx.NewContext);
        var ef = Store();
        SeedOrders(ef, efCases, 0);
        var fakeCases = new FakeIssueCaseStore();
        var fake = new FakeWorkOrderStore(fakeCases);
        SeedOrders(fake, fakeCases, 0);

        foreach (var store in new IWorkOrderStore[] { ef, fake })
        {
            var pages = Enumerable.Range(1, 3)
                .Select(p => store.QueryOrders(new WorkOrderQuery { Status = "all", Page = p, PageSize = 3 }))
                .ToList();
            Assert.All(pages, p => Assert.Equal(8, p.Total));
            Assert.Equal(new[] { "W8", "W7", "W6", "W5", "W4", "W3", "W2", "W1" },
                pages.SelectMany(p => p.Items).Select(o => o.IssueLabel));
        }
    }

    [Fact]
    public void QueryOrders_不合法條件擲ArgumentException()
    {
        var store = Store();
        Assert.Throws<ArgumentException>(() => store.QueryOrders(new WorkOrderQuery { Status = "bogus" }));
        Assert.Throws<ArgumentException>(() => store.QueryOrders(new WorkOrderQuery { Sort = "bogus" }));
        Assert.Throws<ArgumentException>(() => store.QueryOrders(new WorkOrderQuery { PageSize = 101 }));
    }

    [Fact]
    public void QueryOrders_命令數固定_不隨單數與成員數增長()
    {
        var store = CountingStore();
        var cases = new EfIssueCaseStore(_fx.NewContext);
        SeedOrders(store, cases, 0);

        _counter.Readers = 0;
        store.QueryOrders(new WorkOrderQuery { Status = "overdue", Sort = "members_desc", PageSize = 100 });
        var small = _counter.Readers;

        SeedOrders(store, new EfIssueCaseStoreWithSuffix(_fx.NewContext, "x"), 100);
        SeedOrders(store, new EfIssueCaseStoreWithSuffix(_fx.NewContext, "y"), 200);
        _counter.Readers = 0;
        store.QueryOrders(new WorkOrderQuery { Status = "overdue", Sort = "members_desc", PageSize = 100 });

        Assert.Equal(2, small);
        Assert.Equal(small, _counter.Readers);
    }

    /// <summary>重複灌資料時讓案件編號不撞：委派 EF 版，只改寫 CaseId</summary>
    private sealed class EfIssueCaseStoreWithSuffix : IIssueCaseStore
    {
        private readonly EfIssueCaseStore _inner;
        private readonly string _suffix;
        public EfIssueCaseStoreWithSuffix(Func<LfDbContext> factory, string suffix) { _inner = new EfIssueCaseStore(factory); _suffix = suffix; }
        public void Save(IssueCase issueCase) { issueCase.CaseId += _suffix; _inner.Save(issueCase); }
        public IssueCase? GetOpen(string hostName, string issueKey) => throw new NotSupportedException();
        public List<(string HostNameKey, string IssueKey)> GetOpenKeys() => throw new NotSupportedException();
        public List<IssueCase> GetOpenForHost(string hostName) => throw new NotSupportedException();
        public List<IssueCase> GetMany(IEnumerable<string> hostNames) => throw new NotSupportedException();
        public List<IssueCase> GetOpenByHandler(long userId) => throw new NotSupportedException();
        public List<IssueCase> GetByHandler(long userId) => throw new NotSupportedException();
        public List<IssueCase> GetResolvedSince(DateTime since) => throw new NotSupportedException();
        public IssueCase? Get(string caseId) => throw new NotSupportedException();
        public void SaveMany(IEnumerable<IssueCase> cases) => throw new NotSupportedException();
        public List<IssueCase> GetOpenByIssue(string source, int eventId) => throw new NotSupportedException();
        public List<IssueCase> GetOpenMany(IEnumerable<string> hostNames, string source, int eventId) => throw new NotSupportedException();
        public List<IssueCase> GetByWorkOrder(long workOrderId, int skip, int take) => throw new NotSupportedException();
        public int CountByWorkOrder(long workOrderId) => throw new NotSupportedException();
        public (List<IssueCase> Items, int Total) QueryMembers(WorkOrderMemberQuery q) => throw new NotSupportedException();
        public List<IssueCase> GetDaySyncPending(int take) => throw new NotSupportedException();
        public int CountDaySyncPending() => throw new NotSupportedException();
        public bool ClearDaySyncPendingIfUnchanged(string caseId, CaseDayIntent intent) => throw new NotSupportedException();
    }

    [Fact]
    public void CountMembers擴充欄位_EF與替身一致_單一查詢()
    {
        var today = DateTime.Today;
        var efCases = new EfIssueCaseStore(_fx.NewContext);
        var ef = CountingStore();
        var fakeCases = new FakeIssueCaseStore();
        var fake = new FakeWorkOrderStore(fakeCases);

        foreach (var (orders, cases) in new (IWorkOrderStore, IIssueCaseStore)[] { (ef, efCases), (fake, fakeCases) })
        {
            var id = orders.Insert(NewOrder(1));
            void M(string caseId, string status, DateTime? due = null, DateTime? closed = null, bool pending = false) =>
                cases.Save(new IssueCase
                {
                    CaseId = caseId, HostName = caseId, IssueKey = "System|Disk|153|2", IssueLabel = "Disk 153",
                    Status = status, HandlerId = 1, DueDate = due, FirstLinkedDate = Base, LastLinkedDate = Base,
                    CreatedAt = Base, CreatedByAccount = "admin", UpdatedAt = Base, ClosedAt = closed,
                    WorkOrderId = id, DaySyncPending = pending
                });
            M("a", IssueHandlingStatuses.InProgress, due: today.AddDays(-1));   // 逾期
            M("b", IssueHandlingStatuses.InProgress, pending: true);
            M("c", IssueHandlingStatuses.Observing, due: today);                // 期限今天不算逾期
            M("d", IssueHandlingStatuses.Escalated, due: today.AddDays(-5));    // escalated 不算逾期
            M("e", IssueHandlingStatuses.Open);
            M("f", IssueHandlingStatuses.Resolved, due: today.AddDays(-9), closed: Base);

            _counter.Readers = 0;
            var c = orders.CountMembers(new[] { id })[id];

            Assert.Equal(6, c.Total);
            Assert.Equal(5, c.Active);
            Assert.Equal(1, c.Closed);
            Assert.Equal(2, c.InProgress);
            Assert.Equal(1, c.Observing);
            Assert.Equal(1, c.Open);
            Assert.Equal(1, c.Escalated);
            Assert.Equal(1, c.Overdue);
            Assert.Equal(1, c.DaySyncPending);
        }
        Assert.Equal(1, CountOnce(ef));
    }

    private int CountOnce(IWorkOrderStore store)
    {
        _counter.Readers = 0;
        store.CountMembers(new long[] { 1, 2, 3 });
        return _counter.Readers;
    }

    [Fact]
    public void LoadBoard擴充欄位_逾期與近7日結案_EF與替身一致_單一查詢()
    {
        var today = DateTime.Today;
        var efCases = new EfIssueCaseStore(_fx.NewContext);
        var ef = CountingStore();
        var fakeCases = new FakeIssueCaseStore();
        var fake = new FakeWorkOrderStore(fakeCases);
        var boards = new List<Dictionary<long, HandlerLoad>>();

        foreach (var (orders, cases) in new (IWorkOrderStore, IIssueCaseStore)[] { (ef, efCases), (fake, fakeCases) })
        {
            // 處理人 1：一張進行中單（2 件逾期、1 件期限今天、1 件 escalated 過期不算）＋一張 3 天前結案
            var active = NewOrder(1, "A", 1); active.CreatedAt = new DateTime(2026, 8, 3);
            var activeId = orders.Insert(active);
            var recent = NewOrder(1, "B", 2); recent.ClosedAt = today.AddDays(-3);
            orders.Insert(recent);
            // 處理人 2：只有近 7 日結案單（今天−7 算、今天−8 不算）
            var edge = NewOrder(2, "A", 1); edge.ClosedAt = today.AddDays(-7);
            orders.Insert(edge);
            var old = NewOrder(2, "B", 2); old.ClosedAt = today.AddDays(-8);
            orders.Insert(old);
            // 處理人 3：只有 8 天前結案單→不列
            var gone = NewOrder(3, "A", 1); gone.ClosedAt = today.AddDays(-8).AddHours(-1);
            orders.Insert(gone);

            void M(string caseId, string status, DateTime? due) =>
                cases.Save(new IssueCase
                {
                    CaseId = caseId, HostName = caseId, IssueKey = "System|A|1|2", IssueLabel = "A 1",
                    Status = status, HandlerId = 1, DueDate = due, FirstLinkedDate = Base, LastLinkedDate = Base,
                    CreatedAt = Base, CreatedByAccount = "admin", UpdatedAt = Base, WorkOrderId = activeId
                });
            M("m1", IssueHandlingStatuses.InProgress, today.AddDays(-1));
            M("m2", IssueHandlingStatuses.Observing, today.AddDays(-3));
            M("m3", IssueHandlingStatuses.InProgress, today);
            M("m4", IssueHandlingStatuses.Escalated, today.AddDays(-5));

            _counter.Readers = 0;
            boards.Add(orders.LoadBoard().ToDictionary(l => l.HandlerId));
            if (ReferenceEquals(orders, ef)) Assert.Equal(1, _counter.Readers);
        }

        foreach (var board in boards)
        {
            Assert.Equal(new long[] { 1, 2 }, board.Keys.OrderBy(k => k));
            Assert.Equal(1, board[1].ActiveWorkOrders);
            Assert.Equal(4, board[1].ActiveMembers);
            Assert.Equal(2, board[1].OverdueMembers);
            Assert.Equal(1, board[1].ClosedLast7Days);
            Assert.Equal(1, board[1].UnrepliedWorkOrders);
            Assert.Equal(new DateTime(2026, 8, 3), board[1].OldestActiveCreatedAt);
            Assert.Equal(0, board[2].ActiveWorkOrders);
            Assert.Equal(0, board[2].ActiveMembers);
            Assert.Equal(0, board[2].UnrepliedWorkOrders);
            Assert.Equal(1, board[2].ClosedLast7Days);
            Assert.Null(board[2].OldestActiveCreatedAt);
        }
    }
}
