using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgProbeDataFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-probe-flow-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly FakeHostStore _hosts = new();
    private readonly DateTime _day = DateTime.Today.AddDays(-1);

    public PrtgProbeDataFlowTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" 
        }, _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class Output : IRunConsole
    {
        public readonly List<string> Lines = new();
        public void WriteLine(string message = "") => Lines.Add(message);
        public override string ToString() => string.Join("\n", Lines);
    }

    private sealed class HistoricHandler(string response) : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }

    private void Seed()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "srv-a", Active = true });
        var other = _hosts.Upsert(new WebHost { HostName = "srv-b", Active = true });
        var store = _backend.PrtgStore();
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "SNMP CPU Load" },
            new PrtgSensorRow { Objid = 202, DeviceObjid = 101, Name = "Memory", SensorType = "SNMP Memory" },
            new PrtgSensorRow { Objid = 301, DeviceObjid = 102, Name = "CPU other", SensorType = "SNMP CPU Load" }
        }, DateTime.Now);
        store.ReplaceHostMapForDate(DateTime.Today, new[]
        {
            new PrtgHostMapRow { MapDate = DateTime.Today, DeviceObjid = 101, HostId = host.HostId, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { MapDate = DateTime.Today, DeviceObjid = 102, HostId = other.HostId, MapStatus = PrtgMapStatus.Ok }
        });
    }

    private (PrtgClient Client, HistoricHandler Handler) Client(string response)
    {
        var handler = new HistoricHandler(response);
        return (new PrtgClient("https://prtg.example.com", "test-token", 30, false, handler,
            PrtgAuthModes.Token, "", "", ""), handler);
    }

    [Fact]
    public async Task 一台一顆一天_正式取值寫入讀回_其他sensor零請求零寫入()
    {
        Seed();
        var (client, handler) = Client($"{{\"histdata\":[{{\"datetime\":\"{_day:yyyy-MM-dd} 01:00:00\",\"value_raw\":42.5,\"coverage_raw\":10000}}]}}");
        using (client)
        {
            var output = new Output();
            Assert.True(await PrtgProbeDataFlowRunner.RunAsync(client, _backend, _hosts,
                new SystemSettings(), output, targetDay: _day));
            Assert.Contains("有效數值 1 筆", output.ToString());
            Assert.Contains("只驗 1 顆", output.ToString());
        }
        Assert.Single(handler.Urls);
        Assert.Contains("historicdata.json?id=201", handler.Urls[0]);
        Assert.Single(_backend.PrtgStore().GetValuesForSensor(201, _day, _day.AddDays(1)));
        Assert.Empty(_backend.PrtgStore().GetValuesForSensor(202, _day, _day.AddDays(1)));
        Assert.Empty(_backend.PrtgStore().GetValuesForSensor(301, _day, _day.AddDays(1)));
    }

    [Fact]
    public async Task 只有無效數值_不可宣稱資料流通過()
    {
        Seed();
        var (client, _) = Client($"{{\"histdata\":[{{\"datetime\":\"{_day:yyyy-MM-dd} 01:00:00\",\"value_raw\":42.5,\"coverage_raw\":0}}]}}");
        using (client)
        {
            var output = new Output();
            Assert.False(await PrtgProbeDataFlowRunner.RunAsync(client, _backend, _hosts,
                new SystemSettings(), output, targetDay: _day));
            Assert.Contains("沒有可用的數值", output.ToString());
        }
    }

    [Fact]
    public async Task 既有舊數值不可讓本次無效回應誤判通過()
    {
        Seed();
        _backend.PrtgStore().UpsertValues(new[]
        {
            new PrtgValueRow { SensorObjid = 201, PeriodStart = _day.AddHours(2),
                AvgValue = 88, Quality = PrtgDataQuality.Ok, CreatedAt = _day }
        });
        var (client, _) = Client($"{{\"histdata\":[{{\"datetime\":\"{_day:yyyy-MM-dd} 01:00:00\",\"value_raw\":1,\"coverage_raw\":0}}]}}");
        using (client)
        {
            var output = new Output();
            Assert.False(await PrtgProbeDataFlowRunner.RunAsync(client, _backend, _hosts,
                new SystemSettings(), output, targetDay: _day));
            Assert.Contains("沒有可用的數值", output.ToString());
        }
    }

    [Fact]
    public async Task 無主機對應_不打PRTG也不寫入()
    {
        var (client, handler) = Client("{}");
        using (client)
        {
            var output = new Output();
            Assert.False(await PrtgProbeDataFlowRunner.RunAsync(client, _backend, _hosts,
                new SystemSettings(), output, targetDay: _day));
            Assert.Contains("找不到已對應", output.ToString());
        }
        Assert.Empty(handler.Urls);
    }
}
