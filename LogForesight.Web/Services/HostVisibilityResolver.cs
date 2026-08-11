using LogForesight.Web.Auth;

namespace LogForesight.Web.Services;

/// <summary>
/// 「指定使用者看得到哪些主機」的純函式核心（回饋十七輪批次B-4 自 <see cref="VisibilityService"/>
/// 抽出）。<see cref="VisibilityService"/> 的 <c>GetGroupVisibleHostIdsFor</c>／
/// <c>GetOwnedHostIdsFor</c>／<c>GetVisibleHostIdsFor</c> 三個方法與
/// <see cref="Mail.MailNotificationService"/> 的收件人可見範圍過濾共用同一份邏輯——
/// 後者是 Singleton，<see cref="VisibilityService"/> 依賴 Scoped 的 <c>ICurrentUser</c>
/// 無法直接被 Singleton 消費，且這條邏輯本來就不需要「目前登入者」（引數是任意 userId），
/// 單點化能避免兩邊各自維護一份而漂移（本專案已踩過多次「欄位漂移」的失敗模式）。
/// 全部方法只依賴 Singleton 的 Store，沒有 captive dependency 疑慮。
/// </summary>
internal static class HostVisibilityResolver
{
    public static IReadOnlySet<long> GetOwnedHostIds(IHostStore hosts, IUserStore users, long userId)
    {
        var user = users.Get(userId);
        if (userId <= 0 || user == null || !user.Active) return new HashSet<long>();

        return hosts.GetAll()
            .Where(h => h.Active && h.OwnerUserIds.Contains(userId))
            .Select(h => h.HostId)
            .ToHashSet();
    }

    public static IReadOnlySet<long> GetGroupVisibleHostIds(
        IHostStore hosts, IUserStore users, IUserGroupStore userGroups, IGroupAccessStore access, long userId)
    {
        var user = users.Get(userId);
        if (user == null || !user.Active) return new HashSet<long>();

        // 對象持有 ViewAll（dev/manager/admin 群組）時看得到全部主機——能力來自所屬群組的
        // 角色聯集，與登入時解析 cap claim 的規則同一套（RoleCapabilityMap）
        var activeGroups = userGroups.GetAll()
            .Where(g => g.Active && user.GroupIds.Contains(g.GroupId))
            .ToList();

        var allHosts = hosts.GetAll().Where(h => h.Active).ToList();

        if (RoleCapabilityMap.For(activeGroups.Select(g => g.Role)).Contains(Capability.ViewAll))
            return allHosts.Select(h => h.HostId).ToHashSet();

        var hostGroupIds = access.GetAll()
            .Where(a => activeGroups.Any(g => g.GroupId == a.UserGroupId))
            .Select(a => a.HostGroupId)
            .ToHashSet();

        return allHosts
            .Where(h => h.GroupIds.Any(hostGroupIds.Contains))
            .Select(h => h.HostId)
            .ToHashSet();
    }

    /// <summary>群組授權 ∪ 負責人（docs/archive/FEEDBACK-11-PLAN.md §2b）。</summary>
    public static IReadOnlySet<long> GetVisibleHostIds(
        IHostStore hosts, IUserStore users, IUserGroupStore userGroups, IGroupAccessStore access, long userId)
    {
        var visible = GetGroupVisibleHostIds(hosts, users, userGroups, access, userId).ToHashSet();
        visible.UnionWith(GetOwnedHostIds(hosts, users, userId));
        return visible;
    }
}
