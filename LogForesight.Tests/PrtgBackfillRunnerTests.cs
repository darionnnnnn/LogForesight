using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
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
}
