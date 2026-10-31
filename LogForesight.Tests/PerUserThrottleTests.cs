using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

/// <summary>AI 端點每人節流：手動桶 6 次／60 秒、自動桶 30 次／60 秒；快取命中不扣額；超限 429＋Retry-After</summary>
public class PerUserThrottleTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 手動桶第7次拒絕_自動桶第31次拒絕()
    {
        var manual = new ManualAiThrottle();
        for (var i = 0; i < 6; i++) Assert.True(manual.TryAcquire(1, T0));
        Assert.False(manual.TryAcquire(1, T0));

        var auto = new AutoAiThrottle();
        for (var i = 0; i < 30; i++) Assert.True(auto.TryAcquire(1, T0));
        Assert.False(auto.TryAcquire(1, T0));
    }

    [Fact]
    public void 不同使用者不互擋()
    {
        var manual = new ManualAiThrottle();
        for (var i = 0; i < 6; i++) Assert.True(manual.TryAcquire(1, T0));
        Assert.False(manual.TryAcquire(1, T0));
        Assert.True(manual.TryAcquire(2, T0));
    }

    [Fact]
    public void 時鐘前進60秒恢復()
    {
        var manual = new ManualAiThrottle();
        for (var i = 0; i < 6; i++) Assert.True(manual.TryAcquire(1, T0));
        Assert.False(manual.TryAcquire(1, T0.AddSeconds(59)));
        Assert.True(manual.TryAcquire(1, T0.AddSeconds(60)));
    }

    [Fact]
    public void RetryAfter秒數為最舊一筆離開視窗的剩餘秒數_至少1()
    {
        var manual = new ManualAiThrottle();
        for (var i = 0; i < 6; i++) Assert.True(manual.TryAcquire(1, T0.AddSeconds(i * 10)));

        Assert.False(manual.TryAcquire(1, T0.AddSeconds(50), out var retry));
        Assert.Equal(10, retry);   // 最舊一筆在 T0，T0+60 離開

        Assert.False(manual.TryAcquire(1, T0.AddSeconds(59.2), out var almost));
        Assert.Equal(1, almost);   // 剩 0.8 秒，進位成 1

        Assert.True(manual.TryAcquire(1, T0.AddSeconds(60), out var ok));
        Assert.Equal(0, ok);
    }

    /// <summary>依 cacheKey 快取、只在未命中時問 beforeCall 的替身（與 WebAiService 同語意）</summary>
    private sealed class CachingAi : IWebAiService
    {
        private readonly Dictionary<string, object> _cache = new();
        public bool Available => true;
        public int AiCalls { get; private set; }

        public Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class =>
            GenerateAsync<T>(cacheKey, systemPrompt, userPrompt, () => true);

        public Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt, Func<bool> beforeCall) where T : class
        {
            if (_cache.TryGetValue(cacheKey, out var hit)) return Task.FromResult((T?)hit);
            if (!beforeCall()) return Task.FromResult<T?>(null);
            AiCalls++;
            var r = System.Text.Json.JsonSerializer.Deserialize<T>("{\"text\":\"歸納\"}",
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            _cache[cacheKey] = r;
            return Task.FromResult<T?>(r);
        }

        public Task<string?> ChatOnceAsync(string systemPrompt, string userPrompt) => Task.FromResult<string?>("答");
    }

    private static List<IssueClusterDto> Clusters() =>
        new() { new IssueClusterDto { Source = "disk", EventId = 7, HostCount = 2, TotalCount = 9 } };

    [Fact]
    public async Task 快取命中不扣額_連打10次不被擋_只有第一次真的呼叫()
    {
        var ai = new CachingAi();
        var svc = new AiInsightService(ai);
        var gate = AiRateLimit.Gate(new ManualAiThrottle(), 1);

        for (var i = 0; i < 10; i++)
        {
            var result = await svc.SummarizeQueryAsync(Clusters(), "same", gate.BeforeCall);
            Assert.NotNull(result);
        }

        Assert.Equal(1, ai.AiCalls);
        Assert.False(gate.Rejected);
    }

    [Fact]
    public async Task 未命中的呼叫超限_不呼叫AI_並標記被拒與秒數()
    {
        var ai = new CachingAi();
        var svc = new AiInsightService(ai);
        var throttle = new ManualAiThrottle();
        for (var i = 0; i < 6; i++)
        {
            var g = AiRateLimit.Gate(throttle, 1);
            Assert.NotNull(await svc.SummarizeQueryAsync(Clusters(), "key" + i, g.BeforeCall));
        }

        var gate = AiRateLimit.Gate(throttle, 1);
        var blocked = await svc.SummarizeQueryAsync(Clusters(), "key-new", gate.BeforeCall);

        Assert.Null(blocked);
        Assert.True(gate.Rejected);
        Assert.InRange(gate.RetryAfter, 1, 60);
        Assert.Equal(6, ai.AiCalls);
    }

    [Fact]
    public void Reject_設429_rate_limited與RetryAfter標頭()
    {
        var http = new DefaultHttpContext();
        var body = AiRateLimit.Reject<AiTextDto?>(http.Response, 17);

        Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);
        Assert.Equal("17", http.Response.Headers["Retry-After"].ToString());
        Assert.False(body.Success);
        Assert.Equal(ApiErrorCodes.RateLimited, body.Error!.Code);
        Assert.Equal("AI 使用太頻繁，請約 17 秒後再試", body.Error.Message);
    }

    private static HelpController CreateHelp(FakeWebAi ai, long userId, ManualAiThrottle throttle, out DefaultHttpContext http)
    {
        var user = FakeCurrentUser.ForUser(userId, Capability.Maintain);
        var content = new HelpContentService();
        var qa = new HelpQaService(content, ai, user);
        http = new DefaultHttpContext();
        return new HelpController(content, qa, null!, user, throttle)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    [Fact]
    public async Task help_ask第7次回429_rate_limited_帶RetryAfter_其他使用者不受影響()
    {
        var ai = new FakeWebAi { Response = "答案" };
        var throttle = new ManualAiThrottle();
        var controller = CreateHelp(ai, 7, throttle, out var http);

        for (var i = 0; i < 6; i++)
        {
            var ok = await controller.Ask(new AskHelpRequest { Question = "如何新增主機" });
            Assert.True(ok.Success);
        }
        Assert.Equal(6, ai.Calls);

        var blocked = await controller.Ask(new AskHelpRequest { Question = "如何新增主機" });
        Assert.False(blocked.Success);
        Assert.Equal(ApiErrorCodes.RateLimited, blocked.Error!.Code);
        Assert.Equal(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);
        Assert.True(int.Parse(http.Response.Headers["Retry-After"].ToString()) >= 1);
        Assert.Equal(6, ai.Calls);

        var other = CreateHelp(ai, 8, throttle, out _);
        Assert.True((await other.Ask(new AskHelpRequest { Question = "如何新增主機" })).Success);
    }

    [Fact]
    public async Task help_ask驗證失敗與AI不可用不扣額()
    {
        var ai = new FakeWebAi { Response = "答案", Available = false };
        var throttle = new ManualAiThrottle();
        var controller = CreateHelp(ai, 7, throttle, out _);

        for (var i = 0; i < 10; i++)
        {
            var r = await controller.Ask(new AskHelpRequest { Question = "如何新增主機" });
            Assert.True(r.Success);
            Assert.Null(r.Data);
        }
        for (var i = 0; i < 10; i++)
            await Assert.ThrowsAsync<DomainException>(() => controller.Ask(new AskHelpRequest { Question = "" }));

        ai.Available = true;
        Assert.NotNull((await controller.Ask(new AskHelpRequest { Question = "如何新增主機" })).Data);
    }
}
