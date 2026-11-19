using System.Net;
using System.Text;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using Xunit;

namespace LogForesight.Tests;

public sealed class AIServiceRetryCountZeroTests
{
    [Fact]
    public void NegativeRetryCount_RemainsRejectedBeforeSendingHttp()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(SuccessResponse()));

        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(
            () => new AIService(Settings(retryCount: -1), handler, NullAiRequestGate.Instance));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RetryCountZero_AllowsSuccessfulChatThroughActualAiServiceConsumer()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(SuccessResponse()));
        var service = new AIService(Settings(retryCount: 0), handler, NullAiRequestGate.Instance);

        var response = await service.ChatAsync("status check");

        Assert.True(response.Success, response.Error);
        Assert.Equal("accepted", response.Content);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RetryCountZero_DoesNotRetryTransientHttpFailure()
    {
        var handler = new ScriptedHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("owned transient failure")));
        var service = new AIService(Settings(retryCount: 0), handler, NullAiRequestGate.Instance);

        var response = await service.ChatAsync("status check");

        Assert.False(response.Success);
        Assert.Contains("owned transient failure", response.Error ?? string.Empty);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PositiveRetryCount_RetriesTransientHttpFailureThenSucceeds()
    {
        var handler = new ScriptedHandler((attempt, _) => attempt == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("owned transient failure"))
            : Task.FromResult(SuccessResponse()));
        var service = new AIService(Settings(retryCount: 1), handler, NullAiRequestGate.Instance);

        var response = await service.ChatAsync("status check");

        Assert.True(response.Success, response.Error);
        Assert.Equal("accepted", response.Content);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RetryCountZero_PropagatesInFlightCallerCancellationWithoutRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SuccessResponse();
        });
        var service = new AIService(Settings(retryCount: 0), handler, NullAiRequestGate.Instance);
        using var cancellation = new CancellationTokenSource();
        var call = service.ChatAsync("status check", ct: cancellation.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>E4：回應超過整包讀取上限回失敗結果並說明、不重試（重試次數 3 也只打一次）；未超限正常（邊界兩側）。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 回應超過讀取上限_回失敗說明且不重試_未超限正常(bool oversize)
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"accepted\"}}],\"pad\":\"" + new string('x', oversize ? 8192 : 1) + "\"}",
                Encoding.UTF8, "application/json")
        }));
        var service = new AIService(Settings(retryCount: 3), handler, NullAiRequestGate.Instance, maxResponseBytes: 1024);

        var response = await service.ChatAsync("status check");

        Assert.Equal(1, handler.RequestCount);
        if (!oversize)
        {
            Assert.True(response.Success, response.Error);
            return;
        }
        Assert.False(response.Success);
        Assert.Contains("上限", response.Error ?? string.Empty);
        Assert.Contains("不會改變", response.Error ?? string.Empty);
    }

    private static AiSettings Settings(int retryCount) => new()
    {
        BaseUrl = "http://localhost:1",
        RetryCount = retryCount,
        RetryDelaySeconds = 1,
        TimeoutSeconds = 10,
        JsonRetryCount = 0
    };

    private static HttpResponseMessage SuccessResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"choices":[{"message":{"role":"assistant","content":"accepted"}}]}
            """, Encoding.UTF8, "application/json")
    };

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
