using System.Security.Claims;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 取數範圍的規模估算端點（docs/PRTG-SPEC.md §3a）：管理者把範圍放寬之前要看得到
/// 「一晚要抓幾個 sensor」。跑不完的症狀是隔天資料不全、不是當下報錯，所以這個數字要正確。
/// </summary>
public class PrtgFetchScopeEstimateEndpointTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly SettingsController _controller;
    private readonly IHostStore _hosts;

    public PrtgFetchScopeEstimateEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-scope-estimate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);

        _hosts = new HostStore(_backend.Blob("hosts"));

        _controller = new SettingsController(
            new StubSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            new RecordingAuditService(),
            backend: _backend,
            hosts: _hosts);

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

    /// <summary>
    /// 種一份鏡像：兩台 device 對應到兩台主機（其中一台是 conflict，不該計入），
    /// 每台 device 各兩個 sensor（一個在白名單內、一個不在）。
    /// </summary>
    private void SeedMirror()
    {
        var store = _backend.PrtgStore();
        var now = DateTime.Now;

        var hostA = _hosts.Upsert(new WebHost { HostName = "SRV-A", Source = "netiq" });
        var hostB = _hosts.Upsert(new WebHost { HostName = "SRV-B", Source = "netiq" });

        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 101, Name = "dev-a", SyncedAt = now, CreatedAt = now },
            new() { Objid = 102, Name = "dev-b", SyncedAt = now, CreatedAt = now },
            new() { Objid = 103, Name = "dev-c", SyncedAt = now, CreatedAt = now }
        }, now);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 201, DeviceObjid = 101, Name = "cpu", SensorType = "SNMP CPU Load", SyncedAt = now, CreatedAt = now },
            new() { Objid = 202, DeviceObjid = 101, Name = "ping", SensorType = "Ping", SyncedAt = now, CreatedAt = now },
            new() { Objid = 203, DeviceObjid = 102, Name = "disk", SensorType = "SNMP Disk Free", SyncedAt = now, CreatedAt = now },
            new() { Objid = 204, DeviceObjid = 103, Name = "cpu-c", SensorType = "SNMP CPU Load", SyncedAt = now, CreatedAt = now }
        }, now);

        store.ReplaceHostMapForDate(DateTime.Today, new List<PrtgHostMapRow>
        {
            new() { MapDate = DateTime.Today, DeviceObjid = 101, HostId = hostA.HostId, MapStatus = PrtgMapStatus.Ok, CreatedAt = now },
            new() { MapDate = DateTime.Today, DeviceObjid = 102, HostId = hostB.HostId, MapStatus = PrtgMapStatus.Ok, CreatedAt = now },
            // conflict 的對應歸屬不確定，納入會把數值掛到錯的主機上——估算也不得計入
            new() { MapDate = DateTime.Today, DeviceObjid = 103, HostId = hostA.HostId, MapStatus = PrtgMapStatus.Conflict, CreatedAt = now }
        });
    }

    private void SetSettings(string scope, List<string> whitelist, List<string>? extraHosts = null)
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgValueFetchScope = scope;
            s.PrtgSensorTypeWhitelist = whitelist;
            s.PrtgValueFetchExtraHosts = extraHosts ?? new List<string>();
        });
    }

    [Fact]
    public void 全部已對應主機_只計ok對應且套用白名單()
    {
        SeedMirror();
        SetSettings(PrtgValueFetchScope.AllMapped, new List<string> { "SNMP CPU Load", "SNMP Disk Free" });

        var res = _controller.EstimatePrtgFetchScope(PrtgValueFetchScope.AllMapped);

        Assert.True(res.Data!.Success);
        Assert.Equal(PrtgValueFetchScope.AllMapped, res.Data.Scope);
        // conflict 的 device 103（與其上的 sensor 204）不計入
        Assert.Equal(2, res.Data.Hosts);
        Assert.Equal(2, res.Data.Devices);
        // 白名單濾掉 Ping（202），剩 201 與 203
        Assert.Equal(2, res.Data.Sensors);
        Assert.False(res.Data.WhitelistEmpty);
        Assert.Null(res.Data.Warning);
    }

    [Fact]
    public void 全部已對應主機_空白名單時警告且退回觸發模式()
    {
        // 空白名單＋all-mapped＝對全部 sensor 取數。設定層存檔會擋，但設定 blob 若由匯入或
        // 人工編輯繞過驗證寫進來，估算要照實說出「夜間批次會退回觸發主機」。
        SeedMirror();
        SetSettings(PrtgValueFetchScope.AllMapped, new List<string>());

        var res = _controller.EstimatePrtgFetchScope(PrtgValueFetchScope.AllMapped);

        Assert.True(res.Data!.Success);
        Assert.Equal(PrtgValueFetchScope.Triggered, res.Data.Scope);
        Assert.True(res.Data.WhitelistEmpty);
        Assert.NotNull(res.Data.Warning);
        Assert.Contains("白名單", res.Data.Warning);
    }

    [Fact]
    public void 觸發模式_不給假數字()
    {
        // 觸發主機數逐日變動、事前算不出來——估成 0 會被讀成「一個都不抓」。
        SeedMirror();
        SetSettings(PrtgValueFetchScope.Triggered, new List<string> { "SNMP CPU Load" });

        var res = _controller.EstimatePrtgFetchScope(PrtgValueFetchScope.Triggered);

        Assert.True(res.Data!.Success);
        Assert.Equal(PrtgValueFetchScope.Triggered, res.Data.Scope);
        Assert.Equal(0, res.Data.Hosts);
        Assert.Equal(0, res.Data.Sensors);
    }

    [Fact]
    public void 觸發加清單模式_只估清單那部分()
    {
        SeedMirror();
        SetSettings(PrtgValueFetchScope.TriggeredPlusList,
            new List<string> { "SNMP CPU Load", "SNMP Disk Free" },
            new List<string> { "SRV-A" });

        var res = _controller.EstimatePrtgFetchScope(PrtgValueFetchScope.TriggeredPlusList);

        Assert.True(res.Data!.Success);
        Assert.Equal(1, res.Data.Hosts);
        Assert.Equal(1, res.Data.Devices);
        Assert.Equal(1, res.Data.Sensors);   // device 101 上白名單內的只有 201
    }

    [Fact]
    public void 未帶scope時用已儲存的設定()
    {
        SeedMirror();
        SetSettings(PrtgValueFetchScope.AllMapped, new List<string> { "SNMP CPU Load" });

        var res = _controller.EstimatePrtgFetchScope(scope: null);

        Assert.Equal(PrtgValueFetchScope.AllMapped, res.Data!.Scope);
    }

    /// <summary>本測試只碰估算端點，設定服務的其餘方法一律不該被呼叫（同本專案其他端點測試的既有形狀）。</summary>
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
