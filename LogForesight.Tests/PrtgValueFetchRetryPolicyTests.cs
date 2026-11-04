using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 63-2 輪批次 H-1：數值取數的暫停閘、補抓與停損的純邏輯（政策數字與暫停閘機制）。
/// 與 PRTG 連線無關，全部以注入的延遲函式與時鐘驅動，不真的等待。
/// </summary>
public class PrtgValueFetchRetryPolicyTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void 暫停序列依序15_60_180_第4次起維持180()
    {
        var policy = new PrtgValueFetchRetryPolicy(() => 0);

        Assert.Equal(new[] { S(15), S(60), S(180), S(180), S(180) },
            Enumerable.Range(0, 5).Select(_ => policy.NextDelay(null)).ToArray());
    }

    [Fact]
    public void 誤差上限為百分之二十_亂數取滿時各階段多二成()
    {
        var policy = new PrtgValueFetchRetryPolicy(() => 1.0);

        Assert.Equal(new[] { S(18), S(72), S(216) },
            Enumerable.Range(0, 3).Select(_ => policy.NextDelay(null)).ToArray());
    }

    [Fact]
    public void 預設亂數來源的暫停落在序列值到一點二倍之間()
    {
        for (var i = 0; i < 200; i++)
        {
            var delay = new PrtgValueFetchRetryPolicy().NextDelay(null);
            Assert.InRange(delay, S(15), S(18));
        }
    }

    [Fact]
    public void 成功一顆後序列回到15()
    {
        var policy = new PrtgValueFetchRetryPolicy(() => 0);
        Assert.Equal(S(15), policy.NextDelay(null));
        Assert.Equal(S(60), policy.NextDelay(null));

        policy.RecordSuccess();

        Assert.Equal(S(15), policy.NextDelay(null));
    }

    [Theory]
    [InlineData(120, 120)] // Retry-After 較大：取它
    [InlineData(5, 15)]    // Retry-After 較小：不縮短序列值
    public void RetryAfter與序列值取較大者(double retryAfterSeconds, double expectedSeconds)
    {
        var policy = new PrtgValueFetchRetryPolicy(() => 0);

        Assert.Equal(S(expectedSeconds), policy.NextDelay(S(retryAfterSeconds)));
    }

    [Fact]
    public void 連續可重試失敗滿10顆才達停損門檻_成功一顆歸零()
    {
        var policy = new PrtgValueFetchRetryPolicy(() => 0);
        for (var i = 0; i < 9; i++) policy.RecordRetryableFailure();
        Assert.False(policy.ConsecutiveLimitReached);

        policy.RecordSuccess();
        Assert.Equal(0, policy.ConsecutiveFailures);

        for (var i = 0; i < 10; i++) policy.RecordRetryableFailure();
        Assert.True(policy.ConsecutiveLimitReached);
    }

    [Fact]
    public void 總暫停加上下一次達15分鐘就停損()
    {
        var policy = new PrtgValueFetchRetryPolicy(() => 0);

        Assert.False(policy.WouldReachTotalPauseLimit(S(0), S(899)));
        Assert.True(policy.WouldReachTotalPauseLimit(S(0), S(900)));
        Assert.False(policy.WouldReachTotalPauseLimit(S(600), S(299)));
        Assert.True(policy.WouldReachTotalPauseLimit(S(600), S(300)));
    }

    [Fact]
    public void 常數與規劃一致()
    {
        Assert.Equal(new[] { S(15), S(60), S(180) }, PrtgValueFetchRetryPolicy.PauseSequence);
        Assert.Equal(10, PrtgValueFetchRetryPolicy.MaxConsecutiveFailures);
        Assert.Equal(TimeSpan.FromMinutes(15), PrtgValueFetchRetryPolicy.MaxTotalPause);
        Assert.Equal(TimeSpan.FromMinutes(10), PrtgValueFetchRetryPolicy.Cooldown);
        Assert.Equal(0.2, PrtgValueFetchRetryPolicy.JitterFraction);
    }

    [Theory]
    [InlineData(PrtgFailureKind.Timeout, null, true)]
    [InlineData(PrtgFailureKind.Transport, null, true)]
    [InlineData(PrtgFailureKind.HttpStatus, 429, true)]
    [InlineData(PrtgFailureKind.HttpStatus, 500, true)]
    [InlineData(PrtgFailureKind.HttpStatus, 503, true)]
    [InlineData(PrtgFailureKind.HttpStatus, 404, false)]
    [InlineData(PrtgFailureKind.HttpStatus, 403, false)]
    [InlineData(PrtgFailureKind.HttpStatus, 401, false)]
    [InlineData(PrtgFailureKind.Credential, 401, false)]
    [InlineData(PrtgFailureKind.Other, null, false)]
    public void ShouldRetry與表格分頁的可重試判定一致(PrtgFailureKind kind, int? status, bool expected)
    {
        var ex = new PrtgClientException("x", kind: kind, statusCode: status);

        Assert.Equal(expected, new PrtgValueFetchRetryPolicy().ShouldRetry(ex));
        Assert.Equal(PrtgTablePager.IsRetryable(ex), new PrtgValueFetchRetryPolicy().ShouldRetry(ex));
    }

    // ───────── PauseGate ─────────

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    [Fact]
    public async Task 閘沒有拉起時WaitAsync立即完成()
    {
        var gate = new PauseGate((_, _) => Task.CompletedTask);

        Assert.False(gate.IsRaised);
        await gate.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 拉起後等待者在延遲完成前不放行_完成後放行()
    {
        var release = new TaskCompletionSource();
        var gate = new PauseGate((_, ct) => release.Task.WaitAsync(ct));

        _ = gate.RaiseAsync(S(15), CancellationToken.None);
        var waiter = gate.WaitAsync(CancellationToken.None);

        Assert.True(gate.IsRaised);
        Assert.False(waiter.IsCompleted);

        release.SetResult();
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsRaised);
    }

    [Fact]
    public async Task 閘已拉起時再拉起不重新計時也不再呼叫延遲函式()
    {
        var calls = new List<TimeSpan>();
        var release = new TaskCompletionSource();
        var gate = new PauseGate((span, ct) => { calls.Add(span); return release.Task.WaitAsync(ct); });

        var first = gate.RaiseAsync(S(15), CancellationToken.None);
        var second = gate.RaiseAsync(S(180), CancellationToken.None);

        Assert.Equal(new[] { S(15) }, calls);
        Assert.Same(first, second);
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        // 放行後再拉起是新的一次暫停
        _ = gate.RaiseAsync(S(60), CancellationToken.None);
        Assert.Equal(new[] { S(15), S(60) }, calls);
    }

    [Fact]
    public async Task Reset立即放行等待者並中止延遲()
    {
        var delayToken = CancellationToken.None;
        var gate = new PauseGate((_, ct) => { delayToken = ct; return Task.Delay(Timeout.Infinite, ct); });

        _ = gate.RaiseAsync(S(180), CancellationToken.None);
        var waiter = gate.WaitAsync(CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        gate.Reset();

        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsRaised);
        Assert.True(delayToken.IsCancellationRequested);
    }

    [Fact]
    public async Task 等待中取消擲OperationCanceledException()
    {
        var gate = new PauseGate((_, ct) => Task.Delay(Timeout.Infinite, ct));
        _ = gate.RaiseAsync(S(180), CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiter = gate.WaitAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }

    [Fact]
    public async Task 呼叫端令牌取消時延遲被中止_閘視為結束而不卡住後來的等待者()
    {
        using var cts = new CancellationTokenSource();
        var gate = new PauseGate((_, ct) => Task.Delay(Timeout.Infinite, ct));
        var raised = gate.RaiseAsync(S(180), cts.Token);

        cts.Cancel();

        await raised.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsRaised);
    }

    [Fact]
    public async Task 總暫停以時鐘量實際長度_不是排定長度()
    {
        var clock = new ManualClock();
        var gate = new PauseGate((span, _) => { clock.Advance(span); return Task.CompletedTask; }, clock);

        await gate.RaiseAsync(S(15), CancellationToken.None);
        await gate.RaiseAsync(S(60), CancellationToken.None);
        Assert.Equal(S(75), gate.TotalPaused);

        // 延遲函式不推進時鐘（零等待的測試替身）＝沒有實際暫停
        var idle = new PauseGate((_, _) => Task.CompletedTask, clock);
        await idle.RaiseAsync(S(180), CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, idle.TotalPaused);
    }
}

/// <summary>
/// 第 63-2 輪批次 H-1：<c>FetchValuesAsync</c> 的暫停閘、補抓、停損與冷卻（經 <c>FetchValuesForSensorsAsync</c> 進入）。
/// 以真的 PrtgClient 搭假 HttpMessageHandler 驅動，故障按 sensor objid 與第幾次嘗試排程；
/// 暫停以注入的延遲函式觀察與控制，順序一律比對事件佇列，不用牆鐘。
/// </summary>
public sealed class PrtgValueFetchRetryServiceTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 8, 30);
    private const string OkJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":5,\"coverage\":100}]}";

    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // ───────── 測試替身 ─────────

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    private sealed class TestConsole : IRunConsole
    {
        private readonly object _lock = new();
        private readonly List<string> _lines = new();
        public void WriteLine(string message = "") { lock (_lock) _lines.Add(message); }
        public List<string> Lines { get { lock (_lock) return _lines.ToList(); } }
    }

    private sealed class BudgetClock : IPrtgClock
    {
        private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public TimeSpan Elapsed => TimeSpan.FromTicks(_watch.Elapsed.Ticks * 1000);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(TimeSpan.FromTicks(Math.Max(1, delay.Ticks / 1000)), cancellationToken);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class Rig
    {
        /// <summary>依序的事件：send:{objid}、pause-start:{秒}、pause-end、guard-wait。</summary>
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentDictionary<long, int> Attempts { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        public ManualClock Clock { get; } = new();
        public TestConsole Console { get; } = new();
        public Func<long, int, HttpResponseMessage> Respond { get; set; } = (_, _) => Ok();
        /// <summary>回應前先等的非同步關卡（讓兩條並行工作的請求同時在途）；null＝不等。</summary>
        public Func<long, int, Task>? BeforeRespond { get; set; }
        public int HistoricCalls;
        public PrtgFetchService Service { get; set; } = null!;
        public PrtgFreshnessStore Freshness { get; set; } = null!;
        public EfPrtgStore Store { get; set; } = null!;
        public PrtgClient Client { get; set; } = null!;
        public List<string> EventList => Events.ToList();
        public int Count(string prefix) => Events.Count(e => e.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(OkJson, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Status(HttpStatusCode code, Action<HttpResponseMessage>? decorate = null)
    {
        var resp = new HttpResponseMessage(code) { Content = new StringContent("err", Encoding.UTF8, "text/plain") };
        decorate?.Invoke(resp);
        return resp;
    }

    private static HttpResponseMessage TimedOut() => throw new TaskCanceledException("逾時", new TimeoutException());

    /// <param name="pauseBody">暫停期間的行為；null＝立即結束。</param>
    /// <param name="advanceClockOnPause">暫停時把假時鐘推進相同長度（模擬真的睡了那麼久，總暫停停損用）。</param>
    private Rig NewRig(Func<long, int, HttpResponseMessage>? respond = null, double jitter = 0,
        Func<TimeSpan, CancellationToken, Task>? pauseBody = null, bool advanceClockOnPause = false,
        Func<LfDbContext>? contextFactory = null, PrtgResourceGuard? guard = null)
    {
        var rig = new Rig();
        if (respond != null) rig.Respond = respond;

        var handler = new StubHandler(async req =>
        {
            var id = long.Parse(Regex.Match(req.RequestUri!.ToString(), @"id=(\d+)").Groups[1].Value);
            var attempt = rig.Attempts.AddOrUpdate(id, 1, (_, n) => n + 1);
            Interlocked.Increment(ref rig.HistoricCalls);
            rig.Events.Enqueue($"send:{id}");
            if (rig.BeforeRespond != null) await rig.BeforeRespond(id, attempt);
            return rig.Respond(id, attempt);
        });
        rig.Client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler, PrtgAuthModes.Token, "", "", "",
            new PrtgRequestBudget(new BudgetClock()), rig.Clock);
        rig.Store = new EfPrtgStore(contextFactory ?? _fx.NewContext);
        rig.Freshness = new PrtgFreshnessStore(new EfJsonBlobStore(_fx.NewContext, PrtgFreshnessStore.BlobKey));
        rig.Service = new PrtgFetchService(rig.Client, rig.Store, rig.Freshness, rig.Console,
            new Dictionary<string, string>(), guard)
        {
            ValueFetchJitter = () => jitter,
            TimeSource = rig.Clock,
            RetryDelay = async (span, ct) =>
            {
                rig.Events.Enqueue($"pause-start:{(int)span.TotalSeconds}");
                lock (rig.Delays) rig.Delays.Add(span);
                if (advanceClockOnPause) rig.Clock.Advance(span);
                if (pauseBody != null) await pauseBody(span, ct);
                rig.Events.Enqueue("pause-end");
            }
        };
        return rig;
    }

    private static long[] Ids(int count, int first = 1) => Enumerable.Range(first, count).Select(i => (long)i).ToArray();

    private static async Task<(int Written, int Failed)> RunAsync(Rig rig, long[] ids, int concurrency = 1,
        Action<string, int, int>? progress = null, CancellationToken ct = default) =>
        await rig.Service.FetchValuesForSensorsAsync(Day, ids, concurrency, ct, progress);

    /// <summary>擷取 PrtgFetchService 的 NLog 輸出（層級|訊息）。其他測試類可能同時寫入，斷言一律用日期與關鍵字篩。</summary>
    private static IDisposable CaptureLogs(out NLog.Targets.MemoryTarget target)
    {
        target = new NLog.Targets.MemoryTarget("lf-value-retry-" + Guid.NewGuid().ToString("N")) { Layout = "${level}|${message}" };
        var config = NLog.LogManager.Configuration ??= new NLog.Config.LoggingConfiguration();
        config.AddTarget(target);
        config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target, typeof(PrtgFetchService).FullName!);
        NLog.LogManager.ReconfigExistingLoggers();
        var captured = target;
        return new Cleanup(() =>
        {
            config.RemoveTarget(captured.Name);
            config.LoggingRules.Where(r => r.Targets.Contains(captured)).ToList().ForEach(r => config.LoggingRules.Remove(r));
            NLog.LogManager.ReconfigExistingLoggers();
        });
    }

    private sealed class Cleanup(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    // ───────── 暫停閘與補抓 ─────────

    [Fact]
    public async Task 第3顆逾時一次_暫停15秒_其餘顆在閘放行後才送_補抓成功而全數寫入()
    {
        using var _ = CaptureLogs(out var logs);
        // 暫停期間睡一小段真實時間，讓「沒有閘就會搶著送」的實作在這個窗口內露餡；通過與否只看事件順序
        var rig = NewRig((id, attempt) => id == 3 && attempt == 1 ? TimedOut() : Ok(),
            pauseBody: (_, ct) => Task.Delay(40, ct));

        var (written, failed) = await RunAsync(rig, Ids(10));

        Assert.Equal((10, 0), (written, failed));
        Assert.Equal(new[] { S(15) }, rig.Delays);
        Assert.Equal(2, rig.Attempts[3]);

        var events = rig.EventList;
        var pauseStart = events.IndexOf("pause-start:15");
        var pauseEnd = events.IndexOf("pause-end");
        Assert.True(pauseStart >= 0 && pauseEnd > pauseStart, string.Join(",", events));
        // 暫停開始到放行之間，一顆都不能送出
        Assert.DoesNotContain(events.Skip(pauseStart + 1).Take(pauseEnd - pauseStart - 1), e => e.StartsWith("send:"));
        // 其餘 7 顆（4～10）都在放行之後；補抓的第 3 顆排在第一輪全部送完之後
        Assert.Equal(new[] { "send:4", "send:5", "send:6", "send:7", "send:8", "send:9", "send:10", "send:3" },
            events.Skip(pauseEnd + 1).Where(e => e.StartsWith("send:")).ToArray());

        using var db = _fx.NewContext();
        Assert.Equal(10, db.PrtgValues.Count());
        Assert.DoesNotContain(rig.Console.Lines, l => l.Contains("數值擷取失敗"));

        // 暫停與補抓只寫 Info
        var mine = logs.Logs.Where(l => l.Contains("2026-08-30")).ToList();
        Assert.Contains(mine, l => l.StartsWith("Info|") && l.Contains("暫停 15 秒"));
        Assert.Contains(mine, l => l.StartsWith("Info|") && l.Contains("逐顆補抓"));
        Assert.DoesNotContain(mine, l => l.StartsWith("Warn|") || l.StartsWith("Error|"));
    }

    [Fact]
    public async Task 連續三顆逾時暫停依序15_60_180_中間成功一顆後再失敗回到15()
    {
        var rig = NewRig((id, attempt) => attempt == 1 && id is 1 or 2 or 3 or 5 ? TimedOut() : Ok());

        var (written, failed) = await RunAsync(rig, Ids(5));

        Assert.Equal(new[] { S(15), S(60), S(180), S(15) }, rig.Delays);
        Assert.Equal((5, 0), (written, failed));
    }

    [Fact]
    public async Task 暫停長度帶誤差_亂數取滿時多兩成()
    {
        var rig = NewRig((id, attempt) => id == 1 && attempt == 1 ? TimedOut() : Ok(), jitter: 1.0);

        await RunAsync(rig, Ids(2));

        Assert.Equal(new[] { S(18) }, rig.Delays);
    }

    [Theory]
    [InlineData(120, 120)]
    [InlineData(5, 15)]
    public async Task RetryAfter秒數與序列值取較大者(int retryAfterSeconds, int expectedSeconds)
    {
        var rig = NewRig((id, attempt) => id == 1 && attempt == 1
            ? Status(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(S(retryAfterSeconds)))
            : Ok());

        var (written, failed) = await RunAsync(rig, Ids(2));

        Assert.Equal(new[] { S(expectedSeconds) }, rig.Delays);
        Assert.Equal((2, 0), (written, failed));
    }

    [Fact]
    public async Task RetryAfter為日期格式也換算得出()
    {
        var rig = NewRig();
        var when = rig.Clock.GetUtcNow().AddSeconds(90);
        rig.Respond = (id, attempt) => id == 1 && attempt == 1
            ? Status(HttpStatusCode.ServiceUnavailable, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(when))
            : Ok();

        var (written, failed) = await RunAsync(rig, Ids(2));

        Assert.Equal(new[] { S(90) }, rig.Delays);
        Assert.Equal((2, 0), (written, failed));
    }

    [Fact]
    public async Task 其他4xx不暫停不補抓_直接計失敗()
    {
        var rig = NewRig((id, _) => id switch
        {
            1 => Status(HttpStatusCode.NotFound),
            2 => Status(HttpStatusCode.Forbidden),
            _ => Ok()
        });

        var (written, failed) = await RunAsync(rig, Ids(3));

        Assert.Equal((1, 2), (written, failed));
        Assert.Empty(rig.Delays);
        Assert.Equal(1, rig.Attempts[1]);
        Assert.Equal(1, rig.Attempts[2]);
        Assert.Contains(rig.Console.Lines, l => l.Contains("數值擷取失敗 2 顆（其中逾時 0、補抓後仍失敗 0）"));
    }

    [Fact]
    public async Task 補抓仍失敗才計入失敗_彙總行帶逾時與補抓後仍失敗數()
    {
        var rig = NewRig((id, _) => id switch
        {
            1 => TimedOut(),                     // 永遠逾時：補抓後仍失敗，且是逾時
            2 => Status(HttpStatusCode.InternalServerError), // 永遠 500：補抓後仍失敗，不是逾時
            3 => Status(HttpStatusCode.NotFound), // 不可重試：不補抓
            _ => Ok()
        });

        var (written, failed) = await RunAsync(rig, Ids(4));

        Assert.Equal((1, 3), (written, failed));
        Assert.Equal(2, rig.Attempts[1]);
        Assert.Equal(2, rig.Attempts[2]);
        Assert.Equal(1, rig.Attempts[3]);
        Assert.Contains(rig.Console.Lines, l => l.Contains("數值擷取失敗 3 顆（其中逾時 1、補抓後仍失敗 2）"));
    }

    [Fact]
    public async Task 寫入失敗不是PRTG的問題_不暫停不補抓()
    {
        var failWrites = false;
        var rig = NewRig(contextFactory: () => failWrites ? throw new InvalidOperationException("磁碟已滿") : _fx.NewContext());
        failWrites = true;

        var (written, failed) = await RunAsync(rig, Ids(2));

        Assert.Equal((0, 2), (written, failed));
        Assert.Empty(rig.Delays);
        Assert.Equal(1, rig.Attempts[1]);
        Assert.Equal(1, rig.Attempts[2]);
        Assert.Contains(rig.Console.Lines, l => l.Contains("首則錯誤") && l.Contains("磁碟已滿"));
    }

    [Fact]
    public async Task 兩條並行工作同時失敗時暫停不疊加_只拉起一次()
    {
        // 兩顆同時在途、都逾時：第一個失敗的拉起 15 秒，第二個發現閘已拉起就不再疊加（序列也不前進）
        var arrivedCount = 0;
        var bothInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = NewRig((id, attempt) => attempt == 1 && id is 1 or 2 ? TimedOut() : Ok(),
            pauseBody: (_, ct) => Task.Delay(60, ct));
        rig.BeforeRespond = async (id, attempt) =>
        {
            if (attempt != 1 || id is not (1 or 2)) return;
            if (Interlocked.Increment(ref arrivedCount) == 2) bothInFlight.SetResult();
            await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        var (written, failed) = await RunAsync(rig, Ids(4), concurrency: 2);

        Assert.Equal(new[] { S(15) }, rig.Delays);
        Assert.Equal((4, 0), (written, failed));
    }

    [Fact]
    public async Task 閘期間的第二個失敗不讓序列前進_下一次暫停仍是60秒()
    {
        // 第 1、2 顆在同一個閘內失敗，只算序列的一格；第 3 顆再失敗是第二格（60），疊加的話會變 180
        var arrivedCount = 0;
        var bothInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = NewRig((id, attempt) => attempt == 1 ? TimedOut() : Ok(),
            pauseBody: (_, ct) => Task.Delay(60, ct));
        rig.BeforeRespond = async (id, attempt) =>
        {
            if (attempt != 1 || id is not (1 or 2)) return;
            if (Interlocked.Increment(ref arrivedCount) == 2) bothInFlight.SetResult();
            await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        var (written, failed) = await RunAsync(rig, Ids(3), concurrency: 2);

        Assert.Equal(new[] { S(15), S(60) }, rig.Delays);
        Assert.Equal((3, 0), (written, failed));
    }

    // ───────── 停損與冷卻 ─────────

    [Fact]
    public async Task 連續10顆可重試失敗停損_第11顆起不送請求_不擲例外()
    {
        using var _ = CaptureLogs(out var logs);
        var rig = NewRig((_, _) => TimedOut());

        var (written, failed) = await RunAsync(rig, Ids(15));

        Assert.Equal(10, rig.HistoricCalls);
        Assert.Equal((0, 15), (written, failed));
        Assert.All(rig.Attempts.Values, n => Assert.Equal(1, n)); // 停損後不補抓
        Assert.Equal(9, rig.Delays.Count); // 第 10 顆失敗直接停損，不再暫停
        Assert.Equal(new[] { S(15), S(60), S(180) }, rig.Delays.Take(3));

        var stopLines = rig.Console.Lines.Where(l => l.Contains("停止數值取數")).ToList();
        Assert.Single(stopLines);
        Assert.Contains("已停止 5 顆", stopLines[0]);
        Assert.Contains(rig.Console.Lines, l => l.Contains("數值擷取失敗 15 顆（其中逾時 10、補抓後仍失敗 0）"));

        var warns = logs.Logs.Where(l => l.StartsWith("Warn|") && l.Contains("2026-08-30") && l.Contains("數值取數停損")).ToList();
        Assert.Single(warns);
        Assert.Contains("連續 10 顆", warns[0]);
    }

    [Fact]
    public async Task 總暫停達15分鐘停損_以注入時鐘量實際暫停()
    {
        // 第 1、3 顆各被要求等 600 秒（假時鐘真的推進 600 秒）：第 3 顆失敗時 600＋600＞=900，停損
        var rig = NewRig((id, attempt) => id is 1 or 3
            ? Status(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(S(600)))
            : Ok(), advanceClockOnPause: true);

        var (written, failed) = await RunAsync(rig, Ids(5));

        Assert.Equal(3, rig.HistoricCalls); // 第 1、2、3 顆；第 4、5 顆不送，也沒有補抓
        Assert.Equal(new[] { S(600) }, rig.Delays);
        Assert.Equal((1, 4), (written, failed));
        Assert.Contains(rig.Console.Lines, l => l.Contains("停止數值取數（已停止 2 顆）"));
    }

    [Fact]
    public async Task 排定的暫停再長也不算總暫停_要時鐘真的走過才算()
    {
        // 同樣兩個 600 秒的要求，但延遲函式是零等待（時鐘不動）：總暫停量到 0，不會停損，兩顆都補抓成功
        var rig = NewRig((id, attempt) => id is 1 or 3 && attempt == 1
            ? Status(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(S(600)))
            : Ok());

        var (written, failed) = await RunAsync(rig, Ids(5));

        Assert.Equal((5, 0), (written, failed));
        Assert.Equal(new[] { S(600), S(600) }, rig.Delays);
        Assert.DoesNotContain(rig.Console.Lines, l => l.Contains("停止數值取數"));
    }

    [Fact]
    public async Task 停損後冷卻_立即再呼叫不送請求且只出聲一次_十分鐘後恢復()
    {
        var rig = NewRig((id, _) => id <= 15 ? TimedOut() : Ok());
        await RunAsync(rig, Ids(15));
        Assert.Equal(10, rig.HistoricCalls);
        var linesAfterStop = rig.Console.Lines.Count;

        // 冷卻期內：不送任何請求、回全部失敗、輸出只多一行
        var second = await RunAsync(rig, Ids(2, first: 101));
        Assert.Equal((0, 2), second);
        Assert.Equal(10, rig.HistoricCalls);
        Assert.Equal(linesAfterStop + 1, rig.Console.Lines.Count);
        Assert.Contains("冷卻", rig.Console.Lines[^1]);

        // 之後的回絕靜默
        var third = await RunAsync(rig, Ids(3, first: 201));
        Assert.Equal((0, 3), third);
        Assert.Equal(10, rig.HistoricCalls);
        Assert.Equal(linesAfterStop + 1, rig.Console.Lines.Count);

        // 9 分 59 秒仍在冷卻；超過 10 分鐘照常送出（序列與計數歸零，新的 FetchValuesAsync 從頭算）
        rig.Clock.Advance(TimeSpan.FromMinutes(9) + S(59));
        Assert.Equal((0, 1), await RunAsync(rig, Ids(1, first: 301)));
        Assert.Equal(10, rig.HistoricCalls);

        rig.Clock.Advance(S(2));
        var resumed = await RunAsync(rig, Ids(2, first: 401));
        Assert.Equal((2, 0), resumed);
        Assert.Equal(12, rig.HistoricCalls);
    }

    [Fact]
    public async Task 進度以第一輪顆數為總數_補抓不推進_最後done等於total()
    {
        var rig = NewRig((id, attempt) => id == 2 && attempt == 1 ? TimedOut() : Ok());
        var reports = new List<(int Done, int Total)>();

        var (written, failed) = await RunAsync(rig, Ids(6), progress: (_, done, total) => { lock (reports) reports.Add((done, total)); });

        Assert.Equal((6, 0), (written, failed));
        Assert.Equal(2, rig.Attempts[2]); // 有補抓
        Assert.All(reports, r => Assert.Equal(6, r.Total));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, reports.Select(r => r.Done).ToArray());
        Assert.Equal((6, 6), reports[^1]);
    }

    [Fact]
    public async Task 停損時進度仍走到done等於total_冷卻回絕也是()
    {
        var rig = NewRig((_, _) => TimedOut());
        var first = new List<(int Done, int Total)>();
        await RunAsync(rig, Ids(12), progress: (_, d, t) => { lock (first) first.Add((d, t)); });
        Assert.Equal((12, 12), first[^1]);
        Assert.All(first, r => Assert.True(r.Done <= r.Total));

        var rejected = new List<(int Done, int Total)>();
        await RunAsync(rig, Ids(3, first: 101), progress: (_, d, t) => rejected.Add((d, t)));
        Assert.Equal((3, 3), rejected[^1]);
    }

    // ───────── 取消 ─────────

    [Fact]
    public async Task 暫停期間取消令牌生效_不再送出()
    {
        using var cts = new CancellationTokenSource();
        var paused = new TaskCompletionSource();
        var rig = NewRig((id, _) => id == 1 ? TimedOut() : Ok(),
            pauseBody: (_, ct) => { paused.TrySetResult(); return Task.Delay(System.Threading.Timeout.Infinite, ct); });

        var run = RunAsync(rig, Ids(5), ct: cts.Token);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, rig.HistoricCalls); // 只送出過第 1 顆
        Assert.Equal(0, rig.Count("pause-end")); // 暫停是被取消的，不是睡完的
    }

    // ───────── 守門順序 ─────────

    [Fact]
    public async Task 守門在閘放行後才呼叫()
    {
        // 守門第 2 次檢查才忙碌（第 1 顆正常放行；第 2 顆要先等暫停閘，放行後才輪到守門，守門再等一次才送出）
        var guardCalls = 0;
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardStrikes = 1,
            PrtgResourceGuardCpuPercent = 80,
            PrtgResourceGuardCheckSeconds = 0,
            PrtgResourceGuardPauseMinutes = 5
        };
        var guardHandler = new StubHandler(_ =>
        {
            var busy = Interlocked.Increment(ref guardCalls) == 2;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"sensors\":[{{\"objid\":9001,\"device\":\"PRTG\",\"sensor\":\"CPU Load\",\"status\":\"Up\",\"lastvalue\":\"{(busy ? "95 %" : "50 %")}\"}}]}}",
                    Encoding.UTF8, "application/json")
            });
        });
        var guardClient = new PrtgClient("https://prtg.example.com", "token123", 30, true, guardHandler, PrtgAuthModes.Token, "", "", "",
            new PrtgRequestBudget(new BudgetClock()));
        var recorder = new BatchRunRecorder(new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")), "test-host", Array.Empty<string>());
        Rig? created = null;
        using var guard = new PrtgResourceGuard(guardClient, settings,
            new PrtgResourceGuardTargetResult(new long[] { 9001 }, new Dictionary<long, string> { [9001] = "cpu" }),
            recorder, new TestConsole(), null)
        {
            DelayAsync = (_, _) => { created!.Events.Enqueue("guard-wait"); return Task.CompletedTask; }
        };
        var rig = NewRig((id, attempt) => id == 1 && attempt == 1 ? TimedOut() : Ok(),
            pauseBody: (_, ct) => Task.Delay(40, ct), guard: guard);
        created = rig;

        var (written, failed) = await RunAsync(rig, Ids(2));

        Assert.Equal((2, 0), (written, failed));
        var events = rig.EventList;
        var pauseEnd = events.IndexOf("pause-end");
        var guardWait = events.IndexOf("guard-wait");
        Assert.True(pauseEnd >= 0 && guardWait > pauseEnd, string.Join(",", events));
        Assert.True(events.IndexOf("send:2") > guardWait, string.Join(",", events));
    }

    // ───────── 觸發式取數：三輪輪詢只有第一輪累計到停損 ─────────

    [Fact]
    public async Task 觸發式三輪輪詢總呼叫數為10_冷卻擋住第二三輪_全部失敗不記新鮮度()
    {
        var rig = NewRig((_, _) => TimedOut());
        var store = rig.Store;
        var records = new EfAnalysisRecordStore(_fx.NewContext, "test");
        DailyAnalysisRecord Rec(long host) => new() { HostId = host, Host = $"SRV-{host}", Date = Day, RiskLevel = "高" };

        // 三台主機各一台裝置；第 1 台 12 顆 sensor（第一輪累計到停損），第 2、3 台各 3 顆（之後輪詢才觸發）
        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "D1", Ip = "10.0.0.1" },
            new() { Objid = 1002, Name = "D2", Ip = "10.0.0.2" },
            new() { Objid = 1003, Name = "D3", Ip = "10.0.0.3" }
        }, Day);
        var sensors = new List<PrtgSensorRow>();
        sensors.AddRange(Enumerable.Range(1, 12).Select(i => new PrtgSensorRow { Objid = 2000 + i, DeviceObjid = 1001, Name = $"A{i}", SensorType = "SNMP CPU Load" }));
        sensors.AddRange(Enumerable.Range(1, 3).Select(i => new PrtgSensorRow { Objid = 3000 + i, DeviceObjid = 1002, Name = $"B{i}", SensorType = "SNMP CPU Load" }));
        sensors.AddRange(Enumerable.Range(1, 3).Select(i => new PrtgSensorRow { Objid = 4000 + i, DeviceObjid = 1003, Name = $"C{i}", SensorType = "SNMP CPU Load" }));
        store.UpsertSensors(sensors, Day);
        store.ReplaceHostMapForDate(Day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Ok },
            new() { DeviceObjid = 1002, HostId = 102, MapStatus = PrtgMapStatus.Ok },
            new() { DeviceObjid = 1003, HostId = 103, MapStatus = PrtgMapStatus.Ok }
        });
        records.Append(Rec(101));
        rig.Freshness.Record(PrtgFreshnessStore.Values, 5);

        var polls = 0;
        var fetcher = new PrtgTriggeredValueFetcher(rig.Service, store, records, rig.Console);
        var result = await fetcher.RunAsync(Day, whitelist: null, concurrency: 1,
            analysisCompleted: () =>
            {
                // 每輪結束後多一台主機變成高風險：第 2、3 輪各有新目標，但冷卻擋住，不再送請求
                polls++;
                if (polls == 1) records.Append(Rec(102));
                if (polls == 2) records.Append(Rec(103));
                return polls >= 3;
            },
            ct: CancellationToken.None, pollSeconds: 1);

        Assert.Equal(10, rig.HistoricCalls);
        Assert.Equal(3, result.TriggerHosts);
        Assert.Equal(18, result.TargetSensors);
        Assert.Equal(0, result.ValuesWritten);
        Assert.Equal(18, result.FailedSensors);
        // 停損只出聲一次、冷卻回絕也只出聲一次（第 2、3 輪共用同一次冷卻通知）
        Assert.Single(rig.Console.Lines, l => l.Contains("停止數值取數"));
        Assert.Single(rig.Console.Lines, l => l.Contains("冷卻"));
        // 有目標卻全部失敗不算成功完成：新鮮度維持原樣（ZeroStreak 不累）
        var entry = rig.Freshness.GetAll()[PrtgFreshnessStore.Values];
        Assert.Equal(5, entry.LastCount);
        Assert.Equal(0, entry.ZeroStreak);
    }
}
