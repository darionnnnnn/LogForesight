using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 對外連線信任邊界（第 79 輪批次 E4～E9）：共用連線設定、Sentinel 回應位址同源、AI 重試條件、
/// 建立工作不重送、遮蔽、PRTG 明文認證警告。
/// </summary>
public class OutboundTrustBoundaryTests
{
    // ── E4 共用連線設定 ──

    [Fact]
    public void 共用連線設定_有連線逾時且不跟隨轉址()
    {
        using var handler = OutboundHttp.CreateHandler(TimeSpan.FromMinutes(5));
        Assert.Equal(OutboundHttp.ConnectTimeout, handler.ConnectTimeout);
        Assert.True(handler.ConnectTimeout <= TimeSpan.FromSeconds(30));
        Assert.False(handler.AllowAutoRedirect);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void PRTG預設連線走共用設定()
    {
        using var client = new PrtgClient("https://prtg.local", "tok", 30, false, null, PrtgAuthModes.Token, "", "", "");
        var handler = Assert.IsType<SocketsHttpHandler>(client.Handler);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(OutboundHttp.ConnectTimeout, handler.ConnectTimeout);
    }

    [Fact]
    public async Task 收到轉址回應不跟隨()
    {
        using var listener = new HttpListenerScope();
        listener.Respond = ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = "http://127.0.0.1:1/elsewhere";
        };
        using var handler = OutboundHttp.CreateHandler(TimeSpan.Zero);
        using var http = new HttpClient(handler);
        using var resp = await http.GetAsync(listener.Url);
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal(1, listener.Hits);
    }

    // ── E5 Sentinel 回應位址同源；E7 建立工作不重送 ──

    private static SentinelServer Server() => new()
    {
        Name = "SENTINEL-A", BaseUrl = "https://sentinel.local:8443", Username = "svc", Password = "pw"
    };

    private static NetiqOptions Settings(int retryCount = 3) => new()
    {
        RetryCount = retryCount, TimeoutSeconds = 120, QueryDelayMs = 0, PageSize = 500
    };

    private const string Base = "https://sentinel.local:8443";

    private static HttpResponseMessage Json(HttpStatusCode code, string json, Uri? location = null)
    {
        var resp = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (location != null) resp.Headers.Location = location;
        return resp;
    }

    private static SentinelSearchRequest Request() =>
        new("evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    [Fact]
    public async Task 工作位址指向別台主機_不送出且以領域例外失敗()
    {
        var handler = new RecordingHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url.EndsWith("/auth/tokens", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}");
            if (req.Method == HttpMethod.Post && url.EndsWith("/objects/event-search", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, "{}", new Uri("https://evil.example/steal/1"));
            if (req.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            throw new InvalidOperationException($"未預期：{req.Method} {url}");
        });

        await using var client = new SentinelClient(Server(), Settings(), handler);
        var ex = await Assert.ThrowsAsync<SentinelClientException>(() => client.SearchAsync(Request()));

        Assert.Equal("source-endpoint-mismatch", ex.SafeCode);
        Assert.DoesNotContain(handler.Urls, u => u.Contains("evil.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 結果頁位址指向同主機不同埠_不送出()
    {
        var handler = new RecordingHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url.EndsWith("/auth/tokens", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}");
            if (req.Method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, "{}", new Uri($"{Base}/SentinelRESTServices/objects/event-search/1"));
            if (req.Method == HttpMethod.Get && url.EndsWith("/event-search/1", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK,
                    "{\"status\":2,\"found\":1,\"avail\":1,\"results\":{\"@href\":\"https://sentinel.local:9999/x?page=1\"}}");
            if (req.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            throw new InvalidOperationException($"未預期：{req.Method} {url}");
        });

        await using var client = new SentinelClient(Server(), Settings(), handler);
        var ex = await Assert.ThrowsAsync<SentinelClientException>(() => client.SearchAsync(Request()));
        Assert.Equal("source-endpoint-mismatch", ex.SafeCode);
        Assert.DoesNotContain(handler.Urls, u => u.Contains(":9999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 相對工作位址與結果頁_以站台位址解析()
    {
        var handler = new RecordingHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url.EndsWith("/auth/tokens", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}");
            if (req.Method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, "{}",
                    new Uri("/SentinelRESTServices/objects/event-search/7", UriKind.Relative));
            if (req.Method == HttpMethod.Get && url == $"{Base}/SentinelRESTServices/objects/event-search/7")
                return Json(HttpStatusCode.OK,
                    "{\"status\":2,\"found\":1,\"avail\":1,\"results\":{\"@href\":\"/SentinelRESTServices/objects/event?page=1\"}}");
            if (req.Method == HttpMethod.Get && url.StartsWith($"{Base}/SentinelRESTServices/objects/event?", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "[{\"evt\":\"A\"}]");
            if (req.Method == HttpMethod.Delete && url == $"{Base}/SentinelRESTServices/objects/event-search/7")
                return new HttpResponseMessage(HttpStatusCode.OK);
            throw new InvalidOperationException($"未預期：{req.Method} {url}");
        });

        await using var client = new SentinelClient(Server(), Settings(), handler);
        var result = await client.SearchAsync(Request());

        Assert.Single(result.Events);
        Assert.All(handler.Urls, u => Assert.StartsWith(Base + "/", u));
    }

    [Fact]
    public async Task 建立工作逾時_不重送建立請求()
    {
        var creates = 0;
        var handler = new RecordingHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url.EndsWith("/auth/tokens", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}");
            if (req.Method == HttpMethod.Post && url.EndsWith("/objects/event-search", StringComparison.Ordinal))
            {
                creates++;
                // HttpClient 逾時擲的就是 TaskCanceledException（外層 ct 未取消）
                throw new TaskCanceledException("模擬請求逾時");
            }
            if (req.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            throw new InvalidOperationException($"未預期：{req.Method} {url}");
        });

        await using var client = new SentinelClient(Server(), Settings(retryCount: 3), handler);
        await Assert.ThrowsAnyAsync<Exception>(() => client.SearchAsync(Request()));
        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task 建立工作連線失敗_仍會重試()
    {
        var creates = 0;
        var handler = new RecordingHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url.EndsWith("/auth/tokens", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}");
            if (req.Method == HttpMethod.Post && url.EndsWith("/objects/event-search", StringComparison.Ordinal))
            {
                creates++;
                throw new HttpRequestException("模擬連線被拒");
            }
            throw new InvalidOperationException($"未預期：{req.Method} {url}");
        });

        await using var client = new SentinelClient(Server(), Settings(retryCount: 1), handler);
        await Assert.ThrowsAnyAsync<Exception>(() => client.SearchAsync(Request()));
        Assert.Equal(2, creates);
    }

    // ── E6 AI 重試條件 ──

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 1)]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    [InlineData(HttpStatusCode.TooManyRequests, 3)]
    public async Task AI端點錯誤_只有伺服器錯誤與限流會重試(HttpStatusCode status, int expectedCalls)
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        var settings = new AiSettings
        {
            BaseUrl = "https://ai.local", ApiKey = "k", TimeoutSeconds = 5, RetryCount = 2, RetryDelaySeconds = 0
        };
        var service = new AIService(settings, handler, NullAiRequestGate.Instance);

        var response = await service.ChatAsync("hi");

        Assert.False(response.Success);
        Assert.Equal(expectedCalls, handler.Urls.Count);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void AI暫時性失敗判定_邊界兩側(HttpStatusCode? status, bool transient)
        => Assert.Equal(transient, AIService.IsTransientHttpFailure(new HttpRequestException("x", null, status)));

    // ── E8 遮蔽 ──

    [Fact]
    public void 連線字串遮蔽_密碼含分號與引號也不殘留()
    {
        var masked = StorageBackend.MaskConnectionString(
            "Server=db;Database=lf;User Id=sa;Password=\"p@ss;w0rd;tail\";TrustServerCertificate=True");
        Assert.DoesNotContain("p@ss", masked);
        Assert.DoesNotContain("w0rd", masked);
        Assert.DoesNotContain("tail", masked);
        Assert.Contains("lf", masked);

        var pwdAlias = StorageBackend.MaskConnectionString("Server=db;Pwd=abc123;Database=lf");
        Assert.DoesNotContain("abc123", pwdAlias);
    }

    [Fact]
    public void 連線字串無法解析_整段不顯示()
    {
        var masked = StorageBackend.MaskConnectionString("Server=db;Password=\"unterminated;Database=lf");
        Assert.DoesNotContain("unterminated", masked);
    }

    [Theory]
    [InlineData("https://admin:Top$ecret@prtg.local/api", "Top$ecret")]
    [InlineData("see https://u:hunter2@x.local/a?b=1 for detail", "hunter2")]
    [InlineData("https://prtg/api?pass%68ash=987654&username=a", "987654")]
    [InlineData("https://prtg/api?a=1&API%5FKEY=zz9911", "zz9911")]
    [InlineData("https://x/?client_secret=cs-55&y=2", "cs-55")]
    public void 網址遮蔽_帳密與編碼參數名都不殘留(string input, string secret)
    {
        Assert.True(UrlSecrets.ContainsSecret(input));
        var masked = UrlSecrets.Mask(input);
        Assert.DoesNotContain(secret, masked);
        Assert.Contains("***", masked);
    }

    [Theory]
    [InlineData("https://prtg.local/api?username=a&id=5")]
    [InlineData("https://user@prtg.local/")]
    [InlineData("https://prtg/token/path?user=a")]
    [InlineData("https://prtg/api?tokens=1")]
    public void 網址遮蔽_一般網址原樣保留(string input)
    {
        Assert.False(UrlSecrets.ContainsSecret(input));
        Assert.Equal(input, UrlSecrets.Mask(input));
    }

    // ── E9 PRTG 明文認證警告 ──

    [Theory]
    [InlineData("http://prtg.local", "tok", false, true)]
    [InlineData("http://prtg.local", "", true, true)]
    [InlineData("http://prtg.local", "", false, false)]
    [InlineData("https://prtg.local", "tok", true, false)]
    public void PRTG明文認證判定(string url, string token, bool usernameAuth, bool expected)
        => Assert.Equal(expected, PrtgClient.UsesPlainHttpWithCredentials(new Uri(url), token, usernameAuth));

    [Fact]
    public void PRTG走http且帶token_記錄檔有警告()
    {
        var target = new NLog.Targets.MemoryTarget("lf-prtg-http-" + Guid.NewGuid().ToString("N")) { Layout = "${level}|${message}" };
        var config = NLog.LogManager.Configuration ??= new NLog.Config.LoggingConfiguration();
        config.AddTarget(target);
        config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target, typeof(PrtgClient).FullName!);
        NLog.LogManager.ReconfigExistingLoggers();
        try
        {
            using (new PrtgClient("http://prtg-plain.local", "tok", 30, false, new RecordingHandler((_, _) => new HttpResponseMessage()),
                       PrtgAuthModes.Token, "", "", ""))
            {
            }
            Assert.Contains(target.Logs, l => l.StartsWith("Warn|", StringComparison.Ordinal) && l.Contains("http://", StringComparison.Ordinal));
        }
        finally
        {
            config.RemoveTarget(target.Name);
            config.LoggingRules.Where(r => r.Targets.Contains(target)).ToList().ForEach(r => config.LoggingRules.Remove(r));
            NLog.LogManager.ReconfigExistingLoggers();
        }
    }

    // ── 替身 ──

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<string> _urls = new();
        public IReadOnlyList<string> Urls { get { lock (_urls) return _urls.ToList(); } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_urls) _urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request, cancellationToken));
        }
    }

    private sealed class HttpListenerScope : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _hits;
        public Action<HttpListenerContext> Respond { get; set; } = _ => { };
        public string Url { get; }
        public int Hits => Volatile.Read(ref _hits);

        public HttpListenerScope()
        {
            for (var attempt = 0; ; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                Url = $"http://127.0.0.1:{port}/";
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add(Url);
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 10) { }
            }
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch (Exception) { return; }
                    Interlocked.Increment(ref _hits);
                    Respond(ctx);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() => _listener.Close();
    }
}
