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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None);

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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        // 跑第一次
        var result1 = await service.FetchDayAsync(day, 2, CancellationToken.None);
        Assert.Equal(0, result1.Failures);

        // 跑第二次（同一天重跑）
        var result2 = await service.FetchDayAsync(day, 2, CancellationToken.None);
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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None);
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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None);
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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None);
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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None);
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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None);
        Assert.Equal(0, result.Failures);
        Assert.Equal(1, result.StateChanges);

        using var ctx = _fx.NewContext();
        var changes = await ctx.PrtgStateChanges.ToListAsync();
        Assert.Single(changes);
        Assert.Equal(302, changes[0].SensorObjid);
        Assert.Equal("Warning", changes[0].Status);
        Assert.Equal("Target day", changes[0].Message);
        Assert.Equal(new DateTime(2026, 8, 30, 12, 0, 0), changes[0].ChangedAt);
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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        // 斷言不擲出例外
        var result = await service.FetchDayAsync(day, 2, CancellationToken.None);

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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        // 限制併發數為 1
        var result = await service.FetchDayAsync(day, concurrency: 1, CancellationToken.None);
        Assert.Equal(0, result.Failures);
        Assert.Equal(5, result.Values);

        // 斷言同時進行中的請求數峰值不超過 1
        Assert.Equal(1, handler.MaxConcurrentRequests);
    }

    [Fact]
    public async Task FetchDayAsync_併發設定值真的被採用而非寫死()
    {
        // 只驗 concurrency=1 證明不了「設定有被吃進去」——把 semaphore 寫死成 1 也照樣綠。
        // 這裡驗 concurrency=3 時峰值**等於** 3：夠多 sensor＋固定延遲下，semaphore 若真的用
        // 傳入值，峰值必然衝到上限；寫死成 1 或 2 都會在這裡現形。
        var senJson = "{\"treesize\":6,\"sensors\":[" +
                      string.Join(",", Enumerable.Range(301, 6).Select(id => $"{{\"objid\":{id},\"paused\":false}}")) +
                      "]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10,\"coverage\":100}]}";

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
                await Task.Delay(150);
                return JsonResponse(histJson);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        };

        var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        var service = new PrtgFetchService(client, CreateStore(), new TestConsole());

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), concurrency: 3, CancellationToken.None);

        Assert.Equal(0, result.Failures);
        Assert.Equal(3, handler.MaxConcurrentRequests);
    }

    [Fact]
    public async Task FetchDayAsync_分頁能抓完多頁()
    {
        // 第一頁 500 筆
        var page1Sb = new StringBuilder();
        page1Sb.Append("{\"treesize\":503,\"devices\":[");
        for (var i = 1; i <= 500; i++)
        {
            if (i > 1) page1Sb.Append(',');
            page1Sb.Append($"{{\"objid\":{i},\"device\":\"Dev-{i}\",\"paused\":false}}");
        }
        page1Sb.Append("]}");
        var page1Json = page1Sb.ToString();

        // 第二頁 3 筆
        var page2Json = "{\"treesize\":503,\"devices\":[" +
                        "{\"objid\":501,\"device\":\"Dev-501\",\"paused\":false}," +
                        "{\"objid\":502,\"device\":\"Dev-502\",\"paused\":false}," +
                        "{\"objid\":503,\"device\":\"Dev-503\",\"paused\":false}" +
                        "]}";
        // 第三頁空陣列
        var page3Json = "{\"treesize\":503,\"devices\":[]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
            {
                if (url.Contains("start=0")) return JsonResponse(page1Json);
                if (url.Contains("start=500")) return JsonResponse(page2Json);
                return JsonResponse(page3Json);
            }
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 2, CancellationToken.None);
        Assert.Equal(0, result.Failures);
        Assert.Equal(503, result.Devices);

        using var ctx = _fx.NewContext();
        var count = await ctx.PrtgDevices.CountAsync();
        Assert.Equal(503, count);
    }

    [Fact]
    public async Task FetchDayAsync_伺服器忽略start參數時仍會終止不會無限迴圈()
    {
        // PRTG 前面若擺了會忽略 start 參數的代理，每頁都回同一批非空資料。
        // 分頁若只靠「空頁」判定結束，這裡會永遠跑不完、整趟夜間批次無聲卡死。
        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return JsonResponse("{\"treesize\":2,\"devices\":[" +
                    "{\"objid\":701,\"device\":\"Dev-701\",\"paused\":false}," +
                    "{\"objid\":702,\"device\":\"Dev-702\",\"paused\":false}]}");
            if (url.Contains("content=sensors")) return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (url.Contains("content=messages")) return JsonResponse("{\"treesize\":0,\"messages\":[]}");
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var service = new PrtgFetchService(client, store, new TestConsole());

        var task = service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None);
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(task, finished);
        await task;
        Assert.True(handler.RequestedUrls.Count(u => u.Contains("content=devices")) <= 2,
            "未滿一頁就該停止分頁，不應反覆請求同一個 content=devices");
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
        var service = new PrtgFetchService(client, store, console);

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None);

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
        var service = new PrtgFetchService(client, store, console);

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None);

        Assert.Equal(0, result.Failures);
        Assert.Equal(2, result.Values);
        Assert.Contains(console.Lines, l => l.Contains("1 個感測器的數值擷取失敗"));

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
        var service = new PrtgFetchService(client, store, console);

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None);

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
        var service = new PrtgFetchService(client, store, console);

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, syncStructure: false);

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
        var service = new PrtgFetchService(client, store, console);

        var result = await service.FetchDayAsync(new DateTime(2026, 8, 30), 1, CancellationToken.None, syncStructure: false);

        Assert.True(result.Failures > 0);
        Assert.Empty(handler.RequestedUrls);
        Assert.Contains(console.Lines, l => l.Contains("鏡像尚無任何感測器結構"));
    }

    [Fact]
    public async Task FetchDayAsync_每日擷取後自動填入感測器語意分類()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var senJson = "{\"treesize\":2,\"sensors\":[" +
            "{\"objid\":201,\"parentid\":101,\"sensor\":\"Free Space C:\",\"type\":\"SNMP Disk Free\",\"status\":\"Up\",\"paused\":false}," +
            "{\"objid\":202,\"parentid\":101,\"sensor\":\"Ping\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false}" +
            "]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        var (client, _) = CreateClient(req =>
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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 1, CancellationToken.None);

        Assert.Equal(0, result.Failures);
        Assert.Equal(2, result.Sensors);

        using var ctx = _fx.NewContext();
        var s201 = await ctx.PrtgSensors.SingleAsync(s => s.Objid == 201);
        Assert.Equal(PrtgSensorCategories.Disk, s201.Category);
        Assert.Equal(PrtgCategorySources.Auto, s201.CategorySource);

        var s202 = await ctx.PrtgSensors.SingleAsync(s => s.Objid == 202);
        Assert.Null(s202.Category);
        Assert.Null(s202.CategorySource);

        Assert.Contains(console.Lines, l => l.Contains("[階段 2/4] 已自動填入 1 個感測器的語意分類。"));
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
        var service = new PrtgFetchService(client, store, console);
        var day = new DateTime(2026, 8, 30);

        var result = await service.FetchDayAsync(day, 1, CancellationToken.None, syncStructure: true, fetchValues: false);

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
        var service = new PrtgFetchService(client, store, console);
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
}
