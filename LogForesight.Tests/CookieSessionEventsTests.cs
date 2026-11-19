using System.Text.Json;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 登入 cookie 的逐請求驗證（docs/WEB-SPEC.md §6.3）：停用、工作階段版號、權限版本、24 小時絕對上限，
/// 以及未登入／拒絕存取的回應。單元層直接呼叫 <see cref="CookieSessionEvents"/>；經真實站台的行為見
/// <see cref="CookieAuthenticationSiteTests"/>。
/// </summary>
public class CookieSessionEventsTests
{
    // ── 停用／不存在 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task 啟用中的使用者_放行且不更新()
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin();
        var principal = rig.CurrentPrincipal(user);

        var result = await rig.Validate(principal);

        Assert.Same(principal, result.Principal);
        Assert.False(result.ShouldRenew);
        Assert.Equal(0, result.SignOuts);
    }

    /// <summary>停用後不等 cookie 過期：下一個請求立刻失效並清 cookie</summary>
    [Fact]
    public async Task 已停用的使用者_拒絕並清cookie()
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin(active: false);

        var result = await rig.Validate(rig.CurrentPrincipal(user));

        Assert.True(result.Rejected);
        Assert.Equal(1, result.SignOuts);
    }

    /// <summary>邊界另一側：Active 為 true 的同一個人不被擋（避免把「停用檢查」寫成恆擋）</summary>
    [Fact]
    public async Task 登入後才被停用_下一個請求失效_停用前簽發的cookie重新啟用也不復活()
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin();
        var principal = rig.CurrentPrincipal(user);
        Assert.False((await rig.Validate(principal)).Rejected);

        // 經真實儲存的 Upsert 停用：啟用轉停用時工作階段版號遞增
        rig.Users.Upsert(new WebUser { Account = user.Account, DisplayName = user.DisplayName, Active = false, GroupIds = user.GroupIds });
        Assert.True((await rig.Validate(principal)).Rejected);

        rig.Users.Upsert(new WebUser { Account = user.Account, DisplayName = user.DisplayName, Active = true, GroupIds = user.GroupIds });
        Assert.True((await rig.Validate(principal)).Rejected);   // 版號已不同，舊 cookie 不復活
    }

    [Fact]
    public async Task 使用者已不存在_一併拒絕()
    {
        var rig = new SessionRig();

        var result = await rig.Validate(AuthTestKit.Principal(12345));

        Assert.True(result.Rejected);
    }

    /// <summary>serverAdmin 不存在於 lf_users，不能被停用檢查誤擋——它是 AD 停擺時的逃生門</summary>
    [Fact]
    public async Task serverAdmin_不受停用檢查影響()
    {
        var rig = new SessionRig();

        var result = await rig.Validate(AuthTestKit.Principal(0, "serverAdmin", isServerAdmin: true));

        Assert.False(result.Rejected);
        Assert.False(result.ShouldRenew);
    }

    [Fact]
    public async Task 未登入的主體_不檢查直接放行給後續驗證處理()
    {
        var rig = new SessionRig();
        var anonymous = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());

        var result = await rig.Validate(anonymous);

        Assert.Same(anonymous, result.Principal);
        Assert.Equal(0, result.SignOuts);
    }

    // ── 工作階段版號 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 登出遞增版號_舊cookie失效_新版號的cookie有效()
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin();
        var oldPrincipal = rig.CurrentPrincipal(user);

        rig.Versions.Bump(user.UserId, isServerAdmin: false);

        Assert.True((await rig.Validate(oldPrincipal)).Rejected);
        Assert.False((await rig.Validate(rig.CurrentPrincipal(rig.Users.Get(user.UserId)!))).Rejected);
    }

    [Fact]
    public async Task serverAdmin登出_以blob版號失效且不影響一般使用者()
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin();
        var adminPrincipal = AuthTestKit.Principal(0, "serverAdmin", isServerAdmin: true);
        var userPrincipal = rig.CurrentPrincipal(user);

        rig.Versions.Bump(0, isServerAdmin: true);

        Assert.True((await rig.Validate(adminPrincipal)).Rejected);
        Assert.False((await rig.Validate(userPrincipal)).Rejected);
        Assert.Equal(1, rig.Versions.Current(isServerAdmin: true));
    }

    [Fact]
    public async Task 缺少工作階段版號claim_視為無效()
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin();

        var result = await rig.Validate(AuthTestKit.Principal(user.UserId, user.Account,
            omit: new[] { AuthClaims.SessionVersion }));

        Assert.True(result.Rejected);
    }

    // ── 24 小時絕對上限 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 自登入起剛好24小時_仍有效_超過一秒即失效()
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin();
        var principal = rig.CurrentPrincipal(user);   // 起點＝假時鐘現在

        rig.Clock.Advance(TimeSpan.FromHours(24));
        Assert.False((await rig.Validate(principal)).Rejected);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await rig.Validate(principal)).Rejected);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    public async Task 起點claim缺失或無效_視為無效(string mode)
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin();
        var principal = AuthTestKit.Principal(user.UserId, user.Account, omit: new[] { AuthClaims.SessionStartedAt });
        if (mode == "invalid")
            ((System.Security.Claims.ClaimsIdentity)principal.Identity!).AddClaim(
                new System.Security.Claims.Claim(AuthClaims.SessionStartedAt, "not-a-number"));

        Assert.True((await rig.Validate(principal)).Rejected);
    }

    // ── 權限版本：就地更新而非踢出 ──────────────────────────────────────────

    [Fact]
    public async Task 權限版本不符_重算能力就地更新並續發_保留起點與工作階段版號()
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin();
        rig.Stamp.Bump();
        rig.Stamp.Bump();
        // cookie 簽發於版本 1、當時沒有任何能力；目前版本 2 且此人在 admin 群組
        var startedAt = rig.Clock.GetUtcNow().AddHours(-3);
        var principal = AuthTestKit.Principal(user.UserId, user.Account, permissionVersion: "1", startedAt: startedAt);

        var result = await rig.Validate(principal);

        Assert.False(result.Rejected);
        Assert.True(result.ShouldRenew);
        Assert.Contains(result.Principal!.FindAll(AuthClaims.Capability), c => c.Value == nameof(Capability.Maintain));
        Assert.Equal("2", result.Principal.FindFirst(AuthClaims.PermissionVersion)?.Value);
        Assert.Equal(startedAt.ToUnixTimeSeconds().ToString(), result.Principal.FindFirst(AuthClaims.SessionStartedAt)?.Value);
        Assert.Equal("0", result.Principal.FindFirst(AuthClaims.SessionVersion)?.Value);
        Assert.Equal(0, result.SignOuts);
    }

    [Fact]
    public async Task 沒有pv的舊cookie_視為不同而更新()
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin();

        var result = await rig.Validate(AuthTestKit.Principal(user.UserId, user.Account, permissionVersion: null));

        Assert.True(result.ShouldRenew);
        Assert.Contains(result.Principal!.FindAll(AuthClaims.Capability), c => c.Value == nameof(Capability.Maintain));
    }

    [Fact]
    public async Task 權限版本相同_不更新也不續發()
    {
        var rig = new SessionRig();
        var user = rig.AddAdmin();
        rig.Stamp.Bump();
        var principal = rig.CurrentPrincipal(user);

        var result = await rig.Validate(principal);

        Assert.Same(principal, result.Principal);
        Assert.False(result.ShouldRenew);
    }

    // ── 未登入／拒絕存取的回應 ──────────────────────────────────────────────

    private static async Task<(DefaultHttpContext Context, string Body)> Redirect(
        string pathBase, string path, string query = "", string? rejectReason = null, bool accessDenied = false)
    {
        var rig = new SessionRig();
        var http = new DefaultHttpContext();
        http.Request.PathBase = pathBase;
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        http.Response.Body = new MemoryStream();
        if (rejectReason != null) http.Items[CookieSessionEvents.RejectReasonKey] = rejectReason;

        var context = new RedirectContext<CookieAuthenticationOptions>(
            http, new AuthenticationScheme(SessionCookie.Scheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(), new AuthenticationProperties(), "ignored");
        if (accessDenied) await rig.Events().RedirectToAccessDenied(context);
        else await rig.Events().RedirectToLogin(context);

        http.Response.Body.Position = 0;
        return (http, new StreamReader(http.Response.Body).ReadToEnd());
    }

    [Theory]
    [InlineData("/LogForesight", "/LogForesight/login?returnUrl=%2Frecords")]
    [InlineData("", "/login?returnUrl=%2Frecords")]
    public async Task 未登入的頁面請求_轉址帶掛載前綴而returnUrl不帶(string pathBase, string expected)
    {
        var (context, _) = await Redirect(pathBase, "/records");

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        // returnUrl 是 app 內路徑（不含前綴）——前端登入成功後用 appUrl 還原
        Assert.Equal(expected, context.Response.Headers.Location.ToString());
    }

    /// <summary>查詢字串要一起保留，否則使用者被踢回來之後會失去原本的篩選條件</summary>
    [Fact]
    public async Task 未登入的頁面請求_returnUrl保留查詢字串()
    {
        var (context, _) = await Redirect("/LogForesight", "/records", "?riskLevels=%E9%AB%98");

        var location = context.Response.Headers.Location.ToString();
        Assert.StartsWith("/LogForesight/login?returnUrl=", location);
        Assert.Contains(Uri.EscapeDataString("/records?riskLevels="), location);
    }

    /// <summary>API 請求不轉址：前端 api.js 自己攔 401 導頁（頁面請求才 302）</summary>
    [Fact]
    public async Task 未登入的API請求_在子應用下回401信封而非轉址()
    {
        var (context, body) = await Redirect("/LogForesight", "/api/records");

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.Location.ToString()));
        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(ApiErrorCodes.AuthExpired, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>被拒絕的原因（停用、登出）帶進 401 信封，前端能顯示比「已逾期」更準確的說明</summary>
    [Fact]
    public async Task 被拒絕的API請求_信封帶拒絕原因()
    {
        var (_, body) = await Redirect("", "/api/records", rejectReason: "您的帳號已停用或不存在，請重新登入。");

        using var document = JsonDocument.Parse(body);
        Assert.Equal("您的帳號已停用或不存在，請重新登入。",
            document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task 已登入但沒有權限_回403而非轉去框架預設路徑()
    {
        var (context, _) = await Redirect("", "/api/admin/users", accessDenied: true);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.Location.ToString()));
    }
}
