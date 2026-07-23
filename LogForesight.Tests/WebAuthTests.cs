using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using Xunit;

namespace LogForesight.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void 正確密碼_驗證通過()
    {
        var hash = PasswordHasher.Hash("Test-Only-P@ssw0rd");
        Assert.True(PasswordHasher.Verify("Test-Only-P@ssw0rd", hash));
    }

    [Fact]
    public void 錯誤密碼_驗證失敗()
    {
        var hash = PasswordHasher.Hash("Test-Only-P@ssw0rd");
        Assert.False(PasswordHasher.Verify("lfadmin!2026", hash));
        Assert.False(PasswordHasher.Verify("", hash));
    }

    /// <summary>每次雜湊都應使用新的 salt，相同密碼不會產生相同字串（避免比對雜湊即可看出誰跟誰同密碼）</summary>
    [Fact]
    public void 相同密碼兩次雜湊_結果不同但都能驗證()
    {
        var first = PasswordHasher.Hash("same-password");
        var second = PasswordHasher.Hash("same-password");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("same-password", first));
        Assert.True(PasswordHasher.Verify("same-password", second));
    }

    /// <summary>設定檔沒填或格式壞掉時要回 false，不能拋例外——登入端點不該因為設定錯誤噴 500</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("PBKDF2$abc$xx$yy")]
    [InlineData("PBKDF2$1000$!!!invalid-base64!!!$yy")]
    public void 雜湊字串不合格_回false不拋例外(string storedHash)
    {
        Assert.False(PasswordHasher.Verify("any", storedHash));
    }
}

public class RoleCapabilityMapTests
{
    [Fact]
    public void 一般使用者_可維護處理狀態但不可指派()
    {
        var caps = RoleCapabilityMap.For(UserRole.User);

        Assert.Contains(Capability.Handle, caps);
        Assert.DoesNotContain(Capability.Assign, caps);
        Assert.DoesNotContain(Capability.ViewAll, caps);
        Assert.DoesNotContain(Capability.Maintain, caps);
    }

    [Fact]
    public void 主管_純唯讀不碰處理流程()
    {
        var caps = RoleCapabilityMap.For(UserRole.Manager);

        Assert.Contains(Capability.ViewAll, caps);
        Assert.DoesNotContain(Capability.Handle, caps);
        Assert.DoesNotContain(Capability.Assign, caps);
    }

    [Fact]
    public void 開發人員_有執行監控且主管沒有()
    {
        Assert.Contains(Capability.DevMonitor, RoleCapabilityMap.For(UserRole.Dev));
        Assert.DoesNotContain(Capability.DevMonitor, RoleCapabilityMap.For(UserRole.Manager));
    }

    /// <summary>
    /// 多群組取**聯集**而不是「取最高角色」：dev 與 manager 沒有高低之分，
    /// 一個人同時是 dev 與一般使用者時，兩邊的能力都該有。
    /// </summary>
    [Fact]
    public void 跨群組_能力取聯集()
    {
        var caps = RoleCapabilityMap.For(new[] { UserRole.Dev, UserRole.User });

        Assert.Contains(Capability.DevMonitor, caps);   // 來自 dev
        Assert.Contains(Capability.ViewAll, caps);      // 來自 dev
        Assert.Contains(Capability.Handle, caps);       // 來自 user
        Assert.DoesNotContain(Capability.Maintain, caps);
    }

    [Fact]
    public void 系統管理員_具備全部能力()
    {
        var caps = RoleCapabilityMap.For(UserRole.Admin);

        foreach (var capability in Enum.GetValues<Capability>())
            Assert.Contains(capability, caps);
    }

    /// <summary>
    /// serverAdmin 是本地救援帳號，用途是指派 admin 成員與救援——
    /// 依用途給最小權限，**不含任何業務資料檢視**。
    /// </summary>
    [Fact]
    public void serverAdmin_只有維護與稽核不含業務資料()
    {
        var caps = RoleCapabilityMap.ForServerAdmin();

        Assert.Contains(Capability.Maintain, caps);
        Assert.Contains(Capability.ViewAudit, caps);
        Assert.DoesNotContain(Capability.ViewAll, caps);
        Assert.DoesNotContain(Capability.Handle, caps);
        Assert.DoesNotContain(Capability.Assign, caps);
    }
}

public class ServerAdminAuthenticatorTests
{
    private const string Account = "svc-lfadmin";
    private const string Password = "Test-Only-P@ssw0rd";

    private static ServerAdminAuthenticator Create(int maxAttempts = 5, int lockoutMinutes = 15) =>
        new(new WebAppSettings
        {
            Auth = new AuthSettings
            {
                ServerAdmin = new ServerAdminSettings
                {
                    Account = Account,
                    PasswordHash = PasswordHasher.Hash(Password),
                    MaxFailedAttempts = maxAttempts,
                    LockoutMinutes = lockoutMinutes
                }
            }
        });

    [Fact]
    public void 其他帳號_回報NotServerAdmin交給一般流程()
    {
        Assert.Equal(ServerAdminLoginResult.NotServerAdmin,
            Create().TryLogin("DOMAIN\\wang", Password));
    }

    [Fact]
    public void 帳號比對不分大小寫()
    {
        Assert.Equal(ServerAdminLoginResult.Success, Create().TryLogin("SVC-LFADMIN", Password));
    }

    [Fact]
    public void 正確密碼_登入成功()
    {
        Assert.Equal(ServerAdminLoginResult.Success, Create().TryLogin(Account, Password));
    }

    /// <summary>Stub 模式（requiresPassword=false）：救援帳號免密碼登入，空密碼也放行</summary>
    [Fact]
    public void Stub模式_免密碼登入成功()
    {
        Assert.Equal(ServerAdminLoginResult.Success,
            Create().TryLogin(Account, null, requiresPassword: false));
    }

    /// <summary>Stub 模式不驗密碼，先前的失敗計數不應把免密碼登入擋在鎖定外</summary>
    [Fact]
    public void Stub模式_先前失敗不影響免密碼登入()
    {
        var auth = Create(maxAttempts: 2);
        auth.TryLogin(Account, "wrong");   // 需密碼模式下的一次失敗

        Assert.Equal(ServerAdminLoginResult.Success,
            auth.TryLogin(Account, null, requiresPassword: false));
    }

    [Fact]
    public void 連續失敗達門檻_觸發鎖定()
    {
        var auth = Create(maxAttempts: 3);

        Assert.Equal(ServerAdminLoginResult.WrongPassword, auth.TryLogin(Account, "wrong"));
        Assert.Equal(ServerAdminLoginResult.WrongPassword, auth.TryLogin(Account, "wrong"));
        Assert.Equal(ServerAdminLoginResult.LockedOut, auth.TryLogin(Account, "wrong"));
    }

    /// <summary>鎖定期間即使密碼正確也要拒絕，否則鎖定形同虛設</summary>
    [Fact]
    public void 鎖定期間_正確密碼仍被拒()
    {
        var auth = Create(maxAttempts: 2);
        auth.TryLogin(Account, "wrong");
        auth.TryLogin(Account, "wrong");

        Assert.Equal(ServerAdminLoginResult.LockedOut, auth.TryLogin(Account, Password));
    }

    [Fact]
    public void 成功登入_清除先前的失敗計數()
    {
        var auth = Create(maxAttempts: 3);
        auth.TryLogin(Account, "wrong");
        auth.TryLogin(Account, "wrong");

        Assert.Equal(ServerAdminLoginResult.Success, auth.TryLogin(Account, Password));

        // 計數已歸零，接下來仍可再錯兩次而不被鎖
        Assert.Equal(ServerAdminLoginResult.WrongPassword, auth.TryLogin(Account, "wrong"));
        Assert.Equal(ServerAdminLoginResult.WrongPassword, auth.TryLogin(Account, "wrong"));
    }

    [Fact]
    public void 未設定serverAdmin帳號_任何帳號都不視為serverAdmin()
    {
        var auth = new ServerAdminAuthenticator(new WebAppSettings());

        Assert.False(auth.IsServerAdmin(""));
        Assert.Equal(ServerAdminLoginResult.NotServerAdmin, auth.TryLogin("anything", "x"));
    }
}
