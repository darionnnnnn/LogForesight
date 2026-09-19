using System.Net;
using System.Security.Claims;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 數值快照可觀測性與規模估算單元測試（docs/PRTG-SPEC.md §3b、§8）。
/// </summary>
public class PrtgSnapshotObservabilityTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly SystemSettingsStore _settingsStore;
    private readonly RecordingAuditService _audit = new();
    private readonly StubHandler _stubHandler = new();

    public PrtgSnapshotObservabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-snapshot-obs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        _settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));

        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token123");
            s.PrtgTimeoutSeconds = 30;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string> { "Ping" };
            s.PrtgRetentionDays = 90;
            s.RetentionDays = 180;
        });
    }

    public void Dispose()
    {
        _stubHandler.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private PrtgSnapshotHostedService CreateService()
    {
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var syncState = new PrtgStructureSyncRunState();
        var schedulerRunState = new SchedulerRunState();
        var lifetime = new FakeHostApplicationLifetime();
        var statusStore = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        var structureSync = new PrtgStructureSyncService(_settingsStore, _backend, syncState, schedulerRunState, hostStore, statusStore, new PrtgBackfillRunState(), new FakeSentinelStore(), lifetime);
        var backfill = new PrtgBackfillService(_settingsStore, _backend, new PrtgBackfillRunState(), new PrtgProbeRunState(), hostStore, schedulerRunState, syncState, new FakeSentinelStore());

        var service = new PrtgSnapshotHostedService(
            _settingsStore,
            _backend,
            schedulerRunState,
            structureSync,
            backfill,
            hostStore,
            new FakeSentinelStore(),
            new PrtgProbeRunState(),
            lifetime);

        service.ClientFactory = () => new PrtgClient("https://prtg.example.com", "token123", 30, true, _stubHandler);
        return service;
    }

    private SettingsController CreateController(
        PrtgSnapshotHostedService? snapshot = null,
        StubSystemSettingsService? settingsService = null,
        IHostStore? hosts = null)
    {
        var stubSettings = settingsService ?? new StubSystemSettingsService();
        var controller = new SettingsController(
            stubSettings,
            new AiUsageStore(_backend.Blob("ai_usage")),
            _audit,
            backend: _backend,
            hosts: hosts,
            snapshot: snapshot);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test-admin"),
                new Claim(JwtTokenService.AccountClaim, "test-admin")
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private sealed class StubSystemSettingsService : ISystemSettingsService
    {
        public SystemSettingsDto Settings { get; set; } = new()
        {
            PrtgFetchStrategy = PrtgFetchStrategy.Conservative
        };

        public SystemSettingsDto Get() => Settings;
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
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

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode code = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
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

    [Fact]
    public void 鏡像狀態_快照服務未接上時欄位為預設且不擲例外()
    {
        var controller = CreateController(snapshot: null);
        var res = controller.GetPrtgMirrorStatus();

        Assert.NotNull(res.Data);
        Assert.Null(res.Data.SnapshotLastAt);
        Assert.Equal(0, res.Data.SnapshotSensors);
        Assert.Equal(0, res.Data.SnapshotIntervalMinutes);
        Assert.Equal(0, res.Data.SnapshotConsecutiveFailures);
        Assert.False(res.Data.SnapshotBackingOff);
        Assert.Null(res.Data.SnapshotSkipReason);
    }

    [Fact]
    public async Task 鏡像狀態_快照暫停時帶出原因()
    {
        var service = CreateService();
        _settingsStore.Update(s => s.PrtgEnabled = false);

        await service.TickAsync();

        var controller = CreateController(snapshot: service);
        var res = controller.GetPrtgMirrorStatus();

        Assert.NotNull(res.Data);
        Assert.Equal("PRTG 擷取未啟用", res.Data.SnapshotSkipReason);
    }

    [Fact]
    public async Task 鏡像狀態_快照成功後帶出最近時間與顆數()
    {
        SetupTargetSensors(new[] { (101L, "Ping"), (102L, "Ping") });

        var json = "{\"treesize\":2,\"sensors\":[" +
                   "{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\"}," +
                   "{\"objid\":102,\"lastvalue_raw\":20,\"interval\":\"60 s\"}" +
                   "]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(json));

        var service = CreateService();
        await service.TickAsync();

        var controller = CreateController(snapshot: service);
        var res = controller.GetPrtgMirrorStatus();

        Assert.NotNull(res.Data);
        Assert.NotNull(res.Data.SnapshotLastAt);
        Assert.Equal(2, res.Data.SnapshotSensors);
        Assert.False(res.Data.SnapshotBackingOff);
        Assert.Equal(15, res.Data.SnapshotIntervalMinutes);
        Assert.Equal(0, res.Data.SnapshotConsecutiveFailures);
    }

    [Fact]
    public async Task 鏡像狀態_連續失敗三次後標示退避()
    {
        SetupTargetSensors(new[] { (101L, "Ping") });
        _stubHandler.OnSend = (_, _) => throw new HttpRequestException("Simulated HTTP failure");

        var service = CreateService();
        var clock = DateTime.Today.AddHours(1);
        service.Now = () => clock;

        // 第 1 次失敗
        await service.TickAsync();
        Assert.Equal(1, service.GetStatus().ConsecutiveFailures);

        // 第 2 次失敗（推進 15 分鐘）
        clock = clock.AddMinutes(15);
        await service.TickAsync();
        Assert.Equal(2, service.GetStatus().ConsecutiveFailures);

        // 第 3 次失敗（推進 15 分鐘，達到退避門檻 3 次）
        clock = clock.AddMinutes(15);
        await service.TickAsync();
        var statusAfter3 = service.GetStatus();
        Assert.Equal(3, statusAfter3.ConsecutiveFailures);
        Assert.Equal(30, statusAfter3.IntervalMinutes);

        var stubSettings = new StubSystemSettingsService();
        stubSettings.Settings.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;

        var controller = CreateController(snapshot: service, settingsService: stubSettings);
        var res = controller.GetPrtgMirrorStatus();

        Assert.NotNull(res.Data);
        Assert.True(res.Data.SnapshotBackingOff);
        Assert.Equal(3, res.Data.SnapshotConsecutiveFailures);
        Assert.Equal(30, res.Data.SnapshotIntervalMinutes);
    }

    [Fact]
    public void 估算_快照目標不受取數範圍影響()
    {
        SetupTargetSensors(new[] { (101L, "Ping"), (102L, "Ping") });

        var controller = CreateController();
        var resTriggered = controller.EstimatePrtgFetchScope("triggered");
        var resAllMapped = controller.EstimatePrtgFetchScope("all-mapped");

        Assert.NotNull(resTriggered.Data);
        Assert.NotNull(resAllMapped.Data);
        Assert.True(resTriggered.Data.Success);
        Assert.True(resAllMapped.Data.Success);

        // triggered 模式下主機與感測器數量事前無法估算（為 0）
        Assert.Equal(0, resTriggered.Data.Sensors);
        // all-mapped 模式下感測器數量為 2
        Assert.Equal(2, resAllMapped.Data.Sensors);

        // 但快照目標不受取數範圍影響，兩者均為 2
        Assert.True(resTriggered.Data.SnapshotTargets > 0);
        Assert.Equal(2, resTriggered.Data.SnapshotTargets);
        Assert.Equal(resAllMapped.Data.SnapshotTargets, resTriggered.Data.SnapshotTargets);
    }

    [Fact]
    public void 估算_快照列數依保留期換算()
    {
        SetupTargetSensors(new[] { (101L, "Ping"), (102L, "Ping") });

        // 設定 PrtgRetentionDays = 90, RetentionDays = 180 (min = 90)
        _settingsStore.Update(s =>
        {
            s.PrtgRetentionDays = 90;
            s.RetentionDays = 180;
        });

        var controller = CreateController();
        var res = controller.EstimatePrtgFetchScope("all-mapped");

        Assert.NotNull(res.Data);
        Assert.True(res.Data.Success);
        Assert.Equal(2, res.Data.SnapshotTargets);
        Assert.Equal(2 * 24L, res.Data.SnapshotRowsPerDay);
        Assert.Equal(90, res.Data.SnapshotRetentionDays);
        Assert.Equal(res.Data.SnapshotRowsPerDay * 90, res.Data.SnapshotRowsAtRetention);

        // 再測試反向：PrtgRetentionDays = 200, RetentionDays = 60 (min = 60)
        _settingsStore.Update(s =>
        {
            s.PrtgRetentionDays = 200;
            s.RetentionDays = 60;
        });

        var res2 = controller.EstimatePrtgFetchScope("all-mapped");
        Assert.NotNull(res2.Data);
        Assert.True(res2.Data.Success);
        Assert.Equal(60, res2.Data.SnapshotRetentionDays);
        Assert.Equal(res2.Data.SnapshotRowsPerDay * 60, res2.Data.SnapshotRowsAtRetention);
    }

    [Fact]
    public void 估算_白名單為空時快照警告()
    {
        SetupTargetSensors(new[] { (101L, "Ping") });

        _settingsStore.Update(s =>
        {
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var controller = CreateController();
        var res = controller.EstimatePrtgFetchScope("all-mapped");

        Assert.NotNull(res.Data);
        Assert.True(res.Data.Success);
        Assert.NotNull(res.Data.SnapshotWarning);
        Assert.Contains("白名單為空", res.Data.SnapshotWarning);
    }
}
