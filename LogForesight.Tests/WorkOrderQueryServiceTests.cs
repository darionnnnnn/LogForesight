using System.Diagnostics;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 交辦單查詢服務（<see cref="WorkOrderQueryService"/>）：逐單授權、成員可見範圍過濾、清單篩選與分頁、
/// 計數、時間軸、未回覆天數。store 用記憶體替身；時間軸走真的 <see cref="WorkOrderCoordinator"/>。
/// </summary>
public class WorkOrderQueryServiceTests
{
    private const string Source = "disk";
    private const int EventId = 153;
    private static readonly string IssueKey = IssueSignatureKey.For("System", Source, EventId, EventLogEntryType.Error);
    private static readonly DateTime D2 = DateTime.Today.AddDays(-2);

    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly WorkOrderCoordinator _coordinator;

    private readonly WebUser _alice;
    private readonly WebUser _bob;
    private readonly WebUser _stranger;

    public WorkOrderQueryServiceTests()
    {
        _orders = new FakeWorkOrderStore(_cases);
        var caseCoordinator = new IssueCaseCoordinator(_cases, _issueHandlings, _handlingLog, _records, _hosts, new FakeIssueOwnerStore());
        _coordinator = new WorkOrderCoordinator(_orders, _cases, _issueHandlings, caseCoordinator, _handlingLog, _hosts);

        _alice = _users.Upsert(new WebUser { Account = "DOMAIN\\alice", DisplayName = "愛麗絲" });
        _bob = _users.Upsert(new WebUser { Account = "DOMAIN\\bob", DisplayName = "鮑伯" });
        _stranger = _users.Upsert(new WebUser { Account = "DOMAIN\\x", DisplayName = "路人" });
    }

    private WorkOrderQueryService Service(ICurrentUser user, params long[] visibleHostIds) => new(
        _orders, _cases, _users, _hosts, _hostGroups, new FakeRuleStore(), new ScopedVisibility(visibleHostIds), user,
        new UserDisplayNameService(_settings));

    private static ICurrentUser As(long userId, params Capability[] caps) => FakeCurrentUser.ForUser(userId, caps);

    private long AddOrder(long handler, string source = Source, int eventId = EventId, DateTime? createdAt = null,
        DateTime? replied = null, DateTime? closed = null) =>
        _orders.Insert(new WorkOrder
        {
            SourceName = source, EventId = eventId, IssueLabel = $"{source} {eventId}", HandlerId = handler,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, CreatedByAccount = "admin",
            CreatedAt = createdAt ?? DateTime.Today.AddHours(-1), LastReplyAt = replied, ClosedAt = closed
        });

    private IssueCase AddMember(long orderId, string host, string status, DateTime? due = null, DateTime? closed = null, bool pending = false)
    {
        if (_hosts.FindByName(host) == null) _hosts.Upsert(new WebHost { HostName = host });
        var c = new IssueCase
        {
            CaseId = $"{orderId}-{host}", HostName = host, IssueKey = IssueKey, IssueLabel = "disk 153", Status = status,
            HandlerId = _orders.Get(orderId)!.HandlerId, DueDate = due, FirstLinkedDate = D2, LastLinkedDate = D2,
            CreatedAt = D2, CreatedByAccount = "admin", UpdatedAt = D2, ClosedAt = closed, WorkOrderId = orderId,
            DaySyncPending = pending
        };
        _cases.Save(c);
        return c;
    }

    private static List<long> Ids(WorkOrderListDto dto) => dto.Items.Select(i => i.WorkOrderId).ToList();

    // ── 授權 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 授權_Assign看得到任一單()
    {
        var id = AddOrder(_alice.UserId);
        var dto = Service(As(_stranger.UserId, Capability.Assign)).Get(id);
        Assert.Equal(id, dto.WorkOrderId);
        Assert.True(dto.ViewerCanAssign);
        Assert.False(dto.ViewerIsHandler);
    }

    [Fact]
    public void 授權_ViewAll看得到任一單()
    {
        var id = AddOrder(_alice.UserId);
        var svc = Service(As(_stranger.UserId, Capability.ViewAll, Capability.Handle));
        Assert.Equal(id, svc.Get(id).WorkOrderId);
        Assert.NotNull(svc.Timeline(id));
        Assert.False(svc.Get(id).ViewerCanAssign);
    }

    [Fact]
    public void 授權_處理人看得到自己的單_看不到別人的單()
    {
        var mine = AddOrder(_alice.UserId);
        var others = AddOrder(_bob.UserId);
        var svc = Service(As(_alice.UserId, Capability.Handle));

        var dto = svc.Get(mine);
        Assert.True(dto.ViewerIsHandler);
        Assert.Equal(0, svc.Members(mine, "all", 1, 50).HiddenMemberCount);

        var ex = Assert.Throws<DomainException>(() => svc.Get(others));
        Assert.Equal(ApiErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public void 授權_一般使用者非處理人_詳情成員時間軸都403()
    {
        var id = AddOrder(_alice.UserId);
        var svc = Service(As(_stranger.UserId, Capability.Handle));

        Assert.Equal(ApiErrorCodes.Forbidden, Assert.Throws<DomainException>(() => svc.Get(id)).Code);
        Assert.Equal(ApiErrorCodes.Forbidden, Assert.Throws<DomainException>(() => svc.Members(id, "all", 1, 50)).Code);
        Assert.Equal(ApiErrorCodes.Forbidden, Assert.Throws<DomainException>(() => svc.Timeline(id)).Code);
    }

    [Fact]
    public void 授權_不存在404()
    {
        var svc = Service(As(_stranger.UserId, Capability.Assign));
        Assert.Equal(ApiErrorCodes.NotFound, Assert.Throws<DomainException>(() => svc.Get(999)).Code);
    }

    // ── 成員可見範圍 ─────────────────────────────────────────────────────────

    [Fact]
    public void 成員_Assign非ViewAll非處理人只看可見主機_回報隱藏數()
    {
        var id = AddOrder(_alice.UserId);
        AddMember(id, "H1", IssueHandlingStatuses.InProgress);
        AddMember(id, "H2", IssueHandlingStatuses.InProgress);
        AddMember(id, "H3", IssueHandlingStatuses.Resolved, closed: D2);
        var visible = _hosts.FindByName("H2")!.HostId;

        var page = Service(As(_stranger.UserId, Capability.Assign), visible).Members(id, "all", 1, 50);

        Assert.Equal(new[] { "H2" }, page.Items.Select(i => i.HostName));
        Assert.Equal(visible, page.Items[0].HostId);
        Assert.Equal(1, page.Total);
        Assert.Equal(2, page.HiddenMemberCount);

        var active = Service(As(_stranger.UserId, Capability.Assign), visible).Members(id, "active", 1, 50);
        Assert.Equal(1, active.HiddenMemberCount);
    }

    [Fact]
    public void 成員_ViewAll與處理人看全部_隱藏數為0()
    {
        var id = AddOrder(_alice.UserId);
        AddMember(id, "H1", IssueHandlingStatuses.InProgress);
        AddMember(id, "H2", IssueHandlingStatuses.InProgress);

        var viewAll = Service(As(_stranger.UserId, Capability.ViewAll)).Members(id, "all", 1, 50);
        var handler = Service(As(_alice.UserId, Capability.Handle)).Members(id, "all", 1, 50);

        Assert.Equal((2, 0), (viewAll.Total, viewAll.HiddenMemberCount));
        Assert.Equal((2, 0), (handler.Total, handler.HiddenMemberCount));
    }

    [Fact]
    public void 成員_主機已不存在HostId為null_逾期旗標()
    {
        var id = AddOrder(_alice.UserId);
        var c = new IssueCase
        {
            CaseId = "gone", HostName = "GONE", IssueKey = IssueKey, IssueLabel = "disk 153",
            Status = IssueHandlingStatuses.InProgress, HandlerId = _alice.UserId, DueDate = DateTime.Today.AddDays(-1),
            FirstLinkedDate = D2, LastLinkedDate = D2, CreatedAt = D2, CreatedByAccount = "admin", UpdatedAt = D2,
            WorkOrderId = id
        };
        _cases.Save(c);   // 主機表沒有這台＝主機已不存在

        var item = Assert.Single(Service(As(_alice.UserId)).Members(id, "overdue", 1, 50).Items);

        Assert.Equal(c.CaseId, item.CaseId);
        Assert.Null(item.HostId);
        Assert.True(item.Overdue);
    }

    // ── 清單篩選 ────────────────────────────────────────────────────────────

    [Fact]
    public void 清單_escalated正反例()
    {
        var yes = AddOrder(_alice.UserId, eventId: 1);
        AddMember(yes, "H1", IssueHandlingStatuses.Escalated);
        var no = AddOrder(_alice.UserId, eventId: 2);
        AddMember(no, "H2", IssueHandlingStatuses.Escalated, closed: D2);
        AddMember(no, "H3", IssueHandlingStatuses.InProgress);

        Assert.Equal(new[] { yes }, Ids(Service(As(1, Capability.Assign)).List(new WorkOrderListRequest { Status = "escalated" })));
    }

    [Fact]
    public void 清單_overdue正反例()
    {
        var yes = AddOrder(_alice.UserId, eventId: 1);
        AddMember(yes, "H1", IssueHandlingStatuses.Observing, due: DateTime.Today.AddDays(-1));
        var no = AddOrder(_alice.UserId, eventId: 2);
        AddMember(no, "H2", IssueHandlingStatuses.InProgress, due: DateTime.Today);
        AddMember(no, "H3", IssueHandlingStatuses.Escalated, due: DateTime.Today.AddDays(-3));

        Assert.Equal(new[] { yes }, Ids(Service(As(1, Capability.Assign)).List(new WorkOrderListRequest { Status = "overdue" })));
    }

    [Fact]
    public void 清單_unreplied正反例()
    {
        var yes = AddOrder(_alice.UserId, eventId: 1);
        AddOrder(_alice.UserId, eventId: 2, replied: DateTime.Now);
        AddOrder(_alice.UserId, eventId: 3, closed: DateTime.Now);

        Assert.Equal(new[] { yes }, Ids(Service(As(1, Capability.Assign)).List(new WorkOrderListRequest { Status = "unreplied" })));
    }

    [Fact]
    public void 清單_closed正反例()
    {
        AddOrder(_alice.UserId, eventId: 1);
        var yes = AddOrder(_alice.UserId, eventId: 2, closed: DateTime.Now);

        Assert.Equal(new[] { yes }, Ids(Service(As(1, Capability.ViewAll)).List(new WorkOrderListRequest { Status = "closed" })));
    }

    [Fact]
    public void 清單_HandlerInactive只列停用者的單_與HandlerId取交集()
    {
        _bob.Active = false;
        _stranger.Active = false;
        AddOrder(_alice.UserId);
        var bobs = AddOrder(_bob.UserId);
        var strangers = AddOrder(_stranger.UserId);
        var svc = Service(As(1, Capability.Assign));

        Assert.Equal(new[] { strangers, bobs }, Ids(svc.List(new WorkOrderListRequest { HandlerInactive = true })));
        Assert.Equal(new[] { bobs }, Ids(svc.List(new WorkOrderListRequest { HandlerInactive = true, HandlerId = _bob.UserId })));
        Assert.Empty(Ids(svc.List(new WorkOrderListRequest { HandlerInactive = true, HandlerId = _alice.UserId })));
        var row = svc.List(new WorkOrderListRequest { HandlerId = _bob.UserId }).Items.Single();
        Assert.False(row.HandlerActive);
        Assert.Equal("鮑伯(DOMAIN\\bob)", row.HandlerName);
    }

    [Fact]
    public void 清單_Source大小寫不敏感()
    {
        var id = AddOrder(_alice.UserId, source: "Disk");
        AddOrder(_alice.UserId, source: "ntfs", eventId: 55);

        Assert.Equal(new[] { id }, Ids(Service(As(1, Capability.Assign)).List(new WorkOrderListRequest { Source = "DISK" })));
    }

    [Fact]
    public void 清單_分頁穩定_同建立時間依單號降冪()
    {
        var at = DateTime.Today.AddHours(-2);
        var ids = Enumerable.Range(1, 5).Select(i => AddOrder(_alice.UserId, eventId: i, createdAt: at)).ToList();
        var svc = Service(As(1, Capability.Assign));

        var p1 = svc.List(new WorkOrderListRequest { Page = 1, PageSize = 2 });
        var p2 = svc.List(new WorkOrderListRequest { Page = 2, PageSize = 2 });
        var p3 = svc.List(new WorkOrderListRequest { Page = 3, PageSize = 2 });

        Assert.Equal(5, p1.Total);
        Assert.Equal(ids.OrderByDescending(x => x), Ids(p1).Concat(Ids(p2)).Concat(Ids(p3)));
    }

    [Fact]
    public void 清單_不合法狀態回Validation_每頁上限100()
    {
        var svc = Service(As(1, Capability.Assign));
        Assert.Equal(ApiErrorCodes.ValidationFailed,
            Assert.Throws<DomainException>(() => svc.List(new WorkOrderListRequest { Status = "bogus" })).Code);
        Assert.Equal(100, svc.List(new WorkOrderListRequest { PageSize = 1000 }).PageSize);
    }

    [Fact]
    public void 清單_已刪除處理人顯示已刪除()
    {
        AddOrder(424242);
        var row = Service(As(1, Capability.Assign)).List(new WorkOrderListRequest()).Items.Single();
        Assert.Equal("（已刪除）", row.HandlerName);
        Assert.False(row.HandlerActive);
    }

    // ── 計數 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 計數_各欄正確()
    {
        var id = AddOrder(_alice.UserId);
        AddMember(id, "H1", IssueHandlingStatuses.InProgress, due: DateTime.Today.AddDays(-1));   // 逾期
        AddMember(id, "H2", IssueHandlingStatuses.InProgress, pending: true);                     // 待逐日同步
        AddMember(id, "H3", IssueHandlingStatuses.Observing);
        AddMember(id, "H4", IssueHandlingStatuses.Escalated);
        AddMember(id, "H5", IssueHandlingStatuses.Resolved, closed: D2);

        var c = Service(As(1, Capability.Assign)).Get(id).Counts;

        Assert.Equal(5, c.Total);
        Assert.Equal(4, c.Active);
        Assert.Equal(1, c.Closed);
        Assert.Equal(2, c.InProgress);
        Assert.Equal(1, c.Observing);
        Assert.Equal(1, c.Escalated);
        Assert.Equal(0, c.Open);
        Assert.Equal(1, c.Overdue);
        Assert.Equal(1, c.DaySyncPending);
    }

    // ── 詳情 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 詳情_範圍群組名稱_已刪除群組顯示已刪除()
    {
        var group = _hostGroups.Upsert(new HostGroup { GroupName = "機房A" });
        var order = _orders.Get(AddOrder(_alice.UserId))!;
        order.ScopeKind = WorkOrderScopes.Groups;
        order.ScopeGroupIds = new List<long> { group.GroupId, 777 };
        order.Note = "備註";
        _orders.Save(order);

        var dto = Service(As(1, Capability.Assign)).Get(order.WorkOrderId);

        Assert.Equal(new[] { "機房A", "（已刪除）" }, dto.ScopeGroups.Select(g => g.GroupName));
        Assert.Equal("備註", dto.Note);
        Assert.Equal("admin", dto.CreatedByAccount);
    }

    // ── 時間軸 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 時間軸_建立追加回覆改派依序_系統事件顯示系統()
    {
        foreach (var h in new[] { "H1", "H2" })
        {
            var host = _hosts.Upsert(new WebHost { HostName = h });
            _records.Add(new DailyAnalysisRecord
            {
                Date = D2, HostId = host.HostId, Host = h,
                TopIssues = new List<LogIssueSignature> { new() { LogName = "System", Source = Source, EventId = EventId, EntryType = EventLogEntryType.Error } }
            });
        }
        var t0 = DateTime.Today.AddHours(8);
        WorkOrderActor Boss(int hours) => new() { ActorId = _stranger.UserId, ActorAccount = "DOMAIN\\x", OccurredAt = t0.AddHours(hours) };
        WorkOrderMember M(string host) => new() { HostName = host, IssueKey = IssueKey, IssueLabel = "disk 153", TriggerDate = D2 };

        var id = _coordinator.Create(new WorkOrderCreateRequest
        {
            Source = Source, EventId = EventId, IssueLabel = "disk 153", HandlerId = _alice.UserId,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, ScopeGroupIds = new List<long>(),
            Members = new List<WorkOrderMember> { M("H1") }, Actor = Boss(0)
        }).WorkOrderId;
        _coordinator.Append(id, new List<WorkOrderMember> { M("H2") }, false, Boss(1));
        _coordinator.Reply(id, null, IssueHandlingStatuses.InProgress, "處理中", null,
            new WorkOrderActor { ActorId = _alice.UserId, ActorAccount = "DOMAIN\\alice", OccurredAt = t0.AddHours(2) });
        _coordinator.Reassign(id, _bob.UserId, Boss(3));
        _orders.AppendEvent(new WorkOrderEvent
        {
            WorkOrderId = id, Action = WorkOrderEventActions.Appended, ActorId = null, ActorAccount = "system",
            MemberDelta = 0, CreatedAt = t0.AddHours(4)
        });

        var timeline = Service(As(1, Capability.Assign)).Timeline(id);

        Assert.Equal(new[] { "建立", "追加主機", "已回覆", "改派", "追加主機" }, timeline.Select(e => e.ActionText));
        Assert.Equal("in_progress：處理中（2 台）", timeline[2].Note);
        Assert.Equal("愛麗絲(DOMAIN\\alice)", timeline[2].ActorName);
        Assert.Equal("路人(DOMAIN\\x)", timeline[0].ActorName);
        Assert.Equal("系統", timeline[4].ActorName);
        Assert.Equal(timeline.Select(e => e.CreatedAt).OrderBy(x => x), timeline.Select(e => e.CreatedAt));
    }

    // ── 未回覆天數 ───────────────────────────────────────────────────────────

    [Fact]
    public void UnrepliedDays_建立3天前未回覆為3_已回覆為null()
    {
        var unreplied = AddOrder(_alice.UserId, eventId: 1, createdAt: DateTime.Today.AddDays(-3).AddHours(15));
        var replied = AddOrder(_alice.UserId, eventId: 2, createdAt: DateTime.Today.AddDays(-3), replied: DateTime.Today.AddDays(-1));
        var svc = Service(As(1, Capability.Assign));

        Assert.Equal(3, svc.Get(unreplied).UnrepliedDays);
        Assert.Null(svc.Get(replied).UnrepliedDays);
    }

    // ── 處理人清單／摘要／徽章（task-47-D1）────────────────────────────────────

    [Fact]
    public void ListForHandler_本人可看_處理人固定為本人_忽略請求帶的HandlerId()
    {
        var mine = AddOrder(_alice.UserId, eventId: 1);
        AddOrder(_bob.UserId, eventId: 2);
        var svc = Service(As(_alice.UserId, Capability.Handle));

        var dto = svc.ListForHandler(_alice.UserId, new WorkOrderListRequest { HandlerId = _bob.UserId });

        Assert.Equal(new[] { mine }, Ids(dto));
        Assert.Equal(1, dto.Total);
    }

    [Theory]
    [InlineData(Capability.Assign)]
    [InlineData(Capability.ViewAll)]
    public void ListForHandler與HandlerSummary_Assign或ViewAll可看他人(Capability capability)
    {
        var bobs = AddOrder(_bob.UserId);
        AddMember(bobs, "H1", IssueHandlingStatuses.InProgress);
        var svc = Service(As(_stranger.UserId, capability));

        Assert.Equal(new[] { bobs }, Ids(svc.ListForHandler(_bob.UserId, new WorkOrderListRequest())));
        Assert.Equal(1, svc.HandlerSummary(_bob.UserId).ActiveMembers);
    }

    [Fact]
    public void ListForHandler與HandlerSummary_他人無Assign或ViewAll_403()
    {
        AddOrder(_bob.UserId);
        var svc = Service(As(_alice.UserId, Capability.Handle));

        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => svc.ListForHandler(_bob.UserId, new WorkOrderListRequest())).Code);
        Assert.Equal(ApiErrorCodes.Forbidden, Assert.Throws<DomainException>(() => svc.HandlerSummary(_bob.UserId)).Code);
    }

    [Fact]
    public void MyBadge與HandlerSummary數字一致()
    {
        var a = AddOrder(_alice.UserId, eventId: 1);
        var b = AddOrder(_alice.UserId, eventId: 2, replied: DateTime.Today);
        AddMember(a, "H1", IssueHandlingStatuses.InProgress, due: DateTime.Today.AddDays(-1));
        AddMember(a, "H2", IssueHandlingStatuses.Observing, due: DateTime.Today.AddDays(3));
        AddMember(b, "H3", IssueHandlingStatuses.Escalated);
        AddMember(b, "H4", IssueHandlingStatuses.Resolved, closed: DateTime.Today);
        var svc = Service(As(_alice.UserId, Capability.Handle));

        var badge = svc.MyBadge();
        var summary = svc.HandlerSummary(_alice.UserId);

        Assert.Equal(2, badge.ActiveWorkOrders);
        Assert.Equal(3, badge.ActiveMembers);
        Assert.Equal(1, badge.OverdueMembers);
        Assert.Equal(1, badge.UnrepliedWorkOrders);
        Assert.Equal(
            (summary.ActiveWorkOrders, summary.ActiveMembers, summary.OverdueMembers, summary.UnrepliedWorkOrders),
            (badge.ActiveWorkOrders, badge.ActiveMembers, badge.OverdueMembers, badge.UnrepliedWorkOrders));
    }

    [Fact]
    public void MyBadge_ServerAdmin回全0不擲()
    {
        AddOrder(0);
        var badge = Service(FakeCurrentUser.ServerAdmin()).MyBadge();

        Assert.Equal(0, badge.ActiveWorkOrders);
        Assert.Equal(0, badge.ActiveMembers);
        Assert.Equal(0, badge.OverdueMembers);
        Assert.Equal(0, badge.UnrepliedWorkOrders);
    }
}
