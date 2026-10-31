using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LogForesight.Web.Filters;

/// <summary>
/// 全站管理端點的 ViewAll 檢查（docs/WEB-SPEC.md §7.1）：要求目前使用者持有
/// <see cref="Capability.ViewAll"/>，不足時以 <see cref="DomainException"/> 回 403 信封。
/// 掛在 action 或 class 上，授權宣告看得到；不用在 action 開頭記得呼叫一行方法。
/// 只在無條件要求 ViewAll 的端點使用；依參數分支才檢查的端點維持方法內判斷。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireViewAllAttribute : TypeFilterAttribute
{
    public const string DefaultMessage =
        "全站 PRTG 對應、排除及搬運須由可檢視全部主機的管理者操作；受限角色請使用試點與驗收證據入口。";

    public RequireViewAllAttribute(string message = DefaultMessage) : base(typeof(RequireViewAllFilter))
    {
        Arguments = new object[] { message };
    }
}

/// <summary>
/// 以 action filter 實作：例外交給 <see cref="ApiExceptionFilter"/> 統一轉成 403 信封，
/// 且排在模型驗證之後，與原本 action 開頭呼叫的時序相同。
/// </summary>
public sealed class RequireViewAllFilter : IActionFilter
{
    private readonly string _message;
    private readonly ICurrentUser _currentUser;

    public RequireViewAllFilter(string message, ICurrentUser currentUser)
    {
        _message = message;
        _currentUser = currentUser;
    }

    public void OnActionExecuting(ActionExecutingContext context) => _currentUser.RequireViewAll(_message);

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
