using LogForesight.Web.Models;

namespace LogForesight.Web.Auth;

/// <summary>
/// 「這個使用者能做什麼」的**單一事實來源**。
///
/// 規則是：**啟用中群組的角色聯集 ∪ 負責人隱含的 User 角色**（docs/archive/FEEDBACK-11-PLAN.md §2b）。
///
/// **為什麼要獨立成一個類別**：這條規則原本散在
/// <see cref="Services.IdentityService"/>（登入時算 cap claim）與
/// <c>UserAdminService.GetUserDetail</c>（使用者詳細頁顯示能力）兩處，
/// 而體檢 H1 需要第三個呼叫點（指派前檢查「對方動不動得了」）。
/// 再抄一份就會有三份會漂移的複本——**體檢 H3 正是這樣壞掉的**：
/// §2b 新增了負責人這條授權路徑，但使用者清單的文案沒跟著改，於是畫面說謊。
///
/// 依賴刻意只有兩個 store：能力解析不該把登入流程（驗證提供者、稽核、
/// serverAdmin 救援帳號）拖進任何想問「他能不能做這件事」的地方。
/// </summary>
public class UserCapabilityResolver
{
    private readonly IUserGroupStore _groups;
    private readonly IHostStore _hosts;

    /// <summary>問題負責人規則（回饋十八輪批次F）：可為 null——測試組裝不注入時，
    /// 問題負責人隱含能力靜默跳過（同 MailNotificationService 的既有慣例）。</summary>
    private readonly IIssueOwnerStore? _issueOwners;

    public UserCapabilityResolver(IUserGroupStore groups, IHostStore hosts, IIssueOwnerStore? issueOwners = null)
    {
        _groups = groups;
        _hosts = hosts;
        _issueOwners = issueOwners;
    }

    /// <summary>
    /// 使用者的能力集合。**不檢查帳號是否停用**——停用的攔截點在登入與
    /// <c>ActiveUserMiddleware</c>（逐請求 401），呼叫端若需要「停用即無能力」的呈現語意
    /// （例如使用者詳細頁），自行先判斷 <c>user.Active</c>。
    /// </summary>
    public IReadOnlySet<Capability> Resolve(WebUser user)
    {
        // 停用的群組不給能力：停用群組是「暫時收回這批人的權限」的手段，
        // 如果成員資格還在就照給，那個手段等於沒用。
        var roles = _groups.GetAll()
            .Where(g => g.Active && user.GroupIds.Contains(g.GroupId))
            .Select(g => g.Role)
            .ToList();

        // 負責人隱含 User 角色能力（§2b）：負責人匯入會自動建立沒有群組的帳號，
        // 沒有這一段的話對方登入後看得到自己負責的主機、卻連處理狀態都標不了
        // （Handle 來自群組角色），被交辦也回覆不了——「有可見範圍無處置能力」是半套。
        // 刻意只補 User 角色（Handle＋ConfirmPermission），**不含 ViewAll**：
        // 負責人看得到的是自己那幾台，不是全站。
        // 問題負責人（回饋十八輪批次F）是相同概念、相同待遇——與主機負責人同一句判斷式，
        // 不另開分支：兩條路徑都是「這個人自動具備處理能力，但不代表能看到全站」。
        if (IsHostOwner(user.UserId) || IsIssueOwner(user.UserId)) roles.Add(UserRole.User);

        return RoleCapabilityMap.For(roles);
    }

    /// <summary>
    /// 是不是任一**啟用中**主機的負責人。停用主機不算——停用主機的資料已整批退出可見範圍
    /// （§7.1），拿它當能力來源會出現「看不到任何東西卻有 Handle」的空殼權限。
    /// </summary>
    private bool IsHostOwner(long userId) =>
        userId > 0 && _hosts.GetAll().Any(h => h.Active && h.OwnerUserIds.Contains(userId));

    /// <summary>是不是任一問題的負責人（回饋十八輪批次F）。問題負責人規則沒有「啟用中」欄位
    /// （不像主機有 Active）——規則本身要嘛存在要嘛刪除，沒有停用態。</summary>
    private bool IsIssueOwner(long userId) =>
        userId > 0 && _issueOwners != null && _issueOwners.GetAll().Any(r => r.OwnerUserIds.Contains(userId));
}
