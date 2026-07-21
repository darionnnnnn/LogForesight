namespace LogForesight;

/// <summary>
/// 使用者的讀寫（↔ lf_users ＋ lf_user_group_members）。
///
/// 介面語意即規格（DB-PLAN 一致性機制 #2）——JSONL 與 SQL 實作都必須符合下列約定，
/// 合約測試會對兩個後端跑同一組案例：
/// - <see cref="FindByAccount"/> 的帳號比對**不分大小寫**（AD 帳號本來就不分，
///   使用者輸入 domain\user 或 DOMAIN\USER 必須是同一個人）
/// - <see cref="Upsert"/> 以 <c>Account</c> 為自然鍵：存在則更新、不存在則新增並配發 UserId
/// - 停用（Active=false）不刪除資料，避免處理歷程的 handler 指向不存在的使用者
///
/// 方法為同步：與 Core 既有的 IAnalysisRecordStore 等介面一致；EF Core 對 SQL 後端
/// 同樣提供同步 API，不影響後端可替換性。
/// </summary>
public interface IUserStore
{
    List<WebUser> GetAll();

    WebUser? Get(long userId);

    /// <summary>依帳號查詢（不分大小寫）；找不到回 null</summary>
    WebUser? FindByAccount(string account);

    /// <summary>依 Account 自然鍵新增或更新，回傳寫入後的完整物件（含配發的 UserId）</summary>
    WebUser Upsert(WebUser user);

    /// <summary>整組取代某使用者的群組成員資格（CSV 匯入的 groups 欄語意）</summary>
    void SetGroups(long userId, IEnumerable<long> groupIds);
}

/// <summary>
/// 使用者群組的讀寫（↔ lf_user_groups）。
/// 「builtin 群組不可刪除、不可改角色」是**業務規則，由 Service 層強制**，不寫在這裡——
/// Repository 只負責存取（docs/WEB-SPEC.md §4.2 的分層責任邊界）。
/// </summary>
public interface IUserGroupStore
{
    List<UserGroup> GetAll();

    UserGroup? Get(long groupId);

    /// <summary>依群組名稱查詢（不分大小寫）；CSV 匯入自動建立群組時用來判斷是否已存在</summary>
    UserGroup? FindByName(string groupName);

    /// <summary>依 GroupId 更新；GroupId 為 0 時新增並配發</summary>
    UserGroup Upsert(UserGroup group);

    void Delete(long groupId);
}
