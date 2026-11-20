using System.Globalization;
using System.Security.Claims;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

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

/// <summary>記錄登入／登出呼叫的替身：controller 單元測試不必組出整套驗證服務</summary>
internal sealed class RecordingSessionSigner : ISessionSigner
{
    public List<SessionIdentity> SignedIn { get; } = new();
    public List<(long UserId, bool IsServerAdmin)> SignedOut { get; } = new();

    public Task SignInAsync(HttpContext context, SessionIdentity identity)
    {
        SignedIn.Add(identity);
        return Task.CompletedTask;
    }

    public Task SignOutAsync(HttpContext context, long userId, bool isServerAdmin)
    {
        SignedOut.Add((userId, isServerAdmin));
        return Task.CompletedTask;
    }
}

/// <summary>只記錄 SignOut 次數的 IAuthenticationService（ValidatePrincipal 拒絕時會呼叫它清 cookie）</summary>
internal sealed class RecordingAuthenticationService : IAuthenticationService
{
    public int SignOuts { get; private set; }

    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        SignOuts++;
        return Task.CompletedTask;
    }

    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => throw new NotSupportedException();
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
}

/// <summary>登入 cookie 驗證的共用測試組裝：設定、身分服務、主體建構、ValidatePrincipal 呼叫</summary>
internal static class AuthTestKit
{
    public static WebAppSettings Settings()
    {
        var settings = new WebAppSettings();
        settings.Auth.ServerAdmin.Account = "serverAdmin";
        return settings;
    }

    public static IdentityService Identity(IUserStore users, IUserGroupStore groups, IHostStore hosts, WebAppSettings settings) =>
        new(users, groups, hosts, new FakeAuthenticationProvider(), new ServerAdminAuthenticator(settings),
            new RecordingAuditService(), new UserCapabilityResolver(groups, hosts));

    /// <summary>
    /// 直接以 claims 建立登入 cookie 的主體（不經簽發服務）。預設值＝合法且與目前狀態一致的 cookie，
    /// 測試只覆寫要驗證的那一項。<paramref name="omit"/> 可移除指定 claim 以測缺漏。
    /// </summary>
    public static ClaimsPrincipal Principal(
        long userId, string account = "DOMAIN\\wang", string? permissionVersion = "0", long sessionVersion = 0,
        DateTimeOffset? startedAt = null, bool isServerAdmin = false, string[]? omit = null, params Capability[] capabilities)
    {
        var claims = new List<Claim>
        {
            new(AuthClaims.UserId, userId.ToString(CultureInfo.InvariantCulture)),
            new(AuthClaims.Account, account),
            new(AuthClaims.DisplayName, account),
            new(AuthClaims.SessionVersion, sessionVersion.ToString(CultureInfo.InvariantCulture)),
            new(AuthClaims.SessionStartedAt,
                (startedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
        };
        if (permissionVersion != null) claims.Add(new Claim(AuthClaims.PermissionVersion, permissionVersion));
        claims.AddRange(capabilities.Select(c => new Claim(AuthClaims.Capability, c.ToString())));
        if (isServerAdmin) claims.Add(new Claim(AuthClaims.ServerAdmin, "1"));
        if (omit != null) claims.RemoveAll(c => omit.Contains(c.Type));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, SessionCookie.Scheme, AuthClaims.DisplayName, null));
    }

    /// <summary>
    /// 從登入回應的 Set-Cookie 取出登入 cookie 的「名稱=值」。
    /// 站台測試一律 <c>HandleCookies = false</c> 自己帶 Cookie 標頭：CookieContainer 不會把 Secure cookie 送回 http，
    /// 而本專案要驗證的正是 http 與 https 兩邊的行為。
    /// </summary>
    public static string? ReadSessionCookiePair(HttpResponseMessage response, string name = "lf_session")
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return null;
        var line = values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal));
        if (line == null) return null;
        var pair = line.Split(';')[0];
        return pair.Length > name.Length + 1 ? pair : null;   // 「name=」空值＝刪除指令，不是登入
    }

    /// <summary>找回應中指定名稱的 Set-Cookie 整行（含屬性）</summary>
    public static string? ReadSetCookieLine(HttpResponseMessage response, string name = "lf_session") =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal))
            : null;
}

/// <summary>ValidatePrincipal 一次呼叫的結果</summary>
internal sealed record ValidateResult(ClaimsPrincipal? Principal, bool ShouldRenew, int SignOuts, HttpContext Context)
{
    public bool Rejected => Principal == null;
}

/// <summary>CookieSessionEvents 的測試台：假的使用者／群組／主機儲存、假時鐘、獨立的權限與工作階段版號</summary>
internal sealed class SessionRig
{
    public readonly FakeUserStore Users = new();
    public readonly FakeUserGroupStore Groups = new();
    public readonly FakeHostStore Hosts = new();
    public readonly WebAppSettings Settings = AuthTestKit.Settings();
    public readonly PermissionVersionStamp Stamp = TestPermissionStamps.Create();
    public readonly SessionVersionStore Versions;
    /// <summary>整秒起算：cookie 內的登入起點以 Unix 秒記，帶小數會讓「剛好 24 小時」的邊界差不到一秒</summary>
    public readonly FakeTimeProvider Clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public SessionRig()
    {
        Versions = new SessionVersionStore(Users, new EfSqliteFixture().Blob(SessionVersionStore.ServerAdminBlobKey));
    }

    public WebUser AddAdmin(string account = "DOMAIN\\wang", bool active = true)
    {
        var admin = Groups.GetAll().FirstOrDefault(g => g.Role == UserRole.Admin)
            ?? Groups.Upsert(new UserGroup { GroupName = "admin", Role = UserRole.Admin, Builtin = true, Active = true });
        var user = Users.Upsert(new WebUser { Account = account, DisplayName = "王", Active = active });
        Users.SetGroups(user.UserId, new[] { admin.GroupId });
        return Users.Get(user.UserId)!;
    }

    /// <summary>與目前狀態一致的合法 cookie 主體（權限版本取目前值、起點取假時鐘現在）</summary>
    public ClaimsPrincipal CurrentPrincipal(WebUser user, params Capability[] capabilities) =>
        AuthTestKit.Principal(user.UserId, user.Account,
            permissionVersion: Stamp.Current.ToString(CultureInfo.InvariantCulture),
            sessionVersion: user.SessionVersion, startedAt: Clock.GetUtcNow(), capabilities: capabilities);

    public CookieSessionEvents Events()
    {
        var identity = AuthTestKit.Identity(Users, Groups, Hosts, Settings);
        return new CookieSessionEvents(Users, Versions, Stamp, identity, new SessionPrincipalFactory(Stamp));
    }

    public async Task<ValidateResult> Validate(ClaimsPrincipal principal, string pathBase = "", string path = "/api/records")
    {
        var auth = new RecordingAuthenticationService();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(auth);
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.PathBase = pathBase;
        http.Request.Path = path;

        var options = new CookieAuthenticationOptions { TimeProvider = Clock };
        var scheme = new AuthenticationScheme(SessionCookie.Scheme, null, typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties(), SessionCookie.Scheme);
        var context = new CookieValidatePrincipalContext(http, scheme, options, ticket);

        await Events().ValidatePrincipal(context);
        return new ValidateResult(context.Principal, context.ShouldRenew, auth.SignOuts, http);
    }
}
