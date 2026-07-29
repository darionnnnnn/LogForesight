using LogForesight;

namespace LogForesight.Tests;

// ── 測試替身：Core\Persistence 介面的記憶體實作 ──────────────────────────────
// 集中放這裡是因為每個都被多個測試檔共用（見各類別上方註解的引用檔數），
// 原本散落在各自「率先用到」的測試檔裡，難以發現。

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
        existing.Os = sentinel.Os;
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

internal class FakeUserStore : IUserStore
{
    private readonly List<WebUser> _users = new();
    private long _nextId = 1;

    public List<WebUser> GetAll() => _users.ToList();

    public WebUser? Get(long userId) => _users.FirstOrDefault(u => u.UserId == userId);

    public WebUser? FindByAccount(string account) =>
        _users.FirstOrDefault(u => string.Equals(u.Account, account, StringComparison.OrdinalIgnoreCase));

    public WebUser Upsert(WebUser user)
    {
        var existing = FindByAccount(user.Account);
        if (existing == null)
        {
            user.UserId = _nextId++;
            _users.Add(user);
            return user;
        }

        existing.DisplayName = user.DisplayName;
        existing.Email = user.Email;
        existing.Active = user.Active;
        existing.GroupIds = user.GroupIds;
        return existing;
    }

    public void SetGroups(long userId, IEnumerable<long> groupIds)
    {
        var user = Get(userId);
        if (user != null) user.GroupIds = groupIds.Distinct().ToList();
    }
}

internal class FakeUserGroupStore : IUserGroupStore
{
    private readonly List<UserGroup> _groups = new();
    private long _nextId = 1;

    public List<UserGroup> GetAll() => _groups.ToList();

    public UserGroup? Get(long groupId) => _groups.FirstOrDefault(g => g.GroupId == groupId);

    public UserGroup? FindByName(string groupName) =>
        _groups.FirstOrDefault(g => string.Equals(g.GroupName, groupName, StringComparison.OrdinalIgnoreCase));

    public UserGroup Upsert(UserGroup group)
    {
        var existing = group.GroupId == 0 ? null : Get(group.GroupId);
        if (existing == null)
        {
            group.GroupId = _nextId++;
            _groups.Add(group);
            return group;
        }

        existing.GroupName = group.GroupName;
        existing.Role = group.Role;
        existing.Builtin = group.Builtin;
        existing.Active = group.Active;
        return existing;
    }

    public void Delete(long groupId) => _groups.RemoveAll(g => g.GroupId == groupId);
}
