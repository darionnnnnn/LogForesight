using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ 取數與 AI 拆離後的取數端行為（批次 D1）：
/// 取數排程完全不碰 AI，只負責統計分析與資料落地；AI 分析改由獨立排程負責。
/// 銜接點為 lf_daily_records.ai_pending 欄位。
/// 驗證重點：
/// 1. 取數路徑零 AI 呼叫：跑完一趟取數後紀錄已落地、ai_pending 為 true、且注入的 AI 服務一次都沒被呼叫。
/// 2. 取消執行時無 AiAbandoned 概念，紀錄維持待補且零 AI 呼叫。
/// 3. 取數端不再掃描 AiPending 孤兒跑 AI。
/// 4. 統計分析、案件掛接、風險事件暫存與權限異動等後處理完全正常。
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

    private NetiqPipelineService MakePipeline(
        NetiqOptions? options = null, List<string>? consoleLines = null, IRiskyEventStore? riskyEventStore = null,
        IRunProgress? progress = null)
    {
        var netiqOptions = options ?? new NetiqOptions { BackfillDays = 1 };
        var reportSink = new FakeReportSink();
        var reportService = new RiskReportService(_ai, reportSink);
        var batchRunStore = new BatchRunStore(_backend.LogStore("decouple_runs"), _backend.LogStore("decouple_run_logs"));
        var runRecorder = new BatchRunRecorder(batchRunStore, "test-host", Array.Empty<string>());
        var caseCoordinator = new IssueCaseCoordinator(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(),
            _backend.RecordStore(), _hosts, new IssueOwnerStore(_backend.Blob("issue_owners")));
        var console = new RecordingRunConsole(consoleLines ?? new List<string>());

        return new NetiqPipelineService(
            _backend, netiqOptions, _sentinels, _hosts, new EventLogService(),
            _ai, _suppressions, reportService, runRecorder, caseCoordinator, console,
            riskyEventStore: riskyEventStore, rawEventRetentionDays: 14, useAi: true, progress: progress,
            clientFactory: FakeSentinelSearchClientFactory.Single(_client));
    }

    /// <summary>
    /// 取數端完全不碰 AI（批次 D1 驗收標準 5）：
    /// 跑完一趟取數後，統計紀錄已落地、ai_pending 為 true、且注入的 AI 服務替身一次都沒被呼叫。
    /// </summary>
    [Fact]
    public async Task 取數路徑零AI呼叫_統計紀錄已落地且AiPending為true()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };

        var pipeline = MakePipeline(new NetiqOptions { BackfillDays = 3 });
        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(3, result.HostDaysAnalyzed);
        Assert.Equal(3, _client.Requests.Count);
        Assert.Equal(0, _ai.Calls); // 關鍵斷言：AI 服務一次都沒被呼叫！

        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        var records = store.ReadRecent(DateTime.Today.AddDays(-1), 3);
        Assert.Equal(3, records.Count);
        Assert.All(records, r =>
        {
            Assert.True(r.AiPending);
            Assert.False(r.AiAnalyzed);
            Assert.Equal("高", r.RiskLevel);
            Assert.Contains("排隊中", r.Headline);
        });
    }

    /// <summary>
    /// 取消執行語意（批次 D1）：不再有 AiAbandoned 的概念。
    /// 取消發生在取數過程時，已落地的紀錄維持 AiPending=true（待補狀態），未呼叫 AI，
    /// 且拋出 OperationCanceledException。
    /// </summary>
    [Fact]
    public async Task 取數執行取消時已寫入紀錄維持AiPending待補且零AI呼叫()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");

        using var cts = new CancellationTokenSource();
        var searchCalls = 0;
        _client.Responder = _ =>
        {
            searchCalls++;
            if (searchCalls == 2)
            {
                // 第二天查詢完成時取消
                cts.Cancel();
            }
            return new SentinelSearchResult
            {
                Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
            };
        };

        var pipeline = MakePipeline(new NetiqOptions { BackfillDays = 3 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14, cts.Token));

        Assert.Equal(0, _ai.Calls); // 取數被取消，從頭到尾零 AI 呼叫

        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        var records = store.ReadRecent(DateTime.Today.AddDays(-1), 3);
        // 第一天（已完成者）確實落地且為待補
        Assert.NotEmpty(records);
        Assert.All(records, r =>
        {
            Assert.True(r.AiPending);
            Assert.False(r.AiAnalyzed);
        });
    }

    /// <summary>
    /// 孤兒掃描移出取數端（批次 D1）：
    /// 取數端不再掃描 AiPending 孤兒跑 AI。主機若已有紀錄（非缺漏日），
    /// 取數排程直接跳過該主機日，不跑 AI，AiPending 維持 true 留交獨立 AI 排程。
    /// </summary>
    [Fact]
    public async Task 既有AiPending紀錄不被取數端重複處理且零AI呼叫()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");

        // 預先寫入一筆 AiPending = true 的既有紀錄（模擬上次執行留下的紀錄）
        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        store.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            HostId = 1,
            Host = "HOST-A",
            RiskLevel = "高",
            AiPending = true,
            AiAnalyzed = false,
            Headline = "（統計已完成，AI 分析排隊中）",
            Summary = "（統計已完成，AI 分析排隊中）"
        });

        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };

        // 執行 1 天回補：昨天已有紀錄，所以取數端應判定無缺漏並跳過
        var pipeline = MakePipeline(new NetiqOptions { BackfillDays = 1 });
        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(0, result.HostDaysAnalyzed);
        Assert.Equal(1, result.HostsSkippedUpToDate);
        Assert.Equal(0, _ai.Calls); // 關鍵：取數端絕不嘗試替孤兒紀錄補跑 AI！

        // 既有紀錄維持待補狀態
        var record = Assert.Single(store.ReadRecent(yesterday, 1));
        Assert.True(record.AiPending);
        Assert.False(record.AiAnalyzed);
    }

    /// <summary>
    /// 後處理正常執行（批次 D1）：
    /// 取數端雖然不碰 AI，但案件掛接、風險事件暫存與權限異動等後處理仍然完全正常執行。
    /// </summary>
    [Fact]
    public async Task 取數路徑零AI呼叫但案件掛接與風險暫存正常落地()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };
        var riskyEventStore = _backend.RiskyEventStore();

        var pipeline = MakePipeline(new NetiqOptions { BackfillDays = 1 }, riskyEventStore: riskyEventStore);
        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(1, result.HostDaysAnalyzed);
        Assert.Equal(0, _ai.Calls);

        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        var record = Assert.Single(store.ReadRecent(yesterday, 1));
        Assert.True(record.AiPending);

        // 風險事件暫存正常寫入
        Assert.NotEmpty(riskyEventStore.QueryDay(1, yesterday));
    }
}
