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
    /// </summary>
    public static CookieOptions Options(HttpRequest request, DateTimeOffset? expires) =>
        new()
        {
            HttpOnly = true,
            Secure = true,
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
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
    }
}
