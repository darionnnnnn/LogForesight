using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LogForesight.Tests;

public class SessionCookieSecureFlagTests
{
    private static HttpRequest Request(bool https, string? forwardedProto = null)
    {
        var context = new DefaultHttpContext();
        context.Request.IsHttps = https;
        if (forwardedProto != null) context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        return context.Request;
    }

    [Fact]
    public void HTTP請求_Secure為False()
    {
        Assert.False(SessionCookie.IsSecureConnection(Request(https: false)));
    }

    [Fact]
    public void HTTPS請求_Secure為True()
    {
        Assert.True(SessionCookie.IsSecureConnection(Request(https: true)));
    }

    /// <summary>反向代理終止 TLS 後以 HTTP 轉發時 IsHttps 是 false，只看它會讓走 HTTPS 的
    /// 使用者拿到沒有 Secure 旗標的登入 cookie——那是安全降級，必須認 X-Forwarded-Proto。</summary>
    [Fact]
    public void 反向代理轉發的HTTPS請求_Secure仍為True()
    {
        Assert.True(SessionCookie.IsSecureConnection(Request(https: false, "https")));
    }

    /// <summary>多層代理會把 X-Forwarded-Proto 累加成逗號清單，最靠近使用者的是第一段——
    /// 拿整串比對「https」會恆為 false，Secure 又掉回去。</summary>
    [Fact]
    public void 多層代理的XForwardedProto清單_取第一段判定()
    {
        Assert.True(SessionCookie.IsSecureConnection(Request(https: false, "https, http")));
    }

    [Fact]
    public void 代理標示HTTP_Secure為False()
    {
        Assert.False(SessionCookie.IsSecureConnection(Request(https: false, "http")));
    }

    /// <summary>cookie 的 Path 用編碼後形式：瀏覽器 path-match 比對的是請求 URL 的編碼後路徑</summary>
    [Theory]
    [InlineData("", "/")]
    [InlineData("/LogForesight", "/LogForesight")]
    public void CookiePath_跟掛載路徑走(string pathBase, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = pathBase;
        Assert.Equal(expected, SessionCookie.CookiePath(context.Request));
    }

    [Fact]
    public void CookiePath_使用編碼後形式()
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/日誌預警";

        var path = SessionCookie.CookiePath(context.Request);

        Assert.DoesNotContain("日", path);
        Assert.StartsWith("/%", path);
    }
}

/// <summary>舊版 JWT cookie（名稱 lf_auth）的清除：只在請求帶著舊名稱時動作</summary>
public class LegacyAuthCookieMiddlewareTests
{
    private static async Task<DefaultHttpContext> Run(string pathBase, bool https, string? cookieHeader, string currentName = "lf_session")
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = pathBase;
        context.Request.IsHttps = https;
        if (cookieHeader != null) context.Request.Headers.Cookie = cookieHeader;
        var middleware = new LogForesight.Web.Middleware.LegacyAuthCookieMiddleware(_ => Task.CompletedTask, currentName);
        await middleware.InvokeAsync(context);
        return context;
    }

    [Fact]
    public async Task 請求帶舊名稱cookie_回應清掉它_子路徑與根路徑兩個Path都刪()
    {
        var context = await Run("/LogForesight", https: false, "lf_auth=eyJhbGciOi.old");

        var setCookies = context.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains("lf_auth=;", setCookies);
        Assert.Contains("path=/LogForesight", setCookies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/;", setCookies, StringComparison.OrdinalIgnoreCase);
        // HTTP 請求的 Secure 判定與寫入端一致，否則刪不掉
        Assert.DoesNotContain("secure", setCookies, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 請求沒有舊名稱cookie_不動作()
    {
        var context = await Run("", https: true, "lf_session=abc");

        Assert.True(Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(context.Response.Headers.SetCookie));
    }

    /// <summary>邊界：管理者把 CookieName 設成舊名稱時，那張就是現行 cookie，不能被清掉</summary>
    [Fact]
    public async Task 現行名稱就是舊名稱_不清除()
    {
        var context = await Run("", https: true, "lf_auth=abc", currentName: "lf_auth");

        Assert.True(Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(context.Response.Headers.SetCookie));
    }
}

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

    [Fact]
    public void IsValidHashFormat_合法雜湊格式_回True()
    {
        var hash = PasswordHasher.Hash("任意密碼");
        Assert.True(PasswordHasher.IsValidHashFormat(hash));
    }

    [Fact]
    public void IsValidHashFormat_純明文密碼_回False()
    {
        Assert.False(PasswordHasher.IsValidHashFormat("MyPassword123"));
    }

    [Fact]
    public void IsValidHashFormat_段數不足_回False()
    {
        Assert.False(PasswordHasher.IsValidHashFormat("PBKDF2$210000$abc"));
    }

    [Fact]
    public void IsValidHashFormat_迭代次數非數字_回False()
    {
        var validBase64 = Convert.ToBase64String(new byte[16]);
        Assert.False(PasswordHasher.IsValidHashFormat($"PBKDF2$abc${validBase64}${validBase64}"));
    }

    [Fact]
    public void IsValidHashFormat_Salt非合法Base64_回False()
    {
        var validBase64 = Convert.ToBase64String(new byte[16]);
        Assert.False(PasswordHasher.IsValidHashFormat($"PBKDF2$210000$!!!!${validBase64}"));
    }

    [Fact]
    public void IsValidHashFormat_空字串與Null_回False()
    {
        Assert.False(PasswordHasher.IsValidHashFormat(""));
        Assert.False(PasswordHasher.IsValidHashFormat(null));
        Assert.False(PasswordHasher.IsValidHashFormat("   "));
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

/// <summary>P0-2：已提交進版控的公開測試金鑰／雜湊，不得帶著上 Production</summary>
public class WebAppSettingsValidationTests
{
    private const string KnownDevPasswordHash = "PBKDF2$210000$s79Mzbz0FKaArA2SsMlCuQ==$2JKMIZGXFQbelisLIZZo+WAMCPJ0Ao3XPm5jg8TFy9M=";

    /// <summary>其餘欄位皆合格的基準設定，僅金鑰/雜湊/環境由呼叫端覆寫</summary>
    private static WebAppSettings Baseline() => new()
    {
        Jwt = new SessionSettings { ExpireHours = 8 },
        Auth = new AuthSettings
        {
            Provider = "Ldap",
            ServerAdmin = new ServerAdminSettings { Account = "svc-lfadmin", PasswordHash = KnownDevPasswordHash }
        },
        Storage = new StorageSettings { Type = "Sqlite", DataRoot = AppContext.BaseDirectory }
    };

    [Fact]
    public void Production且用已知PasswordHash_啟動失敗()
    {
        var settings = Baseline();

        var ex = Assert.Throws<InvalidOperationException>(() => settings.Validate(strict: true, "Production"));
        Assert.Contains("Auth:ServerAdmin:PasswordHash", ex.Message);
    }

    /// <summary>本機測試合法使用這組公開值——非 Production 不擋</summary>
    [Fact]
    public void 非Production且用已知值_通過()
    {
        var settings = Baseline();

        // 意圖：非 Production 用已知值不擲例外
        var ex = Record.Exception(() => settings.Validate(strict: false, "Development"));
        Assert.Null(ex);
    }

    [Fact]
    public void Production且已覆寫成非已知值_通過()
    {
        var settings = Baseline();
        settings.Auth.ServerAdmin.PasswordHash = PasswordHasher.Hash("Production-Overridden-P@ssw0rd");

        // 意圖：Production 已覆寫成非已知值不擲例外
        var ex = Record.Exception(() => settings.Validate(strict: true, "Production"));
        Assert.Null(ex);
    }

    /// <summary>登入閒置時限的邊界：0 不合格、1 合格（0 會讓登入 cookie 一建立就過期）</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    public void ExpireHours必須大於0(int hours, bool valid)
    {
        var settings = Baseline();
        settings.Jwt.ExpireHours = hours;
        settings.Auth.ServerAdmin.PasswordHash = PasswordHasher.Hash("Any-P@ssw0rd-1");

        var ex = Record.Exception(() => settings.Validate(strict: false, "Development"));

        if (valid) Assert.Null(ex);
        else Assert.Contains("Jwt:ExpireHours", Assert.IsType<InvalidOperationException>(ex).Message);
    }

    [Fact]
    public void PasswordHash為明文密碼_啟動驗證失敗且訊息提示產生方式()
    {
        var settings = Baseline();
        settings.Auth.ServerAdmin.PasswordHash = "MyPlainPassword123!";

        var ex = Assert.Throws<InvalidOperationException>(() => settings.Validate(strict: false, "Development"));
        Assert.Contains("--hash-password", ex.Message);
        Assert.Contains("Auth:ServerAdmin:PasswordHash", ex.Message);
    }

    // §13：Netiq:DiscoveryClient 已退役（離線示範資料改由 DB 開關＋非 Production 限定控制），
    // 對應的啟動驗證測試一併移除；選型邏輯改測 NetiqDiscoveryClientSelectionTests.UseStubNetiqClient。
}

public class ServerAdminAuthenticatorTests
{
    private const string Account = "svc-lfadmin";
    private const string Password = "Test-Only-P@ssw0rd";

    private static ServerAdminAuthenticator Create() =>
        new(new WebAppSettings
        {
            Auth = new AuthSettings
            {
                ServerAdmin = new ServerAdminSettings
                {
                    Account = Account,
                    PasswordHash = PasswordHasher.Hash(Password)
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

    /// <summary>
    /// Stub 模式的核心定義：不論輸入什麼密碼都通過——連「錯誤密碼」也放行
    /// （免密碼發生在後端且不比對密碼值，不是「密碼必須留空」）。
    /// </summary>
    [Theory]
    [InlineData("完全錯誤的密碼")]
    [InlineData("")]
    [InlineData(null)]
    public void Stub模式_任意密碼都通過(string? anyPassword)
    {
        Assert.Equal(ServerAdminLoginResult.Success,
            Create().TryLogin(Account, anyPassword, requiresPassword: false));
    }

    /// <summary>Stub 模式不驗密碼，先前的失敗計數不應把免密碼登入擋在鎖定外</summary>
    [Fact]
    public void Stub模式_先前失敗不影響免密碼登入()
    {
        var auth = Create();
        auth.TryLogin(Account, "wrong");   // 需密碼模式下的一次失敗

        Assert.Equal(ServerAdminLoginResult.Success,
            auth.TryLogin(Account, null, requiresPassword: false));
    }

    /// <summary>
    /// 第 79 輪批次 G10：救援帳號不另設第二套鎖定（防暴力破解交給 LoginThrottle）：
    /// 連續輸錯後輸對仍可登入，否則匿名者輸錯幾次就能把 AD 停擺時唯一的救援入口鎖死。
    /// </summary>
    [Fact]
    public void 連續失敗後正確密碼仍可登入_鎖定交由登入節流()
    {
        var auth = Create();
        for (var i = 0; i < 10; i++) Assert.Equal(ServerAdminLoginResult.WrongPassword, auth.TryLogin(Account, "wrong"));
        Assert.Equal(ServerAdminLoginResult.Success, auth.TryLogin(Account, Password));
    }

    [Fact]
    public void 成功登入_清除先前的失敗計數()
    {
        var auth = Create();
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
