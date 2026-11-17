using System.Diagnostics;
using System.Globalization;
using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Tests.TestDoubles;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 整條鏈的程序內驗證：由 <see cref="FakePrtgServer"/>（真實 HTTP、127.0.0.1）扮演 PRTG，
/// 走真正的 <c>PrtgClient</c> → 結構同步 → 主機對應 → 取數範圍 → 數值快照 → 夜間每日管線 → 主機詳情服務，
/// 每一層都對照上一層拿到的數字。現有約 120 個 PRTG 測試類別多是單一服務層，本類別補的是「層與層之間數字有沒有對上」。
///
/// 情境：3 台主機（WIN-APP01、WIN-DB01 為 Windows，LNX-WEB01 為 Linux），PRTG 有 5 台裝置，其中 3 台 IP 對得上、2 台對不上。
/// 對上的裝置各有 Ping、SNMP Disk Free、SNMP Memory 三顆感測器，WIN-APP01 另有一顆 Windows Network Card（驗流量類換算）；
/// WIN-APP01 的磁碟可用率連續數日偏低、昨日磁碟 Warning 到整天結束、Ping 昨晚 Down 到整天結束。
/// 邊界案例各一個測試；「現況」字樣的測試記錄的是目前行為，不代表規格期望（差異見各測試的註解）。
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class PrtgEndToEndChainTests : IDisposable
{
    // ── 情境常數 ──────────────────────────────────────────────────────────
    private const long AppDevice = 1001, DbDevice = 1002, WebDevice = 1003, EdgeDevice1 = 1004, EdgeDevice2 = 1005;
    private const long AppPing = 2001, AppDisk = 2002, AppMemory = 2003, AppNetwork = 2004;
    private const long DbPing = 2101, DbDisk = 2102, DbMemory = 2103;
    private const long WebPing = 2201, WebDisk = 2202, WebMemory = 2203;
    private const long EdgePing1 = 2401, EdgePing2 = 2501;

    private static readonly long[] MatchedDevices = [AppDevice, DbDevice, WebDevice];
    private static readonly long[] MatchedSensors =
        [AppPing, AppDisk, AppMemory, AppNetwork, DbPing, DbDisk, DbMemory, WebPing, WebDisk, WebMemory];

    private static DateTime Today => DateTime.Today;
    private static DateTime Day => DateTime.Today.AddDays(-1);

    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly SystemSettingsStore _settingsStore;
    private readonly HostStore _hostStore;
    private readonly FakePrtgServer _server;
    private readonly PrtgRequestBudget _budget = new(new AcceleratedPrtgClock());
    private readonly List<PrtgSnapshotHostedService> _snapshotServices = new();
    private WebHost _hostApp = null!, _hostDb = null!, _hostWeb = null!;

    public PrtgEndToEndChainTests()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        _settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        _hostStore = new HostStore(_backend.Blob("hosts"));
        _server = new FakePrtgServer();
        SeedHosts();
        SeedPrtgServer();
        ConfigurePrtg();
    }

    public void Dispose()
    {
        foreach (var service in _snapshotServices) service.Dispose();
        _server.Dispose();
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── 情境建置 ──────────────────────────────────────────────────────────

    private void SeedHosts()
    {
        WebHost Add(string name, string ip) =>
            _hostStore.Upsert(new WebHost { Source = "netiq", HostName = name, IpAddress = ip, Active = true });
        _hostApp = Add("WIN-APP01", "10.76.0.11");
        _hostDb = Add("WIN-DB01", "10.76.0.12");
        _hostWeb = Add("LNX-WEB01", "10.76.0.13");
    }

    private static FakePrtgServer.Sensor Sensor(long id, long parent, string name, string type, double lastValue,
        string unit = "%", string status = "Up", int statusRaw = 3) =>
        new() { Objid = id, ParentId = parent, Name = name, Type = type, LastValue = lastValue, Unit = unit, Status = status, StatusRaw = statusRaw };

    private void SeedPrtgServer()
    {
        _server.Devices.AddRange(
        [
            new(AppDevice, "WIN-APP01", "10.76.0.11"),
            new(DbDevice, "WIN-DB01", "10.76.0.12"),
            new(WebDevice, "LNX-WEB01", "10.76.0.13"),
            new(EdgeDevice1, "EDGE-SW01", "10.76.9.1", "Network"),
            new(EdgeDevice2, "EDGE-SW02", "10.76.9.2", "Network")
        ]);
        _server.Sensors.AddRange(
        [
            // WIN-APP01：昨晚 Ping Down、磁碟 Warning（狀態與下方 messages 一致）
            Sensor(AppPing, AppDevice, "Ping", "Ping", 0, "ms", "Down", 5),
            Sensor(AppDisk, AppDevice, "Disk C:", "SNMP Disk Free", 9.5, "%", "Warning", 4),
            Sensor(AppMemory, AppDevice, "Memory", "SNMP Memory", 41.2),
            Sensor(AppNetwork, AppDevice, "Network Card", "Windows Network Card", 125000, "bytes/s"),
            Sensor(DbPing, DbDevice, "Ping", "Ping", 2.5, "ms"),
            Sensor(DbDisk, DbDevice, "Disk C:", "SNMP Disk Free", 55.5),
            Sensor(DbMemory, DbDevice, "Memory", "SNMP Memory", 62.25),
            Sensor(WebPing, WebDevice, "Ping", "Ping", 1.5, "ms"),
            Sensor(WebDisk, WebDevice, "Disk /", "SNMP Disk Free", 71.0),
            Sensor(WebMemory, WebDevice, "Memory", "SNMP Memory", 48.75),
            // 兩台對不上的裝置：各一顆 Ping（取數範圍外，任何請求都不該碰到）
            Sensor(EdgePing1, EdgeDevice1, "Ping", "Ping", 3, "ms"),
            Sensor(EdgePing2, EdgeDevice2, "Ping", "Ping", 3, "ms")
        ]);
        _server.Messages.AddRange(
        [
            new(AppMemory, Day.AddHours(1), "Up", 3, "OK"),
            new(AppPing, Day.AddHours(3), "Up", 3, "OK"),
            new(AppDisk, Day.AddHours(6), "Warning", 4, "Free space below 15%"),
            new(AppPing, Day.AddHours(20), "Down", 5, "Ping timeout"),
            new(DbPing, Day.AddHours(2), "Up", 3, "OK")
        ]);
        // 磁碟可用率逐日下降（昨日最低）：D-1＝9.0、D-2＝9.5 … D-5＝11.0，每小時再加 0.01×時
        _server.HistoricValue = (sensorId, hourStart) => sensorId switch
        {
            AppDisk => DiskFree(hourStart),
            AppPing => 3.5,
            AppMemory => 40.0,
            AppNetwork => 125000.0,
            DbPing or WebPing => 2.0,
            DbDisk or WebDisk => 60.0,
            DbMemory or WebMemory => 50.0,
            _ => null
        };
    }

    private static double DiskFree(DateTime hourStart) =>
        8.5 + 0.5 * (Today - hourStart.Date).Days + 0.01 * hourStart.Hour;

    private void ConfigurePrtg(string strategy = PrtgFetchStrategy.Conservative, int timeoutSeconds = 30)
    {
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = _server.BaseUrl;
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt(_server.Token);
            s.PrtgTimeoutSeconds = timeoutSeconds;
            s.PrtgFetchStrategy = strategy;
        });
    }

    // ── 共用步驟 ──────────────────────────────────────────────────────────

    private sealed class ListConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") { lock (Lines) Lines.Add(message); }
        public bool Any(string text) { lock (Lines) return Lines.Any(l => l.Contains(text, StringComparison.Ordinal)); }
    }

    private sealed class CollectingProgress : IRunProgress
    {
        public List<string> Phases { get; } = new();
        public void Report(string phase, int done, int total) => Phases.Add(phase);
    }

    private PrtgFetchService NewFetchService(ListConsole console, out PrtgClient client)
    {
        var settings = _settingsStore.Get();
        client = PrtgClientFactory.Create(settings, null, _budget);
        return PrtgFetchService.Create(client, _backend, _backend.PrtgStore(), settings, console, guard: null);
    }

    /// <summary>手動「同步結構與對應」的執行本體（PrtgStructureSyncRunner）：裝置→對應→範圍→感測器→狀態變更。</summary>
    private async Task<(PrtgStructureSyncStatus Status, ListConsole Console)> SyncAsync(CancellationToken ct = default)
    {
        var console = new ListConsole();
        var settings = _settingsStore.Get();
        var fetch = NewFetchService(console, out var client);
        using (client)
        {
            var store = _backend.PrtgStore();
            var status = await PrtgStructureSyncRunner.RunAsync(fetch, store, _hostStore, new PrtgAddressResolver(),
                concurrency: 2, console, ct, new PrtgMirrorGuardSource(store), settings, new List<Sentinel>(), today: Today);
            return (status, console);
        }
    }

    private PrtgScopeResult ComputeScope()
    {
        var store = _backend.PrtgStore();
        return PrtgScopeDevices.Compute(store, _hostStore, new PrtgMirrorGuardSource(store), _settingsStore.Get(),
            new List<Sentinel>(), new ListConsole(), new PrtgAddressResolver(), hostIds: null);
    }

    /// <summary>模擬「管理者確認過一次」的監看裝置數基準：沒有基準時範圍外清除與感測器過期清除一律不自動執行。</summary>
    private void SetScopeBaseline(int deviceCount) =>
        _backend.PrtgStore().ScopeBaseline().Update(b => { b.DeviceCount = deviceCount; b.At = DateTime.Now.AddDays(-1); });

    private HashSet<long> MirrorSensorIds() => _backend.PrtgStore().GetAllSensors().Select(s => s.Objid).ToHashSet();

    /// <summary>
    /// CLAUDE.md「不要讓 PRTG 取數對整台 PRTG 查」：devices 是唯一允許全站的表；sensors 必須帶範圍內的裝置 id（或範圍內感測器的 filter_objid）、
    /// messages 必須帶範圍內裝置 id（不得 id=0 或不帶 id）、historicdata 只能查範圍內感測器。
    /// </summary>
    private void AssertRequestsWithinScope(IEnumerable<long> scopeDevices)
    {
        var devices = scopeDevices.ToHashSet();
        var sensors = MirrorSensorIds();
        foreach (var request in _server.Requests)
        {
            if (request.Path.Equals("/api/status.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (request.Path.Equals("/api/historicdata.json", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(request.Id is { } sid && sensors.Contains(sid), $"historicdata 查了範圍外的感測器：{request.RawQuery}");
                continue;
            }
            Assert.Equal("/api/table.json", request.Path, ignoreCase: true);
            switch (request.Content)
            {
                case "devices":
                    Assert.False(request.HasId, "devices 是全站表，不應帶 id");
                    break;
                case "sensors":
                    var byDevice = request.HasId && request.Id is { } did && did != 0 && devices.Contains(did);
                    var bySensors = !request.HasId && request.FilterObjids.Count > 0 && request.FilterObjids.All(sensors.Contains);
                    Assert.True(byDevice || bySensors, $"sensors 請求不在範圍內：{request.RawQuery}");
                    break;
                case "messages":
                    Assert.True(request.HasId && request.Id is { } mid && mid != 0 && devices.Contains(mid),
                        $"messages 請求沒有限定範圍內的裝置（對整台 PRTG 查）：{request.RawQuery}");
                    break;
                default:
                    Assert.Fail($"未預期的 content：{request.RawQuery}");
                    break;
            }
        }
    }

    private static DateTime LocalOf(double oaUtc) =>
        DateTime.SpecifyKind(DateTime.FromOADate(oaUtc), DateTimeKind.Utc).ToLocalTime();

    // ═════════════════════════════════════════════════════════════════════
    // 整條鏈
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task 整條鏈_PRTG回應到結構同步_對應_範圍_快照_每日管線_主機詳情_每一層數字一致()
    {
        var store = _backend.PrtgStore();

        // ── 1. 結構同步：鏡像的裝置／感測器數與 PRTG 回的一致 ──
        var (sync, _) = await SyncAsync();
        Assert.True(sync.Success, sync.ErrorMessage);
        Assert.Equal(5, sync.Devices);
        Assert.Equal(MatchedSensors.Length, sync.Sensors);

        var mirrorDevices = store.GetAllDevices();
        Assert.Equal(5, mirrorDevices.Count);
        foreach (var expected in _server.Devices)
        {
            var actual = Assert.Single(mirrorDevices, d => d.Objid == expected.Objid);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Host, actual.Ip);
        }

        var mirrorSensors = store.GetAllSensors();
        Assert.Equal(MatchedSensors.Length, mirrorSensors.Count);
        Assert.Equal(MatchedSensors.Order(), mirrorSensors.Select(s => s.Objid).Order());
        foreach (var actual in mirrorSensors)
        {
            var expected = _server.Sensors.Single(s => s.Objid == actual.Objid);
            Assert.Equal(expected.ParentId, actual.DeviceObjid);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Type, actual.SensorType);
            Assert.Equal(expected.Status, actual.Status);
            Assert.False(actual.Paused);
        }
        // 對不上的兩台裝置底下的感測器不進鏡像（範圍外不取）
        Assert.DoesNotContain(mirrorSensors, s => s.Objid is EdgePing1 or EdgePing2);

        var deviceRequests = _server.Requests.Where(r => r.Content == "devices").ToList();
        var deviceRequest = Assert.Single(deviceRequests);
        Assert.Equal(0, deviceRequest.Start);
        Assert.Equal(PrtgTablePager.DefaultPageSize, deviceRequest.Count);
        Assert.Equal(MatchedDevices.Order(),
            _server.Requests.Where(r => r.Content == "sensors").Select(r => r.Id!.Value).Order());
        Assert.Equal(MatchedDevices.Order(),
            _server.Requests.Where(r => r.Content == "messages").Select(r => r.Id!.Value).Order());
        Assert.All(_server.Requests.Where(r => r.Content == "messages"), r => Assert.Equal("7days", r.DateRel));

        // 狀態變更：5 筆訊息都落盤，狀態字串與時間（datetime_raw 以 UTC 換回本地）一致
        var changes = store.GetStateChanges(Day, Today.AddDays(1));
        Assert.Equal(_server.Messages.Count, changes.Count);
        foreach (var message in _server.Messages)
        {
            var row = Assert.Single(changes, c => c.SensorObjid == message.SensorObjid && c.Status == message.Status
                && Math.Abs((c.ChangedAt - message.LocalTime).TotalSeconds) < 2);
            Assert.Equal(message.Text, row.Message);
        }

        // ── 2. 主機對應：3 台 ok、2 台 unmatched；之後的讀取走 GetHostMapForDay ──
        Assert.Equal(3, sync.MapOk);
        Assert.Equal(2, sync.MapUnmatched);
        Assert.Equal(3, sync.MapMappedHosts);
        var (mapDate, mapRows) = store.GetHostMapForDay(Today, PrtgHostMapLookup.LookbackDays);
        Assert.Equal(Today, mapDate);
        Assert.Equal(5, mapRows.Count);
        var ok = mapRows.Where(r => r.MapStatus == PrtgMapStatus.Ok).ToList();
        Assert.Equal(3, ok.Count);
        Assert.Equal(_hostApp.HostId, ok.Single(r => r.DeviceObjid == AppDevice).HostId);
        Assert.Equal(_hostDb.HostId, ok.Single(r => r.DeviceObjid == DbDevice).HostId);
        Assert.Equal(_hostWeb.HostId, ok.Single(r => r.DeviceObjid == WebDevice).HostId);
        var unmatched = mapRows.Where(r => r.MapStatus == PrtgMapStatus.Unmatched).Select(r => r.DeviceObjid).Order().ToList();
        Assert.Equal(new[] { EdgeDevice1, EdgeDevice2 }, unmatched);

        // ── 3. 取數範圍：只含對上的 3 台 ──
        var scope = ComputeScope();
        Assert.Equal(MatchedDevices.Order(), scope.DeviceObjids.Order());
        Assert.Equal(3, scope.Mapped);
        AssertRequestsWithinScope(scope.DeviceObjids);

        // ── 4. 快照：鏡像數值列＝假伺服器的 lastvalue_raw（流量類 ×3600÷interval）──
        await RunSnapshotAsync();
        var hour = Today.AddHours(10);
        var snapshotValues = store.GetValues(hour, hour.AddHours(1));
        Assert.Equal(MatchedSensors.Order(), snapshotValues.Select(v => v.SensorObjid).Order());
        foreach (var value in snapshotValues)
        {
            var source = _server.Sensors.Single(s => s.Objid == value.SensorObjid);
            var expected = value.SensorObjid == AppNetwork ? source.LastValue * 3600.0 / 60.0 : source.LastValue;
            Assert.Equal(expected, value.AvgValue!.Value, 6);
            Assert.Equal(25.0, value.Coverage!.Value, 6);   // 保守策略 15 分鐘一次：每小時預期 4 個樣本
        }
        // 流量類的換算確實發生（不是剛好等於原值）
        Assert.NotEqual(_server.Sensors.Single(s => s.Objid == AppNetwork).LastValue,
            snapshotValues.Single(v => v.SensorObjid == AppNetwork).AvgValue!.Value);

        // ── 5. 夜間每日流程：激進策略才會逐顆取歷史值；磁碟前幾日的值走同一條 historicdata 路徑補齊 ──
        _settingsStore.Update(s => s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive);
        await FetchPastDiskDaysAsync(2, 5);

        var (ctx, console, progress, registry) = CreatePipelineContext();
        await PrtgDailyPipeline.RunAsync(ctx, _backend, _hostStore, new[] { Day }, Task.CompletedTask,
            hostIds: null, guard: null, requestBudget: _budget);

        Assert.True(registry.IsEvaluated(Day));
        Assert.Contains(RunPhases.PrtgFindingsReady, progress.Phases);
        var appFindings = registry.For(_hostApp.HostId, Day);
        Assert.Equal(new[] { $"prtg:{PrtgRuleEvaluator.RuleDown}:{AppPing}", $"prtg:{PrtgRuleEvaluator.RuleWarning}:{AppDisk}" }.Order(),
            appFindings.Select(f => f.EventKey).Order());
        Assert.All(appFindings, f => Assert.Equal("PRTG", f.LogName));
        Assert.Empty(registry.For(_hostDb.HostId, Day));
        Assert.Empty(registry.For(_hostWeb.HostId, Day));

        // 主機日紀錄（PRTG 獨立主機日）：風險與判定依據含 PRTG 來源；只有有 finding 的主機才建
        var appRecord = Assert.Single(_backend.RecordStore(new HostKey { HostId = _hostApp.HostId, HostName = _hostApp.HostName }).ReadRecent(Day, 1));
        Assert.Equal(AnalysisLogSource.Prtg, appRecord.LogSource);
        Assert.Equal(RiskLevels.High, appRecord.RiskLevel);   // availability 的 down 帶 ElevatesDayRisk
        Assert.Contains("prtg:" + PrtgRuleEvaluator.RuleDown, appRecord.RiskBasis);
        Assert.Equal(appFindings.Select(f => f.EventKey).Order(), appRecord.TopIssues.Select(i => i.EventKey).Order());
        Assert.Empty(_backend.RecordStore(new HostKey { HostId = _hostDb.HostId, HostName = _hostDb.HostName }).ReadRecent(Day, 1));
        Assert.Empty(_backend.RecordStore(new HostKey { HostId = _hostWeb.HostId, HostName = _hostWeb.HostName }).ReadRecent(Day, 1));

        // 夜間的對應是對「昨天」重算；範圍仍是同樣 3 台
        Assert.Equal(3, store.GetHostMapForDay(Day, PrtgHostMapLookup.LookbackDays).Rows.Count(r => r.MapStatus == PrtgMapStatus.Ok));
        Assert.Equal(MatchedDevices.Order(), ComputeScope().DeviceObjids.Order());

        // 觸發式取數：只有命中規則的 WIN-APP01 逐顆查歷史值；其餘兩台沒有被觸發，不得出現 historicdata 請求
        var historicIds = _server.Requests.Where(r => r.Path == "/api/historicdata.json").Select(r => r.Id!.Value).Distinct().Order().ToList();
        Assert.Equal(new[] { AppPing, AppDisk, AppMemory, AppNetwork }.Order(), historicIds);

        // 數值評估走 PrtgValueAssessment：磁碟連續 5 日有效、每日代表值＝當日 24 小時平均
        var dayRows = store.GetValuesForSensor(AppDisk, Day, Day.AddDays(1));
        Assert.Equal(24, dayRows.Count);
        Assert.All(dayRows, v => Assert.Equal(PrtgDataQuality.Ok, v.Quality));
        foreach (var value in dayRows)
            Assert.Equal(DiskFree(value.PeriodStart), value.AvgValue!.Value, 6);

        var assessment = new PrtgValueAssessment(store, new Dictionary<string, PrtgValueSemantic>());
        var diskSensor = new PrtgValueSensor(AppDisk, AppDevice, _hostApp.HostId, "Disk C:", "SNMP Disk Free", PrtgSensorCategories.Disk);
        var assessed = Assert.Single(assessment.AssessPage([diskSensor], DateOnly.FromDateTime(Day), windowDays: 35, requiredDays: 28));
        Assert.Equal(PrtgValueReason.InsufficientDays, assessed.Reason);
        Assert.Equal(5, assessed.ValidDays);
        Assert.Equal(PrtgValueSemantic.DiskPercentFree, assessed.Semantic);
        Assert.Equal(5, assessed.Daily.Count);
        for (var back = 1; back <= 5; back++)
        {
            var daily = assessed.Daily.Single(d => d.Day == DateOnly.FromDateTime(Today.AddDays(-back)));
            Assert.Equal(24, daily.UsableHours);
            Assert.Equal(8.5 + 0.5 * back + 0.01 * 11.5, daily.Value, 6);
        }

        // 執行紀錄的逐日統計：沒有失敗階段
        ctx.RunRecorder.Finish(0);
        var run = new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")).GetRun(ctx.RunRecorder.RunId)!;
        var dayStat = Assert.Single(run.PrtgDays!);
        Assert.Equal(Day, dayStat.Date);
        Assert.NotEqual(BatchRun.PrtgOutcomeFailed, dayStat.Outcome);
        Assert.True(console.Any("PRTG 每日擷取完成"), "夜間輸出應有每日擷取完成行");

        // ── 6. 畫面資料：主機詳情的 PRTG 區塊與前面各層一致 ──
        var detailService = new HostPrtgDetailService(store,
            new FixedVisibilityService([_hostApp.HostId, _hostDb.HostId, _hostWeb.HostId]), assessment, new FakeRuleStore());
        var detail = detailService.GetForHost(_hostApp.HostId);
        Assert.Equal(Today, detail.MapDate);   // 「最近一次」對應是手動同步算的今天；夜間重算的昨天那份較舊
        var detailDevice = Assert.Single(detail.Devices);
        Assert.Equal(AppDevice, detailDevice.DeviceObjid);
        Assert.Equal("WIN-APP01", detailDevice.Name);
        Assert.Equal("10.76.0.11", detailDevice.Ip);
        Assert.Equal(PrtgMapStatus.Ok, detailDevice.MapStatus);
        Assert.Equal(4, detailDevice.SensorCount);
        foreach (var sensor in detailDevice.Sensors)
        {
            var source = _server.Sensors.Single(s => s.Objid == sensor.Objid);
            Assert.Equal(source.Name, sensor.Name);
            Assert.Equal(source.Type, sensor.SensorType);
            Assert.Equal(source.Status, sensor.Status);
        }
        var diskDto = detailDevice.Sensors.Single(s => s.Objid == AppDisk);
        Assert.Equal(PrtgSensorCategories.Disk, diskDto.Category);
        Assert.Equal(assessed.ValidDays, diskDto.ValidDays);
        Assert.Equal(assessed.RequiredDays, diskDto.RequiredDays);
        Assert.Equal(assessed.Reason.ToString(), diskDto.ValueReasonCode);
        Assert.Equal(PrtgValueReasonText.Label(assessed.Reason), diskDto.ValueReason);
        // 記憶體只有夜間補到的昨日一天
        var memoryDto = detailDevice.Sensors.Single(s => s.Objid == AppMemory);
        Assert.Equal(1, memoryDto.ValidDays);
        Assert.Equal(PrtgValueReason.InsufficientDays.ToString(), memoryDto.ValueReasonCode);
        // 別台主機的詳情只看得到自己的裝置
        Assert.Equal(DbDevice, Assert.Single(detailService.GetForHost(_hostDb.HostId).Devices).DeviceObjid);

        // 全程每一筆請求都在取數範圍內：沒有整台 PRTG 的 sensors／messages／historicdata
        AssertRequestsWithinScope(MatchedDevices);
        Assert.DoesNotContain(_server.Requests, r => r.Content == "messages" && (!r.HasId || r.Id == 0));
        Assert.DoesNotContain(_server.Requests, r => r.Id is EdgeDevice1 or EdgeDevice2 or EdgePing1 or EdgePing2);
    }

    // ── 整條鏈的輔助 ──────────────────────────────────────────────────────

    private async Task RunSnapshotAsync()
    {
        var statusStore = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        var syncState = new PrtgStructureSyncRunState();
        var scheduler = new SchedulerRunState();
        var backfillState = new PrtgBackfillRunState();
        var probeState = new PrtgProbeRunState();
        var lifetime = new FakeHostApplicationLifetime();
        var structureSync = new PrtgStructureSyncService(_settingsStore, _backend, syncState, scheduler, _hostStore, statusStore,
            backfillState, new FakeSentinelStore(), new DataVersionStamp(), new PrtgAddressResolver(), lifetime);
        var backfill = new PrtgBackfillService(_settingsStore, _backend, backfillState, probeState, _hostStore, scheduler,
            syncState, new FakeSentinelStore(), structureSync, new PrtgAddressResolver());
        var service = new PrtgSnapshotHostedService(_settingsStore, _backend, scheduler, structureSync, backfill, _hostStore,
            new FakeSentinelStore(), probeState, lifetime, TimeSpan.FromSeconds(60));
        service.ClientFactory = () => PrtgClientFactory.Create(_settingsStore.Get(), null, _budget);
        service.Console = new ListConsole();
        _snapshotServices.Add(service);

        // 第一輪在 10:05 取樣；整點後的下一輪先把上一小時結算寫進鏡像（再取下一小時的樣本）
        var hour = Today.AddHours(10);
        service.Now = () => hour.AddMinutes(5);
        await service.TickAsync();
        Assert.NotNull(service.GetStatus().LastSuccessAt);
        service.Now = () => hour.AddHours(1);
        await service.TickAsync();
    }

    /// <summary>以正式的歷史值取數路徑（FetchValuesForSensorsAsync → historicdata）補磁碟前幾日。</summary>
    private async Task FetchPastDiskDaysAsync(int fromDaysBack, int toDaysBack)
    {
        var console = new ListConsole();
        var fetch = NewFetchService(console, out var client);
        using (client)
        {
            for (var back = fromDaysBack; back <= toDaysBack; back++)
            {
                var (written, failed) = await fetch.FetchValuesForSensorsAsync(Today.AddDays(-back), [AppDisk], 1, CancellationToken.None);
                Assert.Equal(24, written);
                Assert.Equal(0, failed);
            }
        }
    }

    private (AnalysisRunContext Ctx, ListConsole Console, CollectingProgress Progress, PrtgFindingsRegistry Registry) CreatePipelineContext(
        CancellationToken ct = default)
    {
        var prtgStore = _backend.PrtgStore();
        var settings = _settingsStore.Get();
        if (prtgStore.GetSensorStatuses().Count > 0)
            prtgStore.ApplyAutoCategories(PrtgSensorTypeCategoryMap.ParseOverrides(settings.PrtgSensorTypeCategoryOverrides).Map);
        var console = new ListConsole();
        var progress = new CollectingProgress();
        var registry = new PrtgFindingsRegistry();
        var caseCoordinator = new IssueCaseCoordinator(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(), _backend.RecordStore(),
            new HostStore(_backend.Blob("hosts")), new IssueOwnerStore(_backend.Blob("issue_owners")));
        var dispatch = NightlyDispatchFakes.Create(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(),
            new HostStore(_backend.Blob("hosts")), new IssueOwnerStore(_backend.Blob("issue_owners")), caseCoordinator);
        var recorder = new BatchRunRecorder(
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")), "test-host", Array.Empty<string>());
        var ctx = new AnalysisRunContext(
            new RunRequest(), new AppSettings(), new RetentionOptions(), console, ct,
            new EventLogService(), caseCoordinator, _backend.RiskyEventStore(), recorder,
            new OrchestratorResult(), UseAi: false, progress, registry, dispatch,
            PrtgEnabled: settings.PrtgEnabled, Workflow: null);
        return (ctx, console, progress, registry);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 邊界案例
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 分頁（頁大小固定 5000，PrtgTablePager.DefaultPageSize）：5003 台時第二頁 start=5000；
    /// 剛好 5000 台時第二頁 start 越界，假伺服器夾回末頁，分頁器靠「本頁沒有新列」收尾，鏡像不重複也不少。
    /// 主情境只有 5 台裝置，翻不到第二頁，所以分頁在這裡單獨驗。
    /// </summary>
    [Theory]
    [InlineData(5003)]
    [InlineData(5000)]
    public async Task 分頁_依count切頁_start越界夾回末頁_鏡像筆數與PRTG一致(int totalDevices)
    {
        for (var i = _server.Devices.Count; i < totalDevices; i++)
            _server.Devices.Add(new(10_000 + i, $"FILLER-{i:D5}", $"10.200.{i / 250}.{i % 250 + 1}", "Filler"));

        var (sync, _) = await SyncAsync();

        Assert.True(sync.Success, sync.ErrorMessage);
        Assert.Equal(totalDevices, sync.Devices);
        Assert.Equal(totalDevices, _backend.PrtgStore().GetAllDevices().Count);
        var starts = _server.Requests.Where(r => r.Content == "devices").Select(r => r.Start!.Value).ToList();
        Assert.Equal(new[] { 0, PrtgTablePager.DefaultPageSize }, starts);
        Assert.All(_server.Requests.Where(r => r.Content == "devices"), r => Assert.Equal(PrtgTablePager.DefaultPageSize, r.Count));
        // 湊數裝置全部對不上主機：取數範圍仍只有 3 台
        Assert.Equal(MatchedDevices.Order(), ComputeScope().DeviceObjids.Order());
        AssertRequestsWithinScope(MatchedDevices);
    }

    /// <summary>devices 單頁少給（treesize 仍是 5）：依現行規則未收斂，不清除既有鏡像，也不重算對應。</summary>
    [Fact]
    public async Task 截斷_devices回傳筆數少於treesize_未收斂且不清除既有鏡像()
    {
        var (first, _) = await SyncAsync();
        Assert.True(first.Success, first.ErrorMessage);
        var store = _backend.PrtgStore();
        Assert.Equal(5, store.GetAllDevices().Count);

        _server.TruncateTo["devices"] = 3;
        var (second, console) = await SyncAsync();

        Assert.False(second.Success);
        Assert.True(console.Any("少於 PRTG 回報的 treesize"), "應出聲說明讀到的筆數少於 treesize");
        Assert.Equal(5, store.GetAllDevices().Count);                 // 沒讀到的不能當成已刪除
        Assert.Equal(MatchedSensors.Length, store.GetAllSensors().Count);
        Assert.Equal(3, store.GetHostMapForDay(Today, PrtgHostMapLookup.LookbackDays).Rows.Count(r => r.MapStatus == PrtgMapStatus.Ok));
        Assert.Equal(MatchedDevices.Order(), ComputeScope().DeviceObjids.Order());
    }

    /// <summary>某一頁不回應：請求在設定的逾時後失敗、整趟標失敗並結束（不無限等待），既有鏡像保留。</summary>
    [Fact]
    public async Task 逾時_devices不回應_逾時後標失敗結束且鏡像保留()
    {
        var (first, _) = await SyncAsync();
        Assert.True(first.Success, first.ErrorMessage);

        _settingsStore.Update(s => s.PrtgTimeoutSeconds = 1);
        _server.HangWhen = r => r.Content == "devices";
        var stopwatch = Stopwatch.StartNew();
        var (second, console) = await SyncAsync().WaitAsync(TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        Assert.False(second.Success);
        Assert.Contains("階段失敗", second.ErrorMessage);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0.5, 20);       // 約等於逾時秒數，不是無限等待
        Assert.True(console.Any("裝置結構同步失敗"), "輸出應說明裝置階段失敗：\n" + string.Join("\n", console.Lines));
        // 現況：輸出的原因是 HttpClient 的英文訊息「The operation was canceled.」，沒有「逾時」字樣（見回報）；
        // 失敗種類則有正確分類為 Timeout（分頁重試與暫停閘靠它判斷）。
        using (var client = PrtgClientFactory.Create(_settingsStore.Get(), null, _budget))
        {
            var timeout = await Assert.ThrowsAsync<PrtgClientException>(() =>
                client.GetJsonAsync("api/table.json?content=devices&columns=objid&count=1"));
            Assert.Equal(PrtgFailureKind.Timeout, timeout.Kind);
        }
        Assert.Equal(5, _backend.PrtgStore().GetAllDevices().Count);
        Assert.Equal(MatchedSensors.Length, _backend.PrtgStore().GetAllSensors().Count);
        // 手動同步路徑不開啟分頁重試：逾時的 devices 只送一次
        Assert.Equal(3, _server.Requests.Count(r => r.Content == "devices"));   // 第一趟 1 次＋第二趟 1 次＋上面直接探的 1 次
    }

    /// <summary>取消訊號能讓卡住的請求立刻離開（不必等逾時）。</summary>
    [Fact]
    public async Task 逾時_取消訊號讓不回應的請求離開()
    {
        _server.HangWhen = r => r.Content == "devices";
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SyncAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"取消後應很快離開，實際 {stopwatch.Elapsed.TotalSeconds:0.0} 秒（逾時設定為 30 秒）");
        Assert.Empty(_backend.PrtgStore().GetAllDevices());
    }

    /// <summary>sensors 一律回空陣列：不清除既有感測器鏡像（CLAUDE.md「不在不可信範圍做清除」）。</summary>
    [Fact]
    public async Task 空回應_sensors全部回空陣列_不清除既有鏡像()
    {
        var (first, _) = await SyncAsync();
        Assert.True(first.Success, first.ErrorMessage);
        var store = _backend.PrtgStore();
        var before = store.GetAllSensors().Select(s => (s.Objid, s.Name, s.SensorType)).Order().ToList();

        _server.EmptySensors = true;
        var (second, console) = await SyncAsync();

        Assert.Equal(before, store.GetAllSensors().Select(s => (s.Objid, s.Name, s.SensorType)).Order().ToList());
        Assert.Equal(0, second.Sensors);
        Assert.True(console.Any("寫入/更新 0 個感測器"));
        Assert.Equal(5, store.GetAllDevices().Count);
        Assert.Equal(_server.Messages.Count, store.GetStateChanges(Day, Today.AddDays(1)).Count);

        // C16：同步仍算完成，但結果多一則「感測器全部回空」警告（保護行為不變，只出聲）；正常那一趟沒有警告
        Assert.Empty(first.Warnings);
        var warning = Assert.Single(second.Warnings);
        Assert.Equal(PrtgSyncWarning.SensorsAllEmpty, warning.Code);
        Assert.Contains("沒有回傳任何感測器", warning.Message);
        Assert.True(console.Any("[警告]"), "主控台也要看得到警告");
    }

    /// <summary>單台裝置回空：其餘裝置照常刷新、空的那台給一趟寬限，不被當成已刪除。</summary>
    [Fact]
    public async Task 空回應_單台裝置sensors回空_該台感測器給寬限保留()
    {
        var (first, _) = await SyncAsync();
        Assert.True(first.Success, first.ErrorMessage);
        SetScopeBaseline(3);   // 有基準才會走到過期感測器清除，寬限才有意義
        var store = _backend.PrtgStore();

        _server.EmptySensorDevices.Add(DbDevice);
        var (second, console) = await SyncAsync();

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(MatchedSensors.Length, store.GetAllSensors().Count);
        Assert.Equal(3, store.GetAllSensors().Count(s => s.DeviceObjid == DbDevice));
        Assert.True(console.Any("先保留一趟"), "回 0 顆的裝置應出聲說明寬限：\n" + string.Join("\n", console.Lines));
        // 只有單台回空不是「全部回空」：不附警告
        Assert.DoesNotContain(second.Warnings, w => w.Code == PrtgSyncWarning.SensorsAllEmpty);
    }

    /// <summary>權限縮小到只剩 2 台（本趟沒出現的 3 台超過鏡像一半）：一台都不刪，鏡像、對應、範圍維持。</summary>
    [Fact]
    public async Task 範圍縮小_權限縮小使裝置少於一半_不清除裝置與感測器鏡像()
    {
        var (first, _) = await SyncAsync();
        Assert.True(first.Success, first.ErrorMessage);
        SetScopeBaseline(3);
        var store = _backend.PrtgStore();

        _server.VisibleDeviceIds = new HashSet<long> { AppDevice, EdgeDevice1 };
        var (second, console) = await SyncAsync();

        Assert.True(console.Any("疑似帳號權限或查詢範圍變動"), "應說明疑似權限變動、本趟不清除：\n" + string.Join("\n", console.Lines));
        Assert.True(console.Any("先保留一趟"), "看不到的兩台裝置回 0 顆感測器，鏡像裡原有的先給寬限：\n" + string.Join("\n", console.Lines));
        Assert.Equal(5, store.GetAllDevices().Count);
        Assert.Equal(MatchedSensors.Length, store.GetAllSensors().Count);
        Assert.Equal(_server.Messages.Count, store.GetStateChanges(Day, Today.AddDays(1)).Count);
        Assert.Equal(MatchedDevices.Order(), ComputeScope().DeviceObjids.Order());
        // 同步本身仍算完成（沒有階段失敗）
        Assert.True(second.Success, second.ErrorMessage);
        // 這一趟觸發安全保險、一台都沒清：不是「已對應主機的裝置被清除」，不附這則警告
        Assert.DoesNotContain(second.Warnings, w => w.Code == PrtgSyncWarning.MappedDevicesPurged);
    }

    /// <summary>
    /// 現行規則：本趟沒出現的裝置「不超過鏡像一半」就照刪（stale×2 ≤ total）。5 台縮成 3 台時 2 台被刪，
    /// 其中包含一台已對應主機的裝置——依 PRTG-SPEC §3c 這是設計（PRTG 端不見了就視為已刪除），這裡記錄現況。
    /// </summary>
    [Fact]
    public async Task 範圍縮小_少於一半時依規則清除該裝置_現況()
    {
        var (first, _) = await SyncAsync();
        Assert.True(first.Success, first.ErrorMessage);
        SetScopeBaseline(3);
        var store = _backend.PrtgStore();

        _server.VisibleDeviceIds = new HashSet<long> { AppDevice, DbDevice, EdgeDevice1 };
        var (second, console) = await SyncAsync();

        Assert.True(second.Success, second.ErrorMessage + "\n" + string.Join("\n", console.Lines));
        Assert.Equal(new[] { AppDevice, DbDevice, EdgeDevice1 }, store.GetAllDevices().Select(d => d.Objid).Order());
        Assert.Equal(2, store.GetHostMapForDay(Today, PrtgHostMapLookup.LookbackDays).Rows.Count(r => r.MapStatus == PrtgMapStatus.Ok));
        Assert.Equal(new[] { AppDevice, DbDevice }, ComputeScope().DeviceObjids.Order());
        // 範圍縮小到 2 台後（已有基準、縮小量未超過 max(30%, 50 台)），被移出範圍那台的感測器鏡像一併清掉
        Assert.DoesNotContain(store.GetAllSensors(), s => s.DeviceObjid == WebDevice);
        Assert.Equal(7, store.GetAllSensors().Count);
        Assert.True(console.Any("已清除 2 台 PRTG 端已不存在的裝置"));

        // C16：被清除的兩台裡有已對應主機的裝置 → 同步結果多一則警告（清除規則不變：上面的鏡像斷言與修正前相同）
        var warning = Assert.Single(second.Warnings, w => w.Code == PrtgSyncWarning.MappedDevicesPurged);
        Assert.Contains("已對應主機的 PRTG 裝置", warning.Message);
        Assert.True(warning.Message.Length <= PrtgSyncWarning.MaxMessageLength);
    }

    /// <summary>
    /// 時區跨日：datetime_raw 是 UTC 的時段結束。找一個「UTC 日期與本地日期不同」的小時，值要歸到本地當天。
    /// 測試機時區為 UTC 時沒有這種小時，退化成一般斷言。
    /// </summary>
    [Fact]
    public async Task 時區跨日_datetime_raw落在UTC前後一天_歸到本地當天()
    {
        var (sync, _) = await SyncAsync();
        Assert.True(sync.Success, sync.ErrorMessage);

        DateTime? crossing = null;
        for (var h = 0; h < 24 && crossing == null; h++)
        {
            var start = Day.AddHours(h);
            var endUtc = DateTime.SpecifyKind(start.AddHours(1), DateTimeKind.Local).ToUniversalTime();
            if (endUtc.Date != start.Date) crossing = start;
        }
        var target = crossing ?? Day.AddHours(1);
        _server.HistoricValue = (id, hour) => id == AppDisk && hour == target ? 7.25 : null;

        var fetch = NewFetchService(new ListConsole(), out var client);
        using (client)
        {
            var (written, failed) = await fetch.FetchValuesForSensorsAsync(Day, [AppDisk], 1, CancellationToken.None);
            Assert.Equal(1, written);
            Assert.Equal(0, failed);
        }

        var row = Assert.Single(_backend.PrtgStore().GetValuesForSensor(AppDisk, Day.AddDays(-2), Today.AddDays(2)));
        Assert.Equal(target, row.PeriodStart);
        Assert.Equal(Day, row.PeriodStart.Date);
        Assert.Equal(7.25, row.AvgValue!.Value, 6);
        if (crossing != null)
        {
            var rawUtc = DateTime.FromOADate(FakePrtgServer.ToOleUtc(target.AddHours(1)));
            Assert.NotEqual(row.PeriodStart.Date, rawUtc.Date);   // 確認測的真的是跨日的情形
        }
    }

    /// <summary>messages 的 objid 是發出訊息的 sensor：同一 sensor 同一天多筆全部保留，不得以 objid 去重。</summary>
    [Fact]
    public async Task messages_同一sensor同一天多筆_全部保留()
    {
        for (var i = 0; i < 6; i++)
            _server.Messages.Add(new(WebPing, Day.AddHours(4 + i).AddMinutes(i * 7), i % 2 == 0 ? "Down" : "Up", i % 2 == 0 ? 5 : 3, $"flap #{i}"));

        var (sync, _) = await SyncAsync();

        Assert.True(sync.Success, sync.ErrorMessage);
        var changes = _backend.PrtgStore().GetStateChanges(Day, Today.AddDays(1));
        var web = changes.Where(c => c.SensorObjid == WebPing).OrderBy(c => c.ChangedAt).ToList();
        Assert.Equal(6, web.Count);
        Assert.Equal(_server.Messages.Count, changes.Count);
        var expected = _server.Messages.Where(m => m.SensorObjid == WebPing).OrderBy(m => m.LocalTime).ToList();
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Status, web[i].Status);
            Assert.Equal(expected[i].Text, web[i].Message);
            Assert.True(Math.Abs((web[i].ChangedAt - expected[i].LocalTime).TotalSeconds) < 2);
        }
    }

    /// <summary>
    /// 現況：PRTG 在非英文介面（繁中）把狀態字串回成「警告」，同列 <c>status_raw</c>=4 是語言無關的數字，但正式碼完全不讀 status_raw；
    /// 狀態比對一律是英文前綴（PrtgSensorStatuses.IsWarning／IsDown／IsUp）。因此中文「警告」認不出是 Warning，warning 規則不命中。
    /// 同一次評估中，英文 Warning 的磁碟照常命中作為對照。這是與「用 raw 值不受語言影響」的期望不一致之處，本測試只記錄現況、不修正。
    /// </summary>
    [Fact]
    public async Task 非英文狀態文字_現況_中文警告認不出Warning_status_raw不被使用()
    {
        _server.Messages.Add(new(AppMemory, Day.AddHours(6), "警告", 4, "可用記憶體偏低"));
        var (sync, _) = await SyncAsync();
        Assert.True(sync.Success, sync.ErrorMessage);
        var store = _backend.PrtgStore();
        var changes = store.GetStateChanges(Day, Today.AddDays(1));

        Assert.Contains(changes, c => c.SensorObjid == AppMemory && c.Status == "警告");   // 字串原樣落盤
        Assert.False(PrtgSensorStatuses.IsWarning("警告"));
        Assert.True(PrtgSensorStatuses.IsWarning("Warning"));

        var sensorStatuses = store.GetSensorStatuses();
        var findings = PrtgRuleEvaluator.Evaluate(
            Day, changes,
            sensorStatuses.ToDictionary(s => s.Objid, s => s.DeviceObjid),
            sensorStatuses.Select(s => new PrtgSensorStatusInput(s.Objid, s.DeviceObjid, s.Status, s.SensorType, s.Category)).ToList(),
            KnownIssueSeed.CreateRules(),
            sensorStatuses.ToDictionary(s => s.Objid, s => s.SensorName),
            store.GetAllDevices().ToDictionary(d => d.Objid, d => d.Name),
            includeSilent: false);

        Assert.Contains(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleWarning && f.SensorObjid == AppDisk);   // 英文：命中
        Assert.DoesNotContain(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleWarning && f.SensorObjid == AppMemory);   // 中文：現況認不出
    }
}
