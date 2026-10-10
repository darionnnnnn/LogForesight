using System.Net;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 數值快照背景服務單元測試（docs/PRTG-SPEC.md §3b）。
/// </summary>
[CollectionDefinition("PrtgSnapshotSharedBudget", DisableParallelization = true)]
public sealed class PrtgSnapshotSharedBudgetCollection { }

[Collection("PrtgSnapshotSharedBudget")]
public class PrtgSnapshotHostedServiceTests : IDisposable
{
    private const string FixtureNowMarker = "__FIXTURE_NOW__";
    private readonly string _dir;
    private readonly SnapshotSqlDiagnosticRecorder _sqlDiagnostics = new();
    private readonly ITestOutputHelper _output;
    private readonly StorageBackend _backend;
    private readonly List<PrtgSnapshotHostedService> _services = [];
    private readonly List<PrtgRequestBudget> _fixtureBudgets = [];
    private readonly SystemSettingsStore _settingsStore;
    private readonly SchedulerRunState _schedulerRunState;
    private readonly PrtgStructureSyncRunState _syncState;
    private readonly PrtgStructureSyncService _structureSync;
    private readonly PrtgBackfillRunState _backfillState;
    private readonly PrtgProbeRunState _probeState;
    private readonly PrtgBackfillService _backfill;
    private readonly FakeHostApplicationLifetime _lifetime;
    private readonly StubHandler _stubHandler;
    private readonly HostStore _hostStore;

    public PrtgSnapshotHostedServiceTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir, _sqlDiagnostics);
        _settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        _schedulerRunState = new SchedulerRunState();
        _syncState = new PrtgStructureSyncRunState();
        _lifetime = new FakeHostApplicationLifetime();

        var hostStore = new HostStore(_backend.Blob("hosts"));
        _hostStore = hostStore;
        var statusStore = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        _backfillState = new PrtgBackfillRunState();
        _structureSync = new PrtgStructureSyncService(_settingsStore, _backend, _syncState, _schedulerRunState, hostStore, statusStore, _backfillState, new FakeSentinelStore(), new DataVersionStamp(), _lifetime);

        _probeState = new PrtgProbeRunState();
        _backfill = new PrtgBackfillService(_settingsStore, _backend, _backfillState, _probeState, hostStore, _schedulerRunState, _syncState, new FakeSentinelStore(), _structureSync);

        _stubHandler = new StubHandler();

        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token123");
            s.PrtgTimeoutSeconds = 30;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        _stubHandler.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private PrtgSnapshotHostedService CreateService(TestConsole? console = null, TimeSpan? pollInterval = null)
    {
        var service = new PrtgSnapshotHostedService(
            _settingsStore,
            _backend,
            _schedulerRunState,
            _structureSync,
            _backfill,
            _hostStore,
            new FakeSentinelStore(),
            _probeState,
            _lifetime, pollInterval ?? TimeSpan.FromSeconds(60));

        service.ClientFactory = () => new PrtgClient("https://prtg.example.com", "token123", 30, true, _stubHandler, PrtgAuthModes.Token, "", "", "", CreateFixtureBudget());
        if (console != null)
        {
            service.Console = console;
        }
        _services.Add(service);
        return service;
    }

    private PrtgRequestBudget CreateFixtureBudget()
    {
        // Production publishes the verified joint plan to Shared. These isolated clients use
        // the same persisted plan, so switching off Shared's Historic fallback does not remove
        // the snapshot lane authorization from a request-count or cancellation fixture.
        var budget = new PrtgRequestBudget(new SnapshotFixtureBudgetClock());
        var plan = new PrtgCapacityAdmissionPlanStore(
            _backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey)).ReadCurrent(DateTimeOffset.UtcNow);
        if (plan is not null) budget.SetAdmissionPlan(plan);
        _fixtureBudgets.Add(budget);
        return budget;
    }

    private sealed class SnapshotFixtureBudgetClock : IPrtgClock
    {
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        private long elapsedTicks;
        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref elapsedTicks));
        public DateTimeOffset UtcNow => start + Elapsed;
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (delay > TimeSpan.Zero) Interlocked.Add(ref elapsedTicks, delay.Ticks);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool RestoreJournal(PrtgSnapshotHostedService service, SystemSettings settings) =>
        (bool)typeof(PrtgSnapshotHostedService)
            .GetMethod("RestoreJournal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(service, new object[] { settings })!;

    [Fact]
    public async Task 背景timeline等待不阻擋下一輪快照且關閉會取消並等待timeline()
    {
        SetupTargetSensors(new[] { (601L, "SNMP Traffic 64bit") });
        var service = CreateService(pollInterval: TimeSpan.FromMilliseconds(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var clockReads = 0;
        service.Now = () => DateTime.Today.AddMinutes(60 + 15 * Interlocked.Increment(ref clockReads));
        service.TimelineTick = async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return new PrtgSensorTimelineTick(TimeSpan.FromSeconds(5), false, "fixture");
        };
        _stubHandler.OnSend = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) >= 2) secondSnapshot.TrySetResult();
            return Task.FromResult(JsonResponse("{\"sensors\":[{\"objid\":601,\"lastvalue_raw\":50,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}"));
        };
        await service.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await secondSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(cancelled.Task.IsCompleted);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await service.StopAsync(stop.Token);
        }
        Assert.True(cancelled.Task.IsCompleted);
    }

    private static bool SaveJournal(PrtgSnapshotHostedService service) =>
        (bool)typeof(PrtgSnapshotHostedService)
            .GetMethod("SaveJournal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(service, null)!;

    private static PrtgSnapshotJournal ServiceJournal(PrtgSnapshotHostedService service) =>
        (PrtgSnapshotJournal)typeof(PrtgSnapshotHostedService)
            .GetField("_journal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;

    private static PrtgSnapshotAccumulator ServiceAccumulator(PrtgSnapshotHostedService service) =>
        (PrtgSnapshotAccumulator)typeof(PrtgSnapshotHostedService)
            .GetField("_accumulator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;

    private static string JournalEndpoint(PrtgSnapshotHostedService service) =>
        (string)typeof(PrtgSnapshotHostedService)
            .GetField("_journalEndpoint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;

    private PrtgRecentStateChangeQueueItem NewRecentStateQueueItem(long deviceObjid, DateTime localToday)
    {
        var sourceHash = (string)typeof(PrtgSnapshotHostedService)
            .GetMethod("SourceIdentityHash", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { _settingsStore.Get() })!;
        var revision = new PrtgScopeRevisionReader(_backend, _hostStore).Read();
        var scopeHash = (string)typeof(PrtgSnapshotHostedService)
            .GetMethod("ScopeVersionHash", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { revision })!;
        var now = DateTime.UtcNow;
        return new PrtgRecentStateChangeQueueItem(deviceObjid, localToday.Date.AddDays(-1), localToday.Date,
            sourceHash, scopeHash, now, now, 0, null, null, null);
    }

    private void SeedCompletedMappedQueueRows(DateTime localToday)
    {
        var store = _backend.PrtgStore();
        var (_, mapRows) = store.GetLatestHostMapWithDate(30, localToday.Date);
        var mappedBusinessDevices = mapRows.Where(row => row.MapStatus is PrtgMapStatus.Ok or PrtgMapStatus.Conflict)
            .Select(row => row.DeviceObjid).ToHashSet();
        foreach (var sensor in store.GetAllSensors().Where(sensor => mappedBusinessDevices.Contains(sensor.DeviceObjid))
                     .GroupBy(sensor => sensor.DeviceObjid).Select(group => group.First()))
        {
            store.UpsertSensorsAndEnqueueRecentStateChanges(new[] { sensor }, DateTime.Now,
                NewRecentStateQueueItem(sensor.DeviceObjid, localToday) with { CompletedAtUtc = DateTime.UtcNow });
        }
    }

    private PrtgRecentStateChangeQueueItem QueueItemForDevice(long deviceObjid) =>
        Assert.Single(_backend.PrtgStore().ReadRecentStateChangeQueue()
            .Where(item => item.DeviceObjid == deviceObjid));

    private WebHost SeedManualBusinessDevice(long deviceObjid)
    {
        var host = _hostStore.Upsert(new WebHost { HostName = $"queue-host-{deviceObjid}", Active = true });
        _backend.PrtgStore().UpsertManualMap(new PrtgManualMapRow { DeviceObjid = deviceObjid, HostId = host.HostId });
        return host;
    }

    private void SeedCurrentMappedJointAdmission(long? witnessDeviceObjid = null)
    {
        var store = _backend.PrtgStore();
        var (_, mapRows) = store.GetLatestHostMapWithDate(30, DateTime.Today);
        var todayMaps = mapRows.Where(row => row.MapStatus == PrtgMapStatus.Ok && row.HostId > 0 &&
            _hostStore.Get(row.HostId!.Value) is { Active: true }).ToArray();
        if (todayMaps.Length == 0) return;

        foreach (var map in todayMaps)
        {
            var host = _hostStore.Get(map.HostId!.Value)!;
            if (string.IsNullOrWhiteSpace(host.IpAddress) && !string.IsNullOrWhiteSpace(map.Ip))
            {
                host.IpAddress = map.Ip;
                _hostStore.Upsert(host);
            }
        }

        var mappedDevices = todayMaps.Select(row => row.DeviceObjid).ToHashSet();
        var eligibleSensors = store.GetAllSensors().Where(sensor => mappedDevices.Contains(sensor.DeviceObjid) &&
            !sensor.Paused && !string.Equals(sensor.Status, "Paused", StringComparison.OrdinalIgnoreCase)).ToList();
        if (eligibleSensors.Count == 0)
        {
            var deviceObjid = witnessDeviceObjid ?? todayMaps.Min(row => row.DeviceObjid);
            var map = todayMaps.First(row => row.DeviceObjid == deviceObjid);
            var sensorObjid = 9_000_000L + Math.Abs(deviceObjid % 900_000L);
            var witness = new PrtgSensorRow
            {
                Objid = sensorObjid,
                DeviceObjid = deviceObjid,
                Name = $"Joint-admission fixture witness {deviceObjid}",
                SensorType = "ping",
                Status = "Up",
                Paused = false
            };
            store.UpsertSensors(new[] { witness }, DateTime.Now);
            eligibleSensors.Add(witness);
        }

        var settings = _settingsStore.Get();
        var sourceGeneration = "snapshot-hosted-fixture-source";
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.Revision = Guid.NewGuid().ToString("N");
            policy.CoreSystemId = "snapshot-hosted-fixture";
            policy.SourceGeneration = sourceGeneration;
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl);
            policy.ValidFrom = DateTimeOffset.UtcNow;
            policy.HostIds = todayMaps.Select(row => row.HostId!.Value).Distinct().Order().ToList();
            policy.SensorIds = eligibleSensors.Select(sensor => sensor.Objid).Distinct().Order().ToList();
            policy.SourceTimeZoneId = "UTC";
            policy.SourceCultureName = "en-US";
        });

        var mapByDevice = todayMaps.GroupBy(row => row.DeviceObjid).ToDictionary(group => group.Key, group => group.First());
        foreach (var sensor in eligibleSensors.OrderBy(sensor => sensor.Objid)
                     .Take(PrtgProfileTransportCapacityPilot.MaximumSensorIds))
        {
            var map = mapByDevice[sensor.DeviceObjid];
            store.BindObservedResource(sensor.Objid, map.HostId!.Value, sourceGeneration,
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(
                    sensor.DeviceObjid.ToString(CultureInfo.InvariantCulture), sensor.SensorType, "created", 0));
        }

        var policySnapshot = policyStore.Get();
        var selection = PrtgSnapshotTargetResolver.Resolve(_backend, _hostStore,
            settings, Array.Empty<Sentinel>(), policySnapshot);
        SeedSyntheticCapacityEvidence(9_000, selection);
    }


    [Fact]
    public async Task 快照未到整點強制重建服務_恢復累積樣本且停用後仍可寫回()
    {
        SetupTargetSensors(new[] { (501L, "Ping") });
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(
            "{\"sensors\":[{\"objid\":501,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}"));
        var hour = DateTime.Today.AddHours(10);
        var first = CreateService();
        first.Now = () => hour.AddMinutes(5);
        await first.TickAsync();
        Assert.Empty(_backend.PrtgStore().GetValues(hour, hour.AddHours(1)));

        // 不呼叫 ApplicationStopping，等同程序在本小時內中斷。
        first.Dispose(); // 模擬程序退出釋放 OS 擁有權；不做關閉時結算。
        _settingsStore.Update(s => { s.PrtgEnabled = false; s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive; });
        _stubHandler.OnSend = (_, _) => throw new InvalidOperationException("停用後不得開新請求");
        var restarted = CreateService();
        restarted.Now = () => hour.AddHours(1);
        await restarted.TickAsync();
        var value = Assert.Single(_backend.PrtgStore().GetValues(hour, hour.AddHours(1)));
        Assert.Equal(10, value.AvgValue);
        Assert.Equal(25, value.Coverage);
        Assert.Equal(0, restarted.GetStatus().PendingSamples);
    }

    [Fact]
    public async Task HostedServiceRestart_鏡像與Queue同交易提交後_下次ScopeRefresh仍取回近期狀態()
    {
        const long deviceId = 702;
        SeedManualBusinessDevice(deviceId);
        SetupOkDevices(new long[] { 10 });
        SeedCurrentMappedJointAdmission();
        var today = DateTime.Today;
        var store = _backend.PrtgStore();
        // Reconciliation creates a queue row for every mirrored business device. Seed
        // mapped-device rows as completed so only the intended pending event is drained.
        SeedCompletedMappedQueueRows(today);
        var sensor = new PrtgSensorRow { Objid = 703, DeviceObjid = deviceId, Name = "Ping", SensorType = "ping" };
        store.UpsertSensorsAndEnqueueRecentStateChanges(new[] { sensor }, DateTime.Now,
            NewRecentStateQueueItem(deviceId, today));

        // This models the process stopping after the atomic database commit but before any HTTP request.
        CreateService().Dispose();
        _stubHandler.OnSend = (request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.Query.Contains("content=messages", StringComparison.Ordinal)
                ? "{\"treesize\":0,\"messages\":[]}"
                : "{\"treesize\":0,\"sensors\":[]}"));
        var restarted = CreateService();
        restarted.Now = () => today.AddHours(12);
        await restarted.ScopeRefreshTickAsync();

        Assert.Contains(_stubHandler.RequestedUrls, url => url.Contains("content=messages", StringComparison.Ordinal) &&
            url.Contains($"id={deviceId}", StringComparison.Ordinal));
        var queueRows = _backend.PrtgStore().ReadRecentStateChangeQueue();
        Assert.Equal(2, queueRows.Count);
        var queued = Assert.Single(queueRows.Where(item => item.DeviceObjid == deviceId));
        Assert.NotNull(queued.CompletedAtUtc);
        Assert.Equal(today.AddDays(-1), queued.FromLocalDate);
        Assert.Equal(today, queued.ToLocalDate);
    }

    [Fact]
    public async Task Queue資格按businessDevice映射列_同host多device含conflict_排除guardOnly()
    {
        var hostA = _hostStore.Upsert(new WebHost { HostName = "queue-map-a", Active = true });
        var hostB = _hostStore.Upsert(new WebHost { HostName = "queue-map-b", Active = true });
        var today = DateTime.Today;
        var store = _backend.PrtgStore();
        store.ReplaceHostMapForDate(today, new[]
        {
            new PrtgHostMapRow { MapDate = today, DeviceObjid = 810, HostId = hostA.HostId, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { MapDate = today, DeviceObjid = 811, HostId = hostA.HostId, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { MapDate = today, DeviceObjid = 820, HostId = hostB.HostId, MapStatus = PrtgMapStatus.Ok },
            new PrtgHostMapRow { MapDate = today, DeviceObjid = 821, HostId = hostB.HostId, MapStatus = PrtgMapStatus.Conflict }
        });
        store.UpsertSensors(new[] { 810L, 811L, 820L, 821L, 899L }.Select(id => new PrtgSensorRow
        { Objid = id + 10_000, DeviceObjid = id, Name = "Ping", SensorType = "ping" }).ToArray(), DateTime.Now);
        // Deliberately do not seed admission here: local business-scope reconciliation
        // must still persist these four queue rows while HTTP stays blocked.
        _settingsStore.Update(settings =>
        {
            settings.PrtgResourceGuardEnabled = true;
            settings.PrtgResourceGuardSensorObjids = new List<string> { "10899" };
        });

        await CreateService().ScopeRefreshTickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Equal(new long[] { 810, 811, 820, 821 },
            store.ReadRecentStateChangeQueue().Select(item => item.DeviceObjid).Order().ToArray());
    }

    [Fact]
    public async Task Queue逐裝置隔離失敗_第一台失敗時第二台仍完成()
    {
        const long firstDevice = 720;
        const long secondDevice = 721;
        SeedManualBusinessDevice(firstDevice);
        SeedManualBusinessDevice(secondDevice);
        SetupOkDevices(new long[] { 10 });
        SeedCurrentMappedJointAdmission();
        SeedCompletedMappedQueueRows(DateTime.Today);
        var today = DateTime.Today;
        var items = new[] { NewRecentStateQueueItem(firstDevice, today), NewRecentStateQueueItem(secondDevice, today) };
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 722, DeviceObjid = firstDevice, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, items[0]);
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 723, DeviceObjid = secondDevice, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, items[1]);
        _stubHandler.OnSend = (request, _) =>
        {
            var query = request.RequestUri!.Query;
            if (!query.Contains("content=messages", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("{\"treesize\":0,\"sensors\":[]}"));
            if (query.Contains($"id={firstDevice}", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("{}", HttpStatusCode.InternalServerError));
            return Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));
        };

        await CreateService().ScopeRefreshTickAsync();

        var rows = _backend.PrtgStore().ReadRecentStateChangeQueue().ToDictionary(x => x.DeviceObjid);
        Assert.Null(rows[firstDevice].CompletedAtUtc);
        Assert.Null(rows[firstDevice].LeaseOwner);
        Assert.NotNull(rows[secondDevice].CompletedAtUtc);
        Assert.Contains(_stubHandler.RequestedUrls, url => url.Contains($"id={secondDevice}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 範圍移除後Queue明確停止清理_重新進入時由既有鏡像重建()
    {
        const long deviceId = 730;
        var host = SeedManualBusinessDevice(deviceId);
        SetupOkDevices(new long[] { 10 });
        SeedCurrentMappedJointAdmission();
        SeedCompletedMappedQueueRows(DateTime.Today);
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 731, DeviceObjid = deviceId, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, NewRecentStateQueueItem(deviceId, DateTime.Today));
        host.Active = false;
        _hostStore.Upsert(host);
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));

        await CreateService().ScopeRefreshTickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.DoesNotContain(_backend.PrtgStore().ReadRecentStateChangeQueue(), item => item.DeviceObjid == deviceId);
        var stopped = Assert.IsType<PrtgRecentStateChangeQueueStopSummary>(
            _backend.PrtgStore().ReadRecentStateChangeQueueStopSummary());
        Assert.Equal("business-scope-removed", stopped.Reason);
        Assert.Equal(1, stopped.StoppedCount);

        host.Active = true;
        _hostStore.Upsert(host);
        SeedCurrentMappedJointAdmission();
        SeedCompletedMappedQueueRows(DateTime.Today);
        var service = CreateService();
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));
        await service.ScopeRefreshTickAsync(); // Rebuilds a new row from the still-present sensor mirror and defers HTTP.
        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Null(QueueItemForDevice(deviceId).CompletedAtUtc);
        await service.ScopeRefreshTickAsync();
        Assert.Contains(_stubHandler.RequestedUrls, url => url.Contains("content=messages", StringComparison.Ordinal));
        Assert.NotNull(QueueItemForDevice(deviceId).CompletedAtUtc);
    }

    [Fact]
    public async Task 來源切換前已有排隊項_該pass不發HTTP_重新識別後下一pass才重建()
    {
        const long deviceId = 750;
        SeedManualBusinessDevice(deviceId);
        SetupOkDevices(new long[] { 10 });
        SeedCurrentMappedJointAdmission();
        SeedCompletedMappedQueueRows(DateTime.Today);
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 751, DeviceObjid = deviceId, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, NewRecentStateQueueItem(deviceId, DateTime.Today));
        _settingsStore.Update(s => s.PrtgUrl = "https://new-source.example.com");
        SeedCurrentMappedJointAdmission(); // Explicitly re-identify the synthetic source for the next two passes.
        SeedCompletedMappedQueueRows(DateTime.Today);
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));
        var service = CreateService();
        service.ClientFactory = () => new PrtgClient(_settingsStore.Get().PrtgUrl!, "token123", 30, true,
            _stubHandler, PrtgAuthModes.Token, "", "", "", CreateFixtureBudget());

        await service.ScopeRefreshTickAsync();
        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Null(QueueItemForDevice(deviceId).CompletedAtUtc);

        await service.ScopeRefreshTickAsync();
        Assert.Contains(_stubHandler.RequestedUrls, url => url.Contains("content=messages", StringComparison.Ordinal));
        Assert.Contains(_stubHandler.RequestedUrls, url => url.StartsWith("https://new-source.example.com", StringComparison.Ordinal));
        Assert.NotNull(QueueItemForDevice(deviceId).CompletedAtUtc);
    }

    [Fact]
    public async Task 來源在狀態HTTP期間改變_回應後版本checkpoint取消且Queue保留()
    {
        const long deviceId = 740;
        SeedManualBusinessDevice(deviceId);
        SetupOkDevices(new long[] { 10 });
        SeedCurrentMappedJointAdmission();
        SeedCompletedMappedQueueRows(DateTime.Today);
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 741, DeviceObjid = deviceId, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, NewRecentStateQueueItem(deviceId, DateTime.Today));
        _stubHandler.OnSend = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("content=messages", StringComparison.Ordinal))
            {
                _settingsStore.Update(s => s.PrtgUrl = "https://changed.example.com");
                return Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));
            }
            return Task.FromResult(JsonResponse("{\"treesize\":0,\"sensors\":[]}"));
        };

        await CreateService().ScopeRefreshTickAsync();

        Assert.Contains(_stubHandler.RequestedUrls, url => url.Contains("content=messages", StringComparison.Ordinal));
        var row = QueueItemForDevice(deviceId);
        Assert.Null(row.CompletedAtUtc);
        Assert.Null(row.LeaseOwner);
    }

    [Fact]
    public async Task 業務範圍在狀態HTTP期間移除_回應後版本checkpoint取消且Queue保留()
    {
        const long deviceId = 760;
        var host = SeedManualBusinessDevice(deviceId);
        SetupOkDevices(new long[] { 10 });
        SeedCurrentMappedJointAdmission();
        SeedCompletedMappedQueueRows(DateTime.Today);
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 761, DeviceObjid = deviceId, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, NewRecentStateQueueItem(deviceId, DateTime.Today));
        _stubHandler.OnSend = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("content=messages", StringComparison.Ordinal))
            {
                host.Active = false;
                _hostStore.Upsert(host);
                return Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));
            }
            return Task.FromResult(JsonResponse("{\"treesize\":0,\"sensors\":[]}"));
        };

        await CreateService().ScopeRefreshTickAsync();

        Assert.Contains(_stubHandler.RequestedUrls, url => url.Contains("content=messages", StringComparison.Ordinal));
        var row = QueueItemForDevice(deviceId);
        Assert.Null(row.CompletedAtUtc);
        Assert.Null(row.LeaseOwner);
    }

    [Fact]
    public async Task 第一頁已寫入第二頁HTTP取消_狀態列保留且Queue不Ack並可重試()
    {
        const long deviceId = 770;
        SeedManualBusinessDevice(deviceId);
        SetupOkDevices(new long[] { 10 });
        SeedCurrentMappedJointAdmission();
        SeedCompletedMappedQueueRows(DateTime.Today);
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 771, DeviceObjid = deviceId, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, NewRecentStateQueueItem(deviceId, DateTime.Today));
        var secondPage = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondPage = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = string.Join(",", Enumerable.Range(0, 5000).Select(i =>
        {
            var changed = DateTime.Today.AddSeconds(i).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return $"{{\"objid\":771,\"datetime\":\"{changed}\",\"status\":\"Up\",\"message\":\"event-{i}\"}}";
        }));
        _stubHandler.OnSend = async (request, token) =>
        {
            var query = request.RequestUri!.Query;
            if (!query.Contains("content=messages", StringComparison.Ordinal))
                return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
            if (query.Contains("start=0", StringComparison.Ordinal))
                return JsonResponse($"{{\"treesize\":5001,\"messages\":[{events}]}}");
            secondPage.TrySetResult(true);
            await releaseSecondPage.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return JsonResponse("{\"treesize\":5001,\"messages\":[]}");
        };

        using var cancel = new CancellationTokenSource();
        var service = CreateService();
        var operation = service.ScopeRefreshTickAsync(cancel.Token);
        try
        {
            await secondPage.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(_backend.PrtgStore().GetStateChanges(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1)).Count > 0);
            cancel.Cancel();
            releaseSecondPage.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancel.Cancel();
            releaseSecondPage.TrySetResult(true);
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        }

        var row = QueueItemForDevice(deviceId);
        Assert.Null(row.CompletedAtUtc);
        Assert.Null(row.LeaseOwner);
        Assert.True(row.Attempts > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tick先做快照再回填_依是否屬於快照映射範圍保留Queue等待(bool includeDeviceInSnapshotMap)
    {
        const long deviceId = 710;
        const long sensorId = 711;
        const string resourceSince = "created";
        SetupOkDevices(new long[] { 10 });
        var mappedHost = SeedManualBusinessDevice(deviceId);
        // A manual map alone admits device 710 to backfill/queue business scope. Snapshot
        // targets also require the active daily host map, so this toggles a real expansion.
        if (includeDeviceInSnapshotMap) SetupOkDevices(new long[] { 10, deviceId });
        SeedCurrentMappedJointAdmission();
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.Revision = Guid.NewGuid().ToString("N");
            policy.SensorIds = policy.SensorIds.Append(sensorId).Distinct().Order().ToList();
        });
        // Authorize the sensor that the bounded mirror backfill will discover while keeping
        // the old mirror selection admitted for this first pass.
        SeedSyntheticCapacityEvidence(9_000);
        _stubHandler.OnSend = (request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("content=messages", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));
            if (IsBackfillUrl(url))
                return Task.FromResult(JsonResponse(
                    $"{{\"treesize\":1,\"sensors\":[{{\"objid\":{sensorId},\"parentid\":{deviceId},\"sensor\":\"Ping\",\"type\":\"ping\",\"cumsince\":\"{resourceSince}\",\"status\":\"Up\",\"paused\":false}}]}}"));
            if (IsSnapshotUrl(url))
                return Task.FromResult(CapacityValidFilteredValues(url));
            return Task.FromResult(JsonResponse("{\"treesize\":0,\"sensors\":[]}"));
        };
        var service = CreateService();
        service.Now = () => DateTime.Today.AddHours(12);
        var resolveSelection = typeof(PrtgSnapshotHostedService).GetMethod("ResolveSnapshotCapacitySelection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        PrtgSnapshotTargetSelection CurrentSelection() => Assert.IsType<PrtgSnapshotTargetSelection>(
            resolveSelection.Invoke(service, new object[] { _settingsStore.Get(), service.Now() }));
        var selectionBeforeBackfill = CurrentSelection();
        Assert.DoesNotContain(sensorId, selectionBeforeBackfill.SensorObjids);
        await service.TickAsync();

        Assert.Contains(_backend.PrtgStore().GetAllSensors(), row => row.Objid == sensorId && row.DeviceObjid == deviceId);
        var selectionAfterBackfill = CurrentSelection();
        Assert.Equal(includeDeviceInSnapshotMap, selectionAfterBackfill.SensorObjids.Contains(sensorId));
        Assert.Equal(includeDeviceInSnapshotMap,
            selectionBeforeBackfill.ScopeFingerprint != selectionAfterBackfill.ScopeFingerprint);
        var backfilledSensor = _backend.PrtgStore().GetAllSensors().Single(row => row.Objid == sensorId);
        var currentPolicy = policyStore.Get();
        if (includeDeviceInSnapshotMap)
            _backend.PrtgStore().BindObservedResource(sensorId, mappedHost.HostId, currentPolicy.SourceGeneration,
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(
                    deviceId.ToString(CultureInfo.InvariantCulture), backfilledSensor.SensorType, resourceSince, 0));
        var requests = _stubHandler.RequestedUrls.ToArray();
        var firstSnapshotRequest = Array.FindIndex(requests, IsSnapshotUrl);
        var mirrorRequest = Array.FindIndex(requests, IsBackfillUrl);
        var stateRequest = Array.FindIndex(requests, url => url.Contains("content=messages", StringComparison.Ordinal));
        Assert.True(firstSnapshotRequest >= 0 && firstSnapshotRequest < mirrorRequest,
            $"snapshotIndex={firstSnapshotRequest}; mirrorBackfillIndex={mirrorRequest}; requestCount={requests.Length}");
        Assert.Equal(-1, stateRequest);
        Assert.Null(QueueItemForDevice(deviceId).CompletedAtUtc);
        if (includeDeviceInSnapshotMap)
            Assert.Contains("容量判定後有效範圍已變更", service.GetStatus().LastSkipReason);
        else
        {
            Assert.DoesNotContain(sensorId, selectionAfterBackfill.SensorObjids);
            Assert.Contains("未送出 messages 請求", service.GetStatus().LastSkipReason);
            Assert.Contains("保留至下一輪", service.GetStatus().LastSkipReason);
        }

        // Publish evidence for the exact post-backfill selection. The durable queue item
        // was enqueued after mirror commit, so the next admitted pass can resume it.
        SeedSyntheticCapacityEvidence(9_000, selectionAfterBackfill);
        Assert.True(PrtgCapacityRuntimeAdmission.TryGetCurrent(_backend, _hostStore,
            _settingsStore.Get(), selectionAfterBackfill, out _, out var admissionReason), admissionReason);
        service.Now = () => DateTime.Today.AddHours(12).AddMinutes(15);
        await service.TickAsync();

        Assert.Equal(includeDeviceInSnapshotMap,
            SnapshotUrls().Any(url => FilterObjids(url).Contains(sensorId)));

        requests = _stubHandler.RequestedUrls.ToArray();
        stateRequest = Array.FindIndex(requests, url => url.Contains("content=messages", StringComparison.Ordinal));
        var currentSettings = _settingsStore.Get();
        var currentSelection = CurrentSelection();
        var currentAdmission = PrtgCapacityRuntimeAdmission.TryGetCurrent(_backend, _hostStore,
            currentSettings, currentSelection, out var currentPlan, out var currentAdmissionReason);
        var queue = QueueItemForDevice(deviceId);
        var safeSkipReason = SafeBoundedDiagnostic(service.GetStatus().LastSkipReason);
        var gateDiagnostic = $"LastSkipReason={safeSkipReason}; currentAdmission={currentAdmission}:{currentAdmissionReason}; " +
            $"currentPlanPresent={currentPlan is not null}; snapshotCount={SnapshotUrls().Count}; " +
            $"snapshotIndex={Array.FindIndex(requests, IsSnapshotUrl)}; " +
            $"stateRequestIndex={stateRequest}; queueCompleted={queue.CompletedAtUtc.HasValue}; " +
            $"queueLeased={queue.LeaseOwner is not null}; requestCount={requests.Length}";
        Assert.True(currentAdmission, gateDiagnostic);
        Assert.True(stateRequest >= 0, gateDiagnostic);
        Assert.NotNull(queue.CompletedAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 實際Tick僅在有效採樣區段中止背景歷史且結束後釋放優先權(bool enabled)
    {
        SeedManualBusinessDevice(720);
        SetupOkDevices(new long[] { 10 });
        _settingsStore.Update(s => s.PrtgEnabled = enabled);
        SeedCurrentMappedJointAdmission();
        var activity = new PrtgSamplingActivity();
        Assert.True(activity.TryBeginBackground(out var background));
        using (background)
        {
            var observedRequests = 0;
            _stubHandler.OnSend = (request, cancellationToken) =>
            {
                observedRequests++;
                Assert.True(background.WasPreempted);
                Assert.False(activity.TryBeginBackground(out _));
                return Task.FromResult(JsonResponse(
                    request.RequestUri!.Query.Contains("content=messages", StringComparison.Ordinal)
                        ? "{\"treesize\":0,\"messages\":[]}"
                        : "{\"treesize\":1,\"sensors\":[{\"objid\":721,\"parentid\":720,\"sensor\":\"Ping\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false,\"lastvalue_raw\":1,\"interval\":\"60 s\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}"));
            };
            var service = CreateService();
            service.SamplingActivity = activity;
            service.Now = () => DateTime.Today.AddHours(12);
            await service.TickAsync();
            Assert.Equal(enabled, background.WasPreempted);
            Assert.Equal(enabled, observedRequests > 0);
            Assert.True(activity.TryBeginBackground(out var next));
            next.Dispose();
        }
    }

    [Fact]
    public async Task 提交後未清復原檔即中斷_重播沿用批次識別不重複加涵蓋率()
    {
        var hour = DateTime.Today.AddHours(10);
        var batch = new PrtgSnapshotJournal.Batch(Guid.NewGuid().ToString("N"), new[]
        {
            new PrtgValueRow { SensorObjid = 501, PeriodStart = hour, AvgValue = 10, MinValue = 10,
                MaxValue = 10, Coverage = 25, Quality = PrtgDataQuality.Sampled, CreatedAt = hour }
        });
        var journal = new PrtgSnapshotJournal(_backend);
        journal.Save(PrtgSnapshotJournal.Endpoint(_settingsStore.Get().PrtgUrl),
            Array.Empty<PrtgSnapshotAccumulator.CheckpointRow>(), new[] { batch }, hour);
        _backend.PrtgStore().MergeSampledValues(batch.Rows, batch.Id);
        _settingsStore.Update(s => s.PrtgEnabled = false);
        var restarted = CreateService();
        restarted.Now = () => hour.AddHours(1);
        await restarted.TickAsync();
        await restarted.TickAsync();
        Assert.Equal(25, Assert.Single(_backend.PrtgStore().GetValues(hour, hour.AddHours(1))).Coverage);
        Assert.Empty(journal.Load(PrtgSnapshotJournal.Endpoint(_settingsStore.Get().PrtgUrl), hour)!.Pending);
    }

    [Fact]
    public async Task 復原檔損壞_不覆寫不開取數請求且顯示停止原因()
    {
        var journal = new PrtgSnapshotJournal(_backend);
        Directory.CreateDirectory(Path.GetDirectoryName(journal.FilePath)!);
        File.WriteAllText(journal.FilePath, "broken");
        _stubHandler.OnSend = (_, _) => throw new InvalidOperationException("復原失敗不得開新請求");
        var service = CreateService();
        await service.TickAsync();
        Assert.Contains("復原檔", service.GetStatus().LastSkipReason);
        Assert.Contains("先備份並檢查", service.GetStatus().LastSkipReason);
        Assert.DoesNotContain("BytePositionInLine", service.GetStatus().LastSkipReason);
        Assert.Equal("broken", File.ReadAllText(journal.FilePath));
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
    }

    [Fact]
    public async Task 復原檔無法原子寫入_停止後續取數且不誤報PRTG失敗()
    {
        SetupTargetSensors(new[] { (501L, "Ping") });
        var journal = new PrtgSnapshotJournal(_backend);
        var hour = DateTime.Today.AddHours(10);
        var service = CreateService();
        service.Now = () => hour;
        _stubHandler.OnSend = (_, _) =>
        {
            // 本輪初始 checkpoint 已寫入；模擬回應期間暫存路徑失去寫入能力。
            Directory.CreateDirectory(journal.ManifestFilePath + ".tmp");
            return Task.FromResult(JsonResponse("{\"sensors\":[{\"objid\":501,\"lastvalue_raw\":10,\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}"));
        };
        await service.TickAsync();
        Assert.Null(service.GetStatus().LastSuccessAt);
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
        Assert.Contains("停止採集", service.GetStatus().LastSkipReason);
        service.Now = () => hour.AddMinutes(20);
        await service.TickAsync();
        Assert.Single(_stubHandler.RequestedUrls);
        Assert.Equal(1, service.GetStatus().PendingSamples);
    }

    [Fact]
    public async Task 有舊來源待寫樣本時改網址_不重播不取數且保留原檔()
    {
        SetupTargetSensors(new[] { (501L, "Ping") });
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{\"sensors\":[{\"objid\":501,\"lastvalue_raw\":10,\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}"));
        var hour = DateTime.Today.AddHours(10);
        var service = CreateService();
        service.Now = () => hour;
        await service.TickAsync();
        var journal = new PrtgSnapshotJournal(_backend);
        var journalRoot = Path.GetDirectoryName(journal.FilePath)!;
        var original = Directory.GetFiles(journalRoot, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(journalRoot, path), path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        _settingsStore.Update(s => s.PrtgUrl = "https://new-source.example.com");
        service.Now = () => hour.AddHours(1);
        await service.TickAsync();
        Assert.Single(_stubHandler.RequestedUrls);
        Assert.Contains("來源位址或來源世代變更", service.GetStatus().LastSkipReason);
        Assert.Empty(_backend.PrtgStore().GetValues(hour, hour.AddHours(1)));
        var after = Directory.GetFiles(journalRoot, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(journalRoot, path), path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(original, after);
    }

    [Fact]
    public async Task 首次恢復後sourceGeneration在operationScope前變更_保留journal並停止任何HTTP()
    {
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value => value.SourceGeneration = "source-before-operation-a");
        var settings = _settingsStore.Get();
        var bindingA = PrtgSnapshotJournal.Binding(_backend, settings.PrtgUrl);
        var clock = DateTime.Now;
        var now = new DateTime(clock.Year, clock.Month, clock.Day, clock.Hour, 0, 0, clock.Kind);
        using (var seed = new PrtgSnapshotJournal(_backend))
        {
            seed.Save(bindingA,
                [new PrtgSnapshotAccumulator.CheckpointRow(now, 501, 10, 1, 10, 10)], [], now);
            seed.Load(bindingA, now);
            seed.EnableIncremental(bindingA, now);
        }

        var journal = new PrtgSnapshotJournal(_backend);
        var journalRoot = Path.GetDirectoryName(journal.FilePath)!;
        var before = Directory.GetFiles(journalRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(journalRoot, path),
                path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        var service = CreateService();
        service.Now = () => now;
        service.BeforeOperationScopeCapture = () =>
            policy.Update(value => value.SourceGeneration = "source-before-operation-b");

        await service.TickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Contains("保留", service.GetStatus().LastSkipReason);
        var after = Directory.GetFiles(journalRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(journalRoot, path),
                path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(before, after);
        using var recovered = new PrtgSnapshotJournal(_backend);
        Assert.Equal(10, Assert.Single(recovered.Load(bindingA, now)!.Accumulator).Sum);
    }

    [Fact]
    public void HostedRestore_全域scope與resource修訂變動仍保留兩顆sensor既有trustedcheckpoint()
    {
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey))
            .Update(value => value.SourceGeneration = "source-r03-local-scope");
        var settings = _settingsStore.Get();
        var binding = PrtgSnapshotJournal.Binding(_backend, settings.PrtgUrl);
        var received = DateTime.UtcNow;
        var effective = new DateTime(received.Year, received.Month, received.Day, received.Hour, 0, 0,
            DateTimeKind.Utc);
        var accumulator = new PrtgSnapshotAccumulator();
        foreach (var sensor in new[] { (Id: 501L, Resource: "resource-a", Epoch: "epoch-a"),
                     (Id: 502L, Resource: "resource-b", Epoch: "epoch-b") })
        {
            var sample = new PrtgTrustedSample(sensor.Id, sensor.Id, "source-r03-local-scope",
                sensor.Resource, $"channel-{sensor.Id}", sensor.Epoch, "semantic-v1", "strategy-v1", 15,
                effective, received.AddSeconds(-5), received, TimeSpan.FromMinutes(1),
                PrtgTrustedSampleQuality.Good, $"physical-{sensor.Id}", "UTC", "UTC");
            Assert.Equal(PrtgTrustedSampleDisposition.Accepted, accumulator.AddTrusted(sample, received));
        }
        using (var seed = new PrtgSnapshotJournal(_backend))
            seed.Save(binding, accumulator.Capture(), [], DateTime.Now);
        var before = new PrtgSnapshotJournal(_backend).Load(binding, DateTime.Now)!.Accumulator
            .OrderBy(row => row.SensorObjid).ToArray();
        Assert.Equal(2, before.Length);

        // Simulate an unrelated mapping/resource change after these rows were accepted.
        _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).Mutate(_ => ("1", 0));
        _backend.Blob("prtg_resource_generation_revision").Mutate(_ => ("1", 0));
        Assert.Equal(binding, PrtgSnapshotJournal.Binding(_backend, settings.PrtgUrl));

        var restarted = CreateService();
        Assert.True(RestoreJournal(restarted, _settingsStore.Get()));
        var restored = ServiceAccumulator(restarted).Capture().OrderBy(row => row.SensorObjid).ToArray();
        Assert.Equal(before.Length, restored.Length);
        Assert.Equal(before.Select(row => row.SensorObjid), restored.Select(row => row.SensorObjid));
        Assert.Equal(before.Select(row => row.Trusted!.ContextHash), restored.Select(row => row.Trusted!.ContextHash));
        Assert.Equal(before.Select(row => row.Trusted!.ResourceEpoch), restored.Select(row => row.Trusted!.ResourceEpoch));
    }

    [Fact]
    public void 空journal來源切換耐久發布新generation後可保存並重載新樣本()
    {
        var now = DateTime.Today.AddHours(10);
        var service = CreateService();
        service.Now = () => now;
        var settingsA = _settingsStore.Get();
        var endpointA = PrtgSnapshotJournal.Binding(_backend, settingsA.PrtgUrl);
        Assert.True(RestoreJournal(service, settingsA));
        Assert.Equal(endpointA, JournalEndpoint(service));

        _settingsStore.Update(settings => settings.PrtgUrl = "https://prtg-b.example.com");
        var settingsB = _settingsStore.Get();
        var endpointB = PrtgSnapshotJournal.Binding(_backend, settingsB.PrtgUrl);
        Assert.NotEqual(endpointA, endpointB);
        Assert.True(RestoreJournal(service, settingsB));
        Assert.Equal(endpointB, JournalEndpoint(service));

        ServiceAccumulator(service).Add(501, now, 12, 25);
        Assert.True(SaveJournal(service));
        using var firstRead = new PrtgSnapshotJournal(_backend);
        var firstState = firstRead.Load(endpointB, now)!;
        var firstRow = Assert.Single(firstState.Accumulator);
        Assert.Equal(endpointB, firstState.SourceEndpoint);
        Assert.Equal(1, firstRow.Count);
        Assert.Equal(12, firstRow.Sum);

        using var retryRead = new PrtgSnapshotJournal(_backend);
        var retryState = retryRead.Load(endpointB, now)!;
        var retryRow = Assert.Single(retryState.Accumulator);
        Assert.Equal(firstRow, retryRow);
        Assert.Empty(retryState.Pending);
    }

    [Fact]
    public void 有RAM樣本時來源切換拒絕且journal檔案與舊binding保持()
    {
        var now = DateTime.Today.AddHours(10);
        var service = CreateService();
        service.Now = () => now;
        var settingsA = _settingsStore.Get();
        var endpointA = PrtgSnapshotJournal.Binding(_backend, settingsA.PrtgUrl);
        Assert.True(RestoreJournal(service, settingsA));
        ServiceAccumulator(service).Add(501, now, 12, 25);
        Assert.True(SaveJournal(service));

        var journal = ServiceJournal(service);
        var journalRoot = Path.GetDirectoryName(journal.FilePath)!;
        var original = Directory.GetFiles(journalRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(journalRoot, path),
                path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        _settingsStore.Update(settings => settings.PrtgUrl = "https://prtg-b.example.com");
        var endpointB = PrtgSnapshotJournal.Binding(_backend, _settingsStore.Get().PrtgUrl);

        Assert.False(RestoreJournal(service, _settingsStore.Get()));

        Assert.Equal(endpointA, JournalEndpoint(service));
        var after = Directory.GetFiles(journalRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(journalRoot, path),
                path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(original, after);
        using var oldRead = new PrtgSnapshotJournal(_backend);
        Assert.Equal(1, Assert.Single(oldRead.Load(endpointA, now)!.Accumulator).Count);
        using var newRead = new PrtgSnapshotJournal(_backend);
        Assert.Throws<InvalidDataException>(() => newRead.Load(endpointB, now));
    }

    [Fact]
    public void generation發布失敗保留舊binding_故障清除後可安全重試()
    {
        var now = DateTime.Today.AddHours(10);
        var service = CreateService();
        service.Now = () => now;
        var settingsA = _settingsStore.Get();
        var endpointA = PrtgSnapshotJournal.Binding(_backend, settingsA.PrtgUrl);
        Assert.True(RestoreJournal(service, settingsA));
        var journal = ServiceJournal(service);
        var originalManifest = File.ReadAllBytes(journal.ManifestFilePath);
        journal.FaultPoint = point =>
        {
            if (point == "before-generation-switch") throw new IOException("simulated generation publication failure");
        };
        _settingsStore.Update(settings => settings.PrtgUrl = "https://prtg-b.example.com");
        var settingsB = _settingsStore.Get();
        var endpointB = PrtgSnapshotJournal.Binding(_backend, settingsB.PrtgUrl);

        Assert.False(RestoreJournal(service, settingsB));
        Assert.Equal(endpointA, JournalEndpoint(service));
        Assert.Equal(originalManifest, File.ReadAllBytes(journal.ManifestFilePath));

        journal.FaultPoint = null;
        Assert.True(RestoreJournal(service, settingsB));
        Assert.Equal(endpointB, JournalEndpoint(service));
        ServiceAccumulator(service).Add(501, now, 12, 25);
        Assert.True(SaveJournal(service));
        using var recovered = new PrtgSnapshotJournal(_backend);
        var state = recovered.Load(endpointB, now)!;
        Assert.Equal(1, Assert.Single(state.Accumulator).Count);
        Assert.Empty(state.Pending);
    }

    private void SetupTargetSensors(
        IEnumerable<(long Objid, string SensorType)> targets,
        IEnumerable<(long Objid, string SensorType)>? nonTargets = null)
    {
        var store = _backend.PrtgStore();
        var today = DateTime.Today;

        // Target resolution now requires a real active host behind each OK host-map row.
        // Seed the fixture's canonical HostId only when absent; tests that deliberately
        // deactivate or merge it retain that negative condition.
        if (_hostStore.Get(1) == null)
            _hostStore.Upsert(new WebHost { HostName = "Server-01", IpAddress = "192.168.1.10", Active = true });

        store.ReplaceHostMapForDate(today, new[]
        {
            new PrtgHostMapRow
            {
                DeviceObjid = 10,
                HostId = 1,
                HostName = "Server-01",
                Ip = "192.168.1.10",
                MapStatus = PrtgMapStatus.Ok,
                MapDate = today
            }
        });

        var targetSensors = targets.Select(t => new PrtgSensorRow
        {
            Objid = t.Objid,
            DeviceObjid = 10,
            Name = $"Sensor-{t.Objid}",
            SensorType = t.SensorType,
            Status = "Up",
            Paused = false
        }).ToList();
        var sensors = targetSensors.ToList();

        if (nonTargets != null)
        {
            sensors.AddRange(nonTargets.Select(t => new PrtgSensorRow
            {
                Objid = t.Objid,
                DeviceObjid = 99,
                Name = $"Sensor-{t.Objid}",
                SensorType = t.SensorType,
                Status = "Up",
                Paused = false
            }));
        }

        store.UpsertSensors(sensors, DateTime.Now);
        var settings = _settingsStore.Get();
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var sourceGeneration = "snapshot-hosted-fixture-source";
        // This is resolver/admission fixture state only. The rows are synthetic and do not
        // represent native PRTG authority or trusted sample history.
        policyStore.Update(policy =>
        {
            policy.Revision = Guid.NewGuid().ToString("N");
            policy.CoreSystemId = "snapshot-hosted-fixture";
            policy.SourceGeneration = sourceGeneration;
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl);
            policy.ValidFrom = DateTimeOffset.UtcNow;
            policy.HostIds = [1];
            policy.SensorIds = targetSensors.Select(sensor => sensor.Objid).Distinct().Order().ToList();
            policy.SourceTimeZoneId = "UTC";
            policy.SourceCultureName = "en-US";
        });

        foreach (var sensor in targetSensors.OrderBy(sensor => sensor.Objid)
                     .Take(PrtgProfileTransportCapacityPilot.MaximumSensorIds))
            store.BindObservedResource(sensor.Objid, 1, sourceGeneration,
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(
                    sensor.DeviceObjid.ToString(CultureInfo.InvariantCulture), sensor.SensorType, "created", 0));

        var policySnapshot = policyStore.Get();
        var selection = PrtgSnapshotTargetResolver.Resolve(_backend, _hostStore,
            settings, Array.Empty<Sentinel>(), policySnapshot);
        SeedSyntheticCapacityEvidence(9_000, selection);
    }

    private void SeedSyntheticCapacityEvidence(long elapsedMilliseconds,
        PrtgSnapshotTargetSelection? supplied = null, long? profileElapsedMilliseconds = null)
    {
        var settings = _settingsStore.Get();
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var selection = supplied ?? PrtgSnapshotTargetResolver.Resolve(_backend, _hostStore,
            settings, Array.Empty<Sentinel>(), policy);
        if (selection.CapacitySampleBatchSize == 0) return;
        var store = new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
        for (var index = 0; index < PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples; index++)
            store.Record(new PrtgSnapshotCapacitySample(selection.ScopeFingerprint,
                selection.EndpointFingerprint, selection.RequestShapeFingerprint,
                DateTimeOffset.UtcNow.AddMinutes(-5).AddMilliseconds(index), elapsedMilliseconds,
                selection.CapacitySampleBatchSize, "success"));

        SeedSyntheticJointAdmissionPlan(settings, policy, selection, profileElapsedMilliseconds);
    }

    private void SeedSyntheticJointAdmissionPlan(SystemSettings settings, PrtgMonitoringPolicy policy,
        PrtgSnapshotTargetSelection selection, long? profileElapsedMilliseconds = null)
    {
        if (!settings.PrtgEnabled || !policy.Ready(settings.PrtgUrl) || policy.Revision.Length == 0 ||
            selection.SensorObjids.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        var prtgStore = _backend.PrtgStore();
        var mirroredSensors = prtgStore.GetAllSensors().ToDictionary(sensor => sensor.Objid);
        var candidateIds = policy.SensorIds.Where(id => id > 0).Distinct().Order()
            .Where(mirroredSensors.ContainsKey).ToArray();
        var identities = prtgStore.GetResourceIdentities(candidateIds);
        var profileIds = candidateIds.Where(id => identities.TryGetValue(id, out var identity) &&
                mirroredSensors[id].DeviceObjid == identity.DeviceId && identity.Epoch > 0 && identity.Active &&
                !identity.PendingReconciliation && identity.SourceGeneration == policy.SourceGeneration &&
                !string.IsNullOrWhiteSpace(identity.ResourceFingerprint) && policy.HostIds.Contains(identity.HostId))
            .Take(PrtgProfileTransportCapacityPilot.MaximumSensorIds).ToArray();
        if (profileIds.Length == 0) return;
        var profileSensorCount = Math.Min(policy.SensorIds.Where(id => id > 0).Distinct().Count(),
            PrtgProfileTransportCapacityPilot.MaximumSensorIds);
        if (profileSensorCount == 0) return;
        var contract = PrtgProfileTransportCapacityPilot.BuildContract(
            _backend, _hostStore, settings, policy, profileIds);
        var profileStore = new PrtgProfileTransportCapacityStore(
            _backend.Blob(PrtgProfileTransportCapacityStore.BlobKey));
        // These are synthetic capacity samples for the bounded policy workload. BuildContract
        // still validates each representative against a real mirrored current identity.
        var profileElapsed = profileElapsedMilliseconds ?? profileSensorCount * 500L;
        for (var index = 0; index < PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples; index++)
            profileStore.Record(new PrtgProfileTransportSample(contract.SourceFingerprint,
                contract.ScopeFingerprint, contract.StrategyFingerprint, contract.RequestShapeFingerprint,
                now.AddMinutes(-5).AddMilliseconds(index), profileElapsed, profileSensorCount,
                PrtgProfileTransportCapacityEvaluator.ExpectedRequests(profileSensorCount),
                PrtgProfileTransportCapacityEvaluator.ExpectedRequests(profileSensorCount), "success", null, contract.VersionFingerprint));

        var snapshot = PrtgSnapshotCapacityEvaluator.Evaluate(selection.SensorObjids.Count,
            PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy), selection.ScopeFingerprint,
            selection.EndpointFingerprint, selection.RequestShapeFingerprint,
            new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Read(), now);
        var profile = PrtgProfileTransportCapacityEvaluator.Evaluate(policy.SensorIds.Distinct().Count(),
            contract.SourceFingerprint, contract.ScopeFingerprint, contract.StrategyFingerprint,
            contract.RequestShapeFingerprint, contract.VersionFingerprint, profileStore.Read(), now);
        var joint = PrtgJointCapacityEvaluator.Evaluate(snapshot, profile,
            PrtgRequestBudget.Shared.ReadUsage(), settings.PrtgTimeoutSeconds);
        if (joint.Status != PrtgSnapshotCapacityStatus.CapacityQualified) return;

        var candidate = PrtgJointCapacityEvaluator.CreatePlan(joint, contract.SourceFingerprint,
            selection.ScopeFingerprint, contract.ScopeFingerprint, contract.StrategyFingerprint,
            selection.RequestShapeFingerprint, contract.RequestShapeFingerprint,
            contract.VersionFingerprint, now, settings.Revision, policy.Revision);
        var store = new PrtgCapacityAdmissionPlanStore(_backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
        var published = store.Publish(candidate, "snapshot-hosted-synthetic-fixture", now, TimeSpan.FromHours(24),
            settings.Revision, () =>
            {
                var currentSettings = _settingsStore.Get();
                var currentPolicy = new PrtgMonitoringPolicyStore(
                    _backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                if (!currentSettings.PrtgEnabled || currentSettings.Revision != settings.Revision ||
                    currentPolicy.Revision != policy.Revision) return false;
                var currentContract = PrtgProfileTransportCapacityPilot.BuildContract(
                    _backend, _hostStore, currentSettings, currentPolicy, profileIds);
                var currentSelection = PrtgSnapshotTargetResolver.Resolve(_backend, _hostStore,
                    currentSettings, Array.Empty<Sentinel>(), currentPolicy);
                return currentContract == contract &&
                    currentSelection.ScopeFingerprint == selection.ScopeFingerprint &&
                    currentSelection.RequestShapeFingerprint == selection.RequestShapeFingerprint;
            });
        // Production renews Shared before using a cached client. Keep each isolated fixture
        // budget on the same published plan when this helper changes scope or strategy.
        foreach (var budget in _fixtureBudgets) budget.SetAdmissionPlan(published);
    }

    [Fact]
    public async Task Snapshot_refresh_excludes_ok_mapping_for_inactive_catalogue_host()
    {
        SetupTargetSensors(new[] { (501L, "Ping") });
        var inactive = _hostStore.Get(1)!;
        inactive.Active = false;
        _hostStore.Upsert(inactive);
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{\"sensors\":[]}"));

        await CreateService().TickAsync();

        Assert.Empty(SnapshotUrls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 快照回應時停用或改對應_停止後續批次且不誤記成功或連線失敗(bool changeMapping)
    {
        // Three snapshot transports may be in flight. Change scope before any response;
        // already admitted requests may finish, but the three queued batches must not start.
        SetupTargetSensors(Enumerable.Range(1, 351).Select(id => ((long)id, "ping")).ToArray());
        var threeAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        _stubHandler.OnSend = async (request, token) =>
        {
            if (Interlocked.Increment(ref started) == 3) threeAdmitted.TrySetResult();
            await releaseResponses.Task.WaitAsync(token);
            return FilteredValues(request.RequestUri!.ToString());
        };
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var service = CreateService();
        var tick = service.TickAsync(parent.Token);
        try
        {
            await threeAdmitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, started);
            if (changeMapping) _backend.PrtgStore().UpsertManualMap(new PrtgManualMapRow { DeviceObjid = 10, HostId = 2 });
            else _settingsStore.Update(s => s.PrtgEnabled = false);
            releaseResponses.TrySetResult();
            await tick.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseResponses.TrySetResult();
            if (!tick.IsCompleted) parent.Cancel();
            try { await tick.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (parent.IsCancellationRequested) { }
        }
        Assert.Equal(3, _stubHandler.RequestedUrls.Count);
        var admittedBatches = _stubHandler.RequestedUrls.Select(FilterObjids).ToArray();
        Assert.All(admittedBatches, batch => Assert.Equal(100, batch.Count));
        var admittedIds = admittedBatches.SelectMany(ids => ids).ToArray();
        Assert.Equal(300, admittedIds.Distinct().Count());
        Assert.All(admittedIds, id => Assert.InRange(id, 1L, 351L));
        Assert.Null(service.GetStatus().LastSuccessAt);
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
        Assert.Equal(0, service.Accumulator.SampleCount);
        Assert.False(parent.IsCancellationRequested);
        Assert.Contains("設定已變更", service.GetStatus().LastSkipReason);
    }

    [Fact]
    public void 範圍版本_一般回報不變更_停用主機及人工對應會變更()
    {
        var host = _hostStore.Upsert(new WebHost { HostName = "scope-host", Active = true });
        var reader = new PrtgScopeRevisionReader(_backend, _hostStore);
        var original = reader.Read();
        _hostStore.TouchNetiq(host.HostId, "更新顯示名稱", DateTime.Now);
        Assert.Equal(original, reader.Read());
        host.Active = false;
        _hostStore.Upsert(host);
        var inactive = reader.Read();
        Assert.NotEqual(original, inactive);
        _backend.PrtgStore().UpsertManualMap(new PrtgManualMapRow { DeviceObjid = 10, HostId = host.HostId });
        Assert.NotEqual(inactive, reader.Read());
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode code = HttpStatusCode.OK)
    {
        var responseNow = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var responseJson = json.Replace(FixtureNowMarker, responseNow, StringComparison.Ordinal);
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
    }

    private sealed class SnapshotSqlDiagnosticRecorder : DbCommandInterceptor
    {
        private long _sensorSelectCount;
        private long _identityBatchSelectCount;
        private long _sensorWriteCount;
        private long _blobWriteCount;
        private long _sensorSelectMs;
        private long _identityBatchSelectMs;
        private long _sensorWriteMs;
        private long _blobWriteMs;

        public long SensorSelectCount => Interlocked.Read(ref _sensorSelectCount);

        public void Reset()
        {
            Interlocked.Exchange(ref _sensorSelectCount, 0);
            Interlocked.Exchange(ref _identityBatchSelectCount, 0);
            Interlocked.Exchange(ref _sensorWriteCount, 0);
            Interlocked.Exchange(ref _blobWriteCount, 0);
            Interlocked.Exchange(ref _sensorSelectMs, 0);
            Interlocked.Exchange(ref _identityBatchSelectMs, 0);
            Interlocked.Exchange(ref _sensorWriteMs, 0);
            Interlocked.Exchange(ref _blobWriteMs, 0);
        }

        public string ReadAndReset()
        {
            var summary = string.Format(CultureInfo.InvariantCulture,
                "sensor_selects={0}/{1}ms; identity_batch_selects={2}/{3}ms; sensor_writes={4}/{5}ms; blob_writes={6}/{7}ms",
                Interlocked.Read(ref _sensorSelectCount), Interlocked.Read(ref _sensorSelectMs),
                Interlocked.Read(ref _identityBatchSelectCount), Interlocked.Read(ref _identityBatchSelectMs),
                Interlocked.Read(ref _sensorWriteCount), Interlocked.Read(ref _sensorWriteMs),
                Interlocked.Read(ref _blobWriteCount), Interlocked.Read(ref _blobWriteMs));
            Reset();
            return summary;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            CountStarted(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CountStarted(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result)
        {
            AddElapsed(command.CommandText, eventData.Duration);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            AddElapsed(command.CommandText, eventData.Duration);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result)
        {
            CountStarted(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            CountStarted(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            AddElapsed(command.CommandText, eventData.Duration);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            AddElapsed(command.CommandText, eventData.Duration);
            return ValueTask.FromResult(result);
        }

        private void CountStarted(string sql)
        {
            var kind = Classify(sql);
            switch (kind)
            {
                case CommandKind.SensorSelect: Interlocked.Increment(ref _sensorSelectCount); break;
                case CommandKind.IdentityBatchSelect: Interlocked.Increment(ref _identityBatchSelectCount); break;
                case CommandKind.SensorWrite: Interlocked.Increment(ref _sensorWriteCount); break;
                case CommandKind.BlobWrite: Interlocked.Increment(ref _blobWriteCount); break;
            }
        }

        private void AddElapsed(string sql, TimeSpan duration)
        {
            var milliseconds = (long)Math.Ceiling(duration.TotalMilliseconds);
            var kind = Classify(sql);
            switch (kind)
            {
                case CommandKind.SensorSelect: Interlocked.Add(ref _sensorSelectMs, milliseconds); break;
                case CommandKind.IdentityBatchSelect: Interlocked.Add(ref _identityBatchSelectMs, milliseconds); break;
                case CommandKind.SensorWrite: Interlocked.Add(ref _sensorWriteMs, milliseconds); break;
                case CommandKind.BlobWrite: Interlocked.Add(ref _blobWriteMs, milliseconds); break;
            }
        }

        private static CommandKind Classify(string sql)
        {
            var isSelect = sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
            if (isSelect && sql.Contains("lf_prtg_sensors", StringComparison.OrdinalIgnoreCase))
                return CommandKind.SensorSelect;
            if (isSelect && sql.Contains("lf_blobs", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains(" IN (", StringComparison.OrdinalIgnoreCase) &&
                (sql.Contains("substr(", StringComparison.OrdinalIgnoreCase) ||
                 sql.Contains("substring(", StringComparison.OrdinalIgnoreCase)))
                return CommandKind.IdentityBatchSelect;
            if (!isSelect && sql.Contains("lf_prtg_sensors", StringComparison.OrdinalIgnoreCase))
                return CommandKind.SensorWrite;
            if (!isSelect && sql.Contains("lf_blobs", StringComparison.OrdinalIgnoreCase))
                return CommandKind.BlobWrite;
            return CommandKind.Other;
        }

        private enum CommandKind { Other, SensorSelect, IdentityBatchSelect, SensorWrite, BlobWrite }
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

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _startedCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();

        public CancellationToken ApplicationStarted => _startedCts.Token;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped => _stoppedCts.Token;

        public void StopApplication() => _stoppingCts.Cancel();
    }

    [Fact]
    public async Task TickAsync_PrtgEnabled為false_不發送請求()
    {
        _settingsStore.Update(s => s.PrtgEnabled = false);
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{}"));

        var service = CreateService();
        await service.TickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
    }

    [Fact]
    public async Task 同一小時縮小白名單_下一輪不再查已移除感測器()
    {
        SetupTargetSensors(new[] { (101L, "Ping"), (102L, "SNMP Traffic 64bit") });
        _settingsStore.Update(s => s.PrtgSensorTypeWhitelist = new List<string> { "Ping", "SNMP Traffic 64bit" });
        SeedSyntheticCapacityEvidence(9_000);
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));
        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        await service.TickAsync();
        Assert.Contains(102L, FilterObjids(SnapshotUrls().Last()));

        _settingsStore.Update(s => s.PrtgSensorTypeWhitelist = new List<string> { "ping" });
        // A settings revision changes the immutable admission contract. Rebuild matching
        // synthetic evidence for the narrowed target scope before exercising the next tick.
        SeedSyntheticCapacityEvidence(9_000);
        clock = clock.AddMinutes(15);
        await service.TickAsync();

        Assert.Equal(new[] { 101L }, FilterObjids(SnapshotUrls().Last()));
    }

    [Fact]
    public async Task TickAsync_結構同步執行中_不發請求且解除後第一次仍發()
    {
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(tableJson));

        SetupTargetSensors(new[] { (101L, "Ping") });

        var service = CreateService();

        Assert.True(_syncState.TryBegin());
        Assert.True(_structureSync.IsRunning);

        // 第 1 次 tick：結構同步執行中，不發請求
        await service.TickAsync();
        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Null(service.GetStatus().LastSuccessAt);

        // 第 2 次 tick：依然在執行中，不發請求且不改變狀態
        await service.TickAsync();
        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Null(service.GetStatus().LastSuccessAt);

        // 結構同步結束
        _syncState.EndRun(true);
        Assert.False(_structureSync.IsRunning);

        // 旗標解除後第 1 次 tick：發送請求並成功
        await service.TickAsync();
        Assert.Single(_stubHandler.RequestedUrls);
        Assert.NotNull(service.GetStatus().LastSuccessAt);
    }

    [Fact]
    public async Task 暫停超過15分鐘後恢復_寫出暫停時長()
    {
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(tableJson));
        SetupTargetSensors(new[] { (101L, "Ping") });

        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        Assert.True(_syncState.TryBegin());
        await service.TickAsync();

        clock = DateTime.Today.AddHours(10).AddMinutes(40);
        _syncState.EndRun(true);
        await service.TickAsync();

        Assert.Contains(service.ExecutionOutputs, l =>
            l == "快照已恢復：因「結構同步執行中，暫停快照」暫停 40 分鐘（10:00～10:40），這段期間的取樣列 coverage 會偏低");

        // 已清掉暫停開始時間：之後正常 tick 不再重複寫
        clock = clock.AddMinutes(20);
        await service.TickAsync();
        Assert.Single(service.ExecutionOutputs, l => l.StartsWith("快照已恢復"));
    }

    [Fact]
    public async Task 暫停不到15分鐘_不寫()
    {
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(tableJson));
        SetupTargetSensors(new[] { (101L, "Ping") });

        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        Assert.True(_syncState.TryBegin());
        await service.TickAsync();

        clock = DateTime.Today.AddHours(10).AddMinutes(10);
        _syncState.EndRun(true);
        await service.TickAsync();

        Assert.DoesNotContain(service.ExecutionOutputs, l => l.StartsWith("快照已恢復"));
        Assert.Single(_stubHandler.RequestedUrls); // 恢復後照常快照，不是整條沒走到
    }

    [Fact]
    public async Task 暫停中原因改變_不重設開始時間且印最後原因()
    {
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(tableJson));
        SetupTargetSensors(new[] { (101L, "Ping") });

        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        Assert.True(_syncState.TryBegin());
        await service.TickAsync();

        // 10:10 同步結束但回填開始：原因換了，開始時間仍是 10:00
        clock = DateTime.Today.AddHours(10).AddMinutes(10);
        _syncState.EndRun(true);
        Assert.True(_backfillState.TryBeginRun(out _));
        await service.TickAsync();

        clock = DateTime.Today.AddHours(10).AddMinutes(30);
        _backfillState.FinishRun(true, cancelled: false);
        await service.TickAsync();

        Assert.Contains(service.ExecutionOutputs, l =>
            l.Contains("因「歷史回填執行中，暫停快照」暫停 30 分鐘（10:00～10:30）"));
    }

    [Fact]
    public async Task TickAsync_正常一次快照_僅目標集合內感測器進累積器()
    {
        SetupTargetSensors(new[] { (101L, "Ping"), (102L, "Ping") }, nonTargets: new[] { (103L, "Ping") });

        var json = "{\"treesize\":3,\"sensors\":[" +
                    "{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}," +
                    "{\"objid\":102,\"lastvalue_raw\":20,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}" +
                   "]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(json));

        var service = CreateService();
        await service.TickAsync();

        var status = service.GetStatus();
        Assert.Equal(2, status.PendingSamples);
        Assert.Equal(2, status.LastSensorCount);
    }

    [Fact]
    public async Task TickAsync_流量類正規化換算_非流量類維持原值()
    {
        SetupTargetSensors(new[] { (201L, "SNMP Traffic 64bit"), (202L, "Ping") });

        var json = "{\"treesize\":2,\"sensors\":[" +
                   "{\"objid\":201,\"lastvalue_raw\":100,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}," +
                   "{\"objid\":202,\"lastvalue_raw\":100,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}" +
                   "]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(json));

        var service = CreateService();
        await service.TickAsync();

        var rows = service.Accumulator.DrainAll(1, DateTime.Now);
        var row201 = rows.Single(r => r.SensorObjid == 201);
        var row202 = rows.Single(r => r.SensorObjid == 202);

        Assert.Equal(6000.0, row201.AvgValue); // 100 * 3600 / 60
        Assert.Equal(100.0, row202.AvgValue);  // 100
    }

    [Fact]
    public async Task TickAsync_Interval無法解析_以60秒計且記錄警告()
    {
        SetupTargetSensors(new[] { (301L, "SNMP Traffic 64bit") });

        var json = "{\"treesize\":1,\"sensors\":[" +
                   "{\"objid\":301,\"lastvalue_raw\":100,\"interval\":\"abc\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}" +
                   "]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(json));

        var console = new TestConsole();
        var service = CreateService(console);
        await service.TickAsync();

        var rows = service.Accumulator.DrainAll(1, DateTime.Now);
        var row301 = rows.Single(r => r.SensorObjid == 301);
        Assert.Equal(6000.0, row301.AvgValue);

        Assert.Contains(console.Lines, l => l.Contains("無法解析") && l.Contains("以 60 秒計"));
        Assert.Contains(service.ExecutionOutputs, l => l.Contains("無法解析") && l.Contains("以 60 秒計"));
    }

    [Fact]
    public async Task TickAsync_傳輸失敗後須以五批實際容量試測重新准入再恢復()
    {
        SetupTargetSensors(new[] { (401L, "Ping") });
        var service = CreateService();
        // A failed live request invalidates full admission. A one-sensor scope may make one
        // timeout-bounded recovery attempt per ordinary tick using the same lease and qualified
        // profile evidence; it remains unqualified until five real full-batch successes.
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        var originalInterval = service.GetStatus().IntervalMinutes;
        Assert.Equal(15, originalInterval);

        _stubHandler.OnSend = (_, _) => throw new HttpRequestException("Simulated HTTP failure");

        await service.TickAsync();
        Assert.Equal(1, service.GetStatus().ConsecutiveFailures);
        Assert.Equal(15, service.GetStatus().IntervalMinutes);
        Assert.Single(SnapshotUrls());

        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Equal(2, service.GetStatus().ConsecutiveFailures);
        Assert.Equal(15, service.GetStatus().IntervalMinutes);
        Assert.Equal(2, SnapshotUrls().Count);
        var selection = PrtgSnapshotTargetResolver.Resolve(_backend, _hostStore, _settingsStore.Get(), Array.Empty<Sentinel>());
        Assert.False(PrtgCapacityRuntimeAdmission.TryGetCurrent(_backend, _hostStore, _settingsStore.Get(),
            selection, out _, out _));
        Assert.All(SnapshotUrls(), url => Assert.Contains("filter_objid=401", url));

        var successJson = "{\"treesize\":1,\"sensors\":[{\"objid\":401,\"lastvalue_raw\":50,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(successJson));
        var settings = _settingsStore.Get();
        var planStore = new PrtgCapacityAdmissionPlanStore(_backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
        var currentPlan = planStore.ReadCurrent(DateTimeOffset.UtcNow);
        Assert.NotNull(currentPlan);
        var originalPlanFingerprint = currentPlan!.Fingerprint;
        var policyRevision = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get().Revision;
        var existingValues = _backend.PrtgStore().GetValues(clock.Date, clock.Date.AddDays(1)).Count;
        var requestsBeforePilot = SnapshotUrls().Count;
        var pilot = new PrtgSnapshotCapacityPilot(_backend, _ =>
            new PrtgClient("https://prtg.example.com", "token123", 30, true, _stubHandler,
                PrtgAuthModes.Token, "", "", "", CreateFixtureBudget()));
        var pilotResult = await pilot.RunAsync(_hostStore, settings, Array.Empty<Sentinel>(), CancellationToken.None);
        Assert.Equal("capacity-qualified", pilotResult.Status);
        Assert.Equal(PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples, pilotResult.RequestsSent);
        Assert.Equal(PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples,
            pilotResult.MatchingFullBatchSamples);
        Assert.Equal(requestsBeforePilot + PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples,
            SnapshotUrls().Count);
        Assert.Equal(settings.Revision, _settingsStore.Get().Revision);
        Assert.Equal(policyRevision, new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get().Revision);
        Assert.Equal(existingValues, _backend.PrtgStore().GetValues(clock.Date, clock.Date.AddDays(1)).Count);
        Assert.Equal(originalPlanFingerprint, planStore.ReadCurrent(DateTimeOffset.UtcNow)?.Fingerprint);

        // The capacity pilot does not count as a live snapshot attempt. Wait the strategy's
        // ordinary interval from the bounded runtime recovery attempt, then verify the same current plan
        // admits a successful production snapshot and clears both real transport failures.
        clock = clock.AddMinutes(30);
        await service.TickAsync();
        var recovered = service.GetStatus();
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Equal(originalInterval, recovered.IntervalMinutes);
        Assert.Equal(clock, recovered.LastSuccessAt);
        Assert.Equal(requestsBeforePilot + PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples + 1,
            SnapshotUrls().Count);
        Assert.Equal(originalPlanFingerprint, planStore.ReadCurrent(DateTimeOffset.UtcNow)?.Fingerprint);
    }

    /// <summary>
    /// PRTG 呼叫成功但整點寫入資料庫失敗：取出的列已不在累積器，必須留著下次再寫，
    /// 而且這不是 PRTG 的失敗，不能推進退避。
    /// </summary>
    [Fact]
    public async Task TickAsync_整點寫入資料庫失敗_列留待下次重試且不計為PRTG失敗()
    {
        SetupTargetSensors(new[] { (501L, "Ping") });
        var json = "{\"treesize\":1,\"sensors\":[{\"objid\":501,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(json));

        var console = new TestConsole();
        var service = CreateService(console);
        var clock = DateTime.Today.AddHours(10).AddMinutes(5);
        service.Now = () => clock;

        // 10:05 取到一筆樣本，留在 10 點桶
        await service.TickAsync();
        Assert.Equal(1, service.GetStatus().PendingSamples);

        // 讓數值表暫時不存在：跨到 11 點後的整點寫入會失敗
        var dbPath = Path.Combine(_dir, "test.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE lf_prtg_values RENAME TO lf_prtg_values_hidden";
            cmd.ExecuteNonQuery();
        }

        clock = clock.AddHours(1);
        await service.TickAsync();
        var status = service.GetStatus();
        Assert.NotNull(status.LastSuccessAt);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Equal(15, status.IntervalMinutes);
        Assert.Contains(console.Lines, l => l.Contains("寫入資料庫失敗") && l.Contains("留待下次重試"));

        // 表回來後，下一次快照把留著的列補寫進去
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE lf_prtg_values_hidden RENAME TO lf_prtg_values";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        clock = clock.AddMinutes(15);
        await service.TickAsync();

        var written = _backend.PrtgStore().GetDailyValueAggregations(DateTime.Today, DateTime.Today.AddDays(1));
        var row = Assert.Single(written, a => a.SensorObjid == 501);
        // 只有 1 個樣本（coverage 25），是一列 sampled 但不可用：總數 1、可用 0
        Assert.Equal(1, row.TotalCount);
        Assert.Equal(0, row.UsableCount);
    }

    /// <summary>
    /// 退避期間取到的樣本本來就少，coverage 要照策略設定的間隔算（15 分鐘＝一小時 4 個），
    /// 不能照拉長後的間隔算——否則 1 個樣本會被算成滿涵蓋，通過可用門檻進基線。
    /// </summary>
    [Fact]
    public async Task TickAsync_退避期間的coverage_以策略間隔而非生效間隔計算()
    {
        SetupTargetSensors(new[] { (601L, "Ping") });
        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        _stubHandler.OnSend = (_, _) => throw new HttpRequestException("Simulated HTTP failure");
        for (var i = 0; i < 3; i++)
        {
            await service.TickAsync();
            // The failed transport invalidates the prior capacity proof. Supply fresh
            // synthetic pilot evidence so the next scheduled attempt reaches backoff logic.
            SeedSyntheticCapacityEvidence(9_000);
            clock = clock.AddMinutes(15);
        }
        Assert.Equal(30, service.GetStatus().IntervalMinutes);

        // 失敗在 10:00／10:15／10:30，退避後間隔 30 分鐘；11:00 成功一次：11 點桶只有 1 個樣本
        var successJson = "{\"treesize\":1,\"sensors\":[{\"objid\":601,\"lastvalue_raw\":50,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(successJson));
        clock = DateTime.Today.AddHours(11);
        await service.TickAsync();
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);

        // 跨到 12 點，整點寫出 11 點桶
        clock = DateTime.Today.AddHours(12);
        await service.TickAsync();

        var rows = _backend.PrtgStore().GetDailyValueAggregations(DateTime.Today, DateTime.Today.AddDays(1));
        var agg = Assert.Single(rows, a => a.SensorObjid == 601);
        // 1 個樣本 ÷ 期望 4 個 ＝ 25，coverage 不足 75 → 不算可用列
        Assert.Equal(0, agg.UsableCount);
        Assert.Equal(1, agg.OtherCount);
    }

    /// <summary>
    /// 執行輸出只留最近的紀錄：站台長時間運行，每次截斷或間隔解析失敗都會寫一行，
    /// 沒有上限就是記憶體洩漏。
    /// </summary>
    [Fact]
    public async Task TickAsync_執行輸出有上限_長時間運行不無限增長()
    {
        SetupTargetSensors(new[] { (601L, "SNMP Traffic 64bit") });
        var service = CreateService();
        var clock = DateTime.Today.AddHours(1);
        service.Now = () => clock;

        // 先由真實 Tick 證明警告進入同一輸出管線；裁切測試不重跑 130 輪 DB／取數工作。
        var invalidIntervalJson = "{\"treesize\":1,\"sensors\":[{\"objid\":601,\"lastvalue_raw\":100,\"interval\":\"abc\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(invalidIntervalJson));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await service.TickAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(SnapshotUrls());
        Assert.Contains(service.ExecutionOutputs, line => line.Contains("間隔") && line.Contains("60"));
        var writeOutput = typeof(PrtgSnapshotHostedService).GetMethod("WriteOutput",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        for (var i = 0; i < 130; i++)
            writeOutput.Invoke(service, [$"bounded-output-{i}", NLog.LogLevel.Warn]);

        Assert.True(service.ExecutionOutputs.Count <= 100,
            $"執行輸出應有上限，實際 {service.ExecutionOutputs.Count} 筆");
        // 確認真的每輪都有寫（上限有被撞到），不是因為沒輸出才恆成立
        Assert.Equal(100, service.ExecutionOutputs.Count);
        Assert.Contains(service.ExecutionOutputs, line => line.Contains("bounded-output-129"));
        Assert.DoesNotContain(service.ExecutionOutputs, line => line.Contains("bounded-output-0"));
    }

    /// <summary>
    /// 退避必須真的讓出 PRTG：失敗之後，未到生效間隔的 tick 不能再發請求。
    /// 以「上次成功」量間隔的話，PRTG 一直回不來時上次成功永遠停在過去（或根本沒有），
    /// 間隔條件恆成立，服務會每一輪都打一次全量快照——正好是退避要防止的事。
    /// 用 ConsecutiveFailures 當訊號：被跳過的 tick 不會增加它，真的去打而失敗才會。
    /// </summary>
    [Fact]
    public async Task TickAsync_失敗後未到間隔_不重打PRTG()
    {
        SetupTargetSensors(new[] { (501L, "Ping") });
        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        _stubHandler.OnSend = (_, _) => throw new HttpRequestException("Simulated HTTP failure");

        await service.TickAsync();
        Assert.Equal(1, service.GetStatus().ConsecutiveFailures);
        SeedSyntheticCapacityEvidence(9_000);

        // 一分鐘後，遠小於保守策略的 15 分鐘：不得再打
        clock = clock.AddMinutes(1);
        await service.TickAsync();
        Assert.Equal(1, service.GetStatus().ConsecutiveFailures);

        // 距上次嘗試滿 15 分鐘：可以再試
        clock = clock.AddMinutes(14);
        await service.TickAsync();
        Assert.Equal(2, service.GetStatus().ConsecutiveFailures);
    }

    [Fact]
    public async Task TickAsync_整點寫出_累積器上一小時資料寫入lf_prtg_values()
    {
        SetupTargetSensors(new[] { (501L, "Ping") });

        var now = DateTime.Now;
        var prevHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(-1);

        var successJson = "{\"treesize\":1,\"sensors\":[{\"objid\":501,\"lastvalue_raw\":50,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(successJson));

        var service = CreateService();
        service.Accumulator.Add(501L, prevHour.AddMinutes(15), 88.0);
        service.Accumulator.Add(501L, prevHour.AddMinutes(30), 92.0);

        await service.TickAsync();

        var store = _backend.PrtgStore();
        var values = store.GetValues(prevHour, prevHour.AddHours(1));
        Assert.Single(values);
        var row = values[0];
        Assert.Equal(501L, row.SensorObjid);
        Assert.Equal(prevHour, row.PeriodStart);
        Assert.Equal(PrtgDataQuality.Sampled, row.Quality);
        Assert.Equal(90.0, row.AvgValue);
    }


    [Fact]
    public void ApplicationStopping_站台關閉時寫出當前小時殘存樣本()
    {
        SetupTargetSensors(new[] { (701L, "Ping") });
        var service = CreateService();

        var now = DateTime.Now;
        var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);

        service.Accumulator.Add(701L, currentHour.AddMinutes(5), 75.0);
        Assert.Equal(1, service.GetStatus().PendingSamples);

        _lifetime.StopApplication();

        Assert.Equal(0, service.GetStatus().PendingSamples);

        var store = _backend.PrtgStore();
        var values = store.GetValues(currentHour, currentHour.AddHours(1));
        Assert.Single(values);
        Assert.Equal(701L, values[0].SensorObjid);
        Assert.Equal(PrtgDataQuality.Sampled, values[0].Quality);
        Assert.Equal(75.0, values[0].AvgValue);
    }

    [Fact]
    public async Task 待寫樣本_資料庫恢復後即使停用PRTG仍補寫且狀態如實顯示()
    {
        var service = CreateService();
        var hour = DateTime.Today.AddHours(8);
        var rows = new[] { new PrtgValueRow
        {
            SensorObjid = 701,
            PeriodStart = hour,
            AvgValue = 42,
            Coverage = 25,
            Quality = PrtgDataQuality.Sampled,
            CreatedAt = hour.AddMinutes(15)
        } };
        var dbOptions = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options;
        using (var ctx = new LfDbContext(dbOptions))
            ctx.Database.ExecuteSqlRaw("DROP TABLE lf_prtg_values");

        typeof(PrtgSnapshotHostedService)
            .GetMethod("WriteSampledRows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(service, new object[] { rows });
        Assert.Equal(1, service.GetStatus().PendingSamples);

        using (var ctx = new LfDbContext(dbOptions)) SchemaUpgrader.Upgrade(ctx);
        _settingsStore.Update(s => s.PrtgEnabled = false);
        await service.TickAsync();

        Assert.Equal(0, service.GetStatus().PendingSamples);
        Assert.Single(_backend.PrtgStore().GetValues(hour, hour.AddHours(1)));
        Assert.Empty(_stubHandler.RequestedUrls);
    }

    [Fact]
    public async Task 正式responseconsumer_15000個感測器以兩個durable局部delta保存()
    {
        var service = CreateService();
        var now = DateTime.Today.AddHours(10);
        service.Now = () => now;
        await service.TickAsync(); // RestoreJournal / EnableIncremental 在正式 worker 路徑啟動。

        var parse = typeof(PrtgSnapshotHostedService).GetMethod("ParseAndCheckpoint",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var tallyType = typeof(PrtgSnapshotHostedService).GetNestedType("SnapshotTally",
            System.Reflection.BindingFlags.NonPublic)!;
        for (var start = 1; start <= 15_000; start += 7_500)
        {
            var sensors = Enumerable.Range(start, 7_500).Select(id => new
            {
                objid = id,
                lastvalue_raw = (double)id,
                interval = "60 s"
            });
            var json = System.Text.Json.JsonSerializer.Serialize(new { sensors });
            var tally = Activator.CreateInstance(tallyType, nonPublic: true)!;
            var accepted = Enumerable.Range(start, 7_500).Select(id => (long)id).ToHashSet();
            var policy = new PrtgMonitoringPolicy();
            var strategy = new PrtgTrustedSamplingStrategyContext("conservative", 15, "", default,
                "", "", ["current_strategy_contract_missing"]);
            parse.Invoke(service, new object[] { json, now, tally, accepted, false, policy,
                new Dictionary<long, PrtgTrustedSamplingProfile>(), new Dictionary<long, PrtgResourceIdentity>(),
                strategy, DateTime.UtcNow });
        }

        var endpoint = PrtgSnapshotJournal.Binding(_backend, _settingsStore.Get().PrtgUrl);
        var journal = new PrtgSnapshotJournal(_backend);
        var restored = journal.Load(endpoint, now)!;
        Assert.Equal(15_000, restored.Accumulator.Count);
        var segmentDirectory = Directory.GetDirectories(Path.Combine(_dir, "pending", "prtg-snapshot"), "checkpoint.g.*")
            .Select(path => Path.Combine(path, "segments")).Single();
        Assert.Equal(3, Directory.GetFiles(segmentDirectory, "*.json").Length); // snapshot base + two response deltas
        Assert.False(File.Exists(journal.FilePath));
    }

    [Fact]
    public async Task SQL已提交但ack段尚未發布_恢復以同一GUID重試且coverage不重複()
    {
        var service = CreateService();
        var now = DateTime.Today.AddHours(8);
        service.Now = () => now;
        var journal = (PrtgSnapshotJournal)typeof(PrtgSnapshotHostedService)
            .GetField("_journal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        var durableWrites = 0;
        journal.FaultPoint = point =>
        {
            if (point == "after-segment-flush" && ++durableWrites == 2)
                throw new IOException("simulated crash before ack manifest");
        };
        var row = new PrtgValueRow
        {
            SensorObjid = 801,
            PeriodStart = now,
            AvgValue = 12,
            MinValue = 12,
            MaxValue = 12,
            Coverage = 50,
            Quality = PrtgDataQuality.Sampled,
            CreatedAt = now
        };
        typeof(PrtgSnapshotHostedService)
            .GetMethod("WriteSampledRows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(service, new object[] { new[] { row } });
        Assert.Equal(1, service.GetStatus().PendingSamples);
        var endpoint = PrtgSnapshotJournal.Binding(_backend, _settingsStore.Get().PrtgUrl);
        var queued = new PrtgSnapshotJournal(_backend).Load(endpoint, now)!.Pending;
        var batch = Assert.Single(queued);
        var saved = Assert.Single(_backend.PrtgStore().GetValues(now, now.AddHours(1)));
        Assert.Equal(50, saved.Coverage);

        journal.FaultPoint = null;
        _settingsStore.Update(settings => settings.PrtgEnabled = false);
        service.Now = () => now.AddMinutes(20);
        await service.TickAsync();

        Assert.Equal(0, service.GetStatus().PendingSamples);
        Assert.Empty(new PrtgSnapshotJournal(_backend).Load(endpoint, now)!.Pending);
        using var verify = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        Assert.Contains(batch.Id, verify.PrtgSampledBatches.Select(entry => entry.BatchId).ToArray());
        var final = Assert.Single(_backend.PrtgStore().GetValues(now, now.AddHours(1)));
        Assert.Equal(50, final.Coverage);
    }

    [Fact]
    public async Task TickAsync_取數執行PRTG階段_不發送請求()
    {
        SetupTargetSensors(new[] { (801L, "Ping") });
        var json = "{\"treesize\":1,\"sensors\":[{\"objid\":801,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(json));

        var service = CreateService();

        Assert.True(_schedulerRunState.TryBeginRun("schedule", out _));
        _schedulerRunState.ReportProgress("prtg-sync", 5, 10);

        await service.TickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Null(service.GetStatus().LastSuccessAt);

        _schedulerRunState.ReportProgress(SchedulerRunState.PrtgDonePhase, 10, 10);

        await service.TickAsync();
        Assert.Single(_stubHandler.RequestedUrls);
    }

    [Fact]
    public async Task 前置條件不成立時記錄暫停原因()
    {
        SetupTargetSensors(new[] { (901L, "Ping") });
        var service = CreateService();

        Assert.True(_syncState.TryBegin());
        Assert.True(_structureSync.IsRunning);

        await service.TickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        var status = service.GetStatus();
        Assert.NotNull(status.LastSkipReason);
        Assert.Contains("結構同步", status.LastSkipReason);
    }

    [Fact]
    public async Task 前置條件恢復後清除暫停原因()
    {
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":902,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(tableJson));
        SetupTargetSensors(new[] { (902L, "Ping") });
        var service = CreateService();

        Assert.True(_syncState.TryBegin());
        await service.TickAsync();
        Assert.Contains("結構同步", service.GetStatus().LastSkipReason);

        _syncState.EndRun(true);
        Assert.False(_structureSync.IsRunning);

        await service.TickAsync();
        var resumed = service.GetStatus();
        Assert.NotNull(resumed.LastSuccessAt);
        Assert.DoesNotContain("結構同步", resumed.LastSkipReason);
        Assert.Contains("狀態補抓 Queue", resumed.LastSkipReason);
    }

    [Fact]
    public async Task 未到間隔不設暫停原因()
    {
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":903,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(tableJson));
        SetupTargetSensors(new[] { (903L, "Ping") });
        var service = CreateService();

        await service.TickAsync();
        Assert.NotNull(service.GetStatus().LastSuccessAt);
        Assert.DoesNotContain("暫停快照", service.GetStatus().LastSkipReason);

        var firstSuccessAt = service.GetStatus().LastSuccessAt;
        var requestsBeforeIntervalTick = SnapshotUrls().Count;
        await service.TickAsync();
        var intervalStatus = service.GetStatus();
        Assert.Equal(firstSuccessAt, intervalStatus.LastSuccessAt);
        Assert.Equal(requestsBeforeIntervalTick, SnapshotUrls().Count);
        // An interval-only idle tick clears the prior operational queue note;
        // it must neither send HTTP nor report a maintenance pause.
        Assert.Null(intervalStatus.LastSkipReason);
    }

    [Theory]
    [InlineData("structure")]
    [InlineData("backfill")]
    [InlineData("nightly")]
    public async Task 長維護仍保留三輪採樣且不擴張補抓(string kind)
    {
        SetupTargetSensors(new[] { (903L, "Ping") });
        _stubHandler.OnSend = (request, _) => Task.FromResult(FilteredValues(request.RequestUri!.ToString()));
        if (kind == "structure") Assert.True(_syncState.TryBegin());
        else if (kind == "backfill") Assert.True(_backfillState.TryBeginRun(out _));
        else
        {
            Assert.True(_schedulerRunState.TryBeginRun("schedule", out _));
            _schedulerRunState.ReportProgress("prtg-sync", 0, 10);
        }
        var clock = DateTime.Today.AddHours(8);
        var service = CreateService();
        service.Now = () => clock;
        await service.TickAsync();
        Assert.Empty(SnapshotUrls());
        for (var round = 1; round <= 3; round++)
        {
            clock = clock.AddMinutes(15);
            await service.TickAsync();
            Assert.Equal(round, SnapshotUrls().Count);
            Assert.Equal(clock, service.GetStatus().LastSuccessAt);
            Assert.Null(service.GetStatus().LastSkipReason);
        }
        Assert.Empty(BackfillUrls());
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
        Assert.True(kind == "structure" ? _structureSync.IsRunning
            : kind == "backfill" ? _backfill.GetStatus().IsRunning : _schedulerRunState.IsRunning);
    }

    [Fact]
    public async Task 維護原因變更不會重新延長採樣等待期限()
    {
        SetupTargetSensors(new[] { (906L, "Ping") });
        _stubHandler.OnSend = (request, _) => Task.FromResult(FilteredValues(request.RequestUri!.ToString()));
        var clock = DateTime.Today.AddHours(8);
        var service = CreateService();
        service.Now = () => clock;
        Assert.True(_syncState.TryBegin());
        await service.TickAsync();
        clock = clock.AddMinutes(8);
        _syncState.EndRun(true);
        Assert.True(_backfillState.TryBeginRun(out _));
        await service.TickAsync();
        Assert.Empty(SnapshotUrls());
        clock = clock.AddMinutes(7);
        await service.TickAsync();
        Assert.Single(SnapshotUrls());
        Assert.Equal(clock, service.GetStatus().LastSuccessAt);
    }

    [Fact]
    public async Task 歷史工作用滿配額仍可透過同一正式budget完成到期採樣()
    {
        SetupTargetSensors(new[] { (905L, "Ping") });
        _stubHandler.OnSend = (request, _) => Task.FromResult(FilteredValues(request.RequestUri!.ToString()));
        Assert.True(_backfillState.TryBeginRun(out _));
        var clock = DateTime.Today.AddHours(8);
        var budgetClock = new PrtgRequestBudgetTests.TestPrtgClock(new DateTimeOffset(clock));
        var budget = new PrtgRequestBudget(budgetClock);
        var admissionPlan = Assert.IsType<PrtgCapacityAdmissionPlan>(new PrtgCapacityAdmissionPlanStore(
            _backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey)).ReadCurrent(DateTimeOffset.UtcNow));
        budget.SetAdmissionPlan(admissionPlan);
        using var historicalClient = PrtgClientFactory.Create(_settingsStore.Get(), _stubHandler, budget);
        for (var i = 1; i <= 5; i++)
            await historicalClient.GetJsonAsync($"api/historicdata.json?id={i}");
        using var pendingCancellation = new CancellationTokenSource();
        var pending = historicalClient.GetJsonAsync("api/historicdata.json?id=6", pendingCancellation.Token);
        var service = CreateService();
        service.ClientFactory = () => PrtgClientFactory.Create(_settingsStore.Get(), _stubHandler, budget);
        service.Now = () => clock;
        try
        {
            await WaitUntilAsync(() => budget.WaiterCount == 1);
            await service.TickAsync();
            clock = clock.AddMinutes(15);
            await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(clock, service.GetStatus().LastSuccessAt);
            Assert.Single(SnapshotUrls());
            Assert.False(pending.IsCompleted);
            Assert.Equal(5, _stubHandler.RequestedUrls.Count(url => url.Contains("historicdata.json")));
            Assert.Equal(0, budget.InFlightCount);
            Assert.True(_backfill.GetStatus().IsRunning);
        }
        finally
        {
            pendingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task 真實服務迴圈有待處理範圍更新時仍按期限採樣()
    {
        SetupTargetSensors(new[] { (904L, "Ping") });
        _stubHandler.OnSend = (request, _) => Task.FromResult(FilteredValues(request.RequestUri!.ToString()));
        Assert.True(_syncState.TryBegin());
        var initial = DateTime.Today.AddHours(8);
        var clockTicks = initial.Ticks;
        var service = CreateService(pollInterval: TimeSpan.FromMilliseconds(10));
        service.Now = () => new DateTime(Interlocked.Read(ref clockTicks));
        service.RequestScopeRefresh();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => service.GetStatus().LastSkipReason?.Contains("結構同步") == true);
            for (var round = 1; round <= 3; round++)
            {
                var expected = initial.AddMinutes(round * 15);
                Interlocked.Exchange(ref clockTicks, expected.Ticks);
                service.RequestScopeRefresh();
                await WaitUntilAsync(() => service.GetStatus().LastSuccessAt == expected);
                Assert.Equal(round, SnapshotUrls().Count);
            }
            Assert.True(_structureSync.IsRunning);
            Assert.Empty(BackfillUrls());
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stop.Token);
        }
    }

    [Fact]
    public async Task 補抓逾期會讓出採樣且取消的補抓不標記裝置為空()
    {
        SetupOkDevices(new long[] { 10, 20 });
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now);
        SeedCurrentMappedJointAdmission();
        _stubHandler.OnSend = async (request, ct) =>
        {
            if (IsBackfillUrl(request.RequestUri!.ToString()))
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return FilteredValues(request.RequestUri!.ToString());
        };
        var clock = DateTime.Today.AddHours(8);
        var service = CreateService();
        service.Now = () => clock;
        service.ScopeWorkBudget = TimeSpan.FromSeconds(2);
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(clock, service.GetStatus().LastSuccessAt);
        Assert.Single(SnapshotUrls());
        Assert.Single(BackfillUrls());
        Assert.Contains(service.ExecutionOutputs, line => line.Contains("補抓已達期限"));
        clock = clock.AddMinutes(15);
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, SnapshotUrls().Count);
        Assert.Equal(2, BackfillUrls().Count);
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
    }

    [Fact]
    public async Task 真實服務迴圈補抓持續逾期後仍恢復下一輪採樣()
    {
        SetupOkDevices(new long[] { 10, 20 });
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now);
        SeedCurrentMappedJointAdmission();
        _stubHandler.OnSend = async (request, ct) =>
        {
            if (IsBackfillUrl(request.RequestUri!.ToString()))
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return FilteredValues(request.RequestUri!.ToString());
        };
        var initial = DateTime.Today.AddHours(8);
        var clockTicks = initial.Ticks;
        var service = CreateService(pollInterval: TimeSpan.FromMilliseconds(10));
        service.Now = () => new DateTime(Interlocked.Read(ref clockTicks));
        service.ScopeWorkBudget = TimeSpan.FromSeconds(2);
        service.RequestScopeRefresh();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => service.GetStatus().LastSuccessAt == initial, TimeSpan.FromSeconds(15));
            Assert.Single(SnapshotUrls());
            var next = initial.AddMinutes(15);
            Interlocked.Exchange(ref clockTicks, next.Ticks);
            service.RequestScopeRefresh();
            await WaitUntilAsync(() => service.GetStatus().LastSuccessAt == next, TimeSpan.FromSeconds(15));
            Assert.Equal(2, SnapshotUrls().Count);
            Assert.True(BackfillUrls().Count >= 2);
            Assert.DoesNotContain(_backend.PrtgStore().GetAllSensors(), row => row.DeviceObjid == 20);
            Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stop.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 補抓第一頁完成後取消不留下部分鏡像且重啟後仍重試(bool restart)
    {
        SetupOkDevices(new long[] { 10, 20 });
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now);
        SeedCurrentMappedJointAdmission();
        var backfilledSensorIds = Enumerable.Range(2000, 5000).Select(id => (long)id)
            .Append(9001L).ToArray();
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.Revision = Guid.NewGuid().ToString("N");
            policy.SensorIds = policy.SensorIds.Concat(backfilledSensorIds).Distinct().Order().ToList();
        });
        // The first admitted pass still targets only the mirrored sensor on device 10.
        // Capacity evidence uses this current bound identity as its representative; the
        // unmirrored policy IDs do not receive fabricated resource identities.
        SeedSyntheticCapacityEvidence(9_000);
        var clock = DateTime.Today.AddHours(8);
        var pageTwoReached = false;
        var first = true;
        var pageOne = System.Text.Json.JsonSerializer.Serialize(new
        {
            treesize = 5001,
            sensors = Enumerable.Range(2000, 5000).Select(id => new
            { objid = id, parentid = 20, sensor = "S", type = "ping", status = "Up", paused = false })
        });
        var pageTwo = "{\"treesize\":5001,\"sensors\":[{\"objid\":9001,\"parentid\":20,\"sensor\":\"S\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false}]}";
        _stubHandler.OnSend = async (request, ct) =>
        {
            var url = request.RequestUri!.ToString();
            if (!IsBackfillUrl(url)) return FilteredValues(url);
            if (url.Contains("start=0")) return JsonResponse(pageOne);
            pageTwoReached = true;
            if (first) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse(pageTwo);
        };
        var service = CreateService();
        service.Now = () => clock;
        service.ScopeWorkBudget = TimeSpan.FromSeconds(2);
        _sqlDiagnostics.Reset();
        var firstTickTimer = Stopwatch.StartNew();
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(8));
        firstTickTimer.Stop();
        var firstTickSql = _sqlDiagnostics.ReadAndReset();
        Assert.True(pageTwoReached, "必須先完整讀到第一頁，再於第二頁等待時取消。");
        Assert.DoesNotContain(_backend.PrtgStore().GetAllSensors(), row => row.DeviceObjid == 20);
        Assert.Equal(clock, service.GetStatus().LastSuccessAt);
        Assert.Single(SnapshotUrls()); // The initial one-sensor snapshot completed before backfill cancellation.
        first = false;
        if (restart)
        {
            service.Dispose();
            service = CreateService();
            service.Now = () => clock;
        }
        service.ScopeWorkBudget = TimeSpan.FromSeconds(30);
        clock = clock.AddMinutes(15);
        _sqlDiagnostics.Reset();
        var secondTickTimer = Stopwatch.StartNew();
        try
        {
            await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            secondTickTimer.Stop();
            var secondTickSql = _sqlDiagnostics.ReadAndReset();
            _output.WriteLine("R05 backfill diagnostic: first_tick_ms={0}; {1}",
                firstTickTimer.ElapsedMilliseconds, firstTickSql);
            _output.WriteLine("R05 backfill diagnostic: second_tick_ms={0}; {1}",
                secondTickTimer.ElapsedMilliseconds, secondTickSql);
        }
        Assert.Equal(5001, _backend.PrtgStore().GetAllSensors().Count(row => row.DeviceObjid == 20));
        Assert.Equal(4, BackfillUrls().Count);
        Assert.Equal(clock, service.GetStatus().LastSuccessAt);
        Assert.Equal(2, SnapshotUrls().Count); // This pass sampled the current small scope before backfill expanded to 5002 sensors.
        Assert.Contains("完整快照容量尚未驗證", service.GetStatus().LastSkipReason);
        Assert.Contains("5002 顆", service.GetStatus().LastSkipReason);
        Assert.DoesNotContain(_stubHandler.RequestedUrls,
            url => url.Contains("content=messages", StringComparison.Ordinal));
        Assert.Null(QueueItemForDevice(20).CompletedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 完整裝置補抓超額或跨裝置回應拒絕整批且下輪可重試(bool oversized)
    {
        SetupOkDevices(new long[] { 10, 20 });
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now);
        SeedCurrentMappedJointAdmission();
        var invalid = oversized
            ? System.Text.Json.JsonSerializer.Serialize(new { treesize = 1, sensors = new[]
                { new { objid = 201, parentid = 20, sensor = "S", type = "ping", tags = new string('x', 4 * 1024 * 1024 + 1) } } })
            : "{\"treesize\":1,\"sensors\":[{\"objid\":201,\"parentid\":999,\"sensor\":\"S\",\"type\":\"ping\"}]}";
        var first = true;
        _stubHandler.OnSend = (request, _) =>
        {
            var url = request.RequestUri!.ToString();
            return Task.FromResult(IsBackfillUrl(url)
                ? first ? JsonResponse(invalid) : DeviceSensors(url, new Dictionary<long, long> { [20] = 201 })
                : FilteredValues(url));
        };
        var clock = DateTime.Today.AddHours(8);
        var service = CreateService();
        service.Now = () => clock;
        await service.TickAsync();
        Assert.DoesNotContain(_backend.PrtgStore().GetAllSensors(), row => row.DeviceObjid != 10);
        Assert.Contains(service.ExecutionOutputs, line => line.Contains("感測器補抓失敗"));
        first = false;
        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Contains(_backend.PrtgStore().GetAllSensors(), row => row.Objid == 201 && row.DeviceObjid == 20);
        Assert.Equal(2, BackfillUrls().Count);
    }

    [Fact]
    public async Task 補抓第二批SQL失敗不留下部分鏡像且下一輪重試全部裝置()
    {
        SetupOkDevices(new long[] { 10, 20 });
        var store = _backend.PrtgStore();
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now);
        SeedCurrentMappedJointAdmission();
        store.ApplyAutoCategories(new Dictionary<string, string>());
        var revision = store.ReadCatalogueDataRevision();
        using (var context = _backend.CreateContext())
            context.Database.ExecuteSqlRaw("""
                CREATE TRIGGER fail_scope_sensor_second_batch BEFORE INSERT ON lf_prtg_sensors
                WHEN NEW.objid = 2500
                BEGIN SELECT RAISE(ABORT, 'injected scope second-batch failure'); END;
                """);
        var response = System.Text.Json.JsonSerializer.Serialize(new
        {
            treesize = 501,
            sensors = Enumerable.Range(2000, 501).Select(id => new
            { objid = id, parentid = 20, sensor = "S", type = "ping", status = "Up", paused = false })
        });
        _stubHandler.OnSend = (request, _) =>
        {
            var url = request.RequestUri!.ToString();
            return Task.FromResult(IsBackfillUrl(url) ? JsonResponse(response) : FilteredValues(url));
        };
        var clock = DateTime.Today.AddHours(8);
        var service = CreateService();
        service.Now = () => clock;
        await service.TickAsync();
        Assert.DoesNotContain(store.GetAllSensors(), sensor => sensor.DeviceObjid == 20);
        Assert.Equal(revision, store.ReadCatalogueDataRevision());
        Assert.Single(BackfillUrls());
        Assert.Equal(clock, service.GetStatus().LastSuccessAt);
        Assert.Single(SnapshotUrls());
        using (var context = _backend.CreateContext())
            context.Database.ExecuteSqlRaw("DROP TRIGGER fail_scope_sensor_second_batch;");
        clock = clock.AddMinutes(15);
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(35));
        Assert.Equal(501, store.GetAllSensors().Count(sensor => sensor.DeviceObjid == 20));
        Assert.Equal(2, BackfillUrls().Count);
        var retryStatus = service.GetStatus();
        var snapshotUrlsAfterRetry = SnapshotUrls();
        var snapshotIdsAfterRetry = snapshotUrlsAfterRetry.SelectMany(FilterObjids).Order().ToArray();
        var retryDiagnostic = $"snapshotHttpCount={snapshotUrlsAfterRetry.Count}; snapshotIds=[{string.Join(",", snapshotIdsAfterRetry)}]; " +
            $"mirroredDevice20={store.GetAllSensors().Count(sensor => sensor.DeviceObjid == 20)}; backfillHttpCount={BackfillUrls().Count}; " +
            $"lastSensorCount={retryStatus.LastSensorCount}; lastSkip={SafeBoundedDiagnostic(retryStatus.LastSkipReason)}";
        // Backfill changes the mirror, not the explicit selected policy. The second
        // snapshot still requests only the previously admitted sensor 101.
        Assert.True(clock == retryStatus.LastSuccessAt, retryDiagnostic);
        Assert.Equal(2, snapshotUrlsAfterRetry.Count);
        Assert.Equal(new long[] { 101, 101 }, snapshotIdsAfterRetry);
        Assert.DoesNotContain(snapshotIdsAfterRetry, id => id >= 2000);

        // Expanding the selected policy is a separate operator action and must
        // invalidate the prior one-sensor capacity evidence before any new HTTP.
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.Revision = Guid.NewGuid().ToString("N");
            policy.SensorIds = store.GetAllSensors().Select(sensor => sensor.Objid).Order().ToList();
        });
        clock = clock.AddMinutes(15);
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(35));
        Assert.Equal(clock.AddMinutes(-15), service.GetStatus().LastSuccessAt);
        Assert.Equal(2, SnapshotUrls().Count);
        Assert.Contains("Joint PRTG admission", service.GetStatus().LastSkipReason);
    }

    [Fact]
    public async Task 父工作取消停止已開始採樣且不開補抓或誤判補抓期限()
    {
        SetupOkDevices(new long[] { 10, 20 });
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now);
        SeedCurrentMappedJointAdmission();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stubHandler.OnSend = async (_, ct) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse("{}");
        };
        var service = CreateService();
        service.ScopeWorkBudget = TimeSpan.FromSeconds(5);
        using var cancelled = new CancellationTokenSource();
        var tick = service.TickAsync(cancelled.Token);
        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await tick.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancelled.Cancel();
            try { await tick.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }
        // Tick-first legitimately admitted one snapshot before the caller cancelled it.
        Assert.Single(SnapshotUrls());
        Assert.Empty(BackfillUrls());
        Assert.Single(_stubHandler.RequestedUrls);
        Assert.Null(service.GetStatus().LastSuccessAt);
        Assert.DoesNotContain(service.ExecutionOutputs, line => line.Contains("補抓已達期限"));
    }

    private static async Task WaitUntilAsync(Func<bool> ready, TimeSpan? timeout = null)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && deadline.Elapsed < (timeout ?? TimeSpan.FromSeconds(5)))
            await Task.Delay(10);
        Assert.True(ready(), "服務迴圈沒有在有界等待內完成預期採樣。");
    }

    // ── 分批快照與範圍補抓 ──

    private static IEnumerable<(long Objid, string SensorType)> ManyTargets(long start, int count) =>
        Enumerable.Range(0, count).Select(i => (start + i, "Ping"));

    /// <summary>快照請求（查目前值）；補抓請求帶 parentid 欄位，兩者以欄位區分。</summary>
    private static bool IsSnapshotUrl(string url) => url.Contains("columns=objid,lastvalue,interval,lastcheck,status,primarychannel");

    private static bool IsBackfillUrl(string url) => url.Contains("content=sensors") && url.Contains("parentid");

    private static string SafeBoundedDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<none>";
        var safe = System.Text.RegularExpressions.Regex.Replace(value, @"https?://\S+", "<url-redacted>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return safe.Length <= 240 ? safe : safe[..240];
    }

    private List<string> SnapshotUrls() { lock (_stubHandler.RequestedUrls) return _stubHandler.RequestedUrls.Where(IsSnapshotUrl).ToList(); }

    private List<string> BackfillUrls() { lock (_stubHandler.RequestedUrls) return _stubHandler.RequestedUrls.Where(IsBackfillUrl).ToList(); }

    private static List<long> FilterObjids(string url) =>
        System.Text.RegularExpressions.Regex.Matches(url, @"filter_objid=(\d+)")
            .Select(m => long.Parse(m.Groups[1].Value))
            .ToList();

    /// <summary>分批請求照 filter_objid 回對應的感測器值（可指定要漏掉的 objid）。</summary>
    private static HttpResponseMessage FilteredValues(string url, ISet<long>? omit = null)
    {
        var ids = FilterObjids(url).Where(id => omit == null || !omit.Contains(id)).ToList();
        var lastCheck = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var rows = string.Join(",", ids.Select(id => $"{{\"objid\":{id},\"lastvalue_raw\":1,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"{lastCheck}\"}}"));
        return JsonResponse($"{{\"treesize\":{ids.Count},\"sensors\":[{rows}]}}");
    }

    private static HttpResponseMessage CapacityValidFilteredValues(string url)
    {
        var ids = FilterObjids(url);
        var lastCheck = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var rows = string.Join(",", ids.Select(id => $"{{\"objid\":{id},\"lastvalue_raw\":1,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"{lastCheck}\"}}"));
        return JsonResponse($"{{\"treesize\":{ids.Count},\"sensors\":[{rows}]}}");
    }

    /// <summary>補抓的逐台回應：指定裝置回一顆感測器，其他裝置回空。</summary>
    private static HttpResponseMessage DeviceSensors(string url, IReadOnlyDictionary<long, long> sensorByDevice)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]id=(\d+)");
        var deviceId = long.Parse(m.Groups[1].Value);
        if (url.Contains("start=0") && sensorByDevice.TryGetValue(deviceId, out var sensorId))
        {
            return JsonResponse($"{{\"treesize\":1,\"sensors\":[{{\"objid\":{sensorId},\"parentid\":{deviceId},\"sensor\":\"S\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false}}]}}");
        }
        return JsonResponse("{\"treesize\":0,\"sensors\":[]}");
    }

    private void SetupOkDevices(IEnumerable<long> deviceObjids)
    {
        var today = DateTime.Today;
        _backend.PrtgStore().ReplaceHostMapForDate(today, deviceObjids.Select((id, i) =>
        {
            // An OK map must reference a real host. Preserve an existing inactive host
            // so this helper cannot accidentally bypass the runtime eligibility guard.
            var host = _hostStore.Get(i + 1) ?? _hostStore.Upsert(new WebHost
            {
                HostName = $"Server-{id}", IpAddress = $"192.168.{id / 250}.{id % 250 + 1}", Active = true
            });
            Assert.Equal(i + 1, host.HostId);
            return new PrtgHostMapRow
            {
                DeviceObjid = id,
                HostId = host.HostId,
                HostName = host.HostName,
                Ip = $"192.168.{id / 250}.{id % 250 + 1}",
                MapStatus = PrtgMapStatus.Ok,
                MapDate = today
            };
        }).ToArray());
    }

    [Fact]
    public async Task 分批快照_目標120顆_恰3個filter請求且無全站查詢()
    {
        SetupTargetSensors(ManyTargets(1001, 120));
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));

        var service = CreateService();
        await service.TickAsync();

        var snapshots = SnapshotUrls();
        Assert.Equal(2, snapshots.Count);
        Assert.All(snapshots, u => Assert.Contains("filter_objid=", u));
        Assert.All(snapshots, u => Assert.Contains("count=", u));
        Assert.All(snapshots, u => Assert.True(System.Text.Encoding.UTF8.GetByteCount(new Uri(u).PathAndQuery.TrimStart('/')) <= 4096));
        Assert.DoesNotContain(_stubHandler.RequestedUrls, u => u.Contains("count=50000"));
        // Sorted 100-ID batches with one extra response row available to detect ignored filters.
        // Three concurrent requests may arrive in a different order. Assert exact
        // batches and target coverage without treating arrival order as batch order.
        var batches = snapshots.Select(FilterObjids).OrderBy(ids => ids.First()).ToArray();
        Assert.Equal(Enumerable.Range(1001, 100).Select(i => (long)i), batches[0]);
        Assert.Equal(Enumerable.Range(1101, 20).Select(i => (long)i), batches[1]);
        Assert.Equal(120, service.GetStatus().PendingSamples);
        Assert.Equal(120, service.GetStatus().LastSensorCount);
    }

    [Fact]
    public async Task 分批快照_三批同時等待且第四批僅在名額釋放後開始()
    {
        SetupTargetSensors(ManyTargets(1001, 350));
        var threeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var inFlight = 0;
        var maximumInFlight = 0;
        _stubHandler.OnSend = async (request, token) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            int observed;
            do { observed = maximumInFlight; }
            while (current > observed && Interlocked.CompareExchange(ref maximumInFlight, current, observed) != observed);
            if (Interlocked.Increment(ref started) == 3) threeStarted.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                return FilteredValues(request.RequestUri!.ToString());
            }
            finally { Interlocked.Decrement(ref inFlight); }
        };
        var service = CreateService();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var tick = service.TickAsync(deadline.Token);
        try
        {
            await threeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, started);
            Assert.Equal(3, inFlight);
            Assert.False(tick.IsCompleted);
            release.TrySetResult();
            await tick.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            if (!tick.IsCompleted) deadline.Cancel();
            try { await tick.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
        Assert.Equal(4, started);
        Assert.Equal(3, maximumInFlight);
        Assert.Equal(0, inFlight);
        Assert.Equal(350, service.GetStatus().PendingSamples);
        Assert.Equal(350, service.GetStatus().LastSensorCount);
    }

    [Fact]
    public async Task 分批快照_取消會等待三個在途請求退出且不開始剩餘批次()
    {
        SetupTargetSensors(ManyTargets(1001, 350));
        var threeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var exited = 0;
        _stubHandler.OnSend = async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 3) threeStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Increment(ref exited); }
            throw new InvalidOperationException("Cancelled request unexpectedly continued.");
        };
        var service = CreateService();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var tick = service.TickAsync(cancellation.Token);
        try
        {
            await threeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _sqlDiagnostics.Reset();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await tick.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancellation.Cancel();
            try { await tick.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        Assert.Equal(3, started);
        Assert.Equal(3, exited);
        Assert.Equal(0, service.GetStatus().PendingSamples);
        Assert.Equal(0, _sqlDiagnostics.SensorSelectCount);
        var evidence = new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Read();
        Assert.NotEmpty(evidence);
        Assert.All(evidence, sample =>
        {
            Assert.Equal("failed", sample.Outcome);
            Assert.Equal(100, sample.RequestedSensorCount);
        });
    }

    [Fact]
    public async Task 分批快照_超過單批512KiB拒絕解析且其餘批次繼續保存()
    {
        SetupTargetSensors(ManyTargets(1001, 150));
        _stubHandler.OnSend = (request, _) =>
        {
            var url = request.RequestUri!.ToString();
            return Task.FromResult(FilterObjids(url).Contains(1001)
                ? JsonResponse(new string(' ', PrtgSnapshotHostedService.MaximumSnapshotBatchResponseBytes + 1))
                : FilteredValues(url));
        };
        var console = new TestConsole();
        var service = CreateService(console);
        await service.TickAsync();
        Assert.Equal(2, SnapshotUrls().Count);
        Assert.Equal(50, service.GetStatus().PendingSamples);
        Assert.Equal(50, service.GetStatus().LastSensorCount);
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
        Assert.Contains(console.Lines, line => line.Contains("1 批查詢失敗"));
    }

    [Fact]
    public async Task 分批快照_目標2001顆_恰41個filter請求且無全站查詢()
    {
        SetupTargetSensors(ManyTargets(1001, 2001));
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));

        var service = CreateService();
        await service.TickAsync();

        var snapshots = SnapshotUrls();
        Assert.Equal(21, snapshots.Count);
        Assert.All(snapshots, u => Assert.Contains("filter_objid=", u));
        Assert.All(snapshots, u => Assert.Contains($"count={FilterObjids(u).Count + 1}", u));
        Assert.All(snapshots, u => Assert.True(System.Text.Encoding.UTF8.GetByteCount(new Uri(u).PathAndQuery.TrimStart('/')) <= 4096));
        Assert.DoesNotContain(_stubHandler.RequestedUrls, u => u.Contains("count=50000"));
        var batches = snapshots.Select(FilterObjids).OrderBy(ids => ids.First()).ToArray();
        for (var index = 0; index < batches.Length; index++)
            Assert.Equal(Enumerable.Range(1001 + index * 100, Math.Min(100, 2001 - index * 100))
                .Select(id => (long)id), batches[index]);
        Assert.Equal(2001, service.GetStatus().PendingSamples);
        Assert.Equal(2001, service.GetStatus().LastSensorCount);
    }

    [Fact]
    public async Task 大範圍容量未知時只執行一批有界恢復並保留既有累積列()
    {
        SetupTargetSensors(ManyTargets(1001, 120));
        _backend.Blob(PrtgSnapshotCapacityStore.BlobKey).Mutate(_ => ("[]", true));
        _stubHandler.OnSend = (request, _) => Task.FromResult(CapacityValidFilteredValues(request.RequestUri!.ToString()));
        var service = CreateService();
        var hour = DateTime.Today.AddHours(10);
        service.Now = () => hour.AddMinutes(5);
        service.Accumulator.Add(90001, hour, 8, 100);

        await service.TickAsync();

        var recoveryUrl = Assert.Single(SnapshotUrls());
        Assert.Equal(100, FilterObjids(recoveryUrl).Count);
        Assert.NotNull(service.GetStatus().LastSuccessAt);
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
        Assert.Equal(101, service.GetStatus().PendingSamples);
        Assert.Contains("容量證據恢復中", service.GetStatus().LastSkipReason);
    }

    [Fact]
    public async Task 大範圍容量已驗證時執行實際快照請求()
    {
        SetupTargetSensors(ManyTargets(1001, 120));
        _stubHandler.OnSend = (request, _) => Task.FromResult(CapacityValidFilteredValues(request.RequestUri!.ToString()));
        var service = CreateService();

        await service.TickAsync();

        Assert.Equal(2, SnapshotUrls().Count);
        Assert.NotNull(service.GetStatus().LastSuccessAt);
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
        Assert.Equal(120, service.GetStatus().LastSensorCount);
    }

    [Fact]
    public async Task 同一來源計畫更新後重用快照Client時刷新SharedAdmissionFingerprint()
    {
        PrtgRequestBudget.Shared.ClearAdmissionPlan();
        try
        {
            _settingsStore.Update(settings => settings.PrtgSensorTypeWhitelist = new List<string> { "Ping" });
            // 本例驗 cached client 的同來源計畫換版；大量資源容量另由完整矩陣測試。
            SetupTargetSensors(Enumerable.Range(1, 1)
                .Select(id => ((long)id, id == 1 ? "Ping" : "Other")));
            SeedSyntheticCapacityEvidence(1_000, profileElapsedMilliseconds: 2_500);

            var planStore = new PrtgCapacityAdmissionPlanStore(_backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
            var firstPlan = Assert.IsType<PrtgCapacityAdmissionPlan>(planStore.ReadCurrent(DateTimeOffset.UtcNow));
            var service = CreateService();
            service.ClientFactory = () => new PrtgClient(_settingsStore.Get().PrtgUrl!, "token123", 30, true,
                _stubHandler, PrtgAuthModes.Token, "", "", "", PrtgRequestBudget.Shared);
            var clientField = typeof(PrtgSnapshotHostedService).GetField("_client",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var clock = DateTime.Today.AddHours(10).AddMinutes(5);
            service.Now = () => clock;
            _stubHandler.OnSend = (request, _) => Task.FromResult(FilteredValues(request.RequestUri!.ToString()));

            await service.TickAsync();

            Assert.Single(SnapshotUrls());
            var cachedClient = Assert.IsType<PrtgClient>(clientField.GetValue(service));
            Assert.Equal(firstPlan.Fingerprint, cachedClient.AdmissionPlanFingerprint);

            // 測量更新但配額配置不變時 fingerprint 刻意保持穩定；改採樣策略才真正改變同來源計畫。
            _settingsStore.Update(settings => settings.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive);
            SeedSyntheticCapacityEvidence(1_000, profileElapsedMilliseconds: 5_000);
            var renewedPlan = Assert.IsType<PrtgCapacityAdmissionPlan>(planStore.ReadCurrent(DateTimeOffset.UtcNow));
            Assert.Equal(firstPlan.SourceFingerprint, renewedPlan.SourceFingerprint);
            Assert.NotEqual(firstPlan.StrategyFingerprint, renewedPlan.StrategyFingerprint);
            Assert.NotEqual(firstPlan.SnapshotTableRequestsPerSecond, renewedPlan.SnapshotTableRequestsPerSecond);
            Assert.NotEqual(firstPlan.Fingerprint, renewedPlan.Fingerprint);
            Assert.True(renewedPlan.Version > firstPlan.Version);

            clock = clock.AddMinutes(15);
            await service.TickAsync();

            Assert.Equal(2, SnapshotUrls().Count);
            Assert.Same(cachedClient, clientField.GetValue(service));
            Assert.Equal(renewedPlan.Fingerprint, cachedClient.AdmissionPlanFingerprint);
            Assert.Equal(clock, service.GetStatus().LastSuccessAt);
        }
        finally
        {
            PrtgRequestBudget.Shared.ClearAdmissionPlan();
        }
    }

    [Fact]
    public async Task TickAsync_前次Admission不可授權已變更scope的補抓()
    {
        SetupTargetSensors(new[] { (101L, "Ping") });
        var service = CreateService();
        var clock = DateTime.Today.AddHours(10).AddMinutes(5);
        service.Now = () => clock;
        _stubHandler.OnSend = (request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("content=messages", StringComparison.Ordinal))
                return Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));
            return Task.FromResult(IsBackfillUrl(url)
                ? DeviceSensors(url, new Dictionary<long, long>())
                : FilteredValues(url));
        };

        await service.TickAsync();
        Assert.Single(SnapshotUrls());
        _stubHandler.RequestedUrls.Clear();

        // Keep an actually selected sensor while changing the admitted target set and
        // leaving a third mapped business device without mirror sensors for backfill.
        SetupOkDevices(new long[] { 10, 20, 30 });
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.Revision = Guid.NewGuid().ToString("N");
            policy.HostIds = new List<long> { 1, 2, 3 };
            policy.SensorIds = new List<long> { 101, 102 };
        });
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "Ping", SensorType = "ping", Status = "Up" },
            new PrtgSensorRow { Objid = 102, DeviceObjid = 20, Name = "Ping", SensorType = "ping", Status = "Up" }
        }, DateTime.Now, NewRecentStateQueueItem(10, DateTime.Today));
        _backend.PrtgStore().BindObservedResource(102, 2, "snapshot-hosted-fixture-source",
            PrtgTimelineResourceIdentity.BuildResourceFingerprint("20", "ping", "created", 0));
        // Keep the snapshot-only prerequisite valid for the changed exact selection so
        // this negative exercises joint admission, without creating a replacement plan.
        var changedSelection = PrtgSnapshotTargetResolver.Resolve(_backend, _hostStore,
            _settingsStore.Get(), Array.Empty<Sentinel>(), policyStore.Get());
        var snapshotCapacity = new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
        for (var index = 0; index < PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples; index++)
            snapshotCapacity.Record(new PrtgSnapshotCapacitySample(changedSelection.ScopeFingerprint,
                changedSelection.EndpointFingerprint, changedSelection.RequestShapeFingerprint,
                DateTimeOffset.UtcNow.AddMinutes(-5).AddMilliseconds(index), 9_000,
                changedSelection.CapacitySampleBatchSize, "success"));
        clock = clock.AddMinutes(15);

        await service.TickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Contains("Joint PRTG admission is Waiting", service.GetStatus().LastSkipReason);
        var admissionField = typeof(PrtgSnapshotHostedService).GetField("_admissionPlanFingerprint",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Null(admissionField.GetValue(service));

        // A newly published plan for the actual current scope allows work again.
        SeedCurrentMappedJointAdmission();
        clock = clock.AddMinutes(15);
        await service.TickAsync();

        Assert.NotEmpty(_stubHandler.RequestedUrls);
        Assert.NotEmpty(SnapshotUrls());
    }

    [Fact]
    public async Task 大範圍範圍補抓容量未知時保留請求並在同形樣本驗證後續行()
    {
        SetupTargetSensors(ManyTargets(1001, 120));
        _backend.Blob(PrtgSnapshotCapacityStore.BlobKey).Mutate(_ => ("[]", true));
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, NewRecentStateQueueItem(10, DateTime.Today));
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(
            "{\"treesize\":0,\"sensors\":[],\"messages\":[]}"));
        var service = CreateService();

        await service.ScopeRefreshTickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Null(service.GetStatus().LastSuccessAt);
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
        Assert.Contains("容量尚未驗證", service.GetStatus().LastSkipReason);
        Assert.Null(QueueItemForDevice(10).CompletedAtUtc);

        SeedSyntheticCapacityEvidence(9_000);
        await service.ScopeRefreshTickAsync();

        Assert.Contains(_stubHandler.RequestedUrls, url => url.Contains("content=messages", StringComparison.Ordinal));
        Assert.NotNull(QueueItemForDevice(10).CompletedAtUtc);
    }

    [Fact]
    public async Task 大範圍範圍補抓容量已知超窗時保留Queue且不啟動HTTP()
    {
        _settingsStore.Update(settings => settings.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive);
        SetupTargetSensors(ManyTargets(1001, 120));
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 1001, DeviceObjid = 10, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, NewRecentStateQueueItem(10, DateTime.Today));
        SeedSyntheticCapacityEvidence(300_000);
        _stubHandler.OnSend = (_, _) => throw new InvalidOperationException("已知超窗時範圍補抓不得啟動HTTP");
        var service = CreateService();

        await service.ScopeRefreshTickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Null(service.GetStatus().LastSuccessAt);
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
        Assert.Contains("估算超出", service.GetStatus().LastSkipReason);
        Assert.Null(QueueItemForDevice(10).CompletedAtUtc);
    }

    [Fact]
    public void 同一小時重新計算容量目標時使用目前HostMap與ActiveHosts()
    {
        SetupTargetSensors(ManyTargets(1001, 120));
        var service = CreateService();
        var resolve = typeof(PrtgSnapshotHostedService).GetMethod("ResolveSnapshotCapacitySelection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var settings = _settingsStore.Get();
        var beforeChange = Assert.IsType<PrtgSnapshotTargetSelection>(resolve.Invoke(service,
            new object[] { settings, DateTime.Now }));
        Assert.Equal(120, beforeChange.SensorObjids.Count);

        var host = _hostStore.Get(1)!;
        host.Active = false;
        _hostStore.Upsert(host);

        // Also cover mapping changes not delivered through RequestScopeRefresh: capacity scope
        // must still equal the shared resolver's current active-map result.
        var refreshed = Assert.IsType<PrtgSnapshotTargetSelection>(resolve.Invoke(service,
            new object[] { settings, DateTime.Now }));
        var shared = PrtgSnapshotTargetResolver.Resolve(_backend, _hostStore, settings,
            Array.Empty<Sentinel>());
        Assert.Empty(refreshed.SensorObjids);
        Assert.Equal(shared.SensorObjids, refreshed.SensorObjids);
        Assert.Equal(shared.ActiveMappedDeviceCount, refreshed.ActiveMappedDeviceCount);

        host.Active = true;
        _hostStore.Upsert(host);
        service.RequestScopeRefresh();
        var requestedRefresh = Assert.IsType<PrtgSnapshotTargetSelection>(resolve.Invoke(service,
            new object[] { settings, DateTime.Now }));
        Assert.Equal(beforeChange.SensorObjids, requestedRefresh.SensorObjids);
    }

    [Fact]
    public async Task 容量核准後HostMap在操作Scope捕捉前變更時拒絕舊證據且不送HTTP()
    {
        SetupTargetSensors(ManyTargets(1001, 120));
        SeedSyntheticCapacityEvidence(9_000);
        _stubHandler.OnSend = (_, _) => throw new InvalidOperationException("範圍在容量核准後改變時不得啟動舊範圍HTTP");
        var service = CreateService();
        var host = _hostStore.Get(1)!;
        service.ScopeRefreshAdmissionAccepted = () =>
        {
            host.Active = false;
            _hostStore.Upsert(host);
            service.RequestScopeRefresh();
        };

        await service.ScopeRefreshTickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Contains("容量判定後有效範圍已變更", service.GetStatus().LastSkipReason);
    }

    [Fact]
    public async Task 大範圍同形樣本已知超窗時拒絕完整快照()
    {
        _settingsStore.Update(settings => settings.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive);
        SetupTargetSensors(ManyTargets(1001, 120));
        SeedSyntheticCapacityEvidence(300_000);
        _stubHandler.OnSend = (_, _) => throw new InvalidOperationException("已知超窗時不得開啟完整快照請求");
        var service = CreateService();

        await service.TickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Null(service.GetStatus().LastSuccessAt);
        Assert.Equal(0, service.GetStatus().ConsecutiveFailures);
        Assert.Contains("估算超出", service.GetStatus().LastSkipReason);
        var diagnostic = Assert.Single(service.Diagnostics.ReadRecent(DateTime.Now.AddHours(1), 1));
        Assert.Equal(1, diagnostic.ReasonCodes["snapshot-capacity-exceeded"]);
    }

    [Fact]
    public async Task 分批快照_目標50顆_恰1個filter請求且無全站查詢()
    {
        SetupTargetSensors(ManyTargets(1001, 50));
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));

        var service = CreateService();
        await service.TickAsync();

        var snapshots = SnapshotUrls();
        var single = Assert.Single(snapshots);
        Assert.Contains("filter_objid=", single);
        Assert.DoesNotContain(_stubHandler.RequestedUrls, u => u.Contains("count=50000"));
        Assert.Equal(50, FilterObjids(single).Count);
        Assert.Equal(50, service.GetStatus().PendingSamples);
        Assert.Equal(50, service.GetStatus().LastSensorCount);
    }

    [Fact]
    public async Task 分批快照_目標2000顆_恰40個filter請求且無全站查詢()
    {
        SetupTargetSensors(ManyTargets(1001, 2000));
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));

        var service = CreateService();
        await service.TickAsync();

        var snapshots = SnapshotUrls();
        Assert.Equal(20, snapshots.Count);
        Assert.All(snapshots, u => Assert.Contains("filter_objid=", u));
        Assert.All(snapshots, u => Assert.Contains("count=101", u));
        Assert.DoesNotContain(_stubHandler.RequestedUrls, u => u.Contains("count=50000"));
        var batches = snapshots.Select(FilterObjids).OrderBy(ids => ids.First()).ToArray();
        for (var index = 0; index < batches.Length; index++)
            Assert.Equal(Enumerable.Range(1001 + index * 100, 100).Select(id => (long)id), batches[index]);
        Assert.Equal(2000, service.GetStatus().PendingSamples);
        Assert.Equal(2000, service.GetStatus().LastSensorCount);
    }

    [Fact]
    public async Task 分批快照_目標0顆_不發請求且照記成功()
    {
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{}"));

        var service = CreateService();
        await service.TickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.NotNull(service.GetStatus().LastSuccessAt);
        Assert.Equal(0, service.GetStatus().LastSensorCount);
        Assert.Contains("有效快照範圍為空", service.GetStatus().LastSkipReason);
    }

    [Fact]
    public async Task 分批快照_零有效sensor但映射裝置有待處理queue_只整理本機狀態()
    {
        SetupOkDevices(new long[] { 10, 20 });
        _settingsStore.Update(settings => settings.PrtgSensorTypeWhitelist = new List<string> { "Ping" });
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(
            new[] { new PrtgSensorRow { Objid = 901, DeviceObjid = 10, Name = "Other", SensorType = "other", Status = "Up" } },
            DateTime.Now, NewRecentStateQueueItem(20, DateTime.Today));
        _stubHandler.OnSend = (_, _) => throw new InvalidOperationException("空有效範圍不得啟動補抓或狀態 HTTP");
        var service = CreateService();

        await service.TickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.NotNull(service.GetStatus().LastSuccessAt);
        Assert.Equal(0, service.GetStatus().LastSensorCount);
        Assert.Contains("有效快照範圍為空", service.GetStatus().LastSkipReason);
        var pending = _backend.PrtgStore().ReadRecentStateChangeQueue()
            .OrderBy(item => item.DeviceObjid).ToArray();
        Assert.Equal(new long[] { 10, 20 }, pending.Select(item => item.DeviceObjid));
        Assert.All(pending, item => Assert.Null(item.CompletedAtUtc));
    }

    [Fact]
    public async Task 分批快照_缺少要求sensor時拒絕整批且不寫部分資料()
    {
        SetupTargetSensors(ManyTargets(1001, 50));
        var omit = new HashSet<long> { 1010, 1020 };
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString(), omit));

        var service = CreateService();
        await service.TickAsync();

        Assert.Equal(1, service.GetStatus().ConsecutiveFailures);
        Assert.Equal(0, service.GetStatus().PendingSamples);
    }

    [Fact]
    public async Task 分批快照_取齊時不出缺顆警告()
    {
        SetupTargetSensors(ManyTargets(1001, 50));
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));

        var service = CreateService();
        await service.TickAsync();

        Assert.DoesNotContain(service.ExecutionOutputs, l => l.Contains("缺"));
    }

    [Fact]
    public async Task 分批快照_兩批中一批500_其餘照累積且不進退避()
    {
        SetupTargetSensors(ManyTargets(1001, 150));
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            return Task.FromResult(FilterObjids(url).Contains(1051)
                ? JsonResponse("{}", HttpStatusCode.InternalServerError)
                : FilteredValues(url));
        };

        var service = CreateService();
        await service.TickAsync();

        Assert.Equal(2, SnapshotUrls().Count);
        Assert.Contains(service.ExecutionOutputs, l => l.Contains("1 批查詢失敗"));
        // 失敗批次不重複算成「缺」
        Assert.DoesNotContain(service.ExecutionOutputs, l => l.Contains("缺"));
        var status = service.GetStatus();
        Assert.Equal(50, status.PendingSamples);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.NotNull(status.LastSuccessAt);
    }

    [Fact]
    public async Task 分批快照_兩批全500清除容量證據_後續等待容量驗證()
    {
        SetupTargetSensors(ManyTargets(1001, 150));
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{}", HttpStatusCode.InternalServerError));

        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        await service.TickAsync();
        Assert.Equal(2, SnapshotUrls().Count);
        Assert.Equal(1, service.GetStatus().ConsecutiveFailures);
        Assert.Null(service.GetStatus().LastSuccessAt);

        clock = clock.AddMinutes(15);
        await service.TickAsync();
        clock = clock.AddMinutes(15);
        await service.TickAsync();
        var status = service.GetStatus();
        Assert.Equal(4, SnapshotUrls().Count); // Each later tick is limited to one bounded recovery batch.
        Assert.Equal(3, status.ConsecutiveFailures); // Recovery attempts are real requests and failures.
        Assert.Equal(30, status.IntervalMinutes);
    }

    [Fact]
    public void 組filter查詢字串_共用方法格式()
    {
        Assert.Equal("&filter_objid=3&filter_objid=12", PrtgResourceGuardProbe.BuildObjidFilter(new long[] { 3, 12 }));
        Assert.Equal("", PrtgResourceGuardProbe.BuildObjidFilter(Array.Empty<long>()));
    }

    [Fact]
    public async Task 範圍補抓_鏡像無感測器的裝置_恰對它發一個請求並寫入鏡像()
    {
        SetupOkDevices(new long[] { 10, 20 });
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now);
        SeedCurrentMappedJointAdmission();
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (IsBackfillUrl(url)) return Task.FromResult(DeviceSensors(url, new Dictionary<long, long> { [20] = 201 }));
            return Task.FromResult(FilteredValues(url));
        };

        var service = CreateService();
        await service.TickAsync();

        var backfill = Assert.Single(BackfillUrls());
        Assert.Matches(@"[?&]id=20(&|$)", backfill);
        var sensor = Assert.Single(_backend.PrtgStore().GetAllSensors(), s => s.Objid == 201);
        Assert.Equal(20, sensor.DeviceObjid);
        Assert.Contains(service.ExecutionOutputs, l => l.Contains("已為 1 台新進取數範圍的裝置補上 1 個感測器"));
        // 補抓不影響快照本身
        Assert.NotNull(service.GetStatus().LastSuccessAt);
    }

    [Fact]
    public async Task 範圍補抓_回0顆的裝置下一輪不再請求_結構同步寫過鏡像後再試()
    {
        SetupOkDevices(new long[] { 10, 20 });
        var store = _backend.PrtgStore();
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now.AddMinutes(-30));
        SeedCurrentMappedJointAdmission();
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (IsBackfillUrl(url)) return Task.FromResult(DeviceSensors(url, new Dictionary<long, long>()));
            return Task.FromResult(FilteredValues(url));
        };

        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        await service.TickAsync();
        var first = BackfillUrls().Count;
        Assert.True(first >= 1);
        Assert.All(BackfillUrls(), u => Assert.Matches(@"[?&]id=20(&|$)", u));
        Assert.DoesNotContain(service.ExecutionOutputs, l => l.Contains("補上"));

        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Equal(first, BackfillUrls().Count);

        // 結構同步寫過鏡像（裝置表 synced_at 最大值變新）→ 「已確認為空」清空，再試一次
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 10, Name = "D" }
        }, DateTime.Now.AddMinutes(5));
        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Equal(first * 2, BackfillUrls().Count);
    }

    [Fact]
    public async Task 範圍補抓_補抓自己寫入的列不觸發清空已確認為空()
    {
        // 20 補到感測器（推高 synced_at）、30 確認為空；下一輪不能因為 synced_at 變新就重打 30
        SetupOkDevices(new long[] { 10, 20, 30 });
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now.AddMinutes(-30));
        SeedCurrentMappedJointAdmission();
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (IsBackfillUrl(url)) return Task.FromResult(DeviceSensors(url, new Dictionary<long, long> { [20] = 201 }));
            return Task.FromResult(FilteredValues(url));
        };

        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        await service.TickAsync();
        var first = BackfillUrls().Count;
        Assert.Contains(BackfillUrls(), u => System.Text.RegularExpressions.Regex.IsMatch(u, @"[?&]id=30(&|$)"));

        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Equal(first, BackfillUrls().Count);
    }

    [Fact]
    public async Task 範圍補抓_結構同步執行中_零補抓請求()
    {
        SetupOkDevices(new long[] { 20 });
        _stubHandler.OnSend = (req, _) => Task.FromResult(DeviceSensors(req.RequestUri!.ToString(), new Dictionary<long, long>()));

        var service = CreateService();
        Assert.True(_syncState.TryBegin());
        await service.TickAsync();

        Assert.Empty(BackfillUrls());
    }

    [Fact]
    public async Task 範圍補抓_環境探測執行中_零補抓請求但快照照常()
    {
        SetupOkDevices(new long[] { 10, 20 });
        SeedCurrentMappedJointAdmission(10);
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (IsBackfillUrl(url)) return Task.FromResult(DeviceSensors(url, new Dictionary<long, long> { [10] = 101, [20] = 201 }));
            return Task.FromResult(FilteredValues(url));
        };
        Assert.True(_probeState.TryBegin());

        var service = CreateService();
        await service.TickAsync();

        Assert.Empty(BackfillUrls());

        _probeState.EndRun(true);
        service.Now = () => DateTime.Now.AddMinutes(20);
        await service.TickAsync();
        Assert.NotEmpty(BackfillUrls());
    }

    [Fact]
    public async Task 分批快照_PRTG忽略filter且回傳foreign_sentinel時_整批拒絕且不發布()
    {
        SetupTargetSensors(ManyTargets(1001, 120));
        // 不論 filter 帶什麼，一律回全部 120 顆；每個 request 只容許預期 ID 加一個 sentinel。
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (IsBackfillUrl(url)) return Task.FromResult(DeviceSensors(url, new Dictionary<long, long>()));
            var rows = string.Join(",", Enumerable.Range(0, 120).Select(i => $"{{\"objid\":{1001 + i},\"lastvalue_raw\":1,\"interval\":\"60 s\",\"status\":\"Up\",\"lastcheck\":\"__FIXTURE_NOW__\"}}"));
            return Task.FromResult(JsonResponse($"{{\"treesize\":120,\"sensors\":[{rows}]}}"));
        };

        var service = CreateService();
        await service.TickAsync();

        Assert.Equal(2, SnapshotUrls().Count);
        Assert.Equal(0, service.GetStatus().PendingSamples);
        Assert.Equal(0, service.GetStatus().LastSensorCount);
        Assert.Equal(1, service.GetStatus().ConsecutiveFailures);
    }

    [Fact]
    public async Task 範圍補抓_待補60台_本輪恰50個補抓請求()
    {
        var devices = Enumerable.Range(1, 60).Select(i => (long)(5000 + i)).ToList();
        SetupOkDevices(devices);
        SeedCurrentMappedJointAdmission(5_060);
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (IsBackfillUrl(url)) return Task.FromResult(DeviceSensors(url, new Dictionary<long, long>()));
            return Task.FromResult(FilteredValues(url));
        };

        var service = CreateService();
        await service.TickAsync();

        var urls = BackfillUrls();
        Assert.Equal(PrtgSnapshotHostedService.MaxBackfillDevicesPerTick, urls.Count);
        // 由小到大取前 50 台
        Assert.DoesNotContain(urls, u => System.Text.RegularExpressions.Regex.IsMatch(u, @"[?&]id=5051(&|$)"));
        Assert.Contains(urls, u => System.Text.RegularExpressions.Regex.IsMatch(u, @"[?&]id=5050(&|$)"));
    }

    // ── 守門覆寫清單的感測器不在鏡像：查所在裝置後補抓 ──

    /// <summary>parentid 查詢：只要 objid,parentid 兩欄且帶 filter_objid（逐台補抓的欄位清單同樣以 objid,parentid 開頭，要排除）。</summary>
    private static bool IsParentLookupUrl(string url) =>
        url.Contains("columns=objid,parentid&") && url.Contains("filter_objid=");

    private List<string> ParentLookupUrls() { lock (_stubHandler.RequestedUrls) return _stubHandler.RequestedUrls.Where(IsParentLookupUrl).ToList(); }

    /// <summary>逐台補抓請求（排除 parentid 查詢本身）。</summary>
    private List<string> DeviceBackfillUrls() { lock (_stubHandler.RequestedUrls) return _stubHandler.RequestedUrls.Where(u => IsBackfillUrl(u) && !IsParentLookupUrl(u)).ToList(); }

    /// <summary>範圍內只有裝置 10、且鏡像已有它的感測器 101：範圍本身沒有待補，只剩覆寫清單這條路。</summary>
    private void SetupScopeWithoutPending(params string[] overrideObjids)
    {
        SetupOkDevices(new long[] { 10 });
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now.AddMinutes(-30));
        _settingsStore.Update(s => s.PrtgResourceGuardSensorObjids = overrideObjids.ToList());
        SeedCurrentMappedJointAdmission();
    }

    [Fact]
    public async Task ScopeBackfillUsesResidualLaneWithoutSpendingTheNextSnapshotSlot()
    {
        SetupScopeWithoutPending("9001");
        var service = CreateService();
        var purposes = new List<(string Url, PrtgRequestPurpose Purpose)>();
        var clientField = typeof(PrtgSnapshotHostedService).GetField("_client",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        _stubHandler.OnSend = (request, _) =>
        {
            var url = request.RequestUri!.ToString();
            purposes.Add((url, Assert.IsType<PrtgClient>(clientField.GetValue(service)).RequestPurpose));
            if (IsParentLookupUrl(url)) return Task.FromResult(JsonResponse(
                "{\"treesize\":1,\"sensors\":[{\"objid\":9001,\"parentid\":77}]}"));
            if (IsBackfillUrl(url)) return Task.FromResult(JsonResponse("{\"treesize\":0,\"sensors\":[]}"));
            return Task.FromResult(FilteredValues(url));
        };
        await service.TickAsync();
        Assert.Single(purposes.Where(p => IsSnapshotUrl(p.Url)));
        Assert.All(purposes.Where(p => IsSnapshotUrl(p.Url)), p => Assert.Equal(PrtgRequestPurpose.Snapshot, p.Purpose));
        var backfill = purposes.Where(p => IsParentLookupUrl(p.Url) || IsBackfillUrl(p.Url)).ToArray();
        Assert.True(backfill.Length >= 2);
        Assert.All(backfill, p => Assert.Equal(PrtgRequestPurpose.General, p.Purpose));
        Assert.True(Assert.Single(_fixtureBudgets).Clock.Elapsed < TimeSpan.FromSeconds(30),
            "Bounded scope work must use residual quota instead of spending several minutes of reserved snapshot cadence.");
    }

    [Fact]
    public async Task RecentMessagesUseResidualLaneAndTheReusedClientReturnsToSnapshotPurpose()
    {
        const long deviceId = 702;
        SeedManualBusinessDevice(deviceId);
        SetupOkDevices([10]);
        SeedCurrentMappedJointAdmission();
        var today = DateTime.Today;
        SeedCompletedMappedQueueRows(today);
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(
            [new PrtgSensorRow { Objid = 703, DeviceObjid = deviceId, Name = "Ping", SensorType = "ping" }],
            DateTime.Now, NewRecentStateQueueItem(deviceId, today));
        var service = CreateService();
        service.Now = () => today.AddHours(12);
        var purposes = new List<PrtgRequestPurpose>();
        var snapshotPurposes = new List<PrtgRequestPurpose>();
        var clientField = typeof(PrtgSnapshotHostedService).GetField("_client",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        _stubHandler.OnSend = (request, _) =>
        {
            if (request.RequestUri!.ToString().Contains("columns=" + PrtgSnapshotTargetResolver.SnapshotColumns, StringComparison.Ordinal))
                snapshotPurposes.Add(Assert.IsType<PrtgClient>(clientField.GetValue(service)).RequestPurpose);
            if (request.RequestUri!.Query.Contains("content=messages", StringComparison.Ordinal))
                purposes.Add(Assert.IsType<PrtgClient>(clientField.GetValue(service)).RequestPurpose);
            return Task.FromResult(JsonResponse("{\"treesize\":0,\"sensors\":[],\"messages\":[]}"));
        };
        await service.ScopeRefreshTickAsync();
        Assert.NotEmpty(purposes);
        Assert.All(purposes, purpose => Assert.Equal(PrtgRequestPurpose.General, purpose));
        Assert.NotNull(QueueItemForDevice(deviceId).CompletedAtUtc);
        Assert.True(Assert.Single(_fixtureBudgets).Clock.Elapsed < TimeSpan.FromSeconds(30));
        await service.TickAsync();
        Assert.NotEmpty(snapshotPurposes);
        Assert.All(snapshotPurposes, purpose => Assert.Equal(PrtgRequestPurpose.Snapshot, purpose));
    }

    [Fact]
    public async Task 覆寫補抓_鏡像沒有的覆寫感測器_查到所在裝置後補抓並進入範圍()
    {
        SetupScopeWithoutPending("9001");
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (IsParentLookupUrl(url))
            {
                return Task.FromResult(FilterObjids(url).Contains(9001)
                    ? JsonResponse("{\"treesize\":1,\"sensors\":[{\"objid\":9001,\"parentid\":77}]}")
                    : JsonResponse("{\"treesize\":0,\"sensors\":[]}"));
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(url, @"[?&]id=77(&|$)"))
            {
                return Task.FromResult(url.Contains("start=0")
                    ? JsonResponse("{\"treesize\":2,\"sensors\":[" +
                                   "{\"objid\":9001,\"parentid\":77,\"sensor\":\"Core Health\",\"type\":\"corehealth\",\"status\":\"Up\",\"paused\":false}," +
                                   "{\"objid\":9002,\"parentid\":77,\"sensor\":\"CPU\",\"type\":\"cpu\",\"status\":\"Up\",\"paused\":false}]}")
                    : JsonResponse("{\"treesize\":2,\"sensors\":[]}"));
            }
            return Task.FromResult(FilteredValues(url));
        };

        var service = CreateService();
        await service.TickAsync();

        var lookup = Assert.Single(ParentLookupUrls());
        Assert.Equal(new long[] { 9001 }, FilterObjids(lookup));
        Assert.Contains(DeviceBackfillUrls(), u => System.Text.RegularExpressions.Regex.IsMatch(u, @"[?&]id=77(&|$)"));
        var store = _backend.PrtgStore();
        var sensor = Assert.Single(store.GetAllSensors(), s => s.Objid == 9001);
        Assert.Equal(77, sensor.DeviceObjid);
        Assert.DoesNotContain(store.ReadRecentStateChangeQueue(), item => item.DeviceObjid == 77);

        var settings = _settingsStore.Get();
        var scope = PrtgScopeDevices.Compute(store, _hostStore, new PrtgMirrorGuardSource(store), settings,
            Array.Empty<Sentinel>(), new TestConsole(), new PrtgAddressResolver(), hostIds: null);
        // 本測試未啟用資源守門：守門裝置不入監看，但在清除時的保留集合裡
        Assert.Contains(77L, scope.PreserveDeviceObjids);
    }

    [Fact]
    public async Task 覆寫補抓_待補超過上限時覆寫裝置優先()
    {
        // 範圍待補 60 台（objid 5001～5060），覆寫感測器所在裝置 99999 objid 最大：由小到大截 50 台時它會被擠掉，必須排在最前面
        var devices = Enumerable.Range(1, 60).Select(i => (long)(5000 + i)).ToList();
        SetupOkDevices(devices);
        _settingsStore.Update(s => s.PrtgResourceGuardSensorObjids = new List<string> { "9001" });
        SeedCurrentMappedJointAdmission(5_060);
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (IsParentLookupUrl(url))
                return Task.FromResult(JsonResponse("{\"treesize\":1,\"sensors\":[{\"objid\":9001,\"parentid\":99999}]}"));
            if (IsBackfillUrl(url)) return Task.FromResult(DeviceSensors(url, new Dictionary<long, long>()));
            return Task.FromResult(FilteredValues(url));
        };

        var service = CreateService();
        await service.TickAsync();

        var urls = DeviceBackfillUrls();
        Assert.Equal(PrtgSnapshotHostedService.MaxBackfillDevicesPerTick, urls.Count);
        Assert.Contains(urls, u => System.Text.RegularExpressions.Regex.IsMatch(u, @"[?&]id=99999(&|$)"));
        Assert.DoesNotContain(urls, u => System.Text.RegularExpressions.Regex.IsMatch(u, @"[?&]id=5050(&|$)"));
    }

    [Fact]
    public async Task 覆寫補抓_覆寫感測器已在鏡像_零個parentid請求()
    {
        SetupScopeWithoutPending("101");
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));

        var service = CreateService();
        await service.TickAsync();

        Assert.Empty(ParentLookupUrls());
        Assert.Empty(DeviceBackfillUrls());
    }

    [Fact]
    public async Task 覆寫補抓_查不到的objid下一輪不再查_結構同步後再查()
    {
        SetupScopeWithoutPending("9001");
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (IsParentLookupUrl(url)) return Task.FromResult(JsonResponse("{\"treesize\":0,\"sensors\":[]}"));
            return Task.FromResult(FilteredValues(url));
        };

        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        await service.TickAsync();
        Assert.Single(ParentLookupUrls());
        Assert.Empty(DeviceBackfillUrls());

        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Single(ParentLookupUrls());

        // 結構同步寫過鏡像（裝置表 synced_at 最大值變新）→ 「已查過找不到」清空，再查一次
        _backend.PrtgStore().UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 10, Name = "D" }
        }, DateTime.Now.AddMinutes(5));
        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Equal(2, ParentLookupUrls().Count);
    }

    [Fact]
    public async Task 範圍補抓_擲例外_快照照常完成且退避計數不變()
    {
        SetupOkDevices(new long[] { 10, 20 });
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now);
        SeedCurrentMappedJointAdmission();
        _stubHandler.OnSend = (req, _) => IsBackfillUrl(req.RequestUri!.ToString())
            ? throw new InvalidOperationException("模擬補抓失敗")
            : Task.FromResult(FilteredValues(req.RequestUri!.ToString()));

        var service = CreateService();
        // 快照與補抓重用同一 client；在真正的補抓 HTTP 邊界失敗，驗採樣及退避不受影響。
        var calls = 0;
        service.ClientFactory = () =>
        {
            Interlocked.Increment(ref calls);
            return new PrtgClient("https://prtg.example.com", "token123", 30, true, _stubHandler, PrtgAuthModes.Token, "", "", "", CreateFixtureBudget());
        };

        await service.TickAsync();

        Assert.Equal(1, calls);
        Assert.Single(DeviceBackfillUrls());
        Assert.Contains(service.ExecutionOutputs, l => l.Contains("感測器補抓失敗"));
        var status = service.GetStatus();
        Assert.NotNull(status.LastSuccessAt);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Single(SnapshotUrls());
    }
}
