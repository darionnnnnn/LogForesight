using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Controlled synthetic E05 acceptance. The HTTP handler is a shaped test source; it is not
/// evidence of native PRTG compatibility. The production mapper, API controllers, pilots,
/// snapshot worker, and resource guard probe perform the actual workflow.
/// </summary>
[Collection("PrtgSnapshotSharedBudget")]
public sealed class PrtgFixedScopeWorkflowIntegrationTests : IDisposable
{
    private const string Url = "https://10.53.0.201";
    private const string Token = "synthetic-only-token";
    private const long MirrorSensorCount = 42_998;
    private const long OriginalScopeSensors = 126;
    private const long OriginalOutsideSensors = 42_872;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "lf-prtg-e05-integration-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly HostStore hosts;
    private readonly SystemSettingsStore settings;
    private readonly RecordingAuditService audit = new();
    private readonly IDataProtectionProvider protection = new ServiceCollection().AddDataProtection()
        .Services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    private readonly ControlledPrtgHandler source = new();

    public PrtgFixedScopeWorkflowIntegrationTests()
    {
        Directory.CreateDirectory(directory);
        backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "e05.db")}"
        }, directory);
        hosts = new HostStore(backend.Blob("hosts"));
        settings = new SystemSettingsStore(backend.Blob("system_settings"));
    }

    public void Dispose()
    {
        source.Dispose();
        PrtgRequestBudget.Shared.ClearAdmissionPlan();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FixedScopeFlowsThroughRealMappingSavedPolicyCapacityAdmissionSnapshotAndGuardRead()
    {
        var hostRows = CreateHostRows();
        hosts.MutateBatch(rows => rows.AddRange(hostRows));
        var sentinelStore = new SentinelStore(backend.Blob("sentinels"));
        sentinelStore.Upsert(new Sentinel { Name = "Synthetic Guard A", BaseUrl = "https://10.53.0.202:8443", Active = true });
        sentinelStore.Upsert(new Sentinel { Name = "Synthetic Guard B", BaseUrl = "https://10.53.0.203:8443", Active = true });

        var systemSettings = CreateSystemSettingsService();
        var syncState = new PrtgStructureSyncRunState();
        var schedulerState = new SchedulerRunState();
        var backfillState = new PrtgBackfillRunState();
        var probeState = new PrtgProbeRunState();
        var lifetime = new FakeHostApplicationLifetime();
        var sync = new PrtgStructureSyncService(settings, backend, syncState, schedulerState, hosts,
            new PrtgStructureSyncStatusStore(backend.Blob(PrtgStructureSyncStatusStore.BlobKey)),
            backfillState, sentinelStore, new DataVersionStamp(), lifetime);
        sync.ClientFactoryForTests = value => CreateClient(value);

        // The first save is the real disabled setup path. The resource guard can be prepared
        // while sampling is disabled; its URL matches device 21, while the sentinels match 22/23.
        // This makes the real structure sync fetch all three guard devices without bypassing
        // any enabled-sampling capacity admission.
        var admin = CreateSettingsController(systemSettings, sync);
        var savedDisabled = admin.UpdatePrtg(new UpdatePrtgSettingsRequest
        {
            ExpectedRevision = settings.Get().Revision,
            PrtgEnabled = false,
            PrtgUrl = Url,
            PrtgAuthMode = PrtgAuthModes.Token,
            PrtgApiToken = Token,
            PrtgTimeoutSeconds = 30,
            PrtgFetchStrategy = PrtgFetchStrategy.Conservative,
            PrtgSensorTypeWhitelist = ["ping"],
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardSensorObjids = []
        });
        Assert.True(savedDisabled.Success);

        // The route starts the production background structure sync. It reads the 20 business
        // devices plus 3 detected guard devices, and the production mapper must yield 18 OK + 2 conflicts.
        var started = admin.StartPrtgStructureSync();
        Assert.True(started.Success);
        var waitUntil = DateTime.UtcNow.AddSeconds(120);
        while (sync.IsRunning && DateTime.UtcNow < waitUntil) await Task.Delay(25);
        Assert.False(sync.IsRunning);
        var syncStatus = sync.GetStatus();
        Assert.True(syncStatus.LastSuccess, syncStatus.LastErrorMessage);
        Assert.Equal(18, syncStatus.LastMapOk);
        Assert.Equal(2, syncStatus.LastMapConflict);
        var structureRequests = source.Requested;
        Assert.Single(structureRequests.Where(url => url.Contains("content=devices", StringComparison.OrdinalIgnoreCase)));
        var synchronizedDeviceIds = structureRequests.Where(url => url.Contains("content=sensors", StringComparison.OrdinalIgnoreCase) &&
                url.Contains("count=5000", StringComparison.OrdinalIgnoreCase))
            .Select(url => QueryLong(new Uri(url).Query, "id")).Where(id => id > 0).Distinct().Order().ToArray();
        Assert.Equal(23, synchronizedDeviceIds.Length);
        Assert.Equal(Enumerable.Range(1, 23).Select(id => (long)id), synchronizedDeviceIds);

        var store = backend.PrtgStore();
        var originalInScope = store.GetAllSensors().Count;
        Assert.Equal(OriginalScopeSensors + 6, originalInScope);
        Assert.Equal(OriginalScopeSensors, store.GetAllSensors().Count(row => row.DeviceObjid <= 20));
        Assert.Equal(6, store.GetAllSensors().Count(row => row.DeviceObjid is >= 21 and <= 23));
        var latestMap = store.GetLatestHostMapWithDate();
        Assert.Equal(18, latestMap.Rows.Count(row => row.MapStatus == PrtgMapStatus.Ok));
        Assert.Equal(2, latestMap.Rows.Count(row => row.MapStatus == PrtgMapStatus.Conflict));

        // The 42,872 outside sensors are explicitly synthetic mirror population. They are
        // inserted only after structure sync so that the real sync's stale-row cleanup cannot
        // confuse them with this controlled outside-scope fixture.
        var outsideDeviceId = 990_000L;
        store.UpsertDevices([new PrtgDeviceRow { Objid = outsideDeviceId, Name = "Synthetic outside population", Ip = null }], DateTime.Now);
        var guardSensors = store.GetAllSensors().Where(row => row.DeviceObjid is >= 21 and <= 23)
            .Select(row => row.Objid).Order().ToArray();
        Assert.Equal(6, guardSensors.Length);
        var outsideIncludingGuard = OriginalOutsideSensors - guardSensors.Length;
        for (var offset = 0L; offset < outsideIncludingGuard; offset += 1000)
        {
            var count = (int)Math.Min(1000, outsideIncludingGuard - offset);
            var rows = Enumerable.Range(0, count).Select(index => new PrtgSensorRow
            {
                Objid = 1_000_000 + offset + index, DeviceObjid = outsideDeviceId,
                Name = "Synthetic outside mirror row", SensorType = "synthetic-outside", Status = "Up", Paused = false
            }).ToArray();
            store.UpsertSensors(rows, DateTime.Now);
        }
        Assert.Equal(MirrorSensorCount, store.GetAllSensors().Count);
        Assert.Equal(OriginalOutsideSensors,
            store.GetAllSensors().Count - OriginalScopeSensors);
        Assert.Equal(OriginalOutsideSensors - guardSensors.Length,
            store.GetAllSensors().Count - OriginalScopeSensors - guardSensors.Length);

        // Walk the real R01 catalogue, estimate, source preview, and save endpoints. Only the
        // 18 unambiguous business hosts and their 108 ping sensors enter the business policy.
        var monitoring = CreateMonitoringController();
        var initial = Assert.IsType<OkObjectResult>(monitoring.Get());
        var initialData = Data(initial.Value);
        var hostIds = hostRows.Where(row => row.HostName.StartsWith("synthetic-business-", StringComparison.Ordinal))
            .Select(row => row.HostId).Order().ToList();
        var businessDeviceIds = Enumerable.Range(1, 18).Select(id => (long)id).ToArray();
        var businessSensorIds = businessDeviceIds.SelectMany(device => Enumerable.Range(1, 6).Select(index => 30_000 + device * 10 + index))
            .Order().ToList();
        var request = new PrtgMonitoringRequest
        {
            Revision = initialData.GetProperty("Revision").GetString()!,
            IdentityConfirmed = true,
            SourceChangeMode = "new",
            CoreSystemId = "synthetic-controlled-source",
            SourceTimeZoneId = "UTC",
            SourceCultureName = "en-US",
            HostIds = hostIds,
            SensorIds = businessSensorIds,
            CatalogueToken = initialData.GetProperty("CatalogueToken").GetString()!
        };
        var hostPage = Assert.IsType<OkObjectResult>(monitoring.HostPage(null, catalogueToken: request.CatalogueToken));
        Assert.Equal(18, Data(hostPage.Value).GetProperty("Total").GetInt32());
        var sensorPage = Assert.IsType<OkObjectResult>(monitoring.SensorPage(new PrtgMonitoringPageRequest
        {
            CatalogueToken = request.CatalogueToken, HostIds = hostIds
        }));
        Assert.Equal(108, Data(sensorPage.Value).GetProperty("Total").GetInt32());
        var estimate = Assert.IsType<OkObjectResult>(monitoring.Estimate(request));
        request.EstimateToken = Data(estimate.Value).GetProperty("EstimateToken").GetString();
        Assert.IsType<OkObjectResult>(monitoring.SourcePreview(request));
        Assert.IsType<OkObjectResult>(monitoring.Put(request));
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        Assert.Equal(18, policy.HostIds.Count);
        Assert.Equal(108, policy.SensorIds.Count);
        Assert.True(policy.Ready(Url));

        // Guard auto-detection derives three device families and six CPU/memory sensor IDs
        // from the synchronized mirror, then an explicit disabled-settings save records them.
        var guardDetection = PrtgResourceGuardTargets.Detect(new PrtgMirrorGuardSource(store), settings.Get(),
            sentinelStore.GetAll(), new TestConsole(), new PrtgAddressResolver());
        var detectedGuardSensors = guardDetection.Targets.SensorObjids.Order().ToArray();
        Assert.Equal(6, detectedGuardSensors.Length);
        Assert.Equal(guardSensors, detectedGuardSensors);
        Assert.Equal(3, guardDetection.SentinelDeviceObjids.Concat(guardDetection.PrtgDeviceObjids).Distinct().Count());
        var savedGuard = admin.UpdatePrtg(new UpdatePrtgSettingsRequest
        {
            ExpectedRevision = settings.Get().Revision,
            PrtgEnabled = false,
            PrtgResourceGuardEnabled = true,
            PrtgResourceGuardSensorObjids = detectedGuardSensors.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList()
        });
        Assert.True(savedGuard.Success);

        // Let the actual timeline collector establish current identity for the five canonical
        // profile-pilot sensors through controlled HTTP. This writes timeline coverage only; no
        // binding qualification proof, qualification job, or outcome is fabricated.
        var profileIds = policy.SensorIds.Order().Take(PrtgProfileTransportCapacityPilot.MaximumSensorIds).ToArray();
        var sensorDevices = store.GetAllSensors().Where(row => profileIds.Contains(row.Objid))
            .ToDictionary(row => row.Objid, row => row.DeviceObjid);
        var currentHostByDevice = store.GetLatestHostMapWithDate().Rows
            .Where(row => row.MapStatus == PrtgMapStatus.Ok && row.HostId.HasValue)
            .ToDictionary(row => row.DeviceObjid, row => row.HostId!.Value);
        foreach (var sensorId in profileIds)
        {
            if (!sensorDevices.TryGetValue(sensorId, out var deviceId) ||
                !currentHostByDevice.TryGetValue(deviceId, out var hostId))
                throw new InvalidOperationException("missing-current-host-map");
            using var client = CreateClient(settings.Get(), advanceRateLimitWithoutWaiting: true);
            await new PrtgSensorTimelineCollector(backend, client).CollectAsync(sensorId, hostId, policy, CancellationToken.None);
            Assert.True(store.GetResourceIdentity(sensorId).Active);
        }

        var snapshotPilot = new PrtgSnapshotCapacityPilot(backend, value => CreateClient(value));
        var snapshotResult = await snapshotPilot.RunAsync(hosts, settings.Get(), sentinelStore.GetAll(), CancellationToken.None);
        Assert.True(snapshotResult.Status == "capacity-qualified",
            $"Snapshot capacity status={snapshotResult.Status}; reason={snapshotResult.Reason}; mode={snapshotResult.PrimaryChannelCapabilityMode}; " +
            $"samples={snapshotResult.MatchingFullBatchSamples}; requests={snapshotResult.RequestsSent}/{snapshotResult.RequestsAttempted}; targets={snapshotResult.TargetCount}.");
        Assert.Equal(114, snapshotResult.TargetCount);
        Assert.Equal(5, new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey))
            .Read().Count(sample => sample.Outcome == "success"));

        var profilePilot = new PrtgProfileTransportCapacityPilot(backend, value => CreateClient(value), hosts);
        PrtgProfileTransportCapacityPilot.Result? profileResult = null;
        for (var index = 0; index < 5; index++)
            profileResult = await profilePilot.RunAsync(profileIds, CancellationToken.None);
        Assert.True(profileResult!.Status == "capacity-qualified",
            $"Profile capacity status={profileResult.Status}; reason={profileResult.Reason}; samples={profileResult.MatchingFreshSuccessfulSamples}; " +
            $"requests={profileResult.RequestsSent}/{profileResult.RequestsAttempted}; elapsed={profileResult.ElapsedMilliseconds}; " +
            $"nonAdmission={profileResult.NonAdmissionElapsedMilliseconds}; admissionWait={profileResult.AdmissionWaitMilliseconds}.");
        Assert.Equal(5, profileResult.MatchingFreshSuccessfulSamples);

        // Only measured current-scope samples allow the real settings endpoint to publish the
        // shared admission plan and enable sampling.
        var enabled = admin.UpdatePrtg(new UpdatePrtgSettingsRequest
        {
            ExpectedRevision = settings.Get().Revision,
            PrtgEnabled = true
        });
        Assert.True(enabled.Success);
        var plan = new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey))
            .ReadCurrent(DateTimeOffset.UtcNow);
        Assert.NotNull(plan);

        var service = CreateSnapshotService(sync, schedulerState, backfillState, probeState, lifetime, sentinelStore, plan!);
        await service.TickAsync();
        Assert.Contains(source.Requested, url => url.Contains("filter_objid=", StringComparison.OrdinalIgnoreCase));
        var tickSensors = source.SnapshotRequestedIds.Distinct().Order().ToArray();
        Assert.Equal(114, tickSensors.Length);
        var conflictSensorIds = new[] { 19L, 20L }.SelectMany(device => Enumerable.Range(1, 9)
            .Select(index => 30_000 + device * 10 + index)).ToHashSet();
        Assert.Empty(tickSensors.Where(conflictSensorIds.Contains));

        // The actual resource guard consumer reads only its six explicit guard sensors; it
        // never sends those readings to formal business evaluation or notification stores.
        using var guardClient = CreateClient(settings.Get());
        var guardRead = await PrtgResourceGuardProbe.FetchSensorValuesAsync(guardClient, guardSensors);
        Assert.True(guardRead.IsSuccess);
        Assert.Equal(6, guardRead.Values.Count);
        Assert.Empty(backend.IssueCaseStore().GetOpenKeys());
        Assert.Empty(new MailNotifyStateStore(backend.Blob("mail_notify_state")).Get().UrgentOutbox);
    }

    private List<WebHost> CreateHostRows()
    {
        var rows = new List<WebHost>();
        for (var index = 1; index <= 18; index++)
            rows.Add(new WebHost { HostId = index, HostName = $"synthetic-business-{index}", IpAddress = $"10.53.0.{index}", Source = "netiq", Active = true });
        rows.AddRange(new[]
        {
            new WebHost { HostId = 19, HostName = "synthetic-conflict-a1", IpAddress = "10.53.0.19", Source = "netiq", Active = true },
            new WebHost { HostId = 20, HostName = "synthetic-conflict-a2", IpAddress = "10.53.0.19", Source = "netiq", Active = true },
            new WebHost { HostId = 21, HostName = "synthetic-conflict-b1", IpAddress = "10.53.0.20", Source = "netiq", Active = true },
            new WebHost { HostId = 22, HostName = "synthetic-conflict-b2", IpAddress = "10.53.0.20", Source = "netiq", Active = true }
        });
        return rows;
    }

    private PrtgMonitoringController CreateMonitoringController() => new(backend, hosts,
        new AllVisible(hosts), FakeCurrentUser.WithCapabilities(Capability.Maintain), audit,
        new DataVersionStamp(), protection);

    private SettingsController CreateSettingsController(SystemSettingsService service, PrtgStructureSyncService? sync = null)
    {
        var controller = new SettingsController(service, new AiUsageStore(backend.Blob("ai_usage")), audit,
            prtgStructureSync: sync, backend: backend, hosts: hosts);
        controller.SnapshotCapacityClientFactory = value => CreateClient(value);
        return controller;
    }

    private PrtgSnapshotHostedService CreateSnapshotService(PrtgStructureSyncService sync,
        SchedulerRunState scheduler, PrtgBackfillRunState backfillState, PrtgProbeRunState probeState,
        FakeHostApplicationLifetime lifetime, SentinelStore sentinelStore, PrtgCapacityAdmissionPlan plan)
    {
        var service = new PrtgSnapshotHostedService(settings, backend, scheduler, sync,
            new PrtgBackfillService(settings, backend, backfillState, probeState, hosts, scheduler,
                new PrtgStructureSyncRunState(), new FakeSentinelStore(), sync),
            hosts, sentinelStore, probeState, lifetime, TimeSpan.FromHours(1));
        service.ClientFactory = () =>
        {
            var budget = new PrtgRequestBudget(new AdvancingTestClock());
            budget.SetAdmissionPlan(plan);
            return PrtgClientFactory.Create(settings.Get(), source, budget);
        };
        return service;
    }

    private SystemSettingsService CreateSystemSettingsService()
    {
        var users = new FakeUserStore();
        var freshness = new ScheduleFreshnessService(
            new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs")),
            new ScheduleOptionsStore(backend.Blob("schedule_options")));
        var mail = new MailNotificationService(settings, new FakeSmtpMailSender(), new FakeHostStore(), users,
            new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
            new MailNotifyStateStore(backend.Blob("mail_notify_state")), freshness);
        return new SystemSettingsService(settings, FakeCurrentUser.WithCapabilities(Capability.Maintain), audit,
            users, mail, new FakeReportUsageQuery());
    }

    private PrtgClient CreateClient(SystemSettings value, bool advanceRateLimitWithoutWaiting = false) =>
        PrtgClientFactory.Create(value, source, advanceRateLimitWithoutWaiting
            ? new PrtgRequestBudget(new AdvancingTestClock()) : new PrtgRequestBudget());

    private sealed class AdvancingTestClock : IPrtgClock
    {
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        private long elapsedTicks;
        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref elapsedTicks));
        public DateTimeOffset UtcNow => started + Elapsed;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delay > TimeSpan.Zero) Interlocked.Add(ref elapsedTicks, delay.Ticks);
            return Task.CompletedTask;
        }
    }

    private static JsonElement Data(object? value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.GetProperty("Data").Clone();
    }

    private static long QueryLong(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(Uri.UnescapeDataString(part[(name.Length + 1)..]), out var value)) return value;
        return 0;
    }

    private sealed class AllVisible(IHostStore hosts) : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => hosts.GetAll().Select(host => host.HostId).ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIds(PrtgHostSnapshot snapshot) => snapshot.Hosts.Select(host => host.HostId).ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long id) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => null;
        public List<WebHost> GetVisibleHosts() => hosts.GetAll();
        public void EnsureVisible(long hostId) { }
    }

    private sealed class TestConsole : IRunConsole { public void WriteLine(string message = "") { } }

    private sealed class ControlledPrtgHandler : HttpMessageHandler
    {
        private readonly long[] deviceIds = Enumerable.Range(1, 20).Select(id => (long)id)
            .Concat([21, 22, 23]).ToArray();
        private readonly List<string> requested = [];
        private readonly List<long> snapshotIds = [];
        public IReadOnlyList<string> Requested { get { lock (requested) return requested.ToArray(); } }
        public IReadOnlyList<long> SnapshotRequestedIds { get { lock (snapshotIds) return snapshotIds.ToArray(); } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.ToString();
            lock (requested) requested.Add(url);
            var query = request.RequestUri.Query;
            string body;
            if (query.Contains("content=devices", StringComparison.OrdinalIgnoreCase))
            {
                var devices = deviceIds.Select(id => new
                {
                    objid = id,
                    device = id <= 18 ? $"Business-{id}" : id <= 20 ? $"Conflict-{id}" : $"Guard-{id}",
                    host = id <= 20 ? $"10.53.0.{id}" : $"10.53.0.{id + 180}",
                    group = "synthetic-fixture", status = "Up", paused = false
                });
                body = JsonSerializer.Serialize(new { treesize = deviceIds.Length, devices });
            }
            else if (query.Contains("content=messages", StringComparison.OrdinalIgnoreCase))
                body = "{\"treesize\":0,\"messages\":[]}";
            else if (query.Contains("content=channels", StringComparison.OrdinalIgnoreCase))
            {
                var id = QuerySingleLong(query, "id");
                body = JsonSerializer.Serialize(new { treesize = 1, channels = new[] { new
                    { objid = id + 500_000, name = "Synthetic Capacity", lastvalue_raw = 50.0, unit = "%" } } });
            }
            else if (url.Contains("getobjectproperty.htm", StringComparison.OrdinalIgnoreCase))
            {
                var id = QuerySingleLong(query, "id");
                body = $"<prtg><result>{id + 500_000}</result></prtg>";
            }
            else if (query.Contains("content=sensors", StringComparison.OrdinalIgnoreCase))
            {
                if (query.Contains("columns=objid,device,sensor,status,lastvalue,lastvalue_raw", StringComparison.OrdinalIgnoreCase))
                {
                    var ids = RequestedSensorIds(query);
                    lock (snapshotIds) snapshotIds.AddRange(ids);
                    body = JsonSerializer.Serialize(new { treesize = ids.Length, sensors = ids.Select(GuardSensor).ToArray() });
                }
                else if (query.Contains("&id=", StringComparison.OrdinalIgnoreCase) &&
                    QuerySingleLong(query, "id") is >= 1 and <= 23 && query.Contains("count=5000", StringComparison.OrdinalIgnoreCase))
                {
                    var deviceId = QuerySingleLong(query, "id");
                    var sensors = DeviceSensors(deviceId);
                    body = JsonSerializer.Serialize(new { treesize = sensors.Length, sensors });
                }
                else
                {
                    var ids = RequestedSensorIds(query);
                    if (query.Contains("filter_objid=", StringComparison.OrdinalIgnoreCase))
                        lock (snapshotIds) snapshotIds.AddRange(ids);
                    body = JsonSerializer.Serialize(new { treesize = ids.Length, sensors = ids.Select(Sensor).ToArray() });
                }
            }
            else body = "{}";

            var contentType = url.Contains("getobjectproperty.htm", StringComparison.OrdinalIgnoreCase)
                ? "application/xml" : "application/json";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, contentType) });
        }

        private static object GuardSensor(long id) => new
        {
            objid = id, device = "Synthetic Guard", sensor = $"Guard sensor {id}", status = "Up",
            lastvalue = "50 %", lastvalue_raw = 50.0
        };

        private static object Sensor(long id)
        {
            var device = id switch
            {
                >= 40_001 and <= 40_006 => (id - 40_001) / 2 + 21,
                >= 30_000 and < 40_000 => id / 10 - 3_000,
                _ => 990_000
            };
            var type = id is >= 40_001 and <= 40_006
                ? (id % 2 == 1 ? "SNMP CPU Load" : "SNMP Memory") : "Ping";
            return new { objid = id, parentid = device, type, sensor = $"Synthetic {type} {id}",
                status = "Up", paused = false, lastvalue = "50 %", lastvalue_raw = 50.0,
                lastcheck = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), interval = 300, cumsince = "2025-01-01T00:00:00Z",
                primarychannel = id + 500_000 };
        }

        private static object[] DeviceSensors(long deviceId)
        {
            if (deviceId is >= 1 and <= 18)
                return Enumerable.Range(1, 6).Select(index => Sensor(30_000 + deviceId * 10 + index)).ToArray();
            if (deviceId is 19 or 20)
                return Enumerable.Range(1, 9).Select(index => Sensor(30_000 + deviceId * 10 + index)).ToArray();
            if (deviceId is >= 21 and <= 23)
            {
                var first = 40_001 + (deviceId - 21) * 2;
                return [Sensor(first), Sensor(first + 1)];
            }
            return [];
        }

        private static long[] RequestedSensorIds(string query)
        {
            var id = QuerySingleLong(query, "id");
            if (id > 0) return [id];
            var result = new List<long>();
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                if (part.StartsWith("filter_objid=", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(Uri.UnescapeDataString(part["filter_objid=".Length..]), out var parsed)) result.Add(parsed);
            if (result.Count > 0) return result.Distinct().Order().ToArray();
            return Array.Empty<long>();
        }

        private static long QuerySingleLong(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                if (part.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(Uri.UnescapeDataString(part[(key.Length + 1)..]), out var parsed)) return parsed;
            return 0;
        }
    }
}
