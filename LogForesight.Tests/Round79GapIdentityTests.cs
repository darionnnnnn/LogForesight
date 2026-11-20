using LogForesight.Web.Auth;
using LogForesight.Web.Auth.Ldap;
using LogForesight.Web.Configuration;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 79 輪 C2：AD 帶回的 mail 屬性不是單一合法位址時，不帶入使用者信箱並留警告記錄。</summary>
[Collection("NLogGlobalConfig")]
public class AdMailAttributeSyncTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly RecordingAuditService _audit = new();

    private sealed class FixedProvider(CredentialCheckResult result) : IAuthenticationProvider
    {
        public string Name => "Fixed";
        public bool RequiresPassword => false;
        public CredentialCheckResult Verify(string account, string? password) => result;
    }

    private IdentityService Create(CredentialCheckResult result)
    {
        var settings = new WebAppSettings
        {
            Auth = new AuthSettings
            {
                ServerAdmin = new ServerAdminSettings { Account = "svc-lfadmin", PasswordHash = PasswordHasher.Hash("Test-Only-P@ssw0rd") }
            }
        };
        return new IdentityService(_users, _groups, _hosts, new FixedProvider(result),
            new ServerAdminAuthenticator(settings), _audit,
            new UserCapabilityResolver(_groups, _hosts, _issueOwners));
    }

    private static CredentialCheckResult AdResult(string? mail) =>
        new(true, UserInfo: new LdapUserInfo("wang", "王小明", mail, "CN=wang,DC=corp,DC=local", IsLocked: false));

    [Theory]
    [InlineData("a@corp.local,b@corp.local")]
    [InlineData("a@corp.local;b@corp.local")]
    [InlineData("王小明 <a@corp.local>")]
    [InlineData("not-an-email")]
    public void AD帶回不合格mail_不帶入信箱且記警告_登入與顯示名稱照常(string badMail)
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "DOMAIN\\wang", Email = null });
        using var capture = NLogCapture.For(typeof(IdentityService));

        var outcome = Create(AdResult(badMail)).Login("DOMAIN\\wang", "any");

        Assert.True(outcome.Success);
        var saved = _users.FindByAccount("DOMAIN\\wang")!;
        Assert.Null(saved.Email);
        Assert.Equal("王小明", saved.DisplayName);   // 其餘屬性照常同步，不因信箱不合格而整筆放棄
        Assert.Contains(capture.Logs, l => l.StartsWith("Warn|", StringComparison.OrdinalIgnoreCase) && l.Contains("不是單一電子郵件位址"));
    }

    [Fact]
    public void AD帶回單一合法mail_照常帶入_沒有警告()
    {
        _users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "DOMAIN\\wang", Email = null });
        using var capture = NLogCapture.For(typeof(IdentityService));

        Create(AdResult("wang@corp.local")).Login("DOMAIN\\wang", "any");

        Assert.Equal("wang@corp.local", _users.FindByAccount("DOMAIN\\wang")!.Email);
        Assert.DoesNotContain(capture.Logs, l => l.Contains("不是單一電子郵件位址"));
    }
}
