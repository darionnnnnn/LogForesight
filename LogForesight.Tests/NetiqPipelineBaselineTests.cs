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

    private WebHost AddLinuxHost(Sentinel sentinel, string ip, string hostName) =>
        _hosts.Upsert(new WebHost
        {
            HostName = hostName, IpAddress = ip, Os = WebHost.OsLinux,
            SentinelId = sentinel.SentinelId, NetiqServer = sentinel.Name, Source = "netiq", Active = true
        });

    /// <summary>10 筆 sshd 暴力破解樣本（docs/FEEDBACK-12-PLAN.md §4.0 第四次 probe 實際樣本格式）
    /// ——10 筆剛好達 builtin-linux-ssh-bruteforce 的 CountThreshold，聚合後應命中滿嚴重度 High。</summary>
    private static SentinelEvent LinuxBruteforceEvent(string ip, int index) => new(new Dictionary<string, string>
    {
        [SentinelFieldMap.HostIp] = ip,
        [SentinelFieldMap.HostName] = "linux01",
        [SentinelFieldMap.LinuxProgram] = "sshd",
        [SentinelFieldMap.Timestamp] = DateTime.UtcNow.AddMinutes(-index).ToString("O"),
        [SentinelFieldMap.Severity] = "1",
        [SentinelFieldMap.Message] = $"Failed password for invalid user test{index} from 10.9.9.9 port 22 ssh2"
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

    /// <summary>
    /// docs/FEEDBACK-12-PLAN.md §4B（拆 Windows 擋板）：同一台 Sentinel 轄下同時有 Windows 與
    /// Linux 主機時，兩者都應該被實際查詢與分析——不再是「Linux 台印警告、本次不查」。
    /// 用 filter 內容（是否含 `sp:`）分辨兩次查詢分別回什麼形狀的假事件，驗證 Linux 主機
    /// 端到端跑完整條 mapper→aggregator→classify 鏈：正確聚合成 EventKey、LogName、次數、嚴重度
    /// 都對得上，不是只有「有寫入紀錄」這種粗淺斷言。
    /// </summary>
    [Fact]
    public async Task 同一Sentinel下Windows與Linux主機皆被查詢且各自正確映射()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        AddLinuxHost(sentinel, "10.0.0.9", "HOST-L");

        _client.Responder = request =>
        {
            if (request.Filter.Contains($"{SentinelFieldMap.LinuxProgram}:"))
            {
                var events = Enumerable.Range(0, 10).Select(i => LinuxBruteforceEvent("10.0.0.9", i)).ToArray();
                return new SentinelSearchResult { Events = events, Found = events.Length, State = SentinelJobState.Completed };
            }
            return new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 0, State = SentinelJobState.Completed };
        };

        var pipeline = MakePipeline(useAi: false);
        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(2, result.HostDaysAnalyzed); // 1 Windows + 1 Linux，兩者都真的跑了
        Assert.Equal(0, result.HostsFailed);
        Assert.Empty(result.Warnings); // 不再有「Linux 主機本次不查詢」警告
        Assert.Equal(2, _client.Requests.Count); // 同一天，但 Windows/Linux 各自一次批次查詢

        var yesterday = DateTime.Today.AddDays(-1);

        var windowsStore = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        Assert.Single(windowsStore.ReadRecent(yesterday, 1));

        var linuxStore = _backend.RecordStore(new HostKey { HostId = 2, HostName = "HOST-L" });
        var linuxRecord = Assert.Single(linuxStore.ReadRecent(yesterday, 1));
        var bruteforceIssue = Assert.Single(linuxRecord.TopIssues, i => i.EventKey == "builtin-linux-ssh-bruteforce");
        Assert.Equal(LogAggregator.LinuxLogName, bruteforceIssue.LogName);
        Assert.Equal("sshd", bruteforceIssue.Source);
        Assert.Equal(0, bruteforceIssue.EventId);
        Assert.Equal(10, bruteforceIssue.Count);
        Assert.Equal(IssueSeverity.High, bruteforceIssue.Severity);
    }

    /// <summary>
    /// 回饋十三輪 B2：查詢截斷時二分重查，收斂到單台才誠實標記資料不完整——不再一台吵的主機
    /// 拖垮整批的趨勢基準。4 台主機模擬「批次越大越容易撞單一 job 上限」：查詢帶 2 台以上 IP
    /// 時回傳截斷結果，只剩 1 台時查得到完整結果，藉此逼出遞迴切半的路徑（4→2+2→1+1+1+1）。
    /// </summary>
    [Fact]
    public async Task 查詢截斷時二分重查_收斂到單台才標記資料不完整()
    {
        var sentinel = AddSentinel();
        var ips = new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4" };
        var hosts = ips.Select((ip, i) => AddWindowsHost(sentinel, ip, $"HOST-{i}")).ToList();

        _client.Responder = request =>
        {
            var ipsInRequest = ips.Count(ip => request.Filter.Contains(ip));
            return ipsInRequest > 1
                // 兩台以上共用一次查詢：模擬撞到單一 job 的 MaxResultsPerJob 上限
                ? new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 1000, State = SentinelJobState.Completed }
                // 已收斂到單台：查得到完整結果，不再截斷
                : new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 0, State = SentinelJobState.Completed };
        };

        var pipeline = MakePipeline(useAi: false);
        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(4, result.HostDaysAnalyzed);
        Assert.Equal(0, result.HostsFailed);
        // 遞迴切半的查詢次數：1（4台）+2（各2台）+4（各1台）＝7，證明真的有切半重試，
        // 不是巧合地全部主機都沒事
        Assert.Equal(7, _client.Requests.Count);

        var yesterday = DateTime.Today.AddDays(-1);
        foreach (var host in hosts)
        {
            var store = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
            var record = Assert.Single(store.ReadRecent(yesterday, 1));
            Assert.False(record.DataIncomplete,
                $"{host.HostName} 不該被連坐標記資料不完整——切到單台查詢時本身已不再截斷");
        }
    }

    /// <summary>單台查詢仍然截斷時（已無法再切），照既有行為誠實標記該台資料不完整</summary>
    [Fact]
    public async Task 單台查詢仍截斷時標記該台資料不完整()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ =>
            new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 999, State = SentinelJobState.Completed };

        var pipeline = MakePipeline(useAi: false);
        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(1, result.HostDaysAnalyzed);
        Assert.Single(_client.Requests);   // 單台本來就切不下去，不會無窮遞迴

        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        var record = Assert.Single(store.ReadRecent(yesterday, 1));
        Assert.True(record.DataIncomplete);
    }

    // ── 回饋十三輪 D：單一 Sentinel 內部的 client pool 併發 ──────────────────────
    // 51 台主機才會跨過 IpBatchSize=50，真的切出兩個批次——這是唯一能逼出「同一天內
    // 多批次」路徑的方式，主機數不能再省。_client.Delay 給查詢一點耗時，讓平行呼叫
    // 真的有機會重疊，不然即使程式碼有平行能力，跑太快也測不出峰值差異。

    /// <summary>MaxParallelQueriesPerServer 大於 1 時，同一天內的批次真的會平行送出查詢，
    /// 但峰值不超過設定值——client pool 正確節制並行度。不同天之間仍嚴格依序：第二天的任何
    /// 查詢都不該在第一天全部查詢完成前開始（同一台主機的趨勢比對需要前一天已寫入的歷史，
    /// 這個前提不能被批次間的平行破壞）。</summary>
    [Fact]
    public async Task 同天內多批次平行且不超過設定上限_不同天之間仍依序()
    {
        var sentinel = AddSentinel();
        for (var i = 1; i <= 51; i++)
        {
            AddWindowsHost(sentinel, $"10.0.9.{i}", $"HOST-D-{i}");
        }
        _client.Delay = TimeSpan.FromMilliseconds(20);
        var options = new NetiqOptions { BackfillDays = 2, MaxParallelQueriesPerServer = 2 };
        var pipeline = MakePipeline(useAi: false, options: options);

        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(102, result.HostDaysAnalyzed); // 51 台 × 2 天缺漏
        Assert.Equal(2, _client.PeakConcurrency);   // 51 台切成 50+1 兩批，池大小 2，兩批同時跑

        // 日期序：找出第一天最後一筆查詢的位置，必須早於第二天第一筆查詢的位置
        var byOrder = _client.Requests.Select((r, index) => (r.Start, index)).ToList();
        var firstDayStart = byOrder.Min(x => x.Start);
        var lastOfFirstDay = byOrder.Where(x => x.Start == firstDayStart).Max(x => x.index);
        var firstOfLaterDay = byOrder.Where(x => x.Start != firstDayStart).Min(x => x.index);
        Assert.True(lastOfFirstDay < firstOfLaterDay,
            "第二天的查詢不該在第一天全部查詢完成前開始——趨勢比對需要前一天已寫入的歷史。");
    }

    /// <summary>MaxParallelQueriesPerServer 預設 1（未設定）時，同一天內即使有多個批次也維持
    /// 依序處理——這是既有行為的逃生門，也是改動前的逐位行為。</summary>
    [Fact]
    public async Task 查詢平行度預設為一時_同天多批次仍依序處理()
    {
        var sentinel = AddSentinel();
        for (var i = 1; i <= 51; i++)
        {
            AddWindowsHost(sentinel, $"10.0.9.{i}", $"HOST-D-{i}");
        }
        _client.Delay = TimeSpan.FromMilliseconds(20);
        var pipeline = MakePipeline(useAi: false); // MaxParallelQueriesPerServer 未設，預設 1

        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(51, result.HostDaysAnalyzed);
        Assert.Equal(1, _client.PeakConcurrency); // 池只有 1 個 client，兩批依序跑，從未重疊
    }
}

/// <summary>把輸出行存進呼叫端提供的清單，供測試檢查文字內容（如平行度、警告訊息）。</summary>
internal sealed class RecordingRunConsole : IRunConsole
{
    private readonly List<string> _lines;
    public RecordingRunConsole(List<string> lines) => _lines = lines;
    public void WriteLine(string message = "") => _lines.Add(message);
}
