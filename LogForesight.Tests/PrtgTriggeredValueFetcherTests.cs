using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgTriggeredValueFetcherTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfPrtgStore CreateStore() => new(_fx.NewContext);
    private EfAnalysisRecordStore CreateRecordStore() => new(_fx.NewContext, "test");

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

    [Fact]
    public async Task RunAsync_只對風險高與中的主機取數()
    {
        var day = new DateTime(2026, 8, 30);
        var store = CreateStore();
        var recordStore = CreateRecordStore();
        var console = new TestConsole();

        // 建立 3 台主機的分析紀錄：高、中、低
        recordStore.Append(CreateRecord(101, "SRV-HIGH", day, "高"));
        recordStore.Append(CreateRecord(102, "SRV-MED", day, "中"));
        recordStore.Append(CreateRecord(103, "SRV-LOW", day, "低"));

        // PRTG 裝置與感測器
        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "Dev-1", Ip = "10.0.0.1" },
            new() { Objid = 1002, Name = "Dev-2", Ip = "10.0.0.2" },
            new() { Objid = 1003, Name = "Dev-3", Ip = "10.0.0.3" }
        }, day);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "Sensor-1", SensorType = "SNMP CPU Load", Paused = false },
            new() { Objid = 2002, DeviceObjid = 1002, Name = "Sensor-2", SensorType = "SNMP Memory", Paused = false },
            new() { Objid = 2003, DeviceObjid = 1003, Name = "Sensor-3", SensorType = "SNMP Disk Free", Paused = false }
        }, day);

        // 主機對應表（全為 ok）
        store.ReplaceHostMapForDate(day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Ok },
            new() { DeviceObjid = 1002, HostId = 102, MapStatus = PrtgMapStatus.Ok },
            new() { DeviceObjid = 1003, HostId = 103, MapStatus = PrtgMapStatus.Ok }
        });

        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";
        var (client, handler) = CreateClient(req => JsonResponse(histJson));

        var fetchService = new PrtgFetchService(client, store, console);
        var fetcher = new PrtgTriggeredValueFetcher(fetchService, store, recordStore, console);

        var result = await fetcher.RunAsync(
            day, whitelist: null, concurrency: 2,
            analysisCompleted: () => true, ct: CancellationToken.None, pollSeconds: 1);

        // 雙面斷言：高／中風險主機對應的 sensor 有被請求，低風險主機對應的 sensor 沒有被請求
        Assert.Contains(handler.RequestedUrls, u => u.Contains("id=2001") && u.Contains("historicdata"));
        Assert.Contains(handler.RequestedUrls, u => u.Contains("id=2002") && u.Contains("historicdata"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("id=2003"));

        Assert.Equal(2, result.TriggerHosts);
        Assert.Equal(2, result.TargetSensors);
        Assert.Equal(2, result.ValuesWritten);
        Assert.Equal(0, result.FailedSensors);
    }

    [Fact]
    public async Task RunAsync_MapStatus為conflict或unmatched的device不取數()
    {
        var day = new DateTime(2026, 8, 30);
        var store = CreateStore();
        var recordStore = CreateRecordStore();
        var console = new TestConsole();

        // 3 台皆為高風險
        recordStore.Append(CreateRecord(101, "SRV-CONFLICT", day, "高"));
        recordStore.Append(CreateRecord(102, "SRV-UNMATCHED", day, "高"));
        recordStore.Append(CreateRecord(103, "SRV-OK", day, "高"));

        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "Dev-Conflict", Ip = "10.0.0.1" },
            new() { Objid = 1002, Name = "Dev-Unmatched", Ip = "10.0.0.2" },
            new() { Objid = 1003, Name = "Dev-Ok", Ip = "10.0.0.3" }
        }, day);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "Sensor-1", SensorType = "SNMP CPU Load", Paused = false },
            new() { Objid = 2002, DeviceObjid = 1002, Name = "Sensor-2", SensorType = "SNMP CPU Load", Paused = false },
            new() { Objid = 2003, DeviceObjid = 1003, Name = "Sensor-3", SensorType = "SNMP CPU Load", Paused = false }
        }, day);

        // 主機對應表：分別為 conflict、unmatched、ok（即使前兩者有填 HostId）
        store.ReplaceHostMapForDate(day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Conflict },
            new() { DeviceObjid = 1002, HostId = 102, MapStatus = PrtgMapStatus.Unmatched },
            new() { DeviceObjid = 1003, HostId = 103, MapStatus = PrtgMapStatus.Ok }
        });

        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";
        var (client, handler) = CreateClient(req => JsonResponse(histJson));

        var fetchService = new PrtgFetchService(client, store, console);
        var fetcher = new PrtgTriggeredValueFetcher(fetchService, store, recordStore, console);

        var result = await fetcher.RunAsync(
            day, whitelist: null, concurrency: 2,
            analysisCompleted: () => true, ct: CancellationToken.None, pollSeconds: 1);

        // 雙面斷言：只有 ok 的 device 有被請求，conflict 與 unmatched 沒有被請求
        Assert.Contains(handler.RequestedUrls, u => u.Contains("id=2003") && u.Contains("historicdata"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("id=2001"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("id=2002"));

        Assert.Equal(3, result.TriggerHosts);
        Assert.Equal(1, result.TargetSensors);
        Assert.Equal(1, result.ValuesWritten);
        Assert.Equal(0, result.FailedSensors);
    }

    [Fact]
    public async Task RunAsync_type不在白名單的sensor不取數()
    {
        var day = new DateTime(2026, 8, 30);
        var store = CreateStore();
        var recordStore = CreateRecordStore();
        var console = new TestConsole();

        recordStore.Append(CreateRecord(101, "SRV-HIGH", day, "高"));

        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "Dev-1", Ip = "10.0.0.1" }
        }, day);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "Sensor-In-Whitelist", SensorType = "SNMP CPU Load", Paused = false },
            new() { Objid = 2002, DeviceObjid = 1001, Name = "Sensor-Outside-Whitelist", SensorType = "Custom Ping Sensor", Paused = false }
        }, day);

        store.ReplaceHostMapForDate(day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Ok }
        });

        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";
        var (client, handler) = CreateClient(req => JsonResponse(histJson));

        var fetchService = new PrtgFetchService(client, store, console);
        var fetcher = new PrtgTriggeredValueFetcher(fetchService, store, recordStore, console);

        // 指定白名單只有 SNMP CPU Load
        var whitelist = new[] { "SNMP CPU Load" };
        var result = await fetcher.RunAsync(
            day, whitelist: whitelist, concurrency: 2,
            analysisCompleted: () => true, ct: CancellationToken.None, pollSeconds: 1);

        // 雙面斷言：白名單內的 sensor 有被請求，白名單外的 sensor 沒有被請求
        Assert.Contains(handler.RequestedUrls, u => u.Contains("id=2001") && u.Contains("historicdata"));
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("id=2002"));

        Assert.Equal(1, result.TriggerHosts);
        Assert.Equal(1, result.TargetSensors);
        Assert.Equal(1, result.ValuesWritten);
        Assert.Equal(0, result.FailedSensors);
    }

    [Fact]
    public async Task RunAsync_同一台主機在兩輪掃描間不重複取數()
    {
        var day = new DateTime(2026, 8, 30);
        var store = CreateStore();
        var recordStore = CreateRecordStore();
        var console = new TestConsole();

        recordStore.Append(CreateRecord(101, "SRV-HIGH", day, "高"));

        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "Dev-1", Ip = "10.0.0.1" }
        }, day);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "Sensor-1", SensorType = "SNMP CPU Load", Paused = false }
        }, day);

        store.ReplaceHostMapForDate(day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Ok }
        });

        var histJson = "{\"histdata\":[{\"datetime\":\"2026-08-30 01:00:00\",\"value_\":10.0,\"coverage\":100}]}";
        var (client, handler) = CreateClient(req => JsonResponse(histJson));

        var fetchService = new PrtgFetchService(client, store, console);
        var fetcher = new PrtgTriggeredValueFetcher(fetchService, store, recordStore, console);

        // 第一次回 false，第二次回 true（模擬兩輪掃描）
        var pollCount = 0;
        bool AnalysisCompleted()
        {
            pollCount++;
            return pollCount >= 2;
        }

        var result = await fetcher.RunAsync(
            day, whitelist: null, concurrency: 2,
            analysisCompleted: AnalysisCompleted, ct: CancellationToken.None, pollSeconds: 1);

        // 斷言該主機 sensor 的 historicdata 請求只出現一次
        var sensorRequests = handler.RequestedUrls
            .Where(u => u.Contains("id=2001") && u.Contains("historicdata"))
            .ToList();
        Assert.Single(sensorRequests);

        Assert.Equal(1, result.TriggerHosts);
        Assert.Equal(1, result.TargetSensors);
        Assert.Equal(1, result.ValuesWritten);
        Assert.Equal(0, result.FailedSensors);
    }

    [Fact]
    public async Task RunAsync_沒有任何問題主機時完全不呼叫API()
    {
        var day = new DateTime(2026, 8, 30);
        var store = CreateStore();
        var recordStore = CreateRecordStore();
        var console = new TestConsole();

        // 只有低風險主機
        recordStore.Append(CreateRecord(101, "SRV-LOW", day, "低"));

        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "Dev-1", Ip = "10.0.0.1" }
        }, day);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "Sensor-1", SensorType = "SNMP CPU Load", Paused = false }
        }, day);

        store.ReplaceHostMapForDate(day, new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Ok }
        });

        var (client, handler) = CreateClient(req => JsonResponse("{\"histdata\":[]}"));

        var fetchService = new PrtgFetchService(client, store, console);
        var fetcher = new PrtgTriggeredValueFetcher(fetchService, store, recordStore, console);

        var result = await fetcher.RunAsync(
            day, whitelist: null, concurrency: 2,
            analysisCompleted: () => true, ct: CancellationToken.None, pollSeconds: 1);

        // 斷言 RequestedUrls 中沒有 historicdata
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("historicdata"));

        // 回傳結果四個數字皆為 0
        Assert.Equal(0, result.TriggerHosts);
        Assert.Equal(0, result.TargetSensors);
        Assert.Equal(0, result.ValuesWritten);
        Assert.Equal(0, result.FailedSensors);
    }
}
