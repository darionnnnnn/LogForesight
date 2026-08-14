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

    /// <summary>回饋十七輪批次D：直接對內部 list 就地操作，與真實 HostStore.MutateBatch
    /// 同一份語意（一次完成、不逐台整份讀改寫）。mutation 內若自行配發 HostId（NetiqImportApplier
    /// 的批次三態邏輯就是這樣），事後把 _nextId 頂到目前最大值之上，避免後續 Upsert／Touch
    /// 撞號。</summary>
    public TResult MutateBatch<TResult>(Func<List<WebHost>, TResult> mutation)
    {
        var result = mutation(_hosts);
        if (_hosts.Count > 0) _nextId = Math.Max(_nextId, _hosts.Max(h => h.HostId) + 1);
        return result;
    }

    public void MutateBatch(Action<List<WebHost>> mutation) => MutateBatch<object?>(list => { mutation(list); return null; });
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

    /// <summary>最近一次 Query() 收到的 filter（回饋十八輪批次A-4）：郵件下推測試斷言用，
    /// 確認 MailNotificationService 真的把 RiskLevels 帶進查詢，而不是只在記憶體篩。</summary>
    public RecordQueryFilter? LastFilter { get; private set; }

    public void Add(DailyAnalysisRecord record) => _records.Add(record);

    public List<DailyAnalysisRecord> Query(RecordQueryFilter filter)
    {
        LastFilter = filter;
        var matcher = filter.Hosts == null ? null : new HostMatcher(filter.Hosts);
        return _records
            .Where(r => matcher == null || matcher.Matches(r))
            .Where(r => filter.From == null || r.Date.Date >= filter.From.Value.Date)
            .Where(r => filter.To == null || r.Date.Date <= filter.To.Value.Date)
            .Where(r => filter.RiskLevels == null || filter.RiskLevels.Count == 0 || filter.RiskLevels.Contains(r.RiskLevel))
            .ToList();
    }

    public DailyAnalysisRecord? GetOne(IReadOnlyCollection<HostKey> hosts, DateTime date)
    {
        var matcher = new HostMatcher(hosts);
        return _records.FirstOrDefault(r => matcher.Matches(r) && r.Date.Date == date.Date);
    }

    public PagedResult<DailyAnalysisRecord> QueryPage(RecordQueryFilter filter, int page, int pageSize, string? sortKey = null, bool ascending = false) =>
        throw new NotImplementedException();

    /// <summary>記憶體實作沒有「整份 blob」與「輕量列」的區別，直接沿用 Query()——
    /// 差異只在真正的 SQL 後端才有效能意義，測試在意的是篩選/授權語意逐位一致。</summary>
    public List<DailyAnalysisRecord> QueryLightweight(RecordQueryFilter filter) => Query(filter);

    /// <summary>與 EfAnalysisRecordStore.ListHostDates 同語意（回饋十八輪批次C 測試所需）：
    /// 窗口內 HostId／Date 的輕量投影，HostId=0（無主機識別的舊列）不列入。</summary>
    public HashSet<(long HostId, DateTime Date)> ListHostDates(DateTime from, DateTime to) =>
        _records
            .Where(r => r.Date.Date >= from.Date && r.Date.Date <= to.Date && r.HostId != 0)
            .Select(r => (r.HostId, r.Date.Date))
            .ToHashSet();
}

/// <summary>問題負責人規則的記憶體實作（回饋十八輪批次F）：與正式的 IssueOwnerStore
/// 同語意（(Source,EventId) 不分大小寫為鍵）。</summary>
internal class FakeIssueOwnerStore : IIssueOwnerStore
{
    private readonly List<IssueProfile> _rules = new();

    public List<IssueProfile> GetAll() => _rules.ToList();

    public IssueProfile? Get(string source, int eventId) => _rules.FirstOrDefault(r => Matches(r, source, eventId));

    public IssueProfile Upsert(IssueProfile rule)
    {
        var existing = _rules.FirstOrDefault(r => Matches(r, rule.SourceName, rule.EventId));
        if (existing == null)
        {
            rule.UpdatedAt = DateTime.Now;
            _rules.Add(rule);
            return rule;
        }
        existing.OwnerUserIds = rule.OwnerUserIds;
        existing.Note = rule.Note;
        existing.UpdatedAt = DateTime.Now;
        existing.UpdatedByAccount = rule.UpdatedByAccount;
        return existing;
    }

    public void Delete(string source, int eventId) => _rules.RemoveAll(r => Matches(r, source, eventId));

    // 委派單一事實來源（終檢輪修正）：自寫一份就是 FakeHostStore/FakeUserStore 踩過的
    // 「測試替身與正式實作語意漂移」家族，正式端改比對規則時這裡不會跟著動。
    private static bool Matches(IssueProfile r, string source, int eventId) =>
        IssueProfile.Matches(r, source, eventId);
}

/// <summary>
/// 記憶體版 SMTP 寄送替身（回饋十五輪批次D）：不連真的 SMTP，只記錄送出過的信供斷言。
/// <see cref="ThrowOnSend"/> 供「寄送失敗不影響呼叫端」的測試情境切換。
/// </summary>
internal class FakeSmtpMailSender : ISmtpMailSender
{
    public List<(SmtpConnectionSpec Connection, MailMessageSpec Message)> Sent { get; } = new();

    /// <summary>每次 SendAsync 被呼叫都記錄（不論成功失敗）——回饋十六輪批次A 用來驗證
    /// 「連續失敗熔斷」之類只看得到嘗試次數、看不到成功次數的行為。</summary>
    public List<MailMessageSpec> Attempts { get; } = new();

    public Exception? ThrowOnSend { get; set; }

    /// <summary>只對 To 含此收件人的信丟例外，其餘照常送出——供「部分收件人寄送失敗」的
    /// 聚合寄送測試切換（回饋十六輪批次A-1）。</summary>
    public string? ThrowOnSendForRecipient { get; set; }

    /// <summary>與 <see cref="ThrowOnSendForRecipient"/> 搭配：丟這個例外實例而非預設的
    /// InvalidOperationException——「取消時已全數寄成功的 record 仍落地標記」的測試需要
    /// 對特定收件人模擬 OperationCanceledException。</summary>
    public Exception? ThrowOnSendForRecipientError { get; set; }

    /// <summary>每次寄送前呼叫——供測試在「寄到第 N 位收件人時」觸發副作用（例如取消
    /// CancellationTokenSource，模擬服務在寄送中途被停止，而非一開始就已停止）。</summary>
    public Action<MailMessageSpec>? OnSend { get; set; }

    public Task SendAsync(SmtpConnectionSpec connection, MailMessageSpec message, CancellationToken ct = default)
    {
        Attempts.Add(message);
        OnSend?.Invoke(message);
        if (ThrowOnSend != null) throw ThrowOnSend;
        if (ThrowOnSendForRecipient != null &&
            message.To.Any(to => string.Equals(to, ThrowOnSendForRecipient, StringComparison.OrdinalIgnoreCase)))
        {
            throw ThrowOnSendForRecipientError ?? new InvalidOperationException($"smtp down for {ThrowOnSendForRecipient}");
        }
        Sent.Add((connection, message));
        return Task.CompletedTask;
    }
}
