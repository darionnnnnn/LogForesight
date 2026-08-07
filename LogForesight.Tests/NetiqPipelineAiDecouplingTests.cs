using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ pipeline 兩階段拆分後的新行為（docs/FEEDBACK-12-PLAN.md §3.4/§3.6/§3.8-4）：
/// 這批測試存在的唯一理由就是驗證這次改動真正解決的問題——AI 拖住搜尋、取消要能立即生效、
/// FIFO 消費序讓隔日 prompt 仍看得到前一天的 AI 摘要。共用 <see cref="NetiqPipelineBaselineTests"/>
/// 同一套固定裝置慣例（真實 StorageBackend + 假搜尋用戶端 + 假 AI），但每個測試都刻意讓 AI
/// 卡住或延遲，逼出「搜尋有沒有真的不等 AI」這件事，用計時斷言而不是只看最終狀態。
/// </summary>
public sealed class NetiqPipelineAiDecouplingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-netiq-decouple-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly FakeSuppressionStore _suppressions = new();
    private readonly FakeAiService _ai = new();
    private readonly FakeSentinelSearchClient _client = new();

    public NetiqPipelineAiDecouplingTests()
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

    private Sentinel AddSentinel(string name = "S1") =>
        _sentinels.Upsert(new Sentinel { Name = name, BaseUrl = "https://x", Username = "u", PasswordEnc = "p", Active = true });

    private WebHost AddWindowsHost(Sentinel sentinel, string ip, string hostName) =>
        _hosts.Upsert(new WebHost
        {
            HostName = hostName, IpAddress = ip, Os = WebHost.OsWindows,
            SentinelId = sentinel.SentinelId, NetiqServer = sentinel.Name, Source = "netiq", Active = true
        });

    private static SentinelEvent HighRiskEvent(string ip) => new(new Dictionary<string, string>
    {
        [SentinelFieldMap.HostIp] = ip,
        [SentinelFieldMap.HostName] = "dc01",
        [SentinelFieldMap.EventId] = "1102",
        [SentinelFieldMap.Source] = "Microsoft-Windows-Security-Auditing",
        [SentinelFieldMap.LogName] = "Security",
        [SentinelFieldMap.Timestamp] = DateTime.UtcNow.ToString("O"),
        [SentinelFieldMap.Severity] = "4",
        [SentinelFieldMap.Message] = "稽核記錄檔已清除",
        [SentinelFieldMap.XdasOutcome] = "1"
    });

    private NetiqPipelineService MakePipeline(NetiqOptions? options = null)
    {
        var netiqOptions = options ?? new NetiqOptions { BackfillDays = 1 };
        var reportSink = new FakeReportSink();
        var reportService = new RiskReportService(_ai, reportSink);
        var batchRunStore = new BatchRunStore(_backend.LogStore("decouple_runs"), _backend.LogStore("decouple_run_logs"));
        var runRecorder = new BatchRunRecorder(batchRunStore, "test-host", Array.Empty<string>());
        var caseCoordinator = new IssueCaseCoordinator(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(),
            _backend.RecordStore(), _hosts);
        var console = new RecordingRunConsole(new List<string>());

        return new NetiqPipelineService(
            _backend, netiqOptions, _sentinels, _hosts, new EventLogService(),
            _ai, _suppressions, reportService, runRecorder, caseCoordinator, console,
            riskyEventStore: null, riskyEventRetentionDays: 14, useAi: true, progress: null,
            clientFactory: FakeSentinelSearchClientFactory.Single(_client));
    }

    /// <summary>
    /// 核心主張：AI 卡住不回應時，後續日期的搜尋仍會照常發出——不像拆分前那樣批內逐台
    /// await AI，本批沒跑完下一天的 SearchAsync 就不會發出。用一個永遠不完成的
    /// TaskCompletionSource 模擬 AI 逾時/無回應，斷言搜尋次數在短時間內就達到全部天數，
    /// 而不是卡在第一天沒有進展。
    /// </summary>
    [Fact]
    public async Task 搜尋不等AI_AI卡住時後續日期的搜尋仍會發出()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };

        var hang = new TaskCompletionSource<AiResponse>();
        _ai.Behavior = (_, ct) => hang.Task.WaitAsync(ct);

        var pipeline = MakePipeline(new NetiqOptions { BackfillDays = 3 });
        var runTask = pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_client.Requests.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(3, _client.Requests.Count); // 三天的搜尋全部發出，沒有被卡住的 AI 擋住

        // 收尾：解除卡住讓背景執行完成，避免留下懸掛的 Task 影響後續測試
        hang.SetResult(new AiResponse { Success = true, Content = "{}" });
        await runTask;
    }

    /// <summary>
    /// FIFO 保序：同一台主機兩天都需要 AI，消費者依序處理，day2 執行時重讀歷史應該已經
    /// 看得到 day1 附掛好的 AI 摘要——這是「歷史刻意不隨件帶入、AI 段執行當下才重讀」
    /// 這個設計決策要成立的前提，也是拆分後最容易不小心破壞語意的地方。
    /// </summary>
    [Fact]
    public async Task FIFO保序_day2重讀歷史時看得到day1的AI摘要()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };
        _ai.NextContent = """{"risk_level":"高","headline":"h","story":"這是獨特摘要ABC","trend_story":"","action":"a"}""";

        var pipeline = MakePipeline(new NetiqOptions { BackfillDays = 2 });
        await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        var day2 = DateTime.Today.AddDays(-1);
        var day2Label = $"daily-{day2:yyyyMMdd}";
        var labels = _ai.Labels;
        var day2Index = labels.ToList().FindIndex(l => l == day2Label);

        Assert.True(day2Index >= 0, "應該有針對 day2 的主分析呼叫");
        Assert.Contains("這是獨特摘要ABC", _ai.Prompts[day2Index]);
        Assert.Contains("當日結論", _ai.Prompts[day2Index]);
    }

    /// <summary>
    /// 取消語意：執行中途取消時，正在進行中的 AI 呼叫要能立即中止（不是等到 600 秒逾時×重試
    /// 跑完），RunAsync 仍要照既有慣例拋出 OperationCanceledException（讓 BatchRunRecorder
    /// 正確回填「已停止」而不是「完成」），且統計紀錄維持 AiPending=true 供下次執行補跑。
    /// </summary>
    [Fact]
    public async Task 執行取消時進行中的AI呼叫立即中止且紀錄維持AiPending()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };

        var hang = new TaskCompletionSource<AiResponse>();
        _ai.Behavior = (_, ct) => hang.Task.WaitAsync(ct);

        var pipeline = MakePipeline(new NetiqOptions { BackfillDays = 1 });
        using var cts = new CancellationTokenSource();

        var runTask = pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14, cts.Token);

        // 等到 AI 真的被呼叫（消費者已進入 CompleteAiAsync、卡在 hang 上）才取消，
        // 測的是「取消時 AI 呼叫正在進行中」而不是「還沒開始」
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_ai.Calls == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.True(_ai.Calls > 0, "AI 應該已經被呼叫（卡在 hang 上）");

        cts.Cancel();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"取消後應立即返回，實際耗時 {sw.Elapsed}");

        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        var record = Assert.Single(store.ReadRecent(yesterday, 1));
        Assert.True(record.AiPending);
        Assert.False(record.AiAnalyzed);
    }
}
