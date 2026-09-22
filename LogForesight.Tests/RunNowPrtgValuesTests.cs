using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 立即執行「一併補齊 PRTG 逐小時數值」：請求綁定與生效條件、Orchestrator 在本趟結束前接續回填、
/// 回填服務的接續入口不被自己這趟擋下、鏡像為空時回填先同步結構。
/// </summary>
public class RunNowPrtgValuesTests
{
    // ── 請求：真實 JSON 綁定 → 執行請求 ──────────────────────────────────

    /// <summary>MVC 的預設 JSON 選項（與 TriggerRunRequestBindingTests 同一手法）</summary>
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    private static RunRequest Bind(string json, SystemSettings settings)
    {
        var request = JsonSerializer.Deserialize<TriggerRunRequest>(json, WebDefaults);
        Assert.NotNull(request);
        return ScheduleController.ToRunRequest(request!, RunScope.Full, null, "tester", settings);
    }

    private static SystemSettings PrtgSettings(string strategy) =>
        new() { PrtgEnabled = true, PrtgFetchStrategy = strategy };

    private const string CheckedJson = """{"scope":"all","backfillDays":3,"includePrtgValues":true}""";

    [Fact]
    public void 保守策略勾選且回望3天_接續回填3天()
    {
        Assert.Equal(3, Bind(CheckedJson, PrtgSettings(PrtgFetchStrategy.Conservative)).PrtgBackfillDays);
    }

    [Fact]
    public void 激進策略_不接續()
    {
        Assert.Equal(0, Bind(CheckedJson, PrtgSettings(PrtgFetchStrategy.Aggressive)).PrtgBackfillDays);
    }

    [Fact]
    public void 未勾選_不接續()
    {
        Assert.Equal(0, Bind("""{"scope":"all","backfillDays":3}""", PrtgSettings(PrtgFetchStrategy.Conservative)).PrtgBackfillDays);
        Assert.Equal(0, Bind("""{"scope":"all","backfillDays":3,"includePrtgValues":false}""", PrtgSettings(PrtgFetchStrategy.Conservative)).PrtgBackfillDays);
    }

    [Fact]
    public void 回望1天_不接續()
    {
        Assert.Equal(0, Bind("""{"scope":"all","backfillDays":1,"includePrtgValues":true}""", PrtgSettings(PrtgFetchStrategy.Conservative)).PrtgBackfillDays);
    }

    [Fact]
    public void PRTG未啟用_不接續()
    {
        var settings = new SystemSettings { PrtgEnabled = false, PrtgFetchStrategy = PrtgFetchStrategy.Conservative };
        Assert.Equal(0, Bind(CheckedJson, settings).PrtgBackfillDays);
    }

    // ── Orchestrator：本趟結束前接續 ──────────────────────────────────────

    /// <summary>進度回報與接續替身寫進同一份事件序列，用來比對先後。</summary>
    private sealed class EventLog
    {
        private readonly List<string> _events = new();
        public void Add(string e) { lock (_events) _events.Add(e); }
        public List<string> Snapshot() { lock (_events) return _events.ToList(); }
    }

    private sealed class RecordingProgress(EventLog log) : IRunProgress
    {
        public void Report(string phase, int done, int total) => log.Add("progress:" + phase);
    }

    private sealed class FakeTail(EventLog log, bool result) : IPrtgBackfillTail
    {
        public List<(int Days, IReadOnlyCollection<long>? HostIds)> Calls { get; } = new();

        public Task<bool> RunTailAsync(int days, IReadOnlyCollection<long>? hostIds, IRunConsole console, CancellationToken ct)
        {
            Calls.Add((days, hostIds?.ToList()));
            log.Add("tail");
            return Task.FromResult(result);
        }
    }

    private sealed class EmptyCandidateSource : IDispatchCandidateSource
    {
        public DispatchCandidatePool Build() => new()
        {
            ByUserId = new Dictionary<long, DispatchCandidate>(), PoolMemberCount = 0, ActivePoolMemberCount = 0
        };
    }

    /// <summary>執行輸出也寫進事件序列：用夜間派工摘要那一行當「分析與派工都已完成」的時間點。</summary>
    private sealed class LoggingConsole(EventLog log) : IRunConsole
    {
        public void WriteLine(string message = "") => log.Add("console:" + message);
    }

    /// <summary>
    /// 跑一趟真的 Orchestrator：PRTG 未啟用（PRTG 路徑仍會對本趟日期補發佈空結果並送就緒訊號）、
    /// AI 未設定、不分析本機，NetIQ 沒有主機——整趟快速結束，只驗接續回填的接線。
    /// </summary>
    private static async Task<OrchestratorResult> RunOrchestratorAsync(RunRequest request, IPrtgBackfillTail tail, EventLog log)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-test-runnow-prtg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new AppSettings
            {
                Ai = new AiSettings { BaseUrl = "" },
                Storage = new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "test.db")}" }
            };
            var orchestrator = new AnalysisOrchestrator(new EmptyCandidateSource());
            return await orchestrator.RunAsync(
                request, settings, dir, new RetentionOptions(), new LoggingConsole(log), CancellationToken.None,
                new RecordingProgress(log), structureSyncGate: null, prtgBackfillTail: tail);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task 接續天數3_替身被呼叫一次且範圍為全站()
    {
        var log = new EventLog();
        var tail = new FakeTail(log, result: true);

        var result = await RunOrchestratorAsync(
            new RunRequest { Scope = RunScope.Full, IncludeLocal = false, BackfillOverride = 3, PrtgBackfillDays = 3 }, tail, log);

        Assert.True(result.Success, result.FailureMessage);
        var call = Assert.Single(tail.Calls);
        Assert.Equal(3, call.Days);
        Assert.Null(call.HostIds);
    }

    [Fact]
    public async Task 指定主機_接續範圍等於請求的主機()
    {
        var log = new EventLog();
        var tail = new FakeTail(log, result: true);

        await RunOrchestratorAsync(
            new RunRequest { Scope = RunScope.NetiqHosts, HostIds = new[] { 901L, 902L }, PrtgBackfillDays = 3 }, tail, log);

        var call = Assert.Single(tail.Calls);
        Assert.Equal(new[] { 901L, 902L }, call.HostIds);
    }

    [Fact]
    public async Task 接續天數0_不呼叫()
    {
        var log = new EventLog();
        var tail = new FakeTail(log, result: true);

        var result = await RunOrchestratorAsync(
            new RunRequest { Scope = RunScope.Full, IncludeLocal = false, PrtgBackfillDays = 0 }, tail, log);

        Assert.True(result.Success, result.FailureMessage);
        Assert.Empty(tail.Calls);
    }

    [Fact]
    public async Task 接續回false_整趟仍成功()
    {
        var log = new EventLog();
        var tail = new FakeTail(log, result: false);

        var result = await RunOrchestratorAsync(
            new RunRequest { Scope = RunScope.Full, IncludeLocal = false, BackfillOverride = 3, PrtgBackfillDays = 3 }, tail, log);

        Assert.Single(tail.Calls);
        Assert.True(result.Success, result.FailureMessage);
    }

    [Fact]
    public async Task 接續發生在PRTG就緒訊號之後()
    {
        var log = new EventLog();
        var tail = new FakeTail(log, result: true);

        await RunOrchestratorAsync(
            new RunRequest { Scope = RunScope.Full, IncludeLocal = false, BackfillOverride = 3, PrtgBackfillDays = 3 }, tail, log);

        var events = log.Snapshot();
        var readyIndex = events.IndexOf("progress:" + RunPhases.PrtgFindingsReady);
        var tailIndex = events.IndexOf("tail");
        Assert.True(readyIndex >= 0, "PRTG finding 就緒訊號沒有送出：" + string.Join(",", events));
        Assert.True(tailIndex > readyIndex, "接續回填早於就緒訊號：" + string.Join(",", events));
        // 分析、派工都完成之後才接續（PRTG 未啟用時就緒訊號是同步送出的，單看它分不出接續是否放在匯合之後）
        var dispatchIndex = events.FindIndex(e => e.StartsWith("console:") && e.Contains("自動派工："));
        Assert.True(dispatchIndex >= 0 && tailIndex > dispatchIndex, "接續回填早於派工收尾：" + string.Join(",", events));
    }

    // ── 回填服務：接續入口與空鏡像先同步 ──────────────────────────────────

    /// <summary>結構同步替身：只覆寫回填用的內部同步入口，其餘沿用真的服務。</summary>
    private sealed class FakeStructureSync : PrtgStructureSyncService
    {
        private readonly Func<string?> _behavior;
        public List<string> Log { get; }

        public FakeStructureSync(Harness h, List<string> log, Func<string?> behavior)
            : base(h.Settings, h.Backend, h.SyncState, h.Scheduler, new HostStore(h.Backend.Blob("hosts")),
                new PrtgStructureSyncStatusStore(h.Backend.Blob(PrtgStructureSyncStatusStore.BlobKey)),
                h.BackfillState, new FakeSentinelStore(), new DataVersionStamp())
        {
            _behavior = behavior;
            Log = log;
        }

        internal override Task<string?> SyncForBackfillAsync(CancellationToken ct)
        {
            Log.Add("sync");
            return Task.FromResult(_behavior());
        }
    }

    private sealed class Harness : IDisposable
    {
        public string Dir { get; }
        public StorageBackend Backend { get; }
        public SystemSettingsStore Settings { get; }
        public SchedulerRunState Scheduler { get; } = new();
        public PrtgStructureSyncRunState SyncState { get; } = new();
        public PrtgBackfillRunState BackfillState { get; } = new();
        public List<string> SyncLog { get; } = new();
        public PrtgBackfillService Service { get; private set; } = null!;

        public Harness(bool withMirror, Func<Harness, string?>? syncBehavior = null)
        {
            Dir = Path.Combine(Path.GetTempPath(), "lf-test-runnow-backfill-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(Dir, "test.db")}" },
                Dir);
            Settings = new SystemSettingsStore(Backend.Blob("system_settings"));
            Settings.Update(s =>
            {
                s.PrtgEnabled = true;
                // 本機不存在的埠：連線立即被拒，回填很快結束，不打外部網路
                s.PrtgUrl = "http://127.0.0.1:1";
                s.PrtgAuthMode = PrtgAuthModes.Token;
                s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token123");
                s.PrtgTimeoutSeconds = 5;
            });
            if (withMirror) SeedMirror();

            var sync = new FakeStructureSync(this, SyncLog, () => syncBehavior?.Invoke(this));
            Service = new PrtgBackfillService(
                Settings, Backend, BackfillState, new PrtgProbeRunState(),
                new HostStore(Backend.Blob("hosts")), Scheduler, SyncState, new FakeSentinelStore(), sync);
        }

        public void SeedMirror()
        {
            Backend.PrtgStore().UpsertSensors(new List<PrtgSensorRow>
            {
                new() { Objid = 2001, DeviceObjid = 1001, Name = "CPU", SensorType = "wmicpu", Paused = false }
            }, DateTime.Now);
            Backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today.AddDays(-1), new List<PrtgHostMapRow>
            {
                new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Ok }
            });
        }

        public async Task<PrtgBackfillStatusDto> WaitIdleAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Service.GetStatus().IsRunning)
            {
                Assert.True(DateTime.UtcNow < deadline, "回填 60 秒內沒有結束");
                await Task.Delay(50);
            }
            return Service.GetStatus();
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class CollectingConsole : IRunConsole
    {
        private readonly List<string> _lines = new();
        public void WriteLine(string message = "") { lock (_lines) _lines.Add(message); }
        public List<string> Lines { get { lock (_lines) return _lines.ToList(); } }
    }

    [Fact]
    public async Task RunTailAsync_取數執行中也不被擋()
    {
        using var h = new Harness(withMirror: true);
        Assert.True(h.Scheduler.TryBeginRun("manual", out _));
        var console = new CollectingConsole();

        await h.Service.RunTailAsync(3, null, console, CancellationToken.None);

        Assert.DoesNotContain(console.Lines, l => l.Contains("取數執行中"));
        Assert.DoesNotContain(console.Lines, l => l.Contains("接續補 PRTG 數值未執行"));
        // 確實跑進回填主體（輸出同時進本趟 console 與回填狀態卡）
        Assert.Contains(console.Lines, l => l.StartsWith("監看裝置："));
        Assert.Contains(h.Service.GetStatus().Output, l => l.StartsWith("監看裝置："));
        Assert.False(h.Service.GetStatus().IsRunning);
    }

    [Fact]
    public void TryStart_取數執行中仍被擋()
    {
        using var h = new Harness(withMirror: true);
        Assert.True(h.Scheduler.TryBeginRun("manual", out _));

        Assert.False(h.Service.TryStart(out var error, out var isConflict));
        Assert.True(isConflict);
        Assert.StartsWith("取數執行中", error);
    }

    [Fact]
    public async Task RunTailAsync_其他擋門照舊_回false並寫原因()
    {
        using var h = new Harness(withMirror: true);
        Assert.True(h.SyncState.TryBegin());
        var console = new CollectingConsole();

        Assert.False(await h.Service.RunTailAsync(3, null, console, CancellationToken.None));
        Assert.Contains(console.Lines, l => l.Contains("接續補 PRTG 數值未執行") && l.Contains("同步結構與對應"));
    }

    [Fact]
    public async Task 鏡像無感測器_先同步結構再開始回填()
    {
        // 同步替身呼叫當下斷言回填主體還沒開始，並補上鏡像（模擬同步成功寫入）
        var bodyStartedBeforeSync = false;
        using var h = new Harness(withMirror: false, syncBehavior: harness =>
        {
            bodyStartedBeforeSync = harness.Service.GetStatus().Output.Any(l => l.StartsWith("監看裝置："));
            harness.SeedMirror();
            return null;
        });

        Assert.True(h.Service.TryStart(out var error, out _), error);
        var status = await h.WaitIdleAsync();

        Assert.Equal(new[] { "sync" }, h.SyncLog);
        Assert.False(bodyStartedBeforeSync);
        var output = status.Output.ToList();
        var syncLine = output.FindIndex(l => l.Contains("先同步結構"));
        var bodyLine = output.FindIndex(l => l.StartsWith("監看裝置："));
        Assert.True(syncLine >= 0 && bodyLine > syncLine, string.Join("\n", status.Output));
    }

    [Fact]
    public async Task 鏡像無感測器且同步失敗_回填不開始並回報結構同步失敗()
    {
        using var h = new Harness(withMirror: false, syncBehavior: _ => "連線逾時");

        Assert.True(h.Service.TryStart(out var error, out _), error);
        var status = await h.WaitIdleAsync();

        Assert.Equal(new[] { "sync" }, h.SyncLog);
        Assert.Contains(status.Output, l => l.Contains("結構同步失敗：連線逾時"));
        Assert.DoesNotContain(status.Output, l => l.StartsWith("監看裝置："));
        Assert.False(status.Success);
    }

    [Fact]
    public async Task 同步後仍沒有對應_回報沒有任何一台對應()
    {
        using var h = new Harness(withMirror: false, syncBehavior: harness =>
        {
            harness.Backend.PrtgStore().UpsertSensors(new List<PrtgSensorRow>
            {
                new() { Objid = 2001, DeviceObjid = 1001, Name = "CPU", SensorType = "wmicpu", Paused = false }
            }, DateTime.Now);
            return null;
        });

        Assert.True(h.Service.TryStart(out var error, out _), error);
        var status = await h.WaitIdleAsync();

        Assert.Contains(status.Output, l => l.StartsWith("PRTG 裝置沒有任何一台對應到主機清單中的主機"));
        Assert.DoesNotContain(status.Output, l => l.StartsWith("監看裝置："));
    }

    [Fact]
    public void 回填服務原始碼不再指路到夜間排程或每日擷取()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");

        var source = File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "Services", "PrtgBackfillService.cs"));
        Assert.DoesNotContain("夜間排程", source);
        Assert.DoesNotContain("每日擷取", source);
    }
}
