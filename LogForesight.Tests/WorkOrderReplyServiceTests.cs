using System.Diagnostics;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理人回覆交辦單（<see cref="WorkOrderReplyService"/>）：部分回覆、只准處理人本人（管理者也擋）、
/// 狀態驗證零寫入、上報通知只算新轉入、多單回覆先全檢查再寫。
/// store 用記憶體替身、寫入走真的 <see cref="WorkOrderCoordinator"/>、郵件走真的
/// <see cref="MailNotificationService"/>＋可記錄的 <see cref="FakeSmtpMailSender"/>。
/// </summary>
public class WorkOrderReplyServiceTests : IDisposable
{
    private const string Source = "disk";
    private static readonly DateTime D2 = DateTime.Today.AddDays(-2);

    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeSmtpMailSender _mailSender = new();
    private readonly RecordingAuditService _audit = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly WorkOrderCoordinator _coordinator;
    private readonly MailNotificationService _mail;

    private readonly WebUser _alice;
    private readonly WebUser _bob;
    private readonly WebUser _manager;

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    public WorkOrderReplyServiceTests()
    {
        _orders = new FakeWorkOrderStore(_cases);
        var caseCoordinator = new IssueCaseCoordinator(_cases, _issueHandlings, _handlingLog, _records, _hosts, new FakeIssueOwnerStore());
        _coordinator = new WorkOrderCoordinator(_orders, _cases, _issueHandlings, caseCoordinator, _handlingLog, _hosts);

        _alice = _users.Upsert(new WebUser { Account = "DOMAIN\\alice", DisplayName = "愛麗絲", Active = true });
        _bob = _users.Upsert(new WebUser { Account = "DOMAIN\\bob", DisplayName = "鮑伯", Active = true });
        _manager = _users.Upsert(new WebUser { Account = "DOMAIN\\boss", DisplayName = "主管", Active = true });

        var groups = new FakeUserGroupStore();
        var admins = groups.Upsert(new UserGroup { GroupName = "admins", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = "admin1", Email = "admin1@test.local", Active = true, GroupIds = new List<long> { admins.GroupId } });
        _settings.Update(s => s.MailEnabled = true);
        _mail = new MailNotificationService(
            _settings, _mailSender, _hosts, _users, groups, new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), _handlingLog, new MailNotifyStateStore(_fx.Blob("mail_notify_state")), new FakeIssueOwnerStore());

        foreach (var host in new[] { "H1", "H2", "H3" })
        {
            var h = _hosts.Upsert(new WebHost { HostName = host });
            _records.Add(new DailyAnalysisRecord
            {
                Date = D2, HostId = h.HostId, Host = host,
                TopIssues = new List<LogIssueSignature> { Signature(153), Signature(154), Signature(155) }
            });
        }
    }

    private static LogIssueSignature Signature(int eventId) =>
        new() { LogName = "System", Source = Source, EventId = eventId, EntryType = EventLogEntryType.Error };

    private WorkOrderReplyService Service(ICurrentUser user) => new(_coordinator, _orders, _cases, user, _audit, _mail);

    private static ICurrentUser As(WebUser user, params Capability[] caps) => FakeCurrentUser.ForUser(user.UserId, caps);

    private long CreateOrder(WebUser handler, int eventId, params string[] hosts) =>
        _coordinator.Create(new WorkOrderCreateRequest
        {
            Source = Source, EventId = eventId, IssueLabel = $"{Source} {eventId}", HandlerId = handler.UserId,
            Members = hosts.Select(h => new WorkOrderMember
            {
                HostName = h, IssueKey = IssueSignatureKey.For(Signature(eventId)), IssueLabel = $"{Source} {eventId}", TriggerDate = D2
            }).ToList(),
            Actor = new WorkOrderActor { ActorId = _manager.UserId, ActorAccount = "DOMAIN\\boss", OccurredAt = DateTime.Now.AddMinutes(-5) }
        }).WorkOrderId;

    private IssueCase Member(long orderId, string host) => _cases.GetByWorkOrder(orderId, 0, 100).Single(c => c.HostName == host);

    /// <summary>寫入前後比對用：成員狀態、單的回覆時間與版本、事件數</summary>
    private string Snapshot(long orderId)
    {
        var order = _orders.Get(orderId)!;
        var members = string.Join(";", _cases.GetByWorkOrder(orderId, 0, 100)
            .OrderBy(c => c.HostName).Select(c => $"{c.HostName}:{c.Status}:{c.ClosedAt}"));
        return $"{members}|{order.LastReplyAt}|{order.UpdatedAt.Ticks}|{_orders.ListEvents(orderId).Count}";
    }

    // ── 單張回覆 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 部分回覆_勾2台resolved_只那2台結案_單仍進行中_稽核一筆()
    {
        var id = CreateOrder(_alice, 153, "H1", "H2", "H3");
        var h1 = Member(id, "H1");
        var h2 = Member(id, "H2");

        var result = Service(As(_alice, Capability.Handle)).Reply(id, new WorkOrderReplyRequest
        {
            CaseIds = new List<string> { h1.CaseId, h2.CaseId }, Status = IssueHandlingStatuses.Resolved, Note = "修好了"
        });

        Assert.Equal(id, result.WorkOrderId);
        Assert.Equal(2, result.Cases);
        Assert.False(result.WorkOrderClosed);
        Assert.NotNull(Member(id, "H1").ClosedAt);
        Assert.NotNull(Member(id, "H2").ClosedAt);
        Assert.Null(Member(id, "H3").ClosedAt);
        var order = _orders.Get(id)!;
        Assert.Null(order.ClosedAt);
        Assert.NotNull(order.LastReplyAt);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.HandlingStatus, entry.Action);
        Assert.Equal("work_order", entry.TargetKind);
        Assert.Equal(id.ToString(), entry.TargetId);
        Assert.Equal($"回覆交辦單 #{id}「disk 153」：2 台標為「已處理」", entry.Summary);
    }

    [Fact]
    public void 非處理人_一般使用者_Forbidden零寫入()
    {
        var id = CreateOrder(_alice, 153, "H1", "H2");
        var before = Snapshot(id);

        var ex = Assert.Throws<DomainException>(() => Service(As(_bob, Capability.Handle)).Reply(id, new WorkOrderReplyRequest
        {
            Status = IssueHandlingStatuses.Resolved, Note = "x"
        }));

        Assert.Equal(ApiErrorCodes.Forbidden, ex.Code);
        Assert.Equal(before, Snapshot(id));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 非處理人_具Assign的管理者也擋_Forbidden零寫入()
    {
        var id = CreateOrder(_alice, 153, "H1", "H2");
        var before = Snapshot(id);
        var svc = Service(As(_manager, Capability.Assign, Capability.ViewAll, Capability.Handle, Capability.Maintain));

        var ex = Assert.Throws<DomainException>(() => svc.Reply(id, new WorkOrderReplyRequest
        {
            Status = IssueHandlingStatuses.Resolved, Note = "管理者代答"
        }));

        Assert.Equal(ApiErrorCodes.Forbidden, ex.Code);
        Assert.Equal(before, Snapshot(id));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 觀察中無日期_Validation零寫入()
    {
        var id = CreateOrder(_alice, 153, "H1", "H2");
        var before = Snapshot(id);

        var ex = Assert.Throws<DomainException>(() => Service(As(_alice, Capability.Handle)).Reply(id, new WorkOrderReplyRequest
        {
            Status = IssueHandlingStatuses.Observing, Note = "看看"
        }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Equal(before, Snapshot(id));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 不存在404_已結案Validation_案件不屬於本單Validation()
    {
        var svc = Service(As(_alice, Capability.Handle));
        Assert.Equal(ApiErrorCodes.NotFound,
            Assert.Throws<DomainException>(() => svc.Reply(9999, new WorkOrderReplyRequest { Status = IssueHandlingStatuses.Resolved })).Code);

        var closed = CreateOrder(_alice, 153, "H1");
        _coordinator.Cancel(closed, "不做了", new WorkOrderActor { ActorAccount = "boss", OccurredAt = DateTime.Now });
        Assert.Equal(ApiErrorCodes.ValidationFailed,
            Assert.Throws<DomainException>(() => svc.Reply(closed, new WorkOrderReplyRequest { Status = IssueHandlingStatuses.Resolved })).Code);

        var id = CreateOrder(_alice, 154, "H1");
        Assert.Equal(ApiErrorCodes.ValidationFailed,
            Assert.Throws<DomainException>(() => svc.Reply(id, new WorkOrderReplyRequest
            {
                CaseIds = new List<string> { "not-a-member" }, Status = IssueHandlingStatuses.Resolved
            })).Code);
    }

    [Fact]
    public void Escalated_一台原本就escalated一台新轉入_通知一封且主機數1()
    {
        var id = CreateOrder(_alice, 153, "H1", "H2");
        var h1 = Member(id, "H1");
        h1.Status = IssueHandlingStatuses.Escalated;
        h1.Note = "先前已上報";
        _cases.Save(h1);

        Service(As(_alice, Capability.Handle)).Reply(id, new WorkOrderReplyRequest
        {
            Status = IssueHandlingStatuses.Escalated, Note = "需要廠商"
        });

        var sent = Assert.Single(_mailSender.Sent);
        Assert.Contains("（H2）", sent.Message.Body);
        Assert.DoesNotContain("台主機", sent.Message.Body);
        Assert.DoesNotContain("H1", sent.Message.Body);
    }

    // ── 多單回覆 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 多單回覆_兩張本人單都寫_稽核一筆()
    {
        var a = CreateOrder(_alice, 153, "H1", "H2");
        var b = CreateOrder(_alice, 154, "H3");

        var result = Service(As(_alice, Capability.Handle)).ReplyMany(new WorkOrderReplyManyRequest
        {
            WorkOrderIds = new List<long> { a, b }, Status = IssueHandlingStatuses.Resolved, Note = "一起修好"
        });

        Assert.Equal(2, result.WorkOrders);
        Assert.Equal(3, result.Cases);
        Assert.Equal(2, result.ClosedWorkOrders);
        Assert.All(new[] { a, b }, id =>
        {
            Assert.NotNull(_orders.Get(id)!.LastReplyAt);
            Assert.Equal(WorkOrderCloseReasons.AllClosed, _orders.Get(id)!.ClosedReason);
            Assert.Single(_orders.ListEvents(id), e => e.Action == WorkOrderEventActions.Replied);
        });
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal($"{a},{b}", entry.TargetId);
        Assert.Equal("work_order", entry.TargetKind);
    }

    [Fact]
    public void 多單回覆_夾一張別人的單_整筆Forbidden零寫入()
    {
        var a = CreateOrder(_alice, 153, "H1", "H2");
        var foreign = CreateOrder(_bob, 154, "H1");
        var b = CreateOrder(_alice, 155, "H3");
        var beforeA = Snapshot(a);
        var beforeB = Snapshot(b);
        var beforeForeign = Snapshot(foreign);

        // 別人的單排在中間：逐張檢查逐張寫的話，第一張會先被寫入
        var ex = Assert.Throws<DomainException>(() => Service(As(_alice, Capability.Handle)).ReplyMany(new WorkOrderReplyManyRequest
        {
            WorkOrderIds = new List<long> { a, foreign, b }, Status = IssueHandlingStatuses.Resolved, Note = "x"
        }));

        Assert.Equal(ApiErrorCodes.Forbidden, ex.Code);
        Assert.Equal(beforeA, Snapshot(a));
        Assert.Equal(beforeB, Snapshot(b));
        Assert.Equal(beforeForeign, Snapshot(foreign));
        Assert.Empty(_audit.Entries);
        Assert.Empty(_mailSender.Sent);
    }

    [Fact]
    public void 多單回覆_空清單Validation()
    {
        var ex = Assert.Throws<DomainException>(() => Service(As(_alice, Capability.Handle)).ReplyMany(new WorkOrderReplyManyRequest
        {
            Status = IssueHandlingStatuses.Resolved
        }));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }

    [Fact]
    public void 多單回覆_稽核單號超過20張只列前20等N張()
    {
        var ids = new List<long>();
        for (var i = 0; i < 21; i++) ids.Add(CreateOrder(_alice, 1000 + i, "H1"));

        Service(As(_alice, Capability.Handle)).ReplyMany(new WorkOrderReplyManyRequest
        {
            WorkOrderIds = ids, Status = IssueHandlingStatuses.InProgress, Note = "處理中"
        });

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(string.Join(",", ids.Take(20)) + " 等 21 張", entry.TargetId);
    }

    // ── 狀態驗證共用 helper ──────────────────────────────────────────────────

    [Fact]
    public void IssueStatusValidation_觀察中91天_Validation_90天通過()
    {
        var ex = Assert.Throws<DomainException>(() =>
            IssueStatusValidation.Validate(IssueHandlingStatuses.Observing, DateTime.Today.AddDays(91), clearing: false, note: null));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);

        IssueStatusValidation.Validate(IssueHandlingStatuses.Observing, DateTime.Today.AddDays(90), clearing: false, note: null);
    }

    // ── 執行活動：案件待逐日同步件數 ─────────────────────────────────────────

    [Fact]
    public void RunActivity_CaseDaySyncPending兩個分支都有值()
    {
        var cases = new FakeIssueCaseStore();
        foreach (var (id, pending) in new[] { ("p1", true), ("p2", true), ("n1", false) })
        {
            cases.Save(new IssueCase
            {
                CaseId = id, HostName = id, IssueKey = "System|disk|153|1", IssueLabel = "disk 153",
                Status = IssueHandlingStatuses.InProgress, FirstLinkedDate = D2, LastLinkedDate = D2,
                CreatedAt = D2, CreatedByAccount = "admin", UpdatedAt = D2, DaySyncPending = pending
            });
        }
        var displayNames = new UserDisplayNameService(_settings);

        var idle = new RunActivityController(new SchedulerRunState(), new AiAnalysisRunState(), _users, displayNames,
            FakeCurrentUser.ForUser(_alice.UserId), cases).Get().Data!;
        var runState = new SchedulerRunState();
        Assert.True(runState.TryBeginRun("schedule", out _));
        var running = new RunActivityController(runState, new AiAnalysisRunState(), _users, displayNames,
            FakeCurrentUser.ForUser(_alice.UserId), cases).Get().Data!;

        Assert.Equal(2, idle.CaseDaySyncPending);
        Assert.False(idle.IsFetchRun);
        Assert.Equal(2, running.CaseDaySyncPending);
        Assert.True(running.IsFetchRun);
    }
}
