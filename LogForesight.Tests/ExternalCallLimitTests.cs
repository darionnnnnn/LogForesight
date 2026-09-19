using System.Net;
using System.Reflection;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 外部呼叫上限（回饋第 50 輪批次 A-5）：SQLite WAL、AI 逾時不重試、PRTG 回應 256 MB 上限、
/// 快照服務重用 PRTG client。
/// </summary>
public class ExternalCallLimitTests : IDisposable
{
    private readonly string _dir;

    public ExternalCallLimitTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-extlimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── SQLite WAL ──────────────────────────────────────────────

    private StorageBackend NewBackend(bool wal) =>
        new(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_dir, "wal.db")}",
            SqliteWal = wal
        }, _dir);

    /// <summary>經由後端自己的 DbContext 工廠開連線（攔截器在這條路上才會套用）後查 journal_mode</summary>
    private static string QueryJournalMode(StorageBackend backend)
    {
        var factory = (Func<LfDbContext>)typeof(StorageBackend)
            .GetField("_dbFactory", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(backend)!;
        using var ctx = factory();
        var conn = ctx.Database.GetDbConnection();
        ctx.Database.OpenConnection();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            return Convert.ToString(cmd.ExecuteScalar())!.ToLowerInvariant();
        }
        finally
        {
            ctx.Database.CloseConnection();
        }
    }

    [Fact]
    public void Sqlite預設走WAL()
    {
        Assert.True(new StorageSettings().SqliteWal);
        Assert.Equal("wal", QueryJournalMode(NewBackend(wal: true)));
    }

    [Fact]
    public void SqliteWal關閉時切回DELETE()
    {
        // 先以 WAL 開過同一個檔（journal_mode=WAL 會寫進檔頭持久化），再以關閉設定開——
        // 回 delete 才代表攔截器真的切回去，而不是檔案本來就是預設值
        Assert.Equal("wal", QueryJournalMode(NewBackend(wal: true)));
        Assert.Equal("delete", QueryJournalMode(NewBackend(wal: false)));
    }

    // ── AI 逾時不重試 ────────────────────────────────────────────

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _onSend;
        private int _calls;
        public int Calls => _calls;

        public CountingHandler(Func<CancellationToken, Task<HttpResponseMessage>> onSend) => _onSend = onSend;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _onSend(cancellationToken);
        }
    }

    private static AiSettings AiSettingsForRetry() => new()
    {
        BaseUrl = "http://localhost:1", RetryCount = 2, RetryDelaySeconds = 0, TimeoutSeconds = 1
    };

    [Fact]
    public async Task AI_HttpClient逾時不重試()
    {
        var handler = new CountingHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = new AIService(AiSettingsForRetry(), handler);

        var response = await service.ChatAsync("hi");

        Assert.False(response.Success);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AI_連線失敗仍依重試次數重試()
    {
        var handler = new CountingHandler(_ => throw new HttpRequestException("連不上"));
        var service = new AIService(AiSettingsForRetry(), handler);

        var response = await service.ChatAsync("hi");

        Assert.False(response.Success);
        Assert.Equal(3, handler.Calls);
    }

    // ── PRTG 回應上限 ────────────────────────────────────────────

    /// <summary>回報 300 MB 長度、內容以串流即時產生的回應本體——不真的配置 300 MB</summary>
    private sealed class HugeStreamingContent : HttpContent
    {
        public const long Size = 300L * 1024 * 1024;

        public HugeStreamingContent() => Headers.ContentLength = Size;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)' ');
            for (long written = 0; written < Size; written += chunk.Length)
            {
                await stream.WriteAsync(chunk);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Size;
            return true;
        }
    }

    [Fact]
    public async Task PRTG回應超過256MB擲PrtgClientException()
    {
        var handler = new CountingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new HugeStreamingContent()
        }));
        using var client = new PrtgClient("https://prtg.example.com", "token123", 30, false, handler);

        var ex = await Assert.ThrowsAsync<PrtgClientException>(
            () => client.GetJsonAsync("api/table.json?content=sensors"));

        Assert.Contains("256 MB", ex.Message);
        Assert.DoesNotContain("連線 PRTG 伺服器失敗", ex.Message);
    }

    // ── 快照服務重用 client ──────────────────────────────────────

    private sealed class CountingSnapshotService : PrtgSnapshotHostedService
    {
        private readonly HttpMessageHandler _handler;
        public int CreateCount { get; private set; }

        public CountingSnapshotService(
            ISystemSettingsStore settingsStore, StorageBackend backend, SchedulerRunState schedulerRunState,
            PrtgStructureSyncService structureSync, PrtgBackfillService backfill, IHostStore hosts,
            ISentinelStore sentinels, PrtgProbeRunState probeState, IHostApplicationLifetime lifetime,
            HttpMessageHandler handler)
            : base(settingsStore, backend, schedulerRunState, structureSync, backfill, hosts, sentinels, probeState, lifetime)
        {
            _handler = handler;
        }

        internal override PrtgClient CreateClient(SystemSettings settings)
        {
            CreateCount++;
            return new PrtgClient(settings.PrtgUrl, "token123", 30, false, _handler);
        }
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private (CountingSnapshotService Service, SystemSettingsStore Settings, CountingHandler Handler) NewSnapshotService()
    {
        var backend = NewBackend(wal: true);
        var settingsStore = new SystemSettingsStore(backend.Blob("system_settings"));
        var schedulerRunState = new SchedulerRunState();
        var syncState = new PrtgStructureSyncRunState();
        var lifetime = new FakeHostApplicationLifetime();
        var hostStore = new HostStore(backend.Blob("hosts"));
        var statusStore = new PrtgStructureSyncStatusStore(backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        var backfillState = new PrtgBackfillRunState();
        var structureSync = new PrtgStructureSyncService(settingsStore, backend, syncState, schedulerRunState, hostStore,
            statusStore, backfillState, new FakeSentinelStore(), lifetime);
        var probeState = new PrtgProbeRunState();
        var backfill = new PrtgBackfillService(settingsStore, backend, backfillState, probeState, hostStore,
            schedulerRunState, syncState, new FakeSentinelStore());

        settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example.com";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token123");
            s.PrtgTimeoutSeconds = 30;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        // 一顆在取數範圍內的感測器，快照才會真的打 PRTG
        var store = backend.PrtgStore();
        var today = DateTime.Today;
        store.ReplaceHostMapForDate(today, new[]
        {
            new PrtgHostMapRow
            {
                DeviceObjid = 10, HostId = 1, HostName = "Server-01", Ip = "192.168.1.10",
                MapStatus = PrtgMapStatus.Ok, MapDate = today
            }
        });
        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 101, DeviceObjid = 10, Name = "Sensor-101", SensorType = "Ping", Status = "Up", Paused = false }
        }, DateTime.Now);

        var handler = new CountingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"treesize\":1,\"sensors\":[{\"objid\":101,\"lastvalue_raw\":10,\"interval\":\"60 s\"}]}",
                Encoding.UTF8, "application/json")
        }));

        var service = new CountingSnapshotService(settingsStore, backend, schedulerRunState, structureSync, backfill,
            hostStore, new FakeSentinelStore(), probeState, lifetime, handler);
        return (service, settingsStore, handler);
    }

    [Fact]
    public async Task 快照_設定不變連續兩輪只建一次client()
    {
        var (service, _, handler) = NewSnapshotService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        await service.TickAsync();
        var afterFirst = handler.Calls;
        clock = clock.AddHours(1);
        await service.TickAsync();

        // 兩輪都真的打了 PRTG，才代表「只建一次」不是因為第二輪根本沒走到取數
        Assert.True(afterFirst > 0);
        Assert.True(handler.Calls > afterFirst);
        Assert.Equal(1, service.CreateCount);
        service.Dispose();
    }

    [Fact]
    public async Task 快照_中間改PRTG網址會重建client()
    {
        var (service, settings, handler) = NewSnapshotService();
        var clock = DateTime.Today.AddHours(10);
        service.Now = () => clock;

        await service.TickAsync();
        var afterFirst = handler.Calls;
        settings.Update(s => s.PrtgUrl = "https://prtg2.example.com");
        clock = clock.AddHours(1);
        await service.TickAsync();

        Assert.True(afterFirst > 0);
        Assert.True(handler.Calls > afterFirst);
        Assert.Equal(2, service.CreateCount);
        service.Dispose();
    }
}
