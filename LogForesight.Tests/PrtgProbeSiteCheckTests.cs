using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 環境探測「站台對照」段：取數範圍試算（純本機）＋範圍內裝置逐台查詢實測（唯讀打 PRTG）。
/// store 用 EfSqliteFixture、PRTG 用記錄請求 URL 的 HttpMessageHandler 替身，與 PrtgScopeDevicesTests／PrtgProbeRunnerTests 同一套寫法。
/// PrtgProbeService 沒有既有的專屬測試檔，接線的兩條測試也寫在這裡。
/// </summary>
public class PrtgProbeSiteCheckTests : IDisposable
{
    private const string BaseUrl = "https://prtg.example.com";
    private const string SampleToken = "test-token-xyz";

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
        public string Text => string.Join("\n", Lines);
    }

    /// <summary>
    /// PRTG 替身：依 URL 的 content 與 id 回應。記錄每一個請求 URL 供斷言「只打了範圍內的裝置」。
    /// </summary>
    private sealed class StubPrtg : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();
        public Dictionary<long, long[]> SensorsByDevice { get; } = new();
        public Dictionary<long, long[]> MessagesByDevice { get; } = new();
        public HashSet<long> ThrowForDevice { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);

            var m = Regex.Match(url, @"[?&]id=(\d+)(&|$)");
            var id = m.Success ? long.Parse(m.Groups[1].Value) : -1;
            if (ThrowForDevice.Contains(id))
                throw new HttpRequestException("模擬連線中斷");

            string json;
            if (url.Contains("content=sensors"))
                json = Table("sensors", SensorsByDevice.TryGetValue(id, out var s) ? s : Array.Empty<long>());
            else if (url.Contains("content=messages"))
                json = Table("messages", MessagesByDevice.TryGetValue(id, out var msg) ? msg : Array.Empty<long>());
            else
                json = "{}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        private static string Table(string content, long[] objids)
        {
            var rows = string.Join(",", objids.Select(o => $"{{\"objid\":{o},\"datetime\":\"2026/09/18 10:00:00\"}}"));
            return $"{{\"treesize\":{objids.Length},\"{content}\":[{rows}]}}";
        }

        /// <summary>請求中出現過的 id= 值（整段比對，避免 id=1 誤中 id=10）。</summary>
        public HashSet<long> RequestedDeviceIds() => RequestedUrls
            .Select(u => Regex.Match(u, @"[?&]id=(\d+)(&|$)"))
            .Where(m => m.Success)
            .Select(m => long.Parse(m.Groups[1].Value))
            .ToHashSet();
    }

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

    private static PrtgSensorRow Sensor(long objid, long deviceObjid, string? status = "Up") => new()
    {
        Objid = objid,
        DeviceObjid = deviceObjid,
        Name = $"S{objid}",
        SensorType = "ping",
        Status = status,
        Paused = false
    };

    /// <summary>裝置 1、2 為 ok 對應（各 2 顆感測器），裝置 3 為 unmatched（3 顆感測器）。</summary>
    private EfPrtgStore SeedTwoMappedOneUnmatched()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1, Name = "D1", Ip = "10.0.0.1" },
            new PrtgDeviceRow { Objid = 2, Name = "D2", Ip = "10.0.0.2" },
            new PrtgDeviceRow { Objid = 3, Name = "D3", Ip = "10.0.0.3" }
        }, now);
        store.UpsertSensors(new[]
        {
            Sensor(11, 1), Sensor(12, 1),
            Sensor(21, 2), Sensor(22, 2),
            Sensor(31, 3), Sensor(32, 3), Sensor(33, 3)
        }, now);
        store.ReplaceHostMapForDate(DateTime.Today, new List<PrtgHostMapRow>
        {
            MapRow(1, "10.0.0.1", 1, PrtgMapStatus.Ok),
            MapRow(2, "10.0.0.2", 2, PrtgMapStatus.Ok),
            MapRow(3, "10.0.0.3", null, PrtgMapStatus.Unmatched)
        });
        return store;
    }

    /// <summary>守門偵測來源替身：直接給裝置與感測器，記錄讀取次數。</summary>
    private sealed class StubGuardSource : IPrtgResourceGuardSource
    {
        public List<PrtgDeviceRow> Devices { get; } = new();
        public List<PrtgSensorRow> Sensors { get; } = new();
        public bool ThrowOnSensors { get; set; }
        public int DeviceReads { get; private set; }
        public int SensorReads { get; private set; }
        public string SourceLabel => "live";

        public IReadOnlyList<PrtgDeviceRow> GetDevices()
        {
            DeviceReads++;
            return Devices;
        }

        public IReadOnlyList<PrtgSensorRow> GetSensors()
        {
            SensorReads++;
            if (ThrowOnSensors) throw new HttpRequestException("模擬感測器全表逾時");
            return Sensors;
        }
    }

    private static async Task<TestConsole> RunAsync(EfPrtgStore store, StubPrtg stub, SystemSettings? settings = null,
        StubGuardSource? guard = null, List<Sentinel>? sentinels = null)
    {
        var console = new TestConsole();
        using var client = new PrtgClient(BaseUrl, SampleToken, 30, false, stub);
        await PrtgProbeSiteCheck.RunAsync(client, console, store, new FakeHostStore(),
            settings ?? new SystemSettings(), sentinels ?? new List<Sentinel>(), guard ?? new StubGuardSource());
        return console;
    }

    // ── [S3] 資源守門目標偵測 ────────────────────────────────────────

    /// <summary>
    /// 守門來源：裝置 1（10.0.0.1，cpu）、裝置 3（10.0.0.3，Core Health）。
    /// 搭配 <see cref="SeedTwoMappedOneUnmatched"/>：裝置 1 在取數範圍內、裝置 3 不在。
    /// </summary>
    private static StubGuardSource GuardWithCoreHealthOn3()
    {
        var g = new StubGuardSource();
        g.Devices.Add(new PrtgDeviceRow { Objid = 1, Name = "D1", Ip = "10.0.0.1" });
        g.Devices.Add(new PrtgDeviceRow { Objid = 3, Name = "D3", Ip = "10.0.0.3" });
        g.Sensors.Add(new PrtgSensorRow { Objid = 11, DeviceObjid = 1, Name = "CPU", Category = "cpu", Paused = false });
        g.Sensors.Add(new PrtgSensorRow { Objid = 31, DeviceObjid = 3, Name = "Core", SensorType = "Core Health", Paused = false });
        return g;
    }

    [Fact]
    public async Task S3_PRTG位址命中且都在範圍內_印位址命中與都在範圍內()
    {
        var store = SeedTwoMappedOneUnmatched();
        var guard = GuardWithCoreHealthOn3();

        var console = await RunAsync(store, new StubPrtg(), new SystemSettings { PrtgUrl = "https://10.0.0.1" }, guard);

        Assert.Contains("[S3] 資源守門目標偵測（直接查 PRTG）", console.Lines);
        Assert.Contains("PRTG 主機：以位址比對命中 1 台裝置：D1(1)", console.Text);
        Assert.Contains("Sentinel 主機：未設定 Sentinel", console.Text);
        Assert.Contains("守門會監看的感測器：cpu 1 顆、memory 0 顆、corehealth 0 顆", console.Text);
        Assert.Contains("都在取數範圍內", console.Text);
        Assert.DoesNotContain("不在取數範圍內", console.Text);
    }

    [Fact]
    public async Task S3_位址對不到改以CoreHealth找到且不在範圍內_提示自動偵測並填入()
    {
        var store = SeedTwoMappedOneUnmatched();
        var guard = GuardWithCoreHealthOn3();

        var console = await RunAsync(store, new StubPrtg(), new SystemSettings { PrtgUrl = "https://10.9.9.9" }, guard);

        Assert.Contains("改以 Core Health 感測器找到裝置 D3(3)", console.Text);
        Assert.Contains("1 台守門用到的裝置不在取數範圍內：D3(3)", console.Text);
        Assert.Contains("自動偵測並填入", console.Text);
        Assert.DoesNotContain("都在取數範圍內", console.Text);
    }

    [Fact]
    public async Task S3_都找不到_印找不到且Sentinel有設定但沒命中()
    {
        var store = SeedTwoMappedOneUnmatched();
        var guard = new StubGuardSource();
        guard.Devices.Add(new PrtgDeviceRow { Objid = 1, Name = "D1", Ip = "10.0.0.1" });
        var sentinels = new List<Sentinel> { new() { Name = "S1", BaseUrl = "https://10.8.8.8:8443" } };

        var console = await RunAsync(store, new StubPrtg(), new SystemSettings { PrtgUrl = "https://10.9.9.9" }, guard, sentinels);

        Assert.Contains("PRTG 主機：找不到（位址比對不到，也沒有 Core Health 感測器）", console.Text);
        Assert.Contains("Sentinel 主機：沒有命中任何裝置", console.Text);
        // 守門自己的警告直接進探測輸出
        Assert.Contains(console.Lines, l => l.Contains("[PRTG資源守門] 找不到主機「10.8.8.8」"));
    }

    [Fact]
    public async Task S3_Sentinel命中超過5台_只列5台並加等N台()
    {
        var store = SeedTwoMappedOneUnmatched();
        var guard = new StubGuardSource();
        for (var i = 101; i <= 107; i++)
            guard.Devices.Add(new PrtgDeviceRow { Objid = i, Name = $"N{i}", Ip = "10.7.7.7" });
        var sentinels = new List<Sentinel> { new() { Name = "S1", BaseUrl = "https://10.7.7.7:8443" } };

        var console = await RunAsync(store, new StubPrtg(), new SystemSettings(), guard, sentinels);

        Assert.Contains("Sentinel 主機：命中 7 台裝置：N101(101)、N102(102)、N103(103)、N104(104)、N105(105) 等 7 台", console.Text);
        Assert.Contains("7 台守門用到的裝置不在取數範圍內", console.Text);
    }

    [Fact]
    public async Task S3_鏡像空_仍執行守門偵測但無法對照取數範圍()
    {
        var store = CreateStore();
        var guard = GuardWithCoreHealthOn3();

        var console = await RunAsync(store, new StubPrtg(), new SystemSettings { PrtgUrl = "https://10.0.0.1" }, guard);

        Assert.Contains("略過（鏡像尚未同步）", console.Text);
        Assert.Contains("PRTG 主機：", console.Text);
        Assert.Contains("無法對照取數範圍", console.Text);
        Assert.DoesNotContain("取數範圍內", console.Text);
    }

    [Fact]
    public async Task S3_覆寫清單3顆其中1顆在鏡像_印已在與不在鏡像顆數()
    {
        var store = SeedTwoMappedOneUnmatched();
        var guard = GuardWithCoreHealthOn3();
        var settings = new SystemSettings
        {
            PrtgUrl = "https://10.0.0.1",
            PrtgResourceGuardSensorObjids = new List<string> { "11", "98", "99" }
        };

        var console = await RunAsync(store, new StubPrtg(), settings, guard);

        Assert.Contains("覆寫清單 3 顆：已在鏡像 1 顆、不在鏡像 2 顆（快照服務的範圍補抓會查出所在裝置並補進鏡像）", console.Text);
    }

    [Fact]
    public async Task S3_沒有Sentinel且PRTG網址解析不出主機_略過且不查來源()
    {
        var store = SeedTwoMappedOneUnmatched();
        var guard = GuardWithCoreHealthOn3();

        var console = await RunAsync(store, new StubPrtg(), new SystemSettings { PrtgUrl = "" }, guard);

        Assert.Contains("沒有可比對的位址（未設定 Sentinel，PRTG 連線網址也解析不出主機），略過。", console.Text);
        Assert.Equal(0, guard.DeviceReads);
        Assert.Equal(0, guard.SensorReads);
    }

    [Fact]
    public async Task S3_來源讀感測器擲例外_印無法完成且S1S2不受影響()
    {
        var store = SeedTwoMappedOneUnmatched();
        var stub = new StubPrtg();
        stub.SensorsByDevice[1] = new long[] { 11, 12 };
        stub.SensorsByDevice[2] = new long[] { 21, 22 };
        stub.MessagesByDevice[1] = new long[] { 11 };
        stub.MessagesByDevice[2] = new long[] { 21 };
        var guard = GuardWithCoreHealthOn3();
        guard.ThrowOnSensors = true;

        // PRTG 網址用鏡像對不到的位址，[S1] 的守門項維持 0，與既有試算測試同一行可比
        var console = await RunAsync(store, stub, new SystemSettings { PrtgUrl = "https://10.9.9.9" }, guard);

        Assert.Contains("守門偵測無法完成（模擬感測器全表逾時）", console.Text);
        Assert.Contains("取數範圍：2 台裝置（對應 2、衝突 0、人工 0、守門 0）", console.Text);
        Assert.Contains("結論 ✓ 範圍內裝置逐台查詢狀態變更可用", console.Text);
        Assert.Contains("估算：範圍 2 台、併發 2——", console.Text);
    }

    [Fact]
    public async Task S3_在S2之後輸出_且成本說明行提到守門偵測()
    {
        var store = SeedTwoMappedOneUnmatched();
        var console = await RunAsync(store, new StubPrtg(), new SystemSettings { PrtgUrl = "https://10.0.0.1" }, GuardWithCoreHealthOn3());

        var s2 = console.Lines.IndexOf("[S2] 範圍內裝置逐台查詢實測");
        var s3 = console.Lines.IndexOf("[S3] 資源守門目標偵測（直接查 PRTG）");
        Assert.True(s2 >= 0 && s3 > s2);
        Assert.Contains("另以直接查 PRTG 取裝置與感測器全表各一次", console.Text);
    }

    [Fact]
    public async Task 試算_範圍台數與範圍外感測器數_並提示下次同步會清除()
    {
        var store = SeedTwoMappedOneUnmatched();
        var stub = new StubPrtg();

        var console = await RunAsync(store, stub);

        Assert.Contains("══════════ 站台對照 ══════════", console.Lines);
        Assert.Contains("取數範圍：2 台裝置（對應 2、衝突 0、人工 0、守門 0）", console.Text);
        Assert.Contains("鏡像現況：裝置 3 台、感測器 7 顆，其中範圍外 3 顆", console.Text);
        Assert.Contains("感測器 7 顆，其中範圍外 3 顆", console.Text);
        Assert.Contains("下次結構同步成功後會清除", console.Text);
    }

    [Fact]
    public async Task 鏡像空_提示尚未同步且S2略過_零個PRTG請求()
    {
        var store = CreateStore();
        var stub = new StubPrtg();

        var console = await RunAsync(store, stub);

        Assert.Contains("鏡像尚未同步", console.Text);
        Assert.Contains("[S2] 範圍內裝置逐台查詢實測", console.Lines);
        Assert.Contains("略過（鏡像尚未同步）", console.Text);
        Assert.Empty(stub.RequestedUrls);
    }

    [Fact]
    public async Task 範圍空_有裝置但無對應_提示範圍是空的_零個PRTG請求()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1, Name = "D1", Ip = "10.0.0.1" },
            new PrtgDeviceRow { Objid = 2, Name = "D2", Ip = "10.0.0.2" }
        }, now);
        store.UpsertSensors(new[] { Sensor(11, 1), Sensor(21, 2) }, now);
        var stub = new StubPrtg();

        var console = await RunAsync(store, stub);

        Assert.Contains("取數範圍：0 台裝置", console.Text);
        Assert.Contains("取數範圍是空的", console.Text);
        Assert.Contains("略過（取數範圍是空的）", console.Text);
        Assert.Empty(stub.RequestedUrls);
    }

    [Fact]
    public async Task 範圍只有人工對應_Mapped為0_提示下次同步不會清除感測器鏡像()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1, Name = "D1", Ip = "10.0.0.1" },
            new PrtgDeviceRow { Objid = 2, Name = "D2", Ip = "10.0.0.2" }
        }, now);
        store.UpsertSensors(new[] { Sensor(11, 1), Sensor(21, 2) }, now);
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1,
            HostId = 1,
            Note = "人工",
            CreatedBy = "admin",
            CreatedAt = now
        });
        var stub = new StubPrtg();

        var console = await RunAsync(store, stub);

        Assert.Contains("取數範圍：1 台裝置（對應 0、衝突 0、人工 1、守門 0）", console.Text);
        Assert.Contains("下次同步不會清除感測器鏡像", console.Text);
        Assert.DoesNotContain("取數範圍是空的", console.Text);
    }

    [Fact]
    public async Task 實測_messages含下層感測器_結論打勾_且只查範圍內裝置()
    {
        var store = SeedTwoMappedOneUnmatched();
        var stub = new StubPrtg();
        stub.SensorsByDevice[1] = new long[] { 11, 12 };
        stub.SensorsByDevice[2] = new long[] { 21, 22 };
        stub.MessagesByDevice[1] = new long[] { 11, 12, 11 };
        stub.MessagesByDevice[2] = new long[] { 21 };

        var console = await RunAsync(store, stub);

        Assert.Contains("結論 ✓ 範圍內裝置逐台查詢狀態變更可用", console.Text);
        Assert.Contains("裝置 objid=1（D1）逐裝置取感測器", console.Text);
        Assert.Contains("回傳 2 顆（鏡像 2 顆）", console.Text);
        Assert.DoesNotContain("與鏡像不同", console.Text);
        var ids = stub.RequestedDeviceIds();
        Assert.Equal(new HashSet<long> { 1, 2 }, ids);
        Assert.DoesNotContain(3L, ids);
        Assert.True(ids.Count <= 3);
        Assert.True(stub.RequestedUrls.Count <= 6);
        Assert.Contains("估算：範圍 2 台、併發 2——", console.Text);
    }

    [Fact]
    public async Task 實測_messages只回裝置自身_結論打叉()
    {
        var store = SeedTwoMappedOneUnmatched();
        var stub = new StubPrtg();
        stub.SensorsByDevice[1] = new long[] { 11, 12 };
        stub.SensorsByDevice[2] = new long[] { 21, 22, 23 };
        stub.MessagesByDevice[1] = new long[] { 1, 1 };
        stub.MessagesByDevice[2] = new long[] { 2 };

        var console = await RunAsync(store, stub);

        Assert.Contains("結論 ✗ 逐裝置查詢只回裝置自身訊息", console.Text);
        Assert.Contains("✗ 只有裝置自身——狀態變更取數需改為逐感測器", console.Text);
        // 裝置 2 PRTG 回 3 顆、鏡像 2 顆
        Assert.Contains("回傳 3 顆（鏡像 2 顆）——與鏡像不同，下次同步後更新", console.Text);
    }

    [Fact]
    public async Task 挑樣本_有Down感測器的裝置優先_全Up且objid最大者不查()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 1, Name = "D1", Ip = "10.0.0.1" },
            new PrtgDeviceRow { Objid = 2, Name = "D2", Ip = "10.0.0.2" },
            new PrtgDeviceRow { Objid = 3, Name = "D3", Ip = "10.0.0.3" },
            new PrtgDeviceRow { Objid = 4, Name = "D4", Ip = "10.0.0.4" }
        }, now);
        store.UpsertSensors(new[]
        {
            Sensor(11, 1), Sensor(21, 2), Sensor(31, 3),
            Sensor(41, 4), Sensor(42, 4, "Down")
        }, now);
        store.ReplaceHostMapForDate(DateTime.Today, new List<PrtgHostMapRow>
        {
            MapRow(1, "10.0.0.1", 1, PrtgMapStatus.Ok),
            MapRow(2, "10.0.0.2", 2, PrtgMapStatus.Ok),
            MapRow(3, "10.0.0.3", 3, PrtgMapStatus.Ok),
            MapRow(4, "10.0.0.4", 4, PrtgMapStatus.Ok)
        });
        var stub = new StubPrtg();

        await RunAsync(store, stub);

        var ids = stub.RequestedDeviceIds();
        Assert.Contains(4L, ids);
        Assert.DoesNotContain(3L, ids);
        Assert.Equal(new HashSet<long> { 1, 2, 4 }, ids);
    }

    [Fact]
    public async Task 單台擲HttpRequestException_該台無法量測_其他台照常_不往外擲()
    {
        var store = SeedTwoMappedOneUnmatched();
        var stub = new StubPrtg();
        stub.ThrowForDevice.Add(1);
        stub.SensorsByDevice[2] = new long[] { 21, 22 };
        stub.MessagesByDevice[2] = new long[] { 22 };

        var console = await RunAsync(store, stub);

        Assert.Contains("裝置 objid=1 無法量測（", console.Text);
        Assert.Contains("裝置 objid=2（D2）逐裝置取感測器", console.Text);
        Assert.Contains("裝置 objid=2 逐裝置取狀態變更", console.Text);
        Assert.Contains("結論 ✓", console.Text);
    }

    [Theory]
    [InlineData(400.0, 20, 2, 4)]
    [InlineData(1.0, 1, 8, 1)]
    [InlineData(1500.0, 3, 1, 5)]
    public void 估算純函式_平均毫秒乘台數除併發_無條件進位(double avgMs, int devices, int concurrency, int expected)
    {
        Assert.Equal(expected, PrtgProbeSiteCheck.EstimateStageSeconds(avgMs, devices, concurrency));
    }

    // ── PrtgProbeService 接線：探測成功才接站台對照 ────────────────────────

    private sealed class ServiceHarness : IDisposable
    {
        private readonly string _dir;
        public StorageBackend Backend { get; }
        public SystemSettingsStore Settings { get; }
        public PrtgProbeService Service { get; }

        public ServiceHarness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "lf-probe-site-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
                _dir);
            Settings = new SystemSettingsStore(Backend.Blob("system_settings"));
            Service = new PrtgProbeService(Settings, new PrtgProbeRunState(), new PrtgBackfillRunState(),
                Backend, new FakeHostStore(), new FakeSentinelStore());
        }

        public void PointTo(int port) => Settings.Update(s =>
        {
            s.PrtgUrl = $"http://127.0.0.1:{port}";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
        });

        public async Task<PrtgProbeSnapshotOutput> RunToEndAsync()
        {
            Assert.True(Service.TryStart(out var error, out _), error);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Service.GetStatus().IsRunning)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("探測在 60 秒內沒有結束");
                await Task.Delay(50);
            }
            var status = Service.GetStatus();
            return new PrtgProbeSnapshotOutput(status.Success, string.Join("\n", status.Output));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed record PrtgProbeSnapshotOutput(bool? Success, string Text);

    private static int GetFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task 探測服務_連線失敗時不做站台對照()
    {
        using var h = new ServiceHarness();
        h.PointTo(GetFreePort()); // 沒有人在聽，步驟 1 必失敗

        var result = await h.RunToEndAsync();

        Assert.False(result.Success);
        Assert.DoesNotContain("站台對照", result.Text);
    }

    [Fact]
    public async Task 探測服務_探測成功後接站台對照且不改成敗()
    {
        using var h = new ServiceHarness();
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        // 每個請求都回空物件：探測步驟 1～7 只要不擲例外就算成功，站台對照看到空鏡像會略過實測
        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { break; }
                var bytes = Encoding.UTF8.GetBytes("{}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        h.PointTo(port);
        var result = await h.RunToEndAsync();
        listener.Stop();
        await server;

        Assert.True(result.Success);
        Assert.Contains("PRTG 環境探測完成", result.Text);
        Assert.Contains("══════════ 站台對照 ══════════", result.Text);
        Assert.Contains("鏡像尚未同步", result.Text);
    }
}
