using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機詳情 PRTG 頁籤改為「只查該主機的對應列」（回饋四十五輪 B5 C2）。
///
/// 原本是把「最近一次對應」整張表讀回記憶體再過濾出一台主機的那幾列；對應表一天一份、
/// 每天數千列，等於每次開頁籤都付一次全表讀取。這裡釘住兩件事：
/// 回傳內容與改動前逐欄相同（有對應／沒對應各一條），以及**不再發生整表讀取**
/// ——以實際送到資料庫的 SQL 為證（EF 的 LogTo），不是相信註解。
/// </summary>
public class HostDetailPrtgScopedQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<string> _sql = new();

    public HostDetailPrtgScopedQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>每一條送出的 SQL 都收進 <see cref="_sql"/>，測試才有可計數的證據</summary>
    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(_connection)
            .LogTo(line => _sql.Add(line), new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information)
            .Options);

    private sealed class OnlyVisible : IVisibilityService
    {
        private readonly long _visibleHostId;
        public OnlyVisible(long visibleHostId) => _visibleHostId = visibleHostId;

        public void EnsureVisible(long hostId)
        {
            if (hostId != _visibleHostId)
                throw DomainException.NotFound("找不到這台主機，或您沒有檢視權限。");
        }

        public IReadOnlySet<long> GetVisibleHostIds() => new HashSet<long> { _visibleHostId };
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => throw new NotSupportedException("測試未使用此方法");
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => throw new NotSupportedException("測試未使用此方法");
        public List<WebHost> GetVisibleHosts() => new() { new WebHost { HostId = _visibleHostId } };
        public IReadOnlyList<string> GetCaseGrantHostNames() => throw new NotSupportedException("測試未使用此方法");
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => GetVisibleHostIds();
    }

    private HostDetailController CreateController(long visibleHostId) =>
        new(null!, new EfPrtgStore(NewContext), new OnlyVisible(visibleHostId));

    /// <summary>今天的對應：hostId 10 有兩台 device、hostId 11 有一台、另外 50 台是別人的</summary>
    private DateTime SeedMap()
    {
        var date = DateTime.Today;
        var now = new DateTime(2026, 9, 16, 3, 0, 0);
        using var ctx = NewContext();

        ctx.PrtgDevices.Add(new PrtgDeviceRow { Objid = 1001, Name = "SW-A", Ip = "10.0.0.1", SyncedAt = now, CreatedAt = now });
        ctx.PrtgSensors.Add(new PrtgSensorRow
        {
            Objid = 2001, DeviceObjid = 1001, Name = "Ping", SensorType = "ping",
            Category = "network", Paused = false, SyncedAt = now, CreatedAt = now
        });

        ctx.PrtgHostMaps.Add(new PrtgHostMapRow
        {
            MapDate = date, DeviceObjid = 1001, Ip = "10.0.0.1", HostId = 10,
            HostName = "SRV-10", MapStatus = PrtgMapStatus.Ok, Note = "自動對應", CreatedAt = now
        });
        ctx.PrtgHostMaps.Add(new PrtgHostMapRow
        {
            MapDate = date, DeviceObjid = 1002, Ip = "10.0.0.2", HostId = 10,
            HostName = "SRV-10", MapStatus = PrtgMapStatus.Conflict, Note = "同 IP 多台", CreatedAt = now
        });
        ctx.PrtgHostMaps.Add(new PrtgHostMapRow
        {
            MapDate = date, DeviceObjid = 1003, Ip = "10.0.0.3", HostId = 11,
            HostName = "SRV-11", MapStatus = PrtgMapStatus.Ok, CreatedAt = now
        });
        for (var i = 0; i < 50; i++)
        {
            ctx.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                MapDate = date, DeviceObjid = 2000 + i, Ip = "10.1.0." + i, HostId = 500 + i,
                HostName = "OTHER-" + i, MapStatus = PrtgMapStatus.Ok, CreatedAt = now
            });
        }

        // 前一天也有一份對應：新方法必須取「最近一次」那一天，不是全部歷史
        ctx.PrtgHostMaps.Add(new PrtgHostMapRow
        {
            MapDate = date.AddDays(-1), DeviceObjid = 9001, Ip = "10.9.9.9", HostId = 10,
            HostName = "SRV-10", MapStatus = PrtgMapStatus.Ok, Note = "昨天的舊對應", CreatedAt = now
        });

        ctx.SaveChanges();
        return date;
    }

    [Fact]
    public void 有對應的主機_回傳內容與改動前相同()
    {
        var date = SeedMap();
        var controller = CreateController(visibleHostId: 10);

        var dto = controller.Prtg(10).Data!;

        Assert.Equal(date, dto.MapDate);
        Assert.Equal(new long[] { 1001, 1002 }, dto.Devices.Select(d => d.DeviceObjid).OrderBy(x => x));

        var first = dto.Devices.Single(d => d.DeviceObjid == 1001);
        Assert.Equal("10.0.0.1", first.Ip);
        Assert.Equal(PrtgMapStatus.Ok, first.MapStatus);
        Assert.Equal("自動對應", first.Note);
        var sensor = Assert.Single(first.Sensors);
        Assert.Equal(2001, sensor.Objid);
        Assert.Equal("Ping", sensor.Name);
        Assert.Equal("ping", sensor.SensorType);
        Assert.Equal("network", sensor.Category);
        Assert.False(sensor.Paused);

        var second = dto.Devices.Single(d => d.DeviceObjid == 1002);
        Assert.Equal(PrtgMapStatus.Conflict, second.MapStatus);
        Assert.Equal("同 IP 多台", second.Note);
        Assert.Empty(second.Sensors);

        // 昨天那一列屬於同一台主機，但不是「最近一次對應」——不得出現
        Assert.DoesNotContain(dto.Devices, d => d.DeviceObjid == 9001);
    }

    [Fact]
    public void 沒有對應的主機_仍回報最近一次對應日期且裝置清單為空()
    {
        var date = SeedMap();
        var controller = CreateController(visibleHostId: 77);

        var dto = controller.Prtg(77).Data!;

        // 改動前的語意：MapDate 是「最近一次有對應資料的日期」，不是「這台主機有對應的日期」
        Assert.Equal(date, dto.MapDate);
        Assert.Empty(dto.Devices);
        Assert.False(dto.IpExcluded);
    }

    [Fact]
    public void 對應表整張讀取不再發生_以實際送出的SQL計數()
    {
        SeedMap();
        var controller = CreateController(visibleHostId: 10);

        _sql.Clear();
        controller.Prtg(10);

        var hostMapQueries = _sql
            .Where(s => s.Contains("lf_prtg_host_map", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(hostMapQueries);

        // 每一條打到對應表的查詢，不是聚合出「最近一次的日期」，就是帶 host_id 條件的單機查詢。
        // 改動前會多出一條「只有 map_date 條件」的整日全表查詢——那條就是被拔掉的浪費。
        var unscoped = hostMapQueries
            .Where(s => !s.Contains("MAX(", StringComparison.OrdinalIgnoreCase))
            .Where(s =>
            {
                var idx = s.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
                return idx < 0 || !s[idx..].Contains("host_id", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        Assert.True(unscoped.Count == 0,
            "仍有未帶 host_id 條件的對應表查詢：" + string.Join(" ／ ", unscoped));
    }

    [Fact]
    public void Store層_只回傳該主機的列且日期仍是最近一次()
    {
        var date = SeedMap();
        var store = new EfPrtgStore(NewContext);

        var (mapDate, rows) = store.GetLatestHostMapForHost(10);
        Assert.Equal(date, mapDate);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(10, r.HostId));

        var (noneDate, noneRows) = store.GetLatestHostMapForHost(999);
        Assert.Equal(date, noneDate);
        Assert.Empty(noneRows);

        // 窗內完全沒有資料時才是 (null, 空清單)
        var (emptyDate, emptyRows) = store.GetLatestHostMapForHost(10, maxLookbackDays: 30, anchor: date.AddDays(-400));
        Assert.Null(emptyDate);
        Assert.Empty(emptyRows);

        // 與整表版本的日期語意一致
        Assert.Equal(store.GetLatestHostMapWithDate(30).MapDate, mapDate);
    }

    [Fact]
    public void Sensor帶狀態且Device帶名稱_名稱一次查回()
    {
        SeedMap();
        using (var ctx = NewContext())
        {
            ctx.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 2002, DeviceObjid = 1001, Name = "CPU", SensorType = "SNMP CPU Load",
                Category = "cpu", Paused = false, Status = "Down (Acknowledged)",
                SyncedAt = DateTime.Today, CreatedAt = DateTime.Today
            });
            var ping = ctx.PrtgSensors.Single(s => s.Objid == 2001);
            ping.Status = "Up";
            ctx.SaveChanges();
        }
        var controller = CreateController(visibleHostId: 10);

        _sql.Clear();
        var dto = controller.Prtg(10).Data!;

        var first = dto.Devices.Single(d => d.DeviceObjid == 1001);
        Assert.Equal("SW-A", first.Name);
        Assert.Equal("Up", first.Sensors.Single(s => s.Objid == 2001).Status);
        Assert.Equal("Down (Acknowledged)", first.Sensors.Single(s => s.Objid == 2002).Status);

        // 1002 在裝置鏡像表裡沒有列：名稱為 null（前端維持原標題格式）
        Assert.Null(dto.Devices.Single(d => d.DeviceObjid == 1002).Name);

        // 兩台 device 只查一次裝置表，不逐 device 查
        var deviceQueries = _sql.Count(s => s.Contains("lf_prtg_device", StringComparison.OrdinalIgnoreCase)
                                            && !s.Contains("lf_prtg_sensor", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, deviceQueries);
    }
}
