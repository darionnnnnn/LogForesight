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

    private (AiAnalysisHostedService Service, AiAnalysisRunState RunState, ScheduleOptionsStore OptionsStore, SchedulerRunState SchedulerState)
        CreateTestHarness(ScheduleOptions? initialOptions = null)
    {
        var optionsStore = new ScheduleOptionsStore(_backend.Blob("schedule_options"));
        if (initialOptions != null)
        {
            optionsStore.Update(o =>
            {
                o.Enabled = initialOptions.Enabled;
                o.Windows = initialOptions.Windows;
                o.AiEnabled = initialOptions.AiEnabled;
                o.AiWindows = initialOptions.AiWindows;
                o.AiConcurrency = initialOptions.AiConcurrency;
            });
        }

        var schedulerState = new SchedulerRunState();
        var aiRunState = new AiAnalysisRunState();
        var recordStore = _backend.RecordStore();

        var service = new AiAnalysisHostedService(
            optionsStore,
            schedulerState,
            aiRunState,
            recordStore,
            _backend,
            _settingsStore,
            new DataVersionStamp(),
            _suppressions,
            _ai);

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

    [Fact]
    public async Task 情境2_處理順序_通過閘門者依日期新到舊依序處理()
    {
        var (service, _, _, _) = CreateTestHarness();
        var store = _backend.RecordStore();

        var date10DaysAgo = DateTime.Today.AddDays(-10);
        var date3DaysAgo = DateTime.Today.AddDays(-3);
        var date7DaysAgo = DateTime.Today.AddDays(-7);

        // 刻意打亂順序寫入
        store.Append(CreateRecord(1, "HOST-A", date10DaysAgo, pending: true));
        store.Append(CreateRecord(1, "HOST-A", date3DaysAgo, pending: true));
        store.Append(CreateRecord(1, "HOST-A", date7DaysAgo, pending: true));

        var processedDates = new List<DateTime>();
        _ai.Behavior = (prompt, ct) =>
        {
            // 從 prompt 或標籤推斷處理的日期
            foreach (var d in new[] { date3DaysAgo, date7DaysAgo, date10DaysAgo })
            {
                if (prompt.Contains(d.ToString("yyyy-MM-dd")))
                {
                    processedDates.Add(d);
                    break;
                }
            }
            return Task.FromResult(new AiResponse { Success = true, Content = _ai.NextContent });
        };

        await service.ExecuteProcessingLoopAsync(CancellationToken.None);

        // 斷言：處理順序嚴格依日期新→舊（3天前 → 7天前 → 10天前）
        Assert.Equal(3, processedDates.Count);
        Assert.Equal(date3DaysAgo, processedDates[0]);
        Assert.Equal(date7DaysAgo, processedDates[1]);
        Assert.Equal(date10DaysAgo, processedDates[2]);
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
            if (callCount == 2)
            {
                // 處理完 2 筆後發出停止
                cts.Cancel();
            }
            return Task.FromResult(new AiResponse { Success = true, Content = _ai.NextContent });
        };

        await service.ExecuteProcessingLoopAsync(cts.Token);

        // 斷言第 1 次執行只完成了 2 筆，殘餘 2 筆仍為待補
        var r1 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date1);
        var r2 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date2);
        var r3 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date3);
        var r4 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date4);

        Assert.False(r1!.AiPending);
        Assert.False(r2!.AiPending);
        Assert.True(r3!.AiPending);
        Assert.True(r4!.AiPending);

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

        // 斷言：第 2 次執行只處理了殘餘的 2 筆（date3 與 date4），已完成的 date1 與 date2 絕不重跑
        Assert.Equal(2, secondRunDates.Count);
        Assert.Contains(date3, secondRunDates);
        Assert.Contains(date4, secondRunDates);
        Assert.DoesNotContain(date1, secondRunDates);
        Assert.DoesNotContain(date2, secondRunDates);

        // 全數完成
        r3 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date3);
        r4 = store.GetOne(new[] { new HostKey { HostId = 1, HostName = "HOST-A" } }, date4);
        Assert.False(r3!.AiPending);
        Assert.False(r4!.AiPending);
    }

    [Fact]
    public async Task 情境5_併發_AiConcurrency大於1時不同主機平行且同主機序列()
    {
        var options = new ScheduleOptions { AiConcurrency = 2, AiEnabled = true };
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
    public async Task 情境8_窗口_窗口外不自動觸發且窗口結束時自動觸發收到停止未完成維持待補()
    {
        var now = DateTime.Now;
        // 設定一個未涵蓋當前時間的窗口
        var windowStart = now.AddHours(2).ToString("HH:mm");
        var windowEnd = now.AddHours(3).ToString("HH:mm");

        var options = new ScheduleOptions
        {
            AiEnabled = true,
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
            optionsStore, new SchedulerRunState(), manualRunState, store, _backend, _settingsStore, new DataVersionStamp(), _suppressions, _ai);

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
        store.Append(CreateRecord(1, "HOST-A", DateTime.Today.AddDays(-3), pending: true));

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
        Assert.Equal("netiq-ai", statusResp.Data.ProgressPhase);
        Assert.Equal("件", statusResp.Data.UnitText);
        Assert.Equal(1, statusResp.Data.PendingTotal);
        Assert.False(statusResp.Data.IsRunning);

        // 觸發執行
        var runResp = await controller.RunAi(new TriggerAiRunRequest { ForceRerun = false });
        Assert.True(runResp.Success);
        Assert.True(runResp.Data!.Started);

        // 停止執行
        var cancelResp = controller.CancelAi();
        Assert.True(cancelResp.Success);
    }
}
