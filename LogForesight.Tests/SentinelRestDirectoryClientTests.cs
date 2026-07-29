using System.Net;
using System.Text;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// SentinelRestDirectoryClient：網段範圍掃描的真實 client（docs/NETIQ-API-PLAN.md §3.4，
/// 2026-07-29 定案）。用 <see cref="StubHandler"/> 模擬 Sentinel 回應，驗證自適應窗口計算、
/// 零事件短路、截斷警告、sn 取眾數、IP fallback——不需要真 Sentinel 環境。
/// </summary>
public class SentinelRestDirectoryClientTests
{
    private static SentinelServer Server() => new()
    {
        Name = "SENTINEL-A", BaseUrl = "https://sentinel.local:8443", Username = "svc", Password = "pw"
    };

    private static NetiqOptions Options() => new() { RetryCount = 1, TimeoutSeconds = 30, QueryDelayMs = 0 };

    private const string AuthUrl = "https://sentinel.local:8443/SentinelAuthServices/auth/tokens";
    private const string JobCollectionUrl = "https://sentinel.local:8443/SentinelRESTServices/objects/event-search";

    [Fact]
    public async Task 網段近24h無事件_回空清單不發第二個查詢()
    {
        var handler = new StubHandler();
        var jobCalls = 0;
        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl)
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl)
            {
                jobCalls++;
                return Task.FromResult(Json(HttpStatusCode.Created, "{}", $"{JobCollectionUrl}/job1"));
            }
            if (req.Method == HttpMethod.Get && url == $"{JobCollectionUrl}/job1")
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":2,\"found\":0,\"avail\":0}"));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(Server(), "10.232.11", CancellationToken.None);

        Assert.Empty(result.Hosts);
        Assert.Contains("無任何事件回報", result.CoverageNote);
        Assert.Equal(1, jobCalls);   // 只發了計數查詢，沒有浪費第二次查詢
    }

    [Fact]
    public async Task 量級低於目標值_掃描窗口取滿24小時()
    {
        var handler = new StubHandler();
        var requestBodies = new List<string>();
        handler.OnSend = (req, body) =>
        {
            var url = req.RequestUri!.ToString();
            if (body != null) requestBodies.Add(body);

            if (req.Method == HttpMethod.Post && url == AuthUrl)
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl)
            {
                var jobId = requestBodies.Count;   // 遞增區分計數查詢/主查詢
                return Task.FromResult(Json(HttpStatusCode.Created, "{}", $"{JobCollectionUrl}/job{jobId}"));
            }
            if (req.Method == HttpMethod.Get && url.EndsWith("/job1"))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":2,\"found\":1000,\"avail\":0}"));
            if (req.Method == HttpMethod.Get && url.EndsWith("/job2"))
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $"{{\"status\":2,\"found\":2,\"avail\":2,\"results\":{{\"@href\":\"{JobCollectionUrl}/job2/results\"}}}}"));
            if (req.Method == HttpMethod.Get && url.Contains("/job2/results"))
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "[{\"repip\":\"10.232.11.5\",\"sn\":\"tc-crecdc01\"},{\"repip\":\"10.232.11.6\",\"sn\":\"tp-brkdc01\"}]"));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(Server(), "10.232.11", CancellationToken.None);

        Assert.Equal(2, result.Hosts.Count);
        Assert.Contains("24 小時", result.CoverageNote);
        Assert.Contains("10.232.11.*", result.CoverageNote);

        // 主查詢的 start/end 應橫跨 24 小時（第二個 job 建立請求的本文）
        var mainJobBody = requestBodies[1];
        Assert.Contains("\"filter\":\"repip:10.232.11.*\"", mainJobBody);
    }

    [Fact]
    public async Task 量級高於目標值_窗口依比例縮短()
    {
        var handler = new StubHandler();
        var requestBodies = new List<string>();
        handler.OnSend = (req, body) =>
        {
            var url = req.RequestUri!.ToString();
            if (body != null) requestBodies.Add(body);

            if (req.Method == HttpMethod.Post && url == AuthUrl)
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl)
                return Task.FromResult(Json(HttpStatusCode.Created, "{}", $"{JobCollectionUrl}/job{requestBodies.Count}"));
            // found=759052（第二輪 probe 實測數字）遠大於 CoverageTargetResults=50000
            if (req.Method == HttpMethod.Get && url.EndsWith("/job1"))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":2,\"found\":759052,\"avail\":0}"));
            if (req.Method == HttpMethod.Get && url.EndsWith("/job2"))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":2,\"found\":0,\"avail\":0}"));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(Server(), "10.232.11", CancellationToken.None);

        // 24h * 50000/759052 ≈ 1.6 小時——用「近24 小時內」這個完整片語排除（涵蓋範圍句），
        // 不能只查「24 小時」子字串，句子後段本來就會提到「該網段近 24 小時共 X 筆事件」
        Assert.DoesNotContain("近24 小時內", result.CoverageNote);
        Assert.Contains("近1.6 小時內", result.CoverageNote);
        Assert.Contains("759,052", result.CoverageNote);
    }

    [Fact]
    public async Task 極端量級_窗口不會小於下限五分鐘()
    {
        var handler = new StubHandler();
        var jobCount = 0;
        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl)
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl)
            {
                jobCount++;
                return Task.FromResult(Json(HttpStatusCode.Created, "{}", $"{JobCollectionUrl}/job{jobCount}"));
            }
            if (req.Method == HttpMethod.Get && url.EndsWith("/job1"))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":2,\"found\":50000000,\"avail\":0}"));
            if (req.Method == HttpMethod.Get && url.EndsWith("/job2"))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":2,\"found\":0,\"avail\":0}"));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(Server(), "10.232.11", CancellationToken.None);

        Assert.Contains("5 分鐘", result.CoverageNote);
    }

    [Fact]
    public async Task 主查詢被截斷_回傳警告()
    {
        var handler = new StubHandler();
        var jobCount = 0;
        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl)
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl)
            {
                jobCount++;
                return Task.FromResult(Json(HttpStatusCode.Created, "{}", $"{JobCollectionUrl}/job{jobCount}"));
            }
            if (req.Method == HttpMethod.Get && url.EndsWith("/job1"))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":2,\"found\":1000,\"avail\":0}"));
            if (req.Method == HttpMethod.Get && url.EndsWith("/job2"))
                // found 遠大於實際取回：avail 只給 1 筆，MaxResultsPerJob 又被 CoverageTargetResults 限制，
                // 用小 avail 模擬取回筆數少於 found 的截斷情境
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $"{{\"status\":2,\"found\":999999,\"avail\":1,\"results\":{{\"@href\":\"{JobCollectionUrl}/job2/results\"}}}}"));
            if (req.Method == HttpMethod.Get && url.Contains("/job2/results"))
                return Task.FromResult(Json(HttpStatusCode.OK, "[{\"repip\":\"10.232.11.5\",\"sn\":\"srv1\"}]"));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(Server(), "10.232.11", CancellationToken.None);

        Assert.Single(result.Warnings);
        Assert.Contains("可能不完整", result.Warnings[0]);
    }

    [Fact]
    public async Task HostName取同一IP最常見的sn值()
    {
        var handler = new StubHandler();
        var jobCount = 0;
        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl)
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl)
            {
                jobCount++;
                return Task.FromResult(Json(HttpStatusCode.Created, "{}", $"{JobCollectionUrl}/job{jobCount}"));
            }
            if (req.Method == HttpMethod.Get && url.EndsWith("/job1"))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":2,\"found\":100,\"avail\":0}"));
            if (req.Method == HttpMethod.Get && url.EndsWith("/job2"))
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $"{{\"status\":2,\"found\":3,\"avail\":3,\"results\":{{\"@href\":\"{JobCollectionUrl}/job2/results\"}}}}"));
            if (req.Method == HttpMethod.Get && url.Contains("/job2/results"))
                // 同一 IP 三筆事件，兩筆 sn=tc-crecdc01、一筆是雜訊值 sn=WEIRD——眾數應勝出
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "[{\"repip\":\"10.232.11.5\",\"sn\":\"tc-crecdc01\"}," +
                    "{\"repip\":\"10.232.11.5\",\"sn\":\"tc-crecdc01\"}," +
                    "{\"repip\":\"10.232.11.5\",\"sn\":\"WEIRD\"}]"));
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(Server(), "10.232.11", CancellationToken.None);

        var host = Assert.Single(result.Hosts);
        Assert.Equal("tc-crecdc01", host.HostName);
        Assert.Equal("10.232.11.5", host.IpAddress);
    }

    [Fact]
    public async Task sn缺席時HostName退回IP()
    {
        var handler = new StubHandler();
        var jobCount = 0;
        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl)
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}"));
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl)
            {
                jobCount++;
                return Task.FromResult(Json(HttpStatusCode.Created, "{}", $"{JobCollectionUrl}/job{jobCount}"));
            }
            if (req.Method == HttpMethod.Get && url.EndsWith("/job1"))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":2,\"found\":10,\"avail\":0}"));
            if (req.Method == HttpMethod.Get && url.EndsWith("/job2"))
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $"{{\"status\":2,\"found\":1,\"avail\":1,\"results\":{{\"@href\":\"{JobCollectionUrl}/job2/results\"}}}}"));
            if (req.Method == HttpMethod.Get && url.Contains("/job2/results"))
                return Task.FromResult(Json(HttpStatusCode.OK, "[{\"repip\":\"10.232.11.9\"}]"));   // 無 sn 欄位
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var result = await client.ListHostsAsync(Server(), "10.232.11", CancellationToken.None);

        var host = Assert.Single(result.Hosts);
        Assert.Equal("10.232.11.9", host.HostName);
    }

    [Fact]
    public async Task BaseUrl未設定_立即擲例外不發任何請求()
    {
        var handler = new StubHandler();
        handler.OnSend = (req, _) => throw new InvalidOperationException($"不該送出任何請求：{req.RequestUri}");

        var client = new SentinelRestDirectoryClient(Options(), handler);
        var server = new SentinelServer { Name = "S", BaseUrl = "", Username = "u", Password = "p" };

        await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(server, "10.232.11", CancellationToken.None));
    }

    [Fact]
    public async Task 網段格式不正確_轉成NetiqDiscoveryException()
    {
        var handler = new StubHandler();
        handler.OnSend = (req, _) => throw new InvalidOperationException($"不該送出任何請求：{req.RequestUri}");

        var client = new SentinelRestDirectoryClient(Options(), handler);

        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "not-a-subnet", CancellationToken.None));
        Assert.Contains("網段格式不正確", ex.Message);
    }

    [Fact]
    public async Task Sentinel回應錯誤_轉成可顯示的NetiqDiscoveryException()
    {
        var handler = new StubHandler();
        handler.OnSend = (req, _) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.ToString() == AuthUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            throw new InvalidOperationException("不該送出認證以外的請求");
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);

        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "10.232.11", CancellationToken.None));
        Assert.Contains("SENTINEL-A", ex.Message);
        Assert.DoesNotContain("pw", ex.Message);   // 密碼不得出現在例外訊息
    }

    /// <summary>
    /// 分頁迴圈不受單次逾時約束（SentinelClient 的 TimeoutSeconds 只管單次呼叫與 job 輪詢），
    /// 50 頁×真實往返會讓管理員在精靈前乾等——整趟掃描必須有自己的總預算，
    /// 且逾時要**明確報錯**而不是回傳半套清單（半套會被誤認為該網段的完整名單）。
    /// </summary>
    [Fact]
    public async Task 整趟掃描超過總預算_明確報錯且不回傳半套結果()
    {
        var handler = new StubHandler();
        var pageCalls = 0;
        var jobsCreated = 0;
        handler.OnSend = async (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl)
                return Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}");
            if (req.Method == HttpMethod.Post && url == JobCollectionUrl)
            {
                jobsCreated++;   // 1=計數查詢、2=主查詢（主查詢才有結果頁可翻）
                return Json(HttpStatusCode.Created, "{}", $"{JobCollectionUrl}/job{jobsCreated}");
            }
            if (req.Method == HttpMethod.Get && url.EndsWith("/job1"))
                return Json(HttpStatusCode.OK, "{\"status\":2,\"found\":40000,\"avail\":40000}");
            if (req.Method == HttpMethod.Get && url.EndsWith("/job2"))
                return Json(HttpStatusCode.OK,
                    $"{{\"status\":2,\"found\":40000,\"avail\":40000,\"results\":{{\"@href\":\"{JobCollectionUrl}/job2/results\"}}}}");
            if (req.Method == HttpMethod.Get && url.Contains("/job2/results"))
            {
                pageCalls++;
                // 每頁都慢——模擬真實 Sentinel 往返（實測約 1.7 秒），讓分頁迴圈撞上總預算
                await Task.Delay(TimeSpan.FromMilliseconds(400), CancellationToken.None);
                var sb = new StringBuilder("[");
                for (var i = 0; i < 1000; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"{{\"repip\":\"10.232.11.{i % 250}\",\"sn\":\"h{i % 250}\"}}");
                }
                sb.Append(']');
                return Json(HttpStatusCode.OK, sb.ToString());
            }
            if (req.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        // 總預算壓到 2 秒：驗證的是「有沒有這道 deadline」，不是正式值本身
        var client = new SentinelRestDirectoryClient(Options(), handler, totalBudgetSeconds: 2);
        var ex = await Assert.ThrowsAsync<NetiqDiscoveryException>(() =>
            client.ListHostsAsync(Server(), "10.232.11", CancellationToken.None));

        Assert.Contains("超過 2 秒", ex.Message);
        Assert.Contains("更小的網段", ex.Message);
        Assert.True(pageCalls < 40, $"應在跑完全部 40 頁前就被總預算中止，實際翻了 {pageCalls} 頁");
    }

    /// <summary>呼叫端自己取消（管理員關掉分頁）不是掃描失敗，不該被包成假的錯誤訊息</summary>
    [Fact]
    public async Task 呼叫端取消_不轉成掃描失敗訊息()
    {
        var handler = new StubHandler();
        using var cts = new CancellationTokenSource();
        handler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url == AuthUrl)
            {
                cts.Cancel();   // 認證後管理員就關掉了分頁
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"Token\":\"tok\"}"));
            }
            if (req.Method == HttpMethod.Delete) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            throw new InvalidOperationException($"未預期的請求：{req.Method} {url}");
        };

        var client = new SentinelRestDirectoryClient(Options(), handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ListHostsAsync(Server(), "10.232.11", cts.Token));
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json, string? locationHeader = null)
    {
        var resp = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (locationHeader != null) resp.Headers.Location = new Uri(locationHeader);
        return resp;
    }

    /// <summary>同 SentinelClientTests 的 StubHandler，這裡另建一份是因為那個是 private 巢狀類別，
    /// 兩邊各自獨立測試檔本來就該各自擁有測試替身，不值得為此拉一個共用基底。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> OnSend { get; set; } =
            (_, _) => throw new InvalidOperationException("測試未設定 OnSend");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
            return await OnSend(request, body);
        }
    }
}
