using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 監控頁的 sensor 型別對照只能用頁內 objid 的專用投影（<c>GetSensorTypes</c>），
/// 不得每個請求整表讀未暫停 sensor 的六欄（<c>GetSensorStatuses</c>）。
/// </summary>
public sealed class PrtgMonitoringSensorTypesProjectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-monitoring-proj-" + Guid.NewGuid().ToString("N"));
    private readonly SqlRecorder _recorder = new();
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;

    private sealed class SqlRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }
        public void Clear() { lock (Commands) Commands.Clear(); }
        public List<string> SensorSelects() { lock (Commands) return Commands.Where(c => c.Contains("lf_prtg_sensors") && c.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)).ToList(); }
    }

    private sealed class Visible(params long[] ids) : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => ids.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIds(PrtgHostSnapshot snapshot) => GetVisibleHostIds();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long id) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => null;
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long id) { }
    }

    public PrtgMonitoringSensorTypesProjectionTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "t.db")}" }, _dir, _recorder);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _hosts.Upsert(new() { HostName = "VISIBLE", Source = "netiq", Active = true });
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s => s.PrtgUrl = "https://fixture.example");
        _backend.PrtgStore().UpsertDevices([new() { Objid = 10, Name = "fixture" }], DateTime.Now);
        _backend.PrtgStore().UpsertSensors([
            new() { Objid = 100, DeviceObjid = 10, Name = "Ping", SensorType = "ping" },
            new() { Objid = 101, DeviceObjid = 10, Name = "Disk", SensorType = "wmidiskspace" }], DateTime.Now);
        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
            [new() { MapDate = DateTime.Today, DeviceObjid = 10, HostId = 1, HostName = "VISIBLE", MapStatus = PrtgMapStatus.Ok }]);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void 監控頁取得_sensor型別只查頁內objid_不整表讀六欄()
    {
        var protection = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        var controller = new PrtgMonitoringController(_backend, _hosts, new Visible(1),
            FakeCurrentUser.WithCapabilities(Capability.Maintain), new RecordingAuditService(), protection);
        _recorder.Clear();

        // 評估預覽頁才會為頁內 sensor 取型別對照（GET preview，不需目錄權杖）
        Assert.IsType<OkObjectResult>(controller.Preview());

        var sensorSelects = _recorder.SensorSelects();
        Assert.NotEmpty(sensorSelects);
        // GetSensorStatuses 的形狀：讀 status／name／category 六欄且只以 paused 過濾（沒有 objid 範圍、沒有分頁、沒有 join）；這裡一條都不能有
        Assert.DoesNotContain(sensorSelects, sql =>
            sql.Contains("\"status\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"category\"", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("json_each", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
        // 專用投影只讀 objid 與 sensor_type，且帶頁內 objid 範圍
        Assert.Contains(sensorSelects, sql =>
            sql.Contains("\"sensor_type\"", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("\"category\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("json_each", StringComparison.OrdinalIgnoreCase));
    }
}
