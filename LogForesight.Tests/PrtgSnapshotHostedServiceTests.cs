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
/// PRTG 數值快照背景服務單元測試（docs/PRTG-SPEC.md §3b）。
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
        _backfill = new PrtgBackfillService(_settingsStore, _backend, _backfillState, _probeState, hostStore, _schedulerRunState, _syncState, new FakeSentinelStore());

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
            _hostStore,
            new FakeSentinelStore(),
            _probeState,
            _lifetime);

        service.ClientFactory = () => new PrtgClient("https://prtg.example.com", "token123", 30, true, _stubHandler, PrtgAuthModes.Token, "", "", "");
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
        // 截斷警告只在單發全站查詢（目標超過分批門檻）時出現
        SetupTargetSensors(ManyTargets(601, PrtgSnapshotHostedService.FilteredSnapshotLimit + 1));
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
    public async Task TickAsync_筆數少於treesize_記錄截斷警告()
    {
        // 截斷警告只在單發全站查詢（目標超過分批門檻）時出現
        SetupTargetSensors(ManyTargets(601, PrtgSnapshotHostedService.FilteredSnapshotLimit + 1));

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
    public async Task 分批快照_目標超過門檻_改單發全站查詢()
    {
        SetupTargetSensors(ManyTargets(1001, PrtgSnapshotHostedService.FilteredSnapshotLimit + 1));
        _stubHandler.OnSend = (_, _) => Task.FromResult(JsonResponse("{\"treesize\":1,\"sensors\":[{\"objid\":1001,\"lastvalue_raw\":1,\"interval\":\"60 s\"}]}"));

        var service = CreateService();
        await service.TickAsync();

        var snapshots = SnapshotUrls();
        var single = Assert.Single(snapshots);
        Assert.Contains("count=50000", single);
        Assert.DoesNotContain("filter_objid=", single);
    }

    [Fact]
    public async Task 分批快照_目標剛好等於門檻_仍走分批()
    {
        SetupTargetSensors(ManyTargets(1001, PrtgSnapshotHostedService.FilteredSnapshotLimit));
        _stubHandler.OnSend = (req, _) => Task.FromResult(FilteredValues(req.RequestUri!.ToString()));

        var service = CreateService();
        await service.TickAsync();

        Assert.Equal(PrtgSnapshotHostedService.FilteredSnapshotLimit / PrtgResourceGuardProbe.MaxBatchSize, SnapshotUrls().Count);
        Assert.DoesNotContain(_stubHandler.RequestedUrls, u => u.Contains("count=50000"));
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

        var settings = _settingsStore.Get();
        var scope = PrtgScopeDevices.Compute(store, _hostStore, new PrtgMirrorGuardSource(store), settings,
            Array.Empty<Sentinel>(), new TestConsole(), new PrtgAddressResolver());
        Assert.Contains(77L, scope.DeviceObjids);
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
