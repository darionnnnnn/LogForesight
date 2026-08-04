using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LogForesight.Web.Filters;

/// <summary>
/// 能力檢查（docs/WEB-SPEC.md §7.1 三層授權的第 2 層）。
/// 用法：<c>[Permission(Capability.Assign)]</c>；可傳多個能力（任一持有即過，OR 語意——
/// docs/archive/FEEDBACK-6-PLAN.md §2「排程作業」頁面同時開放 DevMonitor 與 Maintain 進入的用例）：
/// <c>[Permission(Capability.DevMonitor, Capability.Maintain)]</c>。
///
/// 注意這只回答「能不能用這個功能」。**「能看哪些主機的資料」是 Service 層的職責**——
/// 即使某個 API 忘了掛這個屬性，查詢仍只會回授權範圍內的資料，那是不可繞過的最後防線。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class PermissionAttribute : TypeFilterAttribute
{
    public PermissionAttribute(params Capability[] capabilities) : base(typeof(PermissionFilter))
    {
        Arguments = new object[] { capabilities };
    }
}

public class PermissionFilter : IAuthorizationFilter
{
    private readonly Capability[] _capabilities;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    public PermissionFilter(Capability[] capabilities, ICurrentUser currentUser, IAuditService audit)
    {
        _capabilities = capabilities;
        _currentUser = currentUser;
        _audit = audit;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!_currentUser.IsAuthenticated)
        {
            // 未登入交給驗證管線處理（API 得到 401、頁面被導向登入頁），這裡不重複處理
            return;
        }

        if (_capabilities.Any(_currentUser.Has)) return;

        // 權限不足的嘗試要記錄：這是稽核上最有價值的行之一。
        // 一套專門偵測入侵跡象的系統，對「有人在試探自己的權限邊界」不該視而不見。
        var capabilityLabel = string.Join("／", _capabilities);
        _audit.Record(
            action: "access_denied",
            summary: $"權限不足：嘗試存取需要「{capabilityLabel}」能力的功能 {context.HttpContext.Request.Method} {context.HttpContext.Request.Path}",
            targetKind: "auth",
            targetId: capabilityLabel,
            result: AuditResult.Denied);

        // API 回信封讓前端處理；頁面導向說明頁——瀏覽器直接開一個沒權限的網址時
        // 看到一坨 JSON 是很差的體驗，而且看不出「是沒權限還是壞掉了」
        if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            context.Result = new ObjectResult(
                ApiResponse<object>.Fail(ApiErrorCodes.Forbidden, "您沒有使用這項功能的權限。"))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
        else
        {
            context.Result = new RedirectResult("/access-denied");
        }
    }
}
