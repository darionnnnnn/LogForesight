using System.Net;
using System.Text;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>NetIQ 診斷步驟 7b：以含 dun、tuid 的投影查一筆 4625，只回報形狀、不輸出值。</summary>
public class NetiqDunTuidProbeTests
{
    private const string BaseUrl = "https://sentinel.example.invalid:8443";
    private const string JobUrl = BaseUrl + "/SentinelRESTServices/objects/event-search/one";
    private const string ResultUrl = BaseUrl + "/SentinelRESTServices/objects/event?query=x&page=1&pagesize=1";

    private sealed class CollectingConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
        public string Text => string.Join("\n", Lines);
    }

    private static SentinelClient NewClient(HttpMessageHandler handler) => new(
        new SentinelServer { Name = "test-sentinel", BaseUrl = BaseUrl, Username = "probe-account", Password = "probe-password" },
        new NetiqOptions { RetryCount = 0, TimeoutSeconds = 30, QueryDelayMs = 0 }, handler);

    [Fact]
    public async Task 兩欄位有值_回報有值與形狀_不含實際值()
    {
        var handler = new FakeSentinel(eventsJson: "[{\"dun\":\"CONTOSO-SECRET\",\"tuid\":\"S-1-5-21-111-222-333-1001\",\"sun\":\"private-account\",\"repip\":\"203.0.113.7\"}]");
        var console = new CollectingConsole();

        var verdict = await NetiqProbeRunner.ProbeDunTuidProjectionAsync(NewClient(handler), DateTimeOffset.UtcNow, console, CancellationToken.None);

        Assert.Equal(NetiqProbeRunner.DunTuidVerdict.HasValue, verdict);
        Assert.Contains("dun：有值（長度 14，形狀 text）", console.Text);
        Assert.Contains("tuid：有值", console.Text);
        Assert.Contains("形狀 sid", console.Text);
        Assert.Contains("可投影，至少一個欄位有值", console.Text);
        Assert.DoesNotContain("CONTOSO-SECRET", console.Text);
        Assert.DoesNotContain("S-1-5-21", console.Text);
        Assert.DoesNotContain("private-account", console.Text);
        Assert.DoesNotContain("203.0.113.7", console.Text);
        Assert.Contains("\"fields\":\"", handler.JobBody);
        Assert.Contains("dun,tuid", handler.JobBody);
        Assert.Contains("rv40:4625", handler.JobBody);
    }

    [Fact]
    public async Task 兩欄位缺席或空白_回報無值()
    {
        var handler = new FakeSentinel(eventsJson: "[{\"dun\":\"\",\"sun\":\"private-account\"}]");
        var console = new CollectingConsole();

        var verdict = await NetiqProbeRunner.ProbeDunTuidProjectionAsync(NewClient(handler), DateTimeOffset.UtcNow, console, CancellationToken.None);

        Assert.Equal(NetiqProbeRunner.DunTuidVerdict.NoValue, verdict);
        Assert.Contains("dun：無值", console.Text);
        Assert.Contains("tuid：無值", console.Text);
        Assert.Contains("兩個欄位都沒有值", console.Text);
        Assert.DoesNotContain("private-account", console.Text);
    }

    [Fact]
    public async Task 伺服器回HTTP400_回報欄位被拒且不擲例外()
    {
        var handler = new FakeSentinel(jobStatusCode: HttpStatusCode.BadRequest, jobBody: "{\"error\":\"unknown field dun for 203.0.113.7\"}");
        var console = new CollectingConsole();

        var verdict = await NetiqProbeRunner.ProbeDunTuidProjectionAsync(NewClient(handler), DateTimeOffset.UtcNow, console, CancellationToken.None);

        Assert.Equal(NetiqProbeRunner.DunTuidVerdict.Rejected, verdict);
        Assert.Contains("欄位被拒（HTTP 400）", console.Text);
        Assert.DoesNotContain("203.0.113.7", console.Text);
    }

    [Fact]
    public async Task 查詢工作以錯誤狀態結束_回報欄位被拒()
    {
        var handler = new FakeSentinel(statusCode: 4); // Unavailable
        var console = new CollectingConsole();

        var verdict = await NetiqProbeRunner.ProbeDunTuidProjectionAsync(NewClient(handler), DateTimeOffset.UtcNow, console, CancellationToken.None);

        Assert.Equal(NetiqProbeRunner.DunTuidVerdict.Rejected, verdict);
        Assert.Contains("欄位被拒", console.Text);
    }

    [Fact]
    public async Task 近24小時沒有4625_回報無法判定而非無值()
    {
        var handler = new FakeSentinel(eventsJson: "[]", found: 0);
        var console = new CollectingConsole();

        var verdict = await NetiqProbeRunner.ProbeDunTuidProjectionAsync(NewClient(handler), DateTimeOffset.UtcNow, console, CancellationToken.None);

        Assert.Equal(NetiqProbeRunner.DunTuidVerdict.NoEvent, verdict);
        Assert.Contains("無法判定", console.Text);
    }

    [Fact]
    public async Task 連線類錯誤不被當成欄位被拒_照常往外擲由步驟標為失敗()
    {
        var handler = new FakeSentinel(jobStatusCode: HttpStatusCode.InternalServerError, jobBody: "boom");

        await Assert.ThrowsAsync<SentinelClientException>(() => NetiqProbeRunner.ProbeDunTuidProjectionAsync(
            NewClient(handler), DateTimeOffset.UtcNow, new CollectingConsole(), CancellationToken.None));
    }

    private sealed class FakeSentinel(string eventsJson = "[]", int found = 1, int statusCode = 2,
        HttpStatusCode jobStatusCode = HttpStatusCode.Created, string jobBody = "{}") : HttpMessageHandler
    {
        public string JobBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Post && url.EndsWith("/SentinelAuthServices/auth/tokens", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"Token\":\"test-token\"}");
            if (request.Method == HttpMethod.Post && url.EndsWith("/SentinelRESTServices/objects/event-search", StringComparison.Ordinal))
            {
                JobBody = await request.Content!.ReadAsStringAsync(ct);
                var resp = Json(jobStatusCode, jobBody);
                if (jobStatusCode == HttpStatusCode.Created) resp.Headers.Location = new Uri(JobUrl);
                return resp;
            }
            if (request.Method == HttpMethod.Get && url == JobUrl)
                return Json(HttpStatusCode.OK, $"{{\"status\":{statusCode},\"found\":{found},\"avail\":{found},\"results\":{{\"@href\":\"{ResultUrl}\"}}}}");
            if (request.Method == HttpMethod.Get && url.Contains("/SentinelRESTServices/objects/event?", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, eventsJson);
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            throw new InvalidOperationException("Unexpected request: " + request.Method + " " + url);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
