using System.Reflection;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 測試用：直接呼叫 action 方法不會經過 filter 管線，掛在 action 上的 <see cref="RequireViewAllAttribute"/>
/// 形同不存在。這裡找出 action 上（或控制器 class 上）的屬性，用同一個訊息建出真正的 filter 再執行一次，
/// 驗證「這個 action 確實掛了 ViewAll 檢查、且對指定使用者的結果正確」。
/// </summary>
internal static class ViewAllGuard
{
    public static DomainException? Run(object controller, string actionName, ICurrentUser user)
    {
        var type = controller.GetType();
        var method = type.GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)
                     ?? throw new InvalidOperationException($"{type.Name} 沒有公開方法 {actionName}");
        var attr = method.GetCustomAttributes<RequireViewAllAttribute>(inherit: true).FirstOrDefault()
                   ?? type.GetCustomAttributes<RequireViewAllAttribute>(inherit: true).FirstOrDefault();
        Assert.True(attr != null, $"{type.Name}.{actionName} 沒有 [RequireViewAll]");

        var filter = new RequireViewAllFilter((string)attr!.Arguments![0], user);
        var context = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller);
        try
        {
            filter.OnActionExecuting(context);
            return null;
        }
        catch (DomainException ex)
        {
            return ex;
        }
    }

    public static void AssertForbidden(object controller, string actionName, ICurrentUser user)
    {
        var ex = Run(controller, actionName, user);
        Assert.NotNull(ex);
        Assert.Equal(ApiErrorCodes.Forbidden, ex!.Code);
    }

    public static void AssertAllowed(object controller, string actionName, ICurrentUser user) =>
        Assert.Null(Run(controller, actionName, user));
}
