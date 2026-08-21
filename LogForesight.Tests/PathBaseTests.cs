using LogForesight.Web.Auth;
using LogForesight.Web.Extensions;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// 未帶 token 的頁面請求：JwtBearer 的 OnChallenge 會轉址到登入頁。
    /// 這裡從 DI 取出**實際註冊的那個 lambda** 來跑，而不是複製一份邏輯來測——
    /// 複製品會在正式碼改動時繼續通過，等於沒測。
    /// </summary>
    private static async Task<HttpContext> InvokeOnChallenge(string pathBase, string path, string query = "")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISystemSettingsStore>(new InMemorySystemSettingsStore());
        // 金鑰只為了讓 JwtBearer 選項建得起來；本測試不簽發也不驗證 token
        var settings = new WebAppSettings();
        settings.Jwt.SecretKey = new string('k', 64);
        services.AddLogForesightAuth(settings);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.PathBase = pathBase;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();

        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        await options.Events!.Challenge(
            new JwtBearerChallengeContext(context, scheme, options, new AuthenticationProperties()));

        return context;
    }

    [Theory]
    [InlineData("/LogForesight", "/LogForesight/login?returnUrl=%2Frecords")]
    [InlineData("", "/login?returnUrl=%2Frecords")]
    public async Task 未登入的頁面請求_轉址帶掛載前綴而returnUrl不帶(string pathBase, string expected)
    {
        var context = await InvokeOnChallenge(pathBase, "/records");

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        // returnUrl 是 app 內路徑（不含前綴）——前端登入成功後用 appUrl 還原
        Assert.Equal(expected, context.Response.Headers.Location.ToString());
    }

    /// <summary>查詢字串要一起保留，否則使用者被踢回來之後會失去原本的篩選條件</summary>
    [Fact]
    public async Task 未登入的頁面請求_returnUrl保留查詢字串()
    {
        var context = await InvokeOnChallenge("/LogForesight", "/records", "?riskLevels=%E9%AB%98");

        var location = context.Response.Headers.Location.ToString();
        Assert.StartsWith("/LogForesight/login?returnUrl=", location);
        Assert.Contains(Uri.EscapeDataString("/records?riskLevels="), location);
    }

    /// <summary>只為了讓 auth 註冊拿得到設定；本測試不碰任何設定值</summary>
    private sealed class InMemorySystemSettingsStore : ISystemSettingsStore
    {
        private readonly SystemSettings _settings = new();
        public SystemSettings Get() => _settings;
        public SystemSettings Update(Action<SystemSettings> mutation)
        {
            mutation(_settings);
            return _settings;
        }
    }

    /// <summary>API 請求不轉址：前端 api.js 自己攔 401 導頁（頁面請求才 302）</summary>
    [Fact]
    public async Task 未登入的API請求_在子應用下回401信封而非轉址()
    {
        var context = await InvokeOnChallenge("/LogForesight", "/api/records");

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.Location.ToString()));
    }
}
