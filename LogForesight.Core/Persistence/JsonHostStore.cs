namespace LogForesight;

/// <summary><see cref="IHostStore"/> 的 JSONL 後端實作：webdata\hosts.json</summary>
public class JsonHostStore : JsonCollectionFile<WebHost>, IHostStore
{
    public JsonHostStore(string filePath) : base(filePath) { }
    public JsonHostStore(IJsonBlobStore blob) : base(blob) { }

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
            existing.OrphanedFromSentinel = host.OrphanedFromSentinel;
            // LastReportAt、DisplayName 與 MergedInto 刻意不由此路徑覆寫：
            // 前兩者是批次的職責（Web 不知道 Sentinel 回報了什麼名字），
            // 後者只能經 Merge/Unmerge 設定
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

    public WebHost? TouchNetiq(long hostId, string? displayName, DateTime reportedAt)
    {
        return Mutate(hosts =>
        {
            var existing = hosts.FirstOrDefault(h => h.HostId == hostId);
            if (existing == null) return null;

            existing.LastReportAt = reportedAt;

            // 只在 Sentinel 真的回報了名稱時才寫入：查不到名稱的那幾天不該把既有的顯示名清空
            if (!string.IsNullOrWhiteSpace(displayName)) existing.DisplayName = displayName;

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

            var target = hosts.FirstOrDefault(h => h.HostId == targetHostId);
            if (target != null) CarryOverDescriptiveFields(source, target);

            source.MergedInto = targetHostId;
            source.Active = false;
        });
    }

    public void Unmerge(long hostId)
    {
        Mutate(hosts =>
        {
            var host = hosts.FirstOrDefault(h => h.HostId == hostId);
            if (host == null) return;

            host.MergedInto = null;
            host.Active = true;
        });
    }

    /// <summary>
    /// 目標的空欄位以來源填補（見 <see cref="IHostStore.Merge"/> 的契約說明）。
    /// 一律「目標有值就不動」——合併不該悄悄改掉人已經設好的東西。
    /// </summary>
    private static void CarryOverDescriptiveFields(WebHost source, WebHost target)
    {
        if (string.IsNullOrWhiteSpace(target.RoleDesc)) target.RoleDesc = source.RoleDesc;
        if (target.GroupIds.Count == 0) target.GroupIds = source.GroupIds.ToList();
        if (target.OwnerUserIds.Count == 0) target.OwnerUserIds = source.OwnerUserIds.ToList();
        if (string.IsNullOrWhiteSpace(target.NetiqServer)) target.NetiqServer = source.NetiqServer;

        if (string.IsNullOrWhiteSpace(target.IpAddress))
        {
            target.IpAddress = source.IpAddress;
            target.IpUpdatedAt = source.IpUpdatedAt;
        }

        // 顯示名沒有就退而用來源的登錄名稱：典型情境是「CSV 以機器名登錄的那列」併入
        // 「NetIQ 以 IP 登錄的那列」，不接的話清單上就只剩一串 IP，人認不出是哪台
        if (string.IsNullOrWhiteSpace(target.DisplayName))
        {
            target.DisplayName = string.IsNullOrWhiteSpace(source.DisplayName)
                ? source.HostName
                : source.DisplayName;
        }
    }
}

/// <summary><see cref="IHostGroupStore"/> 的 JSONL 後端實作：webdata\host_groups.json</summary>
public class JsonHostGroupStore : JsonCollectionFile<HostGroup>, IHostGroupStore
{
    public JsonHostGroupStore(string filePath) : base(filePath) { }
    public JsonHostGroupStore(IJsonBlobStore blob) : base(blob) { }

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
    public JsonGroupAccessStore(IJsonBlobStore blob) : base(blob) { }

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
