using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 數值快照背景服務單元測試（階段規格：批次 E2b）。
/// </summary>
public class PrtgSnapshotHostedServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly SystemSettingsStore _settingsStore;
    private readonly SchedulerRunState _schedulerRunState;
    private readonly PrtgStructureSyncRunState _syncState;
    private readonly PrtgStructureSyncService _structureSync;
    private readonly PrtgBackfillRunState _backfillState;
    private readonly PrtgProbeRunState _probeState;
    private readonly PrtgBackfillService _backfill;
    private readonly FakeHostApplicationLifetime _lifetime;
    private readonly StubHandler _stubHandler;

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
        var statusStore = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        _structureSync = new PrtgStructureSyncService(_settingsStore, _backend, _syncState, _schedulerRunState, hostStore, statusStore, _lifetime);

        _backfillState = new PrtgBackfillRunState();
        _probeState = new PrtgProbeRunState();
        _backfill = new PrtgBackfillService(_settingsStore, _backend, _backfillState, _probeState, hostStore);

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
        _stubHandler.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private PrtgSnapshotHostedService CreateService(TestConsole? console = null)
    {
        var service = new PrtgSnapshotHostedService(
            _settingsStore,
            _backend,
            _schedulerRunState,
            _structureSync,
            _backfill,
            _lifetime);

        service.ClientFactory = () => new PrtgClient("https://prtg.example.com", "token123", 30, true, _stubHandler);
        if (console != null)
        {
            service.Console = console;
        }
        return service;
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
    /// 執行輸出只留最近的紀錄：站台長時間運行，每次截斷或間隔解析失敗都會寫一行，
    /// 沒有上限就是記憶體洩漏。
    /// </summary>
    [Fact]
    public async Task TickAsync_執行輸出有上限_長時間運行不無限增長()
    {
        SetupTargetSensors(new[] { (601L, "Ping") });
        var service = CreateService();
        var clock = DateTime.Today.AddHours(1);
        service.Now = () => clock;

        // treesize 大於回傳筆數：每次成功快照都寫一行截斷警告
        var truncatedJson = "{\"treesize\":99,\"sensors\":[{\"objid\":601,\"lastvalue_raw\":1,\"interval\":\"60 s\"}]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(truncatedJson));

        for (var i = 0; i < 130; i++)
        {
            await service.TickAsync();
            clock = clock.AddMinutes(15);
        }

        Assert.True(service.ExecutionOutputs.Count <= 100,
            $"執行輸出應有上限，實際 {service.ExecutionOutputs.Count} 筆");
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
    public async Task TickAsync_筆數少於treesize_記錄截斷警告()
    {
        SetupTargetSensors(new[] { (601L, "Ping") });

        var json = "{\"treesize\":10,\"sensors\":[" +
                   "{\"objid\":601,\"lastvalue_raw\":10,\"interval\":\"60 s\"}" +
                   "]}";
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse(json));

        var console = new TestConsole();
        var service = CreateService(console);
        await service.TickAsync();

        Assert.Contains(console.Lines, l => l.Contains("只取到 1 個感測器") && l.Contains("10") && l.Contains("截斷"));
        Assert.Contains(service.ExecutionOutputs, l => l.Contains("只取到 1 個感測器") && l.Contains("10") && l.Contains("截斷"));
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
}
