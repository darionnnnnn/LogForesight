using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 既有兩條指派路徑（問題批次指派、風險日指派處理人）的寫入改走 <see cref="WorkOrderCoordinator"/>：
/// 端點與回傳不變，但建出的進行中案件一律屬於一張交辦單。
/// 組裝沿用 HandlingFakes 的替身；交辦單 store 由測試自己持有，才能檢查建了幾張單。
/// </summary>
public class WorkOrderAssignRoutingTests
{
    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-2);
    private static readonly DateTime NextDay = Yesterday.AddDays(1);

    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeHandlingStore _handlingStore = new();
    private readonly FakeIssueHandlingStore _issueHandlingStore = new();
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeWorkOrderStore _orderStore;
    private readonly FakeRecordRepository _repository;
    private readonly IssueHandlingCommandService _issueService;
    private readonly DayHandlingCommandService _dayService;

    public WorkOrderAssignRoutingTests()
    {
        _orderStore = new FakeWorkOrderStore(_caseStore);
        _repository = new FakeRecordRepository(_hosts);

        var currentUser = FakeCurrentUser.WithCapabilities(Capability.Assign, Capability.Handle);
        var visibility = new AlwaysVisibleService(_hosts);
        var audit = new RecordingAuditService();
        var displayNames = new UserDisplayNameService(_settingsStore);
        var caseCoordinator = new IssueCaseCoordinator(_caseStore, _issueHandlingStore, _handlingStore, _repository, _hosts, new FakeIssueOwnerStore());
        var workOrders = new WorkOrderCoordinator(_orderStore, _caseStore, _issueHandlingStore, caseCoordinator, _handlingStore, _hosts);
        var progress = new HandlingProgressCalculator(_issueHandlingStore, _handlingStore, _caseStore, _settingsStore);
        var capabilities = new UserCapabilityResolver(new FakeUserGroupStore(), _hosts);
        var issueOwnerAdmin = new IssueOwnerAdminService(
            new FakeIssueOwnerStore(), new FakeIssueAggregateQuery(), _users, audit, currentUser, displayNames, _orderStore, workOrders);

        _issueService = new IssueHandlingCommandService(
            _handlingStore, _issueHandlingStore, _caseStore, caseCoordinator, workOrders, new FakeNoiseMarkStore(),
            _repository, _hosts, _users, visibility, currentUser, audit, progress, capabilities, issueOwnerAdmin, displayNames);
        _dayService = new DayHandlingCommandService(
            _handlingStore, _issueHandlingStore, caseCoordinator, workOrders, _repository, _hosts, _users, visibility,
            currentUser, audit, _settingsStore, progress, capabilities, displayNames);
    }

    private static LogIssueSignature DiskIssue() => new()
    {
        LogName = "System", Source = "disk", EventId = 153,
        EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = IssueSeverity.High
    };

    private static LogIssueSignature NtfsIssue() => new()
    {
        LogName = "System", Source = "ntfs", EventId = 55,
        EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = IssueSeverity.High
    };

    private WebHost AddHost(string name) => _hosts.Upsert(new WebHost { HostName = name, Active = true });

    private WebUser AddUser(string account, string displayName) =>
        _users.Upsert(new WebUser { Account = account, DisplayName = displayName, Active = true });

    private IssueCase OpenCase(string hostName, LogIssueSignature issue) =>
        _caseStore.GetOpen(hostName, IssueSignatureKey.For(issue))!;

    /// <summary>不變式：進行中案件必屬一張交辦單</summary>
    private void AssertEveryOpenCaseHasWorkOrder()
    {
        var all = _caseStore.GetMany(_hosts.GetAll().Select(h => h.HostName));
        Assert.NotEmpty(all);
        Assert.All(all.Where(c => c.ClosedAt == null), c => Assert.NotNull(c.WorkOrderId));
    }

    [Fact]
    public void 批次指派三台給同一人_建一張manual單且三件案件都連到它()
    {
        var hosts = new[] { AddHost("HOST-A"), AddHost("HOST-B"), AddHost("HOST-C") };
        var handler = AddUser("DOMAIN\\h", "處理人");
        foreach (var host in hosts) _repository.AddRecord(host.HostName, Yesterday, DiskIssue());

        var result = _issueService.BulkAssignIssueCase(new BulkAssignIssueCaseRequest
        {
            Source = "disk", EventId = 153,
            HostIds = hosts.Select(h => h.HostId).ToList(),
            HandlerId = handler.UserId
        });

        Assert.Equal(3, result.Created);
        var order = Assert.Single(_orderStore.All);
        Assert.Equal(WorkOrderOrigins.Manual, order.Origin);
        Assert.Equal(handler.UserId, order.HandlerId);
        foreach (var host in hosts)
            Assert.Equal(order.WorkOrderId, OpenCase(host.HostName, DiskIssue()).WorkOrderId);
        AssertEveryOpenCaseHasWorkOrder();
    }

    [Fact]
    public void 批次指派群組分攤給兩人_建兩張單()
    {
        var host1 = AddHost("HOST-A");
        var host2 = AddHost("HOST-B");
        var first = AddUser("DOMAIN\\a", "甲");
        var second = AddUser("DOMAIN\\b", "乙");
        _repository.AddRecord(host1.HostName, Yesterday, DiskIssue());
        _repository.AddRecord(host2.HostName, Yesterday, DiskIssue());

        var result = _issueService.BulkAssignIssueCase(new BulkAssignIssueCaseRequest
        {
            Source = "disk", EventId = 153,
            HostIds = new List<long> { host1.HostId, host2.HostId },
            Assignments = new List<IssueCaseAssignmentDto>
            {
                new() { HostId = host1.HostId, HandlerId = first.UserId },
                new() { HostId = host2.HostId, HandlerId = second.UserId }
            }
        });

        Assert.Equal(2, result.Created);
        Assert.Equal(2, _orderStore.All.Count);
        Assert.Equal(new[] { first.UserId, second.UserId }.OrderBy(x => x), _orderStore.All.Select(o => o.HandlerId).OrderBy(x => x));
        Assert.NotEqual(OpenCase("HOST-A", DiskIssue()).WorkOrderId, OpenCase("HOST-B", DiskIssue()).WorkOrderId);
        AssertEveryOpenCaseHasWorkOrder();
    }

    /// <summary>
    /// 逐台改派語意：只有 ReassignHostIds 內的主機改派並改連新單，其餘保留原處理人與原單；
    /// 同一位處理人的兩組呼叫只產生一張單。
    /// </summary>
    [Fact]
    public void 批次指派_只改派勾選主機_未勾選的略過且原單不變_同處理人只有一張單()
    {
        var host1 = AddHost("HOST-A");
        var host2 = AddHost("HOST-B");
        var owner = AddUser("DOMAIN\\owner", "原處理人");
        var newHandler = AddUser("DOMAIN\\new", "新處理人");
        _repository.AddRecord(host1.HostName, Yesterday, DiskIssue());
        _repository.AddRecord(host2.HostName, Yesterday, DiskIssue());

        _dayService.Assign(host1.HostId, Yesterday, owner.UserId);
        _dayService.Assign(host2.HostId, Yesterday, owner.UserId);
        var host2OrderBefore = OpenCase("HOST-B", DiskIssue()).WorkOrderId;
        Assert.NotNull(host2OrderBefore);

        var result = _issueService.BulkAssignIssueCase(new BulkAssignIssueCaseRequest
        {
            Source = "disk", EventId = 153,
            HostIds = new List<long> { host1.HostId, host2.HostId },
            HandlerId = newHandler.UserId,
            ReassignHostIds = new List<long> { host1.HostId }
        });

        Assert.Equal(0, result.Created);
        var reassigned = Assert.Single(result.Reassigned);
        Assert.Equal("HOST-A", reassigned.HostName);
        Assert.Equal("原處理人(DOMAIN\\owner)", reassigned.PreviousHandlerName);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("HOST-B", skipped.HostName);
        Assert.Equal("原處理人(DOMAIN\\owner)", skipped.ExistingHandlerName);

        var newOrder = Assert.Single(_orderStore.All, o => o.HandlerId == newHandler.UserId);
        Assert.Equal(WorkOrderOrigins.Manual, newOrder.Origin);

        var case1 = OpenCase("HOST-A", DiskIssue());
        Assert.Equal(newHandler.UserId, case1.HandlerId);
        Assert.Equal(newOrder.WorkOrderId, case1.WorkOrderId);

        var case2 = OpenCase("HOST-B", DiskIssue());
        Assert.Equal(owner.UserId, case2.HandlerId);
        Assert.Equal(host2OrderBefore, case2.WorkOrderId);
        AssertEveryOpenCaseHasWorkOrder();
    }

    [Fact]
    public void 日層級指派_兩個問題各一張day_assign單_隔天另一台同問題併入既有單()
    {
        var host1 = AddHost("HOST-A");
        var host2 = AddHost("HOST-B");
        var handler = AddUser("DOMAIN\\h", "處理人");
        _repository.AddRecord(host1.HostName, Yesterday, DiskIssue(), NtfsIssue());
        _repository.AddRecord(host2.HostName, NextDay, DiskIssue());

        var dto = _dayService.Assign(host1.HostId, Yesterday, handler.UserId);

        Assert.Equal(2, dto.CasesCreated);
        Assert.Equal(2, _orderStore.All.Count);
        Assert.All(_orderStore.All, o => Assert.Equal(WorkOrderOrigins.DayAssign, o.Origin));
        var diskCase = OpenCase("HOST-A", DiskIssue());
        var ntfsCase = OpenCase("HOST-A", NtfsIssue());
        Assert.NotNull(diskCase.WorkOrderId);
        Assert.NotNull(ntfsCase.WorkOrderId);
        Assert.NotEqual(diskCase.WorkOrderId, ntfsCase.WorkOrderId);

        var next = _dayService.Assign(host2.HostId, NextDay, handler.UserId);

        Assert.Equal(1, next.CasesCreated);
        Assert.Equal(2, _orderStore.All.Count);
        Assert.Equal(diskCase.WorkOrderId, OpenCase("HOST-B", DiskIssue()).WorkOrderId);
        Assert.Equal(2, _caseStore.CountByWorkOrder(diskCase.WorkOrderId!.Value));
        AssertEveryOpenCaseHasWorkOrder();
    }

    [Fact]
    public void 日層級指派_他人案件未改派時略過並回報_改派時換人並連到新單()
    {
        var host = AddHost("HOST-A");
        var owner = AddUser("DOMAIN\\owner", "原處理人");
        var newHandler = AddUser("DOMAIN\\new", "新處理人");
        _repository.AddRecord(host.HostName, Yesterday, DiskIssue());

        _dayService.Assign(host.HostId, Yesterday, owner.UserId);
        var ownerOrder = OpenCase("HOST-A", DiskIssue()).WorkOrderId;

        var skipped = _dayService.Assign(host.HostId, Yesterday, newHandler.UserId);
        Assert.Equal(0, skipped.CasesCreated);
        Assert.Equal(0, skipped.CasesReassigned);
        Assert.Equal(new[] { "原處理人(DOMAIN\\owner)" }, skipped.CasesSkippedHandlerNames);
        Assert.Equal(ownerOrder, OpenCase("HOST-A", DiskIssue()).WorkOrderId);
        Assert.Single(_orderStore.All);   // 全被略過的指派不建空單

        var reassigned = _dayService.Assign(host.HostId, Yesterday, newHandler.UserId, reassign: true);
        Assert.Equal(1, reassigned.CasesReassigned);
        Assert.Empty(reassigned.CasesSkippedHandlerNames);
        var moved = OpenCase("HOST-A", DiskIssue());
        Assert.Equal(newHandler.UserId, moved.HandlerId);
        var newOrder = Assert.Single(_orderStore.All, o => o.HandlerId == newHandler.UserId);
        Assert.Equal(newOrder.WorkOrderId, moved.WorkOrderId);
        AssertEveryOpenCaseHasWorkOrder();
    }
}
