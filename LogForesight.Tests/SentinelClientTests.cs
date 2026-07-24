using System.Net;
using System.Text;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SentinelClient 的 REST 協定層測試（docs/NETIQ-API-PLAN.md §3.1、§7）：認證生命週期、
/// event-search job 生命週期、清理保證、重試/不重試邊界。用 <see cref="StubHandler"/> 取代真實
/// HTTP 連線，不需要真 Sentinel 環境。
/// </summary>
public class SentinelClientTests
{
    private static SentinelServer Server(string baseUrl = "https://sentinel.local:8443") => new()
    {
        Name = "SENTINEL-A",
        BaseUrl = baseUrl,
        Username = "svc-lfquery",
        Password = "secret"
    };

    private static NetIqSettings Settings(int retryCount = 3, int timeoutSeconds = 120, int queryDelayMs = 0, int pageSize = 500) => new()
    {
        RetryCount = retryCount,
        TimeoutSeconds = timeoutSeconds,
        QueryDelayMs = queryDelayMs,
        PageSize = pageSize
    };

    private static string AuthUrl(string baseUrl = "https://sentinel.local:8443") => $"{baseUrl}/SentinelAuthServices/auth/tokens";
    private static string JobCollectionUrl(string baseUrl = "https://sentinel.local:8443") => $"{baseUrl}/SentinelRESTServices/objects/event-search";
    private static string JobUrl(string id, string baseUrl = "https://sentinel.local:8443") => $"{baseUrl}/SentinelRESTServices/objects/event-search/{id}";

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json, string? locationHeader = null)
    {
        var resp = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (locationHeader != null) resp.Headers.Location = new Uri(locationHeader);
        return resp;
    }

    [Fact]
    public async Task 完整查詢流程_認證建立工作輪詢分頁後刪除工作()
    {
        var handler = new StubHandler();
        var jobUrl = JobUrl("123");
        var resultsUrl = $"https://sentinel.local:8443/SentinelRESTServices/objects/event?query=_jobid_.123&page=1&pagesize=2";

        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"Token\":\"tok-1\"}"));

            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.Created, "{}", locationHeader: jobUrl));

            if (req.Method == HttpMethod.Get && url == jobUrl)
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    $"{{\"status\":2,\"found\":3,\"avail\":3,\"results\":{{\"@href\":\"{resultsUrl}\"}}}}"));

            if (req.Method == HttpMethod.Get && url.Contains("page=1"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "[{\"evt\":\"A\",\"dt\":\"1\"},{\"evt\":\"B\",\"dt\":\"2\"}]"));

            if (req.Method == HttpMethod.Get && url.Contains("page=2"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "[{\"evt\":\"C\",\"dt\":\"3\"}]"));

            if (req.Method == HttpMethod.Delete && url == jobUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        await using var client = new SentinelClient(Server(), Settings(pageSize: 2), handler);
        var result = await client.SearchAsync(new SentinelSearchRequest(
            "evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, PageSize: 2));

        Assert.Equal(3, result.Found);
        Assert.Equal(3, result.Events.Count);
        Assert.False(result.Truncated);
        Assert.Equal(SentinelJobState.Completed, result.State);
        Assert.Equal(new[] { "A", "B", "C" }, result.Events.Select(e => e.Fields["evt"]));

        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Delete && r.Url == jobUrl);
    }

    [Fact]
    public async Task 同一client兩次查詢_只認證一次()
    {
        var handler = new StubHandler();
        var authCalls = 0;

        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl())
            {
                authCalls++;
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"Token\":\"tok-1\"}"));
            }
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.Created, "{}", locationHeader: JobUrl("1")));
            if (req.Method == HttpMethod.Get && url == JobUrl("1"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    "{\"status\":2,\"found\":1,\"avail\":1,\"results\":{\"@href\":\"https://sentinel.local:8443/SentinelRESTServices/objects/event?page=1\"}}"));
            if (req.Method == HttpMethod.Get && url.Contains("/objects/event?"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "[{\"evt\":\"A\"}]"));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        await using var client = new SentinelClient(Server(), Settings(), handler);
        await client.SearchAsync(new SentinelSearchRequest("evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));
        await client.SearchAsync(new SentinelSearchRequest("evt:test2", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

        Assert.Equal(1, authCalls);
    }

    [Fact]
    public async Task token過期時_重新認證後重放請求()
    {
        var handler = new StubHandler();
        var authCalls = 0;
        var jobCreateCalls = 0;

        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();

            if (req.Method == HttpMethod.Post && url == AuthUrl())
            {
                authCalls++;
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, $"{{\"Token\":\"tok-{authCalls}\"}}"));
            }

            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
            {
                jobCreateCalls++;
                // 第一次用舊 token 建立工作時模擬 401（token 已過期），第二次（重新認證後）才成功
                if (jobCreateCalls == 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                Assert.Equal("X-SAML tok-2", req.Headers.Authorization!.ToString());
                return Task.FromResult(JsonResponse(HttpStatusCode.Created, "{}", locationHeader: JobUrl("1")));
            }

            if (req.Method == HttpMethod.Get && url == JobUrl("1"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    "{\"status\":2,\"found\":0,\"avail\":0,\"results\":null}"));

            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        await using var client = new SentinelClient(Server(), Settings(), handler);
        var result = await client.SearchAsync(new SentinelSearchRequest("evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

        Assert.Equal(2, authCalls);
        Assert.Equal(2, jobCreateCalls);
        Assert.Equal(SentinelJobState.Completed, result.State);
    }

    [Fact]
    public async Task 重新認證後仍被拒_擲例外不無限重試()
    {
        var handler = new StubHandler();
        var authCalls = 0;

        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl())
            {
                authCalls++;
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, $"{{\"Token\":\"tok-{authCalls}\"}}"));
            }
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        await using var client = new SentinelClient(Server(), Settings(), handler);
        var ex = await Assert.ThrowsAsync<SentinelClientException>(() =>
            client.SearchAsync(new SentinelSearchRequest("evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)));

        Assert.Equal(2, authCalls); // 只重新認證一次，不會無限迴圈
        Assert.Contains("驗證被拒", ex.Message);
    }

    [Fact]
    public async Task 找不到資料的工作_不呼叫結果頁_直接回空清單()
    {
        var handler = new StubHandler();
        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"Token\":\"tok-1\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.Created, "{}", locationHeader: JobUrl("1")));
            if (req.Method == HttpMethod.Get && url == JobUrl("1"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"status\":2,\"found\":0,\"avail\":0}"));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求（不該查結果頁）：{req.Method} {url}");
        };

        await using var client = new SentinelClient(Server(), Settings(), handler);
        var result = await client.SearchAsync(new SentinelSearchRequest("evt:nomatch", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

        Assert.Empty(result.Events);
        Assert.Equal(0, result.Found);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task 實際取回筆數少於found_標記為截斷()
    {
        var handler = new StubHandler();
        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"Token\":\"tok-1\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.Created, "{}", locationHeader: JobUrl("1")));
            if (req.Method == HttpMethod.Get && url == JobUrl("1"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    "{\"status\":2,\"found\":100,\"avail\":2,\"results\":{\"@href\":\"https://sentinel.local:8443/SentinelRESTServices/objects/event?page=1\"}}"));
            if (req.Method == HttpMethod.Get && url.Contains("/objects/event?"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "[{\"evt\":\"A\"},{\"evt\":\"B\"}]"));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        // max-results 上限設 2，比對 max-results 上限與 avail 都會讓 events 數少於 found=100
        await using var client = new SentinelClient(Server(), Settings(pageSize: 500), handler);
        var result = await client.SearchAsync(new SentinelSearchRequest(
            "evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, MaxResults: 2));

        Assert.Equal(100, result.Found);
        Assert.Equal(2, result.Events.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task 建立工作後查詢逾時_仍嘗試刪除工作()
    {
        var handler = new StubHandler();
        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"Token\":\"tok-1\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.Created, "{}", locationHeader: JobUrl("1")));
            if (req.Method == HttpMethod.Get && url == JobUrl("1"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"status\":1}")); // 永遠 Running
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        await using var client = new SentinelClient(Server(), Settings(timeoutSeconds: 1), handler);
        var ex = await Assert.ThrowsAsync<SentinelClientException>(() =>
            client.SearchAsync(new SentinelSearchRequest("evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)));

        Assert.Contains("逾時", ex.Message);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Delete && r.Url == JobUrl("1"));
    }

    [Fact]
    public async Task 結果頁持續失敗_仍嘗試刪除工作()
    {
        var handler = new StubHandler();
        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"Token\":\"tok-1\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.Created, "{}", locationHeader: JobUrl("1")));
            if (req.Method == HttpMethod.Get && url == JobUrl("1"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    "{\"status\":2,\"found\":5,\"avail\":5,\"results\":{\"@href\":\"https://sentinel.local:8443/SentinelRESTServices/objects/event?page=1\"}}"));
            if (req.Method == HttpMethod.Get && url.Contains("/objects/event?"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        // RetryCount=1：500 錯誤最多重試 1 次，加速測試
        await using var client = new SentinelClient(Server(), Settings(retryCount: 1), handler);
        await Assert.ThrowsAsync<SentinelClientException>(() =>
            client.SearchAsync(new SentinelSearchRequest("evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)));

        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Delete && r.Url == JobUrl("1"));
    }

    [Fact]
    public async Task 呼叫端取消_仍嘗試刪除工作()
    {
        var handler = new StubHandler();
        using var cts = new CancellationTokenSource();

        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"Token\":\"tok-1\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.Created, "{}", locationHeader: JobUrl("1")));
            if (req.Method == HttpMethod.Get && url == JobUrl("1"))
            {
                // 工作已建立完成（jobHref 已在 SearchAsync 內被捕捉）後，才讓外層 ct 取消，
                // 模擬呼叫端在輪詢期間放棄等待——此時清理仍必須用獨立的 token 執行。
                cts.Cancel();
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"status\":1}"));
            }
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        await using var client = new SentinelClient(Server(), Settings(), handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SearchAsync(new SentinelSearchRequest("evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow), cts.Token));

        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Delete && r.Url == JobUrl("1"));
    }

    [Fact]
    public async Task _503錯誤重試後成功()
    {
        var handler = new StubHandler();
        var attempts = 0;

        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"Token\":\"tok-1\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
            {
                attempts++;
                if (attempts == 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Task.FromResult(JsonResponse(HttpStatusCode.Created, "{}", locationHeader: JobUrl("1")));
            }
            if (req.Method == HttpMethod.Get && url == JobUrl("1"))
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"status\":2,\"found\":0,\"avail\":0}"));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        await using var client = new SentinelClient(Server(), Settings(retryCount: 2), handler);
        var result = await client.SearchAsync(new SentinelSearchRequest("evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

        Assert.Equal(2, attempts); // 第一次 503，重試後第二次成功
        Assert.Equal(SentinelJobState.Completed, result.State);
    }

    [Fact]
    public async Task _400錯誤不重試_直接擲例外()
    {
        var handler = new StubHandler();
        var attempts = 0;

        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl())
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"Token\":\"tok-1\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl())
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("filter 語法錯誤") });
            }
            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        await using var client = new SentinelClient(Server(), Settings(retryCount: 3), handler);
        var ex = await Assert.ThrowsAsync<SentinelClientException>(() =>
            client.SearchAsync(new SentinelSearchRequest("bad filter", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)));

        Assert.Equal(1, attempts); // 400 不重試
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public async Task 帳密錯誤_認證回傳401_擲出可顯示的例外()
    {
        var handler = new StubHandler();
        handler.OnSend = (req, _) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.ToString() == AuthUrl())
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            throw new InvalidOperationException("不該送出認證以外的請求");
        };

        await using var client = new SentinelClient(Server(), Settings(), handler);
        var ex = await Assert.ThrowsAsync<SentinelClientException>(() =>
            client.SearchAsync(new SentinelSearchRequest("evt:test", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)));

        Assert.Contains("認證失敗", ex.Message);
        Assert.DoesNotContain("secret", ex.Message); // 密碼不得出現在例外訊息
    }

    [Fact]
    public void 建構子_BaseUrl未設定時立即擲例外()
    {
        Assert.Throws<SentinelClientException>(() => new SentinelClient(Server(baseUrl: ""), Settings()));
    }

    // ── 純函數解析邏輯（internal，InternalsVisibleTo 見 LogForesight.Core.csproj） ──

    [Fact]
    public void ParseEventsPage_純陣列根節點()
    {
        var events = SentinelClient.ParseEventsPage("[{\"evt\":\"A\",\"sev\":\"3\"},{\"evt\":\"B\"}]");

        Assert.Equal(2, events.Count);
        Assert.Equal("A", events[0].Fields["evt"]);
        Assert.Equal("3", events[0].Fields["sev"]);
    }

    [Theory]
    [InlineData("items")]
    [InlineData("event")]
    [InlineData("events")]
    [InlineData("results")]
    [InlineData("rows")]
    public void ParseEventsPage_物件包裝常見鍵名(string key)
    {
        var json = $"{{\"{key}\":[{{\"evt\":\"A\"}}]}}";
        var events = SentinelClient.ParseEventsPage(json);

        Assert.Single(events);
        Assert.Equal("A", events[0].Fields["evt"]);
    }

    [Fact]
    public void ParseEventsPage_未知鍵名_保底取第一個陣列屬性()
    {
        var events = SentinelClient.ParseEventsPage("{\"totalCount\":1,\"unknownKey\":[{\"evt\":\"A\"}]}");

        Assert.Single(events);
        Assert.Equal("A", events[0].Fields["evt"]);
    }

    [Fact]
    public void ParseEventsPage_不是合法JSON_回空清單不擲例外()
    {
        var events = SentinelClient.ParseEventsPage("<html>not json</html>");

        Assert.Empty(events);
    }

    [Fact]
    public void ParseEventsPage_找不到任何陣列_回空清單()
    {
        var events = SentinelClient.ParseEventsPage("{\"status\":\"ok\"}");

        Assert.Empty(events);
    }

    [Fact]
    public void SetPageQueryParam_已有page參數時取代()
    {
        var result = SentinelClient.SetPageQueryParam("https://s/objects/event?query=x&page=1&pagesize=10", 3);

        Assert.Equal("https://s/objects/event?query=x&page=3&pagesize=10", result);
    }

    [Fact]
    public void SetPageQueryParam_沒有page參數時附加()
    {
        var result = SentinelClient.SetPageQueryParam("https://s/objects/event?query=x", 2);

        Assert.Equal("https://s/objects/event?query=x&page=2", result);
    }

    [Fact]
    public void ParseJobStatus_數字狀態碼()
    {
        var status = SentinelClient.ParseJobStatus("{\"status\":2,\"found\":10,\"avail\":10,\"results\":{\"@href\":\"https://x\"}}");

        Assert.Equal(SentinelJobState.Completed, status.State);
        Assert.Equal(10, status.Found);
        Assert.Equal(10, status.Avail);
        Assert.Equal("https://x", status.ResultsHref);
    }

    [Fact]
    public void ParseJobStatus_字串狀態值()
    {
        var status = SentinelClient.ParseJobStatus("{\"status\":\"Running\"}");

        Assert.Equal(SentinelJobState.Running, status.State);
    }

    [Fact]
    public void ParseJobStatus_數字欄位是字串型別_容錯解析不擲例外()
    {
        // TryGetInt32 在非 Number 型別上擲 InvalidOperationException（不是 JsonException）——
        // 這裡釘住「API 版本差異把數字加引號」時不會炸，最多當 0
        var stringNumbers = SentinelClient.ParseJobStatus("{\"status\":2,\"found\":\"10\",\"avail\":\"8\"}");
        Assert.Equal(10, stringNumbers.Found);
        Assert.Equal(8, stringNumbers.Avail);

        var garbage = SentinelClient.ParseJobStatus("{\"status\":2,\"found\":{\"weird\":true},\"avail\":null}");
        Assert.Equal(0, garbage.Found);
        Assert.Equal(0, garbage.Avail);
    }

    [Fact]
    public void ParseJobStatus_缺席欄位一律當作仍在執行()
    {
        var status = SentinelClient.ParseJobStatus("{}");

        Assert.Equal(SentinelJobState.Running, status.State);
        Assert.Equal(0, status.Found);
        Assert.Null(status.ResultsHref);
    }

    /// <summary>
    /// 把每次 SendAsync 的請求方法／URL／內容記錄下來，回應由測試以 <see cref="OnSend"/> 提供。
    /// 取代真實 <see cref="HttpClient"/> 連線，讓 SentinelClient 的協定邏輯可離線測試。
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
