using System.Security.Claims;
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

public class PrtgHostMapEndpointTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly RecordingAuditService _audit = new();
    private readonly SettingsController _controller;

    public PrtgHostMapEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);

        _controller = new SettingsController(
            new StubSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            _audit,
            backend: _backend);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "test-admin"),
            new Claim(JwtTokenService.AccountClaim, "test-admin")
        }, "TestAuth"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class NoOpRunConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    private sealed class StubSystemSettingsService : ISystemSettingsService
    {
        public SystemSettingsDto Settings { get; set; } = new();
        public SystemSettingsDto Get() => Settings;
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public bool SetPrtgEnabled(bool enabled) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void 分頁total正確且不被pageSize影響()
    {
        var store = _backend.PrtgStore();
        var date = DateTime.Today;

        var rows = Enumerable.Range(1, 25).Select(i => new PrtgHostMapRow
        {
            MapDate = date,
            DeviceObjid = 1000 + i,
            Ip = $"10.0.0.{i}",
            MapStatus = PrtgMapStatus.Conflict,
            Note = $"衝突第 {i} 筆",
            CreatedAt = DateTime.Now
        }).ToList();
        store.ReplaceHostMapForDate(date, rows);

        var res = _controller.GetPrtgHostMap("conflict", page: 1, pageSize: 10);

        Assert.True(res.Success);
        Assert.NotNull(res.Data);
        Assert.Equal(25, res.Data.Total);
        Assert.Equal(1, res.Data.Page);
        Assert.Equal(10, res.Data.PageSize);
        Assert.Equal(10, res.Data.Items.Count);
        Assert.Equal(date.Date, res.Data.MapDate?.Date);
    }

    [Fact]
    public void pageSize超上限被夾()
    {
        var store = _backend.PrtgStore();
        var date = DateTime.Today;

        var rows = Enumerable.Range(1, 5).Select(i => new PrtgHostMapRow
        {
            MapDate = date,
            DeviceObjid = 1000 + i,
            Ip = $"10.0.0.{i}",
            MapStatus = PrtgMapStatus.Conflict,
            Note = $"衝突第 {i} 筆",
            CreatedAt = DateTime.Now
        }).ToList();
        store.ReplaceHostMapForDate(date, rows);

        var res = _controller.GetPrtgHostMap("conflict", page: 1, pageSize: 9999);

        Assert.True(res.Success);
        Assert.NotNull(res.Data);
        Assert.Equal(200, res.Data.PageSize);
        Assert.Equal(5, res.Data.Total);
        Assert.Equal(5, res.Data.Items.Count);
    }

    [Fact]
    public void ConflictKind判定_同IP兩台為multiDevice_單台對多主機為multiHost()
    {
        var store = _backend.PrtgStore();
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var date = DateTime.Today;

        // multi-device: 同 IP 有兩台 device
        var dev1 = new PrtgDeviceRow { Objid = 2001, Name = "dev-app-1", GroupPath = "Root/App", Ip = "192.168.10.1" };
        var dev2 = new PrtgDeviceRow { Objid = 2002, Name = "dev-app-2", GroupPath = "Root/App", Ip = "192.168.10.1" };

        // multi-host: 單台 device，但 IP 對到兩台活躍主機
        var dev3 = new PrtgDeviceRow { Objid = 2003, Name = "dev-db-1", GroupPath = "Root/Db", Ip = "192.168.10.2" };

        store.UpsertDevices(new[] { dev1, dev2, dev3 }, DateTime.Now);

        var host1 = hostStore.Upsert(new WebHost { HostName = "srv-db-primary", IpAddress = "192.168.10.2", Active = true });
        var host2 = hostStore.Upsert(new WebHost { HostName = "srv-db-replica", IpAddress = "192.168.10.2", Active = true });

        var mapRows = new List<PrtgHostMapRow>
        {
            new()
            {
                MapDate = date,
                DeviceObjid = 2001,
                Ip = "192.168.10.1",
                MapStatus = PrtgMapStatus.Conflict,
                Note = "同 IP 多台裝置",
                CreatedAt = DateTime.Now
            },
            new()
            {
                MapDate = date,
                DeviceObjid = 2003,
                Ip = "192.168.10.2",
                HostId = host1.HostId,
                HostName = host1.HostName,
                MapStatus = PrtgMapStatus.Conflict,
                Note = "同 IP 多台主機",
                CreatedAt = DateTime.Now
            }
        };
        store.ReplaceHostMapForDate(date, mapRows);

        var res = _controller.GetPrtgHostMap("conflict", page: 1, pageSize: 20);

        Assert.True(res.Success);
        Assert.NotNull(res.Data);
        Assert.Equal(2, res.Data.Items.Count);

        var itemMultiDevice = res.Data.Items.First(i => i.DeviceObjid == 2001);
        Assert.Equal("multi-device", itemMultiDevice.ConflictKind);
        Assert.Equal(2, itemMultiDevice.SameIpDevices.Count);
        Assert.Contains(itemMultiDevice.SameIpDevices, d => d.Objid == 2001 && d.Name == "dev-app-1");
        Assert.Contains(itemMultiDevice.SameIpDevices, d => d.Objid == 2002 && d.Name == "dev-app-2");
        Assert.Null(itemMultiDevice.HostName);

        var itemMultiHost = res.Data.Items.First(i => i.DeviceObjid == 2003);
        Assert.Equal("multi-host", itemMultiHost.ConflictKind);
        Assert.Single(itemMultiHost.SameIpDevices);
        Assert.Equal(2003, itemMultiHost.SameIpDevices[0].Objid);
        Assert.Equal(2, itemMultiHost.CandidateHosts.Count);
        Assert.Contains(itemMultiHost.CandidateHosts, h => h.HostId == host1.HostId && h.HostName == "srv-db-primary");
        Assert.Contains(itemMultiHost.CandidateHosts, h => h.HostId == host2.HostId && h.HostName == "srv-db-replica");
        Assert.Equal(host1.HostName, itemMultiHost.HostName);
    }

    [Fact]
    public void status非conflict時回驗證例外()
    {
        var ex1 = Assert.Throws<DomainException>(() => _controller.GetPrtgHostMap("ok"));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex1.Code);

        var ex2 = Assert.Throws<DomainException>(() => _controller.GetPrtgHostMap("all"));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex2.Code);

        var ex3 = Assert.Throws<DomainException>(() => _controller.GetPrtgHostMap(""));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex3.Code);
    }

    [Fact]
    public void Ip排除端點_PUT讀得到且正規化_DELETE後讀不到_空白IP擲例外()
    {
        // 空白 IP 擲驗證例外
        var exEmpty = Assert.Throws<DomainException>(() =>
            _controller.SetPrtgIpExclude(new SetPrtgIpExcludeRequest { Ip = "" }));
        Assert.Equal(ApiErrorCodes.ValidationFailed, exEmpty.Code);

        var exWhitespace = Assert.Throws<DomainException>(() =>
            _controller.SetPrtgIpExclude(new SetPrtgIpExcludeRequest { Ip = "   " }));
        Assert.Equal(ApiErrorCodes.ValidationFailed, exWhitespace.Code);

        // PUT 之後 GET 讀得到且 IP 已正規化（前後去空白、轉小寫）
        var putRes = _controller.SetPrtgIpExclude(new SetPrtgIpExcludeRequest
        {
            Ip = " 10.0.0.5 ",
            Note = "測試排除"
        });
        Assert.True(putRes.Success);
        Assert.NotNull(putRes.Data);
        Assert.Equal("10.0.0.5", putRes.Data.Ip);
        Assert.Equal("測試排除", putRes.Data.Note);
        Assert.Equal("test-admin", putRes.Data.CreatedBy);

        var getRes = _controller.GetPrtgIpExcludes();
        Assert.True(getRes.Success);
        Assert.NotNull(getRes.Data);
        var excludeItem = Assert.Single(getRes.Data);
        Assert.Equal("10.0.0.5", excludeItem.Ip);
        Assert.Equal("測試排除", excludeItem.Note);
        Assert.Equal("test-admin", excludeItem.CreatedBy);

        // 稽核有記錄
        Assert.Contains(_audit.Entries, a => a.Action == AuditActions.PrtgIpExcludeSet && a.TargetId == "10.0.0.5");

        // DELETE 之後讀不到
        var delRes = _controller.DeletePrtgIpExclude(" 10.0.0.5 ");
        Assert.True(delRes.Success);
        Assert.True(delRes.Data!.Deleted);
        Assert.Null(delRes.Data.RemapWarning);

        var getResAfter = _controller.GetPrtgIpExcludes();
        Assert.True(getResAfter.Success);
        Assert.Empty(getResAfter.Data!);

        Assert.Contains(_audit.Entries, a => a.Action == AuditActions.PrtgIpExcludeDelete && a.TargetId == "10.0.0.5");
    }

    [Fact]
    public void 重算確實發生_PUT排除後當日對應列已不存在()
    {
        var store = _backend.PrtgStore();
        var hostStore = new HostStore(_backend.Blob("hosts"));

        // 建立一台 device 與對應主機
        var targetIp = "192.168.50.100";
        var dev = new PrtgDeviceRow { Objid = 3001, Name = "dev-to-exclude", Ip = targetIp };
        store.UpsertDevices(new[] { dev }, DateTime.Now);

        var host = new WebHost { HostId = 201, HostName = "srv-to-exclude", IpAddress = targetIp, Active = true };
        hostStore.Upsert(host);

        // 跑一次今日對應，產生對應列
        var mapper = new PrtgHostMapper(store, hostStore, new NoOpRunConsole(), new PrtgAddressResolver());
        var initialResult = mapper.MapForDate(DateTime.Today);
        Assert.Equal(1, initialResult.Ok);

        // 確認今日對應表中確實有這台 device
        var beforeRows = store.GetHostMapForDate(DateTime.Today);
        Assert.Contains(beforeRows, r => r.DeviceObjid == 3001 && r.Ip == targetIp);

        // 透過端點 PUT 設定 IP 排除
        var putRes = _controller.SetPrtgIpExclude(new SetPrtgIpExcludeRequest
        {
            Ip = targetIp,
            Note = "排除此 IP"
        });
        Assert.True(putRes.Success);

        // 直接查當日 lf_prtg_host_map，該 device 的列已不存在
        var afterRows = store.GetHostMapForDate(DateTime.Today);
        Assert.DoesNotContain(afterRows, r => r.DeviceObjid == 3001);
    }

    [Fact]
    public void 指派用主機清單_只回活躍主機且依名稱排序()
    {
        var hostStore = new HostStore(_backend.Blob("hosts"));
        hostStore.Upsert(new WebHost { HostId = 1, HostName = "Z-Server", IpAddress = "10.0.1.1", Active = true });
        hostStore.Upsert(new WebHost { HostId = 2, HostName = "A-Server", IpAddress = "10.0.1.2", Active = true });
        hostStore.Upsert(new WebHost { HostId = 3, HostName = "Inactive-Server", IpAddress = "10.0.1.3", Active = false });
        hostStore.Upsert(new WebHost { HostId = 4, HostName = "Merged-Server", IpAddress = "10.0.1.4", Active = true, MergedInto = 1 });

        var service = new HostAdminService(
            hostStore,
            new FakeHostGroupStore(),
            new FakeUserStore(),
            new FakeNetiqServerCatalog("SENTINEL-A"),
            new FakeNetiqHostServiceForAdmin(),
            _audit,
            new UserDisplayNameService(new FakeSystemSettingsStore()),
            _backend.PrtgStore());

        var list = service.GetAllActiveHostOptions();

        Assert.Equal(2, list.Count);
        Assert.Equal("A-Server", list[0].HostName);
        Assert.Equal(2, list[0].HostId);
        Assert.Equal("Z-Server", list[1].HostName);
        Assert.Equal(1, list[1].HostId);
    }

    [Fact]
    public void PrtgMirrorStatus_包含IpExcludeCount且無Conflicts清單()
    {
        var store = _backend.PrtgStore();
        store.UpsertIpExclude(new PrtgIpExcludeRow { Ip = "10.0.0.1", CreatedAt = DateTime.Now });
        store.UpsertIpExclude(new PrtgIpExcludeRow { Ip = "10.0.0.2", CreatedAt = DateTime.Now });

        var res = _controller.GetPrtgMirrorStatus();

        Assert.True(res.Success);
        Assert.NotNull(res.Data);
        Assert.Equal(2, res.Data.IpExcludeCount);
    }

    [Fact]
    public void 人工對應清單_同IP略過台數正確計算()
    {
        var store = _backend.PrtgStore();
        var hostStore = new HostStore(_backend.Blob("hosts"));

        var sharedIp = "192.168.10.1";
        var dev1 = new PrtgDeviceRow { Objid = 4001, Name = "dev-shared-1", Ip = sharedIp };
        var dev2 = new PrtgDeviceRow { Objid = 4002, Name = "dev-shared-2", Ip = sharedIp };
        var dev3 = new PrtgDeviceRow { Objid = 4003, Name = "dev-shared-3", Ip = sharedIp };

        var singleIp = "192.168.10.2";
        var devSingle = new PrtgDeviceRow { Objid = 4004, Name = "dev-single", Ip = singleIp };

        store.UpsertDevices(new[] { dev1, dev2, dev3, devSingle }, DateTime.Now);

        var host1 = new WebHost { HostId = 301, HostName = "srv-1", IpAddress = sharedIp, Active = true };
        var host2 = new WebHost { HostId = 302, HostName = "srv-2", IpAddress = singleIp, Active = true };
        hostStore.Upsert(host1);
        hostStore.Upsert(host2);

        store.UpsertManualMap(new PrtgManualMapRow { DeviceObjid = 4001, HostId = 301, CreatedAt = DateTime.Now });
        store.UpsertManualMap(new PrtgManualMapRow { DeviceObjid = 4004, HostId = 302, CreatedAt = DateTime.Now });

        var res = _controller.GetPrtgManualMaps();
        Assert.True(res.Success);
        Assert.NotNull(res.Data);

        var mapShared = res.Data.FirstOrDefault(m => m.DeviceObjid == 4001);
        var mapSingle = res.Data.FirstOrDefault(m => m.DeviceObjid == 4004);

        Assert.NotNull(mapShared);
        Assert.Equal(2, mapShared!.SameIpSkippedCount);

        Assert.NotNull(mapSingle);
        Assert.Equal(0, mapSingle!.SameIpSkippedCount);
    }

    [Fact]
    public void 人工對應清單_device無IP時SameIpSkippedCount為0()
    {
        var store = _backend.PrtgStore();
        var hostStore = new HostStore(_backend.Blob("hosts"));

        var devNoIp = new PrtgDeviceRow { Objid = 4010, Name = "dev-no-ip", Ip = null };
        store.UpsertDevices(new[] { devNoIp }, DateTime.Now);

        var host = new WebHost { HostId = 310, HostName = "srv-no-ip", Active = true };
        hostStore.Upsert(host);

        store.UpsertManualMap(new PrtgManualMapRow { DeviceObjid = 4010, HostId = 310, CreatedAt = DateTime.Now });

        var res = _controller.GetPrtgManualMaps();
        Assert.True(res.Success);
        Assert.NotNull(res.Data);

        var mapItem = res.Data.FirstOrDefault(m => m.DeviceObjid == 4010);
        Assert.NotNull(mapItem);
        Assert.Equal(0, mapItem!.SameIpSkippedCount);
    }

    /// <summary>
    /// 同 IP 上另一台 device 若也有人工對應，它走的是人工分支、不是被略過——
    /// 直接用「同 IP 台數 − 1」會讓兩列都顯示「另有 1 台已略過」，使用者會去找一台不存在的裝置。
    /// </summary>
    [Fact]
    public void 人工對應清單_同IP兩台皆人工對應時略過台數各為0()
    {
        var store = _backend.PrtgStore();
        const string sharedIp = "10.9.9.9";
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 5001, Name = "dev-a", Ip = sharedIp },
            new PrtgDeviceRow { Objid = 5002, Name = "dev-b", Ip = sharedIp },
            new PrtgDeviceRow { Objid = 5003, Name = "dev-c", Ip = sharedIp }
        }, DateTime.Now);
        store.UpsertManualMap(new PrtgManualMapRow { DeviceObjid = 5001, HostId = 401, CreatedAt = DateTime.Now });
        store.UpsertManualMap(new PrtgManualMapRow { DeviceObjid = 5002, HostId = 402, CreatedAt = DateTime.Now });

        var res = _controller.GetPrtgManualMaps();
        Assert.True(res.Success);
        var a = res.Data!.Single(m => m.DeviceObjid == 5001);
        var bRow = res.Data!.Single(m => m.DeviceObjid == 5002);
        // 三台裡兩台人工對應，真正被略過的只有 dev-c 一台
        Assert.Equal(1, a.SameIpSkippedCount);
        Assert.Equal(1, bRow.SameIpSkippedCount);
    }
}
