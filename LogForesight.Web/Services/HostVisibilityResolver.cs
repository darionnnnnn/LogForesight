using LogForesight.Web.Auth;
using LogForesight.Core.Persistence;

namespace LogForesight.Web.Services;

/// <summary>
/// 「指定使用者看得到哪些主機」的純函式核心（回饋十七輪批次B-4 自 <see cref="VisibilityService"/>
/// 抽出）。群組授權 ∪ 主機負責人 ∪ 問題負責人的規則只在這裡有一份：
/// <see cref="VisibilityService"/> 的「目前登入者」與 <c>*For</c> 三個方法、
/// <see cref="Mail.MailNotificationService"/> 的收件人過濾、派工候選都委派到此。
/// 「目前登入者」入口另帶 ViewAll（由呼叫端依 claim 提供）與問題負責人集合提供者（讓
/// <see cref="VisibilityService"/> 繼續用跨請求快取）兩個參數；<see cref="VisibilityService"/>
/// 依賴 Scoped 的 <c>ICurrentUser</c> 無法被 Singleton 消費，所以規則放在這個純函式類別。
/// 兩條入口的等價性由 VisibilityPathEquivalenceTests 守門（唯一刻意差異：已停用使用者帶 ViewAll claim）。
/// 全部方法只依賴 Singleton 的 Store，沒有 captive dependency 疑慮。
/// </summary>
internal static class HostVisibilityResolver
{
    public static IReadOnlySet<long> GetOwnedHostIds(IHostStore hosts, IUserStore users, long userId)
        => GetOwnedHostIds(hosts.CapturePrtgSnapshot(), users, userId);

    public static IReadOnlySet<long> GetOwnedHostIds(PrtgHostSnapshot hosts, IUserStore users, long userId)
    {
        var user = users.Get(userId);
        if (userId <= 0 || user == null || !user.Active) return new HashSet<long>();

        return hosts.Hosts
            .Where(h => h.Active && h.OwnerUserIds.Contains(userId))
            .Select(h => h.HostId)
            .ToHashSet();
    }

    public static IReadOnlySet<long> GetGroupVisibleHostIds(
        IHostStore hosts, IUserStore users, IUserGroupStore userGroups, IGroupAccessStore access, long userId)
        => GetGroupVisibleHostIds(hosts.CapturePrtgSnapshot(), users, userGroups, access, userId);

    public static IReadOnlySet<long> GetGroupVisibleHostIds(
        PrtgHostSnapshot hosts, IUserStore users, IUserGroupStore userGroups, IGroupAccessStore access, long userId)
        => GetGroupVisibleHostIds(hosts, users, userGroups, access, userId, viewAll: null);

    /// <param name="viewAll">null＝由所屬啟用群組的角色即時算；非 null＝由呼叫端提供（目前登入者以 JWT claim 為準）</param>
    private static IReadOnlySet<long> GetGroupVisibleHostIds(
        PrtgHostSnapshot hosts, IUserStore users, IUserGroupStore userGroups, IGroupAccessStore access, long userId,
        bool? viewAll)
    {
        var user = users.Get(userId);
        if (user == null || !user.Active) return new HashSet<long>();

        // 對象持有 ViewAll（dev/manager/admin 群組）時看得到全部主機——能力來自所屬群組的
        // 角色聯集，與登入時解析 cap claim 的規則同一套（RoleCapabilityMap）
        var activeGroups = userGroups.GetAll()
            .Where(g => g.Active && user.GroupIds.Contains(g.GroupId))
            .ToList();

        var allHosts = hosts.Hosts.Where(h => h.Active).ToList();

        if (viewAll ?? RoleCapabilityMap.For(activeGroups.Select(g => g.Role)).Contains(Capability.ViewAll))
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

    /// <summary>
    /// 問題負責人可見的主機（回饋十八輪批次F，第四條授權路徑）：保留期內出現過其負責問題
    /// （(Source,EventId) 規則）的存活主機——與主機負責人（整台可見）同級，不是案件授與
    /// 那種問題層級的窄授與；理由同主機負責人：問題負責人是這個問題歸屬的第一手資料，
    /// 不該還要另外去授權矩陣補一刀。窗口用 <paramref name="retentionDays"/>（與歷史紀錄保留
    /// 天數一致——保留多久的紀錄，就讓負責人看多久的可見範圍，兩者天然同步）。
    /// </summary>
    public static IReadOnlySet<long> GetIssueOwnedHostIds(
        IHostStore hosts, IIssueOwnerStore issueOwners, IUserStore users, IIssueAggregateQuery issueAggregates,
        long userId, int retentionDays)
        => GetIssueOwnedHostIds(hosts.CapturePrtgSnapshot(), issueOwners, users, issueAggregates, userId, retentionDays);

    public static IReadOnlySet<long> GetIssueOwnedHostIds(
        PrtgHostSnapshot hosts, IIssueOwnerStore issueOwners, IUserStore users, IIssueAggregateQuery issueAggregates,
        long userId, int retentionDays)
    {
        var user = users.Get(userId);
        if (userId <= 0 || user == null || !user.Active) return new HashSet<long>();

        var owned = issueOwners.GetAll()
            .Where(r => r.OwnerUserIds.Contains(userId))
            .Select(r => (Source: WorkOrderIssueKey.SourceKeyOf(r.SourceName), r.EventId))
            .ToList();
        if (owned.Count == 0) return new HashSet<long>();

        var to = DateTime.Today;
        var from = to.AddDays(-(Math.Max(retentionDays, 1) - 1));
        // 靜音不排除：授權範圍不隨顯示決定變動——問題負責人對靜音中的問題仍要看得到主機，才能判斷何時解除
        var hostIds = issueAggregates.HostIdsFor(IssueExclusion.None, owned, from, to);
        if (hostIds.Count == 0) return hostIds;

        // 停用主機排除（回饋十八輪體檢輪修正）：與 GetOwnedHostIds／GetGroupVisibleHostIds 同一條規則
        // ——這裡原本漏過濾，停用主機只要保留期內出現過負責的問題就會被算進可見範圍，
        // 與本方法自己的文件註解（「保留期內出現過其負責問題的『存活』主機」）矛盾。
        var activeHostIds = hosts.Hosts.Where(h => h.Active).Select(h => h.HostId).ToHashSet();
        return hostIds.Where(activeHostIds.Contains).ToHashSet();
    }

    /// <summary>群組授權 ∪ 主機負責人 ∪ 問題負責人（docs/archive/FEEDBACK-11-PLAN.md §2b，
    /// 回饋十八輪批次F 新增第三條聯集）。</summary>
    public static IReadOnlySet<long> GetVisibleHostIds(
        IHostStore hosts, IUserStore users, IUserGroupStore userGroups, IGroupAccessStore access, long userId,
        IIssueOwnerStore? issueOwners, IIssueAggregateQuery? issueAggregates, int retentionDays)
        => GetVisibleHostIds(hosts.CapturePrtgSnapshot(), users, userGroups, access, userId,
            issueOwners, issueAggregates, retentionDays);

    public static IReadOnlySet<long> GetVisibleHostIds(
        PrtgHostSnapshot hosts, IUserStore users, IUserGroupStore userGroups, IGroupAccessStore access, long userId,
        IIssueOwnerStore? issueOwners, IIssueAggregateQuery? issueAggregates, int retentionDays)
    {
        var visible = GetGroupVisibleHostIds(hosts, users, userGroups, access, userId).ToHashSet();
        visible.UnionWith(GetOwnedHostIds(hosts, users, userId));
        if (issueOwners != null && issueAggregates != null)
        {
            visible.UnionWith(GetIssueOwnedHostIds(hosts, issueOwners, users, issueAggregates, userId, retentionDays));
        }
        return visible;
    }

    /// <summary>
    /// 「目前登入者」入口：規則同上，差別在 ViewAll 與問題負責人集合由呼叫端提供。
    /// <paramref name="viewAll"/> 以呼叫端的 claim 為準（不依角色重算）；<paramref name="issueOwnedHostIds"/>
    /// 為 null 代表這條路徑未啟用（測試組裝沒注入問題負責人依賴）。回傳已收斂在存活主機內。
    /// </summary>
    public static IReadOnlySet<long> GetVisibleHostIds(
        PrtgHostSnapshot hosts, IUserStore users, IUserGroupStore userGroups, IGroupAccessStore access, long userId,
        bool viewAll, Func<IReadOnlySet<long>>? issueOwnedHostIds)
    {
        var visible = GetGroupVisibleHostIds(hosts, users, userGroups, access, userId, viewAll).ToHashSet();
        visible.UnionWith(GetOwnedHostIds(hosts, users, userId));
        if (issueOwnedHostIds != null) visible.UnionWith(issueOwnedHostIds());

        // 問題負責人集合可能來自跨請求快取：收斂在這份快照的存活主機內
        visible.IntersectWith(hosts.Hosts.Where(h => h.Active).Select(h => h.HostId));
        return visible;
    }
}
