using LogForesight.Web.Services.Mail;

namespace LogForesight.Tests;

// ── 測試替身：Core\Persistence 介面的記憶體實作 ──────────────────────────────
// 集中放這裡是因為每個都被多個測試檔共用（見各類別上方註解的引用檔數），
// 原本散落在各自「率先用到」的測試檔裡，難以發現。

internal class FakeHostStore : IHostStore
{
    private readonly List<WebHost> _hosts = new();
    private long _nextId = 1;

    /// <summary>回饋十四輪 A3：驗證 HostPlan.GroupIds 計畫階段解析一次有沒有真的把 Get 呼叫次數
    /// 從「主機數×天數」收斂成「主機數」的計數器。</summary>
    public int GetCallCount { get; private set; }

    public List<WebHost> GetAll() => _hosts.ToList();

    public WebHost? Get(long hostId)
    {
        GetCallCount++;
        return _hosts.FirstOrDefault(h => h.HostId == hostId);
    }

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

        // **逐欄與 HostStore.Upsert 對齊**：替身漏抄的後果不是測試失敗，而是測試
        // 綠燈卻蓋掉正式環境的 bug（Sentinel 那組就是這樣漏過去的）。
        // 這裡原本少了 HostName／IpUpdatedAt／Source／Os 四欄。
        existing.HostName = host.HostName;
        existing.IpAddress = host.IpAddress;
        existing.IpUpdatedAt = host.IpUpdatedAt;
        existing.SentinelId = host.SentinelId;
        existing.NetiqServer = host.NetiqServer;
        existing.RoleDesc = host.RoleDesc;
        existing.Source = host.Source;
        existing.Os = host.Os;
        existing.Active = host.Active;
        existing.GroupIds = host.GroupIds;
        existing.OwnerUserIds = host.OwnerUserIds;
        existing.OrphanedFromSentinel = host.OrphanedFromSentinel;
        // LastReportAt／DisplayName／MergedInto 刻意不覆寫（同 HostStore）：
        // 前兩者是批次的職責、後者只能經 Merge/Unmerge
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

    public void SetHighVolume(long hostId, bool isHighVolume)
    {
        var host = _hosts.FirstOrDefault(h => h.HostId == hostId); // 不經 Get()：不想污染 GetCallCount 斷言
        if (host != null) host.IsHighVolume = isHighVolume;
    }

    public HostGroupsBatchResult SetGroupsBatch(IEnumerable<long> hostIds, IEnumerable<long> groupIds, bool replace)
    {
        var wanted = groupIds.Distinct().ToList();
        var result = new HostGroupsBatchResult();

        foreach (var hostId in hostIds.Distinct())
        {
            var host = Get(hostId);
            if (host == null) continue;

            if (host.MergedInto != null)
            {
                result.Skipped.Add(host);
                continue;
            }

            host.GroupIds = replace ? wanted.ToList() : host.GroupIds.Union(wanted).Distinct().ToList();
            result.Updated.Add(host);
        }

        return result;
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

        // 與 SentinelStore.Upsert 逐欄對齊——那是「新增欄位時最容易漏改」的地方，
        // 兩邊漂移會讓測試綠燈而正式環境掉資料（UseEsmDirectory 就這樣漏過一次）
        existing.Name = sentinel.Name;
        existing.BaseUrl = sentinel.BaseUrl;
        existing.Username = sentinel.Username;
        existing.PasswordEnc = sentinel.PasswordEnc;
        existing.Active = sentinel.Active;
        existing.Os = sentinel.Os;
        existing.UseEsmDirectory = sentinel.UseEsmDirectory;
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

        // 逐欄與 UserStore.Upsert 對齊（Account 原本漏抄）。
        // LastLoginAt 刻意不覆寫：唯一寫入點是 TouchLogin，
        // 讓 Upsert 帶進來的 null 蓋掉真實登入時間會讓「最近登入」永遠是空的
        existing.Account = user.Account;
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

    public void TouchLogin(long userId, DateTime at)
    {
        var user = Get(userId);
        if (user != null) user.LastLoginAt = at;
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

/// <summary>
/// 最小可用的 IAnalysisRecordQuery 記憶體實作，供 IssueCaseCoordinator 之類只用得到
/// Query() 的呼叫端測試——QueryPage／ListHostDates 目前無測試用到，故未實作。
/// </summary>
internal class FakeAnalysisRecordQuery : IAnalysisRecordQuery
{
    private readonly List<DailyAnalysisRecord> _records = new();

    public void Add(DailyAnalysisRecord record) => _records.Add(record);

    public List<DailyAnalysisRecord> Query(RecordQueryFilter filter)
    {
        var matcher = filter.Hosts == null ? null : new HostMatcher(filter.Hosts);
        return _records
            .Where(r => matcher == null || matcher.Matches(r))
            .Where(r => filter.From == null || r.Date.Date >= filter.From.Value.Date)
            .Where(r => filter.To == null || r.Date.Date <= filter.To.Value.Date)
            .ToList();
    }

    public DailyAnalysisRecord? GetOne(IReadOnlyCollection<HostKey> hosts, DateTime date)
    {
        var matcher = new HostMatcher(hosts);
        return _records.FirstOrDefault(r => matcher.Matches(r) && r.Date.Date == date.Date);
    }

    public PagedResult<DailyAnalysisRecord> QueryPage(RecordQueryFilter filter, int page, int pageSize, string? sortKey = null, bool ascending = false) =>
        throw new NotImplementedException();

    public HashSet<(long HostId, DateTime Date)> ListHostDates(DateTime from, DateTime to) =>
        throw new NotImplementedException();
}

/// <summary>
/// 記憶體版 SMTP 寄送替身（回饋十五輪批次D）：不連真的 SMTP，只記錄送出過的信供斷言。
/// <see cref="ThrowOnSend"/> 供「寄送失敗不影響呼叫端」的測試情境切換。
/// </summary>
internal class FakeSmtpMailSender : ISmtpMailSender
{
    public List<(SmtpConnectionSpec Connection, MailMessageSpec Message)> Sent { get; } = new();

    public Exception? ThrowOnSend { get; set; }

    public Task SendAsync(SmtpConnectionSpec connection, MailMessageSpec message, CancellationToken ct = default)
    {
        if (ThrowOnSend != null) throw ThrowOnSend;
        Sent.Add((connection, message));
        return Task.CompletedTask;
    }
}
