using LogForesight.Web.Auth;

namespace LogForesight.Web.Middleware;

/// <summary>
/// 清除改用內建 Cookie Authentication 之前的登入 cookie（舊名稱 <see cref="SessionCookie.LegacyCookieName"/>，內容是 JWT）。
/// 新 cookie 換了名稱，舊的不會被讀成登入，但會一直留在瀏覽器；請求帶著舊名稱時在回應刪除它。
/// 舊版掛在子 Application 時寫過 <c>Path=/</c> 與 <c>Path={PathBase}</c> 兩種，兩個 Path 都刪。
/// 沒有舊 cookie 的請求完全不動作，數版後可整支移除。
/// </summary>
public sealed class LegacyAuthCookieMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _currentCookieName;

    public LegacyAuthCookieMiddleware(RequestDelegate next, string currentCookieName)
    {
        _next = next;
        _currentCookieName = currentCookieName;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var legacy = SessionCookie.LegacyCookieName;
        if (!string.Equals(legacy, _currentCookieName, StringComparison.Ordinal)
            && context.Request.Cookies.ContainsKey(legacy))
        {
            var secure = SessionCookie.IsSecureConnection(context.Request);
            // 順序有意義：先刪根路徑、再刪掛載前綴。框架的 Set-Cookie 寫入實測會讓後寫的同名項目取代先寫的：
            // 先寫前綴路徑再寫根路徑時，前綴路徑那筆消失，瀏覽器就只刪了一張（LegacyAuthCookieMiddlewareTests 守住順序）
            foreach (var path in new[] { "/", SessionCookie.CookiePath(context.Request) }.Distinct())
            {
                context.Response.Cookies.Delete(legacy, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = secure,
                    SameSite = SameSiteMode.Strict,
                    Path = path
                });
            }
        }

        return _next(context);
    }
}
