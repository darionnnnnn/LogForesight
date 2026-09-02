using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PrtgBackfillRunner 歷史逐日回填單元測試。
/// 驗證逐日推進、失敗容錯、天數邊界、中斷取消與主機對應不執行等行為。
/// </summary>
public class PrtgBackfillRunnerTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfPrtgStore CreateStore() => new(_fx.NewContext);
    private EfAnalysisRecordStore CreateRecordStore() => new(_fx.NewContext, "test");

    private static DailyAnalysisRecord CreateRecord(long hostId, string host, DateTime date, string riskLevel)
    {
        return new DailyAnalysisRecord
        {
            HostId = hostId,
            Host = host,
            Date = date,
            RiskLevel = riskLevel
        };
    }

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

    [Fact]
    public async Task RunAsync_逐日推進回填過去多天數據且成功回傳true()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU\",\"type\":\"wmicpu\",\"status\":\"Up\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        var (client, handler) = CreateClient(req =>
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
        var fetchService = new PrtgFetchService(client, store, console);

        // 回填不跑結構同步、sensor 清單來自鏡像——沒預置的話整趟是「0 個 sensor 的空跑」，
        // 這個測試就變成在替「空跑報成功」背書（換模型體檢抓到的假通過形狀）
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 3, 2, console, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains(console.Lines, l => l.Contains("開始執行 PRTG 歷史回填（共 3 天"));
        Assert.Contains(console.Lines, l => l.Contains("第 1/3 天"));
        Assert.Contains(console.Lines, l => l.Contains("第 2/3 天"));
        Assert.Contains(console.Lines, l => l.Contains("第 3/3 天"));
        Assert.Contains(console.Lines, l => l.Contains("共成功 3 天，失敗 0 天"));

        // 「逐日推進」要驗實際請求，不能只驗 console 文字：三天的 historicdata 必須帶三個相異的 sdate，
        // 且正是昨天、前天、大前天（若日期計算寫死成固定一天，console 照樣印三行，只有這裡抓得到）
        var sdates = handler.RequestedUrls
            .Where(u => u.Contains("historicdata.json"))
            .Select(u => u.Split("sdate=")[1].Split('&')[0])
            .Distinct()
            .OrderBy(s => s)
            .ToList();
        var expected = Enumerable.Range(1, 3)
            .Select(i => DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd-00-00-00"))
            .OrderBy(s => s)
            .ToList();
        Assert.Equal(expected, sdates);

        // 回填不得寫入主機對應（歷史對應無法重建）；有真的抓到數值才證明這不是空跑
        using var ctx = _fx.NewContext();
        Assert.True(ctx.PrtgValues.Any(), "回填應寫入數值——零筆代表整趟是空跑，測試前提失效");
        Assert.False(ctx.PrtgHostMaps.Any(), "回填不得寫入主機對應");
    }

    [Fact]
    public async Task RunAsync_單日擷取失敗時繼續推進其餘天數()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU\",\"type\":\"wmicpu\",\"status\":\"Up\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        // 第 2 天（AddDays(-2)）模擬 PRTG 異常失敗
        var day2 = DateTime.Today.AddDays(-2);
        var day2Str = day2.ToString("yyyy-MM-dd");

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains($"sdate={day2Str}"))
            {
                return JsonResponse("Internal Server Error", HttpStatusCode.InternalServerError);
            }
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
        var fetchService = new PrtgFetchService(client, store, console);

        // 回填不跑結構同步，sensor 清單改從鏡像讀——先把結構準備好，才是真實的回填前提
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 3, 2, console, CancellationToken.None);

        Assert.True(ok); // 有成功的天數即回傳 true
        Assert.Contains(console.Lines, l => l.Contains($"回填 {day2Str}（第 2/3 天）失敗"));
        Assert.Contains(console.Lines, l => l.Contains("第 3/3 天"));
        Assert.Contains(console.Lines, l => l.Contains("共成功 2 天，失敗 1 天"));
    }

    [Fact]
    public async Task RunAsync_全部天數皆失敗時回傳false()
    {
        var (client, _) = CreateClient(_ => JsonResponse("Service Unavailable", HttpStatusCode.ServiceUnavailable));

        var store = CreateStore();
        var console = new TestConsole();
        var fetchService = new PrtgFetchService(client, store, console);

        // 預置鏡像，讓失敗真的來自 PRTG 回 503，而不是「鏡像為空」的入口防線
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 2, 2, console, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains(console.Lines, l => l.Contains("共成功 0 天，失敗 2 天"));
    }

    [Fact]
    public async Task RunAsync_天數小於等於零直接回傳false()
    {
        var (client, _) = CreateClient(_ => JsonResponse("{}"));
        var store = CreateStore();
        var console = new TestConsole();
        var fetchService = new PrtgFetchService(client, store, console);

        var okZero = await PrtgBackfillRunner.RunAsync(fetchService, 0, 2, console, CancellationToken.None);
        var okNegative = await PrtgBackfillRunner.RunAsync(fetchService, -5, 2, console, CancellationToken.None);

        Assert.False(okZero);
        Assert.False(okNegative);
        Assert.Contains(console.Lines, l => l.Contains("回填天數必須大於 0。"));
    }

    [Fact]
    public async Task RunAsync_收到取消語彙基元時中斷並拋出例外()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU\",\"type\":\"wmicpu\",\"status\":\"Up\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        using var cts = new CancellationTokenSource();

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            // 第一天執行完後觸發取消
            if (url.Contains("content=devices"))
            {
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            }
            if (url.Contains("content=sensors"))
                return url.Contains("start=0") ? JsonResponse(senJson) : JsonResponse("{\"treesize\":1,\"sensors\":[]}");
            if (url.Contains("content=messages"))
            {
                cts.Cancel();
                return JsonResponse(msgJson);
            }
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var fetchService = new PrtgFetchService(client, store, console);

        // 預置鏡像：鏡像為空時 FetchDayAsync 在入口就短路，走不到觸發取消的 messages 請求
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await PrtgBackfillRunner.RunAsync(fetchService, 5, 2, console, cts.Token);
        });

        Assert.Contains(console.Lines, l => l.Contains("回填已中斷"));
    }

    [Fact]
    public async Task RunAsync_回填過程中完全不寫入主機對應表()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU\",\"type\":\"wmicpu\",\"status\":\"Up\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        var (client, handler) = CreateClient(req =>
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
        var fetchService = new PrtgFetchService(client, store, console);

        // 預置鏡像 sensor（回填不跑結構同步）；沒預置的話整趟空跑，「不寫對應」會變成恆真斷言
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 1, 2, console, CancellationToken.None);
        Assert.True(ok);

        using var ctx = _fx.NewContext();
        // 斷言回填只抓 values/messages/devices/sensors，完全不寫入 lf_prtg_host_map
        var mapCount = ctx.PrtgHostMaps.Count();
        Assert.Equal(0, mapCount);
    }

    [Fact]
    public async Task RunAsync_未傳入store與records時維持全量回填()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU\",\"type\":\"wmicpu\",\"status\":\"Up\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        var (client, handler) = CreateClient(req =>
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
        var fetchService = new PrtgFetchService(client, store, console);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        // 不傳 store、records、whitelist（預設 null），應走全量回填路徑
        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 1, 2, console, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains(handler.RequestedUrls, u => u.Contains("historicdata.json") && u.Contains("id=201"));
    }

    [Fact]
    public async Task RunAsync_觸發式回填_只回填高與中風險主機的sensor()
    {
        var day = DateTime.Today.AddDays(-1);
        var store = CreateStore();
        var recordStore = CreateRecordStore();
        var console = new TestConsole();

        // 建立主機分析紀錄：A 為高風險，B 為低風險
        recordStore.Append(CreateRecord(101, "SRV-HIGH", day, "高"));
        recordStore.Append(CreateRecord(102, "SRV-LOW", day, "低"));

        // PRTG 裝置與感測器
        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "Dev-A", Ip = "10.0.0.1" },
            new() { Objid = 1002, Name = "Dev-B", Ip = "10.0.0.2" }
        }, day);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "Sensor-A", SensorType = "wmicpu", Paused = false },
            new() { Objid = 2002, DeviceObjid = 1002, Name = "Sensor-B", SensorType = "wmicpu", Paused = false }
        }, day);

        // 主機對應表
        store.ReplaceHostMapForDate(day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Ok },
            new() { DeviceObjid = 1002, HostId = 102, MapStatus = PrtgMapStatus.Ok }
        });

        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var fetchService = new PrtgFetchService(client, store, console);

        var ok = await PrtgBackfillRunner.RunAsync(
            fetchService, 1, 2, console, CancellationToken.None,
            store, recordStore);

        Assert.True(ok);
        // 雙面斷言：高風險主機 A 的 sensor 2001 有被請求，低風險主機 B 的 sensor 2002 沒有被請求
        Assert.Contains(handler.RequestedUrls, u => u.Contains("historicdata.json") && u.Contains("id=2001"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("historicdata.json") && u.Contains("id=2002"));
        Assert.Contains(console.Lines, l => l.Contains("問題主機 1 台") && l.Contains("sensor 1 個"));
    }

    [Fact]
    public async Task RunAsync_觸發式回填_白名單過濾生效()
    {
        var day = DateTime.Today.AddDays(-1);
        var store = CreateStore();
        var recordStore = CreateRecordStore();
        var console = new TestConsole();

        // 建立高風險主機
        recordStore.Append(CreateRecord(101, "SRV-HIGH", day, "高"));

        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "Dev-A", Ip = "10.0.0.1" }
        }, day);

        // 同一裝置上有兩個 sensor，一個 type 命中白名單，另一個未命中
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "Sensor-Match", SensorType = "SNMP CPU Load", Paused = false },
            new() { Objid = 2002, DeviceObjid = 1001, Name = "Sensor-Mismatch", SensorType = "Ping", Paused = false }
        }, day);

        store.ReplaceHostMapForDate(day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Ok }
        });

        var whitelist = new[] { "SNMP CPU Load" };
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var fetchService = new PrtgFetchService(client, store, console);

        var ok = await PrtgBackfillRunner.RunAsync(
            fetchService, 1, 2, console, CancellationToken.None,
            store, recordStore, whitelist);

        Assert.True(ok);
        // 雙面斷言：白名單內的 sensor 2001 有被請求，白名單外的 sensor 2002 沒有被請求
        Assert.Contains(handler.RequestedUrls, u => u.Contains("historicdata.json") && u.Contains("id=2001"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("historicdata.json") && u.Contains("id=2002"));
        Assert.Contains(console.Lines, l => l.Contains("問題主機 1 台") && l.Contains("sensor 1 個"));
    }

    [Fact]
    public async Task RunAsync_觸發式回填_無問題主機時不抓數值且不算失敗()
    {
        var day = DateTime.Today.AddDays(-1);
        var store = CreateStore();
        var recordStore = CreateRecordStore();
        var console = new TestConsole();

        // 該日僅有低風險主機（非高/中）
        recordStore.Append(CreateRecord(102, "SRV-LOW", day, "低"));

        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1002, Name = "Dev-B", Ip = "10.0.0.2" }
        }, day);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2002, DeviceObjid = 1002, Name = "Sensor-B", SensorType = "wmicpu", Paused = false }
        }, day);

        store.ReplaceHostMapForDate(day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1002, HostId = 102, MapStatus = PrtgMapStatus.Ok }
        });

        var msgJson = "{\"treesize\":0,\"messages\":[]}";

        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var fetchService = new PrtgFetchService(client, store, console);

        var ok = await PrtgBackfillRunner.RunAsync(
            fetchService, 1, 2, console, CancellationToken.None,
            store, recordStore);

        // 該日無問題主機：不抓數值（無 historicdata 請求）且不算失敗（回傳 true）
        Assert.True(ok);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("historicdata.json"));
        Assert.Contains(console.Lines, l => l.Contains("問題主機 0 台") && l.Contains("sensor 0 個") && l.Contains("數值 0 筆"));
        Assert.Contains(console.Lines, l => l.Contains("共成功 1 天，失敗 0 天"));
    }

    [Fact]
    public async Task RunAsync_回報天數進度_依序涵蓋第1至N天且最後為總天數()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var senJson = "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":101,\"sensor\":\"CPU\",\"type\":\"wmicpu\",\"status\":\"Up\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

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
        var fetchService = new PrtgFetchService(client, store, console);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var dayProgressList = new List<(int Done, int Total, DateTime? Date)>();
        var ok = await PrtgBackfillRunner.RunAsync(
            fetchService, 3, 2, console, CancellationToken.None,
            dayProgress: (done, total, date) => dayProgressList.Add((done, total, date)));

        Assert.True(ok);
        // 依序涵蓋第 1、2、3 天，且最後一次為 3/3
        Assert.True(dayProgressList.Count >= 4);
        Assert.Equal((1, 3), (dayProgressList[0].Done, dayProgressList[0].Total));
        Assert.Equal(DateTime.Today.AddDays(-1), dayProgressList[0].Date);

        Assert.Equal((2, 3), (dayProgressList[1].Done, dayProgressList[1].Total));
        Assert.Equal(DateTime.Today.AddDays(-2), dayProgressList[1].Date);

        Assert.Equal((3, 3), (dayProgressList[2].Done, dayProgressList[2].Total));
        Assert.Equal(DateTime.Today.AddDays(-3), dayProgressList[2].Date);

        var last = dayProgressList[^1];
        Assert.Equal(3, last.Done);
        Assert.Equal(3, last.Total);
    }

    [Fact]
    public async Task RunAsync_跨日回填時sensor進度換日重設不累加()
    {
        var devJson = "{\"treesize\":1,\"devices\":[{\"objid\":101,\"device\":\"Server-01\",\"host\":\"192.168.1.10\",\"group\":\"Prod\",\"status\":\"Up\",\"paused\":false}]}";
        var msgJson = "{\"treesize\":0,\"messages\":[]}";
        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=devices"))
                return url.Contains("start=0") ? JsonResponse(devJson) : JsonResponse("{\"treesize\":1,\"devices\":[]}");
            if (url.Contains("content=messages"))
                return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var fetchService = new PrtgFetchService(client, store, console);

        // 預置 4 個 sensor
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU-1", SensorType = "wmicpu", Paused = false },
            new() { Objid = 202, DeviceObjid = 101, Name = "CPU-2", SensorType = "wmicpu", Paused = false },
            new() { Objid = 203, DeviceObjid = 101, Name = "CPU-3", SensorType = "wmicpu", Paused = false },
            new() { Objid = 204, DeviceObjid = 101, Name = "CPU-4", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var events = new List<(int DayIndex, int SensorDone, int SensorTotal)>();
        var currentDayIndex = 0;

        var ok = await PrtgBackfillRunner.RunAsync(
            fetchService, 2, 2, console, CancellationToken.None,
            dayProgress: (done, total, date) =>
            {
                if (date.HasValue)
                {
                    currentDayIndex = done;
                }
            },
            sensorProgress: (done, total) =>
            {
                events.Add((currentDayIndex, done, total));
            });

        Assert.True(ok);

        // 第一天與第二天的 sensor 事件
        var day1Events = events.Where(e => e.DayIndex == 1).ToList();
        var day2Events = events.Where(e => e.DayIndex == 2).ToList();

        Assert.NotEmpty(day1Events);
        Assert.NotEmpty(day2Events);

        // 第一天結束時 sensor 數為 4/4
        var day1Last = day1Events.Last();
        Assert.Equal((4, 4), (day1Last.SensorDone, day1Last.SensorTotal));

        // 第二天開頭必須立即重設為 (0, 0)
        var day2First = day2Events.First();
        Assert.Equal((0, 0), (day2First.SensorDone, day2First.SensorTotal));

        // 第二天所有的 sensor done 都不應大於 total（不得出現 5/4 等累積值）
        Assert.All(day2Events, e =>
        {
            if (e.SensorTotal > 0)
            {
                Assert.True(e.SensorDone <= e.SensorTotal, $"第二天 sensorDone ({e.SensorDone}) 不得大於 sensorTotal ({e.SensorTotal})");
            }
        });

        // 第二天最終完成時為 4/4
        var day2Last = day2Events.Last();
        Assert.Equal((4, 4), (day2Last.SensorDone, day2Last.SensorTotal));
    }

    [Fact]
    public void PrtgBackfillRunState_進度更新隔離_不影響PrtgProbeRunState快照且探測DTO無進度欄位()
    {
        var backfillState = new PrtgBackfillRunState();
        var probeState = new PrtgProbeRunState();

        // 啟動探測狀態
        Assert.True(probeState.TryBegin());
        probeState.AppendLine("探測中...");

        // 更新回填狀態進度
        Assert.True(backfillState.TryBegin());
        backfillState.UpdateDay(2, 5, DateTime.Today.AddDays(-2));
        backfillState.UpdateSensors(10, 20);

        // 斷言探測的快照完全未受回填進度影響
        var probeSnapshot = probeState.Snapshot();
        Assert.True(probeSnapshot.IsRunning);
        Assert.Equal("探測中...", probeSnapshot.LatestMessage);
        Assert.Single(probeSnapshot.Output);
        Assert.Equal("探測中...", probeSnapshot.Output[0]);

        // 斷言回填的進度快照正確更新
        var backfillProgress = backfillState.GetProgress();
        Assert.Equal(2, backfillProgress.DaysDone);
        Assert.Equal(5, backfillProgress.DaysTotal);
        Assert.Equal(DateTime.Today.AddDays(-2), backfillProgress.CurrentDate);
        Assert.Equal(10, backfillProgress.SensorsDone);
        Assert.Equal(20, backfillProgress.SensorsTotal);

        // 斷言探測 DTO 的屬性清單不含回填進度欄位（DaysDone、DaysTotal、SensorsDone 等）
        var probeDtoProps = typeof(PrtgProbeStatusDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("DaysDone", probeDtoProps);
        Assert.DoesNotContain("DaysTotal", probeDtoProps);
        Assert.DoesNotContain("CurrentDate", probeDtoProps);
        Assert.DoesNotContain("SensorsDone", probeDtoProps);
        Assert.DoesNotContain("SensorsTotal", probeDtoProps);

        // 斷言回填 DTO 有這些進度欄位
        var backfillDtoProps = typeof(PrtgBackfillStatusDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("DaysDone", backfillDtoProps);
        Assert.Contains("DaysTotal", backfillDtoProps);
        Assert.Contains("CurrentDate", backfillDtoProps);
        Assert.Contains("SensorsDone", backfillDtoProps);
        Assert.Contains("SensorsTotal", backfillDtoProps);
    }
}
