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

    [Fact]
    public async Task 帳密模式_首次請求前先換passhash且只換一次()
    {
        var passhashCount = 0;
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    Interlocked.Increment(ref passhashCount);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("1234567890\r\n", Encoding.UTF8, "text/plain")
                    });
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
            usernameOrEmpty: "admin",
            passwordOrEmpty: "secret");

        await client.GetJsonAsync("/api/table.json?content=sensors");
        await client.GetJsonAsync("/api/table.json?content=devices");
        await client.GetJsonAsync("/api/table.json?content=messages");

        Assert.Equal(1, passhashCount);
        Assert.Equal(4, stub.Requests.Count);

        // 第一個請求換 passhash
        Assert.Contains("getpasshash.htm", stub.Requests[0].Url);
        Assert.Contains("username=admin", stub.Requests[0].Url);
        Assert.Contains("password=secret", stub.Requests[0].Url);

        // 後續三個資料請求均帶 username 與 passhash
        for (int i = 1; i <= 3; i++)
        {
            Assert.Contains("username=admin", stub.Requests[i].Url);
            Assert.Contains("passhash=1234567890", stub.Requests[i].Url);
            Assert.DoesNotContain("password=", stub.Requests[i].Url);
        }
    }

    [Fact]
    public async Task 帳密模式_併發請求下passhash仍只換一次()
    {
        var passhashCount = 0;
        var stub = new StubHandler
        {
            OnSend = async (req, _) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    Interlocked.Increment(ref passhashCount);
                    await Task.Delay(20);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("9988776655\n", Encoding.UTF8, "text/plain")
                    };
                }
                return JsonResponse(HttpStatusCode.OK, "{}");
            }
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
            usernameOrEmpty: "testuser",
            passwordOrEmpty: "testpass");

        var tasks = Enumerable.Range(0, 10)
            .Select(i => client.GetJsonAsync($"/api/table.json?content=sensors&i={i}"))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, passhashCount);
        Assert.Equal(11, stub.Requests.Count);
    }

    [Fact]
    public async Task 帳密模式_後續請求不得帶password參數()
    {
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("passhash123", Encoding.UTF8, "text/plain")
                    });
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
            usernameOrEmpty: "operator",
            passwordOrEmpty: "secretpass");

        await client.GetJsonAsync("/api/table.json?content=sensors");
        await client.GetJsonAsync("/api/table.json?content=devices");

        Assert.Equal(3, stub.Requests.Count);
        // 除了第 0 個 getpasshash.htm 以外，其他都不含 password=
        Assert.Contains("password=", stub.Requests[0].Url);
        Assert.DoesNotContain("password=", stub.Requests[1].Url);
        Assert.DoesNotContain("password=", stub.Requests[2].Url);
    }

    [Fact]
    public async Task 帳密模式_帳密錯誤時擲例外且訊息含帳號或密碼()
    {
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
            usernameOrEmpty: "operator",
            passwordOrEmpty: "wrongpass");

        var ex = await Assert.ThrowsAsync<PrtgClientException>(() =>
            client.GetJsonAsync("/api/table.json?content=sensors"));

        Assert.Contains("帳號或密碼", ex.Message);
    }

    [Fact]
    public async Task 帳密模式_帳密錯誤後不再重打getpasshash避免帳號鎖定()
    {
        // 每個 sensor 的數值擷取各自呼叫一次 GetJsonAsync。密碼打錯時若每次都重打
        // getpasshash，三千個 sensor 就是三千次登入失敗，足以觸發 PRTG 的帳號鎖定。
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
            usernameOrEmpty: "operator",
            passwordOrEmpty: "wrongpass");

        for (var i = 0; i < 5; i++)
        {
            var ex = await Assert.ThrowsAsync<PrtgClientException>(() =>
                client.GetJsonAsync("/api/table.json?content=sensors"));
            Assert.Contains("帳號或密碼", ex.Message);
        }

        var passhashAttempts = stub.Requests.Count(r => r.Url.Contains("getpasshash.htm"));
        Assert.Equal(1, passhashAttempts);
    }

    [Fact]
    public async Task 帳密模式_passhash回HTML錯誤頁時擲例外()
    {
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<!DOCTYPE html><html><body>Error login</body></html>", Encoding.UTF8, "text/html")
                    });
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
            usernameOrEmpty: "operator",
            passwordOrEmpty: "anypass");

        var ex = await Assert.ThrowsAsync<PrtgClientException>(() =>
            client.GetJsonAsync("/api/table.json?content=sensors"));

        // HTML 可能是登入頁也可能是代理維護頁，訊息不可一口咬定帳密錯
        Assert.Contains("HTML", ex.Message);
        Assert.Contains("帳號密碼", ex.Message);
    }

    [Fact]
    public async Task 帳密模式_HTML回應同樣黏住不重打getpasshash()
    {
        // 黏住的範圍不只 401：HTML 登入頁也是憑證級失敗，不黏的話一樣是「sensor 數 × 登入失敗」
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    return Task.FromResult(HtmlResponse(HttpStatusCode.OK, "<html>login</html>"));
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
            usernameOrEmpty: "operator",
            passwordOrEmpty: "anypass");

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync("/api/x"));
        }

        Assert.Equal(1, stub.Requests.Count(r => r.Url.Contains("getpasshash.htm")));
    }

    [Fact]
    public async Task 帳密模式_伺服器5xx不黏住_下次呼叫仍會重試getpasshash()
    {
        // 傳輸類／伺服器暫時故障要保留重試：PRTG 短暫 500 一次不該讓整趟 run 全滅。
        // 若日後把 5xx 也改成 FailCredentials，這條會紅。
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
            usernameOrEmpty: "operator",
            passwordOrEmpty: "anypass");

        await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync("/api/x"));
        await Assert.ThrowsAsync<PrtgClientException>(() => client.GetJsonAsync("/api/x"));

        Assert.Equal(2, stub.Requests.Count(r => r.Url.Contains("getpasshash.htm")));
    }

    [Fact]
    public async Task 帳密模式_例外訊息不含密碼與passhash()
    {
        const string specialPassword = "p@ss word+/=";
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    throw new HttpRequestException($"Failed connecting to {req.RequestUri}");
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
            usernameOrEmpty: "testuser",
            passwordOrEmpty: specialPassword);

        var ex = await Assert.ThrowsAsync<PrtgClientException>(() =>
            client.GetJsonAsync("/api/table.json?content=sensors"));

        var escapedPassword = Uri.EscapeDataString(specialPassword);
        Assert.DoesNotContain(specialPassword, ex.Message);
        Assert.DoesNotContain(escapedPassword, ex.Message);
        Assert.DoesNotContain(specialPassword, ex.ToString());
        Assert.DoesNotContain(escapedPassword, ex.ToString());
    }

    [Fact]
    public void 帳密模式_username為空時建構期擲例外()
    {
        var ex = Assert.Throws<PrtgClientException>(() =>
            new PrtgClient(
                baseUrl: ValidUrl,
                tokenOrEmpty: "",
                timeoutSeconds: 30,
                ignoreSslErrors: false,
                handler: null,
                authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
                usernameOrEmpty: "   ",
                passwordOrEmpty: "mypassword"));

        Assert.Contains("帳號", ex.Message);
    }

    [Fact]
    public async Task passhash模式_請求帶username與passhash且完全不呼叫getpasshash()
    {
        var stub = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"))
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Passhash,
            usernameOrEmpty: "operator",
            passwordOrEmpty: "",
            passhashOrEmpty: "my-passhash-999");

        await client.GetJsonAsync("/api/table.json?content=sensors");
        await client.GetJsonAsync("/api/table.json?content=devices");
        await client.GetJsonAsync("/api/table.json?content=messages");

        Assert.Equal(3, stub.Requests.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Contains("username=operator", stub.Requests[i].Url);
            Assert.Contains("passhash=my-passhash-999", stub.Requests[i].Url);
            Assert.DoesNotContain("password=", stub.Requests[i].Url);
            Assert.DoesNotContain("getpasshash.htm", stub.Requests[i].Url);
        }
        Assert.DoesNotContain(stub.Requests, r => r.Url.Contains("getpasshash.htm"));
    }

    [Fact]
    public void passhash模式_username為空時建構期擲例外()
    {
        var ex = Assert.Throws<PrtgClientException>(() =>
            new PrtgClient(
                baseUrl: ValidUrl,
                tokenOrEmpty: "",
                timeoutSeconds: 30,
                ignoreSslErrors: false,
                handler: null,
                authMode: LogForesight.Core.Models.PrtgAuthModes.Passhash,
                usernameOrEmpty: "   ",
                passwordOrEmpty: "",
                passhashOrEmpty: "my-passhash-999"));

        Assert.Contains("帳號", ex.Message);
    }

    [Fact]
    public void passhash模式_passhash為空時建構期擲例外()
    {
        var ex = Assert.Throws<PrtgClientException>(() =>
            new PrtgClient(
                baseUrl: ValidUrl,
                tokenOrEmpty: "",
                timeoutSeconds: 30,
                ignoreSslErrors: false,
                handler: null,
                authMode: LogForesight.Core.Models.PrtgAuthModes.Passhash,
                usernameOrEmpty: "operator",
                passwordOrEmpty: "",
                passhashOrEmpty: "   "));

        Assert.Contains("passhash", ex.Message);
    }

    [Fact]
    public async Task passhash模式_例外訊息不含passhash()
    {
        const string specialPasshash = "ph@sh+/= 1";
        var stub = new StubHandler
        {
            OnSend = (req, _) => throw new HttpRequestException($"Failed connecting to {req.RequestUri}")
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Passhash,
            usernameOrEmpty: "operator",
            passwordOrEmpty: "",
            passhashOrEmpty: specialPasshash);

        var ex = await Assert.ThrowsAsync<PrtgClientException>(() =>
            client.GetJsonAsync("/api/table.json?content=sensors"));

        var escapedPasshash = Uri.EscapeDataString(specialPasshash);
        Assert.DoesNotContain(specialPasshash, ex.Message);
        Assert.DoesNotContain(escapedPasshash, ex.Message);
        Assert.DoesNotContain(specialPasshash, ex.ToString());
        Assert.DoesNotContain(escapedPasshash, ex.ToString());
    }

    [Fact]
    public async Task token模式與password模式在新增passhash模式後行為不變()
    {
        var passhashCount = 0;
        var stub = new StubHandler
        {
            OnSend = (req, _) =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("getpasshash.htm"))
                {
                    Interlocked.Increment(ref passhashCount);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("pw-passhash\r\n", Encoding.UTF8, "text/plain")
                    });
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"));
            }
        };

        // 1. token 模式
        using var tokenClient = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: SampleToken,
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Token);

        await tokenClient.GetJsonAsync("/api/table.json?content=sensors");

        Assert.Single(stub.Requests);
        Assert.Contains($"apitoken={SampleToken}", stub.Requests[0].Url);
        Assert.DoesNotContain("username=", stub.Requests[0].Url);
        Assert.DoesNotContain("passhash=", stub.Requests[0].Url);

        stub.Requests.Clear();

        // 2. password 模式
        using var passwordClient = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: "",
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Password,
            usernameOrEmpty: "admin",
            passwordOrEmpty: "secret");

        await passwordClient.GetJsonAsync("/api/table.json?content=sensors");

        Assert.Equal(1, passhashCount);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains("getpasshash.htm", stub.Requests[0].Url);
        Assert.Contains("username=admin", stub.Requests[0].Url);
        Assert.Contains("passhash=pw-passhash", stub.Requests[1].Url);
    }

    [Fact]
    public async Task token模式行為不變()
    {
        var stub = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"))
        };

        using var client = new PrtgClient(
            baseUrl: ValidUrl,
            tokenOrEmpty: SampleToken,
            timeoutSeconds: 30,
            ignoreSslErrors: false,
            handler: stub,
            authMode: LogForesight.Core.Models.PrtgAuthModes.Token);

        await client.GetJsonAsync("/api/table.json?content=sensors");

        Assert.Single(stub.Requests);
        var req = stub.Requests[0];
        Assert.Contains($"apitoken={SampleToken}", req.Url);
        Assert.DoesNotContain("username=", req.Url);
        Assert.DoesNotContain("passhash=", req.Url);
        Assert.DoesNotContain("password=", req.Url);
        Assert.DoesNotContain("getpasshash.htm", req.Url);
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
