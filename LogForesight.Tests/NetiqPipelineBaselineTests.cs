using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetiqPipelineService 的行為基準測試（docs/FEEDBACK-12-PLAN.md §3.8-3）。
///
/// 本體過去零整合測試——只測過靜態純函式（<c>ResolveLookbackDays</c>／<c>ResolveParallelism</c>）
/// 與計數器原子性，改動兩階段拆分（§3.4）之前，先對**現行為**（批次查詢→逐台分析→寫入）釘住
/// 幾個關鍵斷言，改造時才有安全網接得住。刻意選最小可行的固定裝置：
/// 一台 Sentinel、一至兩台 Windows 主機、假搜尋用戶端、假 AI，全程走真正的
/// <see cref="StorageBackend"/>（SQLite 檔案）驗證寫入端到端可讀回。
/// </summary>
public sealed class NetiqPipelineBaselineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-netiq-pipeline-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly FakeSuppressionStore _suppressions = new();
    private readonly FakeAiService _ai = new();
    private readonly FakeSentinelSearchClient _client = new();
    private readonly List<string> _console = new();

    public NetiqPipelineBaselineTests()
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

    /// <summary>命中 builtin-security-audit-log-cleared-1102（ElevatesDayRisk=true）的假事件——
    /// 用來讓某個主機日落在「非低風險」，藉此驗證 useAi=true 時真的會呼叫 AI。</summary>
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

    private NetiqPipelineService MakePipeline(bool useAi = false, NetiqOptions? options = null)
    {
        var netiqOptions = options ?? new NetiqOptions { BackfillDays = 1 };
        var reportSink = new FakeReportSink();
        var reportService = new RiskReportService(_ai, reportSink);
        var batchRunStore = new BatchRunStore(_backend.LogStore("baseline_runs"), _backend.LogStore("baseline_run_logs"));
        var runRecorder = new BatchRunRecorder(batchRunStore, "test-host", Array.Empty<string>());
        var caseCoordinator = new IssueCaseCoordinator(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(),
            _backend.RecordStore(), _hosts);
        var console = new RecordingRunConsole(_console);

        return new NetiqPipelineService(
            _backend, netiqOptions, _sentinels, _hosts, new EventLogService(),
            _ai, _suppressions, reportService, runRecorder, caseCoordinator, console,
            riskyEventStore: null, riskyEventRetentionDays: 14, useAi: useAi, progress: null,
            clientFactory: FakeSentinelSearchClientFactory.Single(_client));
    }

    /// <summary>基準 1：全程零事件的乾淨主機——統計模式寫入一筆低風險紀錄，AI 完全不被呼叫。</summary>
    [Fact]
    public async Task 零事件的新主機寫入低風險統計紀錄且不呼叫AI()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        var pipeline = MakePipeline(useAi: false);

        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(1, result.HostDaysAnalyzed);
        Assert.Equal(0, result.HostsFailed);
        Assert.Empty(result.Warnings);
        Assert.Equal(0, _ai.Calls);

        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        var record = Assert.Single(store.ReadRecent(yesterday, 1));
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
        Assert.False(record.AiAnalyzed);
    }

    /// <summary>基準 2：命中重大規則的主機在 useAi=true 時會實際呼叫 AI 主分析一次。</summary>
    [Fact]
    public async Task 命中重大規則且useAi為true時會呼叫AI()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };
        _ai.NextContent = """{"risk_level":"高","headline":"稽核記錄檔遭清除","story":"偵測到 1102 事件。","trend_story":"","action":"立即調查"}""";

        var pipeline = MakePipeline(useAi: true);
        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(1, result.HostDaysAnalyzed);
        Assert.True(_ai.Calls >= 1);

        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        var record = Assert.Single(store.ReadRecent(yesterday, 1));
        Assert.Equal("高", record.RiskLevel);
        Assert.True(record.AiAnalyzed);
    }

    /// <summary>
    /// 基準 3（改造迴圈前最重要的釘子）：搜尋是「按日期批次」發出、跨主機共用，不是逐主機各自查詢。
    /// 2 台主機 × 2 個缺漏日＝4 個「主機日」，但 SearchAsync 只應該被呼叫 2 次（每個日期一次，
    /// 兩台主機在同一批 IpBatchSize 內共用同一次查詢）——§3.4 拆兩階段時若不小心把批次語意拆壞
    /// （例如改成逐主機查詢），這個斷言會先炸。
    /// </summary>
    [Fact]
    public async Task 搜尋依日期批次發出_不是逐主機各自查詢()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        AddWindowsHost(sentinel, "10.0.0.2", "HOST-B");
        var pipeline = MakePipeline(useAi: false, options: new NetiqOptions { BackfillDays = 2 });

        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(4, result.HostDaysAnalyzed);   // 2 台主機 × 2 天
        Assert.Equal(2, _client.Requests.Count);    // 但只發了 2 次查詢（按日期批次，兩台共用）
    }
}

/// <summary>把輸出行存進呼叫端提供的清單，供測試檢查文字內容（如平行度、警告訊息）。</summary>
internal sealed class RecordingRunConsole : IRunConsole
{
    private readonly List<string> _lines;
    public RecordingRunConsole(List<string> lines) => _lines = lines;
    public void WriteLine(string message = "") => _lines.Add(message);
}
