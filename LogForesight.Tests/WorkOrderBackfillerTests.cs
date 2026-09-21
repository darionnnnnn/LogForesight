using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="WorkOrderBackfiller"/>：既有案件的 source 欄解析（階段一）與整併成交辦單（階段二）。
/// 案件一律直接寫列、source_key 留 null，模擬本功能上線前的既有資料。
/// </summary>
public class WorkOrderBackfillerTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private EfWorkOrderStore Orders() => new(_fx.NewContext);

    private WorkOrderBackfiller Backfiller() => new(Orders());

    private static readonly DateTime Base = new(2026, 9, 1, 9, 0, 0);

    private void AddLegacyCase(string caseId, string host, string issueKey, long? handler,
        DateTime? closedAt = null, DateTime? createdAt = null)
    {
        using var ctx = _fx.NewContext();
        ctx.IssueCases.Add(new IssueCaseRow
        {
            CaseId = caseId, HostName = host, HostNameKey = host.ToUpperInvariant(), IssueKey = issueKey,
            IssueLabel = "label-" + issueKey, Status = IssueHandlingStatuses.InProgress, HandlerId = handler,
            CreatedAt = createdAt ?? Base, CreatedByAccount = "admin", UpdatedAt = Base, ClosedAt = closedAt
        });
        ctx.SaveChanges();
    }

    private IssueCaseRow CaseRow(string caseId)
    {
        using var ctx = _fx.NewContext();
        return ctx.IssueCases.Single(c => c.CaseId == caseId);
    }

    private int OrderCount()
    {
        using var ctx = _fx.NewContext();
        return ctx.WorkOrders.Count();
    }

    private List<WorkOrderEventRow> AllEvents()
    {
        using var ctx = _fx.NewContext();
        return ctx.WorkOrderEvents.ToList();
    }

    private const string DiskKey = "System|Disk|153|2";
    private const string DcomKey = "System|DCOM|10016|1";

    [Fact]
    public void 兩位處理人乘兩個問題乘三台_建四張單並全部連結()
    {
        var i = 0;
        foreach (var handler in new long[] { 1, 2 })
        foreach (var key in new[] { DiskKey, DcomKey })
        for (var h = 0; h < 3; h++)
            AddLegacyCase("c" + i++, $"SRV-{handler}-{h}", key, handler, createdAt: Base.AddHours(i));

        Backfiller().Run(CancellationToken.None);

        using var ctx = _fx.NewContext();
        var orders = ctx.WorkOrders.ToList();
        Assert.Equal(4, orders.Count);
        Assert.All(orders, o =>
        {
            Assert.Equal(WorkOrderOrigins.Backfill, o.Origin);
            Assert.Equal(WorkOrderScopes.Hosts, o.ScopeKind);
            Assert.Equal("", o.ScopeGroupIds);
            Assert.False(o.AutoAttach);
            Assert.Null(o.CreatedById);
            Assert.Equal(AuditActions.SystemAccount, o.CreatedByAccount);
            Assert.Null(o.ClosedAt);
        });
        Assert.Equal(12, ctx.IssueCases.Count(c => c.WorkOrderId != null));
        // 案件 updated_at 不動
        Assert.All(ctx.IssueCases.ToList(), c => Assert.Equal(Base, c.UpdatedAt));

        var events = ctx.WorkOrderEvents.ToList();
        Assert.Equal(4, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(WorkOrderEventActions.Created, e.Action);
            Assert.Equal(3, e.MemberDelta);
            Assert.Equal("既有案件整併", e.Note);
        });

        // 每張單的成員就是同處理人同問題的三件，CreatedAt 取最早
        foreach (var order in orders)
        {
            var members = ctx.IssueCases.Where(c => c.WorkOrderId == order.WorkOrderId).ToList();
            Assert.Equal(3, members.Count);
            Assert.All(members, m => Assert.Equal(order.HandlerId, m.HandlerId));
            Assert.All(members, m => Assert.Equal(order.SourceKey, m.SourceKey));
            Assert.Equal(members.Min(m => m.CreatedAt), order.CreatedAt);
        }
        var dcom = orders.First(o => o.SourceKey == "DCOM");
        Assert.Equal("DCOM", dcom.SourceName);
        Assert.Equal(10016, dcom.EventId);
        Assert.Equal(0, Backfiller().CountPending());
    }

    [Fact]
    public void 重跑零新增()
    {
        for (var h = 0; h < 3; h++) AddLegacyCase("c" + h, "SRV-" + h, DiskKey, 1);
        Backfiller().Run(CancellationToken.None);
        var orders = OrderCount();
        var events = AllEvents().Count;

        Backfiller().Run(CancellationToken.None);

        Assert.Equal(orders, OrderCount());
        Assert.Equal(events, AllEvents().Count);
    }

    [Fact]
    public void 已結案案件補source_key但不連結()
    {
        AddLegacyCase("closed", "SRV-1", DiskKey, 1, closedAt: Base.AddDays(1));

        Backfiller().Run(CancellationToken.None);

        var row = CaseRow("closed");
        Assert.Equal("DISK", row.SourceKey);
        Assert.Equal("Disk", row.SourceName);
        Assert.Equal(153, row.EventId);
        Assert.Null(row.WorkOrderId);
        Assert.Equal(0, OrderCount());
    }

    [Fact]
    public void 無處理人的進行中案件不連結()
    {
        AddLegacyCase("nohandler", "SRV-1", DiskKey, null);

        var backfiller = Backfiller();
        backfiller.Run(CancellationToken.None);

        Assert.Null(CaseRow("nohandler").WorkOrderId);
        Assert.Equal("DISK", CaseRow("nohandler").SourceKey);
        Assert.Equal(0, OrderCount());
        Assert.Equal(0, backfiller.CountPending());
        Assert.True(backfiller.Progress.Completed);
    }

    [Fact]
    public void 解析失敗寫空字串且重跑不再被撈()
    {
        AddLegacyCase("bad", "SRV-1", "abc", 1);
        var backfiller = Backfiller();
        Assert.Equal(1, backfiller.CountPending());

        backfiller.Run(CancellationToken.None);

        var row = CaseRow("bad");
        Assert.Equal("", row.SourceKey);
        Assert.Null(row.SourceName);
        Assert.Null(row.EventId);
        Assert.Null(row.WorkOrderId);
        Assert.Equal(0, backfiller.CountPending());
        Assert.Equal(0, OrderCount());
    }

    [Fact]
    public void 已存在同鍵進行中單時併入()
    {
        var store = Orders();
        var existingId = store.Insert(new WorkOrder
        {
            SourceName = "disk", EventId = 153, IssueLabel = "disk 153", HandlerId = 1,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts,
            CreatedByAccount = "admin", CreatedAt = Base
        });
        AddLegacyCase("c1", "SRV-1", DiskKey, 1);
        AddLegacyCase("c2", "SRV-2", DiskKey, 1);

        Backfiller().Run(CancellationToken.None);

        Assert.Equal(1, OrderCount());
        Assert.Equal(existingId, CaseRow("c1").WorkOrderId);
        Assert.Equal(existingId, CaseRow("c2").WorkOrderId);
        var evt = Assert.Single(AllEvents());
        Assert.Equal(WorkOrderEventActions.MergedIn, evt.Action);
        Assert.Equal(2, evt.MemberDelta);
        Assert.Equal("既有案件整併", evt.Note);
    }

    /// <summary>直接寫一行處理歷程；lf_log_lines.created_at 與歷程時間一致（可控，不吃機器時鐘）。
    /// lineCreatedAt 傳 null 模擬 schema 升級前沒有時間戳記的舊列</summary>
    private void AppendLog(long actorId, string issueKey, string action, DateTime at, bool legacyNullCreatedAt = false)
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new RecordHandlingLog
        {
            HostName = "SRV-1", Date = at.Date, Status = "in_progress", ActorId = actorId, ActorAccount = "u" + actorId,
            Action = action, IssueKey = issueKey, IssueLabel = "x", CreatedAt = at
        }, LfJsonOptions.Compact);
        using var ctx = _fx.NewContext();
        ctx.LogLines.Add(new LogLineRow { LogKey = "handling_log", Line = line, CreatedAt = legacyNullCreatedAt ? null : at });
        ctx.SaveChanges();
    }

    [Fact]
    public void LastReplyAt只計案件建立之後的回覆()
    {
        AddLegacyCase("c1", "SRV-1", DiskKey, 1);
        AppendLog(1, DiskKey, HandlingActions.IssueStatus, Base.AddHours(-1));
        AppendLog(1, DiskKey, HandlingActions.IssueStatus, Base.AddHours(2));

        Backfiller().Run(CancellationToken.None);

        using var ctx = _fx.NewContext();
        Assert.Equal(Base.AddHours(2), Assert.Single(ctx.WorkOrders.ToList()).LastReplyAt);
    }

    [Fact]
    public void LastReplyAt_處理歷程SourceUnicode大小寫異體仍命中但EventKey仍區分()
    {
        var candidateKey = IssueSignatureKey.For("System", "ſ", 153, System.Diagnostics.EventLogEntryType.Error);
        var logKey = IssueSignatureKey.For("System", "S", 153, System.Diagnostics.EventLogEntryType.Error);
        AddLegacyCase("unicode", "SRV-UNICODE", candidateKey, 1);
        AppendLog(1, logKey, HandlingActions.IssueStatus, Base.AddHours(2));
        AppendLog(1, IssueSignatureKey.For("System", "S", 153, System.Diagnostics.EventLogEntryType.Error, "other-rule"),
            HandlingActions.IssueStatus, Base.AddHours(3));

        Backfiller().Run(CancellationToken.None);

        using var ctx = _fx.NewContext();
        var order = Assert.Single(ctx.WorkOrders.ToList());
        Assert.Equal(Base.AddHours(2), order.LastReplyAt);
    }

    /// <summary>起點由全體最早案件決定；較晚建立的組仍要逐筆比自己組的建立時間</summary>
    [Fact]
    public void LastReplyAt以各組自己的最早建立時間為界()
    {
        AddLegacyCase("disk", "SRV-1", DiskKey, 1, createdAt: Base);
        AddLegacyCase("dcom", "SRV-1", DcomKey, 1, createdAt: Base.AddDays(5));
        AppendLog(1, DcomKey, HandlingActions.IssueStatus, Base.AddDays(1));   // 晚於全體起點、早於 dcom 組建立
        AppendLog(1, DiskKey, HandlingActions.IssueStatus, Base.AddDays(2));

        Backfiller().Run(CancellationToken.None);

        using var ctx = _fx.NewContext();
        Assert.Null(ctx.WorkOrders.Single(o => o.SourceKey == "DCOM").LastReplyAt);
        Assert.Equal(Base.AddDays(2), ctx.WorkOrders.Single(o => o.SourceKey == "DISK").LastReplyAt);
    }

    [Fact]
    public void LastReplyAt只有早於案件建立的回覆時為null()
    {
        AddLegacyCase("c1", "SRV-1", DiskKey, 1);
        AppendLog(1, DiskKey, HandlingActions.IssueStatus, Base.AddHours(-1));

        var backfiller = Backfiller();
        backfiller.Run(CancellationToken.None);

        using var ctx = _fx.NewContext();
        Assert.Null(Assert.Single(ctx.WorkOrders.ToList()).LastReplyAt);
        Assert.Equal(0, backfiller.ScannedLogLines);   // 沒有夠新的列＝查不到起點、不掃
    }

    [Fact]
    public void 處理歷程從起點seq開始掃_起點之前的列不讀()
    {
        AddLegacyCase("c1", "SRV-1", DiskKey, 1);
        for (var i = 0; i < 20; i++) AppendLog(1, DiskKey, HandlingActions.IssueStatus, Base.AddDays(-30), legacyNullCreatedAt: true);
        for (var i = 0; i < 50; i++) AppendLog(1, DiskKey, HandlingActions.IssueStatus, Base.AddDays(-10));
        AppendLog(2, DiskKey, HandlingActions.IssueStatus, Base.AddHours(1));
        AppendLog(1, DiskKey, HandlingActions.CaseSync, Base.AddHours(3));

        var backfiller = Backfiller();
        backfiller.Run(CancellationToken.None);

        Assert.Equal(2, backfiller.ScannedLogLines);
        using var ctx = _fx.NewContext();
        Assert.Equal(Base.AddHours(3), Assert.Single(ctx.WorkOrders.ToList()).LastReplyAt);
    }

    [Fact]
    public void 每組交易原子性_寫事件前失敗零殘留且重跑正常()
    {
        AddLegacyCase("c1", "SRV-1", DiskKey, 1);
        AddLegacyCase("c2", "SRV-2", DiskKey, 1);

        var failing = Backfiller();
        failing.BeforeEventWriteForTest = () => throw new InvalidOperationException("模擬中斷");
        Assert.Throws<InvalidOperationException>(() => failing.Run(CancellationToken.None));

        Assert.Equal(0, OrderCount());
        Assert.Empty(AllEvents());
        Assert.Null(CaseRow("c1").WorkOrderId);
        Assert.Null(CaseRow("c2").WorkOrderId);

        Backfiller().Run(CancellationToken.None);

        Assert.Equal(1, OrderCount());
        var evt = Assert.Single(AllEvents());
        Assert.Equal(WorkOrderEventActions.Created, evt.Action);
        Assert.Equal(2, evt.MemberDelta);
        Assert.NotNull(CaseRow("c1").WorkOrderId);
        Assert.Equal(CaseRow("c1").WorkOrderId, CaseRow("c2").WorkOrderId);
    }

    [Fact]
    public void LastReplyAt取處理人本人對本組鍵的最大歷程時間()
    {
        AddLegacyCase("c1", "SRV-1", DiskKey, 1);
        AddLegacyCase("c2", "SRV-2", "System|Disk|153|1", 1);   // 同 (Disk,153) 不同 entry type 的鍵
        AppendLog(1, DiskKey, HandlingActions.IssueStatus, Base.AddHours(1));
        AppendLog(1, "System|Disk|153|1", HandlingActions.CaseSync, Base.AddHours(3));
        AppendLog(1, DiskKey, HandlingActions.Assign, Base.AddHours(9));        // 動作不計
        AppendLog(2, DiskKey, HandlingActions.IssueStatus, Base.AddHours(8));   // 別人不計
        AppendLog(1, DcomKey, HandlingActions.IssueStatus, Base.AddHours(7));   // 別的問題不計

        Backfiller().Run(CancellationToken.None);

        using var ctx = _fx.NewContext();
        var order = Assert.Single(ctx.WorkOrders.ToList());
        Assert.Equal(Base.AddHours(3), order.LastReplyAt);
    }

    [Fact]
    public void LastReplyAt只有別人的歷程時為null()
    {
        AddLegacyCase("c1", "SRV-1", DiskKey, 1);
        AppendLog(2, DiskKey, HandlingActions.IssueStatus, Base.AddHours(1));

        Backfiller().Run(CancellationToken.None);

        using var ctx = _fx.NewContext();
        Assert.Null(Assert.Single(ctx.WorkOrders.ToList()).LastReplyAt);
    }
}
