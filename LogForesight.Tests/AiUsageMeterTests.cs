using System.Net;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// AI token 用量計量（回饋二十七輪作業 B）：AIService 是唯一 HTTP 出口，計量掛在那裡。
/// </summary>
public class AiUsageMeterTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private AiUsageStore NewStore(Func<DateTime>? now = null) =>
        new(_fx.Blob(AiUsageStore.BlobKey), now);

    private static AiSettings Settings() => new()
    {
        BaseUrl = "http://localhost:8080",
        TimeoutSeconds = 5,
        RetryCount = 1,
        RetryDelaySeconds = 0,
        JsonRetryCount = 0
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<int, string> _body;
        public int Calls { get; private set; }

        public StubHandler(Func<int, string> body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = _body(++Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static string ReplyWithUsage(string content, int prompt, int completion) => $$"""
        {
          "choices": [ { "message": { "role": "assistant", "content": "{{content}}" } } ],
          "usage": { "prompt_tokens": {{prompt}}, "completion_tokens": {{completion}}, "total_tokens": {{prompt + completion}} }
        }
        """;

    private static string ReplyWithoutUsage(string content) => $$"""
        { "choices": [ { "message": { "role": "assistant", "content": "{{content}}" } } ] }
        """;

    [Fact]
    public async Task 呼叫成功時記錄token用量並累加()
    {
        var store = NewStore();
        var service = new AIService(Settings(), new StubHandler(_ => ReplyWithUsage("好", 120, 30)), null, store);

        await service.ChatAsync("第一次");
        await service.ChatAsync("第二次");

        var stats = store.Get();
        Assert.Equal(2, stats.Total.Calls);
        Assert.Equal(240, stats.Total.PromptTokens);
        Assert.Equal(60, stats.Total.CompletionTokens);
        Assert.Equal(300, stats.Total.TotalTokens);
        Assert.Equal(0, stats.Total.CallsWithoutUsage);

        var today = Assert.Single(stats.Days);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), today.Date);
        Assert.Equal(300, today.TotalTokens);
    }

    /// <summary>部分地端模型不回 usage：呼叫次數照計、token 記 0，並單獨計數讓畫面能解釋。</summary>
    [Fact]
    public async Task 回應未帶usage時次數照計token記零()
    {
        var store = NewStore();
        var service = new AIService(Settings(), new StubHandler(_ => ReplyWithoutUsage("好")), null, store);

        await service.ChatAsync("問題");

        var stats = store.Get();
        Assert.Equal(1, stats.Total.Calls);
        Assert.Equal(0, stats.Total.TotalTokens);
        Assert.Equal(1, stats.Total.CallsWithoutUsage);
    }

    /// <summary>空回應會觸發重試：每次實際發出的 HTTP 都燒了 token，都要進帳。</summary>
    [Fact]
    public async Task 重試的每次呼叫都計入()
    {
        var store = NewStore();
        var settings = Settings();
        settings.RetryCount = 2;
        var handler = new StubHandler(n => n == 1
            ? ReplyWithUsage("", 100, 0)          // 空回應 → 觸發重試
            : ReplyWithUsage("好", 100, 20));
        var service = new AIService(settings, handler, null, store);

        var response = await service.ChatAsync("問題");

        Assert.True(response.Success);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, store.Get().Total.Calls);
        Assert.Equal(220, store.Get().Total.TotalTokens);
    }

    [Fact]
    public void 跨日分列且累計不受裁切影響()
    {
        var day = new DateTime(2026, 8, 20, 9, 0, 0);
        var store = NewStore(() => day);
        store.Record(10, 5, 15, hasUsage: true);

        var nextDay = new DateTime(2026, 8, 21, 9, 0, 0);
        var storeNext = NewStore(() => nextDay);
        storeNext.Record(20, 10, 30, hasUsage: true);

        var stats = storeNext.Get();
        Assert.Equal(2, stats.Days.Count);
        Assert.Equal(45, stats.Total.TotalTokens);
        Assert.Equal("2026-08-20", stats.CountingSince);
        Assert.Equal(15, stats.Days.Single(d => d.Date == "2026-08-20").TotalTokens);
        Assert.Equal(30, stats.Days.Single(d => d.Date == "2026-08-21").TotalTokens);
    }

    /// <summary>每日列只留 RetainDays 天；累計是「自起算日以來」，不隨裁切變小。</summary>
    [Fact]
    public void 超過保留天數的每日列被裁掉但累計保留()
    {
        var start = new DateTime(2026, 1, 1, 9, 0, 0);
        var current = start;
        var store = NewStore(() => current);

        for (var i = 0; i < AiUsageStore.RetainDays + 10; i++)
        {
            current = start.AddDays(i);
            store.Record(1, 1, 2, hasUsage: true);
        }

        var stats = store.Get();
        Assert.True(stats.Days.Count <= AiUsageStore.RetainDays);
        Assert.Equal(AiUsageStore.RetainDays + 10, stats.Total.Calls);
        Assert.Equal((AiUsageStore.RetainDays + 10) * 2, stats.Total.TotalTokens);
        Assert.Equal("2026-01-01", stats.CountingSince);
    }

    [Fact]
    public void 清空重新計算歸零並重設起算日()
    {
        var now = new DateTime(2026, 8, 21, 9, 0, 0);
        var store = NewStore(() => now);
        store.Record(100, 50, 150, hasUsage: true);

        store.Reset();

        var stats = store.Get();
        Assert.Empty(stats.Days);
        Assert.Equal(0, stats.Total.Calls);
        Assert.Equal(0, stats.Total.TotalTokens);
        Assert.Equal("2026-08-21", stats.CountingSince);
    }

    [Fact]
    public void 近N天查詢只回區間內的每日列且新到舊()
    {
        var start = new DateTime(2026, 8, 1, 9, 0, 0);
        var current = start;
        var store = NewStore(() => current);

        for (var i = 0; i < 10; i++)
        {
            current = start.AddDays(i);
            store.Record(1, 1, 2, hasUsage: true);
        }

        var recent = store.RecentDays(3);   // current 停在 8/10
        Assert.Equal(new[] { "2026-08-10", "2026-08-09", "2026-08-08" }, recent.Select(d => d.Date));
    }

    /// <summary>統計寫入炸掉不得影響 AI 呼叫本身——統計不是主線功能。</summary>
    [Fact]
    public async Task 計量失敗不影響AI呼叫()
    {
        var service = new AIService(Settings(), new StubHandler(_ => ReplyWithUsage("好", 10, 5)),
            null, new ThrowingMeter());

        var response = await service.ChatAsync("問題");

        Assert.True(response.Success);
        Assert.Equal("好", response.Content);
    }

    private sealed class ThrowingMeter : IAiUsageMeter
    {
        public void Record(int promptTokens, int completionTokens, int totalTokens, bool hasUsage) =>
            throw new InvalidOperationException("統計壞了");
    }
}
