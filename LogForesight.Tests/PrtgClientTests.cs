using System.Net;
using System.Text;
using LogForesight.Core;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PrtgClient 的 HTTP 存取層測試（PRTG 第 1 輪批次B）：建構子網址檢驗、連線測試、HTML/401 例外處理、
/// URL 組裝與 token 傳遞、例外訊息遮蔽、逾時與 SSL 設定傳遞。
/// </summary>
public class PrtgClientTests
{
    private const string ValidUrl = "https://prtg.example.com";
    private const string SampleToken = "prtg-secret-token-12345";

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json)
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage HtmlResponse(HttpStatusCode code, string html)
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://prtg.example.com")]
    [InlineData("")]
    [InlineData("   ")]
    public void 建構子_位址格式不合法時擲PrtgClientException(string invalidUrl)
    {
        var ex = Assert.Throws<PrtgClientException>(() =>
            new PrtgClient(invalidUrl, SampleToken, 30, false));

        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_回應正常JSON時回傳耗時()
    {
        var stub = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                @"{""prtg-version"": ""23.1.82"", ""treesize"": 1, ""sensors"": [{""objid"": 1001}]}"))
        };

        using var client = new PrtgClient(ValidUrl, SampleToken, 30, false, stub);
        var elapsed = await client.TestConnectionAsync();

        Assert.True(elapsed >= TimeSpan.Zero);
        Assert.Single(stub.Requests);
        Assert.Contains("content=sensors", stub.Requests[0].Url);
        Assert.Contains($"apitoken={SampleToken}", stub.Requests[0].Url);
    }

    [Fact]
    public async Task TestConnectionAsync_回應HTML登入頁時擲例外()
    {
        var stub = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(HtmlResponse(
                HttpStatusCode.OK,
                "<!DOCTYPE html><html><body><form action='/public/login.htm'>Login</form></body></html>"))
        };

        using var client = new PrtgClient(ValidUrl, SampleToken, 30, false, stub);
        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.TestConnectionAsync());

        Assert.Contains("HTML", ex.Message);
    }

    [Fact]
    public async Task TestConnectionAsync_HTTP401時擲例外()
    {
        var stub = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
        };

        using var client = new PrtgClient(ValidUrl, SampleToken, 30, false, stub);
        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.TestConnectionAsync());

        Assert.Contains("token 無效", ex.Message);
    }

    [Fact]
    public async Task GetJsonAsync_請求URL帶上apitoken()
    {
        var stub = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, @"{}"))
        };

        using var client = new PrtgClient(ValidUrl, SampleToken, 30, false, stub);
        _ = await client.GetJsonAsync("/api/table.json?content=sensors&columns=objid");

        Assert.Single(stub.Requests);
        var reqUrl = stub.Requests[0].Url;
        Assert.Contains("content=sensors", reqUrl);
        Assert.Contains("columns=objid", reqUrl);
        Assert.Contains($"apitoken={SampleToken}", reqUrl);
    }

    [Fact]
    public async Task 例外訊息不含apitoken()
    {
        // 刻意用含特殊字元的 token：底層例外訊息帶的是請求 URL，token 在那裡是 URL 編碼過的，
        // 只遮原文的實作會在這種 token 上原樣外流
        const string sensitiveToken = "super+secret/token=xyz 999";
        var stub = new StubHandler
        {
            OnSend = (req, _) => throw new HttpRequestException($"Failed to connect to {req.RequestUri}")
        };

        using var client = new PrtgClient(ValidUrl, sensitiveToken, 30, false, stub);
        var ex = await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync("/api/test"));

        Assert.DoesNotContain(sensitiveToken, ex.Message);
        Assert.DoesNotContain(sensitiveToken, ex.ToString());
    }

    [Fact]
    public void 逾時秒數與SSL選項傳遞正確()
    {
        // 驗證逾時秒數生效（包含邊界值小於 1 夾至 1）
        using var client1 = new PrtgClient(ValidUrl, SampleToken, 45, false);
        Assert.Equal(TimeSpan.FromSeconds(45), client1.Timeout);

        using var client2 = new PrtgClient(ValidUrl, SampleToken, 0, false);
        Assert.Equal(TimeSpan.FromSeconds(1), client2.Timeout);

        // 驗證 SocketsHttpHandler SslOptions 設定有生效
        using var clientSsl = new PrtgClient(ValidUrl, SampleToken, 30, true);
        var socketHandler = Assert.IsType<SocketsHttpHandler>(clientSsl.Handler);
        Assert.NotNull(socketHandler.SslOptions.RemoteCertificateValidationCallback);
        Assert.True(socketHandler.SslOptions.RemoteCertificateValidationCallback!(null!, null!, null!, System.Net.Security.SslPolicyErrors.None));

        using var clientNoSsl = new PrtgClient(ValidUrl, SampleToken, 30, false);
        var noSslHandler = Assert.IsType<SocketsHttpHandler>(clientNoSsl.Handler);
        Assert.Null(noSslHandler.SslOptions.RemoteCertificateValidationCallback);
    }

    /// <summary>
    /// 把每次 SendAsync 的請求方法／URL／內容記錄下來，回應由測試以 <see cref="OnSend"/> 提供。
    /// 取代真實 <see cref="HttpClient"/> 連線，讓 PrtgClient 的協定邏輯可離線測試。
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } =
            (_, _) => throw new InvalidOperationException("測試未設定 OnSend");

        public List<RecordedRequest> Requests { get; } = new();

        public sealed record RecordedRequest(HttpMethod Method, string Url, string? Authorization, string? Body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(), body));

            return await OnSend(request, cancellationToken);
        }
    }
}
