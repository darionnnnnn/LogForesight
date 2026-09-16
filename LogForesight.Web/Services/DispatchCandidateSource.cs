using LogForesight.Web.Auth;

namespace LogForesight.Web.Services;

/// <summary>
/// <see cref="IDispatchCandidateSource"/> 的 Web 實作：每趟執行前把「誰具處理能力」「誰看得到哪些主機」
/// 算成一份資料快照交給 Core 的派工決策。兩條規則本身只在
/// <see cref="UserCapabilityResolver"/> 與 <see cref="HostVisibilityResolver"/>，這裡只呼叫、不複製。
///
/// Singleton：相依全是 Singleton store。<see cref="UserCapabilityResolver"/> 在 DI 註冊為 Scoped，
/// 不能注入 Singleton，所以以同一批 Singleton store 自行建構一份（它本身無狀態）。
/// </summary>
public sealed class DispatchCandidateSource : IDispatchCandidateSource
{
    private readonly IUserStore _users;
    private readonly IUserGroupStore _userGroups;
    private readonly IHostStore _hosts;
    private readonly IGroupAccessStore _access;
    private readonly IIssueOwnerStore _issueOwners;
    private readonly IIssueAggregateQuery _issueAggregates;
    private readonly ISystemSettingsStore _settings;
    private readonly UserCapabilityResolver _capabilities;

    public DispatchCandidateSource(
        IUserStore users, IUserGroupStore userGroups, IHostStore hosts, IGroupAccessStore access,
        IIssueOwnerStore issueOwners, IIssueAggregateQuery issueAggregates, ISystemSettingsStore settings)
    {
        _users = users;
        _userGroups = userGroups;
        _hosts = hosts;
        _access = access;
        _issueOwners = issueOwners;
        _issueAggregates = issueAggregates;
        _settings = settings;
        _capabilities = new UserCapabilityResolver(userGroups, hosts, issueOwners);
    }

    public DispatchCandidatePool Build()
    {
        var retentionDays = _settings.Get().RetentionDays;
        var poolGroupIds = _userGroups.GetAll()
            .Where(g => g.Active && g.DispatchPool)
            .Select(g => g.GroupId)
            .ToHashSet();

        var byUserId = new Dictionary<long, DispatchCandidate>();
        foreach (var user in _users.GetAll().Where(u => u.Active))
        {
            if (!_capabilities.Resolve(user).Contains(Capability.Handle)) continue;

            var inPool = user.GroupIds.Any(poolGroupIds.Contains);
            var visible = inPool && !user.DispatchPaused
                ? HostVisibilityResolver.GetVisibleHostIds(_hosts, _users, _userGroups, _access, user.UserId,
                    _issueOwners, _issueAggregates, retentionDays)
                : new HashSet<long>();

            byUserId[user.UserId] = new DispatchCandidate
            {
                UserId = user.UserId,
                Account = user.Account,
                Paused = user.DispatchPaused,
                InPool = inPool,
                VisibleHostIds = visible
            };
        }

        return new DispatchCandidatePool
        {
            ByUserId = byUserId,
            PoolMemberCount = byUserId.Values.Count(c => c.InPool),
            ActivePoolMemberCount = byUserId.Values.Count(c => c.InPool && !c.Paused)
        };
    }
}
