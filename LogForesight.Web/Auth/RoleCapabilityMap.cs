namespace LogForesight.Web.Auth;

/// <summary>
/// 角色 → 能力的對應（docs/WEB-SPEC.md §7.1）。**權限模型的單一事實來源**：
/// 要調整某個角色能做什麼，只改這個檔案，不必去翻每個 Controller 的屬性標註。
///
/// 多群組成員資格取**能力聯集**，不是「取最高角色」——dev 與 manager 沒有高低之分
/// （dev 有執行監控、manager 沒有；manager 也沒有 dev 沒有的東西），
/// 用一條直線的角色階層表達不了，聯集才是對的模型。
/// </summary>
public static class RoleCapabilityMap
{
    private static readonly IReadOnlyDictionary<UserRole, Capability[]> Map = new Dictionary<UserRole, Capability[]>
    {
        // 一般使用者：可維護處理狀態與確認權限異動，但**限於授權範圍內的主機**
        // （範圍不在這裡表達，由 IVisibilityService 過濾）；不能指派處理人。
        [UserRole.User] = new[]
        {
            Capability.Handle,
            Capability.ConfirmPermission
        },

        // 開發人員：執行監控是專屬能力；業務資料全域唯讀——查「這台昨天為什麼沒產生紀錄」
        // 時必然要對照該主機的分析結果，只給執行 log 會查到一半卡住。
        [UserRole.Dev] = new[]
        {
            Capability.ViewAll,
            Capability.DevMonitor
        },

        // 主管：純唯讀（2026-07-21 定案）。看得到全部主機的儀表板與報表，不碰處理流程。
        [UserRole.Manager] = new[]
        {
            Capability.ViewAll
        },

        // 系統管理員：全部能力
        [UserRole.Admin] = new[]
        {
            Capability.ViewAll,
            Capability.Handle,
            Capability.Assign,
            Capability.ConfirmPermission,
            Capability.Maintain,
            Capability.DevMonitor,
            Capability.ViewAudit
        }
    };

    /// <summary>單一角色的能力</summary>
    public static IReadOnlySet<Capability> For(UserRole role) =>
        Map.TryGetValue(role, out var caps) ? caps.ToHashSet() : new HashSet<Capability>();

    /// <summary>多個角色（跨群組）的能力聯集</summary>
    public static IReadOnlySet<Capability> For(IEnumerable<UserRole> roles)
    {
        var result = new HashSet<Capability>();
        foreach (var role in roles)
        {
            if (Map.TryGetValue(role, out var caps)) result.UnionWith(caps);
        }
        return result;
    }

    /// <summary>
    /// serverAdmin 本地救援帳號的能力（docs/WEB-SPEC.md §6.2）：
    /// 只有維護與稽核查閱，**沒有任何業務資料檢視**——它的用途是指派 admin 成員與救援，
    /// 依用途給權，不是萬能帳號。
    /// </summary>
    public static IReadOnlySet<Capability> ForServerAdmin() => new HashSet<Capability>
    {
        Capability.Maintain,
        Capability.ViewAudit
    };
}
