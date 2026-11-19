using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace LogForesight.Web.Auth;

/// <summary>登入 cookie 的常數與共用判斷（內建 Cookie Authentication，docs/WEB-SPEC.md §6.1）</summary>
public static class SessionCookie
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>連續登入的絕對上限：滑動延長再久，自登入起超過這段時間一律重新登入。固定常數，不開放設定</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// 改用內建 Cookie Authentication 之前的 cookie 名稱（當時放的是 JWT）。新 cookie 換了名稱，
    /// 舊名稱的 cookie 內容不是現行格式，帶舊名稱的請求在回應清掉它（<see cref="Middleware.LegacyAuthCookieMiddleware"/>）。
    /// </summary>
    public const string LegacyCookieName = "lf_auth";

    /// <summary>
    /// 這次連線該不該掛 Secure 旗標。除了 <c>Request.IsHttps</c>，也認 <c>X-Forwarded-Proto</c>——
    /// 反向代理（IIS ARR／Nginx）終止 TLS 後以 HTTP 轉發時 <c>IsHttps</c> 是 false，
    /// 只看它會讓「使用者明明走 HTTPS」的 cookie 少掉 Secure 旗標，是安全降級。
    ///
    /// 這個標頭未經 <c>UseForwardedHeaders</c> 驗證，但**偽造它只會讓 Secure 變成 true**
    /// （更嚴格，攻擊者只能鎖住自己），不存在往寬鬆方向被利用的路徑，因此不需要 KnownProxies 名單。
    /// http 直連（沒有這個標頭）一律不掛 Secure：寫死 true 在 HTTP 部署下會被瀏覽器靜默丟棄 cookie，
    /// 症狀是登入成功卻被打回登入頁、畫面與記錄檔都沒有錯誤。
    /// </summary>
    public static bool IsSecureConnection(HttpRequest request)
    {
        if (request.IsHttps) return true;

        // 多層代理會把值累加成逗號清單（例「https, http」），最靠近使用者的是第一段
        var proto = request.Headers["X-Forwarded-Proto"].ToString();
        var first = proto.Split(',')[0].Trim();
        return string.Equals(first, "https", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>cookie 的 Path：跟著站台掛載路徑走（編碼後形式，瀏覽器 path-match 比對的是請求 URL 的編碼後路徑）</summary>
    public static string CookiePath(HttpRequest request) =>
        request.PathBase.HasValue ? request.PathBase.ToUriComponent() : "/";
}

/// <summary>
/// 登入 cookie 的讀寫出口：包住框架預設的 <see cref="ChunkingCookieManager"/>，寫入與刪除時補上
/// 「反向代理標示 HTTPS 就掛 Secure」（<see cref="SessionCookie.IsSecureConnection"/>）。
/// 放在這一層而不是 <c>SigningIn</c> 事件：滑動延長與就地續發不經 <c>SigningIn</c>，只有 cookie 管理員涵蓋所有寫入路徑，
/// 否則續發後的 cookie 會悄悄掉掉 Secure。
/// </summary>
public sealed class ForwardedSecureCookieManager : ICookieManager
{
    private readonly ICookieManager _inner = new ChunkingCookieManager();

    public string? GetRequestCookie(HttpContext context, string key) => _inner.GetRequestCookie(context, key);

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options)
    {
        if (SessionCookie.IsSecureConnection(context.Request)) options.Secure = true;
        _inner.AppendResponseCookie(context, key, value, options);
    }

    public void DeleteCookie(HttpContext context, string key, CookieOptions options)
    {
        if (SessionCookie.IsSecureConnection(context.Request)) options.Secure = true;
        _inner.DeleteCookie(context, key, options);
    }
}

/// <summary>登入／登出的出入口：controller 只依賴這個介面，單元測試不必組出整套驗證服務</summary>
public interface ISessionSigner
{
    /// <summary>以指定身分建立登入 cookie（工作階段版號取當下值、起點取現在）</summary>
    Task SignInAsync(HttpContext context, SessionIdentity identity);

    /// <summary>登出：遞增使用者層級的工作階段版號（所有既有 cookie 失效）並清除本次 cookie</summary>
    Task SignOutAsync(HttpContext context, long userId, bool isServerAdmin);
}

public sealed class CookieSessionSigner : ISessionSigner
{
    private readonly SessionPrincipalFactory _principals;
    private readonly SessionVersionStore _versions;
    private readonly TimeProvider _clock;

    public CookieSessionSigner(SessionPrincipalFactory principals, SessionVersionStore versions, TimeProvider clock)
    {
        _principals = principals;
        _versions = versions;
        _clock = clock;
    }

    public Task SignInAsync(HttpContext context, SessionIdentity identity)
    {
        var principal = _principals.Create(
            identity, _versions.CurrentFor(identity.UserId, identity.IsServerAdmin), _clock.GetUtcNow());
        return context.SignInAsync(SessionCookie.Scheme, principal,
            new AuthenticationProperties { IsPersistent = true, AllowRefresh = true });
    }

    public Task SignOutAsync(HttpContext context, long userId, bool isServerAdmin)
    {
        if (isServerAdmin || userId > 0) _versions.Bump(userId, isServerAdmin);
        return context.SignOutAsync(SessionCookie.Scheme);
    }
}
