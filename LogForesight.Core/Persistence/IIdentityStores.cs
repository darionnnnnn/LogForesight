namespace LogForesight.Core.Persistence;

/// <summary>
/// 使用者的讀寫（↔ lf_users ＋ lf_user_group_members）。
///
/// 介面語意即規格（DB-PLAN 一致性機制 #2）——實作必須符合下列約定，由合約測試強制：
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

    /// <summary>
    /// 管理員編輯視窗的一次儲存（第 81 輪批次 M）：基本資料（顯示名稱、Email、啟用）與群組在**同一個原子寫入**內完成，
    /// 並在這同一個臨界區內比對 <paramref name="expectedVersion"/>（<see cref="UserVersion.Of"/>）。
    /// 帳號已存在而版本空白或與當下不同 → 擲 <see cref="ConcurrentUpdateException"/>、什麼都不寫；
    /// 帳號不存在＝新增，不需版本。<paramref name="groupIds"/> 為 null＝不動群組（沿用伺服器當下的值，
    /// 不以開啟視窗時的快照覆蓋）。其餘欄位的保留規則與 <see cref="Upsert"/> 相同。
    /// </summary>
    UserSaveResult SaveIfUnchanged(WebUser user, string? expectedVersion, IReadOnlyCollection<long>? groupIds);

    /// <summary>
    /// 記錄一次成功登入的時間（docs/archive/FEEDBACK-11-PLAN.md §3）。
    /// **刻意不併進 <see cref="Upsert"/>**：Upsert 的既存分支逐欄覆寫，而各處建構
    /// <see cref="WebUser"/> 的呼叫端（使用者頁儲存、批次新增）都不會帶這個欄位，
    /// 交給 Upsert 就會在每次編輯使用者時把上次登入時間靜默清掉——
    /// 與 owners.csv 曾漏抄 SentinelId 同一種失敗模式，用意圖精準的方法避開。
    /// 寫到使用者清單之外（見 <see cref="GetLastLogins"/>），**不改使用者清單**：
    /// 每次登入都改寫整份清單會讓使用者快取在登入尖峰反覆失效。
    /// </summary>
    void TouchLogin(long userId, DateTime at);

    /// <summary>
    /// 全部使用者的最後登入時間（userId → 時間）。登入時間存在使用者清單之外，
    /// 升級前寫在 <see cref="WebUser.LastLoginAt"/> 的舊值不在這裡，呼叫端查不到時自行退回該欄。
    /// </summary>
    IReadOnlyDictionary<long, DateTime> GetLastLogins();

    /// <summary>只改暫停接單旗標：Upsert 是逐欄複製且刻意不含這個欄位（見該處註解），
    /// 這裡是它的唯一寫入點</summary>
    void SetDispatchPaused(long userId, bool paused);

    /// <summary>
    /// 遞增工作階段版號並回傳新值（登出用）；找不到使用者回 0。<see cref="Upsert"/> 不覆寫這個欄位
    /// （停用時由 Upsert 內部遞增，見實作），否則編輯使用者會把已登出的 cookie 復活。
    /// </summary>
    long BumpSessionVersion(long userId);

    /// <summary>
    /// 該使用者目前的有效工作階段版號：<see cref="WebUser.SessionVersion"/>（舊資料與停用遞增）＋登出增量。
    /// Cookie 驗證與簽發一律讀這個，不直接讀 <see cref="WebUser.SessionVersion"/>。
    /// </summary>
    long GetSessionVersion(WebUser user);
}

/// <summary><see cref="IUserStore.SaveIfUnchanged"/> 的結果：寫入前的快照（新增時為 null）與寫入後的物件。</summary>
public sealed record UserSaveResult(WebUser? Before, WebUser After);

/// <summary>
/// 單一使用者的版本：管理員編輯視窗涵蓋的欄位（顯示名稱、Email、啟用、群組）的雜湊，不新增欄位。
/// 暫停接單、登入時間等不在編輯視窗內的欄位不參與，不會讓編輯中的人無謂衝突。
/// </summary>
public static class UserVersion
{
    public static string Of(WebUser user)
    {
        var text = string.Join('\n',
            user.DisplayName ?? "", user.Email ?? "", user.Active ? "1" : "0",
            string.Join(',', user.GroupIds.Distinct().OrderBy(id => id)));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text)), 0, 8);
    }

    /// <summary>寫入前快照（只含編輯視窗涵蓋的欄位，供稽核與權限判斷比對）</summary>
    public static WebUser Snapshot(WebUser user) => new()
    {
        UserId = user.UserId, Account = user.Account, DisplayName = user.DisplayName, Email = user.Email,
        Active = user.Active, GroupIds = user.GroupIds.ToList()
    };
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

    /// <summary>只改派工池旗標：Upsert 是逐欄複製且刻意不含這個欄位（見該處註解），
    /// 這裡是它的唯一寫入點</summary>
    void SetDispatchPool(long groupId, bool inPool);

    void Delete(long groupId);
}
