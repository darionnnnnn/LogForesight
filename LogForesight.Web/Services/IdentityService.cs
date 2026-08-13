using LogForesight.Web.Auth;
using LogForesight.Web.Auth.Ldap;
using LogForesight.Web.Configuration;

namespace LogForesight.Web.Services;

/// <summary>登入結果。失敗訊息一律是可直接顯示的中文，且**不區分「查無帳號」與「密碼錯誤」**（避免帳號列舉）</summary>
public record LoginOutcome(bool Success, TokenIdentity? Identity, string? ErrorMessage)
{
    public static LoginOutcome Ok(TokenIdentity identity) => new(true, identity, null);

    public static LoginOutcome Fail(string message) => new(false, null, message);
}

/// <summary>
/// 登入流程的業務層（docs/WEB-SPEC.md §6.2）。
///
/// 職責邊界：**憑證驗證交給 IAuthenticationProvider**（Stub/AD 各自實作），
/// 這裡負責驗證通過之後的事——查使用者、檢查停用、算能力、組出 token 身分。
/// 換驗證方式時這個類別完全不需要修改，這正是把 Provider 抽出去的目的。
/// </summary>
public class IdentityService
{
    private readonly IUserStore _users;
    private readonly IUserGroupStore _groups;
    private readonly IHostStore _hosts;
    private readonly IAuthenticationProvider _provider;
    private readonly ServerAdminAuthenticator _serverAdmin;
    private readonly IAuditService _audit;
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>系統種子群組：名稱可由管理者改（配合公司慣例），但角色與 builtin 旗標不可改</summary>
    private static readonly (string Name, UserRole Role)[] SeedGroups =
    {
        ("admin", UserRole.Admin),
        ("manager", UserRole.Manager),
        ("dev", UserRole.Dev)
    };

    /// <summary>能力解析的單一事實來源（見 <see cref="ResolveCapabilities"/>）</summary>
    private readonly UserCapabilityResolver _capabilities;

    public IdentityService(
        IUserStore users,
        IUserGroupStore groups,
        IHostStore hosts,
        IAuthenticationProvider provider,
        ServerAdminAuthenticator serverAdmin,
        IAuditService audit,
        UserCapabilityResolver capabilities)
    {
        _capabilities = capabilities;
        _users = users;
        _groups = groups;
        _hosts = hosts;
        _provider = provider;
        _serverAdmin = serverAdmin;
        _audit = audit;
    }

    public LoginOutcome Login(string account, string? password)
    {
        account = account.Trim();
        if (string.IsNullOrWhiteSpace(account))
            return LoginOutcome.Fail("請輸入帳號。");

        // serverAdmin 優先比對：它不存在於 lf_users，也不經任何 Provider，
        // 這樣 AD 停擺時仍然進得來（這正是它存在的理由之一）
        var serverAdminResult = _serverAdmin.TryLogin(account, password, _provider.RequiresPassword);
        switch (serverAdminResult)
        {
            case ServerAdminLoginResult.Success:
                _audit.RecordAuth(AuditActions.Login, account, null, "serverAdmin 本地救援帳號登入成功", AuditResult.Ok);
                return LoginOutcome.Ok(new TokenIdentity(
                    UserId: 0,
                    Account: account,
                    DisplayName: $"{account}（系統維護）",
                    Capabilities: RoleCapabilityMap.ForServerAdmin(),
                    IsServerAdmin: true));

            case ServerAdminLoginResult.LockedOut:
                var remaining = _serverAdmin.LockedUntil(account);
                _audit.RecordAuth(AuditActions.LoginFailed, account, null, "serverAdmin 帳號鎖定中，登入被拒", AuditResult.Denied);
                return LoginOutcome.Fail($"此帳號因連續登入失敗已鎖定，請於 {Math.Ceiling(remaining?.TotalMinutes ?? 0)} 分鐘後再試。");

            case ServerAdminLoginResult.WrongPassword:
                _audit.RecordAuth(AuditActions.LoginFailed, account, null, "serverAdmin 密碼錯誤", AuditResult.Failed);
                return LoginOutcome.Fail("帳號或密碼錯誤。");
        }

        // 一般使用者：先驗憑證，再查使用者資料
        var credentials = _provider.Verify(account, password);
        if (!credentials.Success)
        {
            Log.Info("登入失敗（{0}）：{1}", account, credentials.FailureReason);
            _audit.RecordAuth(AuditActions.LoginFailed, account, null,
                $"憑證驗證未通過（{_provider.Name}）", AuditResult.Failed);
            return LoginOutcome.Fail("帳號或密碼錯誤。");
        }

        var user = _users.FindByAccount(account);
        if (user == null)
        {
            // 憑證正確但系統裡沒有這個人：AD 驗證成功卻未被匯入的情況。
            // 訊息要說得夠明確讓對方知道該找誰，但不透露「帳號存不存在」以外的資訊。
            _audit.RecordAuth(AuditActions.LoginFailed, account, null, "帳號未建立於系統中", AuditResult.Failed);
            return LoginOutcome.Fail("此帳號尚未建立於系統中，請聯繫系統管理員。");
        }

        if (!user.Active)
        {
            _audit.RecordAuth(AuditActions.LoginFailed, account, user.UserId, "帳號已停用", AuditResult.Denied);
            return LoginOutcome.Fail("此帳號已停用，請聯繫系統管理員。");
        }

        user = SyncFromAdIfNeeded(user, credentials.UserInfo);

        // 上次登入時間（§3）：只有這一個寫入點。serverAdmin 不經這裡（它不在 lf_users）
        _users.TouchLogin(user.UserId, DateTime.Now);

        var capabilities = ResolveCapabilities(user);
        _audit.RecordAuth(AuditActions.Login, account, user.UserId,
            $"登入成功（{_provider.Name}）", AuditResult.Ok);

        return LoginOutcome.Ok(new TokenIdentity(
            UserId: user.UserId,
            Account: user.Account,
            DisplayName: string.IsNullOrWhiteSpace(user.DisplayName) ? user.Account : user.DisplayName,
            Capabilities: capabilities,
            IsServerAdmin: false));
    }

    /// <summary>
    /// AD 登入自動補顯示名稱與 Email（docs/archive/HISTORY.md #8）：批次新增使用者
    /// （#7）只填帳號，顯示名稱預設＝帳號、Email 留空；使用者第一次以 AD 登入時，
    /// 用同一次驗證取得的 AD 屬性補齊這兩個欄位。
    ///
    /// 只補「視同未填」的欄位——DisplayName 為空或等於帳號本身（批次新增的預設值）、
    /// Email 為 null。使用者已手動填過的值一律尊重、不覆寫。已知取捨：使用者若手動把
    /// 顯示名稱改成與帳號完全相同的字串，會被 AD 值覆寫——機率低、影響小，接受（見計畫書）。
    ///
    /// 只在登入當下同步，不做背景批次同步：沒有服務帳號，設計上就拿不到別人的資料，
    /// 這也省掉保管服務帳號密碼的整包問題（見 credentials.UserInfo 的取得方式）。
    /// </summary>
    private WebUser SyncFromAdIfNeeded(WebUser user, LdapUserInfo? adInfo)
    {
        if (adInfo == null) return user;

        var displayNameUnfilled = string.IsNullOrWhiteSpace(user.DisplayName) ||
            string.Equals(user.DisplayName, user.Account, StringComparison.OrdinalIgnoreCase);
        var newDisplayName = displayNameUnfilled && !string.IsNullOrWhiteSpace(adInfo.DisplayName)
            ? adInfo.DisplayName
            : null;

        var newEmail = user.Email == null && !string.IsNullOrWhiteSpace(adInfo.Email)
            ? adInfo.Email
            : null;

        if (newDisplayName == null && newEmail == null) return user;

        var beforeDisplayName = user.DisplayName;
        var beforeEmail = user.Email;

        if (newDisplayName != null) user.DisplayName = newDisplayName;
        if (newEmail != null) user.Email = newEmail;
        var saved = _users.Upsert(user);

        // 登入流程此時尚未建立已登入身分（Cookie 要到 Controller 回應才寫入），
        // 用 RecordAuth（明確指定帳號／UserId）而非 Record（會讀「目前登入者」，此刻是 anonymous）
        var changes = new List<string>();
        if (newDisplayName != null) changes.Add($"顯示名稱「{beforeDisplayName}」→「{saved.DisplayName}」");
        if (newEmail != null) changes.Add($"Email「{beforeEmail ?? "(無)"}」→「{saved.Email}」");

        _audit.RecordAuth(
            AuditActions.UserUpdate, saved.Account, saved.UserId,
            $"AD 登入自動同步：{string.Join("、", changes)}", AuditResult.Ok);

        return saved;
    }

    /// <summary>
    /// 使用者的能力集合（登入時算進 cap claim）。
    ///
    /// 規則本身住在 <see cref="UserCapabilityResolver"/>——那是「這個人能做什麼」的
    /// 單一事實來源，因為同一條規則另有兩個呼叫點（使用者詳細頁的能力顯示、
    /// 指派前的「對方動得了嗎」檢查，體檢 H1）。這裡保留同名方法只為不動既有呼叫端。
    /// </summary>
    public IReadOnlySet<Capability> ResolveCapabilities(WebUser user) => _capabilities.Resolve(user);

    public void EnsureSeedGroups()
    {
        foreach (var (name, role) in SeedGroups)
        {
            var existing = _groups.GetAll().FirstOrDefault(g => g.Builtin && g.Role == role);
            if (existing != null) continue;

            _groups.Upsert(new UserGroup
            {
                GroupName = name,
                Role = role,
                Builtin = true,
                Active = true
            });
            Log.Info("建立系統種子群組：{0}（角色 {1}）", name, role);
        }
    }

    /// <summary>
    /// 測試模式（Auth:Provider=Stub，僅非 Production）的開箱 seed（§1，回饋第九輪）：建立一個
    /// admin 群組成員，Stub 不驗密碼即可直接登入測全站功能——避免「只能以 serverAdmin 登入、
    /// 卻只看得到維護頁」的落差（serverAdmin 依 §6.2 刻意只有維護＋稽核，不看業務資料）。
    /// 冪等：已存在同帳號即跳過；不動 §6.2 權限模型，正式環境（Production）不呼叫本方法。
    /// </summary>
    public void SeedTestAdmin(string account, string displayName)
    {
        if (_users.FindByAccount(account) != null) return;

        var adminGroup = _groups.GetAll().FirstOrDefault(g => g.Role == UserRole.Admin && g.Active);
        if (adminGroup == null)
        {
            Log.Warn("測試 admin seed 略過：找不到啟用中的 admin 群組（EnsureSeedGroups 應先執行）。");
            return;
        }

        _users.Upsert(new WebUser
        {
            Account = account,
            DisplayName = displayName,
            Active = true,
            GroupIds = new List<long> { adminGroup.GroupId }
        });
        Log.Info("測試模式：已建立開箱測試管理員 {0}（{1}），可直接登入測試全站功能。", displayName, account);
    }

    // 解析邏輯抽至 AdminMembersResolver（回饋十八輪批次G）：上報通知的收件人來源
    // （Singleton 的 MailNotificationService）也要「找出全部 admin 成員」，共用同一份判定
    public bool HasNoAdmins() => AdminMembersResolver.GetAdminMembers(_groups, _users).Count == 0;
}
