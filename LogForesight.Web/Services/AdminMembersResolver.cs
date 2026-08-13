namespace LogForesight.Web.Services;

/// <summary>
/// 「admin 群組成員」的單一解析出口（回饋十八輪批次G）：判定必須用
/// <see cref="UserGroup.Role"/> == Admin，**不能**用群組名稱字串——群組可以改名，
/// 名稱比對會在改名後靜默失效。純靜態函式（比照 <see cref="HostVisibilityResolver"/> 的
/// 架構理由）：Singleton 的 MailNotificationService（上報通知的收件人來源）與 Scoped 的
/// IdentityService（HasNoAdmins）都要用，不能讓 Singleton 依賴 Scoped 服務。
/// </summary>
public static class AdminMembersResolver
{
    /// <summary>啟用中 admin 群組（Role==Admin 且 Active）的全部啟用中成員；
    /// 沒有任何 admin 群組或成員時回空清單。</summary>
    public static List<WebUser> GetAdminMembers(IUserGroupStore groups, IUserStore users)
    {
        var adminGroupIds = groups.GetAll()
            .Where(g => g.Role == UserRole.Admin && g.Active)
            .Select(g => g.GroupId)
            .ToHashSet();

        if (adminGroupIds.Count == 0) return new List<WebUser>();

        return users.GetAll()
            .Where(u => u.Active && u.GroupIds.Any(adminGroupIds.Contains))
            .ToList();
    }
}
