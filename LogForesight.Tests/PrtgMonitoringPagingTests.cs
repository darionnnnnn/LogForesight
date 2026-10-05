using System.Text.Json;
using System.Data.Common;
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

namespace LogForesight.Tests;

public sealed class PrtgMonitoringPagingTests : IDisposable
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

    private const int HostCount = 3000;
    private const int SensorInventoryCount = 30000;
    private const int SelectedSensorCount = 15000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-monitoring-pages-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly RecordingAuditService _audit = new();
    private readonly IDataProtectionProvider _protection = new ServiceCollection().AddDataProtection()
        .Services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();

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

    public PrtgMonitoringPagingTests()
    {
        _backend = new(new StorageSettings { Type = "Sqlite" }, _dir);
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(settings =>
        {
            settings.PrtgEnabled = false;
            settings.PrtgUrl = "https://127.0.0.1";
        });
        _hosts = new HostStore(_backend.Blob("hosts"));
        SeedHostsOnce();
        var store = _backend.PrtgStore();
        var now = DateTime.Today;
        store.UpsertDevices(Enumerable.Range(1, HostCount).Select(id => new PrtgDeviceRow
        {
            Objid = id, Name = $"device-{id}", Ip = $"10.0.{id / 256}.{id % 256}"
        }).ToArray(), now);
        store.UpsertSensors(Enumerable.Range(1, SensorInventoryCount).Select(id => new PrtgSensorRow
        {
            Objid = id, DeviceObjid = (id - 1) / 10 + 1, Name = $"sensor-{id:D5}",
            SensorType = "ping", Category = PrtgSensorCategories.Availability
        }).ToArray(), now);
        store.ReplaceHostMapForDate(now, Enumerable.Range(1, HostCount).Select(id => new PrtgHostMapRow
        {
            MapDate = now, DeviceObjid = id, HostId = id, HostName = $"netiq-{id:D4}", MapStatus = PrtgMapStatus.Ok
        }).ToArray());
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
        var controller = Controller();
        var get = Data(controller.Get());
        var catalogueToken = get.GetProperty("CatalogueToken").GetString()!;

        var searchedHost = Data(controller.HostPage(null, "netiq-3000", catalogueToken));
        Assert.Equal(1, searchedHost.GetProperty("Total").GetInt32());
        Assert.Equal(3000L, searchedHost.GetProperty("Rows")[0].GetProperty("HostId").GetInt64());

        var browsedHosts = new List<long>();
        string? hostCursor = null;
        int? hostTotal = null;
        do
        {
            var page = Data(controller.HostPage(hostCursor, "", catalogueToken));
            hostTotal ??= page.GetProperty("Total").GetInt32();
            Assert.Equal(hostTotal, page.GetProperty("Total").GetInt32());
            foreach (var row in page.GetProperty("Rows").EnumerateArray())
                browsedHosts.Add(row.GetProperty("HostId").GetInt64());
            hostCursor = page.GetProperty("NextCursor").ValueKind == JsonValueKind.Null
                ? null : page.GetProperty("NextCursor").GetString();
        } while (hostCursor is not null);
        Assert.Equal(HostCount, hostTotal!.Value);
        Assert.Equal(HostCount, browsedHosts.Distinct().Count());

        var selectedHosts = browsedHosts.Order().ToList();
        var browsedSensors = new List<long>();
        string? sensorCursor = null;
        int offset = 0;
        int? sensorTotal = null;
        do
        {
            var requestPage = new PrtgMonitoringPageRequest
            {
                CatalogueToken = catalogueToken, Cursor = sensorCursor ?? "", HostIds = selectedHosts, Offset = offset
            };
            var page = Data(controller.SensorPage(requestPage));
            sensorTotal ??= page.GetProperty("Total").GetInt32();
            Assert.Equal(sensorTotal, page.GetProperty("Total").GetInt32());
            foreach (var row in page.GetProperty("Rows").EnumerateArray())
                browsedSensors.Add(row.GetProperty("SensorId").GetInt64());
            sensorCursor = page.GetProperty("NextCursor").ValueKind == JsonValueKind.Null
                ? null : page.GetProperty("NextCursor").GetString();
            offset += page.GetProperty("Rows").GetArrayLength();
        } while (sensorCursor is not null);
        Assert.Equal(SensorInventoryCount, sensorTotal!.Value);
        Assert.Equal(SensorInventoryCount, browsedSensors.Distinct().Count());

        var searchedSensorRequest = new PrtgMonitoringPageRequest
        {
            CatalogueToken = catalogueToken, HostIds = selectedHosts, Search = "sensor-30000", Offset = 0
        };
        var searchedSensor = Data(controller.SensorPage(searchedSensorRequest));
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
        var estimate = Data(controller.Estimate(request));
        Assert.Equal(HostCount, estimate.GetProperty("HostCount").GetInt32());
        Assert.Equal(SelectedSensorCount, estimate.GetProperty("SelectedSensorCount").GetInt32());
        Assert.Equal(HostCount, estimate.GetProperty("EligibleHostCount").GetInt32());
        Assert.Equal(SensorInventoryCount, estimate.GetProperty("AvailableSensorCount").GetInt32());
        Assert.Equal(30, estimate.GetProperty("HostCataloguePageRequestsAt100").GetInt32());
        Assert.Equal(300, estimate.GetProperty("SensorCataloguePageRequestsAt100").GetInt32());
        request.EstimateToken = estimate.GetProperty("EstimateToken").GetString()!;
        Assert.IsType<OkObjectResult>(controller.Put(request));

        var saved = Data(controller.Get());
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
