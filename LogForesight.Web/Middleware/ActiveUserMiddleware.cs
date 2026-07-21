using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;

namespace LogForesight.Web.Middleware;

/// <summary>
/// 停用帳號即時生效（docs/WEB-SPEC.md §6.3）。
///
/// 為什麼不等 token 自然過期：能力異動可以接受最長 8 小時的延遲（換群組是常態維運），
/// 但**停用是安全事件**——「這個人不該再進來了」如果要等 8 小時，那道命令就沒有意義。
/// 所以每個請求重查一次使用者狀態；JSONL 後端下這是一次小檔案讀取，成本可忽略。
/// </summary>
public class ActiveUserMiddleware
{
    private readonly RequestDelegate _next;

    public ActiveUserMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, IUserStore users, WebAppSettings settings)
    {
        // serverAdmin 不存在於 lf_users，跳過檢查（它的停用手段是改設定檔並重啟）
        if (currentUser.IsAuthenticated && !currentUser.IsServerAdmin)
        {
            var user = users.Get(currentUser.UserId);
            if (user == null || !user.Active)
            {
                context.Response.Cookies.Delete(settings.Jwt.CookieName);

                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(
                        ApiResponse<object>.Fail(ApiErrorCodes.AuthExpired, "您的帳號已停用或不存在，請重新登入。"));
                }
                else
                {
                    context.Response.Redirect("/login");
                }
                return;
            }
        }

        await _next(context);
    }
}
