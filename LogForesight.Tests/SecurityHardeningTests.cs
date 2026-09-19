using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Middleware;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 測試用權限版本號。<see cref="Shared"/> 給「不驗版本號」的服務組裝共用（單一實例、內部上鎖，
/// 平行測試同時推進也安全）；要斷言版本號的測試用 <see cref="Create"/> 取獨立實例。
/// </summary>
internal static class TestPermissionStamps
{
    private static readonly Lazy<PermissionVersionStamp> SharedStamp = new(Create);

    public static PermissionVersionStamp Shared => SharedStamp.Value;

    public static PermissionVersionStamp Create() =>
        new(new EfSqliteFixture().Blob(PermissionVersionStamp.BlobKey));
}

/// <summary>ActiveUserMiddleware 的組裝：middleware 的相依一多，各測試檔共用同一份組法</summary>
internal static class AuthTestKit
{
    public static WebAppSettings Settings()
    {
        var settings = new WebAppSettings();
        settings.Jwt.SecretKey = new string('k', 64);
        settings.Auth.ServerAdmin.Account = "serverAdmin";
        return settings;
    }

    public static IdentityService Identity(IUserStore users, IUserGroupStore groups, IHostStore hosts, WebAppSettings settings) =>
        new(users, groups, hosts, new FakeAuthenticationProvider(), new ServerAdminAuthenticator(settings),
            new RecordingAuditService(), new UserCapabilityResolver(groups, hosts));

    public static Task InvokeMiddleware(
        ActiveUserMiddleware middleware, HttpContext context, ICurrentUser currentUser, IUserStore users, WebAppSettings settings,
        PermissionVersionStamp? stamp = null, RevokedTokens? revoked = null, IUserGroupStore? groups = null, IHostStore? hosts = null)
    {
        stamp ??= TestPermissionStamps.Shared;
        groups ??= new FakeUserGroupStore();
        hosts ??= new FakeHostStore();
        return middleware.InvokeAsync(context, currentUser, users, settings,
            Identity(users, groups, hosts, settings), new JwtTokenService(settings, stamp), stamp, revoked ?? new RevokedTokens());
    }
}

public class SecurityHardeningTests
{
    private const string KnownDevSecretKey = "bge6V8SsJu6GRuYYyVniemo/XhAJ4J3kis3sqSDai5gluW9wmA2Vnd4opTbXBTxo";
    private const string KnownDevPasswordHash = "PBKDF2$210000$s79Mzbz0FKaArA2SsMlCuQ==$2JKMIZGXFQbelisLIZZo+WAMCPJ0Ao3XPm5jg8TFy9M=";

    // ── 1. 環境欄杆 ──────────────────────────────────────────────────────

    /// <summary>三種出廠值各自單獨出現；其餘欄位皆合格</summary>
    public static IEnumerable<object[]> FactoryValueCases() => new[]
    {
        new object[] { "stub" },
        new object[] { "jwt" },
        new object[] { "hash" }
    };

    private static WebAppSettings WithFactoryValue(string which) => new()
    {
        Jwt = new JwtSettings
        {
            SecretKey = which == "jwt" ? KnownDevSecretKey : "這是另外產生的至少三十二個位元組長的隨機字串內容測試用途",
            ExpireHours = 8
        },
        Auth = new AuthSettings
        {
            Provider = which == "stub" ? "Stub" : "Ldap",
            ServerAdmin = new ServerAdminSettings
            {
                Account = "svc-lfadmin",
                PasswordHash = which == "hash" ? KnownDevPasswordHash : PasswordHasher.Hash("Overridden-P@ssw0rd")
            }
        },
        Storage = new StorageSettings { Type = "Sqlite", DataRoot = AppContext.BaseDirectory }
    };

    [Theory]
    [MemberData(nameof(FactoryValueCases))]
    public void Staging環境_出廠值一律擋下且訊息指出環境與Development(string which)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WithFactoryValue(which).Validate(strict: true, "Staging"));

        Assert.Contains("Staging", ex.Message);
        Assert.Contains("Development", ex.Message);
        Assert.Contains("ASPNETCORE_ENVIRONMENT", ex.Message);
    }

    [Theory]
    [MemberData(nameof(FactoryValueCases))]
    public void 非嚴格模式_出廠值放行(string which)
    {
        WithFactoryValue(which).Validate(strict: false, "Development");   // 不應拋例外
    }

    // ── 2. 網址機敏參數 ──────────────────────────────────────────────────

    [Fact]
    public void UrlSecrets_偵測與遮罩()
    {
        Assert.True(UrlSecrets.ContainsSecretQuery("https://prtg/api?passhash=123&username=a"));
        Assert.True(UrlSecrets.ContainsSecretQuery("https://x/?a=1&API_KEY=zz"));
        Assert.False(UrlSecrets.ContainsSecretQuery("https://prtg/"));
        Assert.False(UrlSecrets.ContainsSecretQuery("https://prtg/token/path?user=a"));
        Assert.False(UrlSecrets.ContainsSecretQuery(null));

        var masked = UrlSecrets.Mask("https://prtg/api?passhash=123&username=a");
        Assert.DoesNotContain("123", masked);
        Assert.Equal("https://prtg/api?passhash=***&username=a", masked);
    }

    [Fact]
    public void 系統設定儲存_PRTG網址帶passhash_Validation例外()
    {
        var fx = new EfSqliteFixture();
        var store = new SystemSettingsStore(fx.Blob("system_settings"));
        var service = new SystemSettingsService(store, FakeCurrentUser.WithCapabilities(), new RecordingAuditService(), new FakeUserStore(),
            new MailNotificationService(store, new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(),
                new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
                new MailNotifyStateStore(fx.Blob("mail_state")),
                new ScheduleFreshnessService(new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs")),
                    new ScheduleOptionsStore(fx.Blob("schedule_options")))),
            new FakeReportUsageQuery());

        var ex = Assert.Throws<DomainException>(() => service.UpdatePrtg(new UpdatePrtgSettingsRequest
        {
            PrtgUrl = "https://prtg.corp.local/api/table.json?username=a&PassHash=123456"
        }));

        Assert.Contains("網址不可包含密碼或金鑰參數", ex.Message);
        Assert.DoesNotContain("PassHash=123456", store.Get().PrtgUrl);
    }

    // ── 3. 權限變更即時生效 ──────────────────────────────────────────────

    private static ClaimsPrincipal TokenPrincipal(long userId, string? pv, params Capability[] capabilities)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtTokenService.AccountClaim, "DOMAIN\\wang"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (pv != null) claims.Add(new Claim(JwtTokenService.PermissionVersionClaim, pv));
        claims.AddRange(capabilities.Select(c => new Claim(JwtTokenService.CapabilityClaim, c.ToString())));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private sealed class Rig
    {
        public readonly FakeUserStore Users = new();
        public readonly FakeUserGroupStore Groups = new();
        public readonly FakeHostStore Hosts = new();
        public readonly WebAppSettings Settings = AuthTestKit.Settings();
        public readonly PermissionVersionStamp Stamp = TestPermissionStamps.Create();
        public readonly RevokedTokens Revoked = new();

        public WebUser AddAdmin()
        {
            var admin = Groups.Upsert(new UserGroup { GroupName = "admin", Role = UserRole.Admin, Builtin = true, Active = true });
            var user = Users.Upsert(new WebUser { Account = "DOMAIN\\wang", DisplayName = "王", Active = true });
            Users.SetGroups(user.UserId, new[] { admin.GroupId });
            return Users.Get(user.UserId)!;
        }

        public (DefaultHttpContext Context, HttpContextCurrentUser CurrentUser) Request(ClaimsPrincipal principal, string path = "/api/records")
        {
            var context = new DefaultHttpContext { User = principal };
            context.Request.Path = path;
            context.Response.Body = new MemoryStream();
            return (context, new HttpContextCurrentUser(new HttpContextAccessor { HttpContext = context }));
        }

        public async Task<bool> Invoke(HttpContext context, ICurrentUser currentUser)
        {
            var reached = false;
            var middleware = new ActiveUserMiddleware(_ => { reached = true; return Task.CompletedTask; });
            await AuthTestKit.InvokeMiddleware(middleware, context, currentUser, Users, Settings, Stamp, Revoked, Groups, Hosts);
            return reached;
        }
    }

    [Fact]
    public async Task 權限版本不符_重算能力並重發cookie且同一請求的ICurrentUser讀到新能力()
    {
        var rig = new Rig();
        var user = rig.AddAdmin();
        rig.Stamp.Bump();
        rig.Stamp.Bump();
        Assert.Equal(2, rig.Stamp.Current);

        // token 簽發於版本 1、當時沒有任何能力；目前版本 2 且此人在 admin 群組
        var (context, currentUser) = rig.Request(TokenPrincipal(user.UserId, "1"));
        Assert.False(currentUser.Has(Capability.Maintain));   // 替換前先讀一次，確認不是建構時抓的快照

        var reached = await rig.Invoke(context, currentUser);

        Assert.True(reached);
        Assert.Contains(context.User.FindAll(JwtTokenService.CapabilityClaim), c => c.Value == nameof(Capability.Maintain));
        Assert.Equal("2", context.User.FindFirst(JwtTokenService.PermissionVersionClaim)?.Value);
        Assert.True(currentUser.Has(Capability.Maintain));   // 同一個 Scoped 實例，替換後讀到新能力
        Assert.Contains(context.Response.Headers.SetCookie, c => c != null && c.StartsWith($"{rig.Settings.Jwt.CookieName}=ey"));
    }

    [Fact]
    public async Task 沒有pv的舊token_視為不同而換發()
    {
        var rig = new Rig();
        var user = rig.AddAdmin();

        var (context, currentUser) = rig.Request(TokenPrincipal(user.UserId, pv: null));
        await rig.Invoke(context, currentUser);

        Assert.Contains(context.Response.Headers.SetCookie, c => c != null && c.StartsWith($"{rig.Settings.Jwt.CookieName}=ey"));
        Assert.True(currentUser.Has(Capability.Maintain));
    }

    [Fact]
    public async Task 權限版本相同_不重發cookie也不替換User()
    {
        var rig = new Rig();
        var user = rig.AddAdmin();
        rig.Stamp.Bump();

        var principal = TokenPrincipal(user.UserId, "1");
        var (context, currentUser) = rig.Request(principal);
        var reached = await rig.Invoke(context, currentUser);

        Assert.True(reached);
        Assert.Same(principal, context.User);
        Assert.True(Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(context.Response.Headers.SetCookie));
    }

    [Fact]
    public void 以真實服務移除admin群組成員資格_權限版本加一()
    {
        var rig = new Rig();
        var user = rig.AddAdmin();
        var service = new UserAdminService(
            rig.Users, rig.Groups, rig.Hosts, new FakeHostGroupStore(), new FakeIssueCaseStore(),
            new AlwaysVisibleService(rig.Hosts), new RecordingAuditService(),
            new UserCapabilityResolver(rig.Groups, rig.Hosts), new UserDisplayNameService(new FakeSystemSettingsStore()),
            rig.Stamp);
        var before = rig.Stamp.Current;

        service.SetUserGroups(user.UserId, Array.Empty<long>());

        Assert.Equal(before + 1, rig.Stamp.Current);
        Assert.Empty(rig.Users.Get(user.UserId)!.GroupIds);
    }

    [Fact]
    public async Task 登出後同一token再請求_回401()
    {
        var rig = new Rig();
        var user = rig.AddAdmin();
        var tokens = new JwtTokenService(rig.Settings, rig.Stamp);
        var principal = tokens.CreatePrincipal(new TokenIdentity(user.UserId, user.Account, "王",
            new HashSet<Capability> { Capability.Maintain }, IsServerAdmin: false));

        // 登出前可正常通過（版本相同，不換發）
        var (before, beforeUser) = rig.Request(principal);
        Assert.True(await rig.Invoke(before, beforeUser));

        var identity = AuthTestKit.Identity(rig.Users, rig.Groups, rig.Hosts, rig.Settings);
        var controller = new AuthController(identity, tokens, new FakeAuthenticationProvider(),
            FakeCurrentUser.ForUser(user.UserId), new RecordingAuditService(), rig.Settings,
            new UserDisplayNameService(new FakeSystemSettingsStore()), new LoginThrottle(), rig.Revoked)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
        controller.Logout();

        var (after, afterUser) = rig.Request(principal);
        var reached = await rig.Invoke(after, afterUser);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status401Unauthorized, after.Response.StatusCode);
    }
}
