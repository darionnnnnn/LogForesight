using System.Net;
using LogForesight.Web.Configuration;
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }
}
