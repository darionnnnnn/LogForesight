using LogForesight.Web.Models;
using Microsoft.AspNetCore.Diagnostics;

namespace LogForesight.Web.Filters;

/// <summary>
/// MVC 以外的例外出口（中介層、驗證、路由階段擲出的例外，MVC 的 <see cref="ApiExceptionFilter"/> 攔不到）。
/// 只處理 <c>/api</c> 請求並回信封；頁面請求回 false，交給 <c>UseExceptionHandler</c> 的 <c>/error</c> 頁。
/// 對應表與 filter 共用 <see cref="ApiExceptionMapping"/>。
/// </summary>
public sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // UseExceptionHandler 設了 ExceptionHandlingPath 時，會在呼叫 IExceptionHandler 之前就把 Request.Path 改成錯誤頁路徑，
        // 直接看 Request.Path 會把所有 /api 請求都判成頁面請求、改回 HTML 錯誤頁。原始路徑在 IExceptionHandlerPathFeature。
        var originalPath = httpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>()?.Path;
        var path = string.IsNullOrEmpty(originalPath) ? httpContext.Request.Path : new PathString(originalPath);
        if (!ApiStatusEnvelope.IsApiRequest(path)) return false;

        // 判定為 API 後把路徑還原，對應表的稽核與記錄才不會寫成 /error（只在自己處理時還原；
        // 回 false 時中介層要拿錯誤頁路徑重跑管線，不能動）。ExceptionHandlerMiddleware 結束時本來就會還原。
        httpContext.Request.Path = path;
        var (status, code, message) = ApiExceptionMapping.Map(exception, httpContext);
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(code, message), cancellationToken);
        return true;
    }
}

/// <summary>
/// <c>/api</c> 下沒有本文的錯誤狀態（路由找不到、方法不允許、本文過大、媒體類型不符、空本文的 403）補成信封：
/// 前端 api.js 只會解析信封，空本文時只能顯示通用文字，使用者看不出原因。
/// </summary>
public static class ApiStatusEnvelope
{
    public static bool IsApiRequest(HttpRequest request) => IsApiRequest(request.Path);

    public static bool IsApiRequest(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    public static (string Code, string Message)? For(int status) => status switch
    {
        StatusCodes.Status400BadRequest => (ApiErrorCodes.ValidationFailed, "請求格式不正確。"),
        StatusCodes.Status401Unauthorized => (ApiErrorCodes.AuthExpired, "登入已逾期，請重新登入。"),
        StatusCodes.Status403Forbidden => (ApiErrorCodes.Forbidden, "沒有執行此操作的權限。"),
        StatusCodes.Status404NotFound => (ApiErrorCodes.NotFound, "找不到指定的資源。"),
        StatusCodes.Status405MethodNotAllowed => (ApiErrorCodes.MethodNotAllowed, "此資源不支援這個操作方式。"),
        StatusCodes.Status413PayloadTooLarge => (ApiErrorCodes.PayloadTooLarge, "上傳的內容超過允許的大小。"),
        StatusCodes.Status415UnsupportedMediaType => (ApiErrorCodes.UnsupportedMediaType, "不支援的內容格式。"),
        _ => null
    };

    public static async Task WriteAsync(StatusCodeContext context)
    {
        var http = context.HttpContext;
        if (!IsApiRequest(http.Request) || For(http.Response.StatusCode) is not { } envelope) return;
        await http.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(envelope.Code, envelope.Message));
    }
}
