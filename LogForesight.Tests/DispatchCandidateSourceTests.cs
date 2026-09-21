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

    private DispatchCandidateSource Source() =>
        new(_users, _groups, _hosts, _access, _issueOwners, _aggregates, _settings);

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
