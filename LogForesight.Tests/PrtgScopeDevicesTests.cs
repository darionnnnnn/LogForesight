using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgScopeDevicesTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfPrtgStore CreateStore() => new(_fx.NewContext);

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    /// <summary>
    /// 位址解析器用正式實作：測試位址都是合法 IP，第一層純語法正規化就會命中，不會真的去查 DNS。
    /// </summary>
    private static PrtgScopeResult Compute(EfPrtgStore store, IHostStore hostStore, SystemSettings settings, IReadOnlyList<Sentinel> sentinels)
        => PrtgScopeDevices.Compute(store, hostStore, new PrtgMirrorGuardSource(store), settings, sentinels,
            new TestConsole(), new PrtgAddressResolver());

    private static PrtgHostMapRow MapRow(long deviceObjid, string ip, int? hostId, string status) => new()
    {
        MapDate = DateTime.Today,
        DeviceObjid = deviceObjid,
        Ip = ip,
        HostId = hostId,
        HostName = hostId.HasValue ? $"srv-{hostId}" : null,
        MapStatus = status,
        Note = null,
        CreatedAt = DateTime.Now
    };

    /// <summary>
    /// 種下組成測試的完整資料。PRTG 連線網址指向裝置 1 的 IP：讓守門自動偵測的 PRTG 主機命中裝置 1，
    /// corehealth fallback 不會觸發——裝置 8 只能靠「corehealth 所在裝置」那一項進集合。
    /// </summary>
    private (EfPrtgStore Store, FakeHostStore HostStore, SystemSettings Settings, List<Sentinel> Sentinels) SeedComposition(bool host34Active = true)
    {
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1, Name = "D1", Ip = "10.0.0.1" },
            new PrtgDeviceRow { Objid = 2, Name = "D2", Ip = "10.0.0.2" },
            new PrtgDeviceRow { Objid = 3, Name = "D3", Ip = "10.0.0.34" },
            new PrtgDeviceRow { Objid = 4, Name = "D4", Ip = "10.0.0.34" },
            new PrtgDeviceRow { Objid = 5, Name = "D5", Ip = "10.0.0.5" },
            new PrtgDeviceRow { Objid = 6, Name = "D6", Ip = "10.0.0.6" },
            new PrtgDeviceRow { Objid = 7, Name = "D7", Ip = "10.0.0.7" },
            new PrtgDeviceRow { Objid = 8, Name = "D8", Ip = "10.0.0.8" },
            new PrtgDeviceRow { Objid = 9, Name = "D9", Ip = "10.0.0.90" },
            new PrtgDeviceRow { Objid = 10, Name = "D10", Ip = "10.0.0.90" }
        }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 801, DeviceObjid = 8, Name = "Core Health", SensorType = "corehealth", Paused = false },
            new PrtgSensorRow { Objid = 601, DeviceObjid = 6, Name = "Ping", SensorType = "ping", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(DateTime.Today, new List<PrtgHostMapRow>
        {
            MapRow(1, "10.0.0.1", 1, PrtgMapStatus.Ok),
            MapRow(2, "10.0.0.2", 2, PrtgMapStatus.Conflict),
            MapRow(3, "10.0.0.34", null, PrtgMapStatus.Conflict),
            MapRow(4, "10.0.0.34", null, PrtgMapStatus.Conflict),
            MapRow(9, "10.0.0.90", null, PrtgMapStatus.Conflict),
            MapRow(10, "10.0.0.90", null, PrtgMapStatus.Conflict),
            MapRow(6, "10.0.0.6", null, PrtgMapStatus.Unmatched)
        });

        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 5,
            HostId = 1,
            Note = "人工",
            CreatedBy = "admin",
            CreatedAt = now
        });

        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 1, HostName = "srv-1", IpAddress = "10.0.0.1", Active = true });
            hosts.Add(new WebHost { HostId = 2, HostName = "srv-2a", IpAddress = "10.0.0.2", Active = true });
            hosts.Add(new WebHost { HostId = 22, HostName = "srv-2b", IpAddress = "10.0.0.2", Active = true });
            hosts.Add(new WebHost { HostId = 34, HostName = "srv-34", IpAddress = "10.0.0.34", Active = host34Active });
        });

        var settings = new SystemSettings { PrtgUrl = "https://10.0.0.1", PrtgResourceGuardEnabled = true };
        var sentinels = new List<Sentinel> { new() { Name = "S1", BaseUrl = "https://10.0.0.7:8443" } };
        return (store, hostStore, settings, sentinels);
    }

    [Fact]
    public void Compute_四項聯集_集合恰為預期且與主機無關的conflict與unmatched不收()
    {
        var (store, hostStore, settings, sentinels) = SeedComposition();

        var result = Compute(store, hostStore, settings, sentinels);

        Assert.Equal(new HashSet<long> { 1, 2, 3, 4, 5, 7, 8 }, result.DeviceObjids.ToHashSet());
        Assert.Equal(1, result.Mapped);
        Assert.Equal(3, result.Conflict);
        Assert.Equal(1, result.Manual);
        // 守門：PRTG 主機命中裝置 1、Sentinel 命中裝置 7、corehealth 所在裝置 8
        Assert.Equal(3, result.Guard);
    }

    [Fact]
    public void Compute_主機停用_其IP下的同IP多裝置conflict不收()
    {
        var (store, hostStore, settings, sentinels) = SeedComposition(host34Active: false);

        var result = Compute(store, hostStore, settings, sentinels);

        Assert.DoesNotContain(3L, result.DeviceObjids);
        Assert.DoesNotContain(4L, result.DeviceObjids);
        Assert.Equal(new HashSet<long> { 1, 2, 5, 7, 8 }, result.DeviceObjids.ToHashSet());
    }

    [Fact]
    public void Compute_覆寫清單的sensor所在裝置在集合_鏡像沒有的objid不擲例外()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 11, Name = "D11", Ip = "10.0.0.11" } }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 1101, DeviceObjid = 11, Name = "CPU", Category = "cpu", Paused = false }
        }, now);
        var settings = new SystemSettings
        {
            PrtgResourceGuardSensorObjids = new List<string> { "1101", "999999", "not-a-number" }
        };

        var result = Compute(store, new FakeHostStore(), settings, Array.Empty<Sentinel>());

        Assert.Equal(new HashSet<long> { 11 }, result.DeviceObjids.ToHashSet());
        Assert.Equal(1, result.Guard);
    }

    [Fact]
    public void Compute_守門停用_Sentinel裝置仍在集合()
    {
        var (store, hostStore, settings, sentinels) = SeedComposition();
        settings.PrtgResourceGuardEnabled = false;

        var result = Compute(store, hostStore, settings, sentinels);

        Assert.Contains(7L, result.DeviceObjids);
    }

    [Fact]
    public void Compute_覆寫清單有值_自動偵測的Sentinel裝置仍在集合()
    {
        var (store, hostStore, settings, sentinels) = SeedComposition();
        settings.PrtgResourceGuardSensorObjids = new List<string> { "801" };

        var result = Compute(store, hostStore, settings, sentinels);

        Assert.Contains(7L, result.DeviceObjids);
        Assert.Contains(8L, result.DeviceObjids);
    }

    [Fact]
    public void Compute_全空_回空集合且四個計數皆為零()
    {
        var store = CreateStore();

        var result = Compute(store, new FakeHostStore(), new SystemSettings(), Array.Empty<Sentinel>());

        Assert.Empty(result.DeviceObjids);
        Assert.Equal(0, result.Mapped);
        Assert.Equal(0, result.Conflict);
        Assert.Equal(0, result.Manual);
        Assert.Equal(0, result.Guard);
    }
}
