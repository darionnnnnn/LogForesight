using System.Net;
using System.Text;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgBackfillRangeRunnerTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private EfPrtgStore Store() => new(_fixture.NewContext);

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class ConsoleStub : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(Json("{}"));
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private (PrtgFetchService Fetch, Handler Handler) Fetch(EfPrtgStore store, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null)
    {
        var handler = new Handler();
        if (respond != null) handler.Respond = respond;
        var client = new PrtgClient("https://prtg.example.com", "token", 30, true, handler,
            PrtgAuthModes.Token, "", "", "");
        var console = new ConsoleStub();
        var fetch = new PrtgFetchService(client, store,
            new PrtgFreshnessStore(new EfJsonBlobStore(_fixture.NewContext, PrtgFreshnessStore.BlobKey)),
            console, new Dictionary<string, string>());
        return (fetch, handler);
    }

    private static void AddSensor(EfPrtgStore store, long sensorId, long deviceId, string type = "diskfree") =>
        store.UpsertSensors(new[] { new PrtgSensorRow { Objid = sensorId, DeviceObjid = deviceId, Name = $"sensor-{sensorId}", SensorType = type } }, DateTime.Now);

    private static void AddMap(EfPrtgStore store, DateTime day, params (long DeviceId, long HostId)[] mappings) =>
        store.ReplaceHostMapForDate(day, mappings.Select(mapping => new PrtgHostMapRow
        {
            DeviceObjid = mapping.DeviceId,
            HostId = mapping.HostId,
            MapStatus = PrtgMapStatus.Ok
        }).ToArray());

    private static string HourFor(string url)
    {
        var query = new Uri(url).Query;
        var sdate = query.TrimStart('?').Split('&')
            .Single(part => part.StartsWith("sdate=", StringComparison.Ordinal))
            .Split('=', 2)[1];
        var day = Uri.UnescapeDataString(sdate)[..10];
        return $"{{\"histdata\":[{{\"datetime\":\"{day} 01:00:00\",\"value_\":12.5,\"coverage\":100}}]}}";
    }

    [Fact]
    public async Task 單一主機單日只請求該主機映射裝置的感測器()
    {
        var store = Store();
        var day = DateTime.Today.AddDays(-3);
        AddMap(store, day, (1001, 11), (1002, 22));
        AddSensor(store, 2001, 1001);
        AddSensor(store, 2002, 1002);
        var (fetch, handler) = Fetch(store, (req, _) => Task.FromResult(Json(HourFor(req.RequestUri!.ToString()))));
        var ok = await PrtgBackfillRunner.RunValuesForHostsAsync(fetch, day, day, new long[] { 11 }, 2,
            new[] { "diskfree" }, store, new ConsoleStub(), CancellationToken.None);

        Assert.True(ok);
        Assert.Contains(handler.Urls, u => u.Contains("historicdata.json") && u.Contains("id=2001"));
        Assert.DoesNotContain(handler.Urls, u => u.Contains("historicdata.json") && u.Contains("id=2002"));
    }

    [Fact]
    public async Task 每天只使用當日有效映射且零對應為可操作略過()
    {
        var store = Store();
        var day1 = DateTime.Today.AddDays(-4);
        var day2 = day1.AddDays(1);
        AddMap(store, day1, (1001, 11));
        AddMap(store, day2, (1002, 11));
        AddSensor(store, 2001, 1001);
        AddSensor(store, 2002, 1002);
        var (fetch, handler) = Fetch(store, (req, _) => Task.FromResult(Json(HourFor(req.RequestUri!.ToString()))));
        var output = new ConsoleStub();

        var ok = await PrtgBackfillRunner.RunValuesForHostsAsync(fetch, day1, day2.AddDays(1), new long[] { 11 }, 2,
            new[] { "diskfree" }, store, output, CancellationToken.None);

        Assert.True(ok);
        var calls = handler.Urls.Where(u => u.Contains("historicdata.json")).ToList();
        Assert.Contains(calls, u => u.Contains("id=2001") && u.Contains(day1.ToString("yyyy-MM-dd")));
        Assert.Contains(calls, u => u.Contains("id=2002") && u.Contains(day2.ToString("yyyy-MM-dd")));
        Assert.Equal(2, calls.Count);
        Assert.Contains(output.Lines, line => line.Contains("沒有指定主機的有效對應"));
    }

    [Fact]
    public async Task 沒有歷史映射不套用其他日期或今日映射也不算成功()
    {
        var store = Store();
        var day = DateTime.Today.AddDays(-5);
        AddMap(store, DateTime.Today, (1001, 11));
        AddSensor(store, 2001, 1001);
        var (fetch, handler) = Fetch(store);
        var output = new ConsoleStub();

        var ok = await PrtgBackfillRunner.RunValuesForHostsAsync(fetch, day, day, new long[] { 11 }, 1,
            new[] { "diskfree" }, store, output, CancellationToken.None);

        Assert.False(ok);
        Assert.DoesNotContain(handler.Urls, u => u.Contains("historicdata.json"));
        Assert.Contains(output.Lines, line => line.Contains("略過：該日沒有指定主機的有效對應"));
    }

    [Fact]
    public async Task 部分sensor失敗回報失敗但保留成功寫入()
    {
        var store = Store();
        var day = DateTime.Today.AddDays(-2);
        AddMap(store, day, (1001, 11));
        AddSensor(store, 2001, 1001);
        AddSensor(store, 2002, 1001);
        var (fetch, _) = Fetch(store, (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            return Task.FromResult(url.Contains("id=2002") ? Json("failure", HttpStatusCode.InternalServerError) : Json(HourFor(url)));
        });

        var ok = await PrtgBackfillRunner.RunValuesForHostsAsync(fetch, day, day, new long[] { 11 }, 1,
            new[] { "diskfree" }, store, new ConsoleStub(), CancellationToken.None);

        Assert.False(ok);
        using var db = _fixture.NewContext();
        Assert.Contains(db.PrtgValues, value => value.SensorObjid == 2001);
        Assert.DoesNotContain(db.PrtgValues, value => value.SensorObjid == 2002);
    }

    [Fact]
    public async Task 取消會向呼叫端傳遞且不宣告完成()
    {
        var store = Store();
        var day = DateTime.Today.AddDays(-1);
        AddMap(store, day, (1001, 11));
        AddSensor(store, 2001, 1001);
        using var cts = new CancellationTokenSource();
        var (fetch, _) = Fetch(store, (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(Json("{}"));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PrtgBackfillRunner.RunValuesForHostsAsync(fetch, day, day, new long[] { 11 }, 1,
                new[] { "diskfree" }, store, new ConsoleStub(), cts.Token));
    }
}
