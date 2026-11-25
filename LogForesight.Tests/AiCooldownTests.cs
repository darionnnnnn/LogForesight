using System.Net;
using System.Net.Http;
using System.Text;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 批次 D（A1／CU-4）：AI 網路層失敗後互動入口冷卻 60 秒。冷卻掛在全站 <see cref="IAiRequestGate"/> 上：
/// 失敗後 60 秒內互動請求不送、第 61 秒才送；成功即解除；背景車道不受影響；逾時算失敗、使用者取消不算。
/// </summary>
public sealed class AiCooldownTests
{
    private static AiSettings Settings() => new()
    {
        BaseUrl = "http://localhost:1",
        RetryCount = 0,
        TimeoutSeconds = 10,
        JsonRetryCount = 0
    };

    private static AiRequestGate NewGate(FakeTimeProvider clock) =>
        new(() => 2, timeProvider: clock);

    private static AIService Service(HttpMessageHandler handler, IAiRequestGate gate, AiLane lane) =>
        new(Settings(), handler, gate, lane: lane);

    // ── 閘道層：時間邊界 ──────────────────────────────────────────

    [Fact]
    public void 互動失敗後60秒內冷卻_第61秒解除()
    {
        var clock = new FakeTimeProvider();
        var gate = NewGate(clock);
        Assert.False(gate.InteractiveCoolingDown);

        gate.ReportNetworkFailure(AiLane.Interactive);
        Assert.True(gate.InteractiveCoolingDown);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(gate.InteractiveCoolingDown, "第 59 秒仍在冷卻");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(gate.InteractiveCoolingDown, "滿 60 秒解除（邊界：第 60 秒起可送）");
    }

    [Fact]
    public void 成功一次即解除冷卻()
    {
        var clock = new FakeTimeProvider();
        var gate = NewGate(clock);
        gate.ReportNetworkFailure(AiLane.Interactive);
        Assert.True(gate.InteractiveCoolingDown);

        gate.ReportSuccess();

        Assert.False(gate.InteractiveCoolingDown);
    }

    [Fact]
    public void 背景失敗不開冷卻()
    {
        var gate = NewGate(new FakeTimeProvider());
        gate.ReportNetworkFailure(AiLane.Background);
        Assert.False(gate.InteractiveCoolingDown);
    }

    [Fact]
    public void 冷卻中再失敗只往後延_不縮短()
    {
        var clock = new FakeTimeProvider();
        var gate = NewGate(clock);
        gate.ReportNetworkFailure(AiLane.Interactive);
        clock.Advance(TimeSpan.FromSeconds(40));
        gate.ReportNetworkFailure(AiLane.Interactive);   // 到期時刻延到第 100 秒

        clock.Advance(TimeSpan.FromSeconds(59));         // 第 99 秒
        Assert.True(gate.InteractiveCoolingDown);
        clock.Advance(TimeSpan.FromSeconds(1));          // 第 100 秒
        Assert.False(gate.InteractiveCoolingDown);
    }

    // ── AIService：失敗分類與不送請求 ────────────────────────────

    [Fact]
    public async Task 連線失敗進冷卻_冷卻期間互動請求不送_第61秒再送()
    {
        var clock = new FakeTimeProvider();
        var gate = NewGate(clock);
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("connection refused"));
        var service = Service(handler, gate, AiLane.Interactive);

        var first = await service.ChatAsync("q");
        Assert.False(first.Success);
        Assert.Equal(1, handler.Requests);
        Assert.True(gate.InteractiveCoolingDown);

        clock.Advance(TimeSpan.FromSeconds(59));
        var cooled = await service.ChatAsync("q");
        Assert.False(cooled.Success);
        Assert.Equal(AiRequestGate.CooldownMessage, cooled.Error);
        Assert.Equal(1, handler.Requests);   // 沒有送請求

        clock.Advance(TimeSpan.FromSeconds(1));   // 第 60 秒：冷卻到期，再試一次真的送
        await service.ChatAsync("q");
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task 成功一次解除冷卻()
    {
        var clock = new FakeTimeProvider();
        var gate = NewGate(clock);
        var fail = true;
        var handler = new ScriptedHandler(_ => fail ? throw new HttpRequestException("down") : Ok());
        var service = Service(handler, gate, AiLane.Interactive);
        await service.ChatAsync("q");
        Assert.True(gate.InteractiveCoolingDown);

        // 背景請求成功（背景車道不看冷卻）證明 AI 通了：互動立即恢復
        fail = false;
        var background = Service(handler, gate, AiLane.Background);
        Assert.True((await background.ChatAsync("q")).Success);
        Assert.False(gate.InteractiveCoolingDown);
        Assert.True((await service.ChatAsync("q")).Success);
    }

    [Fact]
    public async Task 背景路徑不受冷卻影響_照常送請求()
    {
        var clock = new FakeTimeProvider();
        var gate = NewGate(clock);
        gate.ReportNetworkFailure(AiLane.Interactive);
        var handler = new ScriptedHandler(_ => Ok());
        var background = Service(handler, gate, AiLane.Background);

        var response = await background.ChatAsync("q");

        Assert.True(response.Success);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task HttpClient逾時算失敗進冷卻()
    {
        var clock = new FakeTimeProvider();
        var gate = NewGate(clock);
        // 與 HttpClient.Timeout 逾時同形：TaskCanceledException 內含 TimeoutException，呼叫端的 ct 沒有取消
        var handler = new ScriptedHandler(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        var service = Service(handler, gate, AiLane.Interactive);

        var response = await service.ChatAsync("q");

        Assert.False(response.Success);
        Assert.True(gate.InteractiveCoolingDown);
    }

    [Fact]
    public async Task 使用者取消不算失敗_不進冷卻()
    {
        var clock = new FakeTimeProvider();
        var gate = NewGate(clock);
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Ok();
        });
        var service = Service(handler, gate, AiLane.Interactive);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ChatAsync("q", ct: cts.Token));

        Assert.False(gate.InteractiveCoolingDown);
    }

    [Fact]
    public async Task 非網路層失敗不進冷卻_400與空回應()
    {
        var clock = new FakeTimeProvider();
        var gate = NewGate(clock);
        var badRequest = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        await Service(badRequest, gate, AiLane.Interactive).ChatAsync("q");
        Assert.False(gate.InteractiveCoolingDown, "400（prompt 太長等）不是連不上");

        var empty = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":""}}]}""", Encoding.UTF8, "application/json")
        });
        await Service(empty, gate, AiLane.Interactive).ChatAsync("q");
        Assert.False(gate.InteractiveCoolingDown, "空回應不是連不上");
    }

    [Fact]
    public async Task 伺服器5xx算網路層失敗()
    {
        var gate = NewGate(new FakeTimeProvider());
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Service(handler, gate, AiLane.Interactive).ChatAsync("q");
        Assert.True(gate.InteractiveCoolingDown);
    }

    [Fact]
    public async Task 一個失敗後其餘並發請求走冷卻_不各送一次()
    {
        var clock = new FakeTimeProvider();
        var gate = NewGate(clock);
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("down"));
        // 各自一個 AIService 實例（正式站台互動與對話兩個實例）；冷卻是全站閘道的，不是實例的
        var services = Enumerable.Range(0, 11).Select(_ => Service(handler, gate, AiLane.Interactive)).ToList();

        var first = await services[0].ChatAsync("q");
        var rest = await Task.WhenAll(services.Skip(1).Select(s => s.ChatAsync("q")));

        Assert.False(first.Success);
        Assert.All(rest, r => Assert.Equal(AiRequestGate.CooldownMessage, r.Error));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void NullAiRequestGate沒有冷卻_舊測試替身行為不變()
    {
        IAiRequestGate gate = NullAiRequestGate.Instance;
        gate.ReportNetworkFailure(AiLane.Interactive);
        Assert.False(gate.InteractiveCoolingDown);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""", Encoding.UTF8, "application/json")
    };

    private sealed class ScriptedHandler(Func<CancellationToken, HttpResponseMessage> script) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(script(cancellationToken));
        }
    }
}
