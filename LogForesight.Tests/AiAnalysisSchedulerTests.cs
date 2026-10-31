using System.Diagnostics;
using System.Reflection;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

public class AiAnalysisSchedulerTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly Func<LfDbContext> _contextFactory;
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeAiService _ai = new();
    private readonly FakeSuppressionStore _suppressions = new();

    /// <summary>回填器（體檢輪）：AI 排程的自動觸發要等存量校正回填完成，測試先跑一次
    /// （空庫零候選、立即 Completed）讓閘門放行</summary>
    private DailyRecordBackfiller CompletedBackfiller()
    {
        var backfiller = new DailyRecordBackfiller(_contextFactory);
        backfiller.Run(CancellationToken.None);
        return backfiller;
    }

    private BatchRunStore BatchRuns() =>
        new(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs"));

    public AiAnalysisSchedulerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-ai-sched-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "ai_test.db")}" }, _dir);

        var field = typeof(StorageBackend).GetField("_dbFactory", BindingFlags.Instance | BindingFlags.NonPublic);
        _contextFactory = (Func<LfDbContext>)field!.GetValue(_backend)!;

        _ai.NextContent = """{"risk_level":"中","headline":"AI分析完成標題","story":"AI分析完成摘要","trend_story":"趨勢正常","action":"持續觀察"}""";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 只回答「AI 有沒有設定好」的假服務。AI 分析沒有獨立的啟用開關（回饋第 40 輪批次E），
    /// 這個判準就是它的總開關。
    /// </summary>
    private sealed class FakeWebAiService : IWebAiService
    {
        public FakeWebAiService(bool available) => Available = available;

        public bool Available { get; }

        public Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class =>
            Task.FromResult<T?>(null);

        public Task<string?> ChatOnceAsync(string systemPrompt, string userPrompt) =>
            Task.FromResult<string?>(null);
    }

    private (AiAnalysisHostedService Service, AiAnalysisRunState RunState, ScheduleOptionsStore OptionsStore, SchedulerRunState SchedulerState)
        CreateTestHarness(ScheduleOptions? initialOptions = null, bool aiAvailable = true,
            HostDayWorkflowService? workflow = null, bool backfillCompleted = true,
            IAnalysisRecordQuery? recordQuery = null)
    {
        var optionsStore = new ScheduleOptionsStore(_backend.Blob("schedule_options"));
        if (initialOptions != null)
        {
            optionsStore.Update(o =>
            {
                o.Enabled = initialOptions.Enabled;
                o.Windows = initialOptions.Windows;
                o.AiWindows = initialOptions.AiWindows;
                o.AiConcurrency = initialOptions.AiConcurrency;
            });
        }

        var schedulerState = new SchedulerRunState();
        var aiRunState = new AiAnalysisRunState();
        var recordStore = recordQuery ?? _backend.RecordStore();

        var service = new AiAnalysisHostedService(
            optionsStore,
            schedulerState,
            aiRunState,
            recordStore,
            _backend,
            _settingsStore,
            new DataVersionStamp(),
            BatchRuns(),
            backfillCompleted ? CompletedBackfiller() : new DailyRecordBackfiller(_contextFactory),
            new FakeWebAiService(aiAvailable),
            NullAiRequestGate.Instance,
            _suppressions,
            _ai,
            workflow: workflow);

        return (service, aiRunState, optionsStore, schedulerState);
    }

    private static DailyAnalysisRecord CreateRecord(long hostId, string hostName, DateTime date, bool pending = true, string headline = "待分析標題") => new()
    {
        HostId = hostId,
        Host = hostName,
        Date = date.Date,
        RiskLevel = "中",
        AiPending = pending,
        Headline = headline,
        TopIssues = new List<LogIssueSignature>
        {
            new()
            {
                LogName = "System",
                Source = $"disk-{hostName}",
                EventId = 153,
                Category = IssueCategory.Storage,
                Count = 1,
                Severity = IssueSeverity.Medium
            }
        }
    };

    [Fact]
    public async Task 情境1_完整性閘門_取數執行中跳過今日與昨日待補_取數結束後撿到()
    {
        var (service, _, _, schedulerState) = CreateTestHarness();
        var store = _backend.RecordStore();

        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        var threeDaysAgo = today.AddDays(-3);

        store.Append(CreateRecord(1, "HOST-A", today, pending: true));
        store.Append(CreateRecord(1, "HOST-A", yesterday, pending: true));
        store.Append(CreateRecord(1, "HOST-A", threeDaysAgo, pending: true));

        // 模擬取數排程執行中
        schedulerState.TryBeginRun("schedule", out _);
        Assert.True(schedulerState.IsRunning);

        // 執行 AI 處理迴圈
        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        // 斷言：3天前的紀錄通過閘門並完成；今天與昨天的紀錄被閘門跳過，維持 ai_pending = true
        var recToday = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, today);
        var recYesterday = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, yesterday);
        var recThreeDaysAgo = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, threeDaysAgo);

        Assert.True(recToday!.AiPending);
        Assert.True(recYesterday!.AiPending);
        Assert.False(recThreeDaysAgo!.AiPending);

        // 模擬取數排程結束
        schedulerState.EndRun(new RunOutcome(true, null, "schedule", DateTime.Now));
        Assert.False(schedulerState.IsRunning);

        // 下一輪再次執行 AI 分析
        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        // 斷言：取數結束後，今天與昨天的待補也被撿到並完成
        recToday = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, today);
        recYesterday = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, yesterday);

        Assert.False(recToday!.AiPending);
        Assert.False(recYesterday!.AiPending);
    }

    /// <summary>記錄每次 AI 呼叫處理的是哪一天（prompt 只有目標日以 yyyy-MM-dd 出現，近期歷史用 MM-dd）。</summary>
    private List<(DateTime Date, string Prompt)> RecordProcessedDates(params DateTime[] candidates)
    {
        var processed = new List<(DateTime Date, string Prompt)>();
        _ai.Behavior = (prompt, ct) =>
        {
            var date = candidates.FirstOrDefault(d => prompt.Contains(d.ToString("yyyy-MM-dd")));
            lock (processed) { processed.Add((date, prompt)); }
            return Task.FromResult(new AiResponse { Success = true, Content = _ai.NextContent });
        };
        return processed;
    }

    [Fact]
    public async Task 情境2_處理順序_積壓層同一塊內同主機舊到新_後一天的prompt看得到前一天的AI結論()
    {
        var (service, _, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();

        var d9 = DateTime.Today.AddDays(-9);
        var d6 = DateTime.Today.AddDays(-6);
        var d3 = DateTime.Today.AddDays(-3);

        // 刻意打亂順序寫入；三天都落在同一個七日塊（D-9～D-3）
        store.Append(CreateRecord(1, "HOST-A", d6, pending: true));
        store.Append(CreateRecord(1, "HOST-A", d3, pending: true));
        store.Append(CreateRecord(1, "HOST-A", d9, pending: true));

        var processed = RecordProcessedDates(d3, d6, d9);

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        Assert.Equal(new[] { d9, d6, d3 }, processed.Select(p => p.Date));
        // D-6 判讀時，D-9 的 AI 結論已寫回，近期歷史帶得到
        var d6Prompt = processed[1].Prompt;
        Assert.Contains($"- {d9:MM-dd}", d6Prompt);
        Assert.Contains("當日結論:AI分析完成摘要", d6Prompt);
    }

    [Fact]
    public async Task 處理順序_近日層新到舊先處理_積壓層塊由新到舊()
    {
        var (service, _, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();

        var d1 = DateTime.Today.AddDays(-1);
        var d2 = DateTime.Today.AddDays(-2);
        var d5 = DateTime.Today.AddDays(-5);
        var d20 = DateTime.Today.AddDays(-20);

        foreach (var d in new[] { d20, d2, d5, d1 })
            store.Append(CreateRecord(1, "HOST-A", d, pending: true));

        var processed = RecordProcessedDates(d1, d2, d5, d20);

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        // 近日層（今天−2 以內）新→舊；之後 D-5 所在的塊比 D-20 所在的塊新，先處理
        Assert.Equal(new[] { d1, d2, d5, d20 }, processed.Select(p => p.Date));
    }

    [Fact]
    public async Task 處理順序_同一塊內主機依最舊待補日先後_每台主機片內舊到新()
    {
        var (service, _, _, _) = CreateTestHarness(new ScheduleOptions { AiConcurrency = 1 });
        var store = _backend.RecordStore();

        var d3 = DateTime.Today.AddDays(-3);
        var d4 = DateTime.Today.AddDays(-4);
        var d6 = DateTime.Today.AddDays(-6);

        store.Append(CreateRecord(1, "HOST-A", d4, pending: true));
        store.Append(CreateRecord(2, "HOST-B", d3, pending: true));
        store.Append(CreateRecord(2, "HOST-B", d6, pending: true));

        var hosts = new List<string>();
        var processed = RecordProcessedDates(d3, d4, d6);
        var recordDates = _ai.Behavior;
        _ai.Behavior = (prompt, ct) =>
        {
            lock (hosts) { hosts.Add(prompt.Contains("HOST-B") ? "HOST-B" : "HOST-A"); }
            return recordDates!(prompt, ct);
        };

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        // HOST-B 的最舊待補（D-6）比 HOST-A（D-4）舊，HOST-B 整片先跑完
        Assert.Equal(new[] { d6, d3, d4 }, processed.Select(p => p.Date));
        Assert.Equal(new[] { "HOST-B", "HOST-B", "HOST-A" }, hosts);
    }

    [Fact]
    public void SelectNextBatch_近日層被完整性閘門擋住時_第一批直接取積壓層()
    {
        var (service, _, _, schedulerState) = CreateTestHarness();
        var store = _backend.RecordStore();

        var d5 = DateTime.Today.AddDays(-5);
        store.Append(CreateRecord(1, "HOST-A", DateTime.Today, pending: true));
        store.Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-1), pending: true));
        store.Append(CreateRecord(1, "HOST-A", d5, pending: true));

        schedulerState.TryBeginRun("schedule", out _);

        var batch = service.SelectNextBatch(new HashSet<(long, string, DateTime)>());

        Assert.NotNull(batch);
        Assert.True(batch.OldestFirst);
        Assert.Equal(new[] { d5 }, batch.Records.Select(r => r.Date.Date));
    }

    [Fact]
    public async Task 情境3_涵蓋保證_300天前超舊待補紀錄仍會被撿到()
    {
        var (service, _, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();

        var veryOldDate = DateTime.Today.AddDays(-300);
        store.Append(CreateRecord(1, "HOST-OLD", veryOldDate, pending: true));

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        var record = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-OLD" } }, veryOldDate);
        Assert.NotNull(record);
        Assert.False(record.AiPending);
        Assert.True(record.AiAnalyzed);
    }

    [Fact]
    public async Task 情境4_續跑_處理部分紀錄後停止_重新開始只處理殘餘且不重跑已完成()
    {
        var (service, _, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();

        var date1 = DateTime.Today.AddDays(-3);
        var date2 = DateTime.Today.AddDays(-4);
        var date3 = DateTime.Today.AddDays(-5);
        var date4 = DateTime.Today.AddDays(-6);

        store.Append(CreateRecord(1, "HOST-A", date1, pending: true));
        store.Append(CreateRecord(1, "HOST-A", date2, pending: true));
        store.Append(CreateRecord(1, "HOST-A", date3, pending: true));
        store.Append(CreateRecord(1, "HOST-A", date4, pending: true));

        using var cts = new CancellationTokenSource();
        int callCount = 0;
        _ai.Behavior = (prompt, ct) =>
        {
            callCount++;
            if (callCount == 3)
            {
                // 前兩筆已寫入；第三筆仍在呼叫內時停止，該晚到結果必須保留待補。
                cts.Cancel();
            }
            return Task.FromResult(new AiResponse { Success = true, Content = _ai.NextContent });
        };

        await service.ExecuteProcessingLoopAsync(cts.Token);
        Assert.Equal(3, callCount);

        // 四天同在一個積壓塊，處理順序舊→新（date4 → date3 → date2）：
        // 第 1 次執行只完成最舊的 2 筆，殘餘 2 筆仍為待補
        var r1 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date1);
        var r2 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date2);
        var r3 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date3);
        var r4 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date4);

        Assert.True(r1!.AiPending);
        Assert.True(r2!.AiPending);
        Assert.False(r3!.AiPending);
        Assert.False(r4!.AiPending);

        // 重新開始執行
        var secondRunDates = new List<DateTime>();
        _ai.Behavior = (prompt, ct) =>
        {
            foreach (var d in new[] { date1, date2, date3, date4 })
            {
                if (prompt.Contains(d.ToString("yyyy-MM-dd")))
                {
                    secondRunDates.Add(d);
                    break;
                }
            }
            return Task.FromResult(new AiResponse { Success = true, Content = _ai.NextContent });
        };

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        // 斷言：第 2 次執行只處理了殘餘的 2 筆（date2 與 date1），已完成的 date3 與 date4 絕不重跑
        Assert.Equal(new[] { date2, date1 }, secondRunDates);

        // 全數完成
        r1 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date1);
        r2 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date2);
        Assert.False(r1!.AiPending);
        Assert.False(r2!.AiPending);
    }

    [Fact]
    public async Task 情境5_併發_AiConcurrency大於1時不同主機平行且同主機序列()
    {
        var options = new ScheduleOptions { AiConcurrency = 2 };
        var (service, _, _, _) = CreateTestHarness(options);
        var store = _backend.RecordStore();

        var dateA = DateTime.Today.AddDays(-3);
        var dateB = DateTime.Today.AddDays(-4);

        store.Append(CreateRecord(1, "HOST-1", dateA, pending: true));
        store.Append(CreateRecord(1, "HOST-1", dateB, pending: true));
        store.Append(CreateRecord(2, "HOST-2", dateA, pending: true));
        store.Append(CreateRecord(2, "HOST-2", dateB, pending: true));

        var lockObj = new object();
        var callLogs = new List<(string Host, DateTime Date, DateTime Start, DateTime End)>();

        // 併發以「會合點」驗證而非計時：兩個不同主機的呼叫必須能同時停在這裡，
        // 第二個到達時才一起放行。若併發度實際上是 1，第一個會等到逾時而永遠等不到第二個，
        // 測試確定性失敗——不像量測 60ms 重疊那樣會因執行緒池忙碌而偶發紅燈。
        var arrived = new SemaphoreSlim(0);
        var bothArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivedCount = 0;

        _ai.Behavior = async (prompt, ct) =>
        {
            var host = prompt.Contains("HOST-1") ? "HOST-1" : "HOST-2";
            var date = prompt.Contains(dateA.ToString("yyyy-MM-dd")) ? dateA : dateB;
            var start = DateTime.UtcNow;

            if (Interlocked.Increment(ref arrivedCount) >= 2) bothArrived.TrySetResult(true);

            // 兩個主機各自的第一筆會在此會合；之後的筆數直接通過（同主機序列的驗證靠時間區間）
            await Task.WhenAny(bothArrived.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
            await Task.Delay(20, ct);

            var end = DateTime.UtcNow;
            lock (lockObj)
            {
                callLogs.Add((host, date, start, end));
            }

            return new AiResponse { Success = true, Content = _ai.NextContent };
        };

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        Assert.Equal(4, callLogs.Count);

        var host1Logs = callLogs.Where(l => l.Host == "HOST-1").OrderBy(l => l.Start).ToList();
        var host2Logs = callLogs.Where(l => l.Host == "HOST-2").OrderBy(l => l.Start).ToList();

        // 斷言 1：同一台主機內部必須是嚴格序列（上一筆結束才開始下一筆）
        Assert.True(host1Logs[0].End <= host1Logs[1].Start.AddMilliseconds(50),
            $"HOST-1 必須序列：第一筆結束 {host1Logs[0].End:O}，第二筆開始 {host1Logs[1].Start:O}");
        Assert.True(host2Logs[0].End <= host2Logs[1].Start.AddMilliseconds(50),
            $"HOST-2 必須序列：第一筆結束 {host2Logs[0].End:O}，第二筆開始 {host2Logs[1].Start:O}");

        // 斷言 2：不同主機之間確實平行——兩個主機的呼叫同時抵達會合點才可能成立。
        // 併發度若退回 1，bothArrived 永遠不會完成，兩筆呼叫各等 5 秒逾時，
        // 且執行區間不會重疊，下面兩個斷言都會失敗。
        Assert.True(bothArrived.Task.IsCompletedSuccessfully, "AiConcurrency=2 時兩個不同主機的 AI 呼叫必須能同時進行（會合點未達成）");

        bool hasOverlap = host1Logs.Any(h1 => host2Logs.Any(h2 => h1.Start < h2.End && h2.Start < h1.End));
        Assert.True(hasOverlap, "AiConcurrency=2 時不同主機之間應有重疊的執行區間");
    }

    /// <summary>
    /// 補跑輸入必須是完整紀錄（體檢輪回歸）：QueryPendingAi 是輕量投影——CorrelationAlerts
    /// 是 "(lightweight)" 佔位字串、TrendAlerts 沒填。若迴圈直接拿投影餵 RetryAiAsync，
    /// prompt 會出現一條假的高嚴重度「(lightweight)」關聯、真實關聯訊號整批消失。
    /// 這條測試釘住「處理前先 GetOne 完整載入」的契約。
    /// </summary>
    [Fact]
    public async Task 補跑輸入為完整紀錄_prompt含真實關聯訊號且不含輕量佔位字串()
    {
        var (service, _, _, _) = CreateTestHarness(new ScheduleOptions());
        var store = _backend.RecordStore();

        var record = CreateRecord(1, "HOST-CORR", DateTime.Today.AddDays(-3), pending: true);
        record.CorrelationAlerts = new List<string> { "同時段異常登入與服務安裝的關聯訊號" };
        store.Append(record);

        var prompts = new List<string>();
        var defaultBehavior = _ai.Behavior;
        _ai.Behavior = (prompt, ct) =>
        {
            lock (prompts) { prompts.Add(prompt); }
            return Task.FromResult(new AiResponse { Success = true, Content = _ai.NextContent });
        };

        try
        {
            await service.ExecuteProcessingLoopAsync(CancellationToken.None);
        }
        finally
        {
            _ai.Behavior = defaultBehavior;
        }

        Assert.NotEmpty(prompts);
        var all = string.Join(Environment.NewLine, prompts);
        Assert.Contains("同時段異常登入與服務安裝的關聯訊號", all);
        Assert.DoesNotContain("(lightweight)", all);
    }

    [Fact]
    public async Task 情境6_失敗不中斷_單筆AI失敗維持待補其餘紀錄仍完成()
    {
        var (service, _, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();

        var date = DateTime.Today.AddDays(-3);
        store.Append(CreateRecord(1, "HOST-OK-1", date, pending: true));
        store.Append(CreateRecord(2, "HOST-FAIL", date, pending: true));
        store.Append(CreateRecord(3, "HOST-OK-2", date, pending: true));

        _ai.Behavior = (prompt, ct) =>
        {
            if (prompt.Contains("HOST-FAIL"))
            {
                // 模擬 AI 呼叫拋出異常
                throw new HttpRequestException("Simulated AI connection timeout");
            }
            return Task.FromResult(new AiResponse { Success = true, Content = _ai.NextContent });
        };

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        var r1 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-OK-1" } }, date);
        var r2 = store.GetOne(new[] { new HostKey { HostId = 2, HostName = "HOST-FAIL" } }, date);
        var r3 = store.GetOne(new[] { new HostKey { HostId = 3, HostName = "HOST-OK-2" } }, date);

        // 斷言：失敗的維持待補，其餘成功的順利完成，整批沒有被中斷
        Assert.False(r1!.AiPending);
        Assert.True(r2!.AiPending);
        Assert.False(r3!.AiPending);
    }

    [Fact]
    public async Task 低風險待補不呼叫AI_直接清除待補且不計失敗也不觸發全滅防線()
    {
        var (service, _, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();

        var date = DateTime.Today.AddDays(-3);
        var low1 = CreateRecord(1, "HOST-LOW-1", date, pending: true);
        low1.RiskLevel = "低";
        var low2 = CreateRecord(2, "HOST-LOW-2", date, pending: true);
        low2.RiskLevel = "低";
        store.Append(low1);
        store.Append(low2);
        store.Append(CreateRecord(3, "HOST-MID", date, pending: true)); // 中風險，AI 替身成功

        var counters = new AiAnalysisHostedService.LoopCounters();
        await service.ExecuteProcessingLoopAsync(CancellationToken.None, counters);

        var r1 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-LOW-1" } }, date);
        var r2 = store.GetOne(new[] { new HostKey { HostId = 2, HostName = "HOST-LOW-2" } }, date);
        var r3 = store.GetOne(new[] { new HostKey { HostId = 3, HostName = "HOST-MID" } }, date);

        // 三筆都不再待補；低風險兩筆沒有 AI 分析，中風險那筆有
        Assert.False(r1!.AiPending);
        Assert.False(r2!.AiPending);
        Assert.False(r3!.AiPending);
        Assert.False(r1.AiAnalyzed);
        Assert.False(r2.AiAnalyzed);
        Assert.True(r3.AiAnalyzed);
        Assert.Equal("今日狀況正常，無需處理", r1.Headline);
        Assert.Equal("低", r1.RiskLevel);
        Assert.Equal(0, counters.Failed);
        Assert.Equal(3, counters.Done);
        Assert.Equal(2, counters.Skipped);
        Assert.Equal(0, store.CountPendingAi());
    }

    [Fact]
    public async Task 全部都是低風險待補時一批清空且不被全滅防線誤判()
    {
        var (service, _, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();

        var date = DateTime.Today.AddDays(-3);
        for (var i = 1; i <= 3; i++)
        {
            var r = CreateRecord(i, $"HOST-ALLLOW-{i}", date, pending: true);
            r.RiskLevel = "低";
            store.Append(r);
        }

        var counters = new AiAnalysisHostedService.LoopCounters();
        await service.ExecuteProcessingLoopAsync(CancellationToken.None, counters);

        Assert.Equal(0, store.CountPendingAi());
        Assert.Equal(0, _ai.Calls);
        Assert.Equal(3, counters.Skipped);
        Assert.Equal(0, counters.Failed);
    }

    [Fact]
    public async Task 情境7_強制重新分析_重標後全部重跑且舊AI文字保留顯示直到覆蓋()
    {
        var (service, _, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();

        var date1 = DateTime.Today.AddDays(-3);
        var date2 = DateTime.Today.AddDays(-4);

        // 先寫入已完成 AI 分析的紀錄（ai_pending = false，舊有文字）
        store.Append(CreateRecord(1, "HOST-A", date1, pending: false, headline: "舊AI分析標題1"));
        store.Append(CreateRecord(1, "HOST-A", date2, pending: false, headline: "舊AI分析標題2"));

        // 未勾選強制重新分析時，已完成者不受影響
        int initialCalls = _ai.Calls;
        await service.TriggerRunAsync(forceRerun: false, trigger: "manual:test");
        // 等待執行結束
        await Task.Delay(100);
        Assert.Equal(initialCalls, _ai.Calls);

        // 驗證重標動作：SQL 整批 UPDATE
        service.BatchResetAiPending();

        // 斷言：舊 AI 文字在被覆蓋前仍可讀（不清空既有 AI 文字）
        var r1Pre = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date1);
        var r2Pre = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date2);
        Assert.True(r1Pre!.AiPending);
        Assert.True(r2Pre!.AiPending);
        Assert.Equal("舊AI分析標題1", r1Pre.Headline);
        Assert.Equal("舊AI分析標題2", r2Pre.Headline);

        // 重新開始處理
        _ai.NextContent = """{"risk_level":"高","headline":"全新AI分析標題","story":"全新故事","trend_story":"","action":""}""";
        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        // 斷言：兩筆全部重跑完成，且標題被覆蓋為新結果
        var r1Post = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date1);
        var r2Post = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date2);
        Assert.False(r1Post!.AiPending);
        Assert.False(r2Post!.AiPending);
        Assert.Equal("全新AI分析標題", r1Post.Headline);
        Assert.Equal("全新AI分析標題", r2Post.Headline);
    }

    [Fact]
    public void 強制重新分析_低風險且已有AI白話的列不被重標且內容不變_非低風險照標()
    {
        var store = _backend.RecordStore();
        var lowDate = DateTime.Today.AddDays(-5);
        var midDate = DateTime.Today.AddDays(-6);

        var low = CreateRecord(1, "HOST-A", lowDate, pending: false, headline: "低風險的AI白話");
        low.RiskLevel = "低";
        low.AiAnalyzed = true;
        low.Summary = "既有的 AI 摘要";
        store.Append(low);
        store.Append(CreateRecord(1, "HOST-A", midDate, pending: false, headline: "中風險標題"));

        var affected = ((IAnalysisRecordQuery)store).MarkAllForAiRerun();

        var key = new[] { new HostKey { HostId = 1, HostName = "HOST-A" } };
        var lowAfter = store.GetOne(key, lowDate)!;
        var midAfter = store.GetOne(key, midDate)!;
        Assert.Equal(1, affected);
        Assert.False(lowAfter.AiPending);
        Assert.True(lowAfter.AiAnalyzed);
        Assert.Equal("低風險的AI白話", lowAfter.Headline);
        Assert.Equal("既有的 AI 摘要", lowAfter.Summary);
        Assert.True(midAfter.AiPending);
    }

    /// <summary>
    /// 只讓 <see cref="IAnalysisRecordQuery.MarkAllForAiRerun"/> 擲例外，其餘方法原樣轉交真實 store。
    /// 用 DispatchProxy 代替為整個介面手寫委派。
    /// </summary>
    public class FailingRerunRecordQuery : DispatchProxy
    {
        internal IAnalysisRecordQuery Target { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IAnalysisRecordQuery.MarkAllForAiRerun))
                throw new InvalidOperationException("模擬整批重標的 DB 失敗");
            try
            {
                return targetMethod.Invoke(Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }

    [Fact]
    public async Task 強制重標擲例外_執行閘門釋放且可再次開始()
    {
        var failing = DispatchProxy.Create<IAnalysisRecordQuery, FailingRerunRecordQuery>();
        ((FailingRerunRecordQuery)failing).Target = _backend.RecordStore();
        var (service, runState, _, _) = CreateTestHarness(recordQuery: failing);

        await Assert.ThrowsAnyAsync<Exception>(() => service.TriggerRunAsync(forceRerun: true, trigger: "manual:test"));

        // 閘門必須釋放：否則之後每次輪詢都回「已有其他執行」，直到站台重啟
        Assert.False(runState.IsRunning);
        Assert.True(runState.TryBeginRun("manual:test", 0, out var cts));
        cts.Dispose();
        runState.EndRun(true);
    }

    [Fact]
    public async Task 情境8_窗口_窗口外不自動觸發且窗口結束時自動觸發收到停止未完成維持待補()
    {
        var now = DateTime.Now;
        // 設定一個未涵蓋當前時間的窗口
        var windowStart = now.AddHours(2).ToString("HH:mm");
        var windowEnd = now.AddHours(3).ToString("HH:mm");

        var options = new ScheduleOptions
        {
            AiWindows = new List<ScheduleWindow> { new ScheduleWindow { Start = windowStart, End = windowEnd } }
        };

        var (service, runState, optionsStore, _) = CreateTestHarness(options);
        var store = _backend.RecordStore();
        store.Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        // 8a. 窗口外不自動觸發
        await service.TickAsync();
        Assert.False(runState.IsRunning);

        // 8b. 模擬自動排程執行中，窗口到點發出停止
        runState.TryBeginRun("schedule", 10, out _);
        Assert.True(runState.IsRunning);

        // 窗口目前不在範圍內，TickAsync 應發出優雅停止
        await service.TickAsync();
        Assert.True(runState.Snapshot().CanStop);

        // 8c. 手動觸發不受窗口限制
        var manualRunState = new AiAnalysisRunState();
        var manualService = new AiAnalysisHostedService(
            optionsStore, new SchedulerRunState(), manualRunState, store, _backend, _settingsStore, new DataVersionStamp(), BatchRuns(), CompletedBackfiller(), new FakeWebAiService(true), NullAiRequestGate.Instance, _suppressions, _ai);

        manualRunState.TryBeginRun("manual:admin", 10, out _);
        await manualService.TickAsync();
        // 手動觸發不該被窗口 End 取消
        Assert.True(manualRunState.IsRunning);
    }

    [Fact]
    public void ScheduleOptions_Ai設定儲存與驗證()
    {
        // 驗證合法視窗
        var validWindows = new List<ScheduleWindow>
        {
            new() { Start = "00:00", End = "23:59" }
        };
        var res = ScheduleCalculator.Validate(validWindows);
        Assert.True(res.IsValid);

        // 驗證超過上限
        var tooMany = new List<ScheduleWindow>
        {
            new() { Start = "00:00", End = "01:00" },
            new() { Start = "02:00", End = "03:00" },
            new() { Start = "04:00", End = "05:00" },
            new() { Start = "06:00", End = "07:00" },
            new() { Start = "08:00", End = "09:00" }
        };
        var resTooMany = ScheduleCalculator.Validate(tooMany);
        Assert.False(resTooMany.IsValid);

        // 驗證重疊視窗
        var overlap = new List<ScheduleWindow>
        {
            new() { Start = "01:00", End = "04:00" },
            new() { Start = "03:00", End = "06:00" }
        };
        var resOverlap = ScheduleCalculator.Validate(overlap);
        Assert.False(resOverlap.IsValid);
    }

    [Fact]
    public async Task ScheduleController_Ai端點_狀態查詢_立即執行與停止()
    {
        var (service, runState, optionsStore, _) = CreateTestHarness();
        var store = _backend.RecordStore();
        // 同一積壓塊內舊→新：D-4 先完成，D-3 卡在呼叫內被停止
        var completedDate = DateTime.Today.AddDays(-4);
        var cancelledDate = DateTime.Today.AddDays(-3);
        store.Append(CreateRecord(1, "HOST-A", completedDate, pending: true));
        store.Append(CreateRecord(1, "HOST-A", cancelledDate, pending: true));

        // 先完成一筆，再把下一筆卡在 AI 呼叫內。停止後 provider 故意忽略 token 並回成功，
        // 用來證明停止邊界會保留已完成結果、拒絕晚到結果，且把整趟記為已停止。
        var secondCallStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _ai.Behavior = async (prompt, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                secondCallStarted.TrySetResult();
                await release.Task;
            }
            return new AiResponse { Success = true, Content = _ai.NextContent };
        };

        var audit = new RecordingAuditService();
        var controller = new ScheduleController(
            optionsStore,
            scheduler: null!,
            new SchedulerRunState(),
            hosts: new FakeHostStore(),
            sentinels: new FakeSentinelStore(),
            audit: audit,
            currentUser: new FakeCurrentUser(),
            users: new FakeUserStore(),
            records: store,
            settingsStore: _settingsStore,
            userDisplayNames: new UserDisplayNameService(_settingsStore),
            aiScheduler: service,
            aiRunState: runState);

        // 查詢狀態
        var statusResp = controller.GetAiStatus();
        Assert.True(statusResp.Success);
        Assert.NotNull(statusResp.Data);
        Assert.Null(statusResp.Data.ProgressPhase);   // 閒置沒有進度可畫
        Assert.Equal("件", statusResp.Data.UnitText);
        Assert.Equal(2, statusResp.Data.PendingTotal);
        Assert.False(statusResp.Data.IsRunning);

        // 觸發執行
        var runResp = await controller.RunAi(new TriggerAiRunRequest { ForceRerun = false });
        Assert.True(runResp.Success);
        Assert.True(runResp.Data!.Started);
        await secondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 停止執行
        var cancelResp = controller.CancelAi();
        Assert.True(cancelResp.Success);

        // 放行 provider；停止後不能寫入這筆晚到的成功結果。
        release.SetResult();
        Assert.False(await runState.WaitForCompletionAsync(TimeSpan.FromSeconds(30)));

        var completed = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, completedDate);
        var cancelled = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, cancelledDate);
        Assert.False(completed!.AiPending);
        Assert.True(cancelled!.AiPending);

        var outcome = runState.Snapshot().LastOutcome;
        Assert.NotNull(outcome);
        Assert.False(outcome!.Success);
        Assert.Equal("執行已停止（優雅停止）。", outcome.Message);

        var aiRun = BatchRuns().GetRecentRuns(7, null).First(r => r.JobType == BatchRun.JobTypeAi);
        Assert.Equal(1, aiRun.ExitCode);
        Assert.True(aiRun.Stopped);
        Assert.Equal(1, aiRun.DaysAnalyzed);
        Assert.Equal(1, aiRun.AiCalls);
        Assert.Equal(0, aiRun.AiFailures);
    }

    [Fact]
    public async Task Ai排程遇到503_待補保留但不誤記為使用者停止()
    {
        var (service, runState, _, _) = CreateTestHarness();
        var date = DateTime.Today.AddDays(-3);
        var store = _backend.RecordStore();
        store.Append(CreateRecord(1, "HOST-503", date, pending: true));
        _ai.Behavior = (_, _) => throw new HttpRequestException("503 Service Unavailable");

        Assert.True(await service.TriggerRunAsync(forceRerun: false, trigger: "manual:test"));
        Assert.True(await runState.WaitForCompletionAsync(TimeSpan.FromSeconds(10)));

        var pending = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-503" } }, date);
        Assert.True(pending!.AiPending);

        var outcome = runState.Snapshot().LastOutcome;
        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);
        Assert.Null(outcome.Message);

        var aiRun = BatchRuns().GetRecentRuns(7, null).First(r => r.JobType == BatchRun.JobTypeAi);
        Assert.Equal(0, aiRun.ExitCode);
        Assert.False(aiRun.Stopped);
        Assert.Equal(0, aiRun.DaysAnalyzed);
        Assert.Equal(1, aiRun.AiFailures);
    }

    [Fact]
    public async Task 優雅停止會收尾目前版本Workflow但保留待補且不計AI失敗()
    {
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        var (service, runState, _, _) = CreateTestHarness(workflow: workflow);
        var date = DateTime.Today.AddDays(-3);
        var store = _backend.RecordStore();
        store.Append(CreateRecord(1, "HOST-CANCEL-WORKFLOW", date, pending: true));

        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ai.Behavior = async (_, _) =>
        {
            providerStarted.TrySetResult();
            await releaseProvider.Task;
            return new AiResponse { Success = true, Content = _ai.NextContent };
        };

        Assert.True(await service.TriggerRunAsync(forceRerun: false, trigger: "manual:test"));
        await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowLegState.Running, workflow.Get(1, date)!.Ai);

        Assert.True(runState.TryCancel());
        releaseProvider.SetResult();
        Assert.False(await runState.WaitForCompletionAsync(TimeSpan.FromSeconds(30)));

        Assert.True(store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-CANCEL-WORKFLOW" } }, date)!.AiPending);
        var workflowState = workflow.Get(1, date)!;
        Assert.Equal(WorkflowLegState.Failed, workflowState.Ai);
        Assert.Equal("cancelled", workflowState.AiFailure);
        var aiRun = BatchRuns().GetRecentRuns(7, null).First(r => r.JobType == BatchRun.JobTypeAi);
        Assert.True(aiRun.Stopped);
        Assert.Equal(0, aiRun.AiFailures);
    }

    [Fact]
    public async Task Provider例外會收尾目前版本Workflow並保留待補()
    {
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        var (service, runState, _, _) = CreateTestHarness(workflow: workflow);
        var date = DateTime.Today.AddDays(-3);
        var store = _backend.RecordStore();
        store.Append(CreateRecord(1, "HOST-PROVIDER-ERROR", date, pending: true));
        _ai.Behavior = (_, _) => throw new HttpRequestException("provider connection reset");

        Assert.True(await service.TriggerRunAsync(forceRerun: false, trigger: "manual:test"));
        Assert.True(await runState.WaitForCompletionAsync(TimeSpan.FromSeconds(10)));

        Assert.True(store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-PROVIDER-ERROR" } }, date)!.AiPending);
        var workflowState = workflow.Get(1, date)!;
        Assert.Equal(WorkflowLegState.Failed, workflowState.Ai);
        Assert.Equal("provider connection reset", workflowState.AiFailure);
        var aiRun = BatchRuns().GetRecentRuns(7, null).First(r => r.JobType == BatchRun.JobTypeAi);
        Assert.Equal(1, aiRun.AiFailures);
    }

    [Fact]
    public async Task Ai排程執行_產生的Warn會進入自己的BatchRun執行紀錄_且JobType為ai()
    {
        var (service, runState, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();
        store.Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        // 模擬 AI 呼叫未成功（回傳 Success = false），促使 ExecuteProcessingLoopAsync 記錄 Log.Warn
        _ai.Behavior = (_, _) => Task.FromResult(new AiResponse { Success = false, Error = "AI 模擬逾時" });

        var started = await service.TriggerRunAsync(forceRerun: false, trigger: "schedule");
        Assert.True(started);
        await runState.WaitForCompletionAsync(TimeSpan.FromSeconds(10));

        var batchRuns = BatchRuns();
        var runs = batchRuns.GetRecentRuns(7, null);
        var aiRun = runs.FirstOrDefault(r => r.JobType == BatchRun.JobTypeAi);

        Assert.NotNull(aiRun);
        Assert.Equal(BatchRun.JobTypeAi, aiRun!.JobType);
        Assert.False(string.IsNullOrEmpty(aiRun.AppVersion));
        Assert.True(aiRun.WarnCount >= 1);

        var logs = batchRuns.GetLogs(aiRun.RunId);
        Assert.NotEmpty(logs);
        Assert.Contains(logs, l => l.Level == "Warn");
    }

    [Fact]
    public async Task Ai排程執行_改用Recorder後JobType仍為ai_且正常計數()
    {
        var (service, runState, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();
        store.Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        // 預設 _ai 成功完成
        var started = await service.TriggerRunAsync(forceRerun: false, trigger: "schedule");
        Assert.True(started);
        await runState.WaitForCompletionAsync(TimeSpan.FromSeconds(10));

        var batchRuns = BatchRuns();
        var runs = batchRuns.GetRecentRuns(7, null);
        var aiRun = runs.FirstOrDefault(r => r.JobType == BatchRun.JobTypeAi);

        Assert.NotNull(aiRun);
        Assert.Equal(BatchRun.JobTypeAi, aiRun!.JobType);
        Assert.Equal(0, aiRun.ExitCode);
        Assert.Equal(1, aiRun.DaysAnalyzed);
        Assert.Equal(1, aiRun.AiCalls);
        Assert.Equal(0, aiRun.AiFailures);
        Assert.False(string.IsNullOrEmpty(aiRun.AppVersion));
    }
    /// <summary>
    /// 批次A：AI 排程每輪有多個前置條件，任一不成立就整輪不跑，而畫面只顯示「閒置 + N 件待補」。
    /// 每個提前返回都要寫下閒置原因，否則使用者無從分辨是設定沒開、不在窗口、還是真的沒事做。
    /// </summary>
    [Fact]
    public async Task TickAsync_AI未設定時記錄閒置原因()
    {
        var (service, runState, _, _) = CreateTestHarness(aiAvailable: false);
        _backend.RecordStore().Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        await service.TickAsync();

        Assert.False(runState.IsRunning);
        Assert.Equal(AiIdleReasons.Disabled, runState.Snapshot().IdleReason);
    }

    [Fact]
    public async Task TickAsync_不在執行窗口時記錄閒置原因()
    {
        // 設定一個未涵蓋當前時間的窗口（同情境8的既有做法）
        var now = DateTime.Now;
        var options = new ScheduleOptions
        {
            AiWindows = new List<ScheduleWindow>
            {
                new ScheduleWindow { Start = now.AddHours(2).ToString("HH:mm"), End = now.AddHours(3).ToString("HH:mm") }
            }
        };

        var (service, runState, _, _) = CreateTestHarness(options);
        _backend.RecordStore().Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        await service.TickAsync();

        Assert.False(runState.IsRunning);
        Assert.Equal(AiIdleReasons.OutsideWindow, runState.Snapshot().IdleReason);
    }

    [Fact]
    public async Task TickAsync_無待補時記錄閒置原因()
    {
        var options = new ScheduleOptions
        {
            AiWindows = new List<ScheduleWindow> { new ScheduleWindow { Start = "00:00", End = "00:00" } }
        };

        var (service, runState, _, _) = CreateTestHarness(options);

        await service.TickAsync();

        Assert.False(runState.IsRunning);
        Assert.Equal(AiIdleReasons.NoPending, runState.Snapshot().IdleReason);
    }

    /// <summary>全天窗口：起訖相同在 IsWithinWindow 恆為窗口內（設定驗證不允許，僅測試直接寫入）。不用 00:00～23:59：23:59 到午夜執行會落在窗口外。</summary>
    private static ScheduleOptions AllDay() => new()
    {
        AiWindows = new List<ScheduleWindow> { new ScheduleWindow { Start = "00:00", End = "00:00" } }
    };

    /// <summary>
    /// 第 57 輪批次C-2：閒置原因在狀態 API 讀取時即時判定，與輪詢共用同一份條件
    /// （AI 未設定 → 資料校正中 → 不在窗口 → 沒有待補），其他都成立時再看是否在等取數。
    /// </summary>
    [Fact]
    public void CurrentIdleReason_依序判定且執行中為null()
    {
        var pendingOne = () => 1;
        Assert.Equal(AiIdleReasons.Disabled, CreateTestHarness(AllDay(), aiAvailable: false).Service.CurrentIdleReason(pendingOne));
        Assert.Equal(AiIdleReasons.BackfillPending, CreateTestHarness(AllDay(), backfillCompleted: false).Service.CurrentIdleReason(pendingOne));

        var now = DateTime.Now;
        var outside = new ScheduleOptions
        {
            AiWindows = new List<ScheduleWindow>
            {
                new ScheduleWindow { Start = now.AddHours(2).ToString("HH:mm"), End = now.AddHours(3).ToString("HH:mm") }
            }
        };
        Assert.Equal(AiIdleReasons.OutsideWindow, CreateTestHarness(outside).Service.CurrentIdleReason(pendingOne));

        var (service, runState, _, schedulerState) = CreateTestHarness(AllDay());
        Assert.Equal(AiIdleReasons.NoPending, service.CurrentIdleReason(() => 0));
        Assert.Null(service.CurrentIdleReason(pendingOne));

        // 取數執行中、PRTG finding 未到齊：不擋開跑，但畫面要說得出最近的待補在等什麼
        schedulerState.TryBeginRun("schedule", out _);
        Assert.Equal(AiIdleReasons.WaitingFetch, service.CurrentIdleReason(pendingOne));

        Assert.True(runState.TryBeginRun("manual:admin", 1, out _));
        Assert.Null(service.CurrentIdleReason(pendingOne));
    }

    /// <summary>上一輪輪詢記下的原因（例如資料校正中）不得在條件改變後繼續顯示。</summary>
    [Fact]
    public void GetAiStatus_閒置原因即時判定_不用輪詢殘值()
    {
        var (service, runState, optionsStore, _) = CreateTestHarness(AllDay());
        runState.SetIdleReason(AiIdleReasons.BackfillPending);
        var controller = new ScheduleController(
            optionsStore, scheduler: null!, new SchedulerRunState(), hosts: new FakeHostStore(),
            sentinels: new FakeSentinelStore(), audit: new RecordingAuditService(), currentUser: new FakeCurrentUser(),
            users: new FakeUserStore(), records: _backend.RecordStore(), settingsStore: _settingsStore,
            userDisplayNames: new UserDisplayNameService(_settingsStore), aiScheduler: service, aiRunState: runState);

        var status = controller.GetAiStatus().Data!;

        Assert.Equal(AiIdleReasons.NoPending, status.IdleReason);
        Assert.Equal(0, status.PendingTotal);
    }

    /// <summary>執行中不得殘留閒置原因——畫面會同時顯示「執行中」與一句「為什麼沒在跑」。</summary>
    [Fact]
    public async Task TickAsync_開始執行後清空閒置原因()
    {
        var (service, runState, _, _) = CreateTestHarness(aiAvailable: false);
        await service.TickAsync();
        Assert.Equal(AiIdleReasons.Disabled, runState.Snapshot().IdleReason);

        Assert.True(runState.TryBeginRun("manual:admin", 1, out _));
        Assert.Null(runState.Snapshot().IdleReason);
    }
    /// <summary>
    /// 批次B：取數執行中，只要當日 PRTG finding 已算完（prtg-findings-ready 已送），
    /// 今天與昨天的待補就立刻合格——AI 因此能與取數並行，不必等整趟取數結束。
    /// 這是本輪要解決的「NetIQ 抓了很多但 AI 完全沒動」。
    /// </summary>
    [Fact]
    public async Task 完整性閘門_取數執行中但PRTG_finding已就緒時當日待補即可處理()
    {
        var (service, _, _, schedulerState) = CreateTestHarness();
        var store = _backend.RecordStore();

        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        store.Append(CreateRecord(1, "HOST-A", today, pending: true));
        store.Append(CreateRecord(1, "HOST-A", yesterday, pending: true));

        schedulerState.TryBeginRun("schedule", out _);
        schedulerState.ReportProgress(AnalysisOrchestrator.PrtgFindingsReadyPhase, 0, 0);
        Assert.True(schedulerState.IsRunning);
        Assert.True(schedulerState.PrtgFindingsReady);

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        var recToday = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, today);
        var recYesterday = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, yesterday);

        Assert.False(recToday!.AiPending);
        Assert.False(recYesterday!.AiPending);
    }

    /// <summary>批次B：有待補卻被閘門擋住時最像壞掉，畫面要說得出原因。</summary>
    [Fact]
    public async Task TickAsync_取數執行中且PRTG未就緒時閒置原因為等待取數()
    {
        var options = new ScheduleOptions
        {
            AiWindows = new List<ScheduleWindow> { new ScheduleWindow { Start = "00:00", End = "00:00" } }
        };

        var (service, runState, _, schedulerState) = CreateTestHarness(options);
        _backend.RecordStore().Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-1), pending: true));

        schedulerState.TryBeginRun("schedule", out _);

        await service.TickAsync();
        await runState.WaitForCompletionAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AiIdleReasons.WaitingFetch, runState.Snapshot().IdleReason);
    }
    /// <summary>
    /// 批次G4：AI 進度在併發分片下不得互相蓋。
    /// 原本寫成 `ReportProgress(done, _runState.ProgressTotal, …)`——那是 read-modify-write：
    /// 兩條分片同時讀到舊分母再寫回，分母會被拉回舊值。
    /// 這裡不用壓力測試（那種測試只在剛好切在錯的那一刻才紅），直接驗語意：
    /// 只推進分子的方法不得動到分母，且分子只增不減。
    /// </summary>
    [Fact]
    public void ReportProgressDone_不動分母且分子只增不減()
    {
        var state = new AiAnalysisRunState();
        Assert.True(state.TryBeginRun("manual:tester", total: 100, out _));

        state.ReportProgressDone(10, "第 10 筆");
        Assert.Equal(10, state.Snapshot().ProgressDone);
        Assert.Equal(100, state.Snapshot().ProgressTotal);

        // 分片完成順序不定：晚到的舊值不得把進度往回拉
        state.ReportProgressDone(4, "晚到的舊值");
        Assert.Equal(10, state.Snapshot().ProgressDone);
        Assert.Equal(100, state.Snapshot().ProgressTotal);

        state.ReportProgressDone(25);
        Assert.Equal(25, state.Snapshot().ProgressDone);
        Assert.Equal(100, state.Snapshot().ProgressTotal);
    }

    [Fact]
    public void ReportProgressDone_未執行時不寫入()
    {
        var state = new AiAnalysisRunState();
        state.ReportProgressDone(5, "不該生效");

        Assert.Equal(0, state.Snapshot().ProgressDone);
    }

    /// <summary>
    /// 取數執行一發佈當日 PRTG finding，AI 就要立刻開跑（回饋第 40 輪批次E）——
    /// 不等自己下一輪 60 秒輪詢。使用者按下「立即執行」後看到取數在跑、AI 卻閒著，
    /// 正是這一輪要解決的事。
    /// </summary>
    [Fact]
    public async Task Finding就緒時立刻觸發AI而不等輪詢()
    {
        var (service, runState, _, schedulerState) = CreateTestHarness();
        _backend.RecordStore().Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        // 只有一筆待補、假 AI 立刻回應：執行可能在兩次輪詢之間開始又結束，斷言就看不到「執行中」。
        // 讓 AI 回應卡在閘門上，執行一定停在「執行中」直到斷言完成。
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ai.Behavior = async (_, _) =>
        {
            await gate.Task;
            return new AiResponse { Success = true, Content = _ai.NextContent };
        };

        Assert.True(schedulerState.TryBeginRun("manual:admin", out _));
        Assert.False(runState.IsRunning);

        // 這一步是取數路徑送出的訊號，**沒有**呼叫 TickAsync
        schedulerState.ReportProgress(RunPhases.PrtgFindingsReady, 0, 0);

        try
        {
            await WaitUntilAsync(() => runState.IsRunning, TimeSpan.FromSeconds(10));
            Assert.True(runState.IsRunning);
            Assert.Equal("fetch-followup", runState.Snapshot().Trigger);
        }
        finally
        {
            // 放行後等執行真的結束再離開：測試 Dispose 釋放 backend 時背景分析還在跑會偶發紅
            gate.TrySetResult(true);
            await WaitUntilAsync(() => !runState.IsRunning, TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// 同一趟執行內 finding 只會就緒一次，重複回報不得再觸發（登錄簿的補發保底會送第二次）。
    /// </summary>
    [Fact]
    public async Task Finding重複回報不重複觸發()
    {
        var (service, runState, _, schedulerState) = CreateTestHarness();
        _backend.RecordStore().Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        Assert.True(schedulerState.TryBeginRun("manual:admin", out _));

        var fired = 0;
        schedulerState.PrtgFindingsBecameReady += () => Interlocked.Increment(ref fired);

        schedulerState.ReportProgress(RunPhases.PrtgFindingsReady, 0, 0);
        schedulerState.ReportProgress(RunPhases.PrtgFindingsReady, 0, 0);
        schedulerState.ReportProgress(RunPhases.PrtgFindingsReady, 0, 0);

        Assert.Equal(1, fired);
    }

    /// <summary>AI 未設定時 finding 就緒不觸發，並寫下閒置原因。</summary>
    [Fact]
    public async Task AI未設定時Finding就緒不觸發()
    {
        var (service, runState, _, schedulerState) = CreateTestHarness(aiAvailable: false);
        _backend.RecordStore().Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        Assert.True(schedulerState.TryBeginRun("manual:admin", out _));
        schedulerState.ReportProgress(RunPhases.PrtgFindingsReady, 0, 0);

        // 等回呼真的跑完並寫下閒置原因，再斷言沒開跑——固定 sleep 在慢機器上會假綠
        await WaitUntilAsync(() => runState.Snapshot().IdleReason == AiIdleReasons.Disabled, TimeSpan.FromSeconds(5));
        Assert.Equal(AiIdleReasons.Disabled, runState.Snapshot().IdleReason);
        Assert.False(runState.IsRunning);
    }

    /// <summary>沒有待補時 finding 就緒不觸發——沒事可做就不該佔住 gate。</summary>
    [Fact]
    public async Task 無待補時Finding就緒不觸發()
    {
        var (service, runState, _, schedulerState) = CreateTestHarness();

        Assert.True(schedulerState.TryBeginRun("manual:admin", out _));
        schedulerState.ReportProgress(RunPhases.PrtgFindingsReady, 0, 0);

        await WaitUntilAsync(() => runState.Snapshot().IdleReason == AiIdleReasons.NoPending, TimeSpan.FromSeconds(5));
        Assert.Equal(AiIdleReasons.NoPending, runState.Snapshot().IdleReason);
        Assert.False(runState.IsRunning);
    }

    /// <summary>輪詢用的等待：避免用固定 sleep 讓測試在慢機器上不穩定。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task 取數執行中送出PRTG日期範圍_範圍內的舊日待補不判讀()
    {
        var (service, _, _, schedulerState) = CreateTestHarness();
        var store = _backend.RecordStore();

        var today = DateTime.Today;
        var threeDaysAgo = today.AddDays(-3);
        var fiveDaysAgo = today.AddDays(-5);
        var sixDaysAgo = today.AddDays(-6);

        store.Append(CreateRecord(1, "HOST-A", threeDaysAgo, pending: true));
        store.Append(CreateRecord(1, "HOST-A", fiveDaysAgo, pending: true));
        store.Append(CreateRecord(1, "HOST-A", sixDaysAgo, pending: true));

        // 模擬取數排程執行中，並送出 PRTG 5 天範圍
        schedulerState.TryBeginRun("schedule", out _);
        schedulerState.ReportProgress(RunPhases.PrtgDateRange, 5, 0);

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        var recThree = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, threeDaysAgo);
        var recFive = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, fiveDaysAgo);
        var recSix = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, sixDaysAgo);

        // 3天前與5天前在範圍內，不判讀（維持 pending）
        Assert.True(recThree!.AiPending);
        Assert.True(recFive!.AiPending);
        // 6天前在範圍外，合格判讀（完成）
        Assert.False(recSix!.AiPending);
    }

    [Fact]
    public async Task 日期範圍訊號後finding就緒_範圍內待補全部放行()
    {
        var (service, _, _, schedulerState) = CreateTestHarness();
        var store = _backend.RecordStore();

        var today = DateTime.Today;
        var threeDaysAgo = today.AddDays(-3);
        var fiveDaysAgo = today.AddDays(-5);

        store.Append(CreateRecord(1, "HOST-A", threeDaysAgo, pending: true));
        store.Append(CreateRecord(1, "HOST-A", fiveDaysAgo, pending: true));

        // 模擬取數排程執行中，先送日期範圍
        schedulerState.TryBeginRun("schedule", out _);
        schedulerState.ReportProgress(RunPhases.PrtgDateRange, 5, 0);

        // 後續 finding 就緒
        schedulerState.ReportProgress(RunPhases.PrtgFindingsReady, 0, 0);

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        var recThree = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, threeDaysAgo);
        var recFive = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, fiveDaysAgo);

        // finding 已就緒，全部合格放行
        Assert.False(recThree!.AiPending);
        Assert.False(recFive!.AiPending);
    }

    [Fact]
    public async Task 未送日期範圍_維持只擋昨天之後()
    {
        var (service, _, _, schedulerState) = CreateTestHarness();
        var store = _backend.RecordStore();

        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        var threeDaysAgo = today.AddDays(-3);

        store.Append(CreateRecord(1, "HOST-A", today, pending: true));
        store.Append(CreateRecord(1, "HOST-A", yesterday, pending: true));
        store.Append(CreateRecord(1, "HOST-A", threeDaysAgo, pending: true));

        // 模擬取數排程執行中，但未送 PrtgDateRange
        schedulerState.TryBeginRun("schedule", out _);

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        var recToday = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, today);
        var recYesterday = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, yesterday);
        var recThree = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, threeDaysAgo);

        // 只擋昨天之後（今天與昨天被擋，3天前通過）
        Assert.True(recToday!.AiPending);
        Assert.True(recYesterday!.AiPending);
        Assert.False(recThree!.AiPending);
    }

    [Fact]
    public async Task 跟隨取數_取數執行中且Finding就緒後呼叫TickAsync開跑()
    {
        var now = DateTime.Now;
        var options = new ScheduleOptions
        {
            AiWindows = new List<ScheduleWindow>
            {
                new ScheduleWindow { Start = now.AddHours(2).ToString("HH:mm"), End = now.AddHours(3).ToString("HH:mm") }
            }
        };

        var (service, runState, _, schedulerState) = CreateTestHarness(options);

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ai.Behavior = async (_, _) =>
        {
            await gate.Task;
            return new AiResponse { Success = true, Content = _ai.NextContent };
        };

        schedulerState.TryBeginRun("schedule", out _);
        // 先送 finding 就緒（此時無待補，OnFetchFollowUp 不會搶跑）
        schedulerState.ReportProgress(RunPhases.PrtgFindingsReady, 0, 0);

        // NetIQ 落地待補紀錄
        _backend.RecordStore().Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        // 呼叫 TickAsync
        await service.TickAsync();

        try
        {
            Assert.True(runState.IsRunning);
            Assert.Equal("fetch-followup", runState.Snapshot().Trigger);
        }
        finally
        {
            gate.TrySetResult(true);
            await WaitUntilAsync(() => !runState.IsRunning, TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task 跟隨取數_取數執行中但Finding未就緒呼叫TickAsync不開跑()
    {
        var now = DateTime.Now;
        var options = new ScheduleOptions
        {
            AiWindows = new List<ScheduleWindow>
            {
                new ScheduleWindow { Start = now.AddHours(2).ToString("HH:mm"), End = now.AddHours(3).ToString("HH:mm") }
            }
        };

        var (service, runState, _, schedulerState) = CreateTestHarness(options);
        _backend.RecordStore().Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        // 取數執行中但未發送 finding 就緒
        schedulerState.TryBeginRun("schedule", out _);

        await service.TickAsync();

        Assert.False(runState.IsRunning);
        Assert.Equal(AiIdleReasons.OutsideWindow, runState.Snapshot().IdleReason);
    }

    [Fact]
    public async Task 跟隨取數_取數結束後立即呼叫TickAsync開跑()
    {
        var now = DateTime.Now;
        var options = new ScheduleOptions
        {
            AiWindows = new List<ScheduleWindow>
            {
                new ScheduleWindow { Start = now.AddHours(2).ToString("HH:mm"), End = now.AddHours(3).ToString("HH:mm") }
            }
        };

        var (service, runState, _, schedulerState) = CreateTestHarness(options);

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ai.Behavior = async (_, _) =>
        {
            await gate.Task;
            return new AiResponse { Success = true, Content = _ai.NextContent };
        };

        schedulerState.TryBeginRun("schedule", out _);
        // 取數結束（此時無待補，RunEnded 事件觸發的 OnFetchFollowUp 不會搶跑）
        schedulerState.EndRun(new RunOutcome(true, null, "schedule", DateTime.Now));

        // NetIQ 落地待補紀錄
        _backend.RecordStore().Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

        // 立即呼叫 TickAsync
        await service.TickAsync();

        try
        {
            Assert.True(runState.IsRunning);
            Assert.Equal("fetch-followup", runState.Snapshot().Trigger);
        }
        finally
        {
            gate.TrySetResult(true);
            await WaitUntilAsync(() => !runState.IsRunning, TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// D4 體檢：輪詢改讀狀態 API 的 30 秒待補快取後，取數期間留下的「0 筆」不得擋住取數後的接續開跑。
    /// </summary>
    [Fact]
    public async Task 跟隨取數_待補快取停在取數前的0筆_取數結束後輪詢仍實查並開跑()
    {
        var now = DateTime.Now;
        var options = new ScheduleOptions
        {
            AiWindows = new List<ScheduleWindow>
            {
                new ScheduleWindow { Start = now.AddHours(2).ToString("HH:mm"), End = now.AddHours(3).ToString("HH:mm") }
            }
        };

        var (service, runState, _, schedulerState) = CreateTestHarness(options);

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ai.Behavior = async (_, _) =>
        {
            await gate.Task;
            return new AiResponse { Success = true, Content = _ai.NextContent };
        };

        // 取數期間狀態頁輪詢把待補件數快取成 0
        Assert.Equal(0, runState.GetPendingAiCount(() => 0));

        schedulerState.TryBeginRun("schedule", out _);
        _backend.RecordStore().Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));
        schedulerState.EndRun(new RunOutcome(true, null, "schedule", DateTime.Now));

        await service.TickAsync();

        try
        {
            await WaitUntilAsync(() => runState.IsRunning, TimeSpan.FromSeconds(5));
            Assert.True(runState.IsRunning);
        }
        finally
        {
            gate.TrySetResult(true);
            await WaitUntilAsync(() => !runState.IsRunning, TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void 跟隨取數_超過寬限期IsNotFollowingFetch為False()
    {
        var state = new SchedulerRunState();
        state.TryBeginRun("schedule", out _);
        state.EndRun(new RunOutcome(true, null, "schedule", DateTime.Now));

        Assert.NotNull(state.LastRunEndedAt);
        var checkTime = state.LastRunEndedAt.Value.AddMinutes(11);
        Assert.False(state.IsFollowingFetch(checkTime));
        Assert.True(state.IsFollowingFetch(state.LastRunEndedAt.Value));
        Assert.True(state.IsFollowingFetch(state.LastRunEndedAt.Value.AddMinutes(5)));
    }

    /// <summary>
    /// 體檢輪：閒置原因只有一份判定（docs/WEB-SPEC.md §9.10）。跟隨取數的即時觸發曾自己抄一份 disabled／
    /// backfill／no-pending 判斷，之後改其中一份另一份不會跟著變——守住它只能經 BlockingIdleReason。
    /// </summary>
    [Fact]
    public void 跟隨取數_即時觸發的前置判定走BlockingIdleReason_不另抄一份()
    {
        var path = Path.Combine(FindRepoRoot(), "LogForesight.Web", "Services", "AiAnalysisHostedService.cs");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("private void OnFetchFollowUp()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("internal async Task TickAsync", start, StringComparison.Ordinal);
        var body = source[start..end];

        Assert.Contains("BlockingIdleReason(", body);
        Assert.Contains("requireWindow: false", body);
        Assert.DoesNotContain("AiIdleReasons.", body);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 跟隨取數_EndRun空結果不觸發RunEnded_有結果觸發一次()
    {
        var state = new SchedulerRunState();
        int count = 0;
        state.RunEnded += () => Interlocked.Increment(ref count);

        // 未真正開始的結束
        state.EndRun(null);
        Assert.Equal(0, count);
        Assert.Null(state.LastRunEndedAt);

        // 正常結束
        state.TryBeginRun("schedule", out _);
        state.EndRun(new RunOutcome(true, null, "schedule", DateTime.Now));
        Assert.Equal(1, count);
        Assert.NotNull(state.LastRunEndedAt);
    }

    /// <summary>只計 <see cref="IAnalysisRecordQuery.CountPendingAi"/> 呼叫次數，其餘原樣轉交真實 store</summary>
    public class CountingPendingRecordQuery : DispatchProxy
    {
        internal IAnalysisRecordQuery Target { get; set; } = null!;
        internal int CountPendingCalls;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IAnalysisRecordQuery.CountPendingAi))
                Interlocked.Increment(ref CountPendingCalls);
            try
            {
                return targetMethod.Invoke(Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }

    [Fact]
    public async Task 處理兩批全成功_CountPendingAi呼叫次數不超過改動前的一半()
    {
        var counting = DispatchProxy.Create<IAnalysisRecordQuery, CountingPendingRecordQuery>();
        var proxy = (CountingPendingRecordQuery)counting;
        proxy.Target = _backend.RecordStore();
        var (service, _, _, _) = CreateTestHarness(recordQuery: counting);
        var store = _backend.RecordStore();

        // 批次大小 50：60 台主機同一天 → 第一批 50 筆、第二批 10 筆
        var date = DateTime.Today.AddDays(-3);
        for (var i = 1; i <= 60; i++)
            store.Append(CreateRecord(i, $"HOST-B{i:D2}", date, pending: true));

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        Assert.Equal(0, store.CountPendingAi());
        // 改動前：每批 2 次（批前進度＋批後全滅防線）×2 批＋結尾 1 次＝5 次；改動後上限為一半（2 次：開頭實查＋結尾確認）
        Assert.True(proxy.CountPendingCalls <= 2, $"CountPendingAi 呼叫 {proxy.CountPendingCalls} 次，應 ≤ 2（改動前為 5）");
    }

    [Fact]
    public async Task AiCalls只計實際呼叫過AI的筆數_成功加失敗_Skipped不計()
    {
        var (service, runState, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();
        var date = DateTime.Today.AddDays(-3);
        store.Append(CreateRecord(1, "HOST-OK", date, pending: true));
        store.Append(CreateRecord(2, "HOST-FAILX", date, pending: true));
        var low = CreateRecord(3, "HOST-LOWX", date, pending: true);
        low.RiskLevel = "低";
        store.Append(low);
        // 兩筆低風險 Skipped（多於失敗數，才分得出「Done」與「Done - Skipped + Failed」）
        var low2 = CreateRecord(4, "HOST-LOWY", date, pending: true);
        low2.RiskLevel = "低";
        store.Append(low2);

        var okContent = _ai.NextContent;
        _ai.Behavior = (prompt, ct) => Task.FromResult(prompt.Contains("HOST-FAILX")
            ? new AiResponse { Success = false, Error = "模擬失敗" }
            : new AiResponse { Success = true, Content = okContent });

        Assert.True(await service.TriggerRunAsync(forceRerun: false, trigger: "schedule"));
        await runState.WaitForCompletionAsync(TimeSpan.FromSeconds(10));

        var aiRun = BatchRuns().GetRecentRuns(7, null).First(r => r.JobType == BatchRun.JobTypeAi);
        Assert.Equal(2, aiRun.AiCalls);
        Assert.Equal(1, aiRun.AiFailures);
    }

    private sealed class ResolveCountingService : AiAnalysisHostedService
    {
        private readonly IAiService _ai;
        public int ResolveCalls;
        public int FactoryCalls;

        public ResolveCountingService(ScheduleOptionsStore o, SchedulerRunState sr, AiAnalysisRunState rs,
            IAnalysisRecordQuery q, StorageBackend b, ISystemSettingsStore ss, BatchRunStore runs,
            DailyRecordBackfiller bf, IWebAiService web, IAiService ai)
            : base(o, sr, rs, q, b, ss, new DataVersionStamp(), runs, bf, web, NullAiRequestGate.Instance, aiService: null)
        {
            _ai = ai;
        }

        internal override Func<IAiService> ResolveAiServiceFactory()
        {
            Interlocked.Increment(ref ResolveCalls);
            return () => { Interlocked.Increment(ref FactoryCalls); return _ai; };
        }
    }

    [Fact]
    public async Task 同一輪兩個主機分片_設定只解析一次但每個分片各建一個AI服務()
    {
        var service = new ResolveCountingService(
            new ScheduleOptionsStore(_backend.Blob("schedule_options")), new SchedulerRunState(), new AiAnalysisRunState(),
            _backend.RecordStore(), _backend, _settingsStore, BatchRuns(), CompletedBackfiller(),
            new FakeWebAiService(true), _ai);
        var store = _backend.RecordStore();
        var date = DateTime.Today.AddDays(-3);
        store.Append(CreateRecord(1, "HOST-S1", date, pending: true));
        store.Append(CreateRecord(2, "HOST-S2", date, pending: true));

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        Assert.Equal(0, store.CountPendingAi());
        Assert.Equal(2, _ai.Calls);
        Assert.Equal(1, service.ResolveCalls);
        // 分片各自一個實例：共用一個會讓 AIService 的逐實例佇列把全輪串成單線（體檢輪）
        Assert.Equal(2, service.FactoryCalls);
    }
}
