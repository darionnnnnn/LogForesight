using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 批次改派（<see cref="WorkOrderBulkReassignService"/>，A12、F14-3）：預覽與套用共用同一份分類，
/// 逐張沿用單張改派，單張失敗不影響其他張，一次上限 100 張。
/// 組裝比照 <see cref="WorkOrderCommandServiceTests"/>（真正的協調器與案件／交辦單記憶體替身）。
/// </summary>
public class WorkOrderBulkReassignTests : IDisposable
{
    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);
    private const int EventCount = 17;

    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeWorkOrderStore _orderStore;
    private readonly RecordingAuditService _audit = new();
    private readonly WorkOrderCommandService _commands;
    private readonly WorkOrderBulkReassignService _service;
    private readonly UserGroup _handlerRole;
    private readonly WebHost _hostA;
    private readonly WebHost _hostB;

    public WorkOrderBulkReassignTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        _orderStore = new FakeWorkOrderStore(_caseStore);
        var visibility = new AlwaysVisibleService(_hosts);
        var severity = new FakeSystemSettingsService();
        var handlingStore = new FakeHandlingStore();
        var issueHandlingStore = new FakeIssueHandlingStore();
        var repository = new RecordRepository(_recordStore, _hosts, visibility, severity);
        var aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);
        var statusResolver = new OccurrenceStatusResolver(_hosts, issueHandlingStore, _caseStore, _settingsStore);
        var displayNames = new UserDisplayNameService(_settingsStore);
        var query = new RecordListQueryService(
            repository, _hosts, _users, handlingStore, issueHandlingStore, _caseStore, _settingsStore, severity,
            visibility, aggregates, statusResolver, displayNames, new NextUnhandledSequenceCache(new DataVersionStamp()),
            new FixedIssueExclusionSource(IssueExclusion.None));
        var caseCoordinator = new IssueCaseCoordinator(_caseStore, issueHandlingStore, handlingStore, _recordStore, _hosts, new FakeIssueOwnerStore());
        var coordinator = new WorkOrderCoordinator(_orderStore, _caseStore, issueHandlingStore, caseCoordinator, handlingStore, _hosts);

        _handlerRole = _userGroups.Upsert(new UserGroup { GroupName = "處理人", Role = UserRole.User, Active = true });
        var mail = new MailNotificationService(
            _settingsStore, new FakeSmtpMailSender(), _hosts, _users, _userGroups, new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), handlingStore, new MailNotifyStateStore(_fixture.Blob("mail_notify_state")),
            new ScheduleFreshnessService(new BatchRunStore(_fixture.LogStore("batch_runs"), _fixture.LogStore("batch_run_logs")), new ScheduleOptionsStore(_fixture.Blob("schedule_options"))),
            new WorkOrderOutboxStore(_fixture.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore(),
            new FakeIssueOwnerStore());
        var capabilities = new UserCapabilityResolver(_userGroups, _hosts);

        _commands = new WorkOrderCommandService(
            query, aggregates, coordinator, _orderStore, _caseStore, new FakeNoiseMarkStore(), _hosts, _users, visibility,
            capabilities, FakeCurrentUser.WithCapabilities(Capability.Assign, Capability.Handle),
            _audit, displayNames, mail, new FakeRuleStore());
        var candidates = new WorkOrderCandidateService(_users, _userGroups, _hosts, _access, new FakeIssueOwnerStore(),
            new FakeIssueAggregateQuery(), _settingsStore, _caseStore, _orderStore, displayNames);
        _service = new WorkOrderBulkReassignService(_commands, candidates, _orderStore, _caseStore, _hosts, _users,
            new FakeRuleStore(), capabilities, _audit, displayNames);

        // 主機群組 10 只開給「看得到的目標」：HOST-A 在群組內、HOST-B 不在（授與要補上的是 B）
        _hostA = AddHost("HOST-A", Enumerable.Range(1, EventCount).ToArray(), groupIds: new List<long> { 10 });
        _hostB = AddHost("HOST-B", Enumerable.Range(1, EventCount).ToArray());
    }

    public void Dispose() => _fixture.Dispose();

    private static LogIssueSignature Disk(int eventId) => new()
    {
        LogName = "System", Source = "disk", EventId = eventId, EntryType = LogEntryType.Error,
        Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Other
    };

    private WebHost AddHost(string name, int[] eventIds, List<long>? groupIds = null)
    {
        var host = TestData.AddHost(_hosts, name);
        if (groupIds != null) { host.GroupIds = groupIds; _hosts.Upsert(host); }
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = Yesterday, RiskLevel = "高", Headline = name,
            TopIssues = eventIds.Select(Disk).ToList()
        });
        return host;
    }

    private WebUser AddUser(string account, bool active = true, bool paused = false, bool canHandle = true, string? email = "x@test.local", bool seesGroup10 = false)
    {
        var groups = new List<long>();
        if (canHandle) groups.Add(_handlerRole.GroupId);
        if (seesGroup10)
        {
            var g = _userGroups.Upsert(new UserGroup { GroupName = "可見" + account, Role = UserRole.User, Active = true });
            _access.SetForUserGroup(g.GroupId, new long[] { 10 });
            groups.Add(g.GroupId);
        }
        return _users.Upsert(new WebUser { Account = account, DisplayName = account, Active = active, DispatchPaused = paused, GroupIds = groups, Email = email });
    }

    /// <summary>建一張單：處理人＋問題 disk/eventId＋指定主機</summary>
    private long CreateOrder(WebUser handler, int eventId, params WebHost[] hosts)
    {
        var created = _commands.Create(new CreateWorkOrderRequest
        {
            Source = "disk", EventId = eventId, AssignMode = "single", HandlerId = handler.UserId,
            ScopeKind = WorkOrderScopes.Hosts, HostIds = hosts.Select(h => h.HostId).ToList()
        });
        return Assert.Single(created.Orders).WorkOrderId;
    }

    private List<long> SeventeenOrders(WebUser handler) =>
        Enumerable.Range(1, EventCount).Select(i => CreateOrder(handler, i, _hostA, _hostB)).ToList();

    private static BulkReassignRequest Req(WebUser target, IEnumerable<long> ids, bool notifyNow = false) =>
        new() { HandlerId = target.UserId, WorkOrderIds = ids.ToList(), NotifyNow = notifyNow };

    // ── A12：17 張批次改派 ────────────────────────────────────────────────

    [Fact]
    public void A12_十七張改派_目標已有同問題單的併入_其餘新單_預覽數字等於結果()
    {
        var from = AddUser("DOMAIN\\from");
        var target = AddUser("DOMAIN\\to", seesGroup10: true);
        var hostT = AddHost("HOST-T", new[] { 1, 2, 3 });
        var orders = SeventeenOrders(from);
        var targetOrders = new[] { 1, 2, 3 }.Select(i => CreateOrder(target, i, hostT)).ToList();

        var preview = _service.Preview(Req(target, orders));

        Assert.Equal(17, preview.TotalOrders);
        Assert.Equal(3, preview.MergeCount);
        Assert.Equal(14, preview.NewCount);
        Assert.Equal(0, preview.SkipCount);
        Assert.Equal(17 * 2, preview.MovedMembers);
        // 目標看得到 HOST-A、看不到 HOST-B：每張補上 1 台，全批主機去重後 1 台
        Assert.All(preview.Rows, r => Assert.Equal(1, r.GrantHostCount));
        Assert.Equal(1, preview.GrantHostCount);
        Assert.Equal(targetOrders, preview.Rows.Where(r => r.Outcome == BulkReassignOutcomes.Merge).Select(r => r.TargetWorkOrderId!.Value).ToList());

        var result = _service.Apply(Req(target, orders));

        Assert.Equal(17, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(preview.MergeCount, result.MergeCount);
        Assert.Equal(preview.NewCount, result.NewCount);
        Assert.Equal(preview.MovedMembers, result.MovedMembers);
        for (var i = 0; i < 17; i++)
        {
            Assert.Equal(preview.Rows[i].WorkOrderId, result.Rows[i].WorkOrderId);
            Assert.Equal(preview.Rows[i].Outcome, result.Rows[i].Outcome);
            Assert.Equal(BulkReassignStatuses.Ok, result.Rows[i].Status);
        }

        // 併入的三張：原單以移交結案，成員在目標處理人原本的單
        foreach (var (orderId, index) in orders.Take(3).Select((id, i) => (id, i)))
        {
            Assert.Equal(WorkOrderCloseReasons.Moved, _orderStore.Get(orderId)!.ClosedReason);
            Assert.Equal(targetOrders[index], result.Rows[index].TargetWorkOrderId);
            Assert.Equal(3, _caseStore.GetByWorkOrder(targetOrders[index], 0, int.MaxValue).Count(c => c.ClosedAt == null));
        }
        // 其餘 14 張：原單換處理人、仍在進行中
        foreach (var orderId in orders.Skip(3))
        {
            var order = _orderStore.Get(orderId)!;
            Assert.Null(order.ClosedAt);
            Assert.Equal(target.UserId, order.HandlerId);
        }
    }

    [Fact]
    public void A12_預覽只讀_不改單不寫稽核()
    {
        var from = AddUser("DOMAIN\\from");
        var target = AddUser("DOMAIN\\to");
        var orders = new[] { CreateOrder(from, 1, _hostA), CreateOrder(from, 2, _hostA) };
        _audit.Entries.Clear();
        var before = _orderStore.All.Select(o => (o.WorkOrderId, o.HandlerId, o.UpdatedAt)).ToList();

        _service.Preview(Req(target, orders));

        Assert.Empty(_audit.Entries);
        Assert.Equal(before, _orderStore.All.Select(o => (o.WorkOrderId, o.HandlerId, o.UpdatedAt)).ToList());
    }

    [Fact]
    public void A12_同批同問題兩張單改派給同一人_較小單號成為他的單_另一張併入它_預覽同結果()
    {
        var one = AddUser("DOMAIN\\one");
        var two = AddUser("DOMAIN\\two");
        var target = AddUser("DOMAIN\\to");
        var hostC = AddHost("HOST-C", new[] { 1 });
        var first = CreateOrder(one, 1, _hostA);
        var second = CreateOrder(two, 1, hostC);

        var preview = _service.Preview(Req(target, new[] { second, first }));

        Assert.Equal(BulkReassignOutcomes.New, preview.Rows.Single(r => r.WorkOrderId == first).Outcome);
        var merge = preview.Rows.Single(r => r.WorkOrderId == second);
        Assert.Equal(BulkReassignOutcomes.Merge, merge.Outcome);
        Assert.Equal(first, merge.TargetWorkOrderId);

        var result = _service.Apply(Req(target, new[] { second, first }));

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(first, result.Rows.Single(r => r.WorkOrderId == second).TargetWorkOrderId);
        Assert.Equal(2, _caseStore.GetByWorkOrder(first, 0, int.MaxValue).Count(c => c.ClosedAt == null));
    }

    [Fact]
    public void A12_一次上限100張_第101張整批被拒_100張可以()
    {
        var target = AddUser("DOMAIN\\to");
        var hundred = Enumerable.Range(1000, 100).Select(i => (long)i).ToList();

        // 100 張（都不存在）：不被上限擋，逐張回報找不到
        var ok = _service.Preview(Req(target, hundred));
        Assert.Equal(100, ok.TotalOrders);
        Assert.Equal(100, ok.SkipCount);

        var tooMany = hundred.Append(1100).ToList();
        var exPreview = Assert.Throws<DomainException>(() => _service.Preview(Req(target, tooMany)));
        var exApply = Assert.Throws<DomainException>(() => _service.Apply(Req(target, tooMany)));
        Assert.Equal(ApiErrorCodes.ValidationFailed, exPreview.Code);
        Assert.Contains("100", exApply.Message);

        // 重複的編號只算一張
        Assert.Equal(1, _service.Preview(Req(target, Enumerable.Repeat(1000L, 101))).TotalOrders);
    }

    [Fact]
    public void A12_第101張被拒時整批不動()
    {
        var from = AddUser("DOMAIN\\from");
        var target = AddUser("DOMAIN\\to");
        var first = CreateOrder(from, 1, _hostA);
        var ids = new List<long> { first };
        ids.AddRange(Enumerable.Range(5000, 100).Select(i => (long)i));
        _audit.Entries.Clear();

        Assert.Throws<DomainException>(() => _service.Apply(Req(target, ids)));

        Assert.Equal(from.UserId, _orderStore.Get(first)!.HandlerId);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void A12_單張失敗時其餘成功並逐張回報原因()
    {
        var from = AddUser("DOMAIN\\from");
        var target = AddUser("DOMAIN\\to");
        var orders = new[] { CreateOrder(from, 1, _hostA), CreateOrder(from, 2, _hostA), CreateOrder(from, 3, _hostA) };
        _orderStore.FailSaveForId = orders[1];

        var result = _service.Apply(Req(target, orders));

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        var failed = result.Rows.Single(r => r.Status == BulkReassignStatuses.Failed);
        Assert.Equal(orders[1], failed.WorkOrderId);
        Assert.Contains("重新載入", failed.Reason);
        Assert.DoesNotContain("注入", failed.Reason);   // 例外原文不外洩
        Assert.Equal(target.UserId, _orderStore.Get(orders[0])!.HandlerId);
        Assert.Equal(target.UserId, _orderStore.Get(orders[2])!.HandlerId);
        Assert.Equal(from.UserId, _orderStore.Get(orders[1])!.HandlerId);
    }

    [Fact]
    public void A12_略過三種_已是該處理人_已結案_找不到_各給原因且不影響其他張()
    {
        var from = AddUser("DOMAIN\\from");
        var target = AddUser("DOMAIN\\to");
        var movable = CreateOrder(from, 1, _hostA);
        var already = CreateOrder(target, 2, _hostA);
        var closed = CreateOrder(from, 3, _hostA);
        _commands.Cancel(closed, new CancelWorkOrderRequest { Reason = "測試取消" });

        var result = _service.Apply(Req(target, new[] { movable, already, closed, 99999L }));

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(3, result.SkippedCount);
        Assert.Contains("已經是", result.Rows.Single(r => r.WorkOrderId == already).Reason);
        Assert.Contains("已結案", result.Rows.Single(r => r.WorkOrderId == closed).Reason);
        Assert.Contains("找不到", result.Rows.Single(r => r.WorkOrderId == 99999).Reason);
        Assert.Equal(target.UserId, _orderStore.Get(movable)!.HandlerId);
    }

    [Fact]
    public void F14_3_預覽標出目標暫停_沒有email_不具處理能力_兩側各一例()
    {
        var from = AddUser("DOMAIN\\from");
        var order = CreateOrder(from, 1, _hostA);

        var weak = _service.Preview(Req(AddUser("DOMAIN\\weak", paused: true, canHandle: false, email: null), new[] { order }));
        Assert.True(weak.HandlerPaused);
        Assert.True(weak.HandlerHasNoEmail);
        Assert.True(weak.HandlerCannotHandle);

        var fine = _service.Preview(Req(AddUser("DOMAIN\\fine"), new[] { order }));
        Assert.False(fine.HandlerPaused);
        Assert.False(fine.HandlerHasNoEmail);
        Assert.False(fine.HandlerCannotHandle);
    }

    [Fact]
    public void A12_目標處理人停用或不存在_整批拒絕()
    {
        var from = AddUser("DOMAIN\\from");
        var order = CreateOrder(from, 1, _hostA);

        var inactive = Assert.Throws<DomainException>(() => _service.Preview(Req(AddUser("DOMAIN\\off", active: false), new[] { order })));
        Assert.Equal(ApiErrorCodes.ValidationFailed, inactive.Code);
        var missing = Assert.Throws<DomainException>(() => _service.Preview(new BulkReassignRequest { HandlerId = 12345, WorkOrderIds = new List<long> { order } }));
        Assert.Equal(ApiErrorCodes.NotFound, missing.Code);
        Assert.Throws<DomainException>(() => _service.Preview(new BulkReassignRequest { HandlerId = from.UserId, WorkOrderIds = new List<long>() }));
    }

    [Fact]
    public void A12_套用寫一筆批次稽核_逐張稽核照單張改派()
    {
        var from = AddUser("DOMAIN\\from");
        var target = AddUser("DOMAIN\\to");
        var orders = new[] { CreateOrder(from, 1, _hostA), CreateOrder(from, 2, _hostA) };
        _audit.Entries.Clear();

        _service.Apply(Req(target, orders));

        var reassigns = _audit.Entries.Where(e => e.Action == AuditActions.WorkOrderReassign).ToList();
        Assert.Equal(3, reassigns.Count);   // 兩張單各一筆＋批次一筆
        Assert.Contains(reassigns, e => e.Summary.StartsWith("批次改派 2 張"));
    }

    // ── SQL Server 翻譯 ──────────────────────────────────────────────────

    [Fact]
    public void SqlServer翻譯_依單號取多張單走IN清單()
    {
        using var ctx = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=x;Trusted_Connection=True").Options);

        var sql = EfWorkOrderStore.BuildManyQuery(ctx, new long[] { 1, 2, 3 }).ToQueryString();

        Assert.Contains("[work_order_id] IN (", sql);
    }

    [Fact]
    public void GetMany_EF實作含已結案_不存在的編號不回()
    {
        var store = new EfWorkOrderStore(_fixture.NewContext);
        Assert.Empty(store.GetMany(Array.Empty<long>()));
        Assert.Empty(store.GetMany(new long[] { 4040 }));
    }
}
