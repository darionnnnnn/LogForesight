using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using System.Reflection;
using LogForesight.Web.Configuration;
using LogForesight.Web.Extensions;
using LogForesight.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests;

/// <summary>全站 AI 閘道（docs/WEB-SPEC.md §9.10）：互動優先、背景上限隨設定、取消不洩漏許可。</summary>
public sealed class AiRequestGateTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task 背景已達上限時互動照樣立即取得()
    {
        var gate = new AiRequestGate(() => 2);
        using var b1 = await gate.EnterAsync(AiLane.Background, CancellationToken.None);
        using var b2 = await gate.EnterAsync(AiLane.Background, CancellationToken.None);

        var interactive = gate.EnterAsync(AiLane.Interactive, CancellationToken.None);

        Assert.True(interactive.IsCompletedSuccessfully);
        (await interactive).Dispose();
    }

    [Fact]
    public async Task 互動在途時背景不開新請求_互動結束後放行()
    {
        var gate = new AiRequestGate(() => 4);
        var interactive = await gate.EnterAsync(AiLane.Interactive, CancellationToken.None);

        var background = gate.EnterAsync(AiLane.Background, CancellationToken.None);
        await Task.Delay(Settle);
        Assert.False(background.IsCompleted);

        interactive.Dispose();
        (await background.WaitAsync(Generous)).Dispose();
    }

    [Fact]
    public async Task 背景讓路有上限_互動一直在途也會放行()
    {
        var gate = new AiRequestGate(() => 1, backgroundMaxYield: TimeSpan.FromMilliseconds(200));
        using var interactive = await gate.EnterAsync(AiLane.Interactive, CancellationToken.None);

        var lease = await gate.EnterAsync(AiLane.Background, CancellationToken.None).WaitAsync(Generous);

        lease.Dispose();
    }

    [Fact]
    public async Task 互動排隊逾上限擲TimeoutException()
    {
        var gate = new AiRequestGate(() => 1, interactiveMaxWait: TimeSpan.FromMilliseconds(200));
        using var i1 = await gate.EnterAsync(AiLane.Interactive, CancellationToken.None);
        using var i2 = await gate.EnterAsync(AiLane.Interactive, CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => gate.EnterAsync(AiLane.Interactive, CancellationToken.None).WaitAsync(Generous));
    }

    [Fact]
    public async Task 等待中取消會擲出且不佔許可()
    {
        var gate = new AiRequestGate(() => 1);
        var held = await gate.EnterAsync(AiLane.Background, CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiting = gate.EnterAsync(AiLane.Background, cts.Token);
        await Task.Delay(Settle);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Generous));

        held.Dispose();
        // 上限 1：取消的那一筆若殘留許可，這裡會一直等不到
        (await gate.EnterAsync(AiLane.Background, CancellationToken.None).WaitAsync(Generous)).Dispose();
    }

    [Fact]
    public async Task 背景上限改設定後等待中的請求重讀生效()
    {
        var limit = 1;
        var gate = new AiRequestGate(() => Volatile.Read(ref limit));
        using var held = await gate.EnterAsync(AiLane.Background, CancellationToken.None);

        var waiting = gate.EnterAsync(AiLane.Background, CancellationToken.None);
        await Task.Delay(Settle);
        Assert.False(waiting.IsCompleted);

        Volatile.Write(ref limit, 2);
        (await waiting.WaitAsync(Generous)).Dispose();
    }

    [Fact]
    public async Task AIService經閘道走自己的車道_呼叫完歸還許可()
    {
        var gate = new RecordingGate();
        var handler = new OkHandler();
        var service = new AIService(Settings(), handler, gate: gate, lane: AiLane.Interactive);

        var response = await service.ChatAsync("ping");

        Assert.True(response.Success, response.Error);
        Assert.Equal(new[] { AiLane.Interactive }, gate.Lanes);
        Assert.Equal(1, gate.Released);
    }

    [Fact]
    public async Task 互動排不到時AIService回失敗且不發出請求()
    {
        var gate = new AiRequestGate(() => 1, interactiveMaxWait: TimeSpan.FromMilliseconds(100));
        using var i1 = await gate.EnterAsync(AiLane.Interactive, CancellationToken.None);
        using var i2 = await gate.EnterAsync(AiLane.Interactive, CancellationToken.None);
        var handler = new OkHandler();
        var service = new AIService(Settings(), handler, gate: gate, lane: AiLane.Interactive);

        var response = await service.ChatAsync("ping");

        Assert.False(response.Success);
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>
    /// 體檢輪：閘道在實例佇列之後取，排隊上限只套在閘道上等於沒限——同一個互動實例的第二個問題
    /// 會在佇列裡等到第一個答完。上限要涵蓋實例佇列：排不到就降級、不發請求。
    /// </summary>
    [Fact]
    public async Task 互動車道在實例佇列排隊逾上限也降級不發請求()
    {
        var gate = new AiRequestGate(() => 1, interactiveMaxWait: TimeSpan.FromMilliseconds(200));
        var handler = new SlowHandler(TimeSpan.FromMilliseconds(900));
        var service = new AIService(Settings(), handler, gate: gate, lane: AiLane.Interactive);

        var first = service.ChatAsync("first");
        await Task.Delay(50);
        var second = await service.ChatAsync("second");
        var firstResponse = await first;

        Assert.True(firstResponse.Success, firstResponse.Error);
        Assert.False(second.Success);
        Assert.Contains("排隊超過", second.Error);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task 背景車道的實例佇列不設上限()
    {
        var gate = new AiRequestGate(() => 1, interactiveMaxWait: TimeSpan.FromMilliseconds(200));
        var handler = new SlowHandler(TimeSpan.FromMilliseconds(400));
        var service = new AIService(Settings(), handler, gate: gate, lane: AiLane.Background);

        var first = service.ChatAsync("first");
        await Task.Delay(50);
        var second = await service.ChatAsync("second");

        Assert.True((await first).Success);
        Assert.True(second.Success, second.Error);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public void 正式程式碼每個AIService建構都帶全站閘道()
    {
        var root = FindRepoRoot();
        var sources = new[] { "LogForesight.Core", "LogForesight.Web" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        var calls = new List<string>();
        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, @"new AIService\("))
            {
                var end = text.IndexOf(';', match.Index);
                var call = text[match.Index..end];
                calls.Add($"{Path.GetFileName(path)}: {(call.Contains("gate:") ? "ok" : "缺 gate")}");
            }
        }

        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.EndsWith("ok", call));
    }

    [Fact]
    public void 正式DI解析的三個AI消費端拿到全站閘道而非不節流替身()
    {
        var settings = new WebAppSettings();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddStorage(settings);
        services.AddLogForesightServices();
        using var provider = services.BuildServiceProvider();

        var siteGate = provider.GetRequiredService<IAiRequestGate>();
        Assert.IsNotType<NullAiRequestGate>(siteGate);

        foreach (var type in new[]
                 {
                     typeof(AiAnalysisHostedService), typeof(HostDayWorkflowRecoveryHostedService), typeof(WebAiService)
                 })
        {
            var instance = provider.GetRequiredService(type == typeof(WebAiService) ? typeof(IWebAiService) : type);
            var gateField = GateFieldOf(type);
            var held = gateField.GetValue(instance);
            Assert.NotNull(held);
            Assert.IsNotType<NullAiRequestGate>(held);
            Assert.Same(siteGate, held);
        }
    }

    [Theory]
    [InlineData(typeof(AIService))]
    [InlineData(typeof(AiAnalysisHostedService))]
    [InlineData(typeof(HostDayWorkflowRecoveryHostedService))]
    [InlineData(typeof(WebAiService))]
    [InlineData(typeof(LogForesight.Core.Service.AnalysisOrchestrator))]
    public void 所有建構式的閘道參數必填且非nullable(Type type)
    {
        var context = new NullabilityInfoContext();
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotEmpty(ctors);
        foreach (var ctor in ctors)
        {
            var gate = ctor.GetParameters().Single(p => p.ParameterType == typeof(IAiRequestGate));
            Assert.False(gate.HasDefaultValue, $"{type.Name} 的 gate 參數不得有預設值");
            Assert.NotEqual(NullabilityState.Nullable, context.Create(gate).WriteState);
        }
    }

    private static FieldInfo GateFieldOf(Type type) =>
        type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(f => f.FieldType == typeof(IAiRequestGate));

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static AiSettings Settings() => new()
    {
        BaseUrl = "http://localhost:1",
        RetryCount = 0,
        TimeoutSeconds = 10,
        JsonRetryCount = 0
    };

    private sealed class RecordingGate : IAiRequestGate
    {
        public List<AiLane> Lanes { get; } = new();
        public int Released;

        public TimeSpan InteractiveMaxWait => Timeout.InfiniteTimeSpan;

        public Task<IDisposable> EnterAsync(AiLane lane, CancellationToken ct)
        {
            Lanes.Add(lane);
            return Task.FromResult<IDisposable>(new Release(this));
        }

        private sealed class Release(RecordingGate owner) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref owner.Released);
        }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(Ok());
        }
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        public int Requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            await Task.Delay(delay, cancellationToken);
            return Ok();
        }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""",
            Encoding.UTF8, "application/json")
    };
}
