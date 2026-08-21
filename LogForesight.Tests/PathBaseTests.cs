using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Middleware;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 掛載前綴（IIS 子 Application，例 <c>/LogForesight</c>）下的轉址行為。
///
/// 這類 bug 的特徵是「本機沒事、正式站全白」：開發時站台掛在網站根目錄，PathBase 是空字串，
/// 所有寫死 <c>/login</c> 的路徑剛好都對；一掛到子 Application 就整批指向網站根目錄。
/// </summary>
public class PathBaseTests
{
    private readonly FakeUserStore _users = new();
    private static readonly WebAppSettings Settings = new();

    private static DefaultHttpContext ContextWith(string pathBase, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = pathBase;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>停用帳號的頁面請求：轉址要帶掛載前綴，否則會導到網站根目錄的 /login（不存在）</summary>
    [Theory]
    [InlineData("/LogForesight", "/LogForesight/login")]
    [InlineData("", "/login")]
    public async Task 停用帳號的頁面轉址_帶掛載前綴(string pathBase, string expectedLocation)
    {
        var user = _users.Upsert(new WebUser { Account = @"DOMAIN\disabled", Active = false });

        var middleware = new ActiveUserMiddleware(_ => Task.CompletedTask);
        var context = ContextWith(pathBase, "/records");

        await middleware.InvokeAsync(context, FakeCurrentUser.ForUser(user.UserId), _users, Settings);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal(expectedLocation, context.Response.Headers.Location.ToString());
    }

    /// <summary>API 請求不轉址（前端自己攔 401 導頁），前綴不影響這條路徑的判定</summary>
    [Fact]
    public async Task 停用帳號的API請求_在子應用下仍回401而非轉址()
    {
        var user = _users.Upsert(new WebUser { Account = @"DOMAIN\disabled2", Active = false });

        var middleware = new ActiveUserMiddleware(_ => Task.CompletedTask);
        // Request.Path 是扣掉 PathBase 之後的值，所以 /api 前綴判定仍然成立
        var context = ContextWith("/LogForesight", "/api/records");

        await middleware.InvokeAsync(context, FakeCurrentUser.ForUser(user.UserId), _users, Settings);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.Location.ToString()));
    }
}
