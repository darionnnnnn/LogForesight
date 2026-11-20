using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Tests.TestDoubles;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 同日續補與「不同步結構」取數路徑：取數目標必須與「當前」取數範圍取交集（docs/PRTG-SPEC.md §3c），
/// 且續補兩個階段各自隔離失敗（取消除外）。以 PRTG 用戶端替身記錄所有請求。
/// </summary>
public class PrtgRefillScopeIntersectionTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") { lock (Lines) Lines.Add(message); }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (RequestedUrls) RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static long? IdQuery(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]id=(\d+)(&|$)");
        return m.Success ? long.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>每個 id 回一顆感測器（objid＝100＋id）、狀態變更回空。</summary>
    private static HttpResponseMessage Healthy(HttpRequestMessage req)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("content=messages")) return Json("{\"messages\":[]}");
        if (url.Contains("historicdata")) return Json("{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":7,\"coverage\":100}]}");
        var id = IdQuery(url);
        return Json("{\"treesize\":1,\"sensors\":[{\"objid\":" + (100 + id) + ",\"parentid\":" + id + ",\"sensor\":\"S\",\"type\":\"ping\",\"paused\":false}]}");
    }

    private (PrtgFetchService Service, RecordingHandler Handler, EfPrtgStore Store) Create(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RecordingHandler(responder);
        var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler, PrtgAuthModes.Token, "", "", "",
            new PrtgRequestBudget(new AcceleratedPrtgClock()));
        var store = new EfPrtgStore(_fx.NewContext);
        var service = new PrtgFetchService(client, store, new PrtgFreshnessStore(new EfJsonBlobStore(_fx.NewContext, PrtgFreshnessStore.BlobKey)),
            new TestConsole(), new Dictionary<string, string>());
        return (service, handler, store);
    }

    private static IEnumerable<long> Ids(RecordingHandler handler, string kind)
    {
        lock (handler.RequestedUrls)
            return handler.RequestedUrls.Where(u => u.Contains(kind)).Select(u => IdQuery(u)!.Value).OrderBy(i => i).ToList();
    }

    // ── J3：續補與當前範圍取交集 ──

    [Fact]
    public async Task 續補_範圍縮小後範圍外的裝置不發任何請求_也不再列為待補()
    {
        var (service, handler, _) = Create(Healthy);

        // 上一趟留下感測器待補 1、2、3，狀態變更待補 2、3、4；現在範圍只剩 1、2
        var left = await service.FetchPendingDevicesAsync(new long[] { 1, 2, 3 }, new long[] { 2, 3, 4 },
            DateTime.Today.AddDays(-1), 1, CancellationToken.None, new long[] { 1, 2 });

        Assert.Equal(new long[] { 1, 2 }, Ids(handler, "content=sensors"));
        Assert.Equal(new long[] { 2 }, Ids(handler, "content=messages"));
        // 範圍外的 3、4 不發任何請求（不論是裝置自己或其 sensor）
        Assert.DoesNotContain(handler.RequestedUrls, u => IdQuery(u) is 3 or 4 or 103 or 104);
        Assert.Empty(left.Sensors);
        Assert.Empty(left.Messages);
        Assert.Empty(left.StageFailures);
    }

    [Fact]
    public async Task 續補_待補裝置都還在範圍內時全部照補()
    {
        var (service, handler, _) = Create(Healthy);

        await service.FetchPendingDevicesAsync(new long[] { 1, 2 }, new long[] { 2, 3 },
            DateTime.Today.AddDays(-1), 1, CancellationToken.None, new long[] { 1, 2, 3, 99 });

        Assert.Equal(new long[] { 1, 2 }, Ids(handler, "content=sensors"));
        Assert.Equal(new long[] { 2, 3 }, Ids(handler, "content=messages"));
    }

    [Fact]
    public async Task 續補_範圍為空時一個請求都不發()
    {
        var (service, handler, _) = Create(Healthy);

        var left = await service.FetchPendingDevicesAsync(new long[] { 1, 2 }, new long[] { 1, 2 },
            DateTime.Today.AddDays(-1), 1, CancellationToken.None, Array.Empty<long>());

        Assert.Empty(handler.RequestedUrls);
        Assert.Empty(left.Sensors);
        Assert.Empty(left.Messages);
    }

    [Fact]
    public async Task 不同步結構取數_鏡像裡範圍外裝置的感測器不發請求()
    {
        var (service, handler, store) = Create(Healthy);
        var syncedAt = new DateTime(2026, 8, 29, 1, 0, 0);
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 901, DeviceObjid = 1, Name = "範圍內", SensorType = "ping" },
            new() { Objid = 902, DeviceObjid = 9, Name = "範圍外", SensorType = "ping" }
        }, syncedAt);

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None,
            _ => new PrtgScopeResult(new HashSet<long> { 1 }, 1, 0, 0, 0), syncStructure: false);

        Assert.Equal(new long[] { 901 }, Ids(handler, "historicdata"));
        Assert.DoesNotContain(handler.RequestedUrls, u => IdQuery(u) is 9 or 902);
        Assert.Equal(0, result.Failures);
    }

    [Fact]
    public async Task 不同步結構取數_範圍計算失敗時不取任何數值_範圍內外都一樣()
    {
        var (service, handler, store) = Create(Healthy);
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 901, DeviceObjid = 1, Name = "A", SensorType = "ping" }
        }, new DateTime(2026, 8, 29, 1, 0, 0));

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None,
            _ => throw new InvalidOperationException("範圍壞了"), syncStructure: false);

        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("historicdata"));
        Assert.True(result.Failures > 0);
    }

    // ── J5：續補逐階段失敗隔離 ──

    [Fact]
    public async Task 續補_感測器階段擲例外時該階段維持待補_狀態變更階段照常執行()
    {
        var (service, handler, _) = Create(Healthy);
        // 感測器階段一開始（進度 0）就擲例外：模擬階段本體非預期失敗
        void Progress(string phase, int done, int total)
        {
            if (phase == PrtgFetchService.PrtgSyncSensorsPhase && done == 0) throw new InvalidOperationException("感測器階段壞了");
        }

        var left = await service.FetchPendingDevicesAsync(new long[] { 1, 2 }, new long[] { 1, 2 },
            DateTime.Today.AddDays(-1), 1, CancellationToken.None, new long[] { 1, 2 }, Progress);

        Assert.Equal(new long[] { 1, 2 }, left.Sensors.OrderBy(i => i));
        Assert.Empty(left.Messages);
        Assert.Single(left.StageFailures);
        Assert.Contains("感測器補取失敗", left.StageFailures[0]);
        // 例外原文（含標記）只進記錄檔，不進維護主控台與執行里程碑
        Assert.DoesNotContain("感測器階段壞了", left.StageFailures[0]);
        Assert.Contains("InvalidOperationException", left.StageFailures[0]);
        Assert.Equal(new long[] { 1, 2 }, Ids(handler, "content=messages"));
    }

    [Fact]
    public async Task 續補_狀態變更階段擲例外時該階段維持待補_感測器階段已完成不受影響()
    {
        var (service, handler, _) = Create(Healthy);
        void Progress(string phase, int done, int total)
        {
            if (phase == PrtgFetchService.PrtgSyncMessagesPhase && done == 0) throw new InvalidOperationException("狀態變更階段壞了");
        }

        var left = await service.FetchPendingDevicesAsync(new long[] { 1 }, new long[] { 1, 2 },
            DateTime.Today.AddDays(-1), 1, CancellationToken.None, new long[] { 1, 2 }, Progress);

        Assert.Empty(left.Sensors);
        Assert.Equal(new long[] { 1, 2 }, left.Messages.OrderBy(i => i));
        Assert.Contains("狀態變更補取失敗", Assert.Single(left.StageFailures));
        Assert.DoesNotContain("狀態變更階段壞了", left.StageFailures[0]);
        Assert.Equal(new long[] { 1 }, Ids(handler, "content=sensors"));
    }

    [Fact]
    public async Task 續補_使用者取消照常往外拋_不被當成階段失敗吞掉()
    {
        var (service, _, _) = Create(Healthy);
        using var cts = new CancellationTokenSource();
        void Progress(string phase, int done, int total)
        {
            if (phase == PrtgFetchService.PrtgSyncSensorsPhase && done == 0)
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchPendingDevicesAsync(
            new long[] { 1 }, new long[] { 1 }, DateTime.Today.AddDays(-1), 1, cts.Token, new long[] { 1 }, Progress));
    }

    [Fact]
    public async Task 續補_逾時的TaskCanceledException但取消訊號未觸發_算階段失敗而非取消()
    {
        var (service, _, _) = Create(Healthy);
        void Progress(string phase, int done, int total)
        {
            // HttpClient 逾時擲的就是這種例外：它是 OCE，但使用者沒有取消
            if (phase == PrtgFetchService.PrtgSyncSensorsPhase && done == 0) throw new TaskCanceledException("逾時");
        }

        var left = await service.FetchPendingDevicesAsync(new long[] { 1 }, new long[] { 1 },
            DateTime.Today.AddDays(-1), 1, CancellationToken.None, new long[] { 1 }, Progress);

        Assert.Equal(new long[] { 1 }, left.Sensors);
        Assert.Single(left.StageFailures);
    }
}
