using System.IdentityModel.Tokens.Jwt;
using LogForesight.Web.Auth;
using System.Security.Claims;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Services;

namespace LogForesight.Web.Middleware;

/// <summary>
/// 停用帳號即時生效（docs/WEB-SPEC.md §6.3）＋登出撤銷＋權限變更即時生效。
///
/// 為什麼不等 token 自然過期：**停用是安全事件**——「這個人不該再進來了」如果要等 8 小時，
/// 那道命令就沒有意義。所以每個請求重查一次使用者狀態；JSONL 後端下這是一次小檔案讀取，成本可忽略。
///
/// 能力異動（被移出 admin 群組、負責人變更）同樣不能等 8 小時：token 帶簽發當下的權限版本號，
/// 與目前版本不同就當場重算能力、換發 cookie，並替換這個請求的 <c>HttpContext.User</c>。
/// </summary>
public class ActiveUserMiddleware
{
    private readonly RequestDelegate _next;

    public ActiveUserMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUser currentUser,
        IUserStore users,
        WebAppSettings settings,
        IdentityService identity,
        JwtTokenService tokens,
        PermissionVersionStamp permissionVersion,
        RevokedTokens revoked)
    {
        if (currentUser.IsAuthenticated)
        {
            var jti = context.User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
            if (string.IsNullOrWhiteSpace(jti))
            {
                await Reject(context, settings, "登入資訊無效，請重新登入。");
                return;
            }

            // 已登出的 token 比照停用處理（serverAdmin 也一樣：登出撤銷與帳號是否在 lf_users 無關）
            if (revoked.IsRevoked(jti))
            {
                await Reject(context, settings, "您已登出，請重新登入。");
                return;
            }

            // serverAdmin 不存在於 lf_users，跳過檢查（它的停用手段是改設定檔並重啟；能力固定，不需換發）
            if (!currentUser.IsServerAdmin)
            {
                var user = users.Get(currentUser.UserId);
                if (user == null || !user.Active)
                {
                    await Reject(context, settings, "您的帳號已停用或不存在，請重新登入。");
                    return;
                }

                // 權限版本不符（含沒有 pv 的舊 token）→ 無感刷新，**不是**踢出登入：
                // 群組編輯很頻繁，每改一次就讓全員重新登入不可接受；這裡重算能力、換發 cookie，
                // 並替換本請求的 User，後段的授權判斷與 ICurrentUser 立即讀到新能力。
                var tokenVersion = context.User.FindFirst(JwtTokenService.PermissionVersionClaim)?.Value;
                var currentVersion = permissionVersion.Current.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(tokenVersion, currentVersion, StringComparison.Ordinal))
                {
                    var tokenIdentity = identity.CreateTokenIdentity(user);
                    if (!TryGetExistingTokenExpiry(context.User, out var expiresAt))
                    {
                        await Reject(context, settings, "登入資訊已失效，請重新登入。");
                        return;
                    }

                    var issued = tokens.CreateTokenAndPrincipal(tokenIdentity, expiresAt, jti);
                    AuthCookie.Append(context.Response, context.Request, settings.Jwt.CookieName,
                        issued.Token, issued.ExpiresAt);
                    context.User = issued.Principal;
                }
            }
        }

        await _next(context);
    }

    private static bool TryGetExistingTokenExpiry(ClaimsPrincipal principal, out DateTimeOffset expiresAt)
    {
        var value = principal.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
        if (long.TryParse(value, out var seconds))
        {
            try
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                expiresAt = default;
                return false;
            }

            return expiresAt > DateTimeOffset.UtcNow;
        }

        expiresAt = default;
        return false;
    }

    private static async Task Reject(HttpContext context, WebAppSettings settings, string message)
    {
        AuthCookie.Delete(context.Response, context.Request, settings.Jwt.CookieName);

        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                ApiResponse<object>.Fail(ApiErrorCodes.AuthExpired, message));
        }
        else
        {
            context.Response.Redirect($"{context.Request.PathBase}/login");
        }
    }
}
