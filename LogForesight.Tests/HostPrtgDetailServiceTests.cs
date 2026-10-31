﻿using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機詳情 PRTG 對應端點（HostDetailController.Prtg → HostPrtgDetailService）的黃金值與授權順序。
/// 黃金值字串是在業務邏輯還在控制器裡時，對同一份種子資料取得的輸出；
/// 邏輯搬進 Service 後必須逐字相同——任何欄位、順序或值的變動都會在這裡現形。
/// </summary>
public class HostPrtgDetailServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    /// <summary>記錄呼叫順序的可見性替身：用來釘住「授權檢查在任何資料讀取之前」</summary>
    private sealed class RecordingVisibility : IVisibilityService
    {
        private readonly long _visibleHostId;
        private readonly string? _ipAddress;
        private readonly bool _caseGrantOnly;
        public List<string> Calls { get; } = new();

        public RecordingVisibility(long visibleHostId, string? ipAddress = null, bool caseGrantOnly = false)
        {
            _visibleHostId = visibleHostId;
            _ipAddress = ipAddress;
            _caseGrantOnly = caseGrantOnly;
        }

        public void EnsureVisible(long hostId)
        {
            Calls.Add(nameof(EnsureVisible));
            if (hostId != _visibleHostId)
                throw DomainException.NotFound("找不到這台主機，或您沒有檢視權限。");
        }

        public bool IsCaseGrantOnly(long hostId)
        {
            Calls.Add(nameof(IsCaseGrantOnly));
            return _caseGrantOnly && hostId == _visibleHostId;
        }

        public List<WebHost> GetVisibleHosts()
        {
            Calls.Add(nameof(GetVisibleHosts));
            return new() { new WebHost { HostId = _visibleHostId, IpAddress = _ipAddress } };
        }

        public IReadOnlySet<long> GetVisibleHostIds() => new HashSet<long> { _visibleHostId };
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => throw new NotSupportedException();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => throw new NotSupportedException();
        public IReadOnlyList<string> GetCaseGrantHostNames() => throw new NotSupportedException();
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => GetVisibleHostIds();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private HostDetailController CreateController(IVisibilityService visibility, EfPrtgStore? store = null)
    {
        store ??= new EfPrtgStore(_fx.NewContext);
        var assessment = new PrtgValueAssessment(store, new Dictionary<string, PrtgValueSemantic>());
        return new HostDetailController(null!, new HostPrtgDetailService(store, visibility, assessment, new FakeRuleStore()));
    }

    private void SeedGolden()
    {
        var now = new DateTime(2026, 3, 3, 8, 0, 0);
        var yesterday = DateTime.Today.AddDays(-1);
        using var ctx = _fx.NewContext();
        ctx.PrtgDevices.Add(new PrtgDeviceRow { Objid = 1001, Name = "SRV-10", Ip = "10.0.0.5", SyncedAt = now, CreatedAt = now });
        ctx.PrtgDevices.Add(new PrtgDeviceRow { Objid = 1002, Name = "SRV-10-b", Ip = "10.0.0.5", SyncedAt = now, CreatedAt = now });
        ctx.PrtgSensors.Add(new PrtgSensorRow
        {
            Objid = 2001, DeviceObjid = 1001, Name = "Memory", SensorType = "SNMP Memory",
            Category = PrtgSensorCategories.Memory, Paused = false, Status = "Up", SyncedAt = now, CreatedAt = now
        });
        ctx.PrtgSensors.Add(new PrtgSensorRow
        {
            Objid = 2002, DeviceObjid = 1001, Name = "Ping", SensorType = "Ping",
            Category = "network", Paused = false, Status = "Up", SyncedAt = now, CreatedAt = now
        });
        ctx.PrtgSensors.Add(new PrtgSensorRow
        {
            Objid = 2003, DeviceObjid = 1001, Name = "Unknown CPU", SensorType = "custom-cpu-unknown",
            Category = PrtgSensorCategories.Cpu, Paused = true, Status = "Paused", SyncedAt = now, CreatedAt = now
        });
        ctx.PrtgSensors.Add(new PrtgSensorRow
        {
            Objid = 2004, DeviceObjid = 1002, Name = "Disk C", SensorType = "SNMP Disk Free",
            Category = PrtgSensorCategories.Disk, Paused = false, Status = "Warning", SyncedAt = now, CreatedAt = now
        });
        ctx.PrtgHostMaps.Add(new PrtgHostMapRow
        {
            MapDate = yesterday, DeviceObjid = 1001, Ip = "10.0.0.5", HostId = 10,
            HostName = "SRV-10", MapStatus = PrtgMapStatus.Ok, Note = "自動對應", CreatedAt = now
        });
        ctx.PrtgHostMaps.Add(new PrtgHostMapRow
        {
            MapDate = yesterday, DeviceObjid = 1002, Ip = "10.0.0.5", HostId = 10,
            HostName = "SRV-10", MapStatus = PrtgMapStatus.Ok, Note = "同 IP 第二台", CreatedAt = now
        });
        ctx.PrtgIpExcludes.Add(new PrtgIpExcludeRow { Ip = " 10.0.0.5 ", CreatedAt = now });
        for (var d = 0; d < 10; d++)
        for (var h = 0; h < 24; h++)
        {
            ctx.PrtgValues.Add(new PrtgValueRow
            {
                SensorObjid = 2001,
                PeriodStart = yesterday.AddDays(-d).AddHours(h),
                AvgValue = 20.0 + d * 0.5 + h * 0.01,
                Coverage = 100,
                Quality = PrtgDataQuality.Ok,
                CreatedAt = now
            });
        }
        ctx.SaveChanges();
    }

    // 黃金值：抽離前對 SeedGolden 的種子資料取得（含 IP 排除、兩台 device、三種就緒度原因）
    private const string GoldenPrtgJson = """
{"success":true,"data":{"mapDate":"{MAPDATE}","devices":[{"deviceObjid":1001,"name":"SRV-10","ip":"10.0.0.5","mapStatus":"ok","note":"\u81EA\u52D5\u5C0D\u61C9","sensors":[{"objid":2001,"name":"Memory","sensorType":"SNMP Memory","category":"memory","paused":false,"status":"Up","valueReasonCode":"InsufficientDays","valueReason":"\u6709\u6548\u65E5\u4E0D\u8DB3","validDays":10,"requiredDays":28},{"objid":2002,"name":"Ping","sensorType":"Ping","category":"network","paused":false,"status":"Up","valueReasonCode":null,"valueReason":null,"validDays":null,"requiredDays":null},{"objid":2003,"name":"Unknown CPU","sensorType":"custom-cpu-unknown","category":"cpu","paused":true,"status":"Paused","valueReasonCode":"UnknownUnit","valueReason":"\u55AE\u4F4D\u672A\u77E5","validDays":0,"requiredDays":28}],"sensorCount":3},{"deviceObjid":1002,"name":"SRV-10-b","ip":"10.0.0.5","mapStatus":"ok","note":"\u540C IP \u7B2C\u4E8C\u53F0","sensors":[{"objid":2004,"name":"Disk C","sensorType":"SNMP Disk Free","category":"disk","paused":false,"status":"Warning","valueReasonCode":"InsufficientDays","valueReason":"\u6709\u6548\u65E5\u4E0D\u8DB3","validDays":0,"requiredDays":28}],"sensorCount":1}],"ipExcluded":true,"excludedIp":" 10.0.0.5 "},"error":null}
""";

    [Fact]
    public void 黃金值_主機詳情PRTG對應輸出與抽離前逐字相同()
    {
        SeedGolden();
        var visibility = new RecordingVisibility(10, ipAddress: "10.0.0.5");
        var yesterdayText = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd'T'00:00:00");

        var actual = JsonSerializer.Serialize(CreateController(visibility).Prtg(10), Json);

        Assert.Equal(GoldenPrtgJson.Replace("{MAPDATE}", yesterdayText), actual);
    }

    [Fact]
    public void 授權順序_可見性與案件授與檢查早於任何資料讀取()
    {
        // 資料庫一被碰到就爆：不可見／僅案件授與的請求，必須在碰到儲存層之前就被擋下
        var untouchable = new EfPrtgStore(() => throw new InvalidOperationException("授權前不得讀取資料"));

        var notVisible = new RecordingVisibility(10);
        var notFound = Assert.Throws<DomainException>(() => CreateController(notVisible, untouchable).Prtg(999));
        Assert.Equal(ApiErrorCodes.NotFound, notFound.Code);
        Assert.Equal(new[] { "EnsureVisible" }, notVisible.Calls);

        var grantOnly = new RecordingVisibility(10, caseGrantOnly: true);
        var forbidden = Assert.Throws<DomainException>(() => CreateController(grantOnly, untouchable).Prtg(10));
        Assert.Equal(ApiErrorCodes.Forbidden, forbidden.Code);
        Assert.Equal(new[] { "EnsureVisible", "IsCaseGrantOnly" }, grantOnly.Calls);
    }

    [Fact]
    public void 授權順序_通過後才讀主機清單與PRTG資料()
    {
        var visibility = new RecordingVisibility(10);

        CreateController(visibility).Prtg(10);

        Assert.Equal(new[] { "EnsureVisible", "IsCaseGrantOnly", "GetVisibleHosts" }, visibility.Calls);
    }

    [Fact]
    public void 主機詳情Prtg端點本文不含儲存層與評估器直接引用()
    {
        var src = File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "Controllers", "Api", "HostDetailController.cs"));
        var start = src.IndexOf("public ApiResponse<HostPrtgMappingDto> Prtg(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = src[start..];
        Assert.DoesNotContain("_prtgStore", body);
        Assert.DoesNotContain("PrtgValueAssessment", body);
    }
}

/// <summary>
/// PRTG 衝突清單分頁（SettingsController.GetPrtgHostMap → PrtgConflictQueryService）的黃金值：
/// 期望字串在邏輯還在控制器時取得，抽進 Service 後逐字相同。
/// </summary>
public class PrtgHostMapGoldenTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;

    public PrtgHostMapGoldenTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-hostmap-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        _hosts = new HostStore(_backend.Blob("hosts"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class FullVisibility(IHostStore hosts) : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => hosts.GetAll().Select(h => h.HostId).ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long id) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => null;
        public List<WebHost> GetVisibleHosts() => hosts.GetAll();
        public void EnsureVisible(long id) { }
    }

    private sealed class StubSettings : ISystemSettingsService
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

    private SettingsController CreateController() => new(
        new StubSettings(),
        new AiUsageStore(_backend.Blob("ai_usage")),
        new RecordingAuditService(),
        backend: _backend,
        hosts: _hosts,
        visibility: new FullVisibility(_hosts),
        currentUser: FakeCurrentUser.WithCapabilities(Capability.Maintain, Capability.ViewAll));

    private DateTime Seed()
    {
        var store = _backend.PrtgStore();
        var date = DateTime.Today;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 3001, Name = "dev-app-1", GroupPath = "Root/App", Ip = "192.168.20.1" },
            new PrtgDeviceRow { Objid = 3002, Name = "dev-app-2", GroupPath = "Root/App", Ip = "192.168.20.1" },
            new PrtgDeviceRow { Objid = 3003, Name = "dev-db-1", GroupPath = "Root/Db", Ip = "192.168.20.2" },
            new PrtgDeviceRow { Objid = 3004, Name = "dev-web-1", GroupPath = "Root/Web", Ip = "192.168.20.3" },
            new PrtgDeviceRow { Objid = 3005, Name = "dev-noip", GroupPath = "Root/Misc", Ip = null },
        }, DateTime.Now);
        var h1 = _hosts.Upsert(new WebHost { HostName = "srv-db-primary", IpAddress = "192.168.20.2", Active = true });
        _hosts.Upsert(new WebHost { HostName = "srv-db-replica", IpAddress = " 192.168.20.2 ", Active = true });
        _hosts.Upsert(new WebHost { HostName = "srv-web-old", IpAddress = "192.168.20.3", Active = false });
        var h4 = _hosts.Upsert(new WebHost { HostName = "srv-web", IpAddress = "192.168.20.3", Active = true });
        store.ReplaceHostMapForDate(date, new List<PrtgHostMapRow>
        {
            new() { MapDate = date, DeviceObjid = 3001, Ip = "192.168.20.1", MapStatus = PrtgMapStatus.Conflict, Note = "同 IP 多台裝置", HostName = "x", CreatedAt = DateTime.Now },
            new() { MapDate = date, DeviceObjid = 3003, Ip = "192.168.20.2", HostId = h1.HostId, HostName = h1.HostName, MapStatus = PrtgMapStatus.Conflict, Note = "同 IP 多台主機", CreatedAt = DateTime.Now },
            new() { MapDate = date, DeviceObjid = 3004, Ip = "192.168.20.3", HostId = h4.HostId, HostName = h4.HostName, MapStatus = PrtgMapStatus.Conflict, Note = PrtgHostMapper.NoteHostnameMultiHostsPrefix + "srv-web", CreatedAt = DateTime.Now },
            new() { MapDate = date, DeviceObjid = 3005, Ip = null, MapStatus = PrtgMapStatus.Conflict, Note = null, CreatedAt = DateTime.Now },
            new() { MapDate = date, DeviceObjid = 3009, Ip = "192.168.20.9", MapStatus = PrtgMapStatus.Ok, Note = "ok 不列", CreatedAt = DateTime.Now },
            new() { MapDate = date, DeviceObjid = 3999, Ip = "10.9.9.9", MapStatus = PrtgMapStatus.Conflict, Note = "裝置不在鏡像", CreatedAt = DateTime.Now },
        });
        return date;
    }

    private const string GoldenPage1 = """
{"success":true,"data":{"mapDate":"{MAPDATE}","total":5,"page":1,"pageSize":3,"items":[{"deviceObjid":3001,"deviceName":"dev-app-1","groupPath":"Root/App","ip":"192.168.20.1","hostName":null,"note":"\u540C IP \u591A\u53F0\u88DD\u7F6E","mapStatus":"conflict","conflictKind":"multi-host","sameIpDevices":[{"objid":3001,"name":"dev-app-1","groupPath":"Root/App"},{"objid":3002,"name":"dev-app-2","groupPath":"Root/App"}],"candidateHosts":[]},{"deviceObjid":3003,"deviceName":"dev-db-1","groupPath":"Root/Db","ip":"192.168.20.2","hostName":"srv-db-primary","note":"\u540C IP \u591A\u53F0\u4E3B\u6A5F","mapStatus":"conflict","conflictKind":"multi-host","sameIpDevices":[{"objid":3003,"name":"dev-db-1","groupPath":"Root/Db"}],"candidateHosts":[{"hostId":1,"hostName":"srv-db-primary","ipAddress":"192.168.20.2"},{"hostId":2,"hostName":"srv-db-replica","ipAddress":" 192.168.20.2 "}]},{"deviceObjid":3004,"deviceName":"dev-web-1","groupPath":"Root/Web","ip":"192.168.20.3","hostName":"srv-web","note":"hostname \u6BD4\u5C0D\u5230srv-web","mapStatus":"conflict","conflictKind":"hostname-multi","sameIpDevices":[{"objid":3004,"name":"dev-web-1","groupPath":"Root/Web"}],"candidateHosts":[{"hostId":4,"hostName":"srv-web","ipAddress":"192.168.20.3"}]}]},"error":null}
""";
    private const string GoldenPage2 = """
{"success":true,"data":{"mapDate":"{MAPDATE}","total":5,"page":2,"pageSize":3,"items":[{"deviceObjid":3005,"deviceName":"dev-noip","groupPath":"Root/Misc","ip":null,"hostName":null,"note":null,"mapStatus":"conflict","conflictKind":"multi-host","sameIpDevices":[{"objid":3005,"name":"dev-noip","groupPath":"Root/Misc"}],"candidateHosts":[]},{"deviceObjid":3999,"deviceName":null,"groupPath":null,"ip":"10.9.9.9","hostName":null,"note":"\u88DD\u7F6E\u4E0D\u5728\u93E1\u50CF","mapStatus":"conflict","conflictKind":"multi-host","sameIpDevices":[{"objid":3999,"name":null,"groupPath":null}],"candidateHosts":[]}]},"error":null}
""";

    [Fact]
    public void 黃金值_衝突清單分頁輸出與抽離前逐字相同()
    {
        var date = Seed();
        var dateText = date.ToString("yyyy-MM-dd'T'00:00:00");
        var controller = CreateController();

        var page1 = JsonSerializer.Serialize(controller.GetPrtgHostMap("conflict", page: 1, pageSize: 3), Json);
        var page2 = JsonSerializer.Serialize(controller.GetPrtgHostMap("CONFLICT", page: 2, pageSize: 3), Json);

        Assert.Equal(GoldenPage1.Replace("{MAPDATE}", dateText), page1);
        Assert.Equal(GoldenPage2.Replace("{MAPDATE}", dateText), page2);
    }

    [Fact]
    public void 控制器GetPrtgHostMap本文少於25行且不直接用儲存層()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "Controllers", "Api", "SettingsController.cs")).Replace("\r\n", "\n");
        var start = src.IndexOf("public ApiResponse<PrtgHostMapPageDto> GetPrtgHostMap(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = src.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        var body = src[start..end];
        var lines = body.Split('\n').Length;
        Assert.True(lines < 25, $"GetPrtgHostMap 本文 {lines} 行");
        Assert.DoesNotContain("PrtgStore()", body);
        Assert.DoesNotContain("GetAllDevices", body);
    }
}
