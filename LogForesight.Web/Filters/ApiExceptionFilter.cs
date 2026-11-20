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
/// 對應表在 <see cref="ApiExceptionMapping"/>（與 MVC 以外的 <see cref="ApiExceptionHandler"/> 共用）。
/// 有了這一層，Controller 與 Service 都不寫 try-catch 樣板——
/// 樣板一多就會有人漏寫，錯誤格式也會慢慢長出好幾種。
/// </summary>
public class ApiExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var (status, code, message) = ApiExceptionMapping.Map(context.Exception, context.HttpContext);
        context.Result = new ObjectResult(ApiResponse<object>.Fail(code, message)) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
