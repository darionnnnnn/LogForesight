using LogForesight.Web.Auth;
using LogForesight.Web.Extensions;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
    [Theory]
    [InlineData("/LogForesight", "/LogForesight/access-denied")]
    [InlineData("", "/access-denied")]
    public void 權限不足的頁面保留掛載前綴_API仍回403(string pathBase, string expected)
    {
        var context = ContextWith(pathBase, "/admin/prtg");
        var action = new Microsoft.AspNetCore.Mvc.ActionContext(context,
            new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var authorization = new Microsoft.AspNetCore.Mvc.Filters.AuthorizationFilterContext(action, []);
        var filter = new LogForesight.Web.Filters.PermissionFilter([Capability.Maintain],
            FakeCurrentUser.WithCapabilities(Capability.Handle), new RecordingAuditService());
        filter.OnAuthorization(authorization);
        Assert.Equal(expected, Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectResult>(authorization.Result).Url);
        context.Request.Path = "/api/prtg/acceptance/incidents";
        authorization.Result = null;
        filter.OnAuthorization(authorization);
        Assert.Equal(403, Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(authorization.Result).StatusCode);
    }

    private static DefaultHttpContext ContextWith(string pathBase, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = pathBase;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>
    /// 登入 cookie 的屬性取自 DI 實際註冊的選項（不複製一份來測）：Path 不指定＝框架用請求的 PathBase
    /// （站台掛子 Application 時 cookie 路徑正確，實際回應見 CookieAuthenticationSiteTests）；
    /// SecurePolicy 跟隨請求，http 與 https 都要能登入。
    /// </summary>
    [Fact]
    public void 登入cookie選項_名稱時限與安全屬性()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISystemSettingsStore>(new FakeSystemSettingsStore());
        var settings = new WebAppSettings { Jwt = new SessionSettings { ExpireHours = 5, CookieName = "lf_x" } };
        services.AddLogForesightAuth(settings);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(SessionCookie.Scheme);

        Assert.Equal("lf_x", options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, options.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
        Assert.Null(options.Cookie.Path);   // 不寫死，跟隨 PathBase
        Assert.True(options.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(5), options.ExpireTimeSpan);
        Assert.Equal(typeof(CookieSessionEvents), options.EventsType);
        Assert.IsType<ForwardedSecureCookieManager>(options.CookieManager);
    }

    /// <summary>
    /// 掛載前綴的設定值驗證：空白不套用；以 / 開頭的去掉尾端 / 後套用；其餘（例如少了開頭 /）
    /// 啟動時直接擲例外並點名設定鍵，不自動補 /，也不讓框架的 ArgumentException 蓋掉真正原因。
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("/", null)]
    [InlineData("/app", "/app")]
    [InlineData("/app/", "/app")]
    [InlineData("/app//", "/app")]
    [InlineData("  /app/  ", "/app")]
    public void 掛載前綴設定_空白不套用_斜線開頭去尾端斜線後套用(string? value, string? expected)
    {
        Assert.Equal(expected, new ServerSettings { PathBase = value! }.ResolvePathBase());
    }

    [Theory]
    [InlineData("app")]
    [InlineData("LogForesight/")]
    [InlineData("\\app")]
    [InlineData("//app")]
    [InlineData("/my app")]
    [InlineData("/app?x=1")]
    [InlineData("/app#top")]
    public void 掛載前綴設定_缺開頭斜線時擲例外並點名設定鍵與目前值(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServerSettings { PathBase = value }.ResolvePathBase());

        Assert.Contains("Server:PathBase", ex.Message);
        Assert.Contains("必須以 / 開頭", ex.Message);
        Assert.Contains(value, ex.Message);
    }

    /// <summary>啟動驗證（fail fast）要把壞的掛載前綴列進錯誤清單，不是等到 UsePathBase 才爆框架例外</summary>
    [Fact]
    public void 啟動驗證_掛載前綴缺開頭斜線時不合格並點名設定鍵()
    {
        var settings = new WebAppSettings
        {
            Jwt = new SessionSettings { ExpireHours = 8 },
            Auth = new AuthSettings
            {
                Provider = "Ldap",
                ServerAdmin = new ServerAdminSettings
                {
                    Account = "svc-lfadmin",
                    PasswordHash = "PBKDF2$210000$s79Mzbz0FKaArA2SsMlCuQ==$2JKMIZGXFQbelisLIZZo+WAMCPJ0Ao3XPm5jg8TFy9M="
                }
            },
            Storage = new LogForesight.Core.Configuration.StorageSettings { Type = "Sqlite", DataRoot = AppContext.BaseDirectory },
            Server = new ServerSettings { PathBase = "LogForesight" }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => settings.Validate(strict: false, "Development"));

        Assert.Contains("Server:PathBase", ex.Message);
        Assert.Contains("必須以 / 開頭", ex.Message);
    }

    /// <summary>X-Frame-Options 只接受瀏覽器認得的值：打錯字等於沒防嵌入，含換行會讓每個回應擲例外</summary>
    [Theory]
    [InlineData("DENNY", false)]
    [InlineData("ALLOW-FROM https://x", false)]
    [InlineData("DENY\r\nX: y", false)]
    [InlineData("DENY", true)]
    [InlineData("sameorigin", true)]
    [InlineData("", true)]
    public void 啟動驗證_FrameOptions只接受DENY_SAMEORIGIN或空字串(string value, bool valid)
    {
        var settings = new WebAppSettings
        {
            Jwt = new SessionSettings { ExpireHours = 8 },
            Auth = new AuthSettings
            {
                Provider = "Ldap",
                ServerAdmin = new ServerAdminSettings
                {
                    Account = "svc-lfadmin",
                    PasswordHash = "PBKDF2$210000$s79Mzbz0FKaArA2SsMlCuQ==$2JKMIZGXFQbelisLIZZo+WAMCPJ0Ao3XPm5jg8TFy9M="
                }
            },
            Storage = new LogForesight.Core.Configuration.StorageSettings { Type = "Sqlite", DataRoot = AppContext.BaseDirectory },
            Server = new ServerSettings { FrameOptions = value }
        };

        var ex = Record.Exception(() => settings.Validate(strict: false, "Development"));

        if (valid) Assert.Null(ex);
        else Assert.Contains("Server:FrameOptions", Assert.IsType<InvalidOperationException>(ex).Message);
    }

    /// <summary>IIS 子 Application 會自動填前綴，Server:PathBase 又有值就會雙重前綴——要在啟動時警告</summary>
    [Theory]
    [InlineData("/LogForesight", "/app", true)]
    [InlineData("/LogForesight", "", false)]
    [InlineData("/LogForesight", null, false)]
    [InlineData("/LogForesight", "/", false)]
    [InlineData("/", "/app", false)]
    [InlineData("", "/app", false)]
    [InlineData(null, "/app", false)]
    public void 雙重前綴警告_只在IIS子應用且又設了PathBase時出現(string? iisApplPath, string? pathBase, bool warns)
    {
        var warning = new ServerSettings { PathBase = pathBase! }.DoublePrefixWarning(iisApplPath);

        if (!warns) { Assert.Null(warning); return; }
        Assert.NotNull(warning);
        Assert.Contains("Server:PathBase", warning);
        Assert.Contains("雙重前綴", warning);
    }

    /// <summary>Program.cs 不得繞過強型別設定直接讀 Server:PathBase（WEB-SPEC §5：組態鍵名只存在於設定類別）</summary>
    [Fact]
    public void Program不直接讀PathBase組態鍵()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var program = File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "Program.cs"));

        Assert.DoesNotContain("Configuration[\"Server:PathBase\"]", program);
        Assert.Contains("settings.Server.ResolvePathBase()", program);
    }
}
