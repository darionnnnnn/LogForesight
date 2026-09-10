using LogForesight.Core;
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

    /// <summary>
    /// 呼叫受測方法。位址解析器預設用正式實作——測試位址都是合法 IP 或對不到的名稱，
    /// 第一層純語法正規化與名稱字面比對就足夠，不會真的去查 DNS。
    /// </summary>
    private static PrtgResourceGuardTargetResult ResolveTargets(
        EfPrtgStore store,
        SystemSettings settings,
        IReadOnlyList<Sentinel> sentinels,
        IRunConsole console,
        IPrtgAddressResolver? resolver = null,
        bool ignoreOverride = false)
        => PrtgResourceGuardTargets.Resolve(
            new PrtgMirrorGuardSource(store), settings, sentinels, console,
            resolver ?? new PrtgAddressResolver(), ignoreOverride);

    /// <summary>可控的位址解析器：先走純語法正規化，對不到時查這張表。</summary>
    private sealed class FakeResolver : IPrtgAddressResolver
    {
        private readonly Dictionary<string, string?> _map;

        public FakeResolver(Dictionary<string, string?> map)
        {
            _map = new Dictionary<string, string?>(map, StringComparer.OrdinalIgnoreCase);
        }

        public string? Resolve(string? value)
        {
            var normalized = PrtgAddress.Normalize(value);
            if (normalized != null) return normalized;

            var token = PrtgAddress.HostToken(value);
            if (token == null) return null;
            return _map.TryGetValue(token, out var mapped) ? mapped : null;
        }
    }

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

        var result = ResolveTargets(store, settings, sentinels, console);

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

        var result = ResolveTargets(store, settings, Array.Empty<Sentinel>(), console);

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

        var result = ResolveTargets(store, settings, sentinels, console);

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

        var result = ResolveTargets(store, settings, sentinels, console);

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

        var result = ResolveTargets(store, settings, sentinels, console);

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

        var result = ResolveTargets(store, settings, Array.Empty<Sentinel>(), console);

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

        var result = ResolveTargets(store, settings, sentinels, console);

        Assert.Empty(result.SensorObjids);
        Assert.Empty(result.SensorCategories);
        Assert.NotEmpty(console.Lines);
        Assert.Contains(console.Lines, l => l.Contains("未偵測到任何受監看"));
    }
    /// <summary>
    /// 批次D：維護頁的「自動偵測並填入」要能在覆寫清單非空時重抓一份。
    /// 不忽略覆寫的話，Resolve 會在第一步就短路、只把手填值原樣吐回來——
    /// 而「已經手填了一些 objid，想重抓」正是這顆按鈕最常見的用法。
    /// </summary>
    [Fact]
    public void Resolve_ignoreOverride為true時跳過覆寫清單改走自動偵測()
    {
        var store = CreateStore();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardSensorObjids = new List<string> { "9001", "9002" }
        };

        // 鏡像表為空 → 自動偵測找不到任何 sensor，但重點是「沒有回傳那兩個手填 objid」
        var result = ResolveTargets(
            store, settings, Array.Empty<Sentinel>(), console, ignoreOverride: true);

        Assert.DoesNotContain(9001L, result.SensorObjids);
        Assert.DoesNotContain(9002L, result.SensorObjids);
    }

    [Fact]
    public void Resolve_預設仍是覆寫優先()
    {
        // 守門執行本身一律走覆寫優先——那是它的既定契約，不得被這個新參數改變。
        var store = CreateStore();
        var console = new TestConsole();
        var settings = new SystemSettings
        {
            PrtgResourceGuardSensorObjids = new List<string> { "9001" }
        };

        var result = ResolveTargets(store, settings, Array.Empty<Sentinel>(), console);

        Assert.Equal(new long[] { 9001 }, result.SensorObjids);
    }

    [Fact]
    public void Resolve_device的Ip帶port時仍命中()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1501, Name = "PRTG Core", Ip = "10.216.7.55:8080" }
        }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2501, DeviceObjid = 1501, Name = "CPU Load", Category = "cpu", Paused = false }
        }, now);

        var console = new TestConsole();
        var settings = new SystemSettings { PrtgUrl = "https://10.216.7.55" };

        var result = ResolveTargets(store, settings, Array.Empty<Sentinel>(), console);

        Assert.Contains(2501L, result.SensorObjids);
    }

    [Fact]
    public void Resolve_Sentinel位址為DNS名稱時以解析出的IP命中device()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1502, Name = "SRV-B", Ip = "10.7.7.7" }
        }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2502, DeviceObjid = 1502, Name = "Memory", Category = "memory", Paused = false }
        }, now);

        var console = new TestConsole();
        var settings = new SystemSettings();
        var sentinels = new List<Sentinel>
        {
            new() { Name = "S1", BaseUrl = "https://srv-b.example.local:8443/" }
        };
        var resolver = new FakeResolver(new Dictionary<string, string?>
        {
            ["srv-b.example.local"] = "10.7.7.7"
        });

        var result = ResolveTargets(store, settings, sentinels, console, resolver);

        Assert.Contains(2502L, result.SensorObjids);
    }

    /// <summary>可控的來源：直接餵裝置與感測器，用來證明判定邏輯與來源實作無關。</summary>
    private sealed class FakeGuardSource : IPrtgResourceGuardSource
    {
        private readonly IReadOnlyList<PrtgDeviceRow> _devices;
        private readonly IReadOnlyList<PrtgSensorRow> _sensors;

        public FakeGuardSource(IReadOnlyList<PrtgDeviceRow> devices, IReadOnlyList<PrtgSensorRow> sensors, string label)
        {
            _devices = devices;
            _sensors = sensors;
            SourceLabel = label;
        }

        public string SourceLabel { get; }

        public IReadOnlyList<PrtgDeviceRow> GetDevices() => _devices;

        public IReadOnlyList<PrtgSensorRow> GetSensors() => _sensors;
    }

    /// <summary>
    /// 同一組資料，鏡像來源與**真的走 HTTP 的即時來源**要得到同一組 objid——
    /// 判定邏輯只有一份，鏡像與即時查詢的差別只在資料哪裡來（docs/PRTG-SPEC.md §12）。
    /// 兩邊必須是不同的實作才有意義：都餵同一份假清單的話，斷言在任何實作下都會過。
    /// </summary>
    [Fact]
    public void Resolve_鏡像與即時兩種來源得到相同結果()
    {
        var now = DateTime.Now;
        var store = CreateStore();
        store.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1601, Name = "SRV-C", Ip = "10.6.6.6" } }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2601, DeviceObjid = 1601, Name = "CPU Load", SensorType = "SNMP CPU Load", Category = "cpu", Paused = false }
        }, now);

        var handler = new StubHandler
        {
            OnSend = req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("content=devices"))
                    return Json("{\"devices\":[{\"objid\":1601,\"device\":\"SRV-C\",\"host\":\"10.6.6.6\"}]}");
                if (url.Contains("content=sensors"))
                    return Json("{\"sensors\":[{\"objid\":2601,\"parentid\":1601,\"sensor\":\"CPU Load\",\"type\":\"SNMP CPU Load\",\"status\":\"Up\"}]}");
                return Json("{}");
            }
        };

        var settings = new SystemSettings();
        var sentinels = new List<Sentinel> { new() { Name = "S1", BaseUrl = "https://10.6.6.6:8443" } };

        var fromMirror = PrtgResourceGuardTargets.Resolve(
            new PrtgMirrorGuardSource(store), settings, sentinels, new TestConsole(), new PrtgAddressResolver());

        using var client = new PrtgClient("https://prtg.example.com", "token123", 30, true, handler);
        var fromLive = PrtgResourceGuardTargets.Resolve(
            new PrtgLiveGuardSource(client), settings, sentinels, new TestConsole(), new PrtgAddressResolver());

        Assert.Equal(fromMirror.SensorObjids, fromLive.SensorObjids);
        Assert.Contains(2601L, fromLive.SensorObjids);
        // 即時來源真的打過 HTTP（不是又讀了鏡像）
        Assert.Contains(handler.RequestedUrls, u => u.Contains("content=devices"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> OnSend { get; set; } =
            _ => throw new InvalidOperationException("測試未設定 OnSend");

        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (RequestedUrls) RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(OnSend(request));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    /// <summary>
    /// 找不到裝置時的訊息要說出資料是哪裡來的：讀鏡像的「找不到」多半是還沒同步，
    /// 直接查 PRTG 的「找不到」才代表 PRTG 上真的沒有。兩者處置完全不同。
    /// </summary>
    [Fact]
    public void Resolve_找不到裝置時訊息標明資料來源()
    {
        var settings = new SystemSettings();
        var sentinels = new List<Sentinel> { new() { Name = "S1", BaseUrl = "https://10.7.7.7:8443" } };

        var mirrorConsole = new TestConsole();
        PrtgResourceGuardTargets.Resolve(
            new FakeGuardSource(new List<PrtgDeviceRow>(), new List<PrtgSensorRow>(), "mirror"),
            settings, sentinels, mirrorConsole, new PrtgAddressResolver());

        var liveConsole = new TestConsole();
        PrtgResourceGuardTargets.Resolve(
            new FakeGuardSource(new List<PrtgDeviceRow>(), new List<PrtgSensorRow>(), "live"),
            settings, sentinels, liveConsole, new PrtgAddressResolver());

        Assert.Contains(mirrorConsole.Lines, l => l.Contains("同步結構與對應"));
        Assert.Contains(liveConsole.Lines, l => l.Contains("已直接查詢 PRTG"));
        Assert.DoesNotContain(liveConsole.Lines, l => l.Contains("同步結構與對應"));
    }

    /// <summary>記錄每次解析請求的假解析器：用來斷言「裝置側到底有沒有送 DNS」。</summary>
    private sealed class RecordingResolver : IPrtgAddressResolver
    {
        private readonly Dictionary<string, string?> _map;
        public List<string> DnsCalls { get; } = new();

        public RecordingResolver(Dictionary<string, string?> map)
        {
            _map = new Dictionary<string, string?>(map, StringComparer.OrdinalIgnoreCase);
        }

        public string? Resolve(string? value)
        {
            var normalized = PrtgAddress.Normalize(value);
            if (normalized != null) return normalized;
            var token = PrtgAddress.HostToken(value);
            if (token == null) return null;
            DnsCalls.Add(token);
            return _map.TryGetValue(token, out var mapped) ? mapped : null;
        }
    }

    [Fact]
    public void Resolve_裝置host為壞掉的IPv4佔位值_裝置側完全不送DNS且其他裝置照常命中()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1601, Name = "Placeholder", Ip = "10.2xx.x.x" },
            new PrtgDeviceRow { Objid = 1602, Name = "NetIQ", Ip = "10.20.30.40" }
        }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2601, DeviceObjid = 1601, Name = "CPU", Category = "cpu", Paused = false },
            new PrtgSensorRow { Objid = 2602, DeviceObjid = 1602, Name = "CPU", Category = "cpu", Paused = false }
        }, now);

        var console = new TestConsole();
        var resolver = new RecordingResolver(new Dictionary<string, string?>());
        var sentinels = new List<Sentinel> { new() { Name = "S1", BaseUrl = "https://10.20.30.40:8443" } };

        var result = ResolveTargets(store, new SystemSettings(), sentinels, console, resolver);

        Assert.Equal(new[] { 2602L }, result.SensorObjids);
        Assert.Empty(resolver.DnsCalls);
    }

    [Fact]
    public void Resolve_來源與裝置同名稱且DNS皆失敗_字面比對命中且裝置側不送DNS()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1611, Name = "NetIQ", Ip = "netiq.corp.local" },
            new PrtgDeviceRow { Objid = 1612, Name = "Other", Ip = "other.corp.local" }
        }, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2611, DeviceObjid = 1611, Name = "CPU", Category = "cpu", Paused = false },
            new PrtgSensorRow { Objid = 2612, DeviceObjid = 1612, Name = "CPU", Category = "cpu", Paused = false }
        }, now);

        var console = new TestConsole();
        var resolver = new RecordingResolver(new Dictionary<string, string?>());
        var sentinels = new List<Sentinel> { new() { Name = "S1", BaseUrl = "https://netiq.corp.local:8443" } };

        var result = ResolveTargets(store, new SystemSettings(), sentinels, console, resolver);

        Assert.Equal(new[] { 2611L }, result.SensorObjids);
        // 只有來源位址本身查過 DNS，裝置側零次
        Assert.All(resolver.DnsCalls, c => Assert.Equal("netiq.corp.local", c));
    }

    [Fact]
    public void Resolve_名稱型裝置超過預算_解析次數受限並輸出警告()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        var devices = Enumerable.Range(1, 30)
            .Select(i => new PrtgDeviceRow { Objid = 1700 + i, Name = $"D{i}", Ip = $"dev{i:00}.corp.local" })
            .ToList();
        store.UpsertDevices(devices, now);
        store.UpsertSensors(devices
            .Select(d => new PrtgSensorRow { Objid = 1000 + d.Objid, DeviceObjid = d.Objid, Name = "CPU", Category = "cpu", Paused = false })
            .ToList(), now);

        var console = new TestConsole();
        var resolver = new RecordingResolver(new Dictionary<string, string?>());
        var sentinels = new List<Sentinel> { new() { Name = "S1", BaseUrl = "https://10.1.1.1:8443" } };

        var result = ResolveTargets(store, new SystemSettings(), sentinels, console, resolver);

        Assert.Empty(result.SensorObjids);
        var deviceCalls = resolver.DnsCalls.Where(c => c.StartsWith("dev", StringComparison.Ordinal)).Distinct().Count();
        Assert.Equal(20, deviceCalls);
        Assert.Contains(console.Lines, l => l.Contains("已達上限 20 次") && l.Contains("10 台"));
    }

    [Fact]
    public void Resolve_命中裝置在預算之後_會被漏掉但有警告_文件化的取捨()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        var devices = Enumerable.Range(1, 25)
            .Select(i => new PrtgDeviceRow { Objid = 1800 + i, Name = $"D{i}", Ip = $"dev{i:00}.corp.local" })
            .ToList();
        store.UpsertDevices(devices, now);
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2825, DeviceObjid = 1825, Name = "CPU", Category = "cpu", Paused = false }
        }, now);

        var console = new TestConsole();
        var resolver = new RecordingResolver(new Dictionary<string, string?> { ["dev25.corp.local"] = "10.1.1.1" });
        var sentinels = new List<Sentinel> { new() { Name = "S1", BaseUrl = "https://10.1.1.1:8443" } };

        var result = ResolveTargets(store, new SystemSettings(), sentinels, console, resolver);

        Assert.Empty(result.SensorObjids);
        Assert.Contains(console.Lines, l => l.Contains("已達上限"));
    }

    [Fact]
    public void Resolve_兩個來源位址共用預算_略過台數不重複計()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        var devices = Enumerable.Range(1, 30)
            .Select(i => new PrtgDeviceRow { Objid = 1900 + i, Name = $"D{i}", Ip = $"dev{i:00}.corp.local" })
            .ToList();
        store.UpsertDevices(devices, now);

        var console = new TestConsole();
        var resolver = new RecordingResolver(new Dictionary<string, string?>());
        var sentinels = new List<Sentinel>
        {
            new() { Name = "S1", BaseUrl = "https://10.1.1.1:8443" },
            new() { Name = "S2", BaseUrl = "https://10.1.1.2:8443" }
        };

        ResolveTargets(store, new SystemSettings(), sentinels, console, resolver);

        var deviceCalls = resolver.DnsCalls.Where(c => c.StartsWith("dev", StringComparison.Ordinal)).Distinct().Count();
        Assert.Equal(20, deviceCalls);
        Assert.Contains(console.Lines, l => l.Contains("已達上限 20 次") && l.Contains("其餘 10 台"));
    }

    [Fact]
    public void Resolve_名稱型裝置恰好等於預算_不印上限警告()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        var devices = Enumerable.Range(1, 20)
            .Select(i => new PrtgDeviceRow { Objid = 2000 + i, Name = $"D{i}", Ip = $"dev{i:00}.corp.local" })
            .ToList();
        store.UpsertDevices(devices, now);

        var console = new TestConsole();
        var resolver = new RecordingResolver(new Dictionary<string, string?>());
        var sentinels = new List<Sentinel> { new() { Name = "S1", BaseUrl = "https://10.1.1.1:8443" } };

        ResolveTargets(store, new SystemSettings(), sentinels, console, resolver);

        Assert.DoesNotContain(console.Lines, l => l.Contains("已達上限"));
    }
}
