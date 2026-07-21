using LogForesight.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LogForesight.Web.Filters;

/// <summary>
/// API 例外的單點處理（docs/WEB-SPEC.md §7.2）。
///
/// - <see cref="DomainException"/>（業務錯誤）→ 對應的 4xx ＋ 信封，訊息直接顯示給使用者
/// - 其他未捕捉例外 → 500 ＋ 通用訊息，**完整堆疊寫診斷 log**
///   （不把例外細節回傳前端：那會洩漏內部結構，而且對使用者也沒有意義）
///
/// 有了這一層，Controller 與 Service 都不寫 try-catch 樣板——
/// 樣板一多就會有人漏寫，錯誤格式也會慢慢長出好幾種。
/// </summary>
public class ApiExceptionFilter : IExceptionFilter
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public void OnException(ExceptionContext context)
    {
        var (status, code, message) = context.Exception switch
        {
            DomainException domain => (
                StatusCodeFor(domain.Code),
                domain.Code,
                domain.Message),

            _ => (
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.ServerError,
                "系統發生未預期的錯誤，請稍後再試；若持續發生請聯繫系統管理員。")
        };

        if (status >= 500)
        {
            Log.Error(context.Exception, "API 未預期例外：{0} {1}",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path);
        }
        else
        {
            // 業務錯誤是正常流程的一部分（使用者輸入不合格等），記 Info 供追蹤即可，不是故障
            Log.Info("API 業務錯誤 [{0}]：{1}（{2} {3}）", code, message,
                context.HttpContext.Request.Method, context.HttpContext.Request.Path);
        }

        context.Result = new ObjectResult(ApiResponse<object>.Fail(code, message)) { StatusCode = status };
        context.ExceptionHandled = true;
    }

    private static int StatusCodeFor(string code) => code switch
    {
        ApiErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ApiErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ApiErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ApiErrorCodes.AuthExpired => StatusCodes.Status401Unauthorized,
        _ => StatusCodes.Status400BadRequest
    };
}
