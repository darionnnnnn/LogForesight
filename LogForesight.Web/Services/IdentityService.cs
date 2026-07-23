using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;

namespace LogForesight.Web.Services;

/// <summary>登入結果。失敗訊息一律是可直接顯示的中文，且**不區分「查無帳號」與「密碼錯誤」**（避免帳號列舉）</summary>
public record LoginOutcome(bool Success, TokenIdentity? Identity, string? ErrorMessage)
{
    public static LoginOutcome Ok(TokenIdentity identity) => new(true, identity, null);

    public static LoginOutcome Fail(string message) => new(false, null, message);
}

public interface IIdentityService
{
    /// <summary>驗證帳密並解析出可簽發 token 的身分</summary>
    LoginOutcome Login(string account, string? password);

    /// <summary>某使用者依其所屬群組取得的能力（多群組取聯集）</summary>
    IReadOnlySet<Capability> ResolveCapabilities(WebUser user);

    /// <summary>建立系統種子群組（admin / manager / dev），已存在則不動</summary>
    void EnsureSeedGroups();

    /// <summary>目前是否還沒有任何 admin 群組成員（首次部署的引導狀態）</summary>
    bool HasNoAdmins();
}

/// <summary>
/// 登入流程的業務層（docs/WEB-SPEC.md §6.2）。
///
/// 職責邊界：**憑證驗證交給 IAuthenticationProvider**（Stub/AD 各自實作），
/// 這裡負責驗證通過之後的事——查使用者、檢查停用、算能力、組出 token 身分。
/// 換驗證方式時這個類別完全不需要修改，這正是把 Provider 抽出去的目的。
/// </summary>
public class IdentityService : IIdentityService
{
    private readonly IUserStore _users;
    private readonly IUserGroupStore _groups;
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

    public IdentityService(
        IUserStore users,
        IUserGroupStore groups,
        IAuthenticationProvider provider,
        ServerAdminAuthenticator serverAdmin,
        IAuditService audit)
    {
        _users = users;
        _groups = groups;
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

    public IReadOnlySet<Capability> ResolveCapabilities(WebUser user)
    {
        var allGroups = _groups.GetAll();

        // 停用的群組不給能力：停用群組是「暫時收回這批人的權限」的手段，
        // 如果成員資格還在就照給，那個手段等於沒用。
        var roles = allGroups
            .Where(g => g.Active && user.GroupIds.Contains(g.GroupId))
            .Select(g => g.Role)
            .ToList();

        return RoleCapabilityMap.For(roles);
    }

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

    public bool HasNoAdmins()
    {
        var adminGroupIds = _groups.GetAll()
            .Where(g => g.Role == UserRole.Admin && g.Active)
            .Select(g => g.GroupId)
            .ToHashSet();

        if (adminGroupIds.Count == 0) return true;

        return !_users.GetAll().Any(u => u.Active && u.GroupIds.Any(adminGroupIds.Contains));
    }
}
