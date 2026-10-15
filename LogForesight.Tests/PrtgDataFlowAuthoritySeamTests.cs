using System.Net;
using System.Net.Sockets;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

[Collection("KnownIssueCatalogState")]
public sealed class PrtgDataFlowAuthoritySeamTests
{
    [Theory]
    [InlineData("date-format", true)]
    [InlineData("raw-a-b-a", false)]
    [InlineData("scope", false)]
    [InlineData("source", false)]
    [InlineData("settings", false)]
    public async Task ActualBackgroundDataFlowRechecksAuthorityAfterHttpBeforeWritingOneValue(string change, bool succeeds)
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-flow-authority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var listener = new HttpListener();
        var port = 0;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 15) { }
        }
        try
        {
            var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, root);
            var hosts = new HostStore(backend.Blob("hosts"));
            var host = hosts.Upsert(new WebHost { HostName = "single-flow-host", Active = true });
            var settings = new SystemSettingsStore(backend.Blob("system_settings"));
            settings.Update(value =>
            {
                value.PrtgUrl = $"http://127.0.0.1:{port}";
                value.PrtgAuthMode = PrtgAuthModes.Token;
                value.PrtgApiTokenEnc = CryptoHelper.Encrypt("synthetic-flow-authority");
                value.PrtgTimeoutSeconds = 5;
                value.PrtgSensorTypeWhitelist = ["CPU"];
            });
            var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
            policy.Update(value =>
            {
                value.CoreSystemId = "flow-core"; value.SourceGeneration = "flow-source";
                value.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.Get().PrtgUrl);
                value.ValidFrom = DateTimeOffset.UtcNow.AddDays(-2);
                value.HostIds = [host.HostId]; value.SensorIds = [201];
                value.SourceTimeZoneId = "UTC"; value.SourceCultureName = "en-US";
                value.RawTimestampTimeZoneId = "UTC"; value.AnalysisTimeZoneId = "UTC";
                value.TimeBasisEvidenceReference = "synthetic-flow-time-basis";
            });
            backend.PrtgStore().UpsertSensors([new PrtgSensorRow
                { Objid = 201, DeviceObjid = 101, Name = "CPU", SensorType = "CPU" }], DateTime.UtcNow);
            backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today, [new PrtgHostMapRow
                { MapDate = DateTime.Today, DeviceObjid = 101, HostId = host.HostId, MapStatus = PrtgMapStatus.Ok }]);
            var service = new PrtgProbeService(settings, new PrtgProbeRunState(), new PrtgBackfillRunState(),
                backend, hosts, new FakeSentinelStore(), new SchedulerRunState(), new PrtgStructureSyncRunState());
            var budget = new PrtgRequestBudget();
            budget.SetHistoricCoordinator(new SqlPrtgHistoricRequestCoordinator(backend));
            service.ClientFactoryForTesting = value => PrtgClientFactory.Create(value, budget: budget);
            var responseTask = Task.Run(async () =>
            {
                var request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Contains("historicdata.json", request.Request.RawUrl);
                if (change == "date-format") policy.Update(value => value.MessageDateFormat = PrtgMessageDateTimeParser.Ymd24);
                else if (change == "raw-a-b-a")
                {
                    policy.Update(value => value.RawTimestampTimeZoneId = "Pacific Standard Time");
                    policy.Update(value => value.RawTimestampTimeZoneId = "UTC");
                }
                else if (change == "scope") policy.Update(value => value.SensorIds = []);
                else if (change == "source") policy.Update(value => value.SourceGeneration = "flow-replacement");
                else settings.Update(value => value.PrtgTimeoutSeconds = 6);
                var body = Encoding.UTF8.GetBytes($"{{\"histdata\":[{{\"datetime\":\"{DateTime.Today.AddDays(-1):yyyy-MM-dd} 01:00:00\",\"value_raw\":42.5,\"coverage_raw\":10000}}]}}");
                request.Response.ContentType = "application/json";
                request.Response.ContentLength64 = body.Length;
                await request.Response.OutputStream.WriteAsync(body);
                request.Response.Close();
            });
            Assert.True(service.TryStartDataFlow(out var error, out _), error);
            try { await responseTask; }
            catch (TimeoutException)
            {
                service.TryCancel();
                Assert.Fail("No request reached the owned listener: " + string.Join(" | ", service.GetStatus().Output));
            }
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (service.GetStatus().IsRunning && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.False(service.GetStatus().IsRunning);
            Assert.True(service.GetStatus().Success == succeeds, string.Join(" | ", service.GetStatus().Output));
            if (!succeeds) Assert.True(service.GetStatus().Cancelled, string.Join(" | ", service.GetStatus().Output));
            var rows = backend.PrtgStore().GetValuesForSensor(201, DateTime.Today.AddDays(-1), DateTime.Today);
            Assert.Equal(succeeds ? 1 : 0, rows.Count);
        }
        finally
        {
            listener.Stop();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
