using System.Net;
using System.Text;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// ChatJsonAsync 的重試邊界（D4 契約 4）：傳輸層失敗（HTTP 錯誤、逾時）使 ChatAsync 回傳
/// Success=false 後必須立即結束、不得再進 JSON 重試，否則逾時會被放大成 JsonRetryCount+1 倍；
/// 只有「有回應但 JSON 不合格」才重試。
/// 測試一律把 RetryCount 設為 0，讓 Polly 的網路層重試不干擾計數，只量 JSON 層的額外請求。
/// </summary>
public sealed class AIServiceChatJsonTransportTests
{
    [Fact]
    public async Task ChatJson_HttpErrorFromTransport_DoesNotRetryJsonLayer()
    {
        var handler = new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var service = new AIService(Settings(jsonRetryCount: 2), handler, NullAiRequestGate.Instance);

        var result = await service.ChatJsonAsync<Probe>("status check");

        Assert.False(result.Success);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ChatJson_TimeoutFromTransport_DoesNotRetryJsonLayer()
    {
        var handler = new ScriptedHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout", new TimeoutException())));
        var service = new AIService(Settings(jsonRetryCount: 2), handler, NullAiRequestGate.Instance);

        var result = await service.ChatJsonAsync<Probe>("status check");

        Assert.False(result.Success);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ChatJson_ResponseWithInvalidJson_StillRetriesJsonLayer()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(ContentResponse("這不是 JSON")));
        var service = new AIService(Settings(jsonRetryCount: 2), handler, NullAiRequestGate.Instance);

        var result = await service.ChatJsonAsync<Probe>("status check");

        Assert.False(result.Success);
        Assert.Equal(3, handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatJson_InvalidJsonThenTransportFailure_RawContentIsEmpty(bool timeout)
    {
        // 第 1 次回不合格 JSON、第 2 次傳輸失敗：前一次的不合格回覆不能帶出去，
        // 否則呼叫端會走「原文保留」降級並標成 AI 已完成
        var handler = new ScriptedHandler((attempt, _) => attempt == 1
            ? Task.FromResult(ContentResponse("這不是 JSON"))
            : timeout
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout", new TimeoutException()))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var service = new AIService(Settings(jsonRetryCount: 2), handler, NullAiRequestGate.Instance);

        var result = await service.ChatJsonAsync<Probe>("status check");

        Assert.False(result.Success);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(string.Empty, result.RawContent);
    }

    private static AiSettings Settings(int jsonRetryCount) => new()
    {
        BaseUrl = "http://localhost:1",
        RetryCount = 0,
        RetryDelaySeconds = 1,
        TimeoutSeconds = 10,
        JsonRetryCount = jsonRetryCount
    };

    private static HttpResponseMessage ContentResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"}}]}",
            Encoding.UTF8, "application/json")
    };

    private sealed class Probe
    {
        public string? Name { get; set; }
    }

    private sealed class ScriptedHandler(
        Func<int, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = ++RequestCount;
            return response(attempt, cancellationToken);
        }
    }
}
