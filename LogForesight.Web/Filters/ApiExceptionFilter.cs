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
                ApiErrorCodes.StatusOf(domain.Code),
                domain.Code,
                domain.Message),

            // 樂觀鎖衝突（docs/archive/SCALE-FIX-PLAN-2026-08-06.md D3）：多人同時操作的正常結果，
            // 不是故障——store 已把「哪一筆被搶先改了」寫進訊息，直接給使用者看。
            // 例外型別在 Core（拋出點是 store，Core 不能參照 Web 的 DomainException）
            LogForesight.Core.Persistence.ConcurrentUpdateException conflict => (
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                conflict.Message),

            // 用戶端已斷線（關分頁／取消請求）：沒人收回應，不是故障
            OperationCanceledException when context.HttpContext.RequestAborted.IsCancellationRequested => (
                499,
                ApiErrorCodes.ServerError,
                "請求已取消。"),

            // 請求沒被取消卻拋 OCE：多半是 HttpClient 逾時（外部服務太慢），不是本站程式錯誤
            OperationCanceledException => (
                ApiErrorCodes.StatusOf(ApiErrorCodes.UpstreamTimeout),
                ApiErrorCodes.UpstreamTimeout,
                "外部服務回應逾時，請稍後再試。"),

            _ => (
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.ServerError,
                "系統發生未預期的錯誤，請稍後再試；若持續發生請聯繫系統管理員。")
        };

        if (status == 499)
        {
            Log.Debug("API 請求已被用戶端取消（{0} {1}）",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path);
        }
        else if (code == ApiErrorCodes.UpstreamTimeout)
        {
            Log.Warn("API 外部服務逾時（{0} {1}）：{2}",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path, context.Exception.GetType().Name);
        }
        else if (status >= 500)
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
}
