using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ pipeline 兩階段拆分後的新行為（docs/archive/FEEDBACK-12-PLAN.md §3.4/§3.6/§3.8-4）：
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

    /// <summary>
    /// 等到條件成立，逾時就**明確失敗並說出等的是什麼**。
    ///
    /// 這批測試原本各處是「輪詢到 5 秒就放棄、然後繼續往下跑」。兩個問題：
    /// 逾時後測試帶著錯誤的前提繼續執行，最後在別的地方以看不懂的斷言失敗收場；
    /// 而且全套並行跑滿 CPU 時 5 秒並不夠，會間歇性假失敗。
    /// 逾時放寬到 30 秒（真的卡死仍會在 30 秒內失敗，不會把測試掛住），並在這裡就斷言。
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), $"等待逾時（{timeoutSeconds} 秒）：{what}");
    }

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

    /// <summary>回饋二十七輪作業 B：記下每次進度回報，驗證背壓期間報的是 AI 消化件數</summary>
    private sealed class RecordingProgress : IRunProgress
    {
        public List<(string Phase, int Done, int Total)> Reports { get; } = new();

        public void Report(string phase, int done, int total)
        {
            lock (Reports) Reports.Add((phase, done, total));
        }
    }

    private NetiqPipelineService MakePipeline(
        NetiqOptions? options = null, List<string>? consoleLines = null, IRiskyEventStore? riskyEventStore = null,
        IRunProgress? progress = null, int? aiQueueCapacity = null)
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

        var pipeline = new NetiqPipelineService(
            _backend, netiqOptions, _sentinels, _hosts, new EventLogService(),
            _ai, _suppressions, reportService, runRecorder, caseCoordinator, console,
            riskyEventStore: riskyEventStore, rawEventRetentionDays: 14, useAi: true, progress: progress,
            clientFactory: FakeSentinelSearchClientFactory.Single(_client));

        if (aiQueueCapacity.HasValue) pipeline.AiQueueCapacity = aiQueueCapacity.Value;
        return pipeline;
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

        await WaitUntilAsync(() => _client.Requests.Count >= 3, "三天的搜尋全部發出");

        Assert.Equal(3, _client.Requests.Count); // 三天的搜尋全部發出，沒有被卡住的 AI 擋住

        // 收尾：解除卡住讓背景執行完成，避免留下懸掛的 Task 影響後續測試
        hang.SetResult(new AiResponse { Success = true, Content = "{}" });
        await runTask;
    }

    /// <summary>
    /// 回饋二十七輪作業 B：AI 佇列滿載、搜尋暫停等待時，子進度必須報「AI 消化件數」，
    /// 不能沿用主機日數字——搜尋此時已停，主機日數字到等待結束都不會變，
    /// 畫面就是一條凍住的進度條（使用者回饋②：背景在跑卻看不出進度，以為卡住了）。
    /// </summary>
    [Fact]
    public async Task 背壓期間子進度報的是AI消化件數而非主機日()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };

        var hang = new TaskCompletionSource<AiResponse>();
        _ai.Behavior = (_, ct) => hang.Task.WaitAsync(ct);

        var progress = new RecordingProgress();
        // 容量 1：第一件被消費者取走後卡在 AI，第二件填滿佇列，第三件就會進背壓等待
        var pipeline = MakePipeline(new NetiqOptions { BackfillDays = 3 }, progress: progress, aiQueueCapacity: 1);
        var runTask = pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        await WaitUntilAsync(
            () => { lock (progress.Reports) return progress.Reports.Any(r => r.Phase == "netiq-backpressure"); },
            "背壓進度已回報");

        List<(string Phase, int Done, int Total)> backpressure;
        lock (progress.Reports)
            backpressure = progress.Reports.Where(r => r.Phase == "netiq-backpressure").ToList();

        // 分母是已排入 AI 的件數（此時 1~2 件），不是三個主機日
        Assert.All(backpressure, r => Assert.True(r.Total < 3,
            $"背壓進度分母應為 AI 件數而非主機日總數，實際 {r.Total}"));
        Assert.All(backpressure, r => Assert.True(r.Done <= r.Total));

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
        await WaitUntilAsync(() => _ai.Calls > 0, "AI 已經被呼叫（卡在 hang 上）");

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

    /// <summary>
    /// AiPending 孤兒補跑（docs/archive/FEEDBACK-12-PLAN.md §3.10）：取消留下的 AiPending 紀錄不會
    /// 永遠卡住——下一次執行時，即使該主機當天已經有紀錄（不算缺漏日、不會被搜尋撿到），
    /// 孤兒補跑機制仍要獨立掃到它並把 AI 分析補上。這是兩階段化本身引入的新風險
    /// （寫入時間點提前、AI 可能永遠追不上）的自癒機制，沒有這個測試就等於沒驗證過
    /// 「取消真的不會遺失工作」這個承諾的下半段。
    /// </summary>
    [Fact]
    public async Task 取消留下的孤兒紀錄下次執行會被自動補跑()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };

        // 第一次執行：AI 卡住，取消，留下 AiPending=true 的孤兒紀錄
        var hang = new TaskCompletionSource<AiResponse>();
        _ai.Behavior = (_, ct) => hang.Task.WaitAsync(ct);
        var firstPipeline = MakePipeline(new NetiqOptions { BackfillDays = 1 });
        using (var cts = new CancellationTokenSource())
        {
            var runTask = firstPipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14, cts.Token);
            await WaitUntilAsync(() => _ai.Calls > 0, "AI 已經被呼叫");
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        }

        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        Assert.True(Assert.Single(store.ReadRecent(yesterday, 1)).AiPending);

        // 第二次執行：AI 恢復正常回應。該主機日已有紀錄（不是缺漏日，搜尋不會再查一次），
        // 但孤兒補跑機制應該獨立掃到它並補上 AI 分析
        _ai.Behavior = null;
        _ai.NextContent = """{"risk_level":"高","headline":"補跑後的標題","story":"補跑後的白話摘要","trend_story":"","action":"a"}""";
        var secondPipeline = MakePipeline(new NetiqOptions { BackfillDays = 1 });
        var result = await secondPipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(1, result.AiQueued);   // 只有孤兒補跑這一件，沒有新的缺漏日
        Assert.Equal(1, result.AiCompleted);
        Assert.Equal(0, result.HostDaysAnalyzed); // 沒有新的統計段執行（該主機日已有紀錄）

        var finalRecord = Assert.Single(store.ReadRecent(yesterday, 1));
        Assert.False(finalRecord.AiPending);
        Assert.True(finalRecord.AiAnalyzed);
        Assert.Equal("補跑後的標題", finalRecord.Headline);
        // 這個 fixture 沒有接風險事件暫存（riskyEventStore: null，見 MakePipeline 預設值）——
        // 報告從缺是因為這裡沒接暫存，不是「補跑不補深析」這個舊決策（回饋十三輪 C 已改為
        // 連深析一起補，見 RetryAiAsync；接了暫存的正例見下一個測試）
        Assert.Null(finalRecord.ReportFile);
    }

    /// <summary>
    /// 回饋十三輪 C（體檢批 0 #6，取代 §3.10 舊決策）：孤兒補跑改為連深析報告一併補——
    /// 第一次執行雖然在 AI 佇列被取消，但統計段早於取消點已經把風險事件寫進暫存
    /// （HostDayPostProcessor.ReplaceRiskyEvents 在 EnqueueAsync 之前執行），第二次執行的
    /// 孤兒補跑因此撈得到原始 log，能重建出報告——不再永遠留一份缺報告的紀錄。
    /// </summary>
    [Fact]
    public async Task 接上風險事件暫存時孤兒補跑連深析報告一併補上()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };
        var riskyEventStore = _backend.RiskyEventStore();

        var hang = new TaskCompletionSource<AiResponse>();
        _ai.Behavior = (_, ct) => hang.Task.WaitAsync(ct);
        var firstPipeline = MakePipeline(new NetiqOptions { BackfillDays = 1 }, riskyEventStore: riskyEventStore);
        using (var cts = new CancellationTokenSource())
        {
            var runTask = firstPipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14, cts.Token);
            await WaitUntilAsync(() => _ai.Calls > 0, "AI 已經被呼叫");
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        }

        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        Assert.True(Assert.Single(store.ReadRecent(yesterday, 1)).AiPending);
        // 統計段已經把命中規則的事件寫進風險暫存——即使 AI 段被取消，這份佐證已經落地
        Assert.NotEmpty(riskyEventStore.QueryDay(1, yesterday));

        _ai.Behavior = null;
        _ai.NextContent = """{"risk_level":"高","headline":"補跑後的標題","story":"補跑後的白話摘要","trend_story":"","action":"a"}""";
        var secondPipeline = MakePipeline(new NetiqOptions { BackfillDays = 1 }, riskyEventStore: riskyEventStore);
        var result = await secondPipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(1, result.AiQueued);
        Assert.Equal(1, result.AiCompleted);

        var finalRecord = Assert.Single(store.ReadRecent(yesterday, 1));
        Assert.False(finalRecord.AiPending);
        Assert.NotNull(finalRecord.ReportFile);
        Assert.Contains(finalRecord.UncoveredChecks, c => c.Contains("風險事件暫存"));
    }


    /// <summary>
    /// 體檢輪抓到的 bug（docs/archive/FEEDBACK-12-PLAN.md §4，全案體檢）：<c>AiFollowupQueue.EnqueueAsync</c>
    /// 若在呼叫當下 token 已被取消會立即拋出 <see cref="OperationCanceledException"/>（見
    /// <c>AiFollowupQueueTests.取消token觸發時EnqueueAsync拋出取消例外</c>）。這個例外原本會被
    /// <c>AnalyzeHostDayAsync</c> 最外層的 <c>catch (Exception ex)</c> 接住，誤判成「分析失敗、
    /// 未寫入紀錄」——但這筆紀錄其實在呼叫 <c>EnqueueAsync</c> 之前就已經靠
    /// <c>plan.Store.Append</c> 成功寫入。用第二天的假搜尋結果回呼時當場觸發取消，讓「統計段
    /// 已完成寫入、正要排入 AI 佇列」這個時間點精準重現（比起硬撐佇列背壓等消費者時序去撞，
    /// 這個做法不受執行緒排程與 Channel 內部消費/取消的競態影響，能穩定重現）。
    /// 驗證取消後：主控台不會出現第二天的「分析失敗」字樣，取而代之的是「已取消排入 AI
    /// 佇列」；第二天的統計紀錄確實寫入且 AiPending=true（下次執行交給孤兒補跑機制），
    /// 而不是被錯誤地標記失敗、統計結果整個作廢。
    /// </summary>
    [Fact]
    public async Task 取消發生在排入AI佇列當下不算分析失敗()
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
                // 缺漏日按日期升冪處理，第二次查詢對應第二個（也是最後一個）缺漏日——
                // 在這裡取消，讓該日的 BuildStatisticalRecordAsync／Store.Append 照常跑完
                // （純同步計算，不檢查 ct），緊接著呼叫的 EnqueueAsync 才會撞見已取消的 token。
                cts.Cancel();
            }
            return new SentinelSearchResult
            {
                Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
            };
        };

        var consoleLines = new List<string>();
        var pipeline = MakePipeline(new NetiqOptions { BackfillDays = 2 }, consoleLines);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14, cts.Token));

        Assert.DoesNotContain(consoleLines, l => l.Contains("分析失敗"));
        Assert.Contains(consoleLines, l => l.Contains("已取消排入 AI 佇列"));

        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        var cancelledDay = DateTime.Today.AddDays(-1); // 缺漏日 [Today-2, Today-1]，第二個即這天
        var records = store.ReadRecent(cancelledDay, 2);
        Assert.Equal(2, records.Count); // 兩天的統計段都確實寫入，取消不影響任何一天的紀錄落地
        Assert.True(Assert.Single(records, r => r.Date.Date == cancelledDay.Date).AiPending);
    }
}
