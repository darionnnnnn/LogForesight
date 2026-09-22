using System.Net;
using System.Security.Claims;
using System.Text;
using LogForesight.Core;
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
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 監看裝置範圍收斂與清除保護（docs/PRTG-SPEC.md §3c）：
/// 人工對應／守門的收錄規則、指定主機更新的 partial 範圍、感測器鏡像清除的保護、
/// 範圍外數值與狀態變更清除的五道保護，以及維護頁預覽與確認的一致性。
/// </summary>
public class PrtgScopeProtectionTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly long _hostA;
    private readonly long _hostB;
    private readonly long _hostInactive;

    private const long MissingHostId = 999_999;
    private static readonly DateTime From = new(2000, 1, 1);
    private static readonly DateTime To = new(2100, 1, 1);

    public PrtgScopeProtectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-scope-protect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _hostA = _hosts.Upsert(new WebHost { HostName = "srv-a", IpAddress = "10.0.0.1", Active = true, Source = "netiq" }).HostId;
        _hostB = _hosts.Upsert(new WebHost { HostName = "srv-b", IpAddress = "10.0.0.2", Active = true, Source = "netiq" }).HostId;
        _hostInactive = _hosts.Upsert(new WebHost { HostName = "srv-off", IpAddress = "10.0.0.30", Active = false, Source = "netiq" }).HostId;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private EfPrtgStore Store => _backend.PrtgStore();

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private static PrtgHostMapRow MapRow(long deviceObjid, string ip, long? hostId, string status) => new()
    {
        MapDate = DateTime.Today,
        DeviceObjid = deviceObjid,
        Ip = ip,
        HostId = hostId,
        HostName = hostId.HasValue ? $"srv-{hostId}" : null,
        MapStatus = status,
        CreatedAt = DateTime.Now
    };

    /// <summary>
    /// 裝置：1→A ok、2→B ok、3 conflict（mapper 指到 B）、4 conflict（無 HostId、IP 屬 A）、
    /// 5 人工對應（目標依參數）、7 Sentinel 所在、8 corehealth 所在、9 PRTG 主機本身（守門自動偵測）。
    /// </summary>
    private void SeedMirror(long manualTarget)
    {
        var store = Store;
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1, Name = "D1", Ip = "10.0.0.1" },
            new PrtgDeviceRow { Objid = 2, Name = "D2", Ip = "10.0.0.2" },
            new PrtgDeviceRow { Objid = 3, Name = "D3", Ip = "10.0.0.2" },
            new PrtgDeviceRow { Objid = 4, Name = "D4", Ip = "10.0.0.1" },
            new PrtgDeviceRow { Objid = 5, Name = "D5", Ip = "10.0.0.5" },
            new PrtgDeviceRow { Objid = 7, Name = "D7", Ip = "10.0.0.7" },
            new PrtgDeviceRow { Objid = 8, Name = "D8", Ip = "10.0.0.8" },
            new PrtgDeviceRow { Objid = 9, Name = "D9", Ip = "10.0.0.9" }
        }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 801, DeviceObjid = 8, Name = "Core Health", SensorType = "corehealth" }
        }, now.AddDays(-1));
        store.ReplaceHostMapForDate(DateTime.Today, new List<PrtgHostMapRow>
        {
            MapRow(1, "10.0.0.1", _hostA, PrtgMapStatus.Ok),
            MapRow(2, "10.0.0.2", _hostB, PrtgMapStatus.Ok),
            MapRow(3, "10.0.0.2", _hostB, PrtgMapStatus.Conflict),
            MapRow(4, "10.0.0.1", null, PrtgMapStatus.Conflict)
        });
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 5, HostId = manualTarget, Note = "人工", CreatedBy = "admin", CreatedAt = now
        });
    }

    private static SystemSettings Settings(bool guardEnabled) => new()
    {
        PrtgUrl = "https://10.0.0.9",
        PrtgResourceGuardEnabled = guardEnabled
    };

    private static readonly List<Sentinel> Sentinels = new() { new() { Name = "S1", BaseUrl = "https://10.0.0.7:8443" } };

    private PrtgScopeResult Compute(SystemSettings settings, IReadOnlyCollection<long>? hostIds, IRunConsole? console = null)
    {
        var store = Store;
        return PrtgScopeDevices.Compute(store, _hosts, new PrtgMirrorGuardSource(store), settings, Sentinels,
            console ?? new TestConsole(), new PrtgAddressResolver(), hostIds);
    }

    private long ManualTarget(string kind) => kind switch
    {
        "active" => _hostA,
        "inactive" => _hostInactive,
        _ => MissingHostId
    };

    // ── 1. 範圍計算 ──

    [Theory]
    [InlineData(true, "active")]
    [InlineData(true, "inactive")]
    [InlineData(true, "missing")]
    [InlineData(false, "active")]
    [InlineData(false, "inactive")]
    [InlineData(false, "missing")]
    public void Compute_守門開關乘人工對應目標_監看與保留集合精確(bool guardEnabled, string manualKind)
    {
        SeedMirror(ManualTarget(manualKind));
        var console = new TestConsole();

        var result = Compute(Settings(guardEnabled), hostIds: null, console);

        var expectedScope = new HashSet<long> { 1, 2, 3, 4 };
        if (manualKind == "active") expectedScope.Add(5);
        var guardDevices = new HashSet<long> { 7, 8, 9 };
        if (guardEnabled) expectedScope.UnionWith(guardDevices);

        Assert.Equal(expectedScope, result.DeviceObjids.ToHashSet());
        Assert.Equal(guardEnabled ? new HashSet<long>() : guardDevices, result.PreserveDeviceObjids.ToHashSet());
        Assert.False(result.IsPartial);
        Assert.Equal(manualKind == "active" ? 0 : 1, result.ManualExcluded);
        if (manualKind == "active")
            Assert.DoesNotContain(console.Lines, l => l.Contains("未納入監看"));
        else
            Assert.Contains("人工對應有 1 筆指向已停用或不存在的主機，未納入監看。", console.Lines);
    }

    [Fact]
    public void Compute_指定主機A_只含A的裝置且為partial且不含守門()
    {
        SeedMirror(_hostA);

        var result = Compute(Settings(guardEnabled: true), hostIds: new[] { _hostA });

        // 1＝ok、4＝IP 屬 A 的 conflict、5＝人工對應到 A；B 的 2、3 與守門 7、8、9 都不在
        Assert.Equal(new HashSet<long> { 1, 4, 5 }, result.DeviceObjids.ToHashSet());
        Assert.True(result.IsPartial);
        Assert.Equal(0, result.Guard);
    }

    // ── 2. 感測器鏡像清除保護（實際跑一趟擷取）──

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        public required Func<HttpRequestMessage, HttpResponseMessage> Responder { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Responder(request));
    }

    private static long? IdQuery(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]id=(\d+)(&|$)");
        return m.Success ? long.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// PRTG 替身：devices 回 <paramref name="devices"/>（可為空）；帶 id= 的 sensors 只回該裝置的感測器；messages 回空。
    /// </summary>
    private static PrtgClient FakeClient(IReadOnlyList<(long Objid, string Ip)> devices, IReadOnlyList<(long Objid, long Device)> sensors)
    {
        var handler = new StubHandler
        {
            Responder = req =>
            {
                var url = req.RequestUri!.ToString();
                var first = url.Contains("start=0&") || url.EndsWith("start=0");
                if (url.Contains("content=devices"))
                {
                    var rows = first ? devices : Array.Empty<(long, string)>();
                    return Json($"{{\"treesize\":{devices.Count},\"devices\":[" + string.Join(",", rows.Select(d =>
                        $"{{\"objid\":{d.Item1},\"device\":\"D{d.Item1}\",\"host\":\"{d.Item2}\",\"group\":\"G\",\"status\":\"Up\",\"paused\":false,\"dependency\":0}}")) + "]}");
                }
                if (url.Contains("content=sensors"))
                {
                    var id = IdQuery(url);
                    var rows = sensors.Where(s => !id.HasValue || s.Device == id.Value).ToList();
                    if (!first) rows.Clear();
                    return Json($"{{\"treesize\":{rows.Count},\"sensors\":[" + string.Join(",", rows.Select(s =>
                        $"{{\"objid\":{s.Objid},\"parentid\":{s.Device},\"sensor\":\"S{s.Objid}\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false}}")) + "]}");
                }
                if (url.Contains("content=messages")) return Json("{\"treesize\":0,\"messages\":[]}");
                return Json("{}", HttpStatusCode.NotFound);
            }
        };
        return new PrtgClient("https://prtg.example.com", "token123", 30, true, handler, PrtgAuthModes.Token, "", "", "");
    }

    private PrtgFetchService FetchService(PrtgClient client, IRunConsole console) =>
        new(client, Store, new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)), console, new Dictionary<string, string>());

    private void SeedOldSensors(params (long Objid, long Device)[] sensors) =>
        Store.UpsertSensors(sensors.Select(s => new PrtgSensorRow
        {
            Objid = s.Objid, DeviceObjid = s.Device, Name = $"Old-{s.Objid}", SensorType = "ping"
        }).ToList(), DateTime.Now.AddDays(-1));

    private List<long> MirrorSensors() => Store.GetAllSensors().Select(s => s.Objid).OrderBy(id => id).ToList();

    [Fact]
    public async Task 感測器清除_partial範圍擷取後其他裝置的感測器列數不變()
    {
        SeedMirror(_hostA);
        SeedOldSensors((201, 1), (202, 2), (203, 3));
        var before = Store.GetAllSensors().Count(s => s.DeviceObjid != 1);
        var console = new TestConsole();
        var service = FetchService(FakeClient(Array.Empty<(long, string)>(), new[] { (201L, 1L), (204L, 1L) }), console);
        var settings = Settings(guardEnabled: false);

        var result = await service.FetchDayAsync(DateTime.Today, 2, CancellationToken.None,
            _ => Compute(settings, hostIds: new[] { _hostA }), fetchValues: false);

        Assert.True(result.Scope!.IsPartial);
        Assert.Equal(before, Store.GetAllSensors().Count(s => s.DeviceObjid != 1));
        Assert.Contains(202L, MirrorSensors());
        Assert.Contains(203L, MirrorSensors());
        Assert.Contains("[階段 2/4] 本趟只處理部分主機，不清除感測器鏡像。", console.Lines);
    }

    [Fact]
    public async Task 感測器清除_守門關閉時守門裝置的感測器仍在()
    {
        SeedMirror(_hostA);
        SeedOldSensors((299, 1), (701, 7));
        var console = new TestConsole();
        var service = FetchService(FakeClient(new[] { (1L, "10.0.0.1"), (2L, "10.0.0.2"), (3L, "10.0.0.3"),
            (4L, "10.0.0.4"), (5L, "10.0.0.5"), (7L, "10.0.0.7"), (8L, "10.0.0.8"), (9L, "10.0.0.9") },
            new[] { (201L, 1L), (202L, 2L), (203L, 3L), (205L, 5L) }), console);
        var settings = Settings(guardEnabled: false);
        var expectedScope = Compute(settings, hostIds: null);
        SetBaseline(expectedScope.DeviceObjids.Count);

        await service.FetchDayAsync(DateTime.Today, 2, CancellationToken.None,
            _ => Compute(settings, hostIds: null), fetchValues: false);

        var mirror = MirrorSensors();
        // 299：監看裝置 1 上 PRTG 已不回的舊感測器 → 照常清；701（Sentinel 裝置 7）、801（corehealth 裝置 8）→ 保留
        Assert.DoesNotContain(299L, mirror);
        Assert.Contains(701L, mirror);
        Assert.Contains(801L, mirror);
    }

    // ── 2. 範圍外數值與狀態變更清除 ──

    /// <summary>
    /// 數值：201（裝置 1，範圍內）、901（裝置 50，範圍外）、777（鏡像已沒有的 sensor）、801（守門裝置 8，保留）；
    /// 狀態變更：202（裝置 2，範圍內）、901。
    /// </summary>
    private void SeedData()
    {
        var store = Store;
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 201, DeviceObjid = 1, Name = "S201", SensorType = "ping" },
            new PrtgSensorRow { Objid = 202, DeviceObjid = 2, Name = "S202", SensorType = "ping" },
            new PrtgSensorRow { Objid = 901, DeviceObjid = 50, Name = "S901", SensorType = "ping" }
        }, DateTime.Now);
        store.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 50, Name = "Outside-50", Ip = "10.9.9.9" } }, DateTime.Now);
        var hour = DateTime.Today.AddDays(-1);
        store.UpsertValues(new[] { 201L, 201L, 901L, 901L, 777L, 801L }
            .Select((s, i) => new PrtgValueRow { SensorObjid = s, PeriodStart = hour.AddHours(i), AvgValue = 1, Quality = "ok" })
            .ToList());
        store.AppendStateChanges(new[] { 202L, 901L, 901L }
            .Select((s, i) => new PrtgStateChangeRow { SensorObjid = s, ChangedAt = hour.AddHours(i), Status = "Down", Quality = "ok" })
            .ToList());
    }

    private (int Values, int StateChanges) Counts() =>
        (Store.GetValues(From, To).Count, Store.GetStateChanges(From, To).Count);

    private void SetBaseline(int deviceCount) =>
        Store.ScopeBaseline().Update(b => { b.DeviceCount = deviceCount; b.At = DateTime.Now.AddDays(-1); });

    public static IEnumerable<object[]> ProtectionCases() => new[]
    {
        new object[] { "計算擲例外" },
        new object[] { "範圍空" },
        new object[] { "partial" },
        new object[] { "縮小超過門檻" },
        new object[] { "無基準" },
        new object[] { "裝置鏡像未更新" }
    };

    [Theory]
    [MemberData(nameof(ProtectionCases))]
    public void 範圍外清除_任一保護成立時數值與狀態變更列數不變(string protection)
    {
        SeedMirror(_hostA);
        SeedData();
        var full = Compute(Settings(guardEnabled: false), hostIds: null);
        PrtgScopeResult? scope = protection switch
        {
            "計算擲例外" => null,
            // 主機主檔暫時讀到空清單、守門未啟用時的形狀：監看裝置空、保留集合仍有守門裝置
            "範圍空" => new PrtgScopeResult(new HashSet<long>(), 0, 0, 0, 0) { PreserveDeviceObjids = full.PreserveDeviceObjids },
            "partial" => Compute(Settings(guardEnabled: false), hostIds: new[] { _hostA }),
            _ => full
        };
        if (protection == "縮小超過門檻")
            SetBaseline(full.DeviceObjids.Count + 51); // 門檻 max(30%, 50)＝50：少 51 台就擋
        else if (protection != "無基準")
            SetBaseline(full.DeviceObjids.Count);
        var before = Counts();
        var console = new TestConsole();

        var purged = PrtgScopePurge.RunAfterStructureSync(Store, scope, devicesRefreshed: protection != "裝置鏡像未更新", console);

        Assert.False(purged);
        Assert.Equal(before, Counts());
        if (protection == "縮小超過門檻")
        {
            var baseline = Store.ScopeBaseline().Get();
            Assert.Contains($"監看裝置數由 {full.DeviceObjids.Count + 51} 降為 {full.DeviceObjids.Count}", baseline.BlockedReason);
            Assert.Contains(console.Lines, l => l.Contains("疑似主機清單或 DNS 異常"));
        }
        if (protection == "無基準")
            Assert.False(Store.ScopeBaseline().Get().HasBaseline);
    }

    [Fact]
    public void 範圍外清除_縮小未超過門檻時照常清除()
    {
        SeedMirror(_hostA);
        SeedData();
        var scope = Compute(Settings(guardEnabled: false), hostIds: null);
        SetBaseline(scope.DeviceObjids.Count + 50); // 恰等於門檻不擋

        Assert.True(PrtgScopePurge.RunAfterStructureSync(Store, scope, devicesRefreshed: true, new TestConsole()));
    }

    [Fact]
    public void 範圍外清除_正常情況刪範圍外列_保留範圍內與保留裝置_更新基準()
    {
        SeedMirror(_hostA);
        SeedData();
        var scope = Compute(Settings(guardEnabled: false), hostIds: null);
        SetBaseline(scope.DeviceObjids.Count);
        Store.ScopeBaseline().Update(b => b.BlockedReason = "舊的擋下原因");
        var startedAt = DateTime.Now;

        var purged = PrtgScopePurge.RunAfterStructureSync(Store, scope, devicesRefreshed: true, new TestConsole());

        Assert.True(purged);
        Assert.Equal(new long[] { 201, 201, 801 }, Store.GetValues(From, To).Select(v => v.SensorObjid).OrderBy(x => x).ToArray());
        Assert.Equal(new long[] { 202 }, Store.GetStateChanges(From, To).Select(c => c.SensorObjid).ToArray());
        var baseline = Store.ScopeBaseline().Get();
        Assert.Equal(scope.DeviceObjids.Count, baseline.DeviceCount);
        Assert.True(baseline.At >= startedAt);
        Assert.Null(baseline.BlockedReason);
    }

    [Fact]
    public async Task 範圍外清除_結構同步全部成功後自動執行()
    {
        SeedMirror(_hostA);
        SeedData();
        SetBaseline(3);
        var devices = new List<(long, string)>
        {
            (1, "10.0.0.1"), (2, "10.0.0.2"), (3, "10.0.0.2"), (4, "10.0.0.1"), (5, "10.0.0.5"),
            (7, "10.0.0.7"), (8, "10.0.0.8"), (9, "10.0.0.9"), (50, "10.9.9.9")
        };
        var console = new TestConsole();
        var service = FetchService(FakeClient(devices, new[] { (201L, 1L), (202L, 2L) }), console);
        var settings = Settings(guardEnabled: false);
        var store = Store;

        var status = await PrtgStructureSyncRunner.RunAsync(service, store, _hosts, new PrtgAddressResolver(), 2, console,
            CancellationToken.None, new PrtgMirrorGuardSource(store), settings, Sentinels);

        Assert.True(status.Success, status.ErrorMessage);
        Assert.DoesNotContain(901L, Store.GetValues(From, To).Select(v => v.SensorObjid));
        Assert.DoesNotContain(777L, Store.GetValues(From, To).Select(v => v.SensorObjid));
        Assert.Contains(201L, Store.GetValues(From, To).Select(v => v.SensorObjid));
        Assert.Equal(new long[] { 202 }, Store.GetStateChanges(From, To).Select(c => c.SensorObjid).ToArray());
        Assert.Contains(console.Lines, l => l.StartsWith("[範圍外清除] 已清除"));
    }

    // ── 預覽與確認 ──

    private (SettingsController Controller, RecordingAuditService Audit, SchedulerRunState Scheduler) NewController()
    {
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        settingsStore.Update(s =>
        {
            s.PrtgUrl = "https://10.0.0.9";
            s.PrtgResourceGuardEnabled = false;
        });
        var scheduler = new SchedulerRunState();
        var backfill = new PrtgBackfillService(settingsStore, _backend, new PrtgBackfillRunState(), new PrtgProbeRunState(),
            _hosts, scheduler, new PrtgStructureSyncRunState(), new FakeSentinelStore(),
            new PrtgStructureSyncService(settingsStore, _backend, new PrtgStructureSyncRunState(), scheduler, _hosts, new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey)), new PrtgBackfillRunState(), new FakeSentinelStore(), new DataVersionStamp()));
        var audit = new RecordingAuditService();
        var controller = new SettingsController(
            new StubSettingsService(), new AiUsageStore(_backend.Blob("ai_usage")), audit,
            prtgBackfill: backfill, backend: _backend, hosts: _hosts);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Name, "test-admin"),
                    new Claim(JwtTokenService.AccountClaim, "test-admin")
                }, "TestAuth"))
            }
        };
        return (controller, audit, scheduler);
    }

    [Fact]
    public void 預覽列數等於確認實際刪除列數_且寫稽核與基準()
    {
        SeedMirror(_hostA);
        SeedData();
        var (controller, audit, _) = NewController();
        var before = Counts();

        var preview = controller.PreviewPrtgScopePurge().Data!;
        var result = controller.ConfirmPrtgScopePurge().Data!;
        var after = Counts();

        Assert.True(preview.Success, preview.ErrorMessage);
        // 901（裝置 50）與 777（鏡像已無）的數值 3 筆、901 的狀態變更 2 筆
        Assert.Equal(3, preview.Values);
        Assert.Equal(2, preview.StateChanges);
        Assert.Equal(1, preview.AffectedDevices);
        Assert.Equal(new[] { "Outside-50（50）" }, preview.TopDeviceNames);
        Assert.Equal(1, preview.UnknownSensors);
        Assert.Equal(preview.Values, result.Values);
        Assert.Equal(preview.StateChanges, result.StateChanges);
        Assert.Equal(before.Values - after.Values, result.Values);
        Assert.Equal(before.StateChanges - after.StateChanges, result.StateChanges);
        Assert.Single(audit.Entries, e => e.Action == AuditActions.PrtgScopePurge);
        Assert.Equal(preview.MonitoredDevices, Store.ScopeBaseline().Get().DeviceCount);

        // 清完再預覽：沒有可清的列
        var again = controller.PreviewPrtgScopePurge().Data!;
        Assert.Equal(0, again.Values + again.StateChanges);
    }

    [Fact]
    public void 確認清除_取數執行中回409且不刪()
    {
        SeedMirror(_hostA);
        SeedData();
        var (controller, _, scheduler) = NewController();
        Assert.True(scheduler.TryBeginRun("manual", out _));
        var before = Counts();

        var ex = Assert.Throws<DomainException>(() => controller.ConfirmPrtgScopePurge());

        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
        Assert.Equal(before, Counts());
    }

    private sealed class StubSettingsService : ISystemSettingsService
    {
        public SystemSettingsDto Get() => new();
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
