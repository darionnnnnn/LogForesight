namespace LogForesight.Web.Auth;

/// <summary>
/// 驗證 Cookie 的單一定義點：寫入、登出、強制登出三處共用——CookieOptions（尤其 Path）
/// 只要有一處不一致，刪除就等於沒刪（瀏覽器把不同 Path 的同名 Cookie 當成不同張）。
/// </summary>
public static class AuthCookie
{
    /// <summary>
    /// Path 跟著站台的掛載路徑走：同一台主機掛兩個 LogForesight 子 Application（正式／測試
    /// 很常見）時，兩者的 Cookie 同名，Path 都寫 "/" 會讓後登入的直接蓋掉前一個——症狀是
    /// 「身分變成另一個環境的人」，比 401 難查得多。
    /// 用 ToUriComponent()（編碼後形式）而不是 Value：瀏覽器做 path-match 比對的是請求 URL
    /// 的編碼後路徑，掛載名稱含空白或非 ASCII 時，解碼後的 Path 永遠比不中，Cookie 就不會被送回。
    /// Secure 跟隨連線是否為 HTTPS（見 <see cref="IsSecureConnection"/>）：寫死 true 在 HTTP 部署下
    /// 會被瀏覽器靜默丟棄 Cookie，症狀是登入成功卻被打回登入頁、畫面與記錄檔都沒有錯誤。
    /// </summary>
    public static CookieOptions Options(HttpRequest request, DateTimeOffset? expires) =>
        new()
        {
            HttpOnly = true,
            Secure = IsSecureConnection(request),
            SameSite = SameSiteMode.Strict,
            Expires = expires,
            Path = request.PathBase.HasValue ? request.PathBase.ToUriComponent() : "/"
        };

    /// <summary>
    /// 寫入 token。掛在子 Application 時**先清掉舊版遺留的 `Path=/` Cookie**：升級前的版本
    /// 一律寫 `Path=/`，那張與新的 `Path={PathBase}` 同名並存，而 ASP.NET Core 解析 Cookie
    /// 標頭時後到的值覆寫先到的——舊 token 會蓋過新 token，使用者重登也救不回來，
    /// 只能自己清瀏覽器。這行清理無副作用（根路徑本來就不該有這張），可在數版後移除。
    /// </summary>
    public static void Append(HttpResponse response, HttpRequest request, string name, string token, DateTimeOffset expires)
    {
        DeleteLegacyRootCookie(response, request, name);
        response.Cookies.Append(name, token, Options(request, expires));
    }

    /// <summary>刪除 token（登出與強制登出共用）。連同舊版遺留的 `Path=/` 一併刪——
    /// 只刪新範圍的話，登出後那張舊 token 仍然有效。</summary>
    public static void Delete(HttpResponse response, HttpRequest request, string name)
    {
        DeleteLegacyRootCookie(response, request, name);
        response.Cookies.Delete(name, Options(request, expires: null));
    }

    private static void DeleteLegacyRootCookie(HttpResponse response, HttpRequest request, string name)
    {
        if (!request.PathBase.HasValue) return;   // 掛根目錄時 Options 的 Path 就是 "/"，下面那次刪除已涵蓋

        response.Cookies.Delete(name, new CookieOptions
        {
            HttpOnly = true,
            Secure = IsSecureConnection(request),
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
    }

    /// <summary>
    /// 這次連線該不該掛 Secure 旗標。除了 <c>Request.IsHttps</c>，也認 <c>X-Forwarded-Proto</c>——
    /// 反向代理（IIS ARR／Nginx）終止 TLS 後以 HTTP 轉發時 <c>IsHttps</c> 是 false，
    /// 只看它會讓「使用者明明走 HTTPS」的 token cookie 少掉 Secure 旗標，是安全降級。
    ///
    /// 這個標頭未經 <c>UseForwardedHeaders</c> 驗證，但**偽造它只會讓 Secure 變成 true**
    /// （更嚴格，攻擊者只能鎖住自己），不存在往寬鬆方向被利用的路徑，因此不需要 KnownProxies 名單。
    /// </summary>
    private static bool IsSecureConnection(HttpRequest request) =>
        request.IsHttps ||
        string.Equals(request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
}
