using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
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
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        // 回填不跑結構同步、sensor 清單來自鏡像——沒預置的話整趟是「0 個 sensor 的空跑」，
        // 這個測試就變成在替「空跑報成功」背書（換模型體檢抓到的假通過形狀）
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 3, 2, console, CancellationToken.None, Array.Empty<long>());

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
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        // 回填不跑結構同步，sensor 清單改從鏡像讀——先把結構準備好，才是真實的回填前提
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 3, 2, console, CancellationToken.None, Array.Empty<long>());

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
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        // 預置鏡像，讓失敗真的來自 PRTG 回 503，而不是「鏡像為空」的入口防線
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 2, 2, console, CancellationToken.None, Array.Empty<long>());

        Assert.False(ok);
        Assert.Contains(console.Lines, l => l.Contains("共成功 0 天，失敗 2 天"));
    }

    [Fact]
    public async Task RunAsync_天數小於等於零直接回傳false()
    {
        var (client, _) = CreateClient(_ => JsonResponse("{}"));
        var store = CreateStore();
        var console = new TestConsole();
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var okZero = await PrtgBackfillRunner.RunAsync(fetchService, 0, 2, console, CancellationToken.None, Array.Empty<long>());
        var okNegative = await PrtgBackfillRunner.RunAsync(fetchService, -5, 2, console, CancellationToken.None, Array.Empty<long>());

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
                return JsonResponse(msgJson);
            if (url.Contains("historicdata.json"))
                return JsonResponse(histJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var store = CreateStore();
        var console = new TestConsole();
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        // 預置鏡像：鏡像為空時 FetchDayAsync 在入口就短路，走不到會回報 sensor 進度的數值階段
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            // 第一天唯一一顆 sensor 的數值寫完（進度回報 1/1）當下按停止：
            // 這一天已沒有任何取消檢查點、照常做完，第二天入口才中斷
            await PrtgBackfillRunner.RunAsync(fetchService, 5, 2, console, cts.Token, Array.Empty<long>(),
                sensorProgress: (done, total) => { if (total > 0 && done == total) cts.Cancel(); });
        });

        // 已完成天數要算到第 1 天，不能因「現在已取消」把做完的那天少算掉
        Assert.Contains(console.Lines, l => l.Contains("回填已中斷（已停止：完成 1／5 天）"));
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
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        // 預置鏡像 sensor（回填不跑結構同步）；沒預置的話整趟空跑，「不寫對應」會變成恆真斷言
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 1, 2, console, CancellationToken.None, Array.Empty<long>());
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
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        // 不傳 store、records、whitelist（預設 null），應走全量回填路徑
        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 1, 2, console, CancellationToken.None, Array.Empty<long>());

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

        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var ok = await PrtgBackfillRunner.RunAsync(
            fetchService, 1, 2, console, CancellationToken.None, Array.Empty<long>(),
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

        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var ok = await PrtgBackfillRunner.RunAsync(
            fetchService, 1, 2, console, CancellationToken.None, Array.Empty<long>(),
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

        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var ok = await PrtgBackfillRunner.RunAsync(
            fetchService, 1, 2, console, CancellationToken.None, Array.Empty<long>(),
            store, recordStore);

        // 該日無問題主機：不抓數值（無 historicdata 請求）且不算失敗（回傳 true）
        Assert.True(ok);
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("historicdata.json"));
        Assert.Contains(console.Lines, l => l.Contains("問題主機 0 台") && l.Contains("sensor 0 個") && l.Contains("數值 0 筆"));
        // 觸發式分支的摘要有「成功／失敗／略過」三項（有對應但無問題主機＝成功，不是略過）
        Assert.Contains(console.Lines, l => l.Contains("回填完成：成功 1 天、失敗 0 天、略過 0 天"));
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
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        var dayProgressList = new List<(int Done, int Total, DateTime? Date)>();
        var ok = await PrtgBackfillRunner.RunAsync(
            fetchService, 3, 2, console, CancellationToken.None, Array.Empty<long>(),
            dayProgress: (done, total, date) => dayProgressList.Add((done, total, date)));

        Assert.True(ok);
        // 每天開始處理時回報「已完成幾天」＋正在處理的日期：
        // 第 1 天開始時已完成 0 天，最後一次（全部跑完）才是 3/3。
        // 回報 done=i 會讓剛開始就顯示 1/3、最後一天處理中顯示 3/3（看起來跑完了其實還在跑）。
        Assert.True(dayProgressList.Count >= 4);
        Assert.Equal((0, 3), (dayProgressList[0].Done, dayProgressList[0].Total));
        Assert.Equal(DateTime.Today.AddDays(-1), dayProgressList[0].Date);

        Assert.Equal((1, 3), (dayProgressList[1].Done, dayProgressList[1].Total));
        Assert.Equal(DateTime.Today.AddDays(-2), dayProgressList[1].Date);

        Assert.Equal((2, 3), (dayProgressList[2].Done, dayProgressList[2].Total));
        Assert.Equal(DateTime.Today.AddDays(-3), dayProgressList[2].Date);

        // 收尾：全部完成才是 3/3，且不再有「正在處理的日期」
        var last = dayProgressList[^1];
        Assert.Equal(3, last.Done);
        Assert.Equal(3, last.Total);
        Assert.Null(last.Date);
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
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        // 預置 4 個 sensor
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "CPU-1", SensorType = "wmicpu", Paused = false },
            new() { Objid = 202, DeviceObjid = 101, Name = "CPU-2", SensorType = "wmicpu", Paused = false },
            new() { Objid = 203, DeviceObjid = 101, Name = "CPU-3", SensorType = "wmicpu", Paused = false },
            new() { Objid = 204, DeviceObjid = 101, Name = "CPU-4", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);

        // 以「正在處理的日期」分桶，不依賴已完成天數的計法
        var events = new List<(DateTime? Day, int SensorDone, int SensorTotal)>();
        DateTime? currentDay = null;

        var ok = await PrtgBackfillRunner.RunAsync(
            fetchService, 2, 2, console, CancellationToken.None, Array.Empty<long>(),
            dayProgress: (done, total, date) =>
            {
                if (date.HasValue)
                {
                    currentDay = date;
                }
            },
            sensorProgress: (done, total) =>
            {
                events.Add((currentDay, done, total));
            });

        Assert.True(ok);

        // 回填由近往遠：第一天是昨天、第二天是前天
        var day1Events = events.Where(e => e.Day == DateTime.Today.AddDays(-1)).ToList();
        var day2Events = events.Where(e => e.Day == DateTime.Today.AddDays(-2)).ToList();

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

    }

    // ── 觸發式回填：狀態變更只翻一次、略過語意、取消（docs/PRTG-SPEC.md §5）────────────────

    private const string EmptyMessagesJson = "{\"treesize\":0,\"messages\":[]}";
    private const string OneHourHistJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";

    /// <summary>觸發式回填的共用前提：一台高風險主機（每天都有紀錄）、一個 sensor，對應只放在 mapDates 指定的日期。</summary>
    private (EfPrtgStore Store, EfAnalysisRecordStore Records) SetupTriggered(int days, params DateTime[] mapDates)
    {
        var store = CreateStore();
        var records = CreateRecordStore();
        for (var i = 1; i <= days; i++)
        {
            records.Append(CreateRecord(101, "SRV-HIGH", DateTime.Today.AddDays(-i), "高"));
        }
        store.UpsertDevices(new List<PrtgDeviceRow> { new() { Objid = 1001, Name = "Dev-A", Ip = "10.0.0.1" } }, DateTime.Today);
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "Sensor-A", SensorType = "wmicpu", Paused = false }
        }, DateTime.Today);
        foreach (var d in mapDates)
        {
            store.ReplaceHostMapForDate(d, new List<PrtgHostMapRow>
            {
                new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Ok }
            });
        }
        return (store, records);
    }

    [Fact]
    public async Task 觸發式回填_狀態變更整趟只查一次()
    {
        var (store, records) = SetupTriggered(3, DateTime.Today.AddDays(-3));
        var console = new TestConsole();
        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages")) return JsonResponse(EmptyMessagesJson);
            if (url.Contains("historicdata.json")) return JsonResponse(OneHourHistJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 3, 2, console, CancellationToken.None, new long[] { 1001, 1002 }, store, records);

        Assert.True(ok);
        // 3 天只有迴圈前那一趟 messages 查詢：範圍內每台裝置恰一組請求；數值照樣逐日取（3 天各一次 historicdata）
        var messageUrls = handler.RequestedUrls.Where(u => u.Contains("content=messages")).ToList();
        Assert.Equal(2, messageUrls.Count);
        Assert.Single(messageUrls, u => System.Text.RegularExpressions.Regex.IsMatch(u, @"[?&]id=1001(&|$)"));
        Assert.Single(messageUrls, u => System.Text.RegularExpressions.Regex.IsMatch(u, @"[?&]id=1002(&|$)"));
        Assert.Equal(3, handler.RequestedUrls.Count(u => u.Contains("historicdata.json")));
        // messages 請求在第一個 historicdata 之前
        var firstMsg = handler.RequestedUrls.FindIndex(u => u.Contains("content=messages"));
        var firstHist = handler.RequestedUrls.FindIndex(u => u.Contains("historicdata.json"));
        Assert.True(firstMsg < firstHist, "狀態變更應在逐日迴圈之前取");
        Assert.Contains(console.Lines, l => l.StartsWith("狀態變更：查詢 2 台、讀取 ") && l.Contains("新增 0 筆"));
        Assert.Contains(console.Lines, l => l.Contains("回填完成：成功 3 天、失敗 0 天、略過 0 天"));
    }

    [Fact]
    public async Task 觸發式回填_某日之前沒有對應_計為略過不計成功()
    {
        // 對應只在昨天：第 2 天（前天）往回找不到任何對應
        var (store, records) = SetupTriggered(2, DateTime.Today.AddDays(-1));
        var console = new TestConsole();
        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages")) return JsonResponse(EmptyMessagesJson);
            if (url.Contains("historicdata.json")) return JsonResponse(OneHourHistJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 2, 2, console, CancellationToken.None, Array.Empty<long>(), store, records);

        Assert.True(ok);
        var day2 = DateTime.Today.AddDays(-2);
        Assert.Contains(console.Lines, l => l == $"回填 {day2:yyyy-MM-dd}（第 2/2 天）略過：該日之前沒有主機對應");
        Assert.Contains(console.Lines, l => l.Contains("回填完成：成功 1 天、失敗 0 天、略過 1 天（無主機對應）"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("historicdata.json") && u.Contains($"sdate={day2:yyyy-MM-dd}"));
    }

    [Fact]
    public async Task 觸發式回填_全部略過_回傳false並印出沒有任何一天有主機對應()
    {
        var (store, records) = SetupTriggered(2);
        var console = new TestConsole();
        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages")) return JsonResponse(EmptyMessagesJson);
            if (url.Contains("historicdata.json")) return JsonResponse(OneHourHistJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 2, 2, console, CancellationToken.None, Array.Empty<long>(), store, records);

        Assert.False(ok);
        Assert.Contains(console.Lines, l => l.Contains("沒有任何一天有主機對應，未取得數值"));
        Assert.Contains(console.Lines, l => l.Contains("回填完成：成功 0 天、失敗 0 天、略過 2 天"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("historicdata.json"));
    }

    [Fact]
    public async Task 觸發式回填_狀態變更失敗_數值照常且結果為false()
    {
        var (store, records) = SetupTriggered(2, DateTime.Today.AddDays(-2));
        var console = new TestConsole();
        var (client, handler) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages")) return JsonResponse("Internal Server Error", HttpStatusCode.InternalServerError);
            if (url.Contains("historicdata.json")) return JsonResponse(OneHourHistJson);
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        var ok = await PrtgBackfillRunner.RunAsync(fetchService, 2, 2, console, CancellationToken.None, new long[] { 1001 }, store, records);

        Assert.False(ok);
        Assert.Equal(2, handler.RequestedUrls.Count(u => u.Contains("historicdata.json")));
        Assert.Contains(console.Lines, l => l.Contains("回填完成：成功 2 天、失敗 0 天、略過 0 天"));
        Assert.Contains(console.Lines, l => l.StartsWith("⚠ 狀態變更未完整取得："));
        using var ctx = _fx.NewContext();
        Assert.True(ctx.PrtgValues.Any(), "狀態變更失敗不得讓數值一起放棄");
    }

    [Fact]
    public async Task 觸發式回填_取消_印出已停止與完成天數()
    {
        var (store, records) = SetupTriggered(3, DateTime.Today.AddDays(-3));
        var console = new TestConsole();
        using var cts = new CancellationTokenSource();
        var day2 = DateTime.Today.AddDays(-2);
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("content=messages")) return JsonResponse(EmptyMessagesJson);
            if (url.Contains("historicdata.json"))
            {
                // 第 2 天開始取數值時使用者按停止
                if (url.Contains($"sdate={day2:yyyy-MM-dd}")) cts.Cancel();
                return JsonResponse(OneHourHistJson);
            }
            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var fetchService = new PrtgFetchService(client, store, console, new Dictionary<string, string>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PrtgBackfillRunner.RunAsync(fetchService, 3, 2, console, cts.Token, Array.Empty<long>(), store, records));

        // 第 1 天完整做完、第 2 天做到一半被停：完成 1／3 天
        Assert.Contains(console.Lines, l => l.Contains("已停止：完成 1／3 天"));
        Assert.DoesNotContain(console.Lines, l => l.StartsWith("回填完成："));
    }

    [Fact]
    public void PrtgBackfillRunState_狀態變更進度_第一次換日時結束讀取旗標且取消後記住已停止()
    {
        var state = new PrtgBackfillRunState();
        Assert.True(state.TryBeginRun(out var token));

        state.UpdateStateChanges(500, 2000);
        var p = state.GetProgress();
        Assert.True(p.ReadingStateChanges);
        Assert.Equal((500, 2000), (p.StateChangesRead, p.StateChangesTotal));

        state.UpdateDay(0, 3, DateTime.Today.AddDays(-1));
        Assert.False(state.GetProgress().ReadingStateChanges);

        Assert.True(state.TryCancel());
        Assert.True(token.IsCancellationRequested);
        state.FinishRun(false, cancelled: true);
        Assert.True(state.Cancelled);
        Assert.False(state.TryCancel()); // 結束後沒有東西可停

        state.ResetProgress();
        Assert.Equal((0, 0, false), (state.GetProgress().StateChangesRead, state.GetProgress().StateChangesTotal, state.GetProgress().ReadingStateChanges));

        // 新一趟開始時歸零「已停止」
        Assert.True(state.TryBeginRun(out _));
        Assert.False(state.Cancelled);
    }

    // ── PrtgBackfillService 啟動閘門與停止（docs/PRTG-SPEC.md §5）──────────────────────────

    private sealed class ServiceHarness : IDisposable
    {
        public string Dir { get; }
        public StorageBackend Backend { get; }
        public SystemSettingsStore Settings { get; }
        public SchedulerRunState Scheduler { get; } = new();
        public PrtgStructureSyncRunState SyncState { get; } = new();
        public PrtgBackfillService Service { get; }

        public ServiceHarness()
        {
            Dir = Path.Combine(Path.GetTempPath(), "lf-test-backfill-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(Dir, "test.db")}" },
                Dir);
            Settings = new SystemSettingsStore(Backend.Blob("system_settings"));
            Settings.Update(s =>
            {
                s.PrtgEnabled = true;
                s.PrtgUrl = "https://prtg.example.com";
                s.PrtgAuthMode = PrtgAuthModes.Token;
                s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token123");
                s.PrtgTimeoutSeconds = 5;
            });
            Backend.PrtgStore().UpsertSensors(new List<PrtgSensorRow>
            {
                new() { Objid = 2001, DeviceObjid = 1001, Name = "CPU", SensorType = "wmicpu", Paused = false }
            }, DateTime.Now);
            Service = new PrtgBackfillService(
                Settings, Backend, new PrtgBackfillRunState(), new PrtgProbeRunState(),
                new HostStore(Backend.Blob("hosts")), Scheduler, SyncState, new FakeSentinelStore());
        }

        public void AddMap(string status) =>
            Backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today.AddDays(-1), new List<PrtgHostMapRow>
            {
                new() { DeviceObjid = 1001, HostId = 101, MapStatus = status }
            });

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TryStart_取數執行中_回false並提示停止執行()
    {
        using var h = new ServiceHarness();
        h.AddMap(PrtgMapStatus.Ok); // 有對應：確認擋下它的是取數閘門而不是對應閘門
        Assert.True(h.Scheduler.TryBeginRun("manual", out _));

        Assert.False(h.Service.TryStart(out var error, out var isConflict));
        Assert.True(isConflict); // 互斥類：呼叫端會回 409
        Assert.StartsWith("取數執行中（已 0 分鐘），回填會與它同時查詢同一台 PRTG。", error);
        Assert.Contains("停止執行", error);
        Assert.False(h.Service.GetStatus().IsRunning);
    }

    [Fact]
    public void TryStart_結構同步中_回false並提示等它完成()
    {
        using var h = new ServiceHarness();
        h.AddMap(PrtgMapStatus.Ok);
        Assert.True(h.SyncState.TryBegin());

        Assert.False(h.Service.TryStart(out var error, out var isConflict));
        Assert.True(isConflict); // 互斥類：呼叫端會回 409
        Assert.StartsWith("「同步結構與對應」執行中（已 0 分鐘），請等它完成", error);
        Assert.False(h.Service.GetStatus().IsRunning);
    }

    [Fact]
    public void TryStart_近期沒有任何ok主機對應_回false並提示先同步()
    {
        using var h = new ServiceHarness();
        // 只有「查無主機」的對應列不算數
        h.AddMap(PrtgMapStatus.Unmatched);

        Assert.False(h.Service.TryStart(out var error, out var isConflict));
        Assert.False(isConflict); // 前提類：呼叫端會回 400
        Assert.Equal(
            $"近 {SystemSettings.DefaultPrtgBackfillDays + PrtgTriggeredValueFetcher.HostMapLookbackDays} 天沒有任何 PRTG 主機對應，回填找不到要取數的主機。請先按「同步結構與對應」建立對應後再回填。",
            error);
        Assert.False(h.Service.GetStatus().IsRunning);
    }

    [Fact]
    public void TryCancel_沒有執行中回false()
    {
        using var h = new ServiceHarness();
        Assert.False(h.Service.TryCancel());
        Assert.False(h.Service.GetStatus().Cancelled);
    }
}
