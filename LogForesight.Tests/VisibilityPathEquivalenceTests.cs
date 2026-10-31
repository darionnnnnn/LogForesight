using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 可見範圍兩條入口的等價守門：「目前登入者」（<see cref="VisibilityService.GetVisibleHostIds()"/>）
/// 與「指定 userId」（<see cref="HostVisibilityResolver.GetVisibleHostIds(IHostStore, IUserStore, IUserGroupStore, IGroupAccessStore, long, IIssueOwnerStore?, IIssueAggregateQuery?, int)"/>）
/// 對同一組資料必須回相同集合；規則只該有一份，兩邊漂移就是授權範圍靜默錯誤。
/// 唯一已知且刻意保留的差異：已停用使用者帶著 ViewAll claim 時，目前登入者路徑回全部、
/// 指定 userId 路徑回空（claim 由 ActiveUserMiddleware 在請求入口把關）。該組合在
/// <see cref="停用使用者持有ViewAll_目前登入者回全部_指定userId回空"/> 明確斷言，不靜默通過。
/// </summary>
public class VisibilityPathEquivalenceTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly FakeIssueAggregateQuery _issueAggregates = new();
    private readonly FakeSystemSettingsStore _settings = new();

    public enum HostState { Active, Inactive, Tombstone }

    /// <summary>使用者啟用 × 群組啟用 × 角色含 ViewAll × 主機狀態 × 三種關係（群組授權／主機負責人／問題負責人）的完整組合</summary>
    public static IEnumerable<object[]> Combinations()
    {
        foreach (var userActive in new[] { true, false })
        foreach (var groupActive in new[] { true, false })
        foreach (var viewAllRole in new[] { false, true })
        foreach (var state in Enum.GetValues<HostState>())
        foreach (var relations in Enumerable.Range(0, 8))
            yield return new object[] { userActive, groupActive, viewAllRole, state, relations };
    }

    private const int ViaGroup = 1, ViaOwner = 2, ViaIssue = 4;

    private (long UserId, long[] AllActiveHostIds) Seed(
        bool userActive, bool groupActive, bool viewAllRole, HostState state, int relations)
    {
        var group = _userGroups.Upsert(new UserGroup
        {
            GroupName = "G", Role = viewAllRole ? UserRole.Manager : UserRole.User, Active = groupActive
        });
        var user = _users.Upsert(new WebUser
        {
            Account = @"DOMAIN\u", Active = userActive, GroupIds = new List<long> { group.GroupId }
        });

        WebHost Make(string name, bool related, Action<WebHost> relate)
        {
            var h = _hosts.Upsert(new WebHost { HostName = name });
            if (related) relate(h);
            if (state == HostState.Inactive) h.Active = false;
            if (state == HostState.Tombstone) { h.Active = false; h.MergedInto = 9999; }
            return h;
        }

        var viaGroup = Make("H-GROUP", (relations & ViaGroup) != 0, h => h.GroupIds = new List<long> { 10 });
        var viaOwner = Make("H-OWNER", (relations & ViaOwner) != 0, h => h.OwnerUserIds = new List<long> { user.UserId });
        var viaIssue = Make("H-ISSUE", (relations & ViaIssue) != 0, _ => { });
        var unrelated = _hosts.Upsert(new WebHost { HostName = "H-NONE", GroupIds = new List<long> { 99 } });

        if ((relations & ViaGroup) != 0) _access.ReplaceAll(new[] { new GroupAccess { UserGroupId = group.GroupId, HostGroupId = 10 } });
        if ((relations & ViaIssue) != 0)
        {
            _issueOwners.Upsert(new IssueProfile { SourceName = "Disk", EventId = 153, OwnerUserIds = new List<long> { user.UserId } });
            _issueAggregates.HostIdsForResult = new HashSet<long> { viaIssue.HostId };
        }

        var allActive = new[] { viaGroup, viaOwner, viaIssue, unrelated }.Where(h => h.Active).Select(h => h.HostId).ToArray();
        return (user.UserId, allActive);
    }

    private IReadOnlySet<long> CurrentPath(long userId, bool claimViewAll) =>
        new VisibilityService(
            FakeCurrentUser.ForUser(userId, claimViewAll ? new[] { Capability.ViewAll } : Array.Empty<Capability>()),
            _users, _userGroups, _access, _hosts, _cases, _settings, _issueOwners, _issueAggregates)
            .GetVisibleHostIds();

    private IReadOnlySet<long> ResolverPath(long userId) =>
        HostVisibilityResolver.GetVisibleHostIds(_hosts, _users, _userGroups, _access, userId,
            _issueOwners, _issueAggregates, _settings.Get().RetentionDays);

    [Theory]
    [MemberData(nameof(Combinations))]
    public void 目前登入者與指定userId兩條路徑_結果一致(
        bool userActive, bool groupActive, bool viewAllRole, HostState state, int relations)
    {
        var (userId, allActive) = Seed(userActive, groupActive, viewAllRole, state, relations);
        // claim 與實際系統一致：啟用群組且角色含 ViewAll 才有 ViewAll 能力
        var claimViewAll = groupActive && viewAllRole;

        var current = CurrentPath(userId, claimViewAll);
        var resolver = ResolverPath(userId);

        if (!userActive && claimViewAll)
        {
            // 已知差異（見類別註解）：明確釘住兩邊各自的結果，不讓它靜默通過
            Assert.Equal(allActive.OrderBy(x => x), current.OrderBy(x => x));
            Assert.Empty(resolver);
        }
        else
        {
            Assert.Equal(resolver.OrderBy(x => x), current.OrderBy(x => x));
        }
    }

    [Fact]
    public void 停用使用者持有ViewAll_目前登入者回全部_指定userId回空()
    {
        var (userId, allActive) = Seed(userActive: false, groupActive: true, viewAllRole: true, HostState.Active, relations: 0);

        Assert.Equal(allActive.OrderBy(x => x), CurrentPath(userId, claimViewAll: true).OrderBy(x => x));
        Assert.Empty(ResolverPath(userId));
    }

    [Fact]
    public void 主機表為空_兩條路徑都回空()
    {
        var group = _userGroups.Upsert(new UserGroup { GroupName = "G", Role = UserRole.User });
        var user = _users.Upsert(new WebUser { Account = @"DOMAIN\u", GroupIds = new List<long> { group.GroupId } });

        Assert.Empty(CurrentPath(user.UserId, claimViewAll: false));
        Assert.Empty(ResolverPath(user.UserId));
    }
}
