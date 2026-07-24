using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 資料可見範圍（docs/WEB-SPEC.md §7.1 第 3 層）——**Phase 1 的核心驗收項目**。
///
/// 這是不可繞過的最後防線：即使 API 忘了掛 [Permission]，查詢仍只回授權範圍內的資料。
/// §12 要求「每個查詢型 Service 至少一條授權過濾測試」，這裡是那條規則的源頭。
/// </summary>
public class VisibilityServiceTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeHostStore _hosts = new();

    private VisibilityService Create(ICurrentUser currentUser) =>
        new(currentUser, _users, _userGroups, _access, _hosts);

    /// <summary>建立「OO 部門使用者 → OO 部門主機」的完整授權鏈，並額外建立一台 XX 部門主機</summary>
    private (WebUser user, WebHost ooHost, WebHost xxHost) SetupTwoDepartments()
    {
        var ooGroup = _userGroups.Upsert(new UserGroup { GroupName = "OO部門", Role = UserRole.User });
        var xxGroup = _userGroups.Upsert(new UserGroup { GroupName = "XX部門", Role = UserRole.User });

        var user = _users.Upsert(new WebUser
        {
            Account = "DOMAIN\\wang",
            GroupIds = new List<long> { ooGroup.GroupId }
        });

        var ooHost = _hosts.Upsert(new WebHost { HostName = "SRV-OO-01", GroupIds = new List<long> { 10 } });
        var xxHost = _hosts.Upsert(new WebHost { HostName = "SRV-XX-01", GroupIds = new List<long> { 20 } });

        _access.ReplaceAll(new[]
        {
            new GroupAccess { UserGroupId = ooGroup.GroupId, HostGroupId = 10 },
            new GroupAccess { UserGroupId = xxGroup.GroupId, HostGroupId = 20 }
        });

        return (user, ooHost, xxHost);
    }

    [Fact]
    public void 一般使用者_只看得到被授權群組的主機()
    {
        var (user, ooHost, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        var visible = service.GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
        Assert.DoesNotContain(xxHost.HostId, visible);
    }

    [Fact]
    public void 持有ViewAll_看得到全部主機()
    {
        var (_, ooHost, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll));

        var visible = service.GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
        Assert.Contains(xxHost.HostId, visible);
    }

    [Fact]
    public void 跨部門成員_看得到兩個部門的主機()
    {
        var (user, ooHost, xxHost) = SetupTwoDepartments();
        var xxGroup = _userGroups.FindByName("XX部門")!;
        _users.SetGroups(user.UserId, new[] { user.GroupIds[0], xxGroup.GroupId });

        var visible = Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
        Assert.Contains(xxHost.HostId, visible);
    }

    [Fact]
    public void 未授權任何主機群組_可見範圍為空()
    {
        SetupTwoDepartments();
        var lonely = _users.Upsert(new WebUser { Account = "DOMAIN\\new" });

        Assert.Empty(Create(FakeCurrentUser.ForUser(lonely.UserId)).GetVisibleHostIds());
    }

    /// <summary>停用群組是「暫時收回這批人的權限」的手段，成員資格還在也不該給範圍</summary>
    [Fact]
    public void 群組已停用_不帶來可見範圍()
    {
        var (user, _, _) = SetupTwoDepartments();
        var ooGroup = _userGroups.FindByName("OO部門")!;
        ooGroup.Active = false;
        _userGroups.Upsert(ooGroup);

        Assert.Empty(Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds());
    }

    [Fact]
    public void 使用者已停用_可見範圍為空()
    {
        var (user, _, _) = SetupTwoDepartments();
        user.Active = false;
        _users.Upsert(user);

        Assert.Empty(Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds());
    }

    /// <summary>serverAdmin 是維護帳號，刻意不看業務資料（最小授權）</summary>
    [Fact]
    public void serverAdmin_可見範圍為空()
    {
        SetupTwoDepartments();

        var service = Create(FakeCurrentUser.ServerAdmin());

        Assert.Empty(service.GetVisibleHostIds());
    }

    [Fact]
    public void 未登入_可見範圍為空()
    {
        SetupTwoDepartments();

        Assert.Empty(Create(FakeCurrentUser.Anonymous()).GetVisibleHostIds());
    }

    /// <summary>
    /// 對沒有權限的人來說，「不存在」與「看不到」必須是同一件事——
    /// 回 403 等於確認「這台主機存在」，可以用來列舉機房裡有哪些主機。
    /// </summary>
    [Fact]
    public void EnsureVisible_未授權主機_拋出找不到而非權限不足()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        var ex = Assert.Throws<DomainException>(() => service.EnsureVisible(xxHost.HostId));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void EnsureVisible_已授權主機_通過()
    {
        var (user, ooHost, _) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        service.EnsureVisible(ooHost.HostId);   // 不應拋例外
    }

    [Fact]
    public void GetVisibleHosts_依主機名稱排序()
    {
        _hosts.Upsert(new WebHost { HostName = "SRV-Z", GroupIds = new List<long> { 10 } });
        _hosts.Upsert(new WebHost { HostName = "SRV-A", GroupIds = new List<long> { 10 } });

        var hosts = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHosts();

        Assert.Equal(new[] { "SRV-A", "SRV-Z" }, hosts.Select(h => h.HostName));
    }
}

// ── 測試替身 ─────────────────────────────────────────────────────────────────

internal class FakeCurrentUser : ICurrentUser
{
    public bool IsAuthenticated { get; private init; } = true;
    public long UserId { get; private init; }
    public string Account { get; private init; } = "test";
    public string DisplayName { get; private init; } = "測試使用者";
    public IReadOnlySet<Capability> Capabilities { get; private init; } = new HashSet<Capability>();
    public bool IsServerAdmin { get; private init; }

    public bool Has(Capability capability) => Capabilities.Contains(capability);

    public static FakeCurrentUser ForUser(long userId, params Capability[] capabilities) =>
        new() { UserId = userId, Capabilities = capabilities.ToHashSet() };

    public static FakeCurrentUser WithCapabilities(params Capability[] capabilities) =>
        new() { UserId = 999, Capabilities = capabilities.ToHashSet() };

    public static FakeCurrentUser ServerAdmin() =>
        new() { UserId = 0, IsServerAdmin = true, Capabilities = RoleCapabilityMap.ForServerAdmin() };

    public static FakeCurrentUser Anonymous() => new() { IsAuthenticated = false, UserId = 0 };
}

internal class FakeHostStore : IHostStore
{
    private readonly List<WebHost> _hosts = new();
    private long _nextId = 1;

    public List<WebHost> GetAll() => _hosts.ToList();

    public WebHost? Get(long hostId) => _hosts.FirstOrDefault(h => h.HostId == hostId);

    public WebHost? FindByName(string hostName) =>
        _hosts.FirstOrDefault(h => string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase));

    public WebHost Upsert(WebHost host)
    {
        var existing = FindByName(host.HostName);
        if (existing == null)
        {
            host.HostId = _nextId++;
            _hosts.Add(host);
            return host;
        }

        existing.IpAddress = host.IpAddress;
        existing.SentinelId = host.SentinelId;
        existing.NetiqServer = host.NetiqServer;
        existing.RoleDesc = host.RoleDesc;
        existing.Active = host.Active;
        existing.GroupIds = host.GroupIds;
        existing.OwnerUserIds = host.OwnerUserIds;
        existing.OrphanedFromSentinel = host.OrphanedFromSentinel;
        return existing;
    }

    public WebHost Touch(string hostName, DateTime reportedAt, string source = "local")
    {
        var existing = FindByName(hostName);
        if (existing == null)
        {
            var host = new WebHost { HostId = _nextId++, HostName = hostName, Source = source, LastReportAt = reportedAt };
            _hosts.Add(host);
            return host;
        }

        existing.LastReportAt = reportedAt;
        return existing;
    }

    public void SetGroups(long hostId, IEnumerable<long> groupIds)
    {
        var host = Get(hostId);
        if (host != null) host.GroupIds = groupIds.Distinct().ToList();
    }

    public void SetOwners(long hostId, IEnumerable<long> userIds)
    {
        var host = Get(hostId);
        if (host != null) host.OwnerUserIds = userIds.Distinct().ToList();
    }

    public WebHost? TouchNetiq(long hostId, string? displayName, DateTime reportedAt)
    {
        var host = Get(hostId);
        if (host == null) return null;

        host.LastReportAt = reportedAt;
        if (!string.IsNullOrWhiteSpace(displayName)) host.DisplayName = displayName;
        return host;
    }

    // 欄位搬移刻意不在替身裡重做一次：這裡只要維持「墓碑＋停用」的可見性語意，
    // 搬移規則由 HostStoreContractTests 對真實實作驗證
    public void Merge(long sourceHostId, long targetHostId)
    {
        var source = Get(sourceHostId);
        if (source == null) return;
        source.MergedInto = targetHostId;
        source.Active = false;
    }

    public void Unmerge(long hostId)
    {
        var host = Get(hostId);
        if (host == null) return;
        host.MergedInto = null;
        host.Active = true;
    }
}

internal class FakeSentinelStore : ISentinelStore
{
    private readonly List<Sentinel> _sentinels = new();
    private long _nextId = 1;

    public List<Sentinel> GetAll() => _sentinels.ToList();

    public Sentinel? Get(long sentinelId) => _sentinels.FirstOrDefault(s => s.SentinelId == sentinelId);

    public Sentinel? FindByName(string name) =>
        _sentinels.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public Sentinel Upsert(Sentinel sentinel)
    {
        var existing = sentinel.SentinelId == 0 ? null : Get(sentinel.SentinelId);
        if (existing == null)
        {
            sentinel.SentinelId = _nextId++;
            if (sentinel.CreatedAt == default) sentinel.CreatedAt = DateTime.Now;
            _sentinels.Add(sentinel);
            return sentinel;
        }

        existing.Name = sentinel.Name;
        existing.BaseUrl = sentinel.BaseUrl;
        existing.Username = sentinel.Username;
        existing.PasswordEnc = sentinel.PasswordEnc;
        existing.Active = sentinel.Active;
        existing.UpdatedAt = DateTime.Now;
        return existing;
    }

    public void Delete(long sentinelId) => _sentinels.RemoveAll(s => s.SentinelId == sentinelId);
}

internal class FakeHostGroupStore : IHostGroupStore
{
    private readonly List<HostGroup> _groups = new();
    private long _nextId = 1;

    public List<HostGroup> GetAll() => _groups.ToList();

    public HostGroup? Get(long groupId) => _groups.FirstOrDefault(g => g.GroupId == groupId);

    public HostGroup? FindByName(string groupName) =>
        _groups.FirstOrDefault(g => string.Equals(g.GroupName, groupName, StringComparison.OrdinalIgnoreCase));

    public HostGroup Upsert(HostGroup group)
    {
        var existing = group.GroupId == 0 ? null : Get(group.GroupId);
        if (existing == null)
        {
            group.GroupId = _nextId++;
            _groups.Add(group);
            return group;
        }

        existing.GroupName = group.GroupName;
        existing.Active = group.Active;
        return existing;
    }

    public void Delete(long groupId) => _groups.RemoveAll(g => g.GroupId == groupId);
}

internal class FakeGroupAccessStore : IGroupAccessStore
{
    private List<GroupAccess> _accesses = new();

    public List<GroupAccess> GetAll() => _accesses.ToList();

    public void SetForUserGroup(long userGroupId, IEnumerable<long> hostGroupIds)
    {
        _accesses.RemoveAll(a => a.UserGroupId == userGroupId);
        foreach (var hostGroupId in hostGroupIds.Distinct())
            _accesses.Add(new GroupAccess { UserGroupId = userGroupId, HostGroupId = hostGroupId });
    }

    public void ReplaceAll(IEnumerable<GroupAccess> accesses) => _accesses = accesses.ToList();
}
