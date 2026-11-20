using LogForesight.Web.Models;

namespace LogForesight.Web.Filters;

/// <summary>
/// 例外 → (HTTP 狀態, 錯誤碼, 給使用者的訊息) 的唯一對應表（docs/WEB-SPEC.md §7.2）。
/// MVC 內由 <see cref="ApiExceptionFilter"/>、MVC 以外（中介層、驗證、路由）由 <see cref="ApiExceptionHandler"/> 套用，
/// 兩個出口共用這一份：新增專屬例外型別只改這裡（並登錄 <see cref="UserFacingMessage"/>）。
/// 非領域例外一律回固定說明，原文只進記錄檔。
/// </summary>
public static class ApiExceptionMapping
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetLogger("LogForesight.Web.Filters.ApiExceptionFilter");

    public static (int Status, string Code, string Message) Map(Exception exception, HttpContext httpContext)
    {
        var (status, code, message) = exception switch
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

            // PRTG 範圍外清除作業佔用範圍寫入槽：多人／背景同時操作的正常結果，不是故障，回 409。
            // 型別繼承 InvalidOperationException，所以必須排在任何會先吃掉 InvalidOperationException 的 arm 之前
            LogForesight.Core.Persistence.PrtgScopePurgeBusyException busy => (
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                busy.Message),

            // 站台金鑰無法使用：不是使用者輸入的錯，是站台環境問題，回 503 並原樣顯示處理方式
            LogForesight.Core.CryptoKeyUnavailableException keyUnavailable => (
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorCodes.ServiceUnavailable,
                keyUnavailable.Message),

            // 協調器自己判定的規則違反（成員不屬於本單、已結案、缺原因……）：訊息是協調器寫好的中文，回 400。
            // 所有呼叫協調器的路徑（交辦、回覆、看板建單、處理面板）共用這一處，服務層不各抄一份 try/catch
            LogForesight.Core.Persistence.WorkOrderRuleException rule => (
                ApiErrorCodes.StatusOf(ApiErrorCodes.ValidationFailed),
                ApiErrorCodes.ValidationFailed,
                rule.Message),

            // PRTG 對應／排除範圍被別人先改了（樂觀鎖衝突）：訊息固定中文，回 409
            LogForesight.Core.Persistence.Sql.PrtgScopeConflictException scopeConflict => (
                StatusCodes.Status409Conflict,
                ApiErrorCodes.Conflict,
                scopeConflict.Message),

            // NetIQ 探索的領域錯誤（輸入格式、掃描預算、連線失敗說明）：訊息是寫好的中文，回 400
            LogForesight.Web.Services.NetiqDiscoveryException discovery => (
                ApiErrorCodes.StatusOf(ApiErrorCodes.ValidationFailed),
                ApiErrorCodes.ValidationFailed,
                discovery.Message),

            // PRTG 診斷傳輸儲存層的領域例外：狀態碼與錯誤碼由拋出點決定（例如 403 transfer_binding_changed、
            // 409 transfer_context_changed），訊息本身就是給使用者看的。專屬型別，不會誤傷其他控制器
            LogForesight.Core.Persistence.Sql.PrtgTransferStoreException transfer => (
                transfer.HttpStatusCode,
                transfer.Code,
                transfer.Message),

            // 排程作業診斷匯出的去識別化失敗：中止匯出、不給檔案。訊息與例外屬性只含來源名稱與殘留類別次數，
            // 不含原值，可以直接回給使用者並寫進 log
            LogForesight.Web.Services.RunExportDeidentificationException deidentification => (
                ApiErrorCodes.StatusOf(ApiErrorCodes.ExportDeidentificationFailed),
                ApiErrorCodes.ExportDeidentificationFailed,
                deidentification.Message),

            // Kestrel／模型繫結層的請求格式錯誤：本文超過上限（413）或格式壞掉（400）。不是程式錯誤，不能落到 500
            Microsoft.AspNetCore.Http.BadHttpRequestException bad => bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? (StatusCodes.Status413PayloadTooLarge, ApiErrorCodes.PayloadTooLarge, "上傳的內容超過允許的大小。")
                : (StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "請求格式不正確。"),

            // 用戶端已斷線（關分頁／取消請求）：沒人收回應，不是故障
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (
                499,
                ApiErrorCodes.RequestCancelled,
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

        LogOutcome(exception, httpContext, status, code, message);
        if (status == StatusCodes.Status403Forbidden) AuditDenied(httpContext, message);
        return (status, code, message);
    }

    /// <summary>
    /// 權限不足的嘗試一律寫稽核（與 PermissionFilter 相同的動作代碼）：action 內與 RequireViewAll 擲出的拒絕
    /// 都經過這裡，不必各自記得寫。單元測試的 DefaultHttpContext 沒有服務容器時略過。
    /// </summary>
    private static void AuditDenied(HttpContext httpContext, string message)
    {
        var audit = httpContext.RequestServices?.GetService(typeof(LogForesight.Web.Services.IAuditService)) as LogForesight.Web.Services.IAuditService;
        audit?.Record(
            action: LogForesight.Core.Models.AuditActions.AccessDenied,
            summary: $"權限不足：{httpContext.Request.Method} {httpContext.Request.Path}（{message}）",
            targetKind: "auth",
            targetId: httpContext.Request.Path.Value ?? string.Empty,
            result: LogForesight.Core.Models.AuditResult.Denied);
    }

    private static void LogOutcome(Exception exception, HttpContext httpContext, int status, string code, string message)
    {
        if (status == 499)
        {
            Log.Debug("API 請求已被用戶端取消（{0} {1}）", httpContext.Request.Method, httpContext.Request.Path);
        }
        else if (code == ApiErrorCodes.UpstreamTimeout)
        {
            Log.Warn("API 外部服務逾時（{0} {1}）：{2}", httpContext.Request.Method, httpContext.Request.Path, exception.GetType().Name);
        }
        else if (status >= 500)
        {
            Log.Error(exception, "API 未預期例外：{0} {1}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            // 業務錯誤是正常流程的一部分（使用者輸入不合格等），記 Info 供追蹤即可，不是故障
            Log.Info("API 業務錯誤 [{0}]：{1}（{2} {3}）", code, message, httpContext.Request.Method, httpContext.Request.Path);
        }
    }
}
