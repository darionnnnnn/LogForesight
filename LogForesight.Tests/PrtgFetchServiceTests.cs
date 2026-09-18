using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgFetchServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfPrtgStore CreateStore() => new(_fx.NewContext);

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode code = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; } =
            (_, _) => throw new InvalidOperationException("測試未設定 OnSend");

        public List<string> RequestedUrls { get; } = new();

        private int _activeRequests;
        public int MaxConcurrentRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (RequestedUrls)
            {
                RequestedUrls.Add(url);
            }

            var current = Interlocked.Increment(ref _activeRequests);
            lock (this)
            {
                if (current > MaxConcurrentRequests)
                {
                    MaxConcurrentRequests = current;
                }
            }

            try
            {
                return await OnSend(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
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

    [Fact]
    public async Task FetchDayAsync_device與sensor結構寫入鏡像表()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod/Servers\",\"status\":\"Up\",\"tags\":\"win prod\",\"paused\":false,\"dependency\":0}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU Load\",\"type\":\"wmicpu\",\"tags\":\"cpu perf\",\"unit\":\"%\",\"status\":\"Up\",\"paused\":false,\"dependency\":\"none\"}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":15.5,\"coverage\":100}]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":1,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(101));

        Assert.Equal(1, result.Devices);
        Assert.Equal(1, result.Sensors);
        Assert.Equal(0, result.StateChanges);
        Assert.Equal(1, result.Values);
        Assert.Equal(0, result.Failures);

        using var ctx = _fx.NewContext();
        var dev = await ctx.PrtgDevices.FirstOrDefaultAsync(d => d.Objid == 101);
        Assert.NotNull(dev);
        Assert.Equal("Server-01", dev.Name);
        Assert.Equal("192.168.1.10", dev.Ip);
        Assert.Equal("Prod/Servers", dev.GroupPath);
        Assert.False(dev.Paused);
        Assert.Null(dev.DependencyObjid);

        var sen = await ctx.PrtgSensors.FirstOrDefaultAsync(s => s.Objid == 201);
        Assert.NotNull(sen);
        Assert.Equal(101, sen.DeviceObjid);
        Assert.Equal("CPU Load", sen.Name);
        Assert.Equal("wmicpu", sen.SensorType);
        Assert.Null(sen.Category);
        Assert.Null(sen.CategorySource);
        Assert.False(sen.Paused);
        Assert.Null(sen.DependencyObjid);
    }

    [Fact]
    public async Task FetchDayAsync_同一天重跑不產生重複列()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU Load\",\"type\":\"wmicpu\",\"status\":\"Up\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":1,\"messages\":[{\"objid\":201,\"datetime\":\"2026-08-30 08:30:00\",\"parent\":101,\"status\":\"Down\",\"message\":\"Host unreachable\"}]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":25.0,\"coverage\":100}]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":1,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return url.Contains("start=0") ? JsonResponse(msgJson) : JsonResponse("{\"treesize\":1,\"messages\":[]}");
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        // 跑第一次
        var result1 = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(101));
        Assert.Equal(0, result1.Failures);

        // 跑第二次（同一天重跑）
        var result2 = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(101));
        Assert.Equal(0, result2.Failures);

        using var ctx = _fx.NewContext();
        Assert.Equal(1, await ctx.PrtgDevices.CountAsync());
        Assert.Equal(1, await ctx.PrtgSensors.CountAsync());
        Assert.Equal(1, await ctx.PrtgStateChanges.CountAsync());
        Assert.Equal(1, await ctx.PrtgValues.CountAsync());
    }

    [Fact]
    public async Task FetchDayAsync_paused的sensor不抓數值()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"paused\":false}]}";
        // 兩個 sensor：201 為暫停 (paused = true)，202 為正常 (paused = false)
        var senJson = "{\"treesize\":2,\"sensors\":[" +
                      "{\"objid\":201,\"parentid\":101,\"sensor\":\"Memory\",\"type\":\"wmimem\",\"paused\":true}," +
                      "{\"objid\":202,\"parentid\":101,\"sensor\":\"Disk Free\",\"type\":\"wmidisk\",\"paused\":false}" +
                      "]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":80.0,\"coverage\":100}]}";

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":2,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(101));
        Assert.Equal(0, result.Failures);
        Assert.Equal(1, result.Values);

        // 斷言 paused 的 sensor (201) 完全沒有任何 lf_prtg_values 列
        using var ctx = _fx.NewContext();
        var values201 = await ctx.PrtgValues.Where(v => v.SensorObjid == 201).ToListAsync();
        Assert.Empty(values201);

        var values202 = await ctx.PrtgValues.Where(v => v.SensorObjid == 202).ToListAsync();
        Assert.Single(values202);

        // 斷言 paused 的 sensor 201 的 historicdata 端點完全沒有被請求
        var requestedSensor201 = handler.RequestedUrls.Any(u => u.Contains("historicdata.json") && u.Contains("id=201"));
        Assert.False(requestedSensor201, "已暫停的 sensor 不應發出 historicdata 請求");

        var requestedSensor202 = handler.RequestedUrls.Any(u => u.Contains("historicdata.json") && u.Contains("id=202"));
        Assert.True(requestedSensor202, "未暫停的 sensor 必須發出 historicdata 請求");
    }

    [Fact]
    public async Task FetchDayAsync_數值為空時標記為NoData且值為null()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"Sensor-01\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        // value_ 為空字串、""、或 null
        var histJson = "{\"histdata\":[" +
                       "{\"datetime\":\"2026-08-30 00:00:00\",\"value_\":\"\",\"coverage\":100}," +
                       "{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":null,\"coverage\":100}" +
                       "]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":1,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(101));
        Assert.Equal(0, result.Failures);
        Assert.Equal(2, result.Values);

        using var ctx = _fx.NewContext();
        var rows = await ctx.PrtgValues.OrderBy(v => v.PeriodStart).ToListAsync();
        Assert.Equal(2, rows.Count);

        Assert.Equal(PrtgDataQuality.NoData, rows[0].Quality);
        Assert.Null(rows[0].AvgValue);

        Assert.Equal(PrtgDataQuality.NoData, rows[1].Quality);
        Assert.Null(rows[1].AvgValue);
    }

    [Fact]
    public async Task FetchDayAsync_coverage為0時標記為Unknown()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"Sensor-01\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        // 一筆 coverage 為 0，另一筆值為 "unknown"
        var histJson = "{\"histdata\":[" +
                       "{\"datetime\":\"2026-08-30 00:00:00\",\"value_\":45.2,\"coverage\":0}," +
                       "{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":\"Unknown\",\"coverage\":100}" +
                       "]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":1,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(101));
        Assert.Equal(0, result.Failures);
        Assert.Equal(2, result.Values);

        using var ctx = _fx.NewContext();
        var rows = await ctx.PrtgValues.OrderBy(v => v.PeriodStart).ToListAsync();
        Assert.Equal(2, rows.Count);

        Assert.Equal(PrtgDataQuality.Unknown, rows[0].Quality);
        Assert.Null(rows[0].AvgValue);

        Assert.Equal(PrtgDataQuality.Unknown, rows[1].Quality);
        Assert.Null(rows[1].AvgValue);
    }

    [Fact]
    public async Task FetchDayAsync_Unknown與NoData的列仍然寫入()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"Sensor-01\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[" +
                       "{\"datetime\":\"2026-08-30 00:00:00\",\"value_\":\"\",\"coverage\":100}," +
                       "{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":\"[unknown]\",\"coverage\":100}," +
                       "{\"datetime\":\"2026-08-30 02:00:00\",\"value_\":33.5,\"coverage\":100}" +
                       "]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":1,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(101));
        Assert.Equal(0, result.Failures);
        Assert.Equal(3, result.Values);

        using var ctx = _fx.NewContext();
        var rows = await ctx.PrtgValues.OrderBy(v => v.PeriodStart).ToListAsync();
        Assert.Equal(3, rows.Count);

        // 斷言列存在、AvgValue 為 null、Quality 正確
        Assert.Equal(PrtgDataQuality.NoData, rows[0].Quality);
        Assert.Null(rows[0].AvgValue);

        Assert.Equal(PrtgDataQuality.Unknown, rows[1].Quality);
        Assert.Null(rows[1].AvgValue);

        Assert.Equal(PrtgDataQuality.Ok, rows[2].Quality);
        Assert.Equal(33.5, rows[2].AvgValue);
    }

    [Fact]
    public async Task FetchDayAsync_狀態變更只保留當天的紀錄()
    {
        var devJson = "{\"treesize\":0,\"devices\":[]}";
        var senJson = "{\"treesize\":0,\"sensors\":[]}";
        // messages 含前一天 (2026-08-29)、當天 (2026-08-30)、後一天 (2026-08-31)
        var msgJson = "{\"treesize\":3,\"messages\":[" +
                      "{\"objid\":301,\"datetime\":\"2026-08-29 23:59:59\",\"parent\":101,\"status\":\"Down\",\"message\":\"Prev day\"}," +
                      "{\"objid\":302,\"datetime\":\"2026-08-30 12:00:00\",\"parent\":101,\"status\":\"Warning\",\"message\":\"Target day\"}," +
                      "{\"objid\":303,\"datetime\":\"2026-08-31 00:00:01\",\"parent\":101,\"status\":\"Up\",\"message\":\"Next day\"}" +
                      "]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return JsonResponse(devJson);
            if (url.Contains("content=sensors"))
                return JsonResponse(senJson);
            if (url.Contains("content=messages"))
                return url.Contains("start=0") ? JsonResponse(msgJson) : JsonResponse("{\"treesize\":3,\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(101));
        Assert.Equal(0, result.Failures);
        // 新語意：抓取目標日前一天～今天，因此 2026-08-29 (301)、2026-08-30 (302)、2026-08-31 (303) 皆在區間內並寫入
        Assert.Equal(3, result.StateChanges);

        using var ctx = _fx.NewContext();
        var changes = await ctx.PrtgStateChanges.ToListAsync();
        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c.SensorObjid == 301 && c.Message == "Prev day");
        Assert.Contains(changes, c => c.SensorObjid == 302 && c.Message == "Target day");
        Assert.Contains(changes, c => c.SensorObjid == 303 && c.Message == "Next day");
    }

    [Fact]
    public async Task FetchDayAsync_某階段失敗時其餘階段照跑()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":1,\"messages\":[{\"objid\":101,\"datetime\":\"2026-08-30 10:00:00\",\"status\":\"Up\"}]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            // sensors 階段擲 HTTP 500 錯誤
            if (url.Contains("content=sensors"))
                return JsonResponse("{\"error\":\"Internal error\"}", HttpStatusCode.InternalServerError);
            if (url.Contains("content=messages"))
                return url.Contains("start=0") ? JsonResponse(msgJson) : JsonResponse("{\"treesize\":1,\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        // 斷言不擲出例外
        var result = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(101));

        // 斷言 devices 仍然寫入、回傳的 Failures 大於 0
        Assert.Equal(1, result.Devices);
        Assert.Equal(0, result.Sensors);
        Assert.Equal(1, result.StateChanges);
        Assert.True(result.Failures > 0, "Sensors 階段失敗應記錄 Failures > 0");

        using var ctx = _fx.NewContext();
        Assert.Equal(1, await ctx.PrtgDevices.CountAsync());
        Assert.Equal(0, await ctx.PrtgSensors.CountAsync());
        Assert.Equal(1, await ctx.PrtgStateChanges.CountAsync());
    }

    [Fact]
    public async Task FetchDayAsync_併發上限生效()
    {
        var devJson = "{\"treesize\":0,\"devices\":[]}";
        // 5 個未暫停的 sensor
        var senJson = "{\"treesize\":5,\"sensors\":[" +
                      "{\"objid\":201,\"paused\":false}," +
                      "{\"objid\":202,\"paused\":false}," +
                      "{\"objid\":203,\"paused\":false}," +
                      "{\"objid\":204,\"paused\":false}," +
                      "{\"objid\":205,\"paused\":false}" +
                      "]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10,\"coverage\":100}]}";

        var handler = new StubHandler();
        handler.OnSend = async (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse(devJson);
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":5,\"sensors\":[]}");
            if (url.Contains("content=messages")) return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
            {
                await Task.Delay(50); // 模擬網路延遲以觀察併發峰值
                return JsonResponse(histJson);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        // 限制併發數為 1
        var result = await service.FetchDayAsync(day, concurrency: 1, CancellationToken.None, ScopeOf(0));
        Assert.Equal(0, result.Failures);
        Assert.Equal(5, result.Values);

        // 斷言同時進行中的請求數峰值不超過 1
        Assert.Equal(1, handler.MaxConcurrentRequests);
    }

    [Fact]
    public async Task FetchDayAsync_併發設定值真的被採用而非寫死()
    {
        // 只驗 concurrency=1 證明不了「設定有被吃進去」——把 semaphore 寫死成 1 也照樣綠。
        // 這裡驗 concurrency=3 時峰值**等於** 3。放行閘門取代固定延遲：前 3 個請求到齊才一起放行，
        // 峰值不再取決於機器負載下的排程時機（固定延遲版在全套並行時偶發達不到峰值）。
        // semaphore 若被寫死成 1 或 2，第 3 個請求永遠不會到達，閘門逾時 → 失敗數不為 0，一樣現形。
        var senJson = "{\"treesize\":6,\"sensors\":[" +
                      string.Join(",", Enumerable.Range(301, 6).Select(id => $"{{\"objid\":{id},\"paused\":false}}")) +
                      "]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10,\"coverage\":100}]}";

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentArrivals = 0;

        var handler = new StubHandler();
        handler.OnSend = async (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":6,\"sensors\":[]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            if (url.Contains("historicdata.json"))
            {
                // 前 3 個併發請求到齊才一起放行，確保峰值必然衝到 semaphore 上限
                if (Interlocked.Increment(ref concurrentArrivals) >= 3) gate.TrySetResult();
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return JsonResponse(histJson);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), concurrency: 3, CancellationToken.None, ScopeOf(0));

        Assert.Equal(0, result.Failures);
        Assert.Equal(3, handler.MaxConcurrentRequests);
    }

    [Fact]
    public async Task FetchDayAsync_分頁能抓完多頁()
    {
        // 第一頁 500 筆
        var page1Sb = new StringBuilder();
        page1Sb.Append("{\"treesize\":5003,\"devices\":[");
        for (var i = 1; i <= 5000; i++)
        {
            if (i > 1) page1Sb.Append(',');
            page1Sb.Append($"{{\"objid\":{i},\"device\":\"Dev-{i}\",\"paused\":false}}");
        }
        page1Sb.Append("]}");
        var page1Json = page1Sb.ToString();

        // 第二頁 3 筆
        var page2Json = "{\"treesize\":5003,\"devices\":[" +
                        "{\"objid\":5001,\"device\":\"Dev-5001\",\"paused\":false}," +
                        "{\"objid\":5002,\"device\":\"Dev-5002\",\"paused\":false}," +
                        "{\"objid\":5003,\"device\":\"Dev-5003\",\"paused\":false}" +
                        "]}";
        // 第三頁空陣列
        var page3Json = "{\"treesize\":5003,\"devices\":[]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
            {
                if (url.Contains("start=0")) return JsonResponse(page1Json);
                if (url.Contains("start=5000")) return JsonResponse(page2Json);
                return JsonResponse(page3Json);
            }
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None, NoScope);
        Assert.Equal(0, result.Failures);
        Assert.Equal(5003, result.Devices);

        using var ctx = _fx.NewContext();
        var count = await ctx.PrtgDevices.CountAsync();
        Assert.Equal(5003, count);
    }

    [Fact]
    public async Task FetchDayAsync_伺服器忽略start參數且每頁回滿時仍會終止()
    {
        // PRTG 前面若擺了會忽略 start 參數的代理，**每頁都回滿一頁**同一批資料。
        // 只靠「空頁」或「未滿一頁」判定都會永遠跑不完、整趥夜間批次無聲卡死。
        // 替身必須真的回滿頁（500 筆），每頁只回兩筆的替身第一頁就已經「未滿一頁」而停，根本沒測到這件事。
        var fullPage = BuildDevicePage(1, PageSizeForFullPage);

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse(fullPage);
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var service = new PrtgFetchService(client, store, new TestConsole(), new Dictionary<string, string>());

        var task = service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, NoScope);
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(task, finished);
        var result = await task;
        Assert.Equal(2, handler.RequestedUrls.Count(u => u.Contains("content=devices")));
        Assert.Equal(PageSizeForFullPage, result.Devices);
        Assert.Equal(0, result.Failures);
    }

    [Fact]
    public async Task FetchDayAsync_超出範圍夾到末頁時備註重複列數與階段耗時()
    {
        // 實機行為（探測步驟 8 實測）：start 超出範圍時回最後一頁而非空頁
        var page1 = BuildDevicePage(1, PageSizeForFullPage);
        var page2 = BuildDevicePage(PageSizeForFullPage + 1, PageSizeForFullPage);

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return JsonResponse(url.Contains("start=0") ? page1 : page2);
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, NoScope);

        Assert.Equal(PageSizeForFullPage * 2, result.Devices);
        Assert.Equal(0, result.Failures);
        Assert.Equal(3, handler.RequestedUrls.Count(u => u.Contains("content=devices")));
        Assert.Contains(console.Lines, l => l.Contains($"跳過重複列 {PageSizeForFullPage} 筆"));
        Assert.Contains(console.Lines, l => l.Contains("[階段 1/4]") && l.Contains("耗時"));
    }

    /// <summary>
    /// messages 的 objid 是「發出訊息的 sensor」，同一顆 sensor 一天會有很多筆狀態變更。
    /// 分頁若按 objid 去重，每顆 sensor 只會留下第一筆，而且整頁同一顆 sensor 時
    /// 還會被當成分頁結尾提早停止——兩個後果都是靜默的，畫面照樣顯示同步完成。
    /// </summary>
    [Fact]
    public async Task FetchDayAsync_同一sensor同一天多筆狀態變更全部寫入()
    {
        var day = new DateTime(2026, 8, 30);
        var messages = new StringBuilder();
        messages.Append("{\"messages\":[");
        for (var i = 0; i < 6; i++)
        {
            if (i > 0) messages.Append(',');
            // 同一顆 sensor（9001），六個不同時間
            messages.Append($"{{\"objid\":9001,\"datetime\":\"2026-08-30 1{i}:00:00\",\"status\":\"Down\",\"message\":\"m{i}\"}}");
        }
        messages.Append("]}");
        var messagesJson = messages.ToString();

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return JsonResponse(url.Contains("start=0") ? messagesJson : "{\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var service = new PrtgFetchService(client, store, new TestConsole(), new Dictionary<string, string>());

        var result = await service.FetchDayAsync(day, 1, CancellationToken.None, ScopeOf(1));

        Assert.Equal(6, result.StateChanges);
        using var ctx = _fx.NewContext();
        Assert.Equal(6, await ctx.PrtgStateChanges.CountAsync(c => c.SensorObjid == 9001));
    }

    /// <summary>
    /// 「滿頁」的筆數＝分頁器的預設 count。要模擬「PRTG 回滿一頁」就必須剛好是這個數，
    /// 少一筆就會被停止條件判成最後一頁，整個情境就測不到了。
    /// </summary>
    private const int PageSizeForFullPage = 5000;

    /// <summary>產生一頁 devices（objid 從 firstObjid 連號）。</summary>
    private static string BuildDevicePage(int firstObjid, int count)
    {
        var sb = new StringBuilder();
        sb.Append("{\"devices\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"objid\":{firstObjid + i},\"device\":\"Dev-{firstObjid + i}\",\"paused\":false}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }
    // ── historicdata 原始欄位解析（datetime_raw／value_raw／coverage_raw，docs/PRTG-SPEC.md §3a）──────────

    /// <summary>
    /// 只回一個 sensor（objid 9001）與指定 histdata 原文的假 PRTG。
    /// </summary>
    private (PrtgClient Client, StubHandler Handler) CreateHistClient(string histJson)
    {
        return CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return JsonResponse("{\"treesize\":1,\"sensors\":[{\"objid\":9001,\"parentid\":1,\"sensor\":\"S\",\"type\":\"ping\",\"paused\":false}]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            if (url.Contains("historicdata")) return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
    }

    /// <summary>
    /// 以指定文化執行：顯示字串的解析會用 CurrentCulture 試一次，
    /// 繁中格式的斷言不能依賴跑測試那台機器剛好是 zh-TW。
    /// </summary>
    private async Task<(PrtgFetchResult Result, List<PrtgValueRow> Rows, TestConsole Console)> RunHistWithCultureAsync(
        string histJson, string cultureName)
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(cultureName);
        try
        {
            return await RunHistAsync(histJson);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    private async Task<(PrtgFetchResult Result, List<PrtgValueRow> Rows, TestConsole Console)> RunHistAsync(string histJson)
    {
        var (client, _) = CreateHistClient(histJson);
        var console = new TestConsole();
        var service = new PrtgFetchService(client, CreateStore(), console, new Dictionary<string, string>());
        var result = await service.FetchDayAsync(new DateTime(2026, 9, 10), 1, CancellationToken.None, ScopeOf(1));
        using var ctx = _fx.NewContext();
        var rows = await ctx.PrtgValues.OrderBy(v => v.PeriodStart).ToListAsync();
        return (result, rows, console);
    }

    /// <summary>
    /// PRTG 24.1.92 繁中實機的一列原文。關鍵事實：同一列的 datetime_raw（OLE 46275.6666666667
    /// ＝2026-09-10 16:00）與顯示字串（下午 11:00＝23:00）**差 7 小時**，兩者不是同一個時間基準。
    /// 鏡像要跟管理者在 PRTG 畫面上看到的時間對齊，所以顯示字串優先；拿 raw 當主要來源會讓
    /// 整份基線整體平移數小時而毫無徵兆。value／value_raw 重複是多頻道，取第一組（主要頻道）。
    /// </summary>
    [Fact]
    public async Task FetchDayAsync_實機形狀_顯示字串優先於datetime_raw()
    {
        var histJson = "{\"histdata\":[" +
                       "{\"datetime\":\"2026/9/10 下午 11:00:00 - 上午 12:00:00\",\"datetime_raw\":46275.6666666667," +
                       "\"value\":\"4 %\",\"value_raw\":3.5593,\"value\":\"6 %\",\"value_raw\":5.6949," +
                       "\"coverage\":\"100 %\",\"coverage_raw\":10000}" +
                       "]}";

        var (result, rows, console) = await RunHistWithCultureAsync(histJson, "zh-TW");

        Assert.Equal(1, result.Values);
        var row = Assert.Single(rows);
        // 用 raw 會得到 16:00；必須是顯示字串的 23:00
        Assert.Equal(new DateTime(2026, 9, 10, 23, 0, 0), row.PeriodStart);
        Assert.Equal(3.5593, row.AvgValue!.Value, 4);
        Assert.Equal(PrtgDataQuality.Ok, row.Quality);
        Assert.Equal(100.0, row.Coverage!.Value, 6);
        // 走的是顯示字串，不該出現退路警告
        Assert.DoesNotContain(console.Lines, l => l.Contains("原始日期數值推得"));
    }

    /// <summary>
    /// d/M 格式的 PRTG（如 en-GB）配上 M/d 或 y/M/d 的站台文化：「10/09/2026」會被解析成 10 月 9 日，
    /// 而且解析成功、不報錯。datetime_raw 與顯示字串同一天（只差幾小時），差超過一天就是月日讀反了，
    /// 這時要退到 raw 並回報，不能讓錯的日期靜默進基線。
    /// </summary>
    [Fact]
    public async Task FetchDayAsync_顯示字串月日對調時_以datetime_raw擋下並回報()
    {
        // raw 46275.6666666667 ＝ 2026-09-10 16:00；字串 10/09/2026 在 M/d 文化會讀成 2026-10-09
        var histJson = "{\"histdata\":[" +
                       "{\"datetime\":\"10/09/2026 23:00:00 - 11/09/2026 00:00:00\",\"datetime_raw\":46275.6666666667," +
                       "\"value_raw\":3.5593,\"coverage_raw\":10000}" +
                       "]}";

        var (result, rows, console) = await RunHistWithCultureAsync(histJson, "en-US");

        Assert.Equal(1, result.Values);
        var row = Assert.Single(rows);
        Assert.Equal(new DateTime(2026, 9, 10), row.PeriodStart.Date);
        Assert.Contains(console.Lines, l => l.Contains("原始日期數值推得"));
    }

    /// <summary>
    /// 沒有 value_raw 時退回 value_，多頻道的重複鍵同樣要取第一組（主要頻道），
    /// 不能因為走了退路就變成取最後一個頻道。
    /// </summary>
    [Fact]
    public async Task FetchDayAsync_value退路遇重複鍵_取第一組主要頻道()
    {
        var histJson = "{\"histdata\":[" +
                       "{\"datetime\":\"2026-09-10 23:00:00 - 2026-09-11 00:00:00\"," +
                       "\"value_\":4,\"value_\":6,\"coverage_raw\":10000}" +
                       "]}";

        var (_, rows, _) = await RunHistAsync(histJson);

        var row = Assert.Single(rows);
        Assert.Equal(4.0, row.AvgValue!.Value, 6);
    }

    /// <summary>
    /// 不依賴機器文化的版本：顯示字串用 ISO 形式，raw 指向完全不同的時間。
    /// 這一條在任何文化的機器上都必須通過，是「顯示字串優先」的主要守門。
    /// </summary>
    [Fact]
    public async Task FetchDayAsync_顯示字串可解析時不採用datetime_raw()
    {
        var histJson = "{\"histdata\":[" +
                       "{\"datetime\":\"2026-09-10 23:00:00 - 2026-09-11 00:00:00\",\"datetime_raw\":46275.6666666667," +
                       "\"value_raw\":3.5593,\"coverage_raw\":10000}" +
                       "]}";

        var (_, rows, console) = await RunHistAsync(histJson);

        var row = Assert.Single(rows);
        Assert.Equal(new DateTime(2026, 9, 10, 23, 0, 0), row.PeriodStart);
        Assert.DoesNotContain(console.Lines, l => l.Contains("原始日期數值推得"));
    }

    /// <summary>
    /// 顯示字串解析不了（PRTG 的地區格式與站台文化不合）時才退回 OLE 日期，
    /// 並且要出聲——走退路的資料落在哪個小時不可靠，靜默接受等於讓基線悄悄錯位。
    /// </summary>
    [Fact]
    public async Task FetchDayAsync_顯示字串不可解析時退回datetime_raw並警告()
    {
        var histJson = "{\"histdata\":[" +
                       "{\"datetime\":\"完全不是日期\",\"datetime_raw\":46275.6666666667,\"value_raw\":12.5,\"coverage_raw\":10000}" +
                       "]}";

        var (result, rows, console) = await RunHistAsync(histJson);

        Assert.Equal(1, result.Values);
        var row = Assert.Single(rows);
        Assert.Equal(new DateTime(2026, 9, 10, 16, 0, 0), row.PeriodStart);
        Assert.Equal(12.5, row.AvgValue!.Value, 6);
        Assert.Contains(console.Lines, l => l.Contains("原始日期數值推得"));
        Assert.DoesNotContain(console.Lines, l => l.Contains("時間欄位無法解析"));
    }

    [Fact]
    public async Task FetchDayAsync_沒有datetime欄位時用datetime_raw()
    {
        var histJson = "{\"histdata\":[" +
                       "{\"datetime_raw\":46275.6666666667,\"value_\":7,\"coverage\":100}" +
                       "]}";

        var (result, rows, console) = await RunHistAsync(histJson);

        Assert.Equal(1, result.Values);
        var row = Assert.Single(rows);
        Assert.Equal(new DateTime(2026, 9, 10, 16, 0, 0), row.PeriodStart);
        Assert.Contains(console.Lines, l => l.Contains("原始日期數值推得"));
    }

    [Fact]
    public async Task FetchDayAsync_datetime_raw超出OLE合法範圍且字串不可解析時計入略過()
    {
        var histJson = "{\"histdata\":[" +
                       "{\"datetime\":\"完全不是日期\",\"datetime_raw\":1e12,\"value_\":7,\"coverage\":100}" +
                       "]}";

        var (result, rows, console) = await RunHistAsync(histJson);

        Assert.Equal(0, result.Values);
        Assert.Empty(rows);
        Assert.Contains(console.Lines, l => l.Contains("時間欄位無法解析"));
    }

    [Fact]
    public async Task FetchDayAsync_datetime_raw與datetime皆不可解析時計入略過筆數()
    {
        var histJson = "{\"histdata\":[" +
                       "{\"datetime\":\"完全不是日期\",\"datetime_raw\":\"abc\",\"value_raw\":1.0}" +
                       "]}";

        var (result, rows, console) = await RunHistAsync(histJson);

        Assert.Equal(0, result.Values);
        Assert.Empty(rows);
        Assert.Contains(console.Lines, l => l.Contains("時間欄位無法解析"));
    }

    [Fact]
    public async Task FetchDayAsync_coverage_raw為0時仍判Unknown()
    {
        var histJson = "{\"histdata\":[" +
                       "{\"datetime_raw\":46275.6666666667,\"value_raw\":45.2,\"coverage_raw\":0}" +
                       "]}";

        var (_, rows, _) = await RunHistAsync(histJson);

        var row = Assert.Single(rows);
        Assert.Equal(PrtgDataQuality.Unknown, row.Quality);
        Assert.Null(row.AvgValue);
        Assert.Equal(0.0, row.Coverage!.Value, 6);
    }

    [Fact]
    public async Task FetchDayAsync_coverage_raw換算為百分比()
    {
        var histJson = "{\"histdata\":[" +
                       "{\"datetime_raw\":46275.6666666667,\"value_raw\":1.25,\"coverage_raw\":8500}" +
                       "]}";

        var (_, rows, _) = await RunHistAsync(histJson);

        var row = Assert.Single(rows);
        Assert.Equal(85.0, row.Coverage!.Value, 6);
        Assert.Equal(PrtgDataQuality.Ok, row.Quality);
    }

    [Fact]
    public async Task FetchDayAsync_只有value_raw沒有value時照樣取得值()
    {
        // 不依賴 value_／value：整列只有原始欄位也要能落地。
        var histJson = "{\"histdata\":[" +
                       "{\"datetime_raw\":46275.6666666667,\"value_raw\":\"3.5593\",\"coverage_raw\":10000}" +
                       "]}";

        var (result, rows, _) = await RunHistAsync(histJson);

        Assert.Equal(1, result.Values);
        var row = Assert.Single(rows);
        Assert.Equal(3.5593, row.AvgValue!.Value, 4);
        Assert.Equal(PrtgDataQuality.Ok, row.Quality);
    }

    [Fact]
    public async Task FetchDayAsync_數值時間無法解析時回報略過筆數而非靜默跳過()
    {
        // PRTG 依伺服器地區設定輸出時間字串，格式與本機不符時整段會解析失敗。
        // 這種缺口必須在執行輸出中被看見——靜默略過會讓後續基線把「解析失敗」誤認成「本來就沒資料」。
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return JsonResponse("{\"treesize\":1,\"sensors\":[{\"objid\":9001,\"parentid\":1,\"sensor\":\"S\",\"type\":\"ping\",\"paused\":false}]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            if (url.Contains("historicdata"))
                return JsonResponse("{\"histdata\":[{\"datetime\":\"完全不是日期\",\"value_\":1},{\"datetime\":\"\",\"value_\":2}]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, ScopeOf(1));

        Assert.Equal(0, result.Values);
        Assert.Contains(console.Lines, l => l.Contains("無法解析") && l.Contains("2"));
    }

    [Fact]
    public async Task FetchDayAsync_單一sensor失敗時其餘sensor數值照樣落地且不計階段失敗()
    {
        // 三個 sensor、其中一個 historicdata 回 500：隔離語意是「只影響它自己」——
        // 其餘兩個的數值照樣寫入、Failures 為 0、console 有損耗回報。
        // 沒有這個測試的話，把隔離邏輯改回「例外往外拋」全綠照樣通過（換模型體檢補）。
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return JsonResponse("{\"treesize\":3,\"sensors\":[" +
                    "{\"objid\":901,\"parentid\":1,\"sensor\":\"A\",\"type\":\"ping\",\"paused\":false}," +
                    "{\"objid\":902,\"parentid\":1,\"sensor\":\"B\",\"type\":\"ping\",\"paused\":false}," +
                    "{\"objid\":903,\"parentid\":1,\"sensor\":\"C\",\"type\":\"ping\",\"paused\":false}]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            if (url.Contains("historicdata"))
            {
                if (url.Contains("id=902")) return JsonResponse("boom", HttpStatusCode.InternalServerError);
                return JsonResponse("{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":5,\"coverage\":100}]}");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, ScopeOf(1));

        Assert.Equal(0, result.Failures);
        Assert.Equal(2, result.Values);
        Assert.Contains(console.Lines, l => l.Contains("1 個感測器的數值擷取失敗"));

        using var ctx = _fx.NewContext();
        Assert.Equal(2, ctx.PrtgValues.Count());
    }

    [Fact]
    public async Task FetchDayAsync_單一sensor逾時其餘正常_回報逾時計數且其餘數值照樣落地()
    {
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return JsonResponse("{\"treesize\":3,\"sensors\":[" +
                    "{\"objid\":901,\"parentid\":1,\"sensor\":\"A\",\"type\":\"ping\",\"paused\":false}," +
                    "{\"objid\":902,\"parentid\":1,\"sensor\":\"B\",\"type\":\"ping\",\"paused\":false}," +
                    "{\"objid\":903,\"parentid\":1,\"sensor\":\"C\",\"type\":\"ping\",\"paused\":false}]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            if (url.Contains("historicdata"))
            {
                if (url.Contains("id=902")) throw new TaskCanceledException();
                return JsonResponse("{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":5,\"coverage\":100}]}");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, ScopeOf(1));

        Assert.Equal(0, result.Failures);
        Assert.Equal(2, result.Values);
        Assert.Contains(console.Lines, l => l.Contains("其中 1 個是請求逾時"));

        using var ctx = _fx.NewContext();
        Assert.Equal(2, ctx.PrtgValues.Count());
    }

    [Fact]
    public async Task FetchDayAsync_全部sensor數值擷取皆失敗時計為階段失敗()
    {
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return JsonResponse("{\"treesize\":1,\"sensors\":[{\"objid\":901,\"parentid\":1,\"sensor\":\"A\",\"type\":\"ping\",\"paused\":false}]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            if (url.Contains("historicdata")) return JsonResponse("boom", HttpStatusCode.InternalServerError);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, ScopeOf(1));

        // 有 sensor 要抓卻一筆都沒抓到＝這個階段實質沒成功，必須反映在 Failures
        Assert.True(result.Failures > 0);
        Assert.Equal(0, result.Values);
    }

    [Fact]
    public async Task FetchDayAsync_不同步結構時不打結構端點且沿用鏡像sensor清單()
    {
        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            if (url.Contains("historicdata"))
                return JsonResponse("{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":7,\"coverage\":100}]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var syncedAt = new DateTime(2026, 8, 29, 1, 0, 0);
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 901, DeviceObjid = 1, Name = "A", SensorType = "ping", Paused = false },
            new() { Objid = 902, DeviceObjid = 1, Name = "B", SensorType = "ping", Paused = true }
        }, syncedAt);

        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, NoScope, syncStructure: false);

        // 結構端點零請求；paused 的 902 不抓；synced_at 未被改寫（回填不得汙染「最後結構同步時間」）
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("content=devices"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("content=sensors"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("id=902"));
        Assert.Equal(0, result.Failures);
        Assert.Equal(1, result.Values);

        using var ctx = _fx.NewContext();
        Assert.Equal(syncedAt, ctx.PrtgSensors.Single(s => s.Objid == 901).SyncedAt);
    }

    [Fact]
    public async Task FetchDayAsync_不同步結構且鏡像為空時計為失敗不空跑()
    {
        // 剛設定完就按回填（每日擷取一次都沒跑過）：不擋下的話每一天都是
        // 「0 個 sensor → 0 筆 → 無失敗」的空跑，整趟回填被報成成功
        var (client, handler) = CreateClient(_ => JsonResponse("{}"));
        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, NoScope, syncStructure: false);

        Assert.True(result.Failures > 0);
        Assert.Empty(handler.RequestedUrls);
        Assert.Contains(console.Lines, l => l.Contains("鏡像尚無任何感測器結構"));
    }

    [Fact]
    public async Task FetchDayAsync_每日擷取後自動填入感測器語意分類()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var senJson = "{\"treesize\":3,\"sensors\":[" +
            "{\"objid\":201,\"parentid\":101,\"sensor\":\"Free Space C:\",\"type\":\"SNMP Disk Free\",\"status\":\"Up\",\"paused\":false}," +
            "{\"objid\":202,\"parentid\":101,\"sensor\":\"Custom\",\"type\":\"SNMP Custom\",\"status\":\"Up\",\"paused\":false}," +
            "{\"objid\":203,\"parentid\":101,\"sensor\":\"Fan\",\"type\":\"Custom Fan\",\"status\":\"Up\",\"paused\":false}" +
            "]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":3,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        // 建構端傳入的補充表必須真的流到分類重算（203 只在補充表裡）
        var overrides = PrtgSensorTypeCategoryMap.ParseOverrides(new[] { "Custom Fan=hardware" }).Map;
        var service = new PrtgFetchService(client, store, console, overrides);
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 1, CancellationToken.None, ScopeOf(101));

        Assert.Equal(0, result.Failures);
        Assert.Equal(3, result.Sensors);

        using var ctx = _fx.NewContext();
        var s201 = await ctx.PrtgSensors.SingleAsync(s => s.Objid == 201);
        Assert.Equal(PrtgSensorCategories.Disk, s201.Category);
        Assert.Equal(PrtgCategorySources.Auto, s201.CategorySource);

        var s202 = await ctx.PrtgSensors.SingleAsync(s => s.Objid == 202);
        Assert.Null(s202.Category);
        Assert.Null(s202.CategorySource);

        var s203 = await ctx.PrtgSensors.SingleAsync(s => s.Objid == 203);
        Assert.Equal(PrtgSensorCategories.Hardware, s203.Category);
        Assert.Equal(PrtgCategorySources.Auto, s203.CategorySource);

        Assert.Contains(console.Lines, l => l.Contains("[階段 2/4] 已更新 2 個感測器的語意分類。"));
    }

    [Fact]
    public async Task FetchDayAsync_fetchValues為false時略過階段4且不發出historicdata請求()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"Ping\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":1,\"messages\":[{\"objid\":201,\"datetime\":\"2026-08-30 10:00:00\",\"parent\":\"Server-01\",\"type\":\"Ping\",\"status\":\"Up\",\"message\":\"OK\"}]}";

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":1,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return url.Contains("start=0") ? JsonResponse(msgJson) : JsonResponse("{\"treesize\":1,\"messages\":[]}");
            if (url.Contains("historicdata"))
                return JsonResponse("{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 1, CancellationToken.None, ScopeOf(101), syncStructure: true, fetchValues: false);

        // 階段 1~3 照常完成（device／sensor／狀態變更有寫入）
        Assert.Equal(1, result.Devices);
        Assert.Equal(1, result.Sensors);
        Assert.Equal(1, result.StateChanges);
        // 階段 4 略過：Values 為 0、Failures 不因此增加
        Assert.Equal(0, result.Values);
        Assert.Equal(0, result.Failures);

        // handler.RequestedUrls 中沒有任何 historicdata 請求
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("historicdata"));
        Assert.Contains(console.Lines, l => l.Contains("[階段 4/4] 數值擷取改由觸發式流程執行，本階段略過。"));

        // 資料庫斷言：device／sensor／狀態變更有寫入，數值為 0
        using var ctx = _fx.NewContext();
        Assert.Equal(1, ctx.PrtgDevices.Count(d => d.Objid == 101));
        Assert.Equal(1, ctx.PrtgSensors.Count(s => s.Objid == 201));
        Assert.Equal(1, ctx.PrtgStateChanges.Count());
        Assert.Equal(0, ctx.PrtgValues.Count());
    }

    [Fact]
    public async Task FetchValuesForSensorsAsync_只對指定sensor發出historicdata請求並寫入數值()
    {
        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("id=501"))
            {
                return JsonResponse("{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":12.5,\"coverage\":100},{\"datetime\":\"2026-08-30 02:00:00\",\"value_\":14.0,\"coverage\":100}]}");
            }
            if (url.Contains("id=502"))
            {
                return JsonResponse("{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":55.0,\"coverage\":100}]}");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        // 指定 501，未指定 502 與 503
        var (written, failed) = await service.FetchValuesForSensorsAsync(
            day, new[] { 501L }, concurrency: 1, CancellationToken.None);

        Assert.Equal(2, written);
        Assert.Equal(0, failed);

        // 雙面斷言：指定的有發出，未指定的沒有發出
        Assert.Contains(handler.RequestedUrls, u => u.Contains("id=501"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("id=502"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("id=503"));

        // 資料庫斷言：501 的 2 筆數值確實落地
        using var ctx = _fx.NewContext();
        Assert.Equal(2, ctx.PrtgValues.Count(v => v.SensorObjid == 501));
        Assert.Equal(0, ctx.PrtgValues.Count(v => v.SensorObjid == 502));
    }

    [Fact]
    public async Task FetchValuesForSensorsAsync_進度回呼回報6個sensor完成且單調遞增()
    {
        var (client, _) = CreateClient(req =>
        {
            return JsonResponse("{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}");
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);
        var sensors = new long[] { 101, 102, 103, 104, 105, 106 };

        var reports = new List<(string Stage, int Done, int Total)>();
        var lockObj = new object();

        var (written, failed) = await service.FetchValuesForSensorsAsync(
            day, sensors, concurrency: 2, CancellationToken.None,
            progress: (stage, done, total) =>
            {
                lock (lockObj)
                {
                    reports.Add((stage, done, total));
                }
            });

        Assert.Equal(6, written);
        Assert.Equal(0, failed);

        // 包含初始 (0, 6) 與 6 次遞增回報，共 7 次回報
        Assert.Equal(7, reports.Count);
        Assert.Equal(("prtg-triggered", 0, 6), reports[0]);
        Assert.Equal(("prtg-triggered", 6, 6), reports[^1]);
        Assert.All(reports, r =>
        {
            Assert.Equal("prtg-triggered", r.Stage);
            Assert.Equal(6, r.Total);
        });

        // 斷言 done 值單調遞增 (0, 1, 2, 3, 4, 5, 6)
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].Done > reports[i - 1].Done, $"done 應單調遞增: {reports[i - 1].Done} -> {reports[i].Done}");
        }
    }

    [Fact]
    public async Task FetchDayAsync_帶進度回呼時回報prtgSync與prtgValues()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"paused\":false}]}";
        var senJson = "{\"treesize\":2,\"sensors\":[" +
                      "{\"objid\":201,\"parentid\":101,\"sensor\":\"S1\",\"paused\":false}," +
                      "{\"objid\":202,\"parentid\":101,\"sensor\":\"S2\",\"paused\":false}" +
                      "]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            if (url.Contains("content=sensors"))
            {
                // S＝{101,102}：102 沒有感測器，逐台查詢時回空表
                if (!HasIdQuery(url, 101)) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":2,\"sensors\":[]}");
            }
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            if (url.Contains("historicdata"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());
        var day = new DateTime(2026, 8, 30);

        var reports = new List<(string Stage, int Done, int Total)>();
        var lockObj = new object();

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(101, 102), syncStructure: true, fetchValues: true,
            progress: (stage, done, total) =>
            {
                lock (lockObj)
                {
                    reports.Add((stage, done, total));
                }
            });

        Assert.Equal(0, result.Failures);

        // 結構同步三階段各自回報自己的 phase（批次A）：原本整段只送一次 (prtg-sync, 0, 0)，
        // 畫面從進入 PRTG 到觸發式取數之間永遠是「準備中…」，跑得慢與卡死長得一樣。
        Assert.Contains(reports, r => r.Stage == PrtgFetchService.PrtgSyncDevicesPhase);
        Assert.Contains(reports, r => r.Stage == PrtgFetchService.PrtgSyncSensorsPhase);
        Assert.Contains(reports, r => r.Stage == PrtgFetchService.PrtgSyncMessagesPhase);

        // 分母取 PRTG 回報的 treesize，分子是已讀取列數
        Assert.Contains(reports, r => r.Stage == PrtgFetchService.PrtgSyncDevicesPhase && r.Done == 1 && r.Total == 1);
        Assert.Contains(reports, r => r.Stage == PrtgFetchService.PrtgSyncSensorsPhase && r.Done == 2 && r.Total == 2);

        Assert.Contains(reports, r => r.Stage == PrtgFetchService.PrtgValuesPhase && r.Done == 2 && r.Total == 2);
    }

    /// <summary>
    /// 批次A：PRTG 沒有回報 treesize（或值不可用）時，分母維持 0（畫面顯示不定進度），
    /// 分子仍照常累加——不得因為缺少分母就整段不回報，那正是原本「準備中…」的成因。
    /// </summary>
    [Fact]
    public async Task FetchDayAsync_treesize缺失時分母為0但分子照常回報()
    {
        var devJson = "{\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"paused\":false}]}";
        var senJson = "{\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"S1\",\"paused\":true}]}";
        var msgJson = "{\"messages\":[]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse(devJson);
            if (url.Contains("content=sensors")) return JsonResponse(senJson);
            if (url.Contains("content=messages")) return JsonResponse(msgJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var reports = new List<(string Stage, int Done, int Total)>();
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, ScopeOf(101),
            syncStructure: true, fetchValues: false,
            progress: (stage, done, total) => reports.Add((stage, done, total)));

        Assert.Equal(0, result.Failures);
        Assert.Contains(reports, r => r.Stage == PrtgFetchService.PrtgSyncDevicesPhase && r.Done == 1 && r.Total == 0);
        Assert.Contains(reports, r => r.Stage == PrtgFetchService.PrtgSyncSensorsPhase && r.Done == 1 && r.Total == 1);
    }

    /// <summary>
    /// 批次A：messages 端點必須帶相對日期過濾 <c>filter_drel</c>，取「涵蓋得到目標日的最小級距」。
    /// 不帶它就是把整台 PRTG 的訊息歷史從頭翻到尾、再由用戶端丟掉 99%——實機環境要翻幾百頁。
    /// 刻意不用 today／yesterday：跨午夜的執行在階段 3 跑過零點時目標日會落在前天，那兩個級距會整段漏掉。
    /// </summary>
    [Theory]
    // 邊界一律往上跳一階：級距是滾動時間窗（7days＝now-7d 起算）而非日曆日。
    // 因 FetchDayAsync 需涵蓋目標日前一天（day - 1），最舊日期為 today - (daysAgo + 1)：
    // daysAgo = 1 -> fromDate 為 2 天前 -> 7days
    // daysAgo = 5 -> fromDate 為 6 天前 -> 7days
    // daysAgo = 6 -> fromDate 為 7 天前 -> 30days
    // daysAgo = 28 -> fromDate 為 29 天前 -> 30days
    // daysAgo = 29 -> fromDate 為 30 天前 -> 12months
    // daysAgo = 363 -> fromDate 為 364 天前 -> 12months
    [InlineData(1, "filter_drel=7days")]
    [InlineData(5, "filter_drel=7days")]
    [InlineData(6, "filter_drel=30days")]
    [InlineData(28, "filter_drel=30days")]
    [InlineData(29, "filter_drel=12months")]
    [InlineData(363, "filter_drel=12months")]
    public async Task FetchDayAsync_狀態變更查詢帶相對日期過濾(int daysAgo, string expectedFilter)
    {
        var messageUrls = new List<string>();
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
            {
                messageUrls.Add(url);
                return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            }
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());
        await service.FetchDayAsync(DateTime.Today.AddDays(-daysAgo), 2, CancellationToken.None, ScopeOf(1),
            syncStructure: true, fetchValues: false);

        var messageUrl = Assert.Single(messageUrls);
        Assert.Contains(expectedFilter, messageUrl);
        Assert.Contains("id=1", messageUrl);
    }

    /// <summary>
    /// 驗證 FetchStateChangesRangeAsync 依 fromDate 距今天數選擇最小涵蓋級距。
    /// 級距表與簽章不變：&lt;7 為 7days、&lt;30 為 30days、&lt;365 為 12months。
    /// </summary>
    [Theory]
    [InlineData(1, "filter_drel=7days")]
    [InlineData(6, "filter_drel=7days")]
    [InlineData(7, "filter_drel=30days")]
    [InlineData(29, "filter_drel=30days")]
    [InlineData(30, "filter_drel=12months")]
    [InlineData(364, "filter_drel=12months")]
    public async Task FetchStateChangesRangeAsync_查詢帶相對日期過濾(int daysAgo, string expectedFilter)
    {
        var messageUrls = new List<string>();
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
            {
                messageUrls.Add(url);
                return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());
        await service.FetchStateChangesRangeAsync(DateTime.Today.AddDays(-daysAgo), DateTime.Today, new long[] { 1 }, 1, CancellationToken.None);

        var messageUrl = Assert.Single(messageUrls);
        Assert.Contains(expectedFilter, messageUrl);
        Assert.Contains("id=1", messageUrl);
    }

    /// <summary>
    /// 批次A：目標日超過 12 個月（超出任何保留期的極端回填）時不帶級距參數，
    /// 但 <c>id=0</c> 仍在——不得組出 <c>id=0&amp;</c> 這種尾巴懸空的查詢字串。
    /// </summary>
    [Fact]
    public async Task FetchDayAsync_目標日超過一年時不帶相對日期過濾()
    {
        var messageUrls = new List<string>();
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
            {
                messageUrls.Add(url);
                return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            }
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());
        await service.FetchDayAsync(DateTime.Today.AddDays(-400), 2, CancellationToken.None, ScopeOf(1),
            syncStructure: true, fetchValues: false);

        var messageUrl = Assert.Single(messageUrls);
        Assert.DoesNotContain("filter_drel", messageUrl);
        Assert.Contains("id=1", messageUrl);
        Assert.DoesNotContain("id=1&&", messageUrl);
    }

    private static string BuildMessagePage(int count, Func<int, (long Objid, string DtStr, string Status, string Message)> rowGenerator, int? treesize = null)
    {
        var sb = new StringBuilder(count * 80 + 30);
        sb.Append('{');
        if (treesize.HasValue) sb.Append($"\"treesize\":{treesize.Value},");
        sb.Append("\"messages\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            var (objid, dtStr, status, msg) = rowGenerator(i);
            sb.Append($"{{\"objid\":{objid},\"datetime\":\"{dtStr}\",\"status\":\"{status}\",\"message\":\"{msg}\"}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    [Fact]
    public async Task 區間取法_依時間遞減且整頁早於門檻_提早停止且不發下一頁()
    {
        var fromDate = new DateTime(2026, 8, 30);
        var toDate = new DateTime(2026, 8, 31);

        // 第 1 頁（滿頁 5000 筆）：含 8/31 與 8/30 兩天的列，時間遞減
        var page1 = BuildMessagePage(PageSizeForFullPage, i =>
        {
            var dt = i < 2500
                ? new DateTime(2026, 8, 31, 23, 59, 59).AddSeconds(-i)
                : new DateTime(2026, 8, 30, 23, 59, 59).AddSeconds(-(i - 2500));
            return (10000 + i, dt.ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}");
        });

        // 第 2 頁（滿頁 5000 筆）：整頁早於 fromDate - 1天（2026-08-29 00:00:00），第一筆為 2026-08-28 23:59:59
        var page2 = BuildMessagePage(PageSizeForFullPage, i =>
        {
            var dt = new DateTime(2026, 8, 28, 23, 59, 59).AddSeconds(-i);
            return (20000 + i, dt.ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}");
        });

        // 第 3 頁：存在但不得被請求
        var page3 = BuildMessagePage(PageSizeForFullPage, i =>
        {
            var dt = new DateTime(2026, 8, 27, 23, 59, 59).AddSeconds(-i);
            return (30000 + i, dt.ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}");
        });

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
            {
                if (url.Contains("start=0")) return JsonResponse(page1);
                if (url.Contains("start=5000")) return JsonResponse(page2);
                if (url.Contains("start=10000")) return JsonResponse(page3);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchStateChangesRangeAsync(fromDate, toDate, new long[] { 1 }, 1, CancellationToken.None);

        Assert.Equal(2, handler.RequestedUrls.Count(u => u.Contains("content=messages")));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("start=10000"));
        Assert.True(result.StoppedEarly);
        Assert.True(result.Converged);
        Assert.Equal(2, result.Pages);
        Assert.Equal(5000, result.TotalWritten);
        Assert.Equal(2500, result.WrittenByDay[new DateTime(2026, 8, 30)]);
        Assert.Equal(2500, result.WrittenByDay[new DateTime(2026, 8, 31)]);
        Assert.False(result.WrittenByDay.ContainsKey(new DateTime(2026, 8, 28)));

        using var ctx = _fx.NewContext();
        Assert.Equal(5000, await ctx.PrtgStateChanges.CountAsync());
        Assert.False(await ctx.PrtgStateChanges.AnyAsync(r => r.ChangedAt.Date == new DateTime(2026, 8, 28)));
    }

    [Fact]
    public async Task 區間取法_頁內時間不單調_不提早停止()
    {
        var fromDate = new DateTime(2026, 8, 30);
        var toDate = new DateTime(2026, 8, 31);

        var page1 = BuildMessagePage(PageSizeForFullPage, i =>
        {
            var dt = new DateTime(2026, 8, 31, 23, 59, 59).AddSeconds(-i);
            return (10000 + i, dt.ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}");
        });

        // 第 2 頁整頁很舊，但在 index 50 有一處遞增
        var page2 = BuildMessagePage(PageSizeForFullPage, i =>
        {
            DateTime dt;
            if (i == 50)
                dt = new DateTime(2026, 8, 28, 10, 0, 0);
            else if (i == 51)
                dt = new DateTime(2026, 8, 28, 10, 5, 0); // 遞增！
            else
                dt = new DateTime(2026, 8, 28, 9, 0, 0).AddSeconds(-i);

            return (20000 + i, dt.ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}");
        });

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
            {
                if (url.Contains("start=0")) return JsonResponse(page1);
                if (url.Contains("start=5000")) return JsonResponse(page2);
                if (url.Contains("start=10000")) return JsonResponse("{\"messages\":[]}");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchStateChangesRangeAsync(fromDate, toDate, new long[] { 1 }, 1, CancellationToken.None);

        Assert.Contains(handler.RequestedUrls, u => u.Contains("start=10000"));
        Assert.False(result.StoppedEarly);
    }

    [Fact]
    public async Task 區間取法_頁內有時間無法解析_不提早停止()
    {
        var fromDate = new DateTime(2026, 8, 30);
        var toDate = new DateTime(2026, 8, 31);

        var page1 = BuildMessagePage(PageSizeForFullPage, i =>
        {
            var dt = new DateTime(2026, 8, 31, 23, 59, 59).AddSeconds(-i);
            return (10000 + i, dt.ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}");
        });

        // 第 2 頁夾一筆無法解析的時間
        var page2 = BuildMessagePage(PageSizeForFullPage, i =>
        {
            if (i == 50)
                return (20000 + i, "invalid-date", "Up", $"m{i}");

            var dt = new DateTime(2026, 8, 28, 23, 59, 59).AddSeconds(-i);
            return (20000 + i, dt.ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}");
        });

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
            {
                if (url.Contains("start=0")) return JsonResponse(page1);
                if (url.Contains("start=5000")) return JsonResponse(page2);
                if (url.Contains("start=10000")) return JsonResponse("{\"messages\":[]}");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchStateChangesRangeAsync(fromDate, toDate, new long[] { 1 }, 1, CancellationToken.None);

        Assert.Contains(handler.RequestedUrls, u => u.Contains("start=10000"));
        Assert.False(result.StoppedEarly);
    }

    [Fact]
    public async Task 區間取法_頁內有一筆晚於門檻_不提早停止()
    {
        var fromDate = new DateTime(2026, 8, 30);
        var toDate = new DateTime(2026, 8, 31);
        // 門檻為 fromDate - 1天 = 2026-08-29 00:00:00

        var page1 = BuildMessagePage(PageSizeForFullPage, i =>
        {
            var dt = new DateTime(2026, 8, 31, 23, 59, 59).AddSeconds(-i);
            return (10000 + i, dt.ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}");
        });

        // 第 2 頁第一筆恰在門檻當天 (2026-08-29 10:00:00，不早於門檻)
        var page2 = BuildMessagePage(PageSizeForFullPage, i =>
        {
            var dt = i == 0
                ? new DateTime(2026, 8, 29, 10, 0, 0)
                : new DateTime(2026, 8, 28, 23, 59, 59).AddSeconds(-i);
            return (20000 + i, dt.ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}");
        });

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
            {
                if (url.Contains("start=0")) return JsonResponse(page1);
                if (url.Contains("start=5000")) return JsonResponse(page2);
                if (url.Contains("start=10000")) return JsonResponse("{\"messages\":[]}");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchStateChangesRangeAsync(fromDate, toDate, new long[] { 1 }, 1, CancellationToken.None);

        Assert.Contains(handler.RequestedUrls, u => u.Contains("start=10000"));
        Assert.False(result.StoppedEarly);
    }

    [Fact]
    public async Task 區間取法_區間外的列不寫入且每日新增數正確()
    {
        var fromDate = new DateTime(2026, 8, 20);
        var toDate = new DateTime(2026, 8, 22);

        var msgJson = "{\"messages\":[" +
                      "{\"objid\":501,\"datetime\":\"2026-08-23 10:00:00\",\"status\":\"Down\",\"message\":\"After toDate\"}," +
                      "{\"objid\":502,\"datetime\":\"2026-08-22 15:00:00\",\"status\":\"Up\",\"message\":\"On toDate 1\"}," +
                      "{\"objid\":503,\"datetime\":\"2026-08-22 16:00:00\",\"status\":\"Warning\",\"message\":\"On toDate 2\"}," +
                      "{\"objid\":504,\"datetime\":\"2026-08-21 08:00:00\",\"status\":\"Down\",\"message\":\"Inside range\"}," +
                      "{\"objid\":505,\"datetime\":\"2026-08-20 23:59:59\",\"status\":\"Up\",\"message\":\"On fromDate\"}," +
                      "{\"objid\":506,\"datetime\":\"2026-08-19 23:59:59\",\"status\":\"Down\",\"message\":\"Before fromDate\"}" +
                      "]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
                return url.Contains("start=0") ? JsonResponse(msgJson) : JsonResponse("{\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchStateChangesRangeAsync(fromDate, toDate, new long[] { 1 }, 1, CancellationToken.None);

        Assert.Equal(4, result.TotalWritten);
        Assert.Equal(2, result.WrittenByDay[new DateTime(2026, 8, 22)]);
        Assert.Equal(1, result.WrittenByDay[new DateTime(2026, 8, 21)]);
        Assert.Equal(1, result.WrittenByDay[new DateTime(2026, 8, 20)]);
        Assert.False(result.WrittenByDay.ContainsKey(new DateTime(2026, 8, 23)));
        Assert.False(result.WrittenByDay.ContainsKey(new DateTime(2026, 8, 19)));

        using var ctx = _fx.NewContext();
        var rows = await ctx.PrtgStateChanges.ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(rows, r => r.SensorObjid == 501);
        Assert.DoesNotContain(rows, r => r.SensorObjid == 506);
    }

    [Fact]
    public async Task 區間取法_重跑同區間_新增數為0且摘要註明其餘已存在()
    {
        var fromDate = new DateTime(2026, 8, 20);
        var toDate = new DateTime(2026, 8, 21);

        var msgJson = "{\"messages\":[" +
                      "{\"objid\":601,\"datetime\":\"2026-08-20 10:00:00\",\"status\":\"Down\",\"message\":\"m1\"}," +
                      "{\"objid\":602,\"datetime\":\"2026-08-21 11:00:00\",\"status\":\"Up\",\"message\":\"m2\"}" +
                      "]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
                return url.Contains("start=0") ? JsonResponse(msgJson) : JsonResponse("{\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        // 第一次執行：新增 2 筆
        var res1 = await service.FetchStateChangesRangeAsync(fromDate, toDate, new long[] { 1 }, 1, CancellationToken.None);
        Assert.Equal(2, res1.TotalWritten);

        // 第二次執行（同一區間重跑）：新增 0 筆
        var res2 = await service.FetchStateChangesRangeAsync(fromDate, toDate, new long[] { 1 }, 1, CancellationToken.None);
        Assert.Equal(0, res2.TotalWritten);
        Assert.Equal(0, res2.WrittenByDay[new DateTime(2026, 8, 20)]);
        Assert.Equal(0, res2.WrittenByDay[new DateTime(2026, 8, 21)]);
        Assert.Contains(console.Lines, l => l.Contains("新增 0 筆") && l.Contains("其餘已存在"));
    }

    [Fact]
    public async Task FetchDayAsync_狀態變更寫入目標日前一天到今天()
    {
        var day = DateTime.Today.AddDays(-2);

        // 目標日前兩天 (day - 2)、前一天 (day - 1)、當天 (day)、今天 (Today)、明天 (Today + 1)
        var msgJson = "{\"messages\":[" +
                      $"{{\"objid\":701,\"datetime\":\"{day.AddDays(-2):yyyy-MM-dd 12:00:00}\",\"status\":\"Down\",\"message\":\"day-2\"}}," +
                      $"{{\"objid\":702,\"datetime\":\"{day.AddDays(-1):yyyy-MM-dd 12:00:00}\",\"status\":\"Warning\",\"message\":\"day-1\"}}," +
                      $"{{\"objid\":703,\"datetime\":\"{day:yyyy-MM-dd 12:00:00}\",\"status\":\"Up\",\"message\":\"day\"}}," +
                      $"{{\"objid\":704,\"datetime\":\"{DateTime.Today:yyyy-MM-dd 08:00:00}\",\"status\":\"Up\",\"message\":\"today\"}}," +
                      $"{{\"objid\":705,\"datetime\":\"{DateTime.Today.AddDays(1):yyyy-MM-dd 08:00:00}\",\"status\":\"Down\",\"message\":\"tomorrow\"}}" +
                      "]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return url.Contains("start=0") ? JsonResponse(msgJson) : JsonResponse("{\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None, ScopeOf(1), syncStructure: true, fetchValues: false);

        Assert.Equal(0, result.Failures);
        // 目標日前兩天 (701) 與明天 (705) 不寫；前一天 (702)、目標日 (703)、今天 (704) 寫入
        Assert.Equal(3, result.StateChanges);

        using var ctx = _fx.NewContext();
        var rows = await ctx.PrtgStateChanges.ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.SensorObjid == 702);
        Assert.Contains(rows, r => r.SensorObjid == 703);
        Assert.Contains(rows, r => r.SensorObjid == 704);
        Assert.DoesNotContain(rows, r => r.SensorObjid == 701);
        Assert.DoesNotContain(rows, r => r.SensorObjid == 705);
    }

    // ── 狀態變更逐裝置查詢（階段 3）──

    private static string MessagesJson(params (long Objid, DateTime At)[] rows) =>
        "{\"messages\":[" + string.Join(",", rows.Select(r =>
            $"{{\"objid\":{r.Objid},\"datetime\":\"{r.At:yyyy-MM-dd HH:mm:ss}\",\"status\":\"Down\",\"message\":\"m\"}}")) + "]}";

    /// <summary>messages 替身：依 <c>id=</c>（整段比對）路由到各裝置的第一頁內容，其餘頁回空。</summary>
    private static (PrtgClient Client, StubHandler Handler) CreateMessagesClient(
        IReadOnlyDictionary<long, string> firstPageByDevice, long? failingDevice = null)
    {
        return CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages"))
            {
                var id = IdQuery(url);
                if (id.HasValue && id == failingDevice) throw new HttpRequestException("模擬連線失敗");
                if (id.HasValue && url.Contains("start=0&") && firstPageByDevice.TryGetValue(id.Value, out var page))
                    return JsonResponse(page);
                return JsonResponse("{\"messages\":[]}");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task 狀態變更_範圍兩台_逐台帶id查詢且不查id0也不帶parent欄位()
    {
        var today = DateTime.Today;
        var (client, handler) = CreateMessagesClient(new Dictionary<long, string>
        {
            [1] = MessagesJson((201, today.AddHours(1))),
            [2] = MessagesJson((202, today.AddHours(2))),
        });
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());

        var result = await service.FetchDayAsync(today.AddDays(-1), 2, CancellationToken.None, ScopeOf(1, 2), fetchValues: false);

        var messageUrls = handler.RequestedUrls.Where(u => u.Contains("content=messages")).ToList();
        Assert.Single(messageUrls, u => HasIdQuery(u, 1));
        Assert.Single(messageUrls, u => HasIdQuery(u, 2));
        Assert.Equal(2, messageUrls.Count);
        Assert.DoesNotContain(handler.RequestedUrls, u => HasIdQuery(u, 0));
        Assert.DoesNotContain(messageUrls, u => u.Contains("parent"));
        Assert.Equal(2, result.StateChanges);
        Assert.Equal(0, result.Failures);
    }

    [Fact]
    public async Task 狀態變更_範圍為空_不發messages請求且不計失敗()
    {
        var (client, handler) = CreateMessagesClient(new Dictionary<long, string>());
        var console = new TestConsole();
        var service = new PrtgFetchService(client, CreateStore(), console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(DateTime.Today.AddDays(-1), 2, CancellationToken.None, NoScope, fetchValues: false);

        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("content=messages"));
        Assert.Equal(0, result.Failures);
        Assert.Contains("[階段 3/4] 取數範圍內沒有任何裝置，略過狀態變更同步。", console.Lines);
    }

    [Fact]
    public async Task 狀態變更_範圍計算失敗_不發messages請求且只計一次失敗()
    {
        var (client, handler) = CreateMessagesClient(new Dictionary<long, string>());
        var console = new TestConsole();
        var service = new PrtgFetchService(client, CreateStore(), console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(DateTime.Today.AddDays(-1), 2, CancellationToken.None,
            _ => throw new InvalidOperationException("模擬範圍計算失敗"), fetchValues: false);

        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("content=messages"));
        // 只有範圍計算那一次失敗；階段 3 不重複計
        Assert.Equal(1, result.Failures);
        Assert.Contains("[階段 3/4] 取數範圍無法取得，略過狀態變更同步。", console.Lines);

        // 直接呼叫傳 null 也一樣：零請求、視為收斂
        var direct = await service.FetchStateChangesRangeAsync(DateTime.Today.AddDays(-1), DateTime.Today, null, 2, CancellationToken.None);
        Assert.True(direct.Converged);
        Assert.Equal(0, direct.QueriedObjects);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("content=messages"));
    }

    [Fact]
    public async Task 狀態變更_一台逾時_另一台照寫且記為失敗並列出明細()
    {
        var today = DateTime.Today;
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
            {
                // 逾時：替身擲 TaskCanceledException，但測試的 ct 並未取消
                if (HasIdQuery(url, 2)) throw new TaskCanceledException("模擬逾時");
                return JsonResponse(url.Contains("start=0&") ? MessagesJson((201, today.AddHours(1))) : "{\"messages\":[]}");
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var store = CreateStore();
        store.UpsertDevices(new List<PrtgDeviceRow> { new() { Objid = 2, Name = "Dev-Two" } }, DateTime.Now);
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchStateChangesRangeAsync(today.AddDays(-1), today, new long[] { 1, 2 }, 2, CancellationToken.None);

        Assert.False(result.Converged);
        Assert.Equal(1, result.FailedObjects);
        Assert.Equal(2, result.QueriedObjects);
        Assert.Equal(1, result.TotalWritten);
        Assert.Equal("1 台裝置的狀態變更取得失敗。", result.Error);
        Assert.Contains(console.Lines, l => l.StartsWith("[階段 3/4] ✗ 1 台裝置的狀態變更取得失敗：") && l.Contains("Dev-Two(2)"));
        using var ctx = _fx.NewContext();
        Assert.Contains(ctx.PrtgStateChanges, r => r.SensorObjid == 201);
    }

    [Fact]
    public async Task 狀態變更_一台連線失敗_FetchDayAsync失敗數加一()
    {
        var today = DateTime.Today;
        var (client, _) = CreateMessagesClient(new Dictionary<long, string>
        {
            [1] = MessagesJson((201, today.AddHours(1))),
        }, failingDevice: 2);
        var store = CreateStore();
        store.UpsertDevices(new List<PrtgDeviceRow> { new() { Objid = 2, Name = "Dev-Two" } }, DateTime.Now);
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(today.AddDays(-1), 2, CancellationToken.None, ScopeOf(1, 2), fetchValues: false);

        // 階段 1、2 無失敗（裝置表空、感測器空），唯一的失敗來自階段 3
        Assert.Equal(1, result.Failures);
        Assert.Equal(1, result.StateChanges);
        Assert.Contains(console.Lines, l => l.StartsWith("[階段 3/4] ✗ 1 台裝置的狀態變更取得失敗：Dev-Two(2)"));
    }

    [Fact]
    public async Task 狀態變更_全部零筆時印出9d5提示_有一台非空就不印()
    {
        var today = DateTime.Today;
        var (emptyClient, _) = CreateMessagesClient(new Dictionary<long, string>());
        var emptyConsole = new TestConsole();
        var emptyService = new PrtgFetchService(emptyClient, CreateStore(), emptyConsole, new Dictionary<string, string>());
        var emptyResult = await emptyService.FetchStateChangesRangeAsync(today.AddDays(-1), today, new long[] { 1, 2 }, 2, CancellationToken.None);
        Assert.Contains(emptyConsole.Lines, l => l.Contains("9d-5") && l.Contains("範圍內 2 台裝置近期都沒有任何狀態變更"));
        Assert.Equal(2, emptyResult.EmptyObjects);

        var (someClient, _) = CreateMessagesClient(new Dictionary<long, string>
        {
            [2] = MessagesJson((202, today.AddHours(1))),
        });
        var someConsole = new TestConsole();
        var someService = new PrtgFetchService(someClient, CreateStore(), someConsole, new Dictionary<string, string>());
        var someResult = await someService.FetchStateChangesRangeAsync(today.AddDays(-1), today, new long[] { 1, 2 }, 2, CancellationToken.None);
        Assert.DoesNotContain(someConsole.Lines, l => l.Contains("9d-5"));
        Assert.Equal(1, someResult.EmptyObjects);
    }

    [Fact]
    public async Task 狀態變更_提早停止狀態不跨物件共用()
    {
        var fromDate = new DateTime(2026, 8, 30);
        var toDate = new DateTime(2026, 8, 31);
        // 裝置 1 第一頁：滿頁且整頁早於門檻（2026-08-29 00:00）→ 提早停止
        var oldPage = BuildMessagePage(PageSizeForFullPage, i =>
            (10000 + i, new DateTime(2026, 8, 28, 23, 59, 59).AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}"));
        // 裝置 2 第一頁：滿頁且在區間內 → 不停，必須翻第二頁
        var recentPage = BuildMessagePage(PageSizeForFullPage, i =>
            (20000 + i, new DateTime(2026, 8, 31, 23, 59, 59).AddSeconds(-i).ToString("yyyy-MM-dd HH:mm:ss"), "Up", $"m{i}"));

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (!url.Contains("content=messages")) return JsonResponse("{}", HttpStatusCode.NotFound);
            if (url.Contains("start=0&"))
                return JsonResponse(HasIdQuery(url, 1) ? oldPage : recentPage);
            return JsonResponse("{\"messages\":[]}");
        });
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());

        // 併發 1：裝置 1 一定先跑完，它的提早停止若外洩就會讓裝置 2 少翻一頁
        var result = await service.FetchStateChangesRangeAsync(fromDate, toDate, new long[] { 1, 2 }, 1, CancellationToken.None);

        Assert.True(result.StoppedEarly);
        Assert.DoesNotContain(handler.RequestedUrls, u => HasIdQuery(u, 1) && u.Contains("start=5000"));
        Assert.Contains(handler.RequestedUrls, u => HasIdQuery(u, 2) && u.Contains("start=5000"));
    }

    [Fact]
    public async Task 狀態變更_守門忙碌時放行前不發messages請求()
    {
        var today = DateTime.Today;
        var (client, handler) = CreateMessagesClient(new Dictionary<long, string>
        {
            [1] = MessagesJson((201, today.AddHours(1))),
        });

        // 守門自己的 client：第一次讀到 CPU 95%（超標、strikes=1 → 進入暫停），之後回落
        var guardCalls = 0;
        var (guardClient, _) = CreateClient(_ =>
        {
            var busy = Interlocked.Increment(ref guardCalls) == 1;
            return JsonResponse($"{{\"sensors\":[{{\"objid\":9001,\"device\":\"PRTG\",\"sensor\":\"CPU Load\",\"status\":\"Up\",\"lastvalue\":\"{(busy ? "95 %" : "50 %")}\"}}]}}");
        });
        var settings = new SystemSettings
        {
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardStrikes = 1,
            PrtgResourceGuardCpuPercent = 80,
            PrtgResourceGuardCheckSeconds = 0,
            PrtgResourceGuardPauseMinutes = 5
        };
        var recorder = new BatchRunRecorder(new LogForesight.Core.Persistence.BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")), "test-host", Array.Empty<string>());
        int? messagesSeenDuringPause = null;
        using var guard = new PrtgResourceGuard(guardClient, settings,
            new PrtgResourceGuardTargetResult(new long[] { 9001 }, new Dictionary<long, string> { [9001] = "cpu" }),
            recorder, new TestConsole(), null)
        {
            DelayAsync = (_, _) =>
            {
                messagesSeenDuringPause ??= handler.RequestedUrls.Count(u => u.Contains("content=messages"));
                return Task.CompletedTask;
            }
        };
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>(), guard);

        var result = await service.FetchStateChangesRangeAsync(today.AddDays(-1), today, new long[] { 1 }, 1, CancellationToken.None);

        Assert.Equal(0, messagesSeenDuringPause);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("content=messages") && HasIdQuery(u, 1));
        Assert.Equal(1, result.TotalWritten);
    }

    [Fact]
    public async Task 狀態變更_感測器不在鏡像中_仍寫入並提示()
    {
        var today = DateTime.Today;
        var (client, _) = CreateMessagesClient(new Dictionary<long, string>
        {
            [1] = MessagesJson((201, today.AddHours(1)), (777, today.AddHours(2))),
        });
        var store = CreateStore();
        SeedOldSensors(store, (201, 1));
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchStateChangesRangeAsync(today.AddDays(-1), today, new long[] { 1 }, 1, CancellationToken.None);

        Assert.Equal(2, result.TotalWritten);
        Assert.Contains(console.Lines, l => l.Contains("其中 1 顆感測器尚未在鏡像中"));
        using var ctx = _fx.NewContext();
        Assert.Contains(ctx.PrtgStateChanges, r => r.SensorObjid == 777);
    }

    // ── 取數範圍提供者（scopeProvider）的呼叫時機與引數 ──

    private const string ScopeMark = "<<SCOPE>>";

    /// <summary>最小結構樹；devicesMode：ok＝一台裝置、empty＝空陣列、error＝回 500。</summary>
    private static (PrtgClient Client, StubHandler Handler) CreateScopeClient(string devicesMode)
    {
        return CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
            {
                return devicesMode switch
                {
                    "error" => JsonResponse("{}", HttpStatusCode.InternalServerError),
                    "empty" => JsonResponse("{\"treesize\":0,\"devices\":[]}"),
                    _ => url.Contains("start=0")
                        ? JsonResponse("{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"S1\",\"host\":\"10.0.0.1\",\"group\":\"G\"}]}")
                        : JsonResponse("{\"treesize\":1,\"devices\":[]}")
                };
            }
            if (url.Contains("content=sensors"))
                return url.Contains("start=0")
                    ? JsonResponse("{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU\",\"type\":\"wmicpu\",\"status\":\"Up\"}]}")
                    : JsonResponse("{\"treesize\":1,\"sensors\":[]}");
            if (url.Contains("content=messages"))
                return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            if (url.Contains("historicdata.json"))
                return JsonResponse("{\"histdata\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
    }

    private static Func<bool, PrtgScopeResult> MarkingScope(StubHandler handler, List<bool> received) => refreshed =>
    {
        received.Add(refreshed);
        lock (handler.RequestedUrls) handler.RequestedUrls.Add(ScopeMark);
        return new PrtgScopeResult(new HashSet<long> { 101 }, 1, 0, 0, 0);
    };

    [Fact]
    public async Task FetchDayAsync_範圍提供者在裝置同步之後_感測器同步之前呼叫恰一次()
    {
        var (client, handler) = CreateScopeClient("ok");
        var console = new TestConsole();
        var service = new PrtgFetchService(client, CreateStore(), console, new Dictionary<string, string>());
        var received = new List<bool>();

        await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, MarkingScope(handler, received));

        var seq = handler.RequestedUrls.ToList();
        Assert.Single(seq, u => u == ScopeMark);
        var mark = seq.IndexOf(ScopeMark);
        var lastDevices = seq.FindLastIndex(u => u.Contains("content=devices"));
        var firstSensors = seq.FindIndex(u => u.Contains("content=sensors"));
        Assert.True(lastDevices >= 0 && firstSensors >= 0);
        Assert.True(lastDevices < mark, "標記要在最後一個 devices 請求之後");
        Assert.True(mark < firstSensors, "標記要在第一個 sensors 請求之前");
        Assert.Contains(console.Lines, l => l.Contains("[範圍] 取數範圍：1 台裝置（對應 1、衝突 0、人工 0、守門 0）"));
    }

    [Fact]
    public async Task FetchDayAsync_不同步結構時範圍提供者在狀態變更之前呼叫恰一次且收到false()
    {
        var (client, handler) = CreateScopeClient("ok");
        var store = CreateStore();
        var service = new PrtgFetchService(client, store, new TestConsole(), new Dictionary<string, string>());
        // 先跑一次讓鏡像有感測器（syncStructure:false 需要既有鏡像）
        await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, ScopeOf(101));
        lock (handler.RequestedUrls) handler.RequestedUrls.Clear();

        var received = new List<bool>();
        await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, MarkingScope(handler, received), syncStructure: false);

        var seq = handler.RequestedUrls.ToList();
        Assert.Single(seq, u => u == ScopeMark);
        var firstMessages = seq.FindIndex(u => u.Contains("content=messages"));
        Assert.True(firstMessages >= 0);
        Assert.True(seq.IndexOf(ScopeMark) < firstMessages, "標記要在第一個 messages 請求之前");
        Assert.Equal(new[] { false }, received);
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("error", false)]
    [InlineData("empty", false)]
    public async Task FetchDayAsync_範圍提供者收到的devicesRefreshed依裝置同步結果(string devicesMode, bool expected)
    {
        var (client, handler) = CreateScopeClient(devicesMode);
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());
        var received = new List<bool>();

        await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, MarkingScope(handler, received));

        Assert.Equal(new[] { expected }, received);
    }

    [Fact]
    public async Task FetchDayAsync_範圍提供者擲例外_失敗加一且其餘階段照常()
    {
        var (client, handler) = CreateScopeClient("ok");
        var console = new TestConsole();
        var service = new PrtgFetchService(client, CreateStore(), console, new Dictionary<string, string>());

        var baseline = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, NoScope);
        lock (handler.RequestedUrls) handler.RequestedUrls.Clear();

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None,
            _ => throw new InvalidOperationException("範圍壞了"));

        Assert.Equal(baseline.Failures + 1, result.Failures);
        Assert.Contains(console.Lines, l => l.Contains("取數範圍計算失敗") && l.Contains("範圍壞了"));
        var seq = handler.RequestedUrls.ToList();
        Assert.DoesNotContain(seq, u => u.Contains("content=sensors"));
        // 範圍無法取得時階段 3 也略過（逐裝置查詢沒有可查的物件），且不重複計失敗
        Assert.DoesNotContain(seq, u => u.Contains("content=messages"));
    }

    // ── 過期裝置清除（階段 1 之後、取數範圍之前）──

    /// <summary>預放裝置鏡像，SyncedAt 設在過去，模擬上一趟同步留下的列。</summary>
    private static void SeedOldDevices(EfPrtgStore store, IEnumerable<long> objids)
    {
        var rows = objids.Select(id => new PrtgDeviceRow { Objid = id, Name = $"Old-{id}", GroupPath = "G" }).ToList();
        store.UpsertDevices(rows, DateTime.Now.AddDays(-1));
    }

    /// <summary>devices 回指定 objid、sensors 與 messages 皆空的替身。</summary>
    private static PrtgClient CreateDevicesOnlyClient(IEnumerable<long> objids)
    {
        var devJson = "{\"devices\":[" +
                      string.Join(",", objids.Select(id => $"{{\"objid\":{id},\"device\":\"Dev-{id}\",\"paused\":false}}")) +
                      "]}";
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return JsonResponse(url.Contains("start=0") ? devJson : "{\"devices\":[]}");
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        return client;
    }

    [Fact]
    public async Task FetchDayAsync_清除PRTG端已不存在的裝置且在取數範圍之前()
    {
        var store = CreateStore();
        SeedOldDevices(store, new long[] { 101, 102, 99 });
        var console = new TestConsole();
        var service = new PrtgFetchService(CreateDevicesOnlyClient(new long[] { 101, 102 }), store, console,
            new Dictionary<string, string>());

        int? devicesSeenByProvider = null;
        PrtgScopeResult Provider(bool _)
        {
            devicesSeenByProvider = store.GetAllDevices().Count;
            return new PrtgScopeResult(new HashSet<long>(), 0, 0, 0, 0);
        }

        var result = await service.FetchDayAsync(new DateTime(2026, 9, 17), 1, CancellationToken.None, Provider,
            fetchValues: false);

        Assert.Equal(0, result.Failures);
        var remaining = store.GetAllDevices().Select(d => d.Objid).OrderBy(id => id).ToList();
        Assert.Equal(new long[] { 101, 102 }, remaining);
        Assert.Equal(2, devicesSeenByProvider);
        Assert.Contains(console.Lines, l => l.Contains("[階段 1/4] 已清除 1 台 PRTG 端已不存在的裝置。"));
    }

    [Fact]
    public async Task FetchDayAsync_devices端點失敗時不清除過期裝置()
    {
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return JsonResponse("{\"error\":\"Internal error\"}", HttpStatusCode.InternalServerError);
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var store = CreateStore();
        SeedOldDevices(store, new long[] { 101, 102, 99 });
        var service = new PrtgFetchService(client, store, new TestConsole(), new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 9, 17), 1, CancellationToken.None, NoScope,
            fetchValues: false);

        Assert.True(result.Failures > 0);
        Assert.Equal(3, store.GetAllDevices().Count);
    }

    [Fact]
    public async Task FetchDayAsync_devices分頁未收斂時不清除過期裝置()
    {
        // 未收斂＝翻到頁數上限仍是滿頁且都是新列。沒有 objid 的列不會被去重、也不會寫入（mapper 回 null），
        // 用它填滿每一頁，只花解析成本就能讓分頁器翻到上限；第一頁另放 2 台真實裝置讓寫入數大於 0，
        // 確保擋下清除的是「未收斂」這個條件，而不是「寫入數為 0」。
        var filler = string.Join(",", Enumerable.Repeat("{}", PageSizeForFullPage - 2));
        var firstPage = "{\"devices\":[{\"objid\":101,\"device\":\"Dev-101\"},{\"objid\":102,\"device\":\"Dev-102\"}," + filler + "]}";
        var otherPage = "{\"devices\":[" + string.Join(",", Enumerable.Repeat("{}", PageSizeForFullPage)) + "]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return JsonResponse(url.Contains("start=0&") ? firstPage : otherPage);
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var store = CreateStore();
        SeedOldDevices(store, new long[] { 99 });
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 9, 17), 1, CancellationToken.None, NoScope,
            fetchValues: false);

        Assert.Equal(2, result.Devices);
        Assert.True(result.Failures > 0);
        Assert.Contains(console.Lines, l => l.Contains("鏡像不完整"));
        Assert.Contains(store.GetAllDevices(), d => d.Objid == 99);
        Assert.Equal(3, store.GetAllDevices().Count);
    }

    [Fact]
    public async Task FetchDayAsync_本趟未出現的裝置過半時不清除且不計失敗()
    {
        var store = CreateStore();
        SeedOldDevices(store, Enumerable.Range(1, 10).Select(i => (long)i));
        var console = new TestConsole();
        var service = new PrtgFetchService(CreateDevicesOnlyClient(new long[] { 1, 2, 3, 4 }), store, console,
            new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 9, 17), 1, CancellationToken.None, NoScope,
            fetchValues: false);

        Assert.Equal(0, result.Failures);
        Assert.Equal(10, store.GetAllDevices().Count);
        Assert.Contains(console.Lines, l => l.Contains("有 6 台裝置（超過鏡像 10 台的一半）") && l.Contains("本趟不清除"));
    }

    private static PrtgScopeResult NoScope(bool _) => new(new HashSet<long>(), 0, 0, 0, 0);

    /// <summary>取數範圍＝指定裝置集合（階段 2 只同步這些裝置的感測器）。</summary>
    private static Func<bool, PrtgScopeResult> ScopeOf(params long[] ids) =>
        _ => new PrtgScopeResult(ids.ToHashSet(), ids.Length, 0, 0, 0);

    /// <summary>URL 是否帶 <c>id={id}</c>（整段比對，避免 id=1 誤中 id=10）。</summary>
    private static bool HasIdQuery(string url, long id) =>
        System.Text.RegularExpressions.Regex.IsMatch(url, $@"[?&]id={id}(&|$)");

    private static long? IdQuery(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]id=(\d+)(&|$)");
        return m.Success ? long.Parse(m.Groups[1].Value) : null;
    }

    // ── 感測器依取數範圍同步與清除（階段 2）──

    private sealed record FakeSensor(long Objid, long Device, string Name, bool Paused = false);

    private static string SensorsJson(IReadOnlyCollection<FakeSensor> sensors) =>
        $"{{\"treesize\":{sensors.Count},\"sensors\":[" +
        string.Join(",", sensors.Select(s =>
            $"{{\"objid\":{s.Objid},\"parentid\":{s.Device},\"sensor\":\"{s.Name}\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":{(s.Paused ? "true" : "false")}}}")) +
        "]}";

    /// <summary>
    /// 感測器替身：帶 <c>id=</c> 的 sensors 請求只回該裝置的感測器（failingDevice 回 500）；
    /// 不帶 <c>id=</c> 的回全部感測器。devices、messages 回空；historicdata 回一筆。
    /// </summary>
    private static (PrtgClient Client, StubHandler Handler) CreateSensorScopeClient(
        IReadOnlyList<FakeSensor> sensors, long? failingDevice = null)
    {
        return CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices")) return JsonResponse("{\"treesize\":0,\"devices\":[]}");
            if (url.Contains("content=sensors"))
            {
                var id = IdQuery(url);
                if (id.HasValue && id == failingDevice)
                    return JsonResponse("{\"error\":\"Internal error\"}", HttpStatusCode.InternalServerError);
                var rows = id.HasValue ? sensors.Where(s => s.Device == id.Value).ToList() : sensors.ToList();
                return JsonResponse(url.Contains("start=0&") ? SensorsJson(rows) : SensorsJson(Array.Empty<FakeSensor>()));
            }
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            if (url.Contains("historicdata"))
                return JsonResponse("{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
    }

    /// <summary>預放感測器鏡像，SyncedAt 設在過去，模擬上一趟同步留下的列。</summary>
    private static void SeedOldSensors(EfPrtgStore store, params (long Objid, long Device)[] sensors)
    {
        store.UpsertSensors(sensors.Select(s => new PrtgSensorRow
        {
            Objid = s.Objid,
            DeviceObjid = s.Device,
            Name = $"Old-{s.Objid}",
            SensorType = "ping"
        }).ToList(), DateTime.Now.AddDays(-1));
    }

    private List<long> MirrorSensorObjids()
    {
        using var ctx = _fx.NewContext();
        return ctx.PrtgSensors.Select(s => s.Objid).OrderBy(id => id).ToList();
    }

    private static readonly FakeSensor[] TwoDeviceSensors =
    {
        new(201, 1, "D1-A"),
        new(202, 2, "D2-A"),
    };

    [Fact]
    public async Task 感測器同步_範圍不超過門檻時逐台帶id查詢且沒有全站查詢()
    {
        var (client, handler) = CreateSensorScopeClient(TwoDeviceSensors);
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, ScopeOf(1, 2),
            fetchValues: false);

        var sensorUrls = handler.RequestedUrls.Where(u => u.Contains("content=sensors")).ToList();
        Assert.Equal(2, sensorUrls.Count);
        Assert.Single(sensorUrls, u => HasIdQuery(u, 1));
        Assert.Single(sensorUrls, u => HasIdQuery(u, 2));
        Assert.DoesNotContain(sensorUrls, u => IdQuery(u) == null);
        Assert.Equal(2, result.Sensors);
        Assert.Equal(0, result.Failures);
    }

    [Fact]
    public async Task 感測器同步_清除範圍外與PRTG端已不存在的感測器()
    {
        var store = CreateStore();
        // 301：範圍外裝置 3；299：裝置 1 但 PRTG 這次沒回
        SeedOldSensors(store, (301, 3), (299, 1));
        var console = new TestConsole();
        var (client, _) = CreateSensorScopeClient(TwoDeviceSensors);
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, ScopeOf(1, 2),
            fetchValues: false);

        Assert.Equal(0, result.Failures);
        Assert.Equal(new long[] { 201, 202 }, MirrorSensorObjids());
        Assert.Contains(console.Lines, l => l.Contains("[階段 2/4] 已清除 2 個範圍外或 PRTG 端已不存在的感測器。"));
    }

    [Fact]
    public async Task 感測器同步_範圍為空時不抓不清除()
    {
        var store = CreateStore();
        SeedOldSensors(store, (301, 3));
        var console = new TestConsole();
        var (client, handler) = CreateSensorScopeClient(TwoDeviceSensors);
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, NoScope,
            fetchValues: false);

        Assert.Equal(0, result.Failures);
        Assert.Equal(new long[] { 301 }, MirrorSensorObjids());
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("content=sensors"));
        Assert.Contains(console.Lines, l => l.Contains("[階段 2/4] 取數範圍內沒有任何裝置，略過感測器同步。"));
    }

    [Fact]
    public async Task 感測器同步_範圍提供者擲例外時不抓不清除()
    {
        var store = CreateStore();
        SeedOldSensors(store, (301, 3));
        var console = new TestConsole();
        var (client, handler) = CreateSensorScopeClient(TwoDeviceSensors);
        var service = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None,
            _ => throw new InvalidOperationException("範圍壞了"), fetchValues: false);

        // 只有範圍計算失敗那一次
        Assert.Equal(1, result.Failures);
        Assert.Equal(new long[] { 301 }, MirrorSensorObjids());
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("content=sensors"));
        Assert.Contains(console.Lines, l => l.Contains("[階段 2/4] 取數範圍無法取得，略過感測器同步。"));
    }

    [Fact]
    public async Task 感測器同步_有一台取得失敗時不清除()
    {
        var store = CreateStore();
        SeedOldSensors(store, (301, 3));
        var (client, _) = CreateSensorScopeClient(TwoDeviceSensors, failingDevice: 2);
        var service = new PrtgFetchService(client, store, new TestConsole(), new Dictionary<string, string>());

        await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, ScopeOf(1, 2),
            fetchValues: false);

        Assert.Contains(301L, MirrorSensorObjids());
    }

    [Fact]
    public async Task 感測器同步_範圍內全部零感測器時不清除()
    {
        var store = CreateStore();
        SeedOldSensors(store, (301, 3));
        var (client, _) = CreateSensorScopeClient(Array.Empty<FakeSensor>());
        var service = new PrtgFetchService(client, store, new TestConsole(), new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, ScopeOf(1, 2),
            fetchValues: false);

        Assert.Equal(0, result.Sensors);
        Assert.Equal(new long[] { 301 }, MirrorSensorObjids());
    }

    [Fact]
    public async Task 感測器同步_單台失敗時其餘照寫且失敗只加一()
    {
        var console = new TestConsole();
        var (client, _) = CreateSensorScopeClient(TwoDeviceSensors, failingDevice: 2);
        var service = new PrtgFetchService(client, CreateStore(), console, new Dictionary<string, string>());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, ScopeOf(1, 2),
            fetchValues: false);

        // 其餘階段（裝置空表、狀態變更空表）都不會失敗，失敗數就是階段 2 的那一次
        Assert.Equal(1, result.Failures);
        Assert.Equal(1, result.Sensors);
        Assert.Equal(new long[] { 201 }, MirrorSensorObjids());
        Assert.Contains(console.Lines, l => l.Contains("1 台裝置的感測器取得失敗") && l.Contains("：2"));
    }

    [Fact]
    public async Task 感測器同步_範圍超過門檻時全站分頁一次且只寫範圍內()
    {
        var (client, handler) = CreateSensorScopeClient(new FakeSensor[] { new(201, 1, "D1-A"), new(901, 9999, "Out") });
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());
        var scope = Enumerable.Range(1, 501).Select(i => (long)i).ToArray();

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, ScopeOf(scope),
            fetchValues: false);

        var sensorUrls = handler.RequestedUrls.Where(u => u.Contains("content=sensors")).ToList();
        Assert.Single(sensorUrls);
        Assert.Null(IdQuery(sensorUrls[0]));
        Assert.Equal(new long[] { 201 }, MirrorSensorObjids());
        Assert.Equal(1, result.Sensors);
    }

    [Fact]
    public async Task 感測器同步_逐台與全站兩種取法寫進鏡像的內容相同()
    {
        var sensors = new FakeSensor[]
        {
            new(201, 1, "D1-A"),
            new(202, 1, "D1-B", Paused: true),
            new(203, 2, "D2-A"),
            new(901, 7777, "Out"),
        };
        var (client, handler) = CreateSensorScopeClient(sensors);
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());

        List<(long, long, string, string, string?, bool)> Snapshot()
        {
            using var ctx = _fx.NewContext();
            return ctx.PrtgSensors.Where(s => s.DeviceObjid == 1 || s.DeviceObjid == 2)
                .OrderBy(s => s.Objid).ToList()
                .Select(s => (s.Objid, s.DeviceObjid, s.Name, s.SensorType, s.Status, s.Paused)).ToList();
        }

        await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, ScopeOf(1, 2), fetchValues: false);
        var perDevice = Snapshot();
        using (var ctx = _fx.NewContext()) ctx.PrtgSensors.ExecuteDelete();

        lock (handler.RequestedUrls) handler.RequestedUrls.Clear();
        var wide = new long[] { 1, 2 }.Concat(Enumerable.Range(1000, 500).Select(i => (long)i)).ToArray();
        await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, ScopeOf(wide), fetchValues: false);
        var siteWide = Snapshot();

        // 確認第二趟真的走全站模式
        Assert.Contains(handler.RequestedUrls, u => u.Contains("content=sensors") && IdQuery(u) == null);
        Assert.Equal(3, perDevice.Count);
        Assert.Equal(perDevice, siteWide);
        Assert.Equal(new long[] { 201, 202, 203 }, MirrorSensorObjids());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 感測器同步_數值名單只含範圍內未暫停感測器(bool siteWide)
    {
        var sensors = new FakeSensor[]
        {
            new(201, 1, "D1-A"),
            new(202, 1, "D1-B", Paused: true),
            new(301, 3, "D3-A"),
        };
        var (client, handler) = CreateSensorScopeClient(sensors);
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());
        var scope = siteWide
            ? new long[] { 1 }.Concat(Enumerable.Range(1000, 500).Select(i => (long)i)).ToArray()
            : new long[] { 1 };

        await service.FetchDayAsync(new DateTime(2026, 8, 30), 2, CancellationToken.None, ScopeOf(scope), fetchValues: true);

        var histIds = handler.RequestedUrls.Where(u => u.Contains("historicdata"))
            .Select(u => IdQuery(u)).OrderBy(id => id).ToList();
        Assert.Equal(new long?[] { 201 }, histIds);
    }

    [Fact]
    public async Task BackfillSensorsForDevicesAsync_兩台一台失敗_另一台寫入且不清除既有列()
    {
        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("id=102"))
                return JsonResponse("{}", HttpStatusCode.InternalServerError);
            if (url.Contains("content=sensors") && url.Contains("id=101"))
                return url.Contains("start=0")
                    ? JsonResponse("{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU\",\"type\":\"wmicpu\",\"status\":\"Up\"}]}")
                    : JsonResponse("{\"treesize\":1,\"sensors\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var store = CreateStore();
        // 範圍外、很久以前同步的既有列：補抓不得清除
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 900, DeviceObjid = 999, Name = "Old", SensorType = "ping", Status = "Up" }
        }, new DateTime(2020, 1, 1));
        var service = new PrtgFetchService(client, store, new TestConsole(),
            new Dictionary<string, string> { ["wmicpu"] = PrtgSensorCategories.Cpu });

        var before = DateTime.Now;
        var result = await service.BackfillSensorsForDevicesAsync(new long[] { 101, 102 }, 2, CancellationToken.None);

        Assert.Equal(1, result.SensorsWritten);
        Assert.Equal(new long[] { 102 }, result.FailedDevices);
        Assert.Empty(result.EmptyDevices);
        var all = store.GetAllSensors();
        Assert.Contains(all, s => s.Objid == 900);
        var written = Assert.Single(all, s => s.Objid == 201);
        Assert.Equal(101, written.DeviceObjid);
        // SyncedAt 用當下時間，晚於任何已開始的結構同步起點
        Assert.True(written.SyncedAt >= before.AddSeconds(-1));
        // 與階段 2 同一個語意分類重算（同一份補充對照）：wmicpu 依補充對照分類為 cpu
        Assert.Equal(PrtgSensorCategories.Cpu, written.Category);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("id=101"));
        Assert.Contains(handler.RequestedUrls, u => u.Contains("id=102"));
    }

    [Fact]
    public async Task BackfillSensorsForDevicesAsync_查詢成功但0顆_列入取回0顆清單()
    {
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=sensors") && url.Contains("id=101"))
                return url.Contains("start=0")
                    ? JsonResponse("{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU\",\"type\":\"wmicpu\",\"status\":\"Up\"}]}")
                    : JsonResponse("{\"treesize\":1,\"sensors\":[]}");
            if (url.Contains("content=sensors") && url.Contains("id=103"))
                return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole(), new Dictionary<string, string>());

        var result = await service.BackfillSensorsForDevicesAsync(new long[] { 101, 102, 103 }, 1, CancellationToken.None);

        Assert.Equal(new long[] { 103 }, result.EmptyDevices);
        Assert.Equal(new long[] { 102 }, result.FailedDevices);
        Assert.Equal(1, result.SensorsWritten);
    }
}
