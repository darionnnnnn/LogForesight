using LogForesight.Web.Auth;
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
    private readonly FakeAuditService _audit = new();

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
    [Fact]
    public void Login_Stub模式_serverAdmin免密碼也能登入()
    {
        var outcome = Create().Login(ServerAdminAccount, null);

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
}

// ── 測試替身 ─────────────────────────────────────────────────────────────────

internal class FakeUserStore : IUserStore
{
    private readonly List<WebUser> _users = new();
    private long _nextId = 1;

    public List<WebUser> GetAll() => _users.ToList();

    public WebUser? Get(long userId) => _users.FirstOrDefault(u => u.UserId == userId);

    public WebUser? FindByAccount(string account) =>
        _users.FirstOrDefault(u => string.Equals(u.Account, account, StringComparison.OrdinalIgnoreCase));

    public WebUser Upsert(WebUser user)
    {
        var existing = FindByAccount(user.Account);
        if (existing == null)
        {
            user.UserId = _nextId++;
            _users.Add(user);
            return user;
        }

        existing.DisplayName = user.DisplayName;
        existing.Email = user.Email;
        existing.Active = user.Active;
        existing.GroupIds = user.GroupIds;
        return existing;
    }

    public void SetGroups(long userId, IEnumerable<long> groupIds)
    {
        var user = Get(userId);
        if (user != null) user.GroupIds = groupIds.Distinct().ToList();
    }
}

internal class FakeUserGroupStore : IUserGroupStore
{
    private readonly List<UserGroup> _groups = new();
    private long _nextId = 1;

    public List<UserGroup> GetAll() => _groups.ToList();

    public UserGroup? Get(long groupId) => _groups.FirstOrDefault(g => g.GroupId == groupId);

    public UserGroup? FindByName(string groupName) =>
        _groups.FirstOrDefault(g => string.Equals(g.GroupName, groupName, StringComparison.OrdinalIgnoreCase));

    public UserGroup Upsert(UserGroup group)
    {
        var existing = group.GroupId == 0 ? null : Get(group.GroupId);
        if (existing == null)
        {
            group.GroupId = _nextId++;
            _groups.Add(group);
            return group;
        }

        existing.GroupName = group.GroupName;
        existing.Role = group.Role;
        existing.Builtin = group.Builtin;
        existing.Active = group.Active;
        return existing;
    }

    public void Delete(long groupId) => _groups.RemoveAll(g => g.GroupId == groupId);
}

internal class FakeAuditService : IAuditService
{
    public List<AuditEntry> Entries { get; } = new();

    public void Record(string action, string summary, string? targetKind = null, string? targetId = null,
        object? detail = null, AuditResult result = AuditResult.Ok) =>
        Entries.Add(new AuditEntry { Action = action, Summary = summary, TargetKind = targetKind, TargetId = targetId, Result = result });

    public void RecordAuth(string action, string account, long? userId, string summary, AuditResult result) =>
        Entries.Add(new AuditEntry { Action = action, Account = account, UserId = userId, Summary = summary, Result = result });

    public void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null) =>
        Entries.Add(new AuditEntry { Action = action, Account = AuditActions.SystemAccount, Summary = summary });
}
