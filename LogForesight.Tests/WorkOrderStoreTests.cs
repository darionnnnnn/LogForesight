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
}
