using System.Diagnostics;
using System.Reflection;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="WorkOrderCandidateService"/>：手動派工人選。能力與可見範圍沿用既有解析器，
/// 這裡驗候選收錄、可見台數、暫停／近期處理過同問題的標示與排序。
/// </summary>
public class WorkOrderCandidateServiceTests
{
    private const string Source = "disk";
    private const int EventId = 153;

    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly FakeIssueAggregateQuery _aggregates = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly UserGroup _dept;
    private readonly UserGroup _noAccess;
    private readonly long _hostA;
    private readonly long _hostB;

    public WorkOrderCandidateServiceTests()
    {
        _orders = new FakeWorkOrderStore(_cases);
        _dept = _groups.Upsert(new UserGroup { GroupName = "dept", Role = UserRole.User, DispatchPool = true });
        _noAccess = _groups.Upsert(new UserGroup { GroupName = "other", Role = UserRole.User });
        _hostA = _hosts.Upsert(new WebHost { HostName = "SRV-A", GroupIds = new List<long> { 10 } }).HostId;
        _hostB = _hosts.Upsert(new WebHost { HostName = "SRV-B", GroupIds = new List<long> { 10 } }).HostId;
        _access.SetForUserGroup(_dept.GroupId, new long[] { 10 });
    }

    private WorkOrderCandidateService Service() =>
        new(_users, _groups, _hosts, _access, _issueOwners, _aggregates, _settings, _cases, _orders,
            new UserDisplayNameService(_settings));

    private WebUser User(string account, UserGroup? group = null, bool paused = false, bool active = true) =>
        _users.Upsert(new WebUser
        {
            Account = account,
            DispatchPaused = paused,
            Active = active,
            GroupIds = group == null ? new List<long>() : new List<long> { group.GroupId }
        });

    private WorkOrderCandidatesRequest Req(bool withIssue = true) => new()
    {
        HostIds = new List<long> { _hostA, _hostB },
        Source = withIssue ? Source : null,
        EventId = withIssue ? EventId : null
    };

    private void Resolved(long handlerId, int daysAgo)
    {
        _cases.Save(new IssueCase
        {
            CaseId = Guid.NewGuid().ToString("N"),
            HostName = "SRV-A",
            IssueKey = IssueSignatureKey.For(Source, "disk", EventId, EventLogEntryType.Error),
            Status = IssueHandlingStatuses.Resolved,
            HandlerId = handlerId,
            ClosedAt = DateTime.Today.AddDays(-daysAgo)
        });
    }

    [Fact]
    public void 無Handle者不在清單()
    {
        User("nogroup");
        var ok = User("ok", _dept);

        var list = Service().Get(Req());

        Assert.Equal(new[] { ok.UserId }, list.Select(c => c.UserId));
    }

    [Fact]
    public void 停用者不在清單()
    {
        User("gone", _dept, active: false);
        var ok = User("ok", _dept);

        Assert.Equal(new[] { ok.UserId }, Service().Get(Req()).Select(c => c.UserId));
    }

    [Fact]
    public void 暫停者標Paused且仍在清單()
    {
        var paused = User("p", _dept, paused: true);

        var item = Assert.Single(Service().Get(Req()));

        Assert.Equal(paused.UserId, item.UserId);
        Assert.True(item.Paused);
        Assert.True(item.InPool);
        Assert.Equal(2, item.VisibleHostCount);
    }

    [Fact]
    public void 近三十天結案過同問題的處理人標記並排第一()
    {
        User("aaa", _dept);
        var recent = User("zzz", _dept);
        Resolved(recent.UserId, daysAgo: 5);

        var list = Service().Get(Req());

        Assert.Equal(recent.UserId, list[0].UserId);
        Assert.True(list[0].RecentlyResolvedSameIssue);
        Assert.False(list[1].RecentlyResolvedSameIssue);
    }

    [Fact]
    public void 三十一天前結案的不算()
    {
        var old = User("old", _dept);
        Resolved(old.UserId, daysAgo: 31);

        Assert.False(Assert.Single(Service().Get(Req())).RecentlyResolvedSameIssue);
    }

    [Fact]
    public void 沒帶問題時不標最近處理過()
    {
        var u = User("u", _dept);
        Resolved(u.UserId, daysAgo: 1);

        Assert.False(Assert.Single(Service().Get(Req(withIssue: false))).RecentlyResolvedSameIssue);
    }

    [Fact]
    public void 看不到部分主機者計算隱藏台數且排在全可見者之後()
    {
        // 「other」群組沒有任何主機群組授權；同群組再加一個授權兩台的人作對照
        var blind = User("aaa-blind", _noAccess);
        var sees = User("zzz-sees", _dept);

        var list = Service().Get(Req());

        Assert.Equal(sees.UserId, list[0].UserId);
        Assert.Equal(0, list[0].HiddenHostCount);
        Assert.Equal(blind.UserId, list[1].UserId);
        Assert.Equal(0, list[1].VisibleHostCount);
        Assert.Equal(2, list[1].HiddenHostCount);
    }

    [Fact]
    public void 請求主機為空時台數皆為零仍回人選()
    {
        User("u", _dept);

        var item = Assert.Single(Service().Get(new WorkOrderCandidatesRequest()));

        Assert.Equal(0, item.VisibleHostCount);
        Assert.Equal(0, item.HiddenHostCount);
    }

    [Fact]
    public void 能力標註為Assign()
    {
        var method = typeof(WorkOrdersController).GetMethod(nameof(WorkOrdersController.Candidates))!;
        var methodCaps = method.GetCustomAttributes<PermissionAttribute>(inherit: false)
            .Select(a => (Capability[])a.Arguments![0]!).ToList();
        var classCaps = typeof(WorkOrdersController).GetCustomAttributes<PermissionAttribute>(inherit: false)
            .Select(a => (Capability[])a.Arguments![0]!).ToList();

        Assert.All(methodCaps.Concat(classCaps), caps => Assert.Equal(new[] { Capability.Assign }, caps));
        Assert.NotEmpty(methodCaps);
    }
}
