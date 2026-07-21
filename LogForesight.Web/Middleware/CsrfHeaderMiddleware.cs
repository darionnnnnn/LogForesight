using LogForesight.Web.Models;

namespace LogForesight.Web.Middleware;

/// <summary>
/// CSRF 防禦的第二層（docs/WEB-SPEC.md §6.4）：所有**非 GET 的 API 請求**
/// 必須帶自訂標頭 <c>X-Requested-By</c>。
///
/// 為什麼夠：跨站的表單提交無法自訂標頭（只能送 simple headers），要帶自訂標頭
/// 就得走 CORS 預檢，而預檢會被同源政策擋下。第一層是 SameSite=Strict Cookie，
/// 兩層都破才會失守。
///
/// 為什麼不用 ASP.NET Antiforgery token：它假設「伺服器渲染表單 → 表單 post」的模型，
/// 與本專案「View 只是殼、資料全走 API」的架構不合，硬套會多出一條 token 傳遞路徑。
/// </summary>
public class CsrfHeaderMiddleware
{
    public const string HeaderName = "X-Requested-By";
    public const string HeaderValue = "LogForesight";

    private readonly RequestDelegate _next;

    public CsrfHeaderMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresHeader(context) && !HasValidHeader(context))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                ApiResponse<object>.Fail(ApiErrorCodes.ValidationFailed, "請求來源驗證失敗，請重新整理頁面後再試。"));
            return;
        }

        await _next(context);
    }

    private static bool RequiresHeader(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) return false;

        return !HttpMethods.IsGet(context.Request.Method)
               && !HttpMethods.IsHead(context.Request.Method)
               && !HttpMethods.IsOptions(context.Request.Method);
    }

    private static bool HasValidHeader(HttpContext context) =>
        context.Request.Headers.TryGetValue(HeaderName, out var value) &&
        string.Equals(value, HeaderValue, StringComparison.Ordinal);
}
