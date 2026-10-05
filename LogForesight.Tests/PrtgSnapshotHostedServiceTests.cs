using System.Net;
using System.Globalization;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 數值快照背景服務單元測試（docs/PRTG-SPEC.md §3b）。
/// </summary>
public class PrtgSnapshotHostedServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly List<PrtgSnapshotHostedService> _services = [];
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

    public PrtgSnapshotHostedServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
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

        service.ClientFactory = () => new PrtgClient("https://prtg.example.com", "token123", 30, true, _stubHandler, PrtgAuthModes.Token, "", "", "");
        if (console != null)
        {
            service.Console = console;
        }
        _services.Add(service);
        return service;
    }

    private static bool RestoreJournal(PrtgSnapshotHostedService service, SystemSettings settings) =>
        (bool)typeof(PrtgSnapshotHostedService)
            .GetMethod("RestoreJournal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(service, new object[] { settings })!;

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

    private WebHost SeedManualBusinessDevice(long deviceObjid)
    {
        var host = _hostStore.Upsert(new WebHost { HostName = $"queue-host-{deviceObjid}", Active = true });
        _backend.PrtgStore().UpsertManualMap(new PrtgManualMapRow { DeviceObjid = deviceObjid, HostId = host.HostId });
        return host;
    }


    [Fact]
    public async Task 快照未到整點強制重建服務_恢復累積樣本且停用後仍可寫回()
    {
        SetupTargetSensors(new[] { (501L, "Ping") });
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(
            "{\"sensors\":[{\"objid\":501,\"lastvalue_raw\":10,\"interval\":\"60 s\"}]}"));
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
        var today = DateTime.Today;
        var sensor = new PrtgSensorRow { Objid = 703, DeviceObjid = deviceId, Name = "Ping", SensorType = "ping" };
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[] { sensor }, DateTime.Now,
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
        var queued = Assert.Single(_backend.PrtgStore().ReadRecentStateChangeQueue());
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
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 731, DeviceObjid = deviceId, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, NewRecentStateQueueItem(deviceId, DateTime.Today));
        host.Active = false;
        _hostStore.Upsert(host);
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));

        await CreateService().ScopeRefreshTickAsync();

        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Empty(_backend.PrtgStore().ReadRecentStateChangeQueue());
        var stopped = Assert.IsType<PrtgRecentStateChangeQueueStopSummary>(
            _backend.PrtgStore().ReadRecentStateChangeQueueStopSummary());
        Assert.Equal("business-scope-removed", stopped.Reason);
        Assert.Equal(1, stopped.StoppedCount);

        host.Active = true;
        _hostStore.Upsert(host);
        var service = CreateService();
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));
        await service.ScopeRefreshTickAsync(); // Rebuilds a new row from the still-present sensor mirror and defers HTTP.
        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Null(Assert.Single(_backend.PrtgStore().ReadRecentStateChangeQueue()).CompletedAtUtc);
        await service.ScopeRefreshTickAsync();
        Assert.Contains(_stubHandler.RequestedUrls, url => url.Contains("content=messages", StringComparison.Ordinal));
        Assert.NotNull(Assert.Single(_backend.PrtgStore().ReadRecentStateChangeQueue()).CompletedAtUtc);
    }

    [Fact]
    public async Task 來源切換前已有排隊項_該pass不發HTTP_重新識別後下一pass才重建()
    {
        const long deviceId = 750;
        SeedManualBusinessDevice(deviceId);
        _backend.PrtgStore().UpsertSensorsAndEnqueueRecentStateChanges(new[]
        {
            new PrtgSensorRow { Objid = 751, DeviceObjid = deviceId, Name = "Ping", SensorType = "ping" }
        }, DateTime.Now, NewRecentStateQueueItem(deviceId, DateTime.Today));
        _settingsStore.Update(s => s.PrtgUrl = "https://new-source.example.com");
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{\"treesize\":0,\"messages\":[]}"));
        var service = CreateService();
        service.ClientFactory = () => new PrtgClient(_settingsStore.Get().PrtgUrl!, "token123", 30, true,
            _stubHandler, PrtgAuthModes.Token, "", "", "");

        await service.ScopeRefreshTickAsync();
        Assert.Empty(_stubHandler.RequestedUrls);
        Assert.Null(Assert.Single(_backend.PrtgStore().ReadRecentStateChangeQueue()).CompletedAtUtc);

        await service.ScopeRefreshTickAsync();
        Assert.Contains(_stubHandler.RequestedUrls, url => url.Contains("content=messages", StringComparison.Ordinal));
        Assert.Contains(_stubHandler.RequestedUrls, url => url.StartsWith("https://new-source.example.com", StringComparison.Ordinal));
        Assert.NotNull(Assert.Single(_backend.PrtgStore().ReadRecentStateChangeQueue()).CompletedAtUtc);
    }

    [Fact]
    public async Task 來源在狀態HTTP期間改變_回應後版本checkpoint取消且Queue保留()
    {
        const long deviceId = 740;
        SeedManualBusinessDevice(deviceId);
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
        var row = Assert.Single(_backend.PrtgStore().ReadRecentStateChangeQueue());
        Assert.Null(row.CompletedAtUtc);
        Assert.Null(row.LeaseOwner);
    }

    [Fact]
    public async Task 業務範圍在狀態HTTP期間移除_回應後版本checkpoint取消且Queue保留()
    {
        const long deviceId = 760;
        var host = SeedManualBusinessDevice(deviceId);
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
        var row = Assert.Single(_backend.PrtgStore().ReadRecentStateChangeQueue());
        Assert.Null(row.CompletedAtUtc);
        Assert.Null(row.LeaseOwner);
    }

    [Fact]
    public async Task 第一頁已寫入第二頁HTTP取消_狀態列保留且Queue不Ack並可重試()
    {
        const long deviceId = 770;
        SeedManualBusinessDevice(deviceId);
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
            await releaseSecondPage.Task;
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        }
        finally
        {
            releaseSecondPage.TrySetResult(true);
        }

        var row = Assert.Single(_backend.PrtgStore().ReadRecentStateChangeQueue());
        Assert.Null(row.CompletedAtUtc);
        Assert.Null(row.LeaseOwner);
        Assert.True(row.Attempts > 0);
    }

    [Fact]
    public async Task Tick先做快照及鏡像回填_同交易Queue讓當輪狀態查詢接續成功()
    {
        const long deviceId = 710;
        const long sensorId = 711;
        SeedManualBusinessDevice(deviceId);
        _stubHandler.OnSend = (request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.Query.Contains("content=messages", StringComparison.Ordinal)
                ? "{\"treesize\":0,\"messages\":[]}"
                : $"{{\"treesize\":1,\"sensors\":[{{\"objid\":{sensorId},\"parentid\":{deviceId},\"sensor\":\"Ping\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false,\"lastvalue_raw\":1,\"interval\":\"60 s\"}}]}}"));
        var service = CreateService();
        service.Now = () => DateTime.Today.AddHours(12);
        await service.TickAsync();

        Assert.Contains(_backend.PrtgStore().GetAllSensors(), row => row.Objid == sensorId && row.DeviceObjid == deviceId);
        var requests = _stubHandler.RequestedUrls.ToArray();
        var mirrorRequest = Array.FindIndex(requests, url => url.Contains("content=sensors", StringComparison.Ordinal));
        var stateRequest = Array.FindIndex(requests, url => url.Contains("content=messages", StringComparison.Ordinal));
        Assert.True(mirrorRequest >= 0);
        Assert.True(stateRequest > mirrorRequest);
        Assert.NotNull(Assert.Single(_backend.PrtgStore().ReadRecentStateChangeQueue()).CompletedAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 實際Tick僅在有效採樣區段中止背景歷史且結束後釋放優先權(bool enabled)
    {
        SeedManualBusinessDevice(720);
        _settingsStore.Update(s => s.PrtgEnabled = enabled);
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
                        : "{\"treesize\":1,\"sensors\":[{\"objid\":721,\"parentid\":720,\"sensor\":\"Ping\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false,\"lastvalue_raw\":1,\"interval\":\"60 s\"}]}"));
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
            return Task.FromResult(JsonResponse("{\"sensors\":[{\"objid\":501,\"lastvalue_raw\":10}]}"));
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
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{\"sensors\":[{\"objid\":501,\"lastvalue_raw\":10}]}"));
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
        Assert.Contains("來源位址變更", service.GetStatus().LastSkipReason);
        Assert.Empty(_backend.PrtgStore().GetValues(hour, hour.AddHours(1)));
        var after = Directory.GetFiles(journalRoot, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(journalRoot, path), path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(original, after);
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

        var sensors = targets.Select(t => new PrtgSensorRow
        {
            Objid = t.Objid,
            DeviceObjid = 10,
            Name = $"Sensor-{t.Objid}",
            SensorType = t.SensorType,
            Status = "Up",
            Paused = false
        }).ToList();

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
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 快照回應時停用或改對應_停止後續批次且不誤記成功或連線失敗(bool changeMapping)
    {
        SetupTargetSensors(Enumerable.Range(1, 120).Select(id => ((long)id, "ping")).ToArray());
        _stubHandler.OnSend = (_, _) =>
        {
            if (changeMapping) _backend.PrtgStore().UpsertManualMap(new PrtgManualMapRow { DeviceObjid = 10, HostId = 2 });
            else _settingsStore.Update(s => s.PrtgEnabled = false);
            return Task.FromResult(JsonResponse("{\"treesize\":1,\"sensors\":[{\"objid\":1,\"lastvalue_raw\":10,\"interval\":60}]}"));
        };
        using var parent = new CancellationTokenSource();
        using var service = CreateService();
        await service.TickAsync(parent.Token);
        Assert.Single(_stubHandler.RequestedUrls);
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
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
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
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));
        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        await service.TickAsync();
        Assert.Contains(102L, FilterObjids(SnapshotUrls().Last()));

        _settingsStore.Update(s => s.PrtgSensorTypeWhitelist = new List<string> { "ping" });
        clock = clock.AddMinutes(15);
        await service.TickAsync();

        Assert.Equal(new[] { 101L }, FilterObjids(SnapshotUrls().Last()));
    }

    [Fact]
    public async Task TickAsync_結構同步執行中_不發請求且解除後第一次仍發()
    {
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\"}]}";
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
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\"}]}";
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
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\"}]}";
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
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\"}]}";
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
                   "{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\"}," +
                   "{\"objid\":102,\"lastvalue_raw\":20,\"interval\":\"60 s\"}," +
                   "{\"objid\":103,\"lastvalue_raw\":30,\"interval\":\"60 s\"}" +
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
                   "{\"objid\":201,\"lastvalue_raw\":100,\"interval\":\"60 s\"}," +
                   "{\"objid\":202,\"lastvalue_raw\":100,\"interval\":\"60 s\"}" +
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
                   "{\"objid\":301,\"lastvalue_raw\":100,\"interval\":\"abc\"}" +
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
    public async Task TickAsync_連續三次失敗退避加倍_成功恢復原值()
    {
        SetupTargetSensors(new[] { (401L, "Ping") });
        var service = CreateService();
        // 退避以「上次嘗試」量間隔：連續 tick 之間必須推進時鐘，才會真的再試一次
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        var originalInterval = service.GetStatus().IntervalMinutes;
        Assert.Equal(15, originalInterval);

        _stubHandler.OnSend = (_, _) => throw new HttpRequestException("Simulated HTTP failure");

        await service.TickAsync();
        Assert.Equal(1, service.GetStatus().ConsecutiveFailures);
        Assert.Equal(15, service.GetStatus().IntervalMinutes);

        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Equal(2, service.GetStatus().ConsecutiveFailures);
        Assert.Equal(15, service.GetStatus().IntervalMinutes);

        clock = clock.AddMinutes(15);
        await service.TickAsync();
        var statusAfter3 = service.GetStatus();
        Assert.Equal(3, statusAfter3.ConsecutiveFailures);
        Assert.Equal(30, statusAfter3.IntervalMinutes);

        var successJson = "{\"treesize\":1,\"sensors\":[{\"objid\":401,\"lastvalue_raw\":50,\"interval\":\"60 s\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(successJson));

        // 退避後間隔是 30 分鐘，推進滿 30 分鐘才會再試
        clock = clock.AddMinutes(30);
        await service.TickAsync();
        var statusAfterSuccess = service.GetStatus();
        Assert.Equal(0, statusAfterSuccess.ConsecutiveFailures);
        Assert.Equal(15, statusAfterSuccess.IntervalMinutes);
    }

    /// <summary>
    /// PRTG 呼叫成功但整點寫入資料庫失敗：取出的列已不在累積器，必須留著下次再寫，
    /// 而且這不是 PRTG 的失敗，不能推進退避。
    /// </summary>
    [Fact]
    public async Task TickAsync_整點寫入資料庫失敗_列留待下次重試且不計為PRTG失敗()
    {
        SetupTargetSensors(new[] { (501L, "Ping") });
        var json = "{\"treesize\":1,\"sensors\":[{\"objid\":501,\"lastvalue_raw\":10,\"interval\":\"60 s\"}]}";
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
            clock = clock.AddMinutes(15);
        }
        Assert.Equal(30, service.GetStatus().IntervalMinutes);

        // 失敗在 10:00／10:15／10:30，退避後間隔 30 分鐘；11:00 成功一次：11 點桶只有 1 個樣本
        var successJson = "{\"treesize\":1,\"sensors\":[{\"objid\":601,\"lastvalue_raw\":50,\"interval\":\"60 s\"}]}";
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

        // 每輪都有無法解析的 interval：每次成功快照都寫一行警告
        var invalidIntervalJson = "{\"treesize\":1,\"sensors\":[{\"objid\":601,\"lastvalue_raw\":100,\"interval\":\"abc\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(invalidIntervalJson));

        for (var i = 0; i < 130; i++)
        {
            await service.TickAsync();
            clock = clock.AddMinutes(15);
        }

        Assert.True(service.ExecutionOutputs.Count <= 100,
            $"執行輸出應有上限，實際 {service.ExecutionOutputs.Count} 筆");
        // 確認真的每輪都有寫（上限有被撞到），不是因為沒輸出才恆成立
        Assert.Equal(100, service.ExecutionOutputs.Count);
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

        var successJson = "{\"treesize\":1,\"sensors\":[{\"objid\":501,\"lastvalue_raw\":50,\"interval\":\"60 s\"}]}";
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
            parse.Invoke(service, new object[] { json, now, tally, accepted });
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
        var json = "{\"treesize\":1,\"sensors\":[{\"objid\":801,\"lastvalue_raw\":10,\"interval\":\"60 s\"}]}";
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
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":902,\"lastvalue_raw\":10,\"interval\":\"60 s\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(tableJson));
        SetupTargetSensors(new[] { (902L, "Ping") });
        var service = CreateService();

        Assert.True(_syncState.TryBegin());
        await service.TickAsync();
        Assert.Contains("結構同步", service.GetStatus().LastSkipReason);

        _syncState.EndRun(true);
        Assert.False(_structureSync.IsRunning);

        await service.TickAsync();
        Assert.Null(service.GetStatus().LastSkipReason);
    }

    [Fact]
    public async Task 未到間隔不設暫停原因()
    {
        var tableJson = "{\"treesize\":1,\"sensors\":[{\"objid\":903,\"lastvalue_raw\":10,\"interval\":\"60 s\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(tableJson));
        SetupTargetSensors(new[] { (903L, "Ping") });
        var service = CreateService();

        await service.TickAsync();
        Assert.NotNull(service.GetStatus().LastSuccessAt);
        Assert.Null(service.GetStatus().LastSkipReason);

        await service.TickAsync();
        Assert.Null(service.GetStatus().LastSkipReason);
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
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
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(pageTwoReached, "必須先完整讀到第一頁，再於第二頁等待時取消。");
        Assert.DoesNotContain(_backend.PrtgStore().GetAllSensors(), row => row.DeviceObjid == 20);
        Assert.Equal(clock, service.GetStatus().LastSuccessAt);
        first = false;
        if (restart)
        {
            service.Dispose();
            service = CreateService();
            service.Now = () => clock;
        }
        service.ScopeWorkBudget = TimeSpan.FromSeconds(30);
        clock = clock.AddMinutes(15);
        await service.TickAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(5001, _backend.PrtgStore().GetAllSensors().Count(row => row.DeviceObjid == 20));
        Assert.Equal(4, BackfillUrls().Count);
        Assert.Equal(clock, service.GetStatus().LastSuccessAt);
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
        using (var context = _backend.CreateContext())
            context.Database.ExecuteSqlRaw("DROP TRIGGER fail_scope_sensor_second_batch;");
        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Equal(501, store.GetAllSensors().Count(sensor => sensor.DeviceObjid == 20));
        Assert.Equal(2, BackfillUrls().Count);
        Assert.Equal(clock, service.GetStatus().LastSuccessAt);
    }

    [Fact]
    public async Task 父工作取消不當成補抓期限也不再開採樣請求()
    {
        SetupOkDevices(new long[] { 10, 20 });
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "S", SensorType = "ping", Status = "Up" }
        }, DateTime.Now);
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
        finally { cancelled.Cancel(); }
        Assert.Empty(SnapshotUrls());
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
    private static bool IsSnapshotUrl(string url) => url.Contains("lastvalue_raw");

    private static bool IsBackfillUrl(string url) => url.Contains("content=sensors") && url.Contains("parentid");

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
        var rows = string.Join(",", ids.Select(id => $"{{\"objid\":{id},\"lastvalue_raw\":1,\"interval\":\"60 s\"}}"));
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
        _backend.PrtgStore().ReplaceHostMapForDate(today, deviceObjids.Select((id, i) => new PrtgHostMapRow
        {
            DeviceObjid = id,
            HostId = i + 1,
            HostName = $"Server-{id}",
            Ip = $"192.168.{id / 250}.{id % 250 + 1}",
            MapStatus = PrtgMapStatus.Ok,
            MapDate = today
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
        Assert.Equal(3, snapshots.Count);
        Assert.All(snapshots, u => Assert.Contains("filter_objid=", u));
        Assert.DoesNotContain(_stubHandler.RequestedUrls, u => u.Contains("count=50000"));
        // 依 objid 排序、每批 50 顆
        Assert.Equal(Enumerable.Range(1001, 50).Select(i => (long)i), FilterObjids(snapshots[0]));
        Assert.Equal(20, FilterObjids(snapshots[2]).Count);
        Assert.Equal(120, service.GetStatus().PendingSamples);
        Assert.Equal(120, service.GetStatus().LastSensorCount);
    }

    [Fact]
    public async Task 分批快照_目標2001顆_恰41個filter請求且無全站查詢()
    {
        SetupTargetSensors(ManyTargets(1001, 2001));
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));

        var service = CreateService();
        await service.TickAsync();

        var snapshots = SnapshotUrls();
        Assert.Equal(41, snapshots.Count);
        Assert.All(snapshots, u => Assert.Contains("filter_objid=", u));
        Assert.DoesNotContain(_stubHandler.RequestedUrls, u => u.Contains("count=50000"));
        Assert.Equal(50, FilterObjids(snapshots[0]).Count);
        Assert.Single(FilterObjids(snapshots[40]));
        Assert.Equal(2001, service.GetStatus().PendingSamples);
        Assert.Equal(2001, service.GetStatus().LastSensorCount);
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
        Assert.Equal(40, snapshots.Count);
        Assert.All(snapshots, u => Assert.Contains("filter_objid=", u));
        Assert.DoesNotContain(_stubHandler.RequestedUrls, u => u.Contains("count=50000"));
        Assert.Equal(50, FilterObjids(snapshots[0]).Count);
        Assert.Equal(50, FilterObjids(snapshots[39]).Count);
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
    }

    [Fact]
    public async Task 分批快照_要求50回48_輸出缺2顆()
    {
        SetupTargetSensors(ManyTargets(1001, 50));
        var omit = new HashSet<long> { 1010, 1020 };
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString(), omit));

        var service = CreateService();
        await service.TickAsync();

        Assert.Single(service.ExecutionOutputs, l => l.Contains("要求 50 顆、取回 48 顆") && l.Contains("缺 2 顆"));
        Assert.Equal(48, service.GetStatus().PendingSamples);
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
    public async Task 分批快照_三批中一批500_其餘照累積且不進退避()
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

        Assert.Equal(3, SnapshotUrls().Count);
        Assert.Contains(service.ExecutionOutputs, l => l.Contains("1 批查詢失敗"));
        // 失敗批次不重複算成「缺」
        Assert.DoesNotContain(service.ExecutionOutputs, l => l.Contains("缺"));
        var status = service.GetStatus();
        Assert.Equal(100, status.PendingSamples);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.NotNull(status.LastSuccessAt);
    }

    [Fact]
    public async Task 分批快照_三批全500_進退避()
    {
        SetupTargetSensors(ManyTargets(1001, 150));
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{}", HttpStatusCode.InternalServerError));

        var service = CreateService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        await service.TickAsync();
        Assert.Equal(3, SnapshotUrls().Count);
        Assert.Equal(1, service.GetStatus().ConsecutiveFailures);
        Assert.Null(service.GetStatus().LastSuccessAt);

        clock = clock.AddMinutes(15);
        await service.TickAsync();
        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Equal(3, service.GetStatus().ConsecutiveFailures);
        Assert.Equal(30, service.GetStatus().IntervalMinutes);
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
    public async Task 分批快照_PRTG忽略filter回整站時_每顆只累積一次()
    {
        SetupTargetSensors(ManyTargets(1001, 120));
        // 不論 filter 帶什麼，一律回全部 120 顆
        _stubHandler.OnSend = (req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (IsBackfillUrl(url)) return Task.FromResult(DeviceSensors(url, new Dictionary<long, long>()));
            var rows = string.Join(",", Enumerable.Range(0, 120).Select(i => $"{{\"objid\":{1001 + i},\"lastvalue_raw\":1,\"interval\":\"60 s\"}}"));
            return Task.FromResult(JsonResponse($"{{\"treesize\":120,\"sensors\":[{rows}]}}"));
        };

        var service = CreateService();
        await service.TickAsync();

        Assert.Equal(3, SnapshotUrls().Count);
        Assert.Equal(120, service.GetStatus().LastSensorCount);
    }

    [Fact]
    public async Task 範圍補抓_待補60台_本輪恰50個補抓請求()
    {
        var devices = Enumerable.Range(1, 60).Select(i => (long)(5000 + i)).ToList();
        SetupOkDevices(devices);
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
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));

        var service = CreateService();
        // 第一次建立連線（補抓）就擲例外；之後（快照）正常
        var calls = 0;
        service.ClientFactory = () =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("模擬補抓失敗");
            return new PrtgClient("https://prtg.example.com", "token123", 30, true, _stubHandler, PrtgAuthModes.Token, "", "", "");
        };

        await service.TickAsync();

        Assert.Equal(2, calls);
        Assert.Contains(service.ExecutionOutputs, l => l.Contains("補抓失敗") && l.Contains("模擬補抓失敗"));
        var status = service.GetStatus();
        Assert.NotNull(status.LastSuccessAt);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Single(SnapshotUrls());
    }
}
