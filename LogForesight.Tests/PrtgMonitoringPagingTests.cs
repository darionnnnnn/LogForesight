using System.Text.Json;
using System.Data.Common;
using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

public sealed class PrtgMonitoringRequestMetadataTests
{
    [Theory]
    [InlineData(typeof(PrtgMonitoringPageRequest), nameof(PrtgMonitoringPageRequest.Cursor))]
    [InlineData(typeof(PrtgMonitoringPageRequest), nameof(PrtgMonitoringPageRequest.Search))]
    [InlineData(typeof(PrtgMonitoringRequest), nameof(PrtgMonitoringRequest.EstimateToken))]
    public void 選填請求欄位不會被Mvc隱含必填驗證拒絕(Type requestType, string property)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var metadata = provider.GetRequiredService<IModelMetadataProvider>()
            .GetMetadataForType(requestType);
        Assert.False(metadata.Properties[property]!.IsRequired);
        var request = JsonSerializer.Deserialize("{\"" + property + "\":null}", requestType)!;
        Assert.Null(requestType.GetProperty(property)!.GetValue(request));
    }

}

public sealed class PrtgMonitoringPagingTests : IDisposable
{
    private const int HostCount = 3000;
    private const int SensorInventoryCount = 30000;
    private const int SelectedSensorCount = 15000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-monitoring-pages-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly RecordingAuditService _audit = new();
    private readonly IDataProtectionProvider _protection = new ServiceCollection().AddDataProtection()
        .Services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    private readonly ITestOutputHelper _output;

    private sealed class Visible(long[] ids) : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => ids.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long id) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => null;
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long id) { }
    }

    public PrtgMonitoringPagingTests(ITestOutputHelper output)
    {
        _output = output;
        var setup = Stopwatch.StartNew();
        _backend = new(new StorageSettings { Type = "Sqlite" }, _dir);
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(settings =>
        {
            settings.PrtgEnabled = false;
            settings.PrtgUrl = "https://127.0.0.1";
        });
        _hosts = new HostStore(_backend.Blob("hosts"));
        LogPhase($"paging-stage test-fixture-setup complete elapsedMs={setup.Elapsed.TotalMilliseconds:F1}");
    }

    private void LogPhase(string message)
    {
        _output.WriteLine(message);
        File.AppendAllText(Path.Combine(_dir, "paging-phase.log"),
            $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
    }

    private void SeedFullScaleMirror()
    {
        var phase = Stopwatch.StartNew();
        LogPhase("paging-stage seed-hosts begin");
        SeedHostsOnce();
        LogPhase($"paging-stage seed-hosts complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1}");
        var store = _backend.PrtgStore();
        var now = DateTime.Today;
        phase.Restart();
        LogPhase("paging-stage seed-devices begin count=3000");
        store.UpsertDevices(Enumerable.Range(1, HostCount).Select(id => new PrtgDeviceRow
        {
            Objid = id, Name = $"device-{id}", Ip = $"10.0.{id / 256}.{id % 256}"
        }).ToArray(), now);
        LogPhase($"paging-stage seed-devices complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1}");
        phase.Restart();
        LogPhase("paging-stage seed-sensors begin count=30000");
        store.UpsertSensors(Enumerable.Range(1, SensorInventoryCount).Select(id => new PrtgSensorRow
        {
            Objid = id, DeviceObjid = (id - 1) / 10 + 1, Name = $"sensor-{id:D5}",
            SensorType = "ping", Category = PrtgSensorCategories.Availability
        }).ToArray(), now);
        LogPhase($"paging-stage seed-sensors complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1}");
        phase.Restart();
        LogPhase("paging-stage seed-host-map begin count=3000");
        store.ReplaceHostMapForDate(now, Enumerable.Range(1, HostCount).Select(id => new PrtgHostMapRow
        {
            MapDate = now, DeviceObjid = id, HostId = id, HostName = $"netiq-{id:D4}", MapStatus = PrtgMapStatus.Ok
        }).ToArray());
        LogPhase($"paging-stage seed-host-map complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1}");
    }

    private void SeedHostsOnce()
    {
        var hosts = Enumerable.Range(1, HostCount).Select(id => new WebHost
        {
            HostId = id, HostName = $"netiq-{id:D4}", Source = "netiq", Active = true
        }).ToList();
        _backend.Blob("hosts").Mutate(raw => (JsonSerializer.Serialize(hosts), 0));
    }

    private PrtgMonitoringController Controller() => new(_backend, _hosts,
        new Visible(Enumerable.Range(1, HostCount).Select(id => (long)id).ToArray()),
        FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit, new DataVersionStamp(), _protection);

    private static JsonElement Data(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return document.RootElement.GetProperty("Data").Clone();
    }

    [Fact]
    public void BrowseFull30000MirrorAcross100RowPagesThenSaveExact15000Selection()
    {
        var fullRun = Stopwatch.StartNew();
        LogPhase("paging-stage full-workload begin hosts=3000 sensors=30000 selectedSensors=15000 pageSize=100");
        var phase = Stopwatch.StartNew();
        SeedFullScaleMirror();
        LogPhase($"paging-stage fixture-seed complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1}");
        var identityStore = _backend.PrtgStore();
        var authorityRevisions = identityStore.ReadResourceAuthorityRevisions([1, HostCount]);
        var observationRevisions = identityStore.ReadResourceObservationRevisions([1, HostCount]);
        Assert.Equal(10, authorityRevisions[1]);
        Assert.Equal(10, authorityRevisions[HostCount]);
        Assert.Equal(10, observationRevisions[1]);
        Assert.Equal(10, observationRevisions[HostCount]);
        LogPhase("paging-stage revision-semantics verified sampledHosts=2 revisionsPerHost=10");
        var controller = Controller();
        phase.Restart();
        LogPhase("paging-stage initial-read begin");
        var get = Data(controller.Get());
        LogPhase($"paging-stage initial-read complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1}");
        var catalogueToken = get.GetProperty("CatalogueToken").GetString()!;

        phase.Restart();
        var searchedHost = Data(controller.HostPage(null, "netiq-3000", catalogueToken));
        LogPhase($"paging-stage host-search complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1}");
        Assert.Equal(1, searchedHost.GetProperty("Total").GetInt32());
        Assert.Equal(3000L, searchedHost.GetProperty("Rows")[0].GetProperty("HostId").GetInt64());

        var browsedHosts = new List<long>();
        string? hostCursor = null;
        int? hostTotal = null;
        var hostPages = 0;
        var worstHostPageMs = 0d;
        phase.Restart();
        var pageClock = new Stopwatch();
        do
        {
            pageClock.Restart();
            var page = Data(controller.HostPage(hostCursor, "", catalogueToken));
            worstHostPageMs = Math.Max(worstHostPageMs, pageClock.Elapsed.TotalMilliseconds);
            hostPages++;
            hostTotal ??= page.GetProperty("Total").GetInt32();
            Assert.Equal(hostTotal, page.GetProperty("Total").GetInt32());
            foreach (var row in page.GetProperty("Rows").EnumerateArray())
                browsedHosts.Add(row.GetProperty("HostId").GetInt64());
            hostCursor = page.GetProperty("NextCursor").ValueKind == JsonValueKind.Null
                ? null : page.GetProperty("NextCursor").GetString();
            if (hostPages % 10 == 0)
                LogPhase($"paging-progress hosts pages={hostPages} elapsedMs={phase.Elapsed.TotalMilliseconds:F1} worstPageMs={worstHostPageMs:F1}");
        } while (hostCursor is not null);
        LogPhase($"paging-stage host-pages complete pages={hostPages} elapsedMs={phase.Elapsed.TotalMilliseconds:F1} worstPageMs={worstHostPageMs:F1}");
        Assert.Equal(HostCount, hostTotal!.Value);
        Assert.Equal(HostCount, browsedHosts.Distinct().Count());

        var selectedHosts = browsedHosts.Order().ToList();
        var browsedSensors = new List<long>();
        string? sensorCursor = null;
        int offset = 0;
        int? sensorTotal = null;
        var sensorPages = 0;
        var worstSensorPageMs = 0d;
        phase.Restart();
        do
        {
            var requestPage = new PrtgMonitoringPageRequest
            {
                CatalogueToken = catalogueToken, Cursor = sensorCursor ?? "", HostIds = selectedHosts, Offset = offset
            };
            pageClock.Restart();
            var page = Data(controller.SensorPage(requestPage));
            worstSensorPageMs = Math.Max(worstSensorPageMs, pageClock.Elapsed.TotalMilliseconds);
            sensorPages++;
            sensorTotal ??= page.GetProperty("Total").GetInt32();
            Assert.Equal(sensorTotal, page.GetProperty("Total").GetInt32());
            foreach (var row in page.GetProperty("Rows").EnumerateArray())
                browsedSensors.Add(row.GetProperty("SensorId").GetInt64());
            sensorCursor = page.GetProperty("NextCursor").ValueKind == JsonValueKind.Null
                ? null : page.GetProperty("NextCursor").GetString();
            offset += page.GetProperty("Rows").GetArrayLength();
            if (sensorPages % 25 == 0)
                LogPhase($"paging-progress sensors pages={sensorPages} rows={offset} elapsedMs={phase.Elapsed.TotalMilliseconds:F1} worstPageMs={worstSensorPageMs:F1}");
        } while (sensorCursor is not null);
        LogPhase($"paging-stage sensor-pages complete pages={sensorPages} rows={offset} elapsedMs={phase.Elapsed.TotalMilliseconds:F1} worstPageMs={worstSensorPageMs:F1}");
        Assert.Equal(SensorInventoryCount, sensorTotal!.Value);
        Assert.Equal(SensorInventoryCount, browsedSensors.Distinct().Count());

        var searchedSensorRequest = new PrtgMonitoringPageRequest
        {
            CatalogueToken = catalogueToken, HostIds = selectedHosts, Search = "sensor-30000", Offset = 0
        };
        phase.Restart();
        var searchedSensor = Data(controller.SensorPage(searchedSensorRequest));
        LogPhase($"paging-stage sensor-search complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1}");
        Assert.Equal(1, searchedSensor.GetProperty("Total").GetInt32());
        Assert.Equal(30000L, searchedSensor.GetProperty("Rows")[0].GetProperty("SensorId").GetInt64());

        var browsedSensorSet = browsedSensors.ToHashSet();
        var selectedSensors = browsedSensors.Take(SelectedSensorCount).Order().ToList();
        Assert.All(selectedSensors, id => Assert.Contains(id, browsedSensorSet));
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var request = new PrtgMonitoringRequest
        {
            Revision = policy.Revision, IdentityConfirmed = true, CoreSystemId = "large-fixture-core",
            SourceTimeZoneId = TimeZoneInfo.Local.Id, SourceCultureName = "en-US",
            HostIds = selectedHosts, SensorIds = selectedSensors, CatalogueToken = catalogueToken
        };
        phase.Restart();
        LogPhase("paging-stage estimate begin");
        var estimate = Data(controller.Estimate(request));
        LogPhase($"paging-stage estimate complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1}");
        Assert.Equal(HostCount, estimate.GetProperty("HostCount").GetInt32());
        Assert.Equal(SelectedSensorCount, estimate.GetProperty("SelectedSensorCount").GetInt32());
        Assert.Equal(HostCount, estimate.GetProperty("EligibleHostCount").GetInt32());
        Assert.Equal(SensorInventoryCount, estimate.GetProperty("AvailableSensorCount").GetInt32());
        Assert.Equal(30, estimate.GetProperty("HostCataloguePageRequestsAt100").GetInt32());
        Assert.Equal(300, estimate.GetProperty("SensorCataloguePageRequestsAt100").GetInt32());
        request.EstimateToken = estimate.GetProperty("EstimateToken").GetString()!;
        phase.Restart();
        LogPhase("paging-stage save begin");
        Assert.IsType<OkObjectResult>(controller.Put(request));
        LogPhase($"paging-stage save complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1}");

        phase.Restart();
        var saved = Data(controller.Get());
        LogPhase($"paging-stage final-read complete elapsedMs={phase.Elapsed.TotalMilliseconds:F1} fullWorkloadElapsedMs={fullRun.Elapsed.TotalMilliseconds:F1}");
        Assert.Equal(selectedHosts, saved.GetProperty("HostIds").EnumerateArray().Select(x => x.GetInt64()).ToArray());
        Assert.Equal(selectedSensors, saved.GetProperty("SensorIds").EnumerateArray().Select(x => x.GetInt64()).ToArray());
    }

    [Fact]
    public void SensorPagesCrossing100To101CountOnceAndContinueWithBoundedKnownTotal()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var counter = new CountCommands();
        LfDbContext NewContext() => new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection).AddInterceptors(counter).Options);
        using (var context = NewContext())
        {
            context.Database.EnsureCreated();
            context.PrtgDevices.Add(new PrtgDeviceRow { Objid = 1, Name = "d" });
            context.PrtgSensors.AddRange(Enumerable.Range(1, 101).Select(id => new PrtgSensorRow
            {
                Objid = id, DeviceObjid = 1, Name = $"sensor-{id}", SensorType = "ping"
            }));
            context.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                MapDate = new DateTime(2026, 10, 3), DeviceObjid = 1, HostId = 1,
                HostName = "host", MapStatus = PrtgMapStatus.Ok
            });
            context.SaveChanges();
        }
        counter.Reset();
        var store = new EfPrtgStore(NewContext);

        var first = store.GetMonitoringSensorPage(new DateTime(2026, 10, 3), [1], null, 0, 100);
        var second = store.GetMonitoringSensorPage(new DateTime(2026, 10, 3), [1], null, 100, 100, first.Total);

        Assert.Equal(101, first.Total);
        Assert.Equal(100, first.Rows.Count);
        Assert.Single(second.Rows);
        Assert.Equal(101, first.Rows.Select(x => x.SensorId).Concat(second.Rows.Select(x => x.SensorId)).Distinct().Count());
        Assert.Equal(1, counter.SensorCountQueries);
    }

    private sealed class CountCommands : DbCommandInterceptor
    {
        public int SensorCountQueries { get; private set; }
        public void Reset() => SensorCountQueries = 0;
        private void Record(DbCommand command)
        {
            if (command.CommandText.Contains("lf_prtg_sensors", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase))
                SensorCountQueries++;
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result)
        {
            Record(command);
            return base.ScalarExecuting(command, eventData, result);
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
