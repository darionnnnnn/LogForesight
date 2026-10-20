using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 手動派工人選（問題查詢的交辦、交辦單改派／拆出、風險日層級指派共用）。
/// 候選規則與自動派工同一套：啟用且具 Handle 能力（<see cref="UserCapabilityResolver"/>），
/// 可見主機走 <see cref="HostVisibilityResolver"/>，派工池判定同 <see cref="DispatchCandidateSource"/>；
/// 本服務只組合這些規則並附上排序用的資訊，不另寫判定。
/// 與自動派工的差別：自動派工會排除看不到主機的人；手動派工允許選（事後以案件授與補上可見範圍），
/// 所以這裡只回報「看不到幾台」「是否暫停」，由前端標示、不剔除。
/// </summary>
public sealed class WorkOrderCandidateService
{
    private readonly IUserStore _users;
    private readonly IUserGroupStore _userGroups;
    private readonly IHostStore _hosts;
    private readonly IGroupAccessStore _access;
    private readonly IIssueOwnerStore _issueOwners;
    private readonly IIssueAggregateQuery _issueAggregates;
    private readonly ISystemSettingsStore _settings;
    private readonly IIssueCaseStore _cases;
    private readonly IWorkOrderStore _orders;
    private readonly IUserDisplayNameService _displayNames;
    private readonly UserCapabilityResolver _capabilities;

    public WorkOrderCandidateService(
        IUserStore users, IUserGroupStore userGroups, IHostStore hosts, IGroupAccessStore access,
        IIssueOwnerStore issueOwners, IIssueAggregateQuery issueAggregates, ISystemSettingsStore settings,
        IIssueCaseStore cases, IWorkOrderStore orders, IUserDisplayNameService displayNames)
    {
        _users = users;
        _userGroups = userGroups;
        _hosts = hosts;
        _access = access;
        _issueOwners = issueOwners;
        _issueAggregates = issueAggregates;
        _settings = settings;
        _cases = cases;
        _orders = orders;
        _displayNames = displayNames;
        _capabilities = new UserCapabilityResolver(userGroups, hosts, issueOwners);
    }

    public List<WorkOrderCandidateDto> Get(WorkOrderCandidatesRequest req)
    {
        var requested = (req?.HostIds ?? new List<long>()).Distinct().ToList();
        var retentionDays = _settings.Get().RetentionDays;
        var poolGroupIds = _userGroups.GetAll()
            .Where(g => g.Active && g.DispatchPool)
            .Select(g => g.GroupId)
            .ToHashSet();

        var recentHandlers = RecentHandlersOfSameIssue(req);
        var loads = _orders.LoadBoard().ToDictionary(l => l.HandlerId, l => l.ActiveMembers);

        var result = new List<WorkOrderCandidateDto>();
        foreach (var user in _users.GetAll().Where(u => u.Active))
        {
            if (!_capabilities.Resolve(user).Contains(Capability.Handle)) continue;

            var visibleCount = 0;
            if (requested.Count > 0)
            {
                var visible = HostVisibilityResolver.GetVisibleHostIds(_hosts, _users, _userGroups, _access, user.UserId,
                    _issueOwners, _issueAggregates, retentionDays);
                visibleCount = requested.Count(visible.Contains);
            }

            result.Add(new WorkOrderCandidateDto
            {
                UserId = user.UserId,
                Account = user.Account,
                DisplayName = _displayNames.Of(user.DisplayName),
                VisibleHostCount = visibleCount,
                HiddenHostCount = requested.Count == 0 ? 0 : requested.Count - visibleCount,
                Paused = user.DispatchPaused,
                InPool = user.GroupIds.Any(poolGroupIds.Contains),
                RecentlyResolvedSameIssue = recentHandlers.Contains(user.UserId),
                ActiveLoad = loads.TryGetValue(user.UserId, out var load) ? load : 0
            });
        }

        // 最近處理過同問題者優先 → 看得到全部主機者優先 → 負載輕者優先 → 帳號
        return result
            .OrderByDescending(c => c.RecentlyResolvedSameIssue)
            .ThenBy(c => c.HiddenHostCount == 0 ? 0 : 1)
            .ThenBy(c => c.ActiveLoad)
            .ThenBy(c => c.Account, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>近 <see cref="DispatchContext.ContinuityDays"/> 天內結案（resolved）過同一個問題的處理人；資料來源與派工延續性同一支。</summary>
    private HashSet<long> RecentHandlersOfSameIssue(WorkOrderCandidatesRequest? req)
    {
        var handlers = new HashSet<long>();
        if (req == null || string.IsNullOrWhiteSpace(req.Source) || req.EventId == null) return handlers;

        var sourceKey = WorkOrderIssueKey.SourceKeyOf(req.Source);
        foreach (var c in _cases.GetResolvedSince(DateTime.Today.AddDays(-DispatchContext.ContinuityDays)))
        {
            if (c.HandlerId == null || c.SourceName == null || c.EventId != req.EventId) continue;
            if (WorkOrderIssueKey.SourceKeyOf(c.SourceName) == sourceKey) handlers.Add(c.HandlerId.Value);
        }

        return handlers;
    }
}
