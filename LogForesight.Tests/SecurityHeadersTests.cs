using System.Net;
using LogForesight.Web.Configuration;
using LogForesight.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 全站安全回應標頭（<see cref="SecurityHeadersPolicy"/>）：一般 API、頁面、靜態檔、會提前結束請求的
/// 401 回應都要帶 nosniff／Referrer-Policy／X-Frame-Options；X-Frame-Options 來自 Server:FrameOptions，空字串＝不送。
/// 用真 Kestrel 起最小管線，掛的是 Program.cs 實際使用的同一支 middleware 工廠。
/// </summary>
public sealed class SecurityHeadersTests
{
    private static async Task<(HttpResponseMessage Response, IDisposable Cleanup)> Send(string path, string? frameOptions)
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        File.WriteAllText(Path.Combine(root, "wwwroot", "site.css"), "body{}");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ContentRootPath = root,
            WebRootPath = Path.Combine(root, "wwwroot")
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        var settings = new ServerSettings();
        if (frameOptions != null) settings.FrameOptions = frameOptions;

        app.Use(SecurityHeadersPolicy.Middleware(settings.FrameOptions));
        app.UseStaticFiles();
        // 模擬認證／CSRF 這類會提前結束請求的中介層：排在標頭中介層之後
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/secure")) { context.Response.StatusCode = 401; return; }
            await next();
        });
        app.Map("/api/ok", b => b.Run(c => c.Response.WriteAsync("{}")));
        app.Map("/page", b => b.Run(c => { c.Response.ContentType = "text/html"; return c.Response.WriteAsync("<html></html>"); }));
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
        var response = await client.GetAsync(path.TrimStart('/'));
        return (response, new Cleanup(app, client, root));
    }

    private sealed class Cleanup(WebApplication app, HttpClient client, string root) : IDisposable
    {
        public void Dispose()
        {
            client.Dispose();
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try { Directory.Delete(root, true); } catch { /* 暫存目錄清不掉不影響結果 */ }
        }
    }

    [Theory]
    [InlineData("/api/ok", HttpStatusCode.OK)]
    [InlineData("/page", HttpStatusCode.OK)]
    [InlineData("/site.css", HttpStatusCode.OK)]
    [InlineData("/api/secure/x", HttpStatusCode.Unauthorized)]
    [InlineData("/missing", HttpStatusCode.NotFound)]
    public async Task 各類回應都帶三個安全標頭(string path, HttpStatusCode expected)
    {
        var (response, cleanup) = await Send(path, null);
        using (cleanup)
        using (response)
        {
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
            Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        }
    }

    [Fact]
    public async Task FrameOptions設為空字串_不送XFrameOptions但其餘照送()
    {
        var (response, cleanup) = await Send("/api/ok", "");
        using (cleanup)
        using (response)
        {
            Assert.False(response.Headers.Contains("X-Frame-Options"));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        }
    }

    [Fact]
    public async Task FrameOptions可改成SAMEORIGIN()
    {
        var (response, cleanup) = await Send("/page", "SAMEORIGIN");
        using (cleanup)
        using (response)
            Assert.Equal("SAMEORIGIN", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }

    [Fact]
    public void ServerSettings預設FrameOptions為DENY()
    {
        Assert.Equal("DENY", new ServerSettings().FrameOptions);
    }

    /// <summary>appsettings.json 的 Server 節要有 FrameOptions 鍵，預設 DENY（設定有鍵才有人知道能調）</summary>
    [Fact]
    public void appsettings_Server節含FrameOptions且預設DENY()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "LogForesight.Web", "appsettings.json"));
        Assert.Matches(@"""FrameOptions""\s*:\s*""DENY""", text);
    }

    /// <summary>
    /// Program.cs 的管線順序：標頭中介層在 UsePathBase 之後，且排在所有會提前結束請求的中介層
    /// （例外處理、驗證、ActiveUser、MigrationGate、Csrf）之前。
    /// </summary>
    [Fact]
    public void Program_標頭中介層順序正確()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "LogForesight.Web", "Program.cs"));
        var headers = text.IndexOf("SecurityHeadersPolicy.Middleware(", StringComparison.Ordinal);
        Assert.True(headers > 0, "Program.cs 沒有掛 SecurityHeadersPolicy.Middleware");
        var pathBase = text.IndexOf("app.UsePathBase(", StringComparison.Ordinal);
        Assert.True(pathBase > 0 && pathBase < headers, "標頭中介層必須在 UsePathBase 之後");
        foreach (var later in new[]
                 {
                     "app.UseExceptionHandler(", "app.UseHttpsRedirection(", "app.UseStaticFiles(", "app.UseAuthentication(",
                     "app.UseMiddleware<ActiveUserMiddleware>", "app.UseMiddleware<MigrationGateMiddleware>",
                     "app.UseMiddleware<CsrfHeaderMiddleware>"
                 })
        {
            var index = text.IndexOf(later, StringComparison.Ordinal);
            Assert.True(index > headers, $"標頭中介層必須排在 {later} 之前");
        }
    }

    // CSP 相關案件走行程內執行中介層、不經網路：本機若有 AdGuard 這類攔截 localhost 流量的軟體，
    // 會改寫線上的 CSP 標頭（實測會合併自己的指令、丟掉 Report-Only），測到的就不是我們送出的值。
    private sealed class CapturingResponseFeature : Microsoft.AspNetCore.Http.Features.IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = new();
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => false;
        public void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));
        public void OnCompleted(Func<object, Task> callback, object state) { }
        public async Task FireStarting() { foreach (var (cb, st) in _starting) await cb(st); }
    }

    private static async Task<(IHeaderDictionary Headers, string Nonce)> RunCsp(CspMode csp, string? frameOptions = null, string? pathBase = null)
    {
        var feature = new CapturingResponseFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(feature);
        if (!string.IsNullOrEmpty(pathBase)) context.Request.PathBase = pathBase;
        var nonceInHandler = "";
        await SecurityHeadersPolicy.Middleware(frameOptions, csp)(context, () =>
        {
            nonceInHandler = context.GetCspNonce() + "|" + context.GetCspNonce();
            return Task.CompletedTask;
        });
        await feature.FireStarting();
        return (feature.Headers, nonceInHandler);
    }

    [Fact]
    public async Task Csp_Off_不送任何CSP標頭()
    {
        var (headers, _) = await RunCsp(CspMode.Off);
        Assert.False(headers.ContainsKey("Content-Security-Policy"));
        Assert.False(headers.ContainsKey("Content-Security-Policy-Report-Only"));
    }

    [Fact]
    public async Task Csp_ReportOnly_只送ReportOnly標頭()
    {
        var (headers, _) = await RunCsp(CspMode.ReportOnly);
        Assert.True(headers.ContainsKey("Content-Security-Policy-Report-Only"));
        Assert.False(headers.ContainsKey("Content-Security-Policy"));
    }

    [Fact]
    public async Task Csp_Enforce_只送Content_Security_Policy標頭_且政策含必要指令()
    {
        var (headers, _) = await RunCsp(CspMode.Enforce, "DENY");
        Assert.False(headers.ContainsKey("Content-Security-Policy-Report-Only"));
        var policy = headers["Content-Security-Policy"].ToString();
        Assert.StartsWith("default-src 'self'; script-src 'self' 'nonce-", policy);
        Assert.Contains("; style-src 'self'; ", policy);
        Assert.Contains("img-src 'self' data:", policy);
        Assert.Contains("object-src 'none'", policy);
        Assert.Contains("base-uri 'self'", policy);
        Assert.Contains("form-action 'self'", policy);
        Assert.EndsWith("; report-uri /api/csp-report", policy);
    }

    [Theory]
    [InlineData("DENY", "frame-ancestors 'none'")]
    [InlineData("SAMEORIGIN", "frame-ancestors 'self'")]
    [InlineData("", null)]
    public async Task Csp_frame_ancestors對應FrameOptions(string frameOptions, string? expected)
    {
        var (headers, _) = await RunCsp(CspMode.Enforce, frameOptions);
        var policy = headers["Content-Security-Policy"].ToString();
        if (expected == null) Assert.DoesNotContain("frame-ancestors", policy);
        else Assert.Contains(expected, policy);
    }

    [Fact]
    public async Task Csp_report_uri帶PathBase前綴()
    {
        var (headers, _) = await RunCsp(CspMode.Enforce, null, "/LogForesight");
        Assert.EndsWith("; report-uri /LogForesight/api/csp-report", headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public async Task Csp_nonce與標頭一致_且每次請求不同()
    {
        string Nonce(IHeaderDictionary headers, string handlerSaw)
        {
            var parts = handlerSaw.Split('|');
            Assert.Equal(parts[0], parts[1]);                       // 同一請求多次讀取回同值
            Assert.Contains($"'nonce-{parts[0]}'", headers["Content-Security-Policy"].ToString());
            return parts[0];
        }

        var (h1, saw1) = await RunCsp(CspMode.Enforce);
        var (h2, saw2) = await RunCsp(CspMode.Enforce);
        var n1 = Nonce(h1, saw1);
        var n2 = Nonce(h2, saw2);
        Assert.NotEqual(n1, n2);
        Assert.Equal(16, Convert.FromBase64String(n1).Length);
    }

    private static WebAppSettings SettingsWithCsp(string? csp) => new()
    {
        Jwt = new JwtSettings { SecretKey = new string('k', 40), ExpireHours = 8 },
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
        Server = new ServerSettings { Csp = csp }
    };

    [Theory]
    [InlineData("Off", true)]
    [InlineData("reportonly", true)]
    [InlineData("ENFORCE", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("on", false)]
    [InlineData("true", false)]
    public void 啟動驗證_Csp只接受三值或未設定(string? value, bool valid)
    {
        var ex = Record.Exception(() => SettingsWithCsp(value).Validate(strict: false, "Development"));

        if (valid) Assert.Null(ex);
        else Assert.Contains("Server:Csp", Assert.IsType<InvalidOperationException>(ex).Message);
    }

    [Theory]
    [InlineData("Development", CspMode.ReportOnly)]
    [InlineData("Production", CspMode.Enforce)]
    [InlineData("Staging", CspMode.Enforce)]
    public void Csp未設定時依環境名稱解析(string environment, CspMode expected)
    {
        Assert.Equal(expected, new ServerSettings().ResolveCspMode(environment));
        Assert.Equal(expected, new ServerSettings { Csp = "  " }.ResolveCspMode(environment));
    }

    [Theory]
    [InlineData("Off", CspMode.Off)]
    [InlineData("reportonly", CspMode.ReportOnly)]
    [InlineData("ENFORCE", CspMode.Enforce)]
    public void Csp明確設定時不受環境影響(string value, CspMode expected)
    {
        Assert.Equal(expected, new ServerSettings { Csp = value }.ResolveCspMode("Development"));
        Assert.Equal(expected, new ServerSettings { Csp = value }.ResolveCspMode("Production"));
    }

    [Fact]
    public void appsettings_Server節含Csp鍵()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "LogForesight.Web", "appsettings.json"));
        Assert.Matches(@"""Csp""\s*:", text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }
}
