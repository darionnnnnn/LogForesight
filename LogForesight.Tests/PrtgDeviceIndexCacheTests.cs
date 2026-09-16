using System.Security.Claims;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Extensions;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 裝置索引的跨請求快取（回饋四十五輪 B5 C3）。
///
/// 衝突清單每翻一頁都重讀裝置全表、重建索引，而裝置表是結構鏡像、一天只在夜間同步時變一次。
/// 這裡釘住：TTL 內不重查、TTL 過後重查（計數替身）、索引內容正確，
/// 以及**接上快取之後分頁的回傳內容與改動前完全相同**。
/// </summary>
public class PrtgDeviceIndexCacheTests
{
    private static PrtgDeviceRow Dev(long objid, string? ip, string? name = null) =>
        new() { Objid = objid, Ip = ip, Name = name ?? ("dev-" + objid), GroupPath = "Root" };

    // ── 驗收 4：TTL 內不重查、TTL 過後重查 ──────────────────────────────────────

    [Fact]
    public void TTL內第二次取用不重新查詢()
    {
        var now = new DateTime(2026, 9, 16, 9, 0, 0);
        var calls = 0;
        var cache = new PrtgDeviceIndexCache(() => now);

        IReadOnlyList<PrtgDeviceRow> Load()
        {
            calls++;
            return new[] { Dev(1, "10.0.0.1") };
        }

        var first = cache.GetOrAdd(PrtgDeviceIndexCache.GlobalKey, Load);
        now = now.AddSeconds(PrtgDeviceIndexCache.TtlSeconds - 1);
        var second = cache.GetOrAdd(PrtgDeviceIndexCache.GlobalKey, Load);

        Assert.Equal(1, calls);
        Assert.Same(first, second);
    }

    [Fact]
    public void TTL過後重新查詢()
    {
        var now = new DateTime(2026, 9, 16, 9, 0, 0);
        var calls = 0;
        var cache = new PrtgDeviceIndexCache(() => now);

        IReadOnlyList<PrtgDeviceRow> Load()
        {
            calls++;
            return new[] { Dev(1, "10.0.0." + calls) };
        }

        cache.GetOrAdd(PrtgDeviceIndexCache.GlobalKey, Load);
        now = now.AddSeconds(PrtgDeviceIndexCache.TtlSeconds);
        var second = cache.GetOrAdd(PrtgDeviceIndexCache.GlobalKey, Load);

        Assert.Equal(2, calls);
        // 重查之後拿到的是新資料，不是逾時仍回舊快照
        Assert.Equal("10.0.0.2", second.ByObjid(1)!.Ip);
    }

    [Fact]
    public void 索引內容_依objid與正規化IP分組且非法IP不進索引()
    {
        var cache = new PrtgDeviceIndexCache(() => new DateTime(2026, 9, 16));

        var index = cache.GetOrAdd(PrtgDeviceIndexCache.GlobalKey, () => new[]
        {
            Dev(1, "10.0.0.1"),
            Dev(2, "10.0.0.1"),
            Dev(3, "10.0.0.2"),
            Dev(4, "not-an-ip"),
            Dev(5, null)
        });

        Assert.Equal(5, index.DeviceCount);
        Assert.Equal("dev-3", index.ByObjid(3)!.Name);
        Assert.Null(index.ByObjid(99));
        Assert.Equal(2, index.ByNormIp("10.0.0.1").Count);
        Assert.Single(index.ByNormIp("10.0.0.2"));
        // 正規化回 null 的位址不進索引：兩邊都 null 時會湊出假命中
        Assert.Empty(index.ByNormIp(null));
        Assert.Empty(index.ByNormIp("not-an-ip"));
    }

    [Fact]
    public void 註冊守門_快取以Singleton註冊於DI()
    {
        // 可選相依若沒被註冊，controller 會安靜地走回「每頁重建」的退路，測試全綠但效益是零
        var services = new ServiceCollection();
        services.AddLogForesightServices();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(PrtgDeviceIndexCache));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}

/// <summary>
/// 衝突清單接上裝置索引快取之後的端點行為（回饋四十五輪 B5 C3 驗收 5）。
/// </summary>
public class PrtgHostMapCachedPagingTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly SettingsController _controller;
    private DateTime _now = new(2026, 9, 16, 9, 0, 0);

    public PrtgHostMapCachedPagingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtgidx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);

        _controller = new SettingsController(
            new StubSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            new RecordingAuditService(),
            backend: _backend,
            deviceIndexCache: new PrtgDeviceIndexCache(() => _now));

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test-admin"),
                new Claim(JwtTokenService.AccountClaim, "test-admin")
            }, "TestAuth"))
        };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
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

    /// <summary>三十列衝突：其中 device 1001/1002 同 IP（multi-device），其餘各自單台</summary>
    private DateTime Seed()
    {
        var store = _backend.PrtgStore();
        var date = DateTime.Today;

        var devices = new List<PrtgDeviceRow> { Device(1001, "192.168.5.1"), Device(1002, "192.168.5.1") };
        for (var i = 3; i <= 30; i++) devices.Add(Device(1000 + i, "192.168.9." + i));
        store.UpsertDevices(devices, DateTime.Now);

        var rows = devices.Select(d => new PrtgHostMapRow
        {
            MapDate = date,
            DeviceObjid = d.Objid,
            Ip = d.Ip,
            HostName = "SRV-" + d.Objid,
            MapStatus = PrtgMapStatus.Conflict,
            Note = "衝突 " + d.Objid,
            CreatedAt = DateTime.Now
        }).ToList();
        store.ReplaceHostMapForDate(date, rows);

        return date;
    }

    private static PrtgDeviceRow Device(long objid, string ip) =>
        new() { Objid = objid, Name = "dev-" + objid, GroupPath = "Root/Net", Ip = ip };

    [Fact]
    public void 第一頁與第二頁_內容與改動前相同()
    {
        var date = Seed();

        var page1 = _controller.GetPrtgHostMap("conflict", page: 1, pageSize: 20).Data!;
        var page2 = _controller.GetPrtgHostMap("conflict", page: 2, pageSize: 20).Data!;

        Assert.Equal(date.Date, page1.MapDate?.Date);
        Assert.Equal(30, page1.Total);
        Assert.Equal(20, page1.Items.Count);
        Assert.Equal(30, page2.Total);
        Assert.Equal(10, page2.Items.Count);

        // 依 DeviceObjid 排序分頁，兩頁不重覆、不漏列
        Assert.Equal(Enumerable.Range(1001, 20).Select(i => (long)i), page1.Items.Select(i => i.DeviceObjid));
        Assert.Equal(Enumerable.Range(1021, 10).Select(i => (long)i), page2.Items.Select(i => i.DeviceObjid));

        // 第一頁一條：同 IP 兩台 → multi-device，裝置名稱與群組路徑來自索引
        var multi = page1.Items.Single(i => i.DeviceObjid == 1001);
        Assert.Equal("multi-device", multi.ConflictKind);
        Assert.Equal("dev-1001", multi.DeviceName);
        Assert.Equal("Root/Net", multi.GroupPath);
        Assert.Equal(new long[] { 1001, 1002 }, multi.SameIpDevices.Select(d => d.Objid).OrderBy(x => x));
        Assert.Null(multi.HostName);

        // 第二頁一條：單台 → multi-host（同 IP 沒有第二台裝置）
        var single = page2.Items.Single(i => i.DeviceObjid == 1030);
        Assert.Equal("multi-host", single.ConflictKind);
        Assert.Equal("dev-1030", single.DeviceName);
        Assert.Equal("SRV-1030", single.HostName);
        Assert.Equal("衝突 1030", single.Note);
        Assert.Single(single.SameIpDevices);
        Assert.Empty(single.CandidateHosts);
    }

    [Fact]
    public void 翻頁不再重建裝置索引_以TTL內新增裝置不被看見為證()
    {
        Seed();

        var page1 = _controller.GetPrtgHostMap("conflict", page: 1, pageSize: 20).Data!;
        Assert.Equal("multi-device", page1.Items.Single(i => i.DeviceObjid == 1001).ConflictKind);

        // TTL 內替 1030 的 IP 補上第二台裝置：若翻頁時重讀了裝置全表，1030 會變成 multi-device
        _backend.PrtgStore().UpsertDevices(new[] { Device(1999, "192.168.9.30") }, DateTime.Now);

        var page2 = _controller.GetPrtgHostMap("conflict", page: 2, pageSize: 20).Data!;
        Assert.Equal("multi-host", page2.Items.Single(i => i.DeviceObjid == 1030).ConflictKind);

        // TTL 過後就會看見新裝置（快取不是永久的假新鮮）
        _now = _now.AddSeconds(PrtgDeviceIndexCache.TtlSeconds);
        var page2Again = _controller.GetPrtgHostMap("conflict", page: 2, pageSize: 20).Data!;
        Assert.Equal("multi-device", page2Again.Items.Single(i => i.DeviceObjid == 1030).ConflictKind);
    }
}
