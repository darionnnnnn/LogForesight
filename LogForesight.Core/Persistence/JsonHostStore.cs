namespace LogForesight;

/// <summary><see cref="IHostStore"/> 的 JSONL 後端實作：webdata\hosts.json</summary>
public class JsonHostStore : JsonCollectionFile<WebHost>, IHostStore
{
    public JsonHostStore(string filePath) : base(filePath) { }

    public List<WebHost> GetAll() => Read();

    public WebHost? Get(long hostId) => Read().FirstOrDefault(h => h.HostId == hostId);

    public WebHost? FindByName(string hostName) =>
        Read().FirstOrDefault(h => string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase));

    public WebHost Upsert(WebHost host)
    {
        return Mutate(hosts =>
        {
            var existing = hosts.FirstOrDefault(h =>
                string.Equals(h.HostName, host.HostName, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                host.HostId = NextId(hosts.Select(h => h.HostId));
                hosts.Add(host);
                return host;
            }

            existing.HostName = host.HostName;
            existing.IpAddress = host.IpAddress;
            existing.IpUpdatedAt = host.IpUpdatedAt;
            existing.NetiqServer = host.NetiqServer;
            existing.RoleDesc = host.RoleDesc;
            existing.Source = host.Source;
            existing.Active = host.Active;
            existing.GroupIds = host.GroupIds;
            existing.OwnerUserIds = host.OwnerUserIds;
            // LastReportAt 與 MergedInto 刻意不由此路徑覆寫：
            // 前者是批次的職責，後者只能經 Merge 設定
            return existing;
        });
    }

    public WebHost Touch(string hostName, DateTime reportedAt, string source = "local")
    {
        return Mutate(hosts =>
        {
            var existing = hosts.FirstOrDefault(h =>
                string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                var host = new WebHost
                {
                    HostId = NextId(hosts.Select(h => h.HostId)),
                    HostName = hostName,
                    Source = source,
                    LastReportAt = reportedAt
                };
                hosts.Add(host);
                return host;
            }

            // 只更新回報時間：其餘欄位由 Web 維護，批次不知道也不該猜
            existing.LastReportAt = reportedAt;
            return existing;
        });
    }

    public void SetGroups(long hostId, IEnumerable<long> groupIds)
    {
        Mutate(hosts =>
        {
            var host = hosts.FirstOrDefault(h => h.HostId == hostId);
            if (host == null) return;
            host.GroupIds = groupIds.Distinct().ToList();
        });
    }

    public void SetOwners(long hostId, IEnumerable<long> userIds)
    {
        Mutate(hosts =>
        {
            var host = hosts.FirstOrDefault(h => h.HostId == hostId);
            if (host == null) return;
            host.OwnerUserIds = userIds.Distinct().ToList();
        });
    }

    public void Merge(long sourceHostId, long targetHostId)
    {
        Mutate(hosts =>
        {
            var source = hosts.FirstOrDefault(h => h.HostId == sourceHostId);
            if (source == null) return;

            source.MergedInto = targetHostId;
            source.Active = false;
        });
    }
}

/// <summary><see cref="IHostGroupStore"/> 的 JSONL 後端實作：webdata\host_groups.json</summary>
public class JsonHostGroupStore : JsonCollectionFile<HostGroup>, IHostGroupStore
{
    public JsonHostGroupStore(string filePath) : base(filePath) { }

    public List<HostGroup> GetAll() => Read();

    public HostGroup? Get(long groupId) => Read().FirstOrDefault(g => g.GroupId == groupId);

    public HostGroup? FindByName(string groupName) =>
        Read().FirstOrDefault(g => string.Equals(g.GroupName, groupName, StringComparison.OrdinalIgnoreCase));

    public HostGroup Upsert(HostGroup group)
    {
        return Mutate(groups =>
        {
            var existing = group.GroupId == 0 ? null : groups.FirstOrDefault(g => g.GroupId == group.GroupId);

            if (existing == null)
            {
                group.GroupId = NextId(groups.Select(g => g.GroupId));
                groups.Add(group);
                return group;
            }

            existing.GroupName = group.GroupName;
            existing.Active = group.Active;
            return existing;
        });
    }

    public void Delete(long groupId) => Mutate(groups => groups.RemoveAll(g => g.GroupId == groupId));
}

/// <summary><see cref="IGroupAccessStore"/> 的 JSONL 後端實作：webdata\group_access.json</summary>
public class JsonGroupAccessStore : JsonCollectionFile<GroupAccess>, IGroupAccessStore
{
    public JsonGroupAccessStore(string filePath) : base(filePath) { }

    public List<GroupAccess> GetAll() => Read();

    public void SetForUserGroup(long userGroupId, IEnumerable<long> hostGroupIds)
    {
        Mutate(accesses =>
        {
            accesses.RemoveAll(a => a.UserGroupId == userGroupId);
            foreach (var hostGroupId in hostGroupIds.Distinct())
            {
                accesses.Add(new GroupAccess
                {
                    UserGroupId = userGroupId,
                    HostGroupId = hostGroupId,
                    GrantedAt = DateTime.Now
                });
            }
        });
    }

    public void ReplaceAll(IEnumerable<GroupAccess> accesses)
    {
        Mutate(existing =>
        {
            existing.Clear();
            existing.AddRange(accesses);
        });
    }
}
