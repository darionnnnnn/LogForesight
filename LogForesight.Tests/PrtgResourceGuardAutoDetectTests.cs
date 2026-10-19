using System.Net;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgResourceGuardAutoDetectTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;

    public PrtgResourceGuardAutoDetectTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-guard-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
    }

    public void Dispose()
    {
        PrtgResourceGuardAutoDetect.DetectTimeout = TimeSpan.FromSeconds(15);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private sealed class StubPrtgHandler : HttpMessageHandler
    {
        public int CallCount => RequestedUrls.Count;
        public List<string> RequestedUrls { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (RequestedUrls) RequestedUrls.Add(request.RequestUri!.ToString());
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }
            if (Responder != null)
            {
                return Responder(request);
            }
            var url = request.RequestUri!.ToString();
            if (url.Contains("content=devices"))
            {
                return Json("{\"treesize\":1,\"devices\":[{\"objid\":100,\"device\":\"prtg.example.com\",\"host\":\"prtg.example.com\"}]}");
            }
            if (url.Contains("content=sensors"))
            {
                return Json("{\"treesize\":2,\"sensors\":[" +
                    "{\"objid\":201,\"parentid\":100,\"sensor\":\"CPU Load\",\"type\":\"SNMP CPU Load\",\"status\":\"Up\"}," +
                    "{\"objid\":202,\"parentid\":100,\"sensor\":\"Memory\",\"type\":\"SNMP Memory\",\"status\":\"Up\"}" +
                    "]}");
            }
            return Json("{}");
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static PrtgClient CreateClient(StubPrtgHandler handler) =>
        new("https://prtg.example.com", "token123", 30, true, handler, PrtgAuthModes.Token, "", "", "");

    [Fact]
    public async Task 清單為空且PRTG位址命中_清單被填_blob寫入_Describe回True與日期()
    {
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        var settings = settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgResourceGuardEnabled = true;
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgResourceGuardSensorObjids = new List<string>();
        });

        var console = new TestConsole();
        var handler = new StubPrtgHandler();

        var detected = await PrtgResourceGuardAutoDetect.EnsureAsync(
            _backend,
            settings,
            Array.Empty<Sentinel>(),
            console,
            () => CreateClient(handler),
            CancellationToken.None);

        // 回傳本趟偵測結果，讓已建好的守門實例當晚直接換上
        Assert.NotNull(detected);
        Assert.Equal(new long[] { 201, 202 }, detected!.SensorObjids.OrderBy(id => id));

        var reloadedSettings = settingsStore.Get();
        Assert.Equal(new[] { "201", "202" }, reloadedSettings.PrtgResourceGuardSensorObjids);

        var blobStore = new PrtgResourceGuardAutoDetectStore(_backend.Blob(PrtgResourceGuardAutoDetect.BlobKey));
        var doc = blobStore.Get();
        Assert.NotNull(doc.DetectedAt);
        Assert.Equal(new[] { "201", "202" }, doc.Objids);

        var (autoDetected, detectedAt) = PrtgResourceGuardAutoDetect.Describe(_backend, reloadedSettings);
        Assert.True(autoDetected);
        Assert.NotNull(detectedAt);
        Assert.Equal(doc.DetectedAt, detectedAt);

        Assert.Contains(console.Lines, l => l.Contains("[PRTG資源守門] 自動偵測並填入 2 顆守門目標"));
    }

    [Fact]
    public async Task 清單非空且與blob不同_視為人工清單_不呼叫PRTG_清單不變()
    {
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        var settings = settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgResourceGuardEnabled = true;
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgResourceGuardSensorObjids = new List<string> { "9001", "9002" };
        });

        var console = new TestConsole();
        var handler = new StubPrtgHandler();

        await PrtgResourceGuardAutoDetect.EnsureAsync(
            _backend,
            settings,
            Array.Empty<Sentinel>(),
            console,
            () => CreateClient(handler),
            CancellationToken.None);

        Assert.Equal(0, handler.CallCount);

        var reloadedSettings = settingsStore.Get();
        Assert.Equal(new[] { "9001", "9002" }, reloadedSettings.PrtgResourceGuardSensorObjids);

        var (autoDetected, detectedAt) = PrtgResourceGuardAutoDetect.Describe(_backend, reloadedSettings);
        Assert.False(autoDetected);
        Assert.Null(detectedAt);
    }

    [Fact]
    public async Task 守門未啟用_不呼叫PRTG()
    {
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        var settings = settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgResourceGuardEnabled = false;
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgResourceGuardSensorObjids = new List<string>();
        });

        var console = new TestConsole();
        var handler = new StubPrtgHandler();

        await PrtgResourceGuardAutoDetect.EnsureAsync(
            _backend,
            settings,
            Array.Empty<Sentinel>(),
            console,
            () => CreateClient(handler),
            CancellationToken.None);

        Assert.Equal(0, handler.CallCount);
        var reloadedSettings = settingsStore.Get();
        Assert.Empty(reloadedSettings.PrtgResourceGuardSensorObjids);
        Assert.Empty(console.Lines);
    }

    [Fact]
    public async Task 偵測逾時_清單仍空_console有警告()
    {
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        var settings = settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgResourceGuardEnabled = true;
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgResourceGuardSensorObjids = new List<string>();
        });

        var console = new TestConsole();
        var handler = new StubPrtgHandler
        {
            Delay = TimeSpan.FromSeconds(2)
        };

        PrtgResourceGuardAutoDetect.DetectTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            await PrtgResourceGuardAutoDetect.EnsureAsync(
                _backend,
                settings,
                Array.Empty<Sentinel>(),
                console,
                () => CreateClient(handler),
                CancellationToken.None);
        }
        finally
        {
            PrtgResourceGuardAutoDetect.DetectTimeout = TimeSpan.FromSeconds(15);
        }

        var reloadedSettings = settingsStore.Get();
        Assert.Empty(reloadedSettings.PrtgResourceGuardSensorObjids);
        Assert.Contains(console.Lines, l => l.Contains("[PRTG資源守門] 自動偵測未完成") && l.Contains("取數照常進行"));
    }

    [Fact]
    public async Task 重跑三次_清單等於blob且objid都在鏡像_只有第一次呼叫PRTG_清單內容相同()
    {
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        var settings = settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgResourceGuardEnabled = true;
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgResourceGuardSensorObjids = new List<string>();
        });

        var console = new TestConsole();
        var handler = new StubPrtgHandler();

        // 第一次跑：PRTG 命中，寫入設定與 blob
        await PrtgResourceGuardAutoDetect.EnsureAsync(
            _backend,
            settings,
            Array.Empty<Sentinel>(),
            console,
            () => CreateClient(handler),
            CancellationToken.None);

        var callsAfterFirst = handler.CallCount;
        Assert.True(callsAfterFirst > 0);

        var firstSettings = settingsStore.Get();
        Assert.Equal(new[] { "201", "202" }, firstSettings.PrtgResourceGuardSensorObjids);

        // 鏡像內已有 201 與 202
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 201, DeviceObjid = 100, Name = "CPU", SensorType = "SNMP CPU Load" },
            new PrtgSensorRow { Objid = 202, DeviceObjid = 100, Name = "Memory", SensorType = "SNMP Memory" }
        }, DateTime.Now);

        // 第二次跑：objid 都在鏡像中，不應再次呼叫 PRTG
        await PrtgResourceGuardAutoDetect.EnsureAsync(
            _backend,
            firstSettings,
            Array.Empty<Sentinel>(),
            console,
            () => CreateClient(handler),
            CancellationToken.None);

        Assert.Equal(callsAfterFirst, handler.CallCount);

        // 第三次跑：仍然不呼叫 PRTG
        await PrtgResourceGuardAutoDetect.EnsureAsync(
            _backend,
            firstSettings,
            Array.Empty<Sentinel>(),
            console,
            () => CreateClient(handler),
            CancellationToken.None);

        Assert.Equal(callsAfterFirst, handler.CallCount);

        var thirdSettings = settingsStore.Get();
        Assert.Equal(firstSettings.PrtgResourceGuardSensorObjids, thirdSettings.PrtgResourceGuardSensorObjids);

        var (autoDetected, detectedAt) = PrtgResourceGuardAutoDetect.Describe(_backend, thirdSettings);
        Assert.True(autoDetected);
        Assert.NotNull(detectedAt);
    }

    [Fact]
    public async Task 清單等於blob但objid不在鏡像_觸發重新偵測()
    {
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        var settings = settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgResourceGuardEnabled = true;
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgResourceGuardSensorObjids = new List<string> { "999" };
        });

        // blob 記錄 999 也是自動偵測填入的
        var blobStore = new PrtgResourceGuardAutoDetectStore(_backend.Blob(PrtgResourceGuardAutoDetect.BlobKey));
        blobStore.Update(d =>
        {
            d.DetectedAt = DateTime.Now.AddDays(-1);
            d.Objids = new[] { "999" };
        });

        // 鏡像內沒有 999（已被 PRTG 刪除）
        var console = new TestConsole();
        var handler = new StubPrtgHandler();

        await PrtgResourceGuardAutoDetect.EnsureAsync(
            _backend,
            settings,
            Array.Empty<Sentinel>(),
            console,
            () => CreateClient(handler),
            CancellationToken.None);

        Assert.True(handler.CallCount > 0);
        var reloaded = settingsStore.Get();
        Assert.Equal(new[] { "201", "202" }, reloaded.PrtgResourceGuardSensorObjids);
    }

    [Fact]
    public async Task 偵測結果為空_不改清單_印一行提示()
    {
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        var settings = settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgResourceGuardEnabled = true;
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgResourceGuardSensorObjids = new List<string>();
        });

        var console = new TestConsole();
        var handler = new StubPrtgHandler
        {
            Responder = req =>
            {
                var url = req.RequestUri!.ToString();
                if (url.Contains("content=devices"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"treesize\":0,\"devices\":[]}", Encoding.UTF8, "application/json") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"treesize\":0,\"sensors\":[]}", Encoding.UTF8, "application/json") };
            }
        };

        await PrtgResourceGuardAutoDetect.EnsureAsync(
            _backend,
            settings,
            Array.Empty<Sentinel>(),
            console,
            () => CreateClient(handler),
            CancellationToken.None);

        var reloaded = settingsStore.Get();
        Assert.Empty(reloaded.PrtgResourceGuardSensorObjids);
        Assert.Contains(console.Lines, l => l.Contains("自動偵測沒有找到守門目標"));
    }
}
