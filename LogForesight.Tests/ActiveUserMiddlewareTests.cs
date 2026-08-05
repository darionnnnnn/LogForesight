using System.Text.Json;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Middleware;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 停用帳號即時生效（docs/WEB-SPEC.md §6.3）——§5（回饋第十一輪）補上的驗收。
///
/// 登入那一關早有測試（IdentityServiceTests.Login_停用帳號_被拒），但「登入後才被停用」
/// 走的是完全不同的路徑：token 還有效、請求照常帶 Cookie 進來，唯一的防線就是這個
/// middleware 每請求重查 lf_users.Active。沒有測試等於這道防線只存在於註解裡。
/// </summary>
public class ActiveUserMiddlewareTests
{
    private readonly FakeUserStore _users = new();
    private static readonly WebAppSettings Settings = new();

    /// <summary>跑一次 middleware，回傳 (是否放行到下一段, HttpContext)</summary>
    private async Task<(bool Reached, DefaultHttpContext Context)> Invoke(string path, ICurrentUser currentUser)
    {
        var reached = false;
        var middleware = new ActiveUserMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, currentUser, _users, Settings);
        return (reached, context);
    }

    [Fact]
    public async Task 啟用中的使用者_放行()
    {
        var user = _users.Upsert(new WebUser { Account = "DOMAIN\\wang", Active = true });

        var (reached, context) = await Invoke("/api/records", FakeCurrentUser.ForUser(user.UserId));

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>停用後不等 token 過期：下一個 API 請求立刻 401，且回的是前端認得的 auth_expired 信封</summary>
    [Fact]
    public async Task 已停用的使用者_API請求回401並帶auth_expired()
    {
        var user = _users.Upsert(new WebUser { Account = "DOMAIN\\wang", Active = false });

        var (reached, context) = await Invoke("/api/records", FakeCurrentUser.ForUser(user.UserId));

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);

        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(ApiErrorCodes.AuthExpired,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>頁面殼（非 /api）導回登入頁——空殼進來再被 API 401 對使用者是壞掉的畫面</summary>
    [Fact]
    public async Task 已停用的使用者_頁面請求導向登入頁()
    {
        var user = _users.Upsert(new WebUser { Account = "DOMAIN\\wang", Active = false });

        var (reached, context) = await Invoke("/records", FakeCurrentUser.ForUser(user.UserId));

        Assert.False(reached);
        Assert.Equal("/login", context.Response.Headers.Location);
    }

    /// <summary>帳號被刪除與被停用等價——查無此人一樣擋下</summary>
    [Fact]
    public async Task 使用者已不存在_一併擋下()
    {
        var (reached, context) = await Invoke("/api/records", FakeCurrentUser.ForUser(12345));

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    /// <summary>serverAdmin 不存在於 lf_users，不能被這道檢查誤擋——它是 AD 停擺時的逃生門</summary>
    [Fact]
    public async Task serverAdmin_不受停用檢查影響()
    {
        var (reached, _) = await Invoke("/api/admin/users", FakeCurrentUser.ServerAdmin());

        Assert.True(reached);
    }

    [Fact]
    public async Task 未登入_不檢查直接放行給後續驗證處理()
    {
        var (reached, _) = await Invoke("/login", FakeCurrentUser.Anonymous());

        Assert.True(reached);
    }
}
