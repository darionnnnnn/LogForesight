using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetiqPipelineService 的行為基準測試（docs/archive/FEEDBACK-12-PLAN.md §3.8-3）。
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

    /// <summary>10 筆 sshd 暴力破解樣本（docs/archive/FEEDBACK-12-PLAN.md §4.0 第四次 probe 實際樣本格式）
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

    private NetiqPipelineService MakePipeline(bool useAi = false, NetiqOptions? options = null, IHostStore? overrideHosts = null)
    {
        var netiqOptions = options ?? new NetiqOptions { BackfillDays = 1 };
        var hosts = overrideHosts ?? _hosts;
        var reportSink = new FakeReportSink();
        var reportService = new RiskReportService(_ai, reportSink);
        var batchRunStore = new BatchRunStore(_backend.LogStore("baseline_runs"), _backend.LogStore("baseline_run_logs"));
        var runRecorder = new BatchRunRecorder(batchRunStore, "test-host", Array.Empty<string>());
        var caseCoordinator = new IssueCaseCoordinator(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(),
            _backend.RecordStore(), hosts, new IssueOwnerStore(_backend.Blob("issue_owners")));
        var console = new RecordingRunConsole(_console);

        return new NetiqPipelineService(
            _backend, netiqOptions, _sentinels, hosts, new EventLogService(),
            _ai, _suppressions, reportService, runRecorder, caseCoordinator, console,
            riskyEventStore: null, rawEventRetentionDays: 14, useAi: useAi, progress: null,
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

    /// <summary>基準 2：命中重大規則的主機在 useAi=true 時，取數端仍零 AI 呼叫，紀錄標記 AiPending=true。</summary>
    [Fact]
    public async Task 命中重大規則且useAi為true時取數端零AI呼叫且紀錄標記AiPending()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };

        var pipeline = MakePipeline(useAi: true);
        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(1, result.HostDaysAnalyzed);
        Assert.Equal(0, _ai.Calls);

        var yesterday = DateTime.Today.AddDays(-1);
        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        var record = Assert.Single(store.ReadRecent(yesterday, 1));
        Assert.Equal("高", record.RiskLevel);
        Assert.True(record.AiPending);
        Assert.False(record.AiAnalyzed);
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
    /// docs/archive/FEEDBACK-12-PLAN.md §4B（拆 Windows 擋板）：同一台 Sentinel 轄下同時有 Windows 與
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

    // ── 回饋十四輪 B1：二分重查跨日記憶（WebHost.IsHighVolume）─────────────────

    /// <summary>
    /// 跨日記憶讓下次執行分批時直接把高流量主機獨立成一批，不必每天都從整批大小重新二分
    /// 收斂到它。同樣是 4 台主機各查 1 個新缺漏日：第一次執行（無人有旗標）要走完整的
    /// 二分流程（4→2+2→1+1）才收斂到單台並標記旗標；第二次執行（旗標已從第一次帶過來）
    /// 應該一開始就把高流量主機獨立成單台批次，查詢次數明顯低於第一次。
    /// </summary>
    [Fact]
    public async Task 高流量主機跨日記憶_第二次執行查詢次數明顯低於第一次()
    {
        var sentinel = AddSentinel();
        const string chattyIp = "10.0.0.1";
        var hosts = new[] { chattyIp, "10.0.0.2", "10.0.0.3", "10.0.0.4" }
            .Select((ip, i) => AddWindowsHost(sentinel, ip, $"HOST-{i}")).ToList();

        // 只要批次的查詢條件包含這台 IP 就模擬截斷——不管跟誰同批、甚至單獨查詢也一樣，
        // 模擬「這台主機本身流量就大到撞單一 job 上限」，不是批次太擁擠的偶發現象
        _client.Responder = request => request.Filter.Contains(chattyIp)
            ? new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 1000, State = SentinelJobState.Completed }
            : new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 0, State = SentinelJobState.Completed };

        // 第一次執行：昨天缺漏、無人有旗標，撞到截斷要完整走一輪二分才收斂到單台
        var pipeline1 = MakePipeline(useAi: false, options: new NetiqOptions { BackfillDays = 1 });
        var firstResult = await pipeline1.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);
        var firstCallQueries = _client.Requests.Count;

        Assert.Equal(4, firstResult.HostDaysAnalyzed);
        Assert.True(_hosts.Get(hosts[0].HostId)!.IsHighVolume, "收斂到單台仍截斷，應該被標記為高流量主機");

        // 第二次執行（模擬下一次排程）：BackfillDays 加大到 2，昨天已有紀錄不重複處理，
        // 只有前天是新的缺漏日——4 台主機一樣各自新增 1 個缺漏日，場景結構與第一次對等，
        // 差別只在旗標是否已從第一次執行帶過來
        var pipeline2 = MakePipeline(useAi: false, options: new NetiqOptions { BackfillDays = 2 });
        var secondResult = await pipeline2.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);
        var secondCallQueries = _client.Requests.Count - firstCallQueries;

        Assert.Equal(4, secondResult.HostDaysAnalyzed);
        Assert.True(secondCallQueries < firstCallQueries,
            $"第二次執行應該因為跨日記憶而少查詢很多次（第一次 {firstCallQueries} 次，第二次 {secondCallQueries} 次）");
    }

    /// <summary>
    /// 旗標清除：已標記為高流量的主機，當某次單獨查詢不再截斷、且量遠低於上限（&lt;50%）時，
    /// 旗標應自動清除，避免安靜下來的主機被永久孤立成單台批次（遲滯設計，防臨界主機
    /// 每天在「單獨成批／合批再二分」間震盪——見 RunBatchDayAsync 的清除條件註解）。
    /// </summary>
    [Fact]
    public async Task 高流量主機安靜下來後旗標自動清除()
    {
        var sentinel = AddSentinel();
        var host = AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        _hosts.SetHighVolume(host.HostId, true); // 模擬先前執行已標記為高流量

        // 這次查詢量遠低於上限（Found=0 < MaxResultsPerJob/2），不再截斷
        _client.Responder = _ =>
            new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 0, State = SentinelJobState.Completed };

        var pipeline = MakePipeline(useAi: false, options: new NetiqOptions { BackfillDays = 1 });
        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(1, result.HostDaysAnalyzed);
        Assert.Single(_client.Requests); // 旗標讓它一開始就單獨成批，不需要二分
        Assert.False(_hosts.Get(host.HostId)!.IsHighVolume, "查詢已不再截斷且遠低於上限，旗標應被清除");
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

    /// <summary>
    /// 回饋十四輪 A3：HostPlan.GroupIds 在計畫階段（RunServerOsGroupAsync）解析一次，取代原本
    /// AnalyzeHostDayAsync 每主機日各自呼叫一次 HostStore.Get（整份主機清單 JSON blob
    /// 反序列化、無快取）。2 台主機×3 個缺漏日：修復前 Get 呼叫次數等於主機日數（6），
    /// 修復後應收斂為主機數（2）。
    /// </summary>
    [Fact]
    public async Task 缺漏多天時HostStoreGet呼叫次數等於主機數而非主機日數()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        AddWindowsHost(sentinel, "10.0.0.2", "HOST-B");
        var beforeRun = _hosts.GetCallCount;
        var pipeline = MakePipeline(useAi: false, options: new NetiqOptions { BackfillDays = 3 });

        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(6, result.HostDaysAnalyzed); // 2 台 × 3 天，確認場景本身橫跨多天可比對
        Assert.Equal(2, _hosts.GetCallCount - beforeRun);
    }

    /// <summary>
    /// 回饋十四輪 A3：抑制清單在 RunAsync 開始時讀一次，往下傳給每台主機每一天共用的快照，
    /// 取代原本每主機日各自呼叫一次 LoadAll()。
    /// </summary>
    [Fact]
    public async Task 缺漏多天時抑制清單LoadAll只呼叫一次()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        AddWindowsHost(sentinel, "10.0.0.2", "HOST-B");
        var pipeline = MakePipeline(useAi: false, options: new NetiqOptions { BackfillDays = 3 });

        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(6, result.HostDaysAnalyzed); // 場景本身橫跨多主機多天，才能證明不是巧合
        Assert.Equal(1, _suppressions.LoadAllCallCount);
    }

    [Fact]
    public async Task 執行完畢且空緩衝時_不觸發主機清單全量寫入()
    {
        using var fx = new EfSqliteFixture();
        var blob = fx.Blob("hosts");
        var realHosts = new HostStore(blob);

        var sentinel = AddSentinel();
        var host = new WebHost
        {
            HostName = "HOST-A", IpAddress = "10.0.0.1", Os = WebHost.OsWindows,
            SentinelId = sentinel.SentinelId, NetiqServer = sentinel.Name, Source = "netiq", Active = true
        };
        realHosts.Upsert(host);

        var initialVersion = blob.ReadVersion();

        // 故意讓這台主機查不到任何事件，hostReported 會是 false，所以不會有任何 touch 寫入緩衝
        _client.Responder = _ => new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 0, State = SentinelJobState.Completed };

        var pipeline = MakePipeline(useAi: false, overrideHosts: realHosts);
        await pipeline.RunAsync(HostListSelection.FromStore(realHosts, _sentinels), trendWindowDays: 14);

        Assert.Equal(initialVersion, blob.ReadVersion());
    }

    [Fact]
    public async Task 批次更新主機_未帶回名稱的主機不覆蓋既有顯示名()
    {
        var sentinel = AddSentinel();
        // 刻意先建好帶有「既有顯示名」的主機
        var hostA = AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        hostA.DisplayName = "既有顯示名-A";
        _hosts.Upsert(hostA);

        var hostB = AddWindowsHost(sentinel, "10.0.0.2", "HOST-B");
        hostB.DisplayName = "既有顯示名-B";
        _hosts.Upsert(hostB);

        _client.Responder = request =>
        {
            var isHostA = request.Filter.Contains("10.0.0.1");
            var isHostB = request.Filter.Contains("10.0.0.2");
            var events = new List<SentinelEvent>();

            if (isHostA || isHostB)
            {
                if (isHostA)
                {
                    events.Add(new SentinelEvent(new Dictionary<string, string>
                    {
                        [SentinelFieldMap.HostIp] = "10.0.0.1",
                        [SentinelFieldMap.Timestamp] = DateTime.UtcNow.ToString("O")
                    }));
                }

                if (isHostB)
                {
                    events.Add(new SentinelEvent(new Dictionary<string, string>
                    {
                        [SentinelFieldMap.HostIp] = "10.0.0.2",
                        [SentinelFieldMap.HostName] = "NEW-NAME-B",
                        [SentinelFieldMap.Timestamp] = DateTime.UtcNow.ToString("O")
                    }));
                }

                return new SentinelSearchResult { Events = events, Found = events.Count, State = SentinelJobState.Completed };
            }

            return new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 0, State = SentinelJobState.Completed };
        };

        var pipeline = MakePipeline(useAi: false);
        await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        var afterA = _hosts.FindByName("HOST-A")!;
        Assert.Equal("既有顯示名-A", afterA.DisplayName); // 不應該被清掉

        var afterB = _hosts.FindByName("HOST-B")!;
        Assert.Equal("NEW-NAME-B", afterB.DisplayName); // 有傳名字的應該要更新
    }

    /// <summary>
    /// 回饋二十輪 D：回報時間批次寫回失敗時，不可讓整台 Sentinel 的主機被記成失敗。
    /// hosts blob 的行程內互斥是 per-instance 的鎖，Web 端與分析端各有一份 backend，
    /// 同一份 blob 真的會在 DB 層競爭並在重試耗盡後拋出——那個例外若冒到 RunServerAsync
    /// 尾端，會被 Parallel.ForEachAsync 的 catch 記成「整台 Sentinel 失敗」，
    /// 幾百台已經分析完成的主機因此變成失敗。
    /// </summary>
    [Fact]
    public async Task 回報時間寫回失敗時_分析結果不作廢且主機不被記為失敗()
    {
        var sentinel = AddSentinel();
        AddWindowsHost(sentinel, "10.0.0.1", "HOST-A");
        // 要真的有事件才會累積回報時間（零事件刻意不 Touch，見 hostReported 的註解）
        _client.Responder = _ => new SentinelSearchResult
        {
            Events = new[] { HighRiskEvent("10.0.0.1") }, Found = 1, State = SentinelJobState.Completed
        };
        _hosts.ThrowOnMutateBatch = true;
        var pipeline = MakePipeline(useAi: false);

        var result = await pipeline.RunAsync(HostListSelection.FromStore(_hosts, _sentinels), trendWindowDays: 14);

        Assert.True(_hosts.MutateBatchAttempts > 0);   // 真的走到了批次寫回
        Assert.Equal(0, result.HostsFailed);
        Assert.Equal(1, result.HostDaysAnalyzed);

        // 分析結果本身照樣落地
        var store = _backend.RecordStore(new HostKey { HostId = 1, HostName = "HOST-A" });
        Assert.Single(store.ReadRecent(DateTime.Today.AddDays(-1), 1));
    }
}

/// <summary>把輸出行存進呼叫端提供的清單，供測試檢查文字內容（如平行度、警告訊息）。</summary>
internal sealed class RecordingRunConsole : IRunConsole
{
    private readonly List<string> _lines;
    public RecordingRunConsole(List<string> lines) => _lines = lines;
    public void WriteLine(string message = "") => _lines.Add(message);
}
