using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 反向代理轉送標頭（Program.cs 的 <c>ForwardedHeadersSetup.Build</c>）：只在 Server:TrustedProxies 非空時才掛
/// <c>UseForwardedHeaders</c>，且 KnownProxies 只含設定的 IP。用真 Kestrel（連線來自 127.0.0.1）驗三種情境：
/// 受信任代理 → 端點看到 X-Forwarded-For 的位址；代理不在清單 → 維持 127.0.0.1（防偽造來源繞過登入節流）；未設定 → 完全不處理。
/// </summary>
public sealed class ForwardedHeadersTests
{
    private static async Task<string> RemoteIpSeenByEndpoint(IReadOnlyList<string> trustedProxies, string forwardedFor)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        // 與 Program.cs 相同的條件：未設定＝完全不掛
        if (trustedProxies.Count > 0)
            app.UseForwardedHeaders(ForwardedHeadersSetup.Build(trustedProxies));
        app.Map("/ip", b => b.Run(c => c.Response.WriteAsync(c.Connection.RemoteIpAddress?.ToString() ?? "")));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") };
            using var request = new HttpRequestMessage(HttpMethod.Get, "ip");
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            using var response = await client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task 受信任代理帶XForwardedFor_端點看到轉送的來源IP()
    {
        var seen = await RemoteIpSeenByEndpoint(new[] { "127.0.0.1" }, "10.1.2.3");
        Assert.Equal("10.1.2.3", seen);
    }

    [Fact]
    public async Task 代理不在清單_維持實際連線IP不被偽造()
    {
        var seen = await RemoteIpSeenByEndpoint(new[] { "10.9.9.9" }, "10.1.2.3");
        Assert.Equal("127.0.0.1", seen);
    }

    [Fact]
    public async Task 未設定受信任代理_完全不處理轉送標頭()
    {
        var seen = await RemoteIpSeenByEndpoint(Array.Empty<string>(), "10.1.2.3");
        Assert.Equal("127.0.0.1", seen);
    }

    [Fact]
    public void Build只信任設定的IP_並清掉預設的迴路信任()
    {
        var options = ForwardedHeadersSetup.Build(new[] { " 10.0.0.5 ", "10.0.0.6" });

        Assert.Equal(new[] { IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.6") }, options.KnownProxies.ToArray());
        Assert.Empty(options.KnownNetworks);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    }

    private static LogForesight.Web.Configuration.WebAppSettings 含受信任代理的設定(params string[] proxies) => new()
    {
        Jwt = new LogForesight.Web.Configuration.SessionSettings { ExpireHours = 8 },
        Auth = new LogForesight.Web.Configuration.AuthSettings
        {
            Provider = "Ldap",
            ServerAdmin = new LogForesight.Web.Configuration.ServerAdminSettings
            {
                Account = "svc-lfadmin",
                PasswordHash = "PBKDF2$210000$s79Mzbz0FKaArA2SsMlCuQ==$2JKMIZGXFQbelisLIZZo+WAMCPJ0Ao3XPm5jg8TFy9M="
            }
        },
        Storage = new LogForesight.Core.Configuration.StorageSettings { Type = "Sqlite", DataRoot = AppContext.BaseDirectory },
        Server = new LogForesight.Web.Configuration.ServerSettings { TrustedProxies = proxies.ToList() }
    };

    /// <summary>A4：受信任 proxy 的啟動驗證走 IpSyntax，<c>10.1</c> 這種 IPAddress.TryParse 會放行的縮寫必須被拒。</summary>
    [Fact]
    public void 啟動驗證_受信任代理填縮寫10點1被拒()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            含受信任代理的設定("10.1").Validate(strict: false, "Development"));

        Assert.Contains("Server:TrustedProxies", ex.Message);
        Assert.Contains("10.1", ex.Message);
    }

    [Fact]
    public void 啟動驗證_受信任代理填完整IPv4通過()
    {
        var ex = Record.Exception(() => 含受信任代理的設定("10.0.0.1").Validate(strict: false, "Development"));

        Assert.Null(ex);
    }
}
