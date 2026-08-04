using LogForesight.Web.Auth;
using LogForesight.Web.Auth.Ldap;
using LogForesight.Web.Configuration;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 登入流程的業務規則（docs/WEB-SPEC.md §12）。
/// 用假的 store 與稽核，不碰檔案系統也不需要 HTTP 管線——
/// 這正是把 ICurrentUser／IAuthenticationProvider 抽成介面換來的可測試性。
/// </summary>
public class IdentityServiceTests
{
    private const string ServerAdminAccount = "svc-lfadmin";
    private const string ServerAdminPassword = "Test-Only-P@ssw0rd";

    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly RecordingAuditService _audit = new();

    private IdentityService Create(IAuthenticationProvider? provider = null)
    {
        var settings = new WebAppSettings
        {
            Auth = new AuthSettings
            {
                ServerAdmin = new ServerAdminSettings
                {
                    Account = ServerAdminAccount,
                    PasswordHash = PasswordHasher.Hash(ServerAdminPassword)
                }
            }
        };

        return new IdentityService(
            _users, _groups,
            provider ?? new StubAuthenticationProvider(),
            new ServerAdminAuthenticator(settings),
            _audit);
    }

    [Fact]
    public void EnsureSeedGroups_建立三個內建群組且可重複執行()
    {
        var service = Create();

        service.EnsureSeedGroups();
        service.EnsureSeedGroups();   // 再跑一次不應重複建立

        var groups = _groups.GetAll();
        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.True(g.Builtin));
        Assert.Contains(groups, g => g.Role == UserRole.Admin);
        Assert.Contains(groups, g => g.Role == UserRole.Manager);
        Assert.Contains(groups, g => g.Role == UserRole.Dev);
    }

    [Fact]
    public void Login_serverAdmin正確密碼_取得最小授權()
    {
        var outcome = Create().Login(ServerAdminAccount, ServerAdminPassword);

        Assert.True(outcome.Success);
        Assert.True(outcome.Identity!.IsServerAdmin);
        Assert.Equal(0, outcome.Identity.UserId);
        Assert.Equal(RoleCapabilityMap.ForServerAdmin(), outcome.Identity.Capabilities);
    }

    /// <summary>serverAdmin 不需要存在於 lf_users——這正是它能在 AD 停擺時救援的原因</summary>
    [Fact]
    public void Login_serverAdmin_不需存在於使用者清單()
    {
        Assert.Empty(_users.GetAll());

        Assert.True(Create().Login(ServerAdminAccount, ServerAdminPassword).Success);
    }

    /// <summary>
    /// Stub（測試）模式下 serverAdmin 免密碼登入——與一般帳號一致。
    /// 預設的 <see cref="StubAuthenticationProvider"/> 的 RequiresPassword 為 false，
    /// 測試模式「一律免密碼」對所有帳號（含救援帳號）一致。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("隨便打的錯誤密碼")]
    public void Login_Stub模式_serverAdmin任意密碼都能登入(string? anyPassword)
    {
        // Stub 免密碼的定義是後端不比對密碼值：留空、錯誤密碼都通過
        var outcome = Create().Login(ServerAdminAccount, anyPassword);

        Assert.True(outcome.Success);
        Assert.True(outcome.Identity!.IsServerAdmin);
    }

    /// <summary>需驗密碼的 Provider（如正式環境 Ldap）下，serverAdmin 仍必須提供正確密碼</summary>
    [Fact]
    public void Login_需密碼模式_serverAdmin空密碼被拒()
    {
        var outcome = Create(new AlwaysFailProvider()).Login(ServerAdminAccount, null);

        Assert.False(outcome.Success);
        Assert.Equal("帳號或密碼錯誤。", outcome.ErrorMessage);
    }

    [Fact]
    public void Login_未建立的帳號_失敗且訊息指向管理員()
    {
        var outcome = Create().Login("DOMAIN\\nobody", null);

        Assert.False(outcome.Success);
        Assert.Contains("尚未建立", outcome.ErrorMessage);
    }

    [Fact]
    public void Login_停用帳號_被拒()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", Active = false });

        var outcome = Create().Login("DOMAIN\\wang", null);

        Assert.False(outcome.Success);
        Assert.Contains("停用", outcome.ErrorMessage);
    }

    [Fact]
    public void Login_憑證驗證失敗_不透露帳號是否存在()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang" });

        var outcome = Create(new AlwaysFailProvider()).Login("DOMAIN\\wang", "wrong");

        Assert.False(outcome.Success);
        // 與「查無此帳號」的訊息刻意不同層次：這裡只說帳密錯誤，不確認帳號存在
        Assert.Equal("帳號或密碼錯誤。", outcome.ErrorMessage);
    }

    [Fact]
    public void Login_成功_依群組取得能力並寫入稽核()
    {
        var service = Create();
        service.EnsureSeedGroups();
        var adminGroup = _groups.GetAll().First(g => g.Role == UserRole.Admin);
        _users.Upsert(new WebUser { Account = "DOMAIN\\chang", GroupIds = new List<long> { adminGroup.GroupId } });

        var outcome = service.Login("DOMAIN\\chang", null);

        Assert.True(outcome.Success);
        Assert.Contains(Capability.Maintain, outcome.Identity!.Capabilities);
        Assert.Contains(_audit.Entries, e => e.Action == AuditActions.Login && e.Result == AuditResult.Ok);
    }

    [Fact]
    public void Login_失敗_寫入登入失敗稽核()
    {
        Create().Login("DOMAIN\\nobody", null);

        Assert.Contains(_audit.Entries, e => e.Action == AuditActions.LoginFailed);
    }

    /// <summary>停用群組是「暫時收回這批人的權限」的手段，成員資格還在也不該給能力</summary>
    [Fact]
    public void ResolveCapabilities_停用的群組_不給予能力()
    {
        var service = Create();
        service.EnsureSeedGroups();
        var adminGroup = _groups.GetAll().First(g => g.Role == UserRole.Admin);
        adminGroup.Active = false;
        _groups.Upsert(adminGroup);

        var user = _users.Upsert(new WebUser { Account = "DOMAIN\\chang", GroupIds = new List<long> { adminGroup.GroupId } });

        Assert.Empty(service.ResolveCapabilities(user));
    }

    [Fact]
    public void HasNoAdmins_無成員時為true_指派後為false()
    {
        var service = Create();
        service.EnsureSeedGroups();
        Assert.True(service.HasNoAdmins());

        var adminGroup = _groups.GetAll().First(g => g.Role == UserRole.Admin);
        _users.Upsert(new WebUser { Account = "DOMAIN\\chang", GroupIds = new List<long> { adminGroup.GroupId } });

        Assert.False(service.HasNoAdmins());
    }

    [Fact]
    public void HasNoAdmins_admin成員已停用_視為沒有管理員()
    {
        var service = Create();
        service.EnsureSeedGroups();
        var adminGroup = _groups.GetAll().First(g => g.Role == UserRole.Admin);
        _users.Upsert(new WebUser { Account = "DOMAIN\\chang", Active = false, GroupIds = new List<long> { adminGroup.GroupId } });

        Assert.True(service.HasNoAdmins());
    }

    private class AlwaysFailProvider : IAuthenticationProvider
    {
        public string Name => "AlwaysFail";
        public bool RequiresPassword => true;
        public CredentialCheckResult Verify(string account, string? password) =>
            CredentialCheckResult.Fail("測試用：一律失敗");
    }

    // ── docs/archive/HISTORY.md #8：AD 登入自動補顯示名稱與 Email ──────────────

    private class FixedResultProvider : IAuthenticationProvider
    {
        public string Name => "Fixed";
        public bool RequiresPassword => false;
        public CredentialCheckResult Result { get; set; } = CredentialCheckResult.Ok;
        public CredentialCheckResult Verify(string account, string? password) => Result;
    }

    private static LdapUserInfo AdUser(string displayName, string? email) =>
        new("wang", displayName, email, "CN=wang,DC=corp,DC=local", IsLocked: false);

    [Fact]
    public void Login_AD補資料_顯示名稱等於帳號且Email為null時自動補上()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "DOMAIN\\wang", Email = null });
        var provider = new FixedResultProvider { Result = new CredentialCheckResult(true, UserInfo: AdUser("王小明", "wang@corp.local")) };

        var outcome = Create(provider).Login("DOMAIN\\wang", "any");

        Assert.True(outcome.Success);
        var saved = _users.FindByAccount("DOMAIN\\wang")!;
        Assert.Equal("王小明", saved.DisplayName);
        Assert.Equal("wang@corp.local", saved.Email);
        Assert.Contains(_audit.Entries, e => e.Action == AuditActions.UserUpdate && e.Summary.Contains("AD 登入自動同步"));
    }

    /// <summary>DisplayName 為空（從未填過）與等於帳號（批次新增的預設值）都視同未填，一併補上</summary>
    [Fact]
    public void Login_AD補資料_顯示名稱為空字串時也視同未填()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "", Email = null });
        var provider = new FixedResultProvider { Result = new CredentialCheckResult(true, UserInfo: AdUser("王小明", "wang@corp.local")) };

        Create(provider).Login("DOMAIN\\wang", "any");

        Assert.Equal("王小明", _users.FindByAccount("DOMAIN\\wang")!.DisplayName);
    }

    [Fact]
    public void Login_已手動填過的顯示名稱與Email_不被AD覆寫()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "自訂顯示名稱", Email = "existing@corp.local" });
        var provider = new FixedResultProvider { Result = new CredentialCheckResult(true, UserInfo: AdUser("AD上的名字", "ad@corp.local")) };

        Create(provider).Login("DOMAIN\\wang", "any");

        var saved = _users.FindByAccount("DOMAIN\\wang")!;
        Assert.Equal("自訂顯示名稱", saved.DisplayName);
        Assert.Equal("existing@corp.local", saved.Email);
        Assert.DoesNotContain(_audit.Entries, e => e.Summary.Contains("AD 登入自動同步"));
    }

    /// <summary>AD 查詢失敗（Provider 驗證成功但查不到使用者屬性，UserInfo=null）不影響登入本身——補資料是加值不是門檻</summary>
    [Fact]
    public void Login_AD查詢失敗_UserInfo為null時仍登入成功且不動原資料()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "DOMAIN\\wang", Email = null });
        var provider = new FixedResultProvider { Result = new CredentialCheckResult(true, UserInfo: null) };

        var outcome = Create(provider).Login("DOMAIN\\wang", "any");

        Assert.True(outcome.Success);
        var saved = _users.FindByAccount("DOMAIN\\wang")!;
        Assert.Equal("DOMAIN\\wang", saved.DisplayName);
        Assert.Null(saved.Email);
        Assert.DoesNotContain(_audit.Entries, e => e.Summary.Contains("AD 登入自動同步"));
    }

    [Fact]
    public void Login_AD只提供Email沒有顯示名稱_只補Email()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "自訂顯示名稱", Email = null });
        var provider = new FixedResultProvider { Result = new CredentialCheckResult(true, UserInfo: AdUser("", "wang@corp.local")) };

        Create(provider).Login("DOMAIN\\wang", "any");

        var saved = _users.FindByAccount("DOMAIN\\wang")!;
        Assert.Equal("自訂顯示名稱", saved.DisplayName);
        Assert.Equal("wang@corp.local", saved.Email);
    }
}
