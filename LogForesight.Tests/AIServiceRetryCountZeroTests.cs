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
            () => new AIService(Settings(retryCount: -1), handler));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RetryCountZero_AllowsSuccessfulChatThroughActualAiServiceConsumer()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(SuccessResponse()));
        var service = new AIService(Settings(retryCount: 0), handler);

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
        var service = new AIService(Settings(retryCount: 0), handler);

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
        var service = new AIService(Settings(retryCount: 1), handler);

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
        var service = new AIService(Settings(retryCount: 0), handler);
        using var cancellation = new CancellationTokenSource();
        var call = service.ChatAsync("status check", ct: cancellation.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, handler.RequestCount);
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
