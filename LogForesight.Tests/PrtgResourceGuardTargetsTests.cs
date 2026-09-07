using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgResourceGuardTargetsTests : IDisposable
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

    [Fact]
    public void Resolve_覆寫清單優先_鏡像表為空仍回傳指定objid且不查device()
    {
        // 1. 覆寫清單優先：設定了 objid 清單時，回傳的就是那些 objid，且完全不查 device
        // （可用「即使鏡像表為空也回得出清單」來斷言）。
        var store = CreateStore();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardSensorObjids = new List<string> { "9001", "9002" }
        };
        var sentinels = new List<Sentinel>
        {
            new() { Name = "S1", BaseUrl = "https://192.168.1.10:8443" }
        };

        var result = PrtgResourceGuardTargets.Resolve(store, settings, sentinels, console);

        Assert.Equal(new long[] { 9001, 9002 }, result.SensorObjids);
        Assert.Empty(store.GetAllDevices());
    }

    [Fact]
    public void Resolve_覆寫清單含非數字項目_略過非數字項目並輸出警告()
    {
        // 2. 覆寫清單含非數字項目：略過該項並仍回傳其餘，且有警告輸出。
        var store = CreateStore();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardSensorObjids = new List<string> { "9001", "not-a-number", "9002" }
        };

        var result = PrtgResourceGuardTargets.Resolve(store, settings, Array.Empty<Sentinel>(), console);

        Assert.Equal(new long[] { 9001, 9002 }, result.SensorObjids);
        Assert.Contains(console.Lines, l => l.Contains("not-a-number"));
    }

    [Fact]
    public void Resolve_依位址命中device且只取cpu與memory()
    {
        // 3. 依位址命中 device 且只取 cpu／memory：造一台 device（Ip 為某位址）與四個 sensor
        // （cpu、memory、disk、traffic 各一）→ 只回 cpu 與 memory 兩個。
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1001, Name = "AppSrv", Ip = "192.168.1.50" }
        }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1001, Name = "CPU", Category = "cpu", Paused = false },
            new PrtgSensorRow { Objid = 2002, DeviceObjid = 1001, Name = "RAM", Category = "memory", Paused = false },
            new PrtgSensorRow { Objid = 2003, DeviceObjid = 1001, Name = "Disk C:", Category = "disk", Paused = false },
            new PrtgSensorRow { Objid = 2004, DeviceObjid = 1001, Name = "NIC Traffic", Category = "traffic", Paused = false }
        }, now);

        var console = new TestConsole();
        var settings = new SystemSettings();
        var sentinels = new List<Sentinel>
        {
            new() { Name = "S1", BaseUrl = "https://192.168.1.50:8443" }
        };

        var result = PrtgResourceGuardTargets.Resolve(store, settings, sentinels, console);

        Assert.Equal(2, result.SensorObjids.Count);
        Assert.Contains(2001, result.SensorObjids);
        Assert.Contains(2002, result.SensorObjids);
        Assert.DoesNotContain(2003, result.SensorObjids);
        Assert.DoesNotContain(2004, result.SensorObjids);
        Assert.Equal("cpu", result.SensorCategories[2001]);
        Assert.Equal("memory", result.SensorCategories[2002]);
    }

    [Fact]
    public void Resolve_暫停的sensor不納入()
    {
        // 4. 暫停的 sensor 不納入：同上但 cpu sensor 的 Paused 為 true → 只回 memory。
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1001, Name = "AppSrv", Ip = "192.168.1.50" }
        }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1001, Name = "CPU", Category = "cpu", Paused = true },
            new PrtgSensorRow { Objid = 2002, DeviceObjid = 1001, Name = "RAM", Category = "memory", Paused = false },
            new PrtgSensorRow { Objid = 2003, DeviceObjid = 1001, Name = "Disk C:", Category = "disk", Paused = false },
            new PrtgSensorRow { Objid = 2004, DeviceObjid = 1001, Name = "NIC Traffic", Category = "traffic", Paused = false }
        }, now);

        var console = new TestConsole();
        var settings = new SystemSettings();
        var sentinels = new List<Sentinel>
        {
            new() { Name = "S1", BaseUrl = "https://192.168.1.50:8443" }
        };

        var result = PrtgResourceGuardTargets.Resolve(store, settings, sentinels, console);

        Assert.Single(result.SensorObjids);
        Assert.Equal(2002, result.SensorObjids[0]);
    }

    [Fact]
    public void Resolve_位址比對不分大小寫()
    {
        // 5. 位址比對不分大小寫：device 的 Ip 存 SRV-A.example.local、Sentinel 的 BaseUrl 為 https://srv-a.example.local:8443/ → 仍命中。
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1001, Name = "SRV-A", Ip = "SRV-A.example.local" }
        }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1001, Name = "CPU Load", Category = "cpu", Paused = false }
        }, now);

        var console = new TestConsole();
        var settings = new SystemSettings();
        var sentinels = new List<Sentinel>
        {
            new() { Name = "S1", BaseUrl = "https://srv-a.example.local:8443/" }
        };

        var result = PrtgResourceGuardTargets.Resolve(store, settings, sentinels, console);

        Assert.Single(result.SensorObjids);
        Assert.Equal(2001, result.SensorObjids[0]);
    }

    [Fact]
    public void Resolve_corehealth_fallback_PRTG對不到device時以CoreHealth感測器fallback()
    {
        // 6. corehealth fallback：PRTG 位址對不到任何 device，但有一台 device 底下有 SensorType 為 Core Health 的 sensor → 該 sensor 被納入。
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1099, Name = "PRTG Core Server", Ip = "10.0.0.99" }
        }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2099, DeviceObjid = 1099, Name = "PRTG Core Health", SensorType = "Core Health", Paused = false }
        }, now);

        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgUrl = "https://unmatched-prtg.local"
        };

        var result = PrtgResourceGuardTargets.Resolve(store, settings, Array.Empty<Sentinel>(), console);

        Assert.Single(result.SensorObjids);
        Assert.Equal(2099, result.SensorObjids[0]);
        Assert.Equal(PrtgResourceGuardTargets.CategoryCoreHealth, result.SensorCategories[2099]);
    }

    [Fact]
    public void Resolve_一個都沒找到時回空清單並警告_不擲例外()
    {
        // 7. 一個都沒找到時回空清單並警告：鏡像表為空、覆寫清單也空 → 回傳空清單、IRunConsole 有警告輸出、不擲例外。
        var store = CreateStore();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgUrl = "https://prtg.notfound.local"
        };
        var sentinels = new List<Sentinel>
        {
            new() { Name = "S1", BaseUrl = "https://sentinel.notfound.local:8443" }
        };

        var result = PrtgResourceGuardTargets.Resolve(store, settings, sentinels, console);

        Assert.Empty(result.SensorObjids);
        Assert.Empty(result.SensorCategories);
        Assert.NotEmpty(console.Lines);
        Assert.Contains(console.Lines, l => l.Contains("未偵測到任何受監看"));
    }
}
