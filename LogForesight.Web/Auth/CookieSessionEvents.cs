using System.Globalization;
using System.Security.Claims;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LogForesight.Web.Auth;

/// <summary>
/// 登入 cookie 的逐請求驗證與未登入／拒絕存取的回應（docs/WEB-SPEC.md §6.3）。
///
/// 為什麼每個請求重查：**停用與登出是安全事件**——「這個人不該再進來了」不能等 cookie 自然到期。
/// 逐請求檢查三件事：
/// 1. 使用者仍存在且啟用（serverAdmin 不在 lf_users，跳過；它的停用手段是改設定檔並重啟——設定戳記併在工作階段版號內，改了之後舊 cookie 在第 2 項失效）；
/// 2. 工作階段版號與使用者目前的相同（登出或停用時已遞增）；
/// 3. 權限版本號與目前相同；不同就重算能力、**就地更新主體並續發 cookie**，不是踢出登入——
///    群組編輯很頻繁，每改一次就讓全員重新登入不可接受。
/// 另外自登入起超過 <see cref="SessionCookie.AbsoluteLifetime"/> 一律失效。
/// </summary>
public sealed class CookieSessionEvents : CookieAuthenticationEvents
{
    /// <summary><c>HttpContext.Items</c> 的鍵：這次請求被拒絕的原因（可直接顯示的中文），給未登入回應用</summary>
    internal const string RejectReasonKey = "lf.auth.reject-reason";

    /// <summary>
    /// 背景輪詢（執行告示、徽章）請求帶的標頭。這類請求不代表使用者在操作，不得滑動延長閒置時限，
    /// 否則前景分頁開著就永不閒置登出（WEB-SPEC §6.1）。
    /// </summary>
    public const string BackgroundHeader = "X-Lf-Background";

    private readonly IUserStore _users;
    private readonly SessionVersionStore _versions;
    private readonly PermissionVersionStamp _permissionVersion;
    private readonly Services.IdentityService _identity;
    private readonly SessionPrincipalFactory _principals;

    public CookieSessionEvents(
        IUserStore users,
        SessionVersionStore versions,
        PermissionVersionStamp permissionVersion,
        Services.IdentityService identity,
        SessionPrincipalFactory principals)
    {
        _users = users;
        _versions = versions;
        _permissionVersion = permissionVersion;
        _identity = identity;
        _principals = principals;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal?.Identity?.IsAuthenticated != true) return;

        var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();

        if (!TryReadLong(principal, AuthClaims.UserId, out var userId)
            || !TryReadLong(principal, AuthClaims.SessionVersion, out var sessionVersion)
            || !TryReadLong(principal, AuthClaims.SessionStartedAt, out var startedSeconds))
        {
            await Reject(context, "登入資訊無效，請重新登入。");
            return;
        }

        DateTimeOffset startedAt;
        try
        {
            startedAt = DateTimeOffset.FromUnixTimeSeconds(startedSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            await Reject(context, "登入資訊無效，請重新登入。");
            return;
        }

        if (now - startedAt > SessionCookie.AbsoluteLifetime)
        {
            await Reject(context, "登入已逾期，請重新登入。");
            return;
        }

        var isServerAdmin = principal.FindFirst(AuthClaims.ServerAdmin)?.Value == "1";
        WebUser? user = null;
        if (!isServerAdmin)
        {
            user = _users.Get(userId);
            if (user == null || !user.Active)
            {
                await Reject(context, "您的帳號已停用或不存在，請重新登入。");
                return;
            }
        }

        if (_versions.Current(isServerAdmin, user) != sessionVersion)
        {
            await Reject(context, "您已登出，請重新登入。");
            return;
        }

        // serverAdmin 的能力固定，不需更新
        if (user == null) return;

        var currentVersion = _permissionVersion.Current.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(principal.FindFirst(AuthClaims.PermissionVersion)?.Value, currentVersion, StringComparison.Ordinal))
        {
            // 就地更新：後段的授權判斷與 ICurrentUser 立即讀到新能力；續發 cookie 讓下一個請求版號相符
            context.ReplacePrincipal(_principals.Create(_identity.CreateSessionIdentity(user), sessionVersion, startedAt));
            context.ShouldRenew = true;
        }
    }

    /// <summary>
    /// 背景輪詢請求不續發 cookie（不滑動延長）。權限版號變更時 <see cref="ValidatePrincipal"/> 的就地更新續發
    /// 走另一條（ShouldRenew），不受這裡影響——那是必要的換主體，不是活動延長。
    /// </summary>
    public override Task CheckSlidingExpiration(CookieSlidingExpirationContext context)
    {
        if (context.HttpContext.Request.Headers.ContainsKey(BackgroundHeader)) context.ShouldRenew = false;
        return Task.CompletedTask;
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        var http = context.HttpContext;

        // API 與頁面待遇不同：API 回 401 信封讓前端攔截後導頁；頁面直接 302，使用者不會先看到空殼再被踢出去
        if (ApiStatusEnvelope.IsApiRequest(http.Request))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            var message = http.Items[RejectReasonKey] as string
                ?? ApiStatusEnvelope.For(StatusCodes.Status401Unauthorized)!.Value.Message;
            return http.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(ApiErrorCodes.AuthExpired, message));
        }

        // Request.Path 已扣掉掛載前綴，returnUrl 存的是 app 內路徑（前端用 appUrl 還原）；
        // 轉址目標本身要補回前綴才到得了登入頁
        var returnUrl = Uri.EscapeDataString(http.Request.Path + http.Request.QueryString);
        http.Response.Redirect($"{http.Request.PathBase}/login?returnUrl={returnUrl}");
        return Task.CompletedTask;
    }

    /// <summary>已登入但沒有權限：交給狀態碼信封出口補本文（/api），不轉去框架預設的 AccessDenied 路徑</summary>
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static async Task Reject(CookieValidatePrincipalContext context, string message)
    {
        context.RejectPrincipal();
        context.HttpContext.Items[RejectReasonKey] = message;
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }

    private static bool TryReadLong(ClaimsPrincipal principal, string type, out long value)
    {
        value = 0;
        return principal.FindFirst(type) is { } claim
            && long.TryParse(claim.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
