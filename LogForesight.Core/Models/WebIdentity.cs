namespace LogForesight;

/// <summary>
/// 使用者群組的角色。角色掛在**群組**上而不是使用者上（docs/WEB-SPEC.md §7.1）：
/// 一個人可屬多個群組（跨部門），能力取各群組的**聯集**、不是取最高——
/// dev 不是「比 manager 小的角色」，四者不構成一條直線，能力表才是事實來源
/// （能力對應見 Web 專案的 RoleCapabilityMap）。
/// </summary>
public enum UserRole
{
    /// <summary>一般使用者：只能看被授權的主機群組，可維護處理狀態</summary>
    User,

    /// <summary>開發人員：可看執行監控頁與全部主機的業務資料（唯讀）</summary>
    Dev,

    /// <summary>主管：可看全部主機的業務資料（唯讀）</summary>
    Manager,

    /// <summary>系統管理員：全部資料＋全部維護功能＋指派處理人</summary>
    Admin
}

/// <summary>
/// Web 使用者（↔ lf_users）。驗證交給 AD/SSO，本模型只做對應與授權。
/// <see cref="Account"/> 是自然鍵（CSV 匯入的 upsert 依據），不是 <see cref="UserId"/>。
/// </summary>
public class WebUser
{
    public long UserId { get; set; }

    /// <summary>AD 帳號，自然鍵。比對一律不分大小寫（AD 帳號本來就不分）</summary>
    public string Account { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Email { get; set; }

    /// <summary>false = 停用。停用不刪除（處理歷程的 handler 還指著它），且停用即時生效（§6.3）</summary>
    public bool Active { get; set; } = true;

    /// <summary>
    /// 所屬使用者群組（↔ lf_user_group_members）。JSONL 後端內嵌於本物件，
    /// SQL 後端是獨立關聯表——store 介面隱藏這個差異，呼叫端看到的都是這份清單。
    /// </summary>
    public List<long> GroupIds { get; set; } = new();
}

/// <summary>
/// 使用者群組（↔ lf_user_groups）。部門（OO部門）與角色群組（admin/manager）都是群組，
/// 差別只在 <see cref="Role"/> 與 <see cref="Builtin"/>。
/// </summary>
public class UserGroup
{
    public long GroupId { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>
    /// true = 系統種子群組（admin / manager / dev）：不可刪除、不可改 Role。
    /// 這三個群組的存在是權限模型的地基，允許改名以配合公司慣例，但改掉角色或刪掉，
    /// 整套授權就沒有依據了。
    /// </summary>
    public bool Builtin { get; set; }

    public bool Active { get; set; } = true;
}
