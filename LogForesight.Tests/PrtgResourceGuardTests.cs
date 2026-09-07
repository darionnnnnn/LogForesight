using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 資源守門閘門服務單元測試（批次F 階段3）。
/// </summary>
public sealed class PrtgResourceGuardTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } =
            (_, _) => throw new InvalidOperationException("測試未設定 OnSend");

        public List<string> RequestedUrls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (RequestedUrls)
            {
                RequestedUrls.Add(url);
            }
            return await OnSend(request, cancellationToken);
        }
    }

    private static (PrtgClient Client, StubHandler Handler) CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler
        {
            OnSend = (req, _) => Task.FromResult(responder(req))
        };
        var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        return (client, handler);
    }

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "")
        {
            lock (Lines) Lines.Add(message);
        }
    }

    private (BatchRunRecorder Recorder, BatchRunStore Store) CreateRecorder()
    {
        var store = new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs"));
        var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>());
        return (recorder, store);
    }

    private static PrtgResourceGuardTargetResult CreateTargets(params long[] objids)
    {
        var ids = objids.Length > 0 ? objids : new long[] { 1001 };
        var dict = ids.ToDictionary(id => id, _ => "cpu");
        return new PrtgResourceGuardTargetResult(ids, dict);
    }

    private static HttpResponseMessage SensorResponse(long objid, string status = "Up", string lastvalue = "50 %")
    {
        var json = $$"""
        {
            "sensors": [
                {
                    "objid": {{objid}},
                    "device": "SRV-TEST",
                    "sensor": "CPU Load",
                    "status": "{{status}}",
                    "lastvalue": "{{lastvalue}}"
                }
            ]
        }
        """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task 未啟用時NoOp_立刻返回且假Handler收到零次請求()
    {
        var (client, handler) = CreateClient(_ => SensorResponse(1001, "Up", "95 %"));
        var (recorder, _) = CreateRecorder();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = false,
            PrtgResourceGuardCpuPercent = 80
        };
        var guard = new PrtgResourceGuard(client, settings, CreateTargets(1001), recorder, console, null);

        await guard.WaitIfBusyAsync(CancellationToken.None);

        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task 未達Strikes不暫停_第一次讀到超標直接返回()
    {
        var (client, handler) = CreateClient(_ => SensorResponse(1001, "Up", "95 %"));
        var (recorder, store) = CreateRecorder();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardStrikes = 2,
            PrtgResourceGuardCpuPercent = 80,
            PrtgResourceGuardCheckSeconds = 0
        };
        var delays = new List<TimeSpan>();
        var guard = new PrtgResourceGuard(client, settings, CreateTargets(1001), recorder, console, null)
        {
            DelayAsync = (ts, _) => { delays.Add(ts); return Task.CompletedTask; }
        };

        await guard.WaitIfBusyAsync(CancellationToken.None);

        Assert.Single(handler.RequestedUrls);
        Assert.Empty(delays);
        var logs = store.GetLogs(recorder.RunId);
        Assert.DoesNotContain(logs, l => l.Message.Contains("進入暫停"));
    }

    [Fact]
    public async Task 連續達Strikes才暫停_進入暫停且回落後離開暫停斷言兩則Milestone()
    {
        var callCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            callCount++;
            return callCount <= 2
                ? SensorResponse(1001, "Up", "95 %")
                : SensorResponse(1001, "Up", "50 %");
        });
        var (recorder, store) = CreateRecorder();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardStrikes = 2,
            PrtgResourceGuardCpuPercent = 80,
            PrtgResourceGuardCheckSeconds = 0,
            PrtgResourceGuardPauseMinutes = 5
        };
        var delays = new List<TimeSpan>();
        var guard = new PrtgResourceGuard(client, settings, CreateTargets(1001), recorder, console, null)
        {
            DelayAsync = (ts, _) => { delays.Add(ts); return Task.CompletedTask; }
        };

        // 第 1 次超標：strikes=1，未達 2，放行
        await guard.WaitIfBusyAsync(CancellationToken.None);
        Assert.Empty(delays);

        // 第 2 次超標：strikes=2，達門檻進入暫停；等待一輪後第 3 次檢查回落，離開暫停返回
        await guard.WaitIfBusyAsync(CancellationToken.None);
        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromMinutes(5), delays[0]);

        var logs = store.GetLogs(recorder.RunId);
        Assert.Contains(logs, l => l.Message.Contains("進入暫停"));
        Assert.Contains(logs, l => l.Message.Contains("離開暫停"));
    }

    [Fact]
    public async Task 暫停中值回落即放行_第二次檢查值回落只等一輪就返回()
    {
        var callCount = 0;
        var (client, handler) = CreateClient(_ =>
        {
            callCount++;
            return callCount == 1
                ? SensorResponse(1001, "Up", "95 %")
                : SensorResponse(1001, "Up", "40 %");
        });
        var (recorder, _) = CreateRecorder();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardStrikes = 1,
            PrtgResourceGuardCpuPercent = 80,
            PrtgResourceGuardCheckSeconds = 0,
            PrtgResourceGuardPauseMinutes = 3
        };
        var delays = new List<TimeSpan>();
        var guard = new PrtgResourceGuard(client, settings, CreateTargets(1001), recorder, console, null)
        {
            DelayAsync = (ts, _) => { delays.Add(ts); return Task.CompletedTask; }
        };

        await guard.WaitIfBusyAsync(CancellationToken.None);

        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromMinutes(3), delays[0]);
        Assert.Equal(2, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task 讀值失敗放行且只警告一次_假Handler一律拋例外不中斷且Console只警告一次()
    {
        var handler = new StubHandler
        {
            OnSend = (_, _) => throw new HttpRequestException("PRTG API 連線中斷")
        };
        var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        var (recorder, _) = CreateRecorder();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardCheckSeconds = 0
        };
        var guard = new PrtgResourceGuard(client, settings, CreateTargets(1001), recorder, console, null);

        await guard.WaitIfBusyAsync(CancellationToken.None);
        await guard.WaitIfBusyAsync(CancellationToken.None);
        await guard.WaitIfBusyAsync(CancellationToken.None);

        var warnings = console.Lines.Where(l => l.Contains("[PRTG資源守門] 警告：")).ToList();
        Assert.Single(warnings);
    }

    [Fact]
    public async Task 取消在暫停中會拋出_暫停期間取消Token拋出OperationCanceledException()
    {
        var (client, _) = CreateClient(_ => SensorResponse(1001, "Up", "95 %"));
        var (recorder, _) = CreateRecorder();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardStrikes = 1,
            PrtgResourceGuardCpuPercent = 80,
            PrtgResourceGuardCheckSeconds = 0,
            PrtgResourceGuardPauseMinutes = 5
        };
        using var cts = new CancellationTokenSource();
        var guard = new PrtgResourceGuard(client, settings, CreateTargets(1001), recorder, console, null)
        {
            DelayAsync = (ts, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => guard.WaitIfBusyAsync(cts.Token));
    }

    [Fact]
    public async Task 累計暫停上限_超過上限後放行並警告且後續呼叫不再暫停()
    {
        var (client, handler) = CreateClient(_ => SensorResponse(1001, "Up", "95 %"));
        var (recorder, store) = CreateRecorder();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardStrikes = 1,
            PrtgResourceGuardCpuPercent = 80,
            PrtgResourceGuardCheckSeconds = 0,
            PrtgResourceGuardPauseMinutes = 5,
            PrtgResourceGuardMaxPauseMinutes = 5
        };
        var delays = new List<TimeSpan>();
        var guard = new PrtgResourceGuard(client, settings, CreateTargets(1001), recorder, console, null)
        {
            DelayAsync = (ts, _) => { delays.Add(ts); return Task.CompletedTask; }
        };

        // 第一次呼叫：超標進入暫停，等 5 分鐘後達到上限（5 >= 5），放行不再暫停
        await guard.WaitIfBusyAsync(CancellationToken.None);

        Assert.Single(delays);
        var logs = store.GetLogs(recorder.RunId);
        Assert.Contains(logs, l => l.Message.Contains("單趟累計暫停時間已達上限"));

        // 第二次呼叫：直接返回（不再暫停），且不再發送 Delay 或 API 請求
        var reqCountBefore = handler.RequestedUrls.Count;
        await guard.WaitIfBusyAsync(CancellationToken.None);
        Assert.Single(delays);
        Assert.Equal(reqCountBefore, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task 檢查間隔生效_未滿間隔連續呼叫兩次只發送一批請求()
    {
        var (client, handler) = CreateClient(_ => SensorResponse(1001, "Up", "50 %"));
        var (recorder, _) = CreateRecorder();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardCheckSeconds = 300,
            PrtgResourceGuardCpuPercent = 80
        };
        var guard = new PrtgResourceGuard(client, settings, CreateTargets(1001), recorder, console, null);

        await guard.WaitIfBusyAsync(CancellationToken.None);
        Assert.Single(handler.RequestedUrls);

        await guard.WaitIfBusyAsync(CancellationToken.None);
        Assert.Single(handler.RequestedUrls);
    }

    [Fact]
    public async Task 併發只讀一次_同時發5個WaitIfBusyAsync假Handler只收到一批請求()
    {
        var (client, handler) = CreateClient(_ => SensorResponse(1001, "Up", "50 %"));
        var (recorder, _) = CreateRecorder();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardCheckSeconds = 60,
            PrtgResourceGuardCpuPercent = 80
        };
        var guard = new PrtgResourceGuard(client, settings, CreateTargets(1001), recorder, console, null);

        var tasks = Enumerable.Range(0, 5).Select(_ => guard.WaitIfBusyAsync(CancellationToken.None));
        await Task.WhenAll(tasks);

        Assert.Single(handler.RequestedUrls);
    }

    private sealed class EmptySentinelStore : ISentinelStore
    {
        public List<Sentinel> GetAll() => new();
        public Sentinel? Get(long sentinelId) => null;
        public Sentinel? FindByName(string name) => null;
        public Sentinel Upsert(Sentinel sentinel) => sentinel;
        public void Delete(long sentinelId) { }
    }

    private static SystemSettings GuardSettings(bool enabled, string apiTokenEnc)
    {
        return new SystemSettings
        {
            PrtgResourceGuardEnabled = enabled,
            PrtgUrl = "https://prtg.example.com",
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiTokenEnc = apiTokenEnc
        };
    }

    /// <summary>
    /// 守門建構的故障隔離（體檢輪補）：`TryCreate` 是主流程唯一的建構入口，它任何一步擲例外
    /// 都會讓整趟夜間批次在啟動前就失敗——而守門的原則是「讀不到就放行」。
    /// 用一個帶密文前綴但內容損毀的 token 讓 `PrtgClientFactory.Create` 的解密擲例外，
    /// 斷言 TryCreate 回 null、不擲例外、且警告進了 Milestone 與 console。
    /// </summary>
    [Fact]
    public void TryCreate_密文損毀時回null不擲例外且有警告()
    {
        var (recorder, store) = CreateRecorder();
        using var _ = recorder;
        var console = new TestConsole();
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        var settings = GuardSettings(enabled: true, apiTokenEnc: "enc:v1:!!!not-base64!!!");

        var guard = PrtgResourceGuard.TryCreate(settings, prtgStore, new EmptySentinelStore(), recorder, console, progress: null);

        Assert.Null(guard);
        Assert.Contains(console.Lines, l => l.Contains("守門建構失敗"));
        Assert.Contains(store.GetLogs(recorder.RunId), l => l.Message.Contains("守門建構失敗"));
    }

    [Fact]
    public void TryCreate_未啟用時回null且零輸出()
    {
        var (recorder, _) = CreateRecorder();
        using var __ = recorder;
        var console = new TestConsole();
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        var settings = GuardSettings(enabled: false, apiTokenEnc: "enc:v1:!!!not-base64!!!");

        var guard = PrtgResourceGuard.TryCreate(settings, prtgStore, new EmptySentinelStore(), recorder, console, progress: null);

        Assert.Null(guard);
        Assert.Empty(console.Lines);
    }

    [Fact]
    public void TryCreate_認證齊備時回非null_且鏡像為空只警告不擲例外()
    {
        var (recorder, _) = CreateRecorder();
        using var __ = recorder;
        var console = new TestConsole();
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        // 明文 token：IsEncrypted 為 false 就不解密，Create 不會擲例外
        var settings = GuardSettings(enabled: true, apiTokenEnc: "plain-token");

        using var guard = PrtgResourceGuard.TryCreate(settings, prtgStore, new EmptySentinelStore(), recorder, console, progress: null);

        Assert.NotNull(guard);
        // 鏡像表為空、Sentinel 為空 → 自動偵測一個 sensor 都沒找到，只警告
        Assert.NotEmpty(console.Lines);
    }

    [Fact]
    public void TryCreate_啟用但位址未設定時回null且有警告()
    {
        var (recorder, store) = CreateRecorder();
        using var _ = recorder;
        var console = new TestConsole();
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        var settings = GuardSettings(enabled: true, apiTokenEnc: "plain-token");
        settings.PrtgUrl = "";

        var guard = PrtgResourceGuard.TryCreate(settings, prtgStore, new EmptySentinelStore(), recorder, console, progress: null);

        Assert.Null(guard);
        // 使用者明確勾了守門卻沒給位址：不能無聲跳過
        Assert.Contains(console.Lines, l => l.Contains("位址或認證未設定"));
        Assert.Contains(store.GetLogs(recorder.RunId), l => l.Message.Contains("位址或認證未設定"));
    }
}
