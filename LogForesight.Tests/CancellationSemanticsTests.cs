using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 逾時 ≠ 取消：HttpClient 逾時擲 TaskCanceledException（屬 OperationCanceledException），
/// 但呼叫端的 ct 並未取消。只有「OCE 且 ct 已取消」才算使用者停止，逾時要走失敗隔離。
/// </summary>
public sealed class CancellationSemanticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-cancel-sem-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly FakeSuppressionStore _suppressions = new();
    private readonly FakeAiService _ai = new();
    private readonly List<string> _console = new();

    public CancellationSemanticsTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "p.db")}" }, _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        GC.SuppressFinalize(this);
    }

    private Sentinel AddSentinel(string name) =>
        _sentinels.Upsert(new Sentinel { Name = name, BaseUrl = "https://x", Username = "u", PasswordEnc = "p", Active = true });

    private WebHost AddWindowsHost(Sentinel sentinel, string ip, string hostName) =>
        _hosts.Upsert(new WebHost
        {
            HostName = hostName, IpAddress = ip, Os = WebHost.OsWindows,
            SentinelId = sentinel.SentinelId, NetiqServer = sentinel.Name, Source = "netiq", Active = true
        });

    private NetiqPipelineService MakePipeline(Func<Sentinel, ISentinelSearchClient> clientFactory)
    {
        var reportService = new RiskReportService(_ai, new FakeReportSink());
        var batchRunStore = new BatchRunStore(_backend.LogStore("cancel_runs"), _backend.LogStore("cancel_run_logs"));
        var runRecorder = new BatchRunRecorder(batchRunStore, "test-host", Array.Empty<string>());
        var caseCoordinator = new IssueCaseCoordinator(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(),
            _backend.RecordStore(), _hosts, new IssueOwnerStore(_backend.Blob("issue_owners")));
        var dispatch = NightlyDispatchFakes.Create(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(), _hosts,
            new IssueOwnerStore(_backend.Blob("issue_owners")), caseCoordinator);
        return new NetiqPipelineService(
            _backend, new NetiqOptions { BackfillDays = 1 }, _sentinels, _hosts, new EventLogService(),
            _ai, _suppressions, reportService, runRecorder, caseCoordinator, dispatch, new RecordingRunConsole(_console),
            riskyEventStore: null, rawEventRetentionDays: 14, useAi: false, progress: null,
            clientFactory: clientFactory);
    }

    private string LedgerStatus(long hostId)
    {
        var day = DateTime.Today.AddDays(-1);
        return NetiqSourceAttempt.ReadLatest(_backend, day, day)[(hostId, day)].Status;
    }

    [Fact]
    public async Task NetIQ逾時_該批記failed_另一台Sentinel續跑_整體不擲例外()
    {
        var s1 = AddSentinel("S1");
        var s2 = AddSentinel("S2");
        var hostA = AddWindowsHost(s1, "10.0.0.1", "HOST-A");
        var hostB = AddWindowsHost(s2, "10.0.0.2", "HOST-B");

        var timeoutClient = new FakeSentinelSearchClient
        {
            // 傳入的 ct 並未取消：模擬 HttpClient 逾時
            Responder = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")
        };
        var okClient = new FakeSentinelSearchClient();
        var pipeline = MakePipeline(FakeSentinelSearchClientFactory.ByName(new Dictionary<string, FakeSentinelSearchClient>
        {
            ["S1"] = timeoutClient, ["S2"] = okClient
        }));

        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.True(result.HostsFailed >= 1, $"逾時批次應計入失敗主機日，實際 {result.HostsFailed}");
        Assert.NotEmpty(timeoutClient.Requests);
        Assert.NotEmpty(okClient.Requests); // 另一台 Sentinel 續跑
        Assert.Equal("failed", LedgerStatus(hostA.HostId));
        Assert.Equal("success", LedgerStatus(hostB.HostId));
    }

    [Fact]
    public async Task NetIQ整台Sentinel逾時_失敗隔離_另一台續跑()
    {
        var s1 = AddSentinel("S1");
        var s2 = AddSentinel("S2");
        AddWindowsHost(s1, "10.0.0.1", "HOST-A");
        var hostB = AddWindowsHost(s2, "10.0.0.2", "HOST-B");

        var okClient = new FakeSentinelSearchClient();
        // 建立用戶端階段就逾時（ct 未取消），走 RunAsync 的 Sentinel 層失敗隔離
        var pipeline = MakePipeline(sentinel => sentinel.Name == "S1"
            ? throw new TaskCanceledException("timeout")
            : okClient);

        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.True(result.HostsFailed >= 1);
        Assert.NotEmpty(okClient.Requests);
        Assert.Equal("success", LedgerStatus(hostB.HostId));
    }

    [Fact]
    public async Task NetIQ使用者取消_RunAsync擲OCE_帳本記stopped()
    {
        var s1 = AddSentinel("S1");
        var hostA = AddWindowsHost(s1, "10.0.0.1", "HOST-A");

        using var cts = new CancellationTokenSource();
        var client = new FakeSentinelSearchClient
        {
            Responder = _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
        };
        var pipeline = MakePipeline(FakeSentinelSearchClientFactory.Single(client));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14, cts.Token));

        Assert.Equal("stopped", LedgerStatus(hostA.HostId));
    }

    // ── Orchestrator ─────────────────────────────────────────────────────

    private sealed class ThrowingTail(Func<Exception> make) : IPrtgBackfillTail
    {
        public Task<bool> RunTailAsync(int days, IReadOnlyCollection<long>? hostIds, IRunConsole console, CancellationToken ct) =>
            throw make();
    }

    private sealed class EmptyCandidateSource : IDispatchCandidateSource
    {
        public DispatchCandidatePool Build() => new()
        {
            ByUserId = new Dictionary<long, DispatchCandidate>(), PoolMemberCount = 0, ActivePoolMemberCount = 0
        };
    }

    private sealed class ListConsole(List<string> lines) : IRunConsole
    {
        public void WriteLine(string message = "") => lines.Add(message);
    }

    private static async Task<(OrchestratorResult Result, List<string> Console)> RunOrchestratorAsync(
        IPrtgBackfillTail tail, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-test-cancel-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        try
        {
            var settings = new AppSettings
            {
                Ai = new AiSettings { BaseUrl = "" },
                Storage = new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "test.db")}" }
            };
            var orchestrator = new AnalysisOrchestrator(new EmptyCandidateSource(), NullAiRequestGate.Instance);
            var result = await orchestrator.RunAsync(
                new RunRequest { Scope = RunScope.Full, IncludeLocal = false, BackfillOverride = 3, PrtgBackfillDays = 3 },
                settings, dir, new RetentionOptions(), new ListConsole(lines), ct,
                progress: null, manualWorkGate: null, prtgBackfillTail: tail);
            return (result, lines);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Orchestrator逾時_失敗訊息為呼叫外部服務逾時_不是使用者取消()
    {
        var (result, lines) = await RunOrchestratorAsync(
            new ThrowingTail(() => new TaskCanceledException("timeout")), CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith("呼叫外部服務逾時", result.FailureMessage);
        Assert.DoesNotContain("使用者取消", result.FailureMessage);
        Assert.DoesNotContain(lines, l => l.Contains("使用者取消"));
    }

    [Fact]
    public async Task Orchestrator記憶體不足_失敗原因為記憶體上限_執行紀錄仍有結束標記()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-test-oom-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new AppSettings
            {
                Ai = new AiSettings { BaseUrl = "" },
                Storage = new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "test.db")}" }
            };
            var orchestrator = new AnalysisOrchestrator(new EmptyCandidateSource(), NullAiRequestGate.Instance);
            var result = await orchestrator.RunAsync(
                new RunRequest { Scope = RunScope.Full, IncludeLocal = false, BackfillOverride = 3, PrtgBackfillDays = 3 },
                settings, dir, new RetentionOptions(), new ListConsole(new List<string>()), CancellationToken.None,
                progress: null, manualWorkGate: null,
                prtgBackfillTail: new ThrowingTail(() => new OutOfMemoryException("模擬撞到硬上限")));

            Assert.False(result.Success);
            Assert.Equal(RunFailureReasons.MemoryLimit, result.FailureMessage);

            // 失敗正是最需要結束標記的情境：開始與結束標記都要寫進這一趟
            var backend = new StorageBackend(settings.Storage, dir);
            var run = Assert.Single(new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs"))
                .GetRecentRuns(1, null));
            Assert.NotNull(run.MemoryStart);
            Assert.NotNull(run.MemoryEnd);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Orchestrator使用者取消_失敗訊息維持使用者取消()
    {
        using var cts = new CancellationTokenSource();
        var (result, _) = await RunOrchestratorAsync(
            new ThrowingTail(() =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            }), cts.Token);

        Assert.False(result.Success);
        Assert.Equal("使用者取消", result.FailureMessage);
    }
}
