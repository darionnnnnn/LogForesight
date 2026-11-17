using LogForesight.Web.Auth;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="DispatchCandidateSource"/>（Web 實作）的候選人快照：只收啟用中且具 Handle 能力者、
/// 派工池判定看群組啟用與旗標、可見主機直接沿用 <see cref="HostVisibilityResolver"/>。
/// </summary>
public class DispatchCandidateSourceTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly FakeIssueAggregateQuery _aggregates = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeWorkOrderStore _orders = new(new FakeIssueCaseStore());

    private DispatchCandidateSource Source() =>
        new(_users, _groups, _hosts, _access, _issueOwners, _aggregates, _settings, _orders);

    private void ContinuableOrder(long handlerId, bool autoAttach = true) =>
        _orders.Insert(new WorkOrder
        {
            SourceName = "Disk", EventId = 153, IssueLabel = "Disk 153", HandlerId = handlerId,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.All, AutoAttach = autoAttach,
            CreatedByAccount = "a", CreatedAt = DateTime.Now
        });

    private UserGroup Group(string name, UserRole role, bool pool, bool active = true) =>
        _groups.Upsert(new UserGroup { GroupName = name, Role = role, DispatchPool = pool, Active = active });

    private WebUser User(string account, bool paused = false, params long[] groupIds) =>
        _users.Upsert(new WebUser { Account = account, DispatchPaused = paused, GroupIds = groupIds.ToList() });

    [Fact]
    public void 無Handle能力的manager群組成員不收入()
    {
        var manager = Group("manager", UserRole.Manager, pool: true);
        var boss = User("boss", groupIds: manager.GroupId);

        var pool = Source().Build();

        Assert.False(pool.ByUserId.ContainsKey(boss.UserId));
        Assert.Equal(0, pool.PoolMemberCount);
    }

    [Fact]
    public void 停用使用者不收入()
    {
        var dept = Group("dept", UserRole.User, pool: true);
        var user = User("gone", groupIds: dept.GroupId);
        user.Active = false;

        Assert.False(Source().Build().ByUserId.ContainsKey(user.UserId));
    }

    [Fact]
    public void 主機負責人無群組也收入且不在池內()
    {
        var owner = User("owner");
        _hosts.Upsert(new WebHost { HostName = "SRV-01", OwnerUserIds = new List<long> { owner.UserId } });

        var pool = Source().Build();

        var candidate = pool.ByUserId[owner.UserId];
        Assert.False(candidate.InPool);
        Assert.Empty(candidate.VisibleHostIds);
    }

    /// <summary>
    /// 問題負責人（沒有群組、也不是主機負責人）靠問題負責與靜音隱含 Handle 能力——派工第 ⑤ 步只從候選快照挑負責人，
    /// 能力解析若漏帶問題負責與靜音，所有「只是問題負責人」的人會靜默消失在候選外，負責人規則派不出去。
    /// </summary>
    [Fact]
    public void 只有問題負責人身分也收入且不在池內()
    {
        var owner = User("issue-owner");
        _issueOwners.Upsert(new IssueProfile { SourceName = "DCOM", EventId = 10016, OwnerUserIds = new List<long> { owner.UserId } });

        var pool = Source().Build();

        var candidate = pool.ByUserId[owner.UserId];
        Assert.False(candidate.InPool);
        Assert.Empty(candidate.VisibleHostIds);
    }

    [Fact]
    public void 派工池群組停用時不算在池內()
    {
        var dept = Group("dept", UserRole.User, pool: true, active: false);
        var admin = Group("admin", UserRole.Admin, pool: false);
        var user = User("u", groupIds: new[] { dept.GroupId, admin.GroupId });

        var pool = Source().Build();

        Assert.False(pool.ByUserId[user.UserId].InPool);
        Assert.Equal(0, pool.PoolMemberCount);
    }

    [Fact]
    public void 暫停者可見主機為空但計入池人數()
    {
        var dept = Group("dept", UserRole.User, pool: true);
        _hosts.Upsert(new WebHost { HostName = "SRV-01", GroupIds = new List<long> { 10 } });
        _access.SetForUserGroup(dept.GroupId, new long[] { 10 });
        var paused = User("p", paused: true, groupIds: dept.GroupId);

        var pool = Source().Build();

        var candidate = pool.ByUserId[paused.UserId];
        Assert.True(candidate.InPool);
        Assert.True(candidate.Paused);
        Assert.Empty(candidate.VisibleHostIds);
        Assert.Equal(1, pool.PoolMemberCount);
        Assert.Equal(0, pool.ActivePoolMemberCount);
    }

    /// <summary>D3：不在派工池、但有可續掛單的處理人也要算可見主機，看得到的新主機才續掛得進去</summary>
    [Fact]
    public void 不在池內但有可續掛單的處理人也計算可見主機()
    {
        var dept = Group("dept", UserRole.User, pool: false);
        var host = _hosts.Upsert(new WebHost { HostName = "SRV-01", GroupIds = new List<long> { 10 } });
        _hosts.Upsert(new WebHost { HostName = "SRV-02", GroupIds = new List<long> { 11 } });
        _access.SetForUserGroup(dept.GroupId, new long[] { 10 });
        var handler = User("h", groupIds: dept.GroupId);
        ContinuableOrder(handler.UserId);

        var candidate = Source().Build().ByUserId[handler.UserId];

        Assert.False(candidate.InPool);
        Assert.Equal(new[] { host.HostId }, candidate.VisibleHostIds.ToArray());
    }

    [Fact]
    public void 不在池內且沒有可續掛單的處理人可見主機仍為空()
    {
        var dept = Group("dept", UserRole.User, pool: false);
        _hosts.Upsert(new WebHost { HostName = "SRV-01", GroupIds = new List<long> { 10 } });
        _access.SetForUserGroup(dept.GroupId, new long[] { 10 });
        var handler = User("h", groupIds: dept.GroupId);
        ContinuableOrder(handler.UserId, autoAttach: false);

        Assert.Empty(Source().Build().ByUserId[handler.UserId].VisibleHostIds);
    }

    [Fact]
    public void 暫停的處理人即使有可續掛單可見主機仍為空()
    {
        var dept = Group("dept", UserRole.User, pool: false);
        _hosts.Upsert(new WebHost { HostName = "SRV-01", GroupIds = new List<long> { 10 } });
        _access.SetForUserGroup(dept.GroupId, new long[] { 10 });
        var handler = User("h", paused: true, groupIds: dept.GroupId);
        ContinuableOrder(handler.UserId);

        Assert.Empty(Source().Build().ByUserId[handler.UserId].VisibleHostIds);
    }

    /// <summary>
    /// D2：案件授與（處理人曾被交辦該主機的問題）不是派工用的可見主機。案件授與的來源是案件表（<c>HasCaseOnHost</c>），
    /// 候選人快照只從群組授權、主機負責人、問題負責人算可見主機——結構上不相依案件儲存與 <c>VisibilityService</c>，
    /// 有人把案件授與加進來就得先改建構子，這個守門會紅。
    /// </summary>
    [Fact]
    public void 候選人快照不相依案件儲存與VisibilityService_案件授與進不來()
    {
        var parameterTypes = typeof(DispatchCandidateSource).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();

        Assert.DoesNotContain(parameterTypes, t => t.Name.Contains("CaseStore", StringComparison.Ordinal));
        Assert.DoesNotContain(parameterTypes, t => t.Name.Contains("VisibilityService", StringComparison.Ordinal));
    }

    /// <summary>
    /// D2 行為面：處理人對某主機有進行中案件（案件授與成立，<c>VisibilityService</c> 看得到），
    /// 但沒有該主機的群組授權、不是主機負責人、不是問題負責人——候選人快照的可見主機不得含它；
    /// 有群組授權的主機照常在內。
    /// </summary>
    [Fact]
    public void 只靠案件授與看得到的主機不進候選人可見主機_群組授權的照常進入()
    {
        var dept = Group("dept", UserRole.User, pool: true);
        var granted = _hosts.Upsert(new WebHost { HostName = "SRV-01", GroupIds = new List<long> { 10 } });
        var caseOnly = _hosts.Upsert(new WebHost { HostName = "SRV-02", GroupIds = new List<long> { 11 } });
        _access.SetForUserGroup(dept.GroupId, new long[] { 10 });
        var member = User("m", groupIds: dept.GroupId);

        // 前提：案件授與成立——同一份資料用 VisibilityService 看，SRV-02 確實是案件授與的主機
        var cases = new FakeIssueCaseStore();
        cases.Save(new IssueCase
        {
            CaseId = "c1", HostName = caseOnly.HostName, IssueKey = "Disk|disk|153|1",
            HandlerId = member.UserId, Status = IssueHandlingStatuses.InProgress
        });
        var visibility = new VisibilityService(FakeCurrentUser.ForUser(member.UserId), _users, _groups, _access, _hosts,
            cases, _settings, _issueOwners, _aggregates);
        Assert.True(visibility.IsCaseGrantOnly(caseOnly.HostId));
        Assert.Contains(caseOnly.HostName, visibility.GetCaseGrantHostNames());

        var candidate = Source().Build().ByUserId[member.UserId];

        Assert.DoesNotContain(caseOnly.HostId, candidate.VisibleHostIds);
        Assert.Contains(granted.HostId, candidate.VisibleHostIds);
    }

    [Fact]
    public void 池成員可見主機等於HostVisibilityResolver直接算的結果()
    {
        _settings.Update(s => s.RetentionDays = 200);
        var dept = Group("dept", UserRole.User, pool: true);
        var granted = _hosts.Upsert(new WebHost { HostName = "SRV-01", GroupIds = new List<long> { 10 } });
        _hosts.Upsert(new WebHost { HostName = "SRV-02", GroupIds = new List<long> { 11 } });
        var issueHost = _hosts.Upsert(new WebHost { HostName = "SRV-03", GroupIds = new List<long> { 12 } });
        _access.SetForUserGroup(dept.GroupId, new long[] { 10 });
        var member = User("m", groupIds: dept.GroupId);
        _issueOwners.Upsert(new IssueProfile { SourceName = "Disk", EventId = 153, OwnerUserIds = new List<long> { member.UserId } });
        _aggregates.HostIdsForResult = new HashSet<long> { issueHost.HostId };

        var pool = Source().Build();

        var expected = HostVisibilityResolver.GetVisibleHostIds(_hosts, _users, _groups, _access, member.UserId,
            _issueOwners, _aggregates, 200);
        var candidate = pool.ByUserId[member.UserId];
        Assert.Equal(expected.OrderBy(x => x), candidate.VisibleHostIds.OrderBy(x => x));
        Assert.Equal(new[] { granted.HostId, issueHost.HostId }.OrderBy(x => x), candidate.VisibleHostIds.OrderBy(x => x));
        Assert.Equal(1, pool.ActivePoolMemberCount);
    }
}
