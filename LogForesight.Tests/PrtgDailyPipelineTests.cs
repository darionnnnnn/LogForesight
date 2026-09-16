using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 每日路徑（docs/PRTG-SPEC.md §3）的**就緒保底**：
/// 不論走哪條路離開（PRTG 未啟用、初始化失敗、取消），都必須發佈 finding 登錄簿並送
/// `prtg-findings-ready`。少了這道，AI 分析排程會一路等到整趟取數結束——等於這個機制沒做，
/// 而症狀只是「AI 晚了幾小時」，不會有任何錯誤訊息。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class PrtgDailyPipelineTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;

    public PrtgDailyPipelineTests()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
    }

    public void Dispose()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class CollectingConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private sealed class CollectingProgress : IRunProgress
    {
        public List<string> Phases { get; } = new();
        public List<(string Phase, int Done, int Total)> Reports { get; } = new();
        public void Report(string phase, int done, int total)
        {
            Phases.Add(phase);
            Reports.Add((phase, done, total));
        }
    }

    private (AnalysisRunContext Ctx, CollectingConsole Console, CollectingProgress Progress, PrtgFindingsRegistry Registry)
        CreateContext(CancellationToken ct = default)
    {
        var console = new CollectingConsole();
        var progress = new CollectingProgress();
        var registry = new PrtgFindingsRegistry();

        var caseCoordinator = new IssueCaseCoordinator(
            _backend.IssueCaseStore(),
            _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(),
            _backend.RecordStore(),
            new HostStore(_backend.Blob("hosts")),
            new IssueOwnerStore(_backend.Blob("issue_owners")));

        var recorder = new BatchRunRecorder(
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
            "test-host", Array.Empty<string>());

        var ctx = new AnalysisRunContext(
            new RunRequest(), new AppSettings(), new RetentionOptions(), console, ct,
            new EventLogService(), caseCoordinator, _backend.RiskyEventStore(), recorder,
            new OrchestratorResult(), UseAi: false, progress, registry);

        return (ctx, console, progress, registry);
    }

    /// <summary>
    /// PRTG 未啟用時整條路徑短路，但**仍要宣告就緒**——
    /// 「算不出東西」與「還沒算完」必須分得出來，否則 AI 會空等一整晚。
    /// </summary>
    [Fact]
    public async Task PRTG未啟用時仍宣告就緒並送出訊號()
    {
        // 預設設定即為 PrtgEnabled = false
        var (ctx, console, progress, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, guard: null);

        Assert.True(registry.IsReady);
        Assert.Contains(RunPhases.PrtgFindingsReady, progress.Phases);
        Assert.Contains(RunPhases.PrtgDone, progress.Phases);
        Assert.Contains(console.Lines, l => l.Contains("PRTG 未啟用"));
    }

    /// <summary>
    /// 設定了啟用但連線位址無效（初始化就失敗）時同樣要就緒——
    /// 失敗隔離的另一面：PRTG 壞掉不能讓 AI 跟著卡住。
    /// </summary>
    [Fact]
    public async Task 初始化失敗時仍宣告就緒()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            // 非 http/https 的 scheme 讓 PrtgClient 在建構時就擲例外——這才是「初始化失敗」，
            // 走的是最外層 catch → finally 的保底；連不上的位址只會讓階段 1 失敗、流程照常往下走。
            s.PrtgUrl = "ftp://prtg.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, _, progress, registry) = CreateContext();
        var days = new[] { DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-2) };

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")), days, Task.CompletedTask, guard: null);

        Assert.True(registry.IsReady);
        Assert.Contains(RunPhases.PrtgFindingsReady, progress.Phases);
        Assert.Contains(RunPhases.PrtgDone, progress.Phases);

        // 逐日迴圈之前就失敗：每一天都要記成 failed，總表才不會把這幾天當成沒有逐日統計的舊紀錄去猜
        ctx.RunRecorder.Finish(0);
        var run = new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs"))
            .GetRun(ctx.RunRecorder.RunId);
        Assert.NotNull(run!.PrtgDays);
        Assert.Equal(days.Select(d => d.Date), run.PrtgDays!.Select(s => s.Date));
        Assert.All(run.PrtgDays, s => Assert.Equal(BatchRun.PrtgOutcomeFailed, s.Outcome));
    }

    /// <summary>
    /// 就緒訊號**必須排在 `prtg-done` 之前**：AI 排程據此決定「當日待補現在可不可以判讀」，
    /// 排在收尾之後等於白做——那時整條路徑已經結束了。
    /// </summary>
    [Fact]
    public async Task 就緒訊號排在收尾訊號之前()
    {
        var (ctx, _, progress, _) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, guard: null);

        var readyAt = progress.Phases.IndexOf(RunPhases.PrtgFindingsReady);
        var doneAt = progress.Phases.IndexOf(RunPhases.PrtgDone);

        Assert.True(readyAt >= 0 && doneAt >= 0);
        Assert.True(readyAt < doneAt, "prtg-findings-ready 必須早於 prtg-done");
    }

    /// <summary>可控的同步閘門：回報執行中，被等待後轉為閒置並記錄呼叫次數。</summary>
    private sealed class FakeStructureSyncGate : IPrtgStructureSyncGate
    {
        private bool _running;

        public FakeStructureSyncGate(bool running) => _running = running;

        public int WaitCalls { get; private set; }

        public bool IsRunning => _running;

        /// <summary>false＝模擬等到上限仍未結束（對方卡住）。</summary>
        public bool ResultToReturn { get; set; } = true;

        public Task<bool> WaitUntilIdleAsync(CancellationToken ct)
        {
            WaitCalls++;
            _running = false;
            return Task.FromResult(ResultToReturn);
        }
    }

    /// <summary>
    /// 等到上限對方仍未結束時，本趟**不得**跳過結構同步——鏡像不是新的。
    /// 少了這條，PRTG 卡住的那一晚鏡像沒更新，畫面與執行紀錄卻都顯示正常。
    /// </summary>
    [Fact]
    public async Task 等待手動同步逾時則本趟自行同步結構()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, console, _, _) = CreateContext();
        var gate = new FakeStructureSyncGate(running: true) { ResultToReturn = false };

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, guard: null, structureSyncGate: gate);

        Assert.Equal(1, gate.WaitCalls);
        Assert.Contains(console.Lines, l => l.Contains("手動同步未成功結束"));
        Assert.DoesNotContain(console.Lines, l => l.Contains("沿用剛更新的鏡像結構"));
        // 真的去爬結構了（第一階段的開場訊息在 HTTP 呼叫之前就印）
        Assert.Contains(console.Lines, l => l.Contains("開始同步 PRTG 裝置結構鏡像"));
    }

    /// <summary>
    /// 手動同步進行中時，PRTG 日路徑要先等它，並在執行輸出說明原因、送出等待 phase
    /// （docs/PRTG-SPEC.md §5a）。畫面上看不到原因的話，那條軌會像是卡死。
    /// </summary>
    [Fact]
    public async Task 手動同步進行中時先等待並送出等待訊號()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, console, progress, _) = CreateContext();
        var gate = new FakeStructureSyncGate(running: true);

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, guard: null, structureSyncGate: gate);

        Assert.Equal(1, gate.WaitCalls);
        Assert.Contains(RunPhases.PrtgWaitSync, progress.Phases);
        Assert.Contains(console.Lines, l => l.Contains("等它完成後再繼續"));
        Assert.Contains(console.Lines, l => l.Contains("沿用剛更新的鏡像結構"));

        // 等完之後**真的沒有重新爬結構**：結構同步的第一階段會印「開始同步 PRTG 裝置結構鏡像」，
        // 跳過時走的是讀鏡像那條路，完全不進那三個階段。
        Assert.DoesNotContain(console.Lines, l => l.Contains("開始同步 PRTG 裝置結構鏡像"));
        // 但對應照做（對昨天）——跳過的只有結構同步這一步
        Assert.Contains(console.Lines, l => l.Contains("對應完成"));
    }

    /// <summary>閘門閒置（或根本沒接上）時行為與沒有這個機制時完全相同。</summary>
    [Fact]
    public async Task 手動同步未執行時不等待也不送等待訊號()
    {
        var (ctx, console, progress, _) = CreateContext();
        var gate = new FakeStructureSyncGate(running: false);

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, guard: null, structureSyncGate: gate);

        Assert.Equal(0, gate.WaitCalls);
        Assert.DoesNotContain(RunPhases.PrtgWaitSync, progress.Phases);
        Assert.DoesNotContain(console.Lines, l => l.Contains("等它完成後再繼續"));
    }

    /// <summary>
    /// 對照組：沒有手動同步時本趟照常爬結構，因此連不上的位址會印出擷取失敗。
    /// 少了這一條，上面那條的「沒有失敗訊息」可能只是因為整段根本沒跑到。
    /// </summary>
    [Fact]
    public async Task 沒有手動同步時仍會嘗試結構同步()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, console, _, _) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, guard: null, structureSyncGate: null);

        Assert.Contains(console.Lines, l => l.Contains("開始同步 PRTG 裝置結構鏡像"));
    }

    /// <summary>
    /// 保守策略下：不執行觸發式取數、印出保守說明與策略狀態，其餘階段（finding、done）照跑。
    /// </summary>
    [Fact]
    public async Task 保守策略_不執行觸發式取數_印出策略狀態與略過說明()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, console, progress, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, guard: null);

        Assert.True(registry.IsReady);
        // 印出策略狀態行
        Assert.Contains(console.Lines, l => l.Contains("PRTG 取數策略：保守（快照間隔 15 分鐘）。"));
        // 印出保守策略略過說明
        Assert.Contains(console.Lines, l => l.Contains("取數策略為保守，夜間不逐顆查詢歷史值，數值由快照供應。"));
        // 不應包含觸發式取數階段
        Assert.DoesNotContain(RunPhases.PrtgTriggered, progress.Phases);
        // 其餘階段照常完成
        Assert.Contains(RunPhases.PrtgFindingsReady, progress.Phases);
        Assert.Contains(RunPhases.PrtgDone, progress.Phases);
    }

    /// <summary>
    /// 激進策略下：進入觸發式取數階段、印出激進策略狀態。
    /// </summary>
    [Fact]
    public async Task 激進策略_進入觸發式取數_印出激進策略狀態()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });

        var (ctx, console, progress, _) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, guard: null);

        // 印出激進策略狀態行
        Assert.Contains(console.Lines, l => l.Contains("PRTG 取數策略：激進（快照間隔 5 分鐘）。"));
        // 激進策略會進入觸發式取數階段
        Assert.Contains(RunPhases.PrtgTriggered, progress.Phases);
        // 不含保守策略跳過訊息
        Assert.DoesNotContain(console.Lines, l => l.Contains("取數策略為保守"));
    }

    [Fact]
    public void 夜間同步狀態_全部成功_回傳夜間來源的完整狀態()
    {
        var fetchResult = new PrtgFetchResult(12, 34, 0, 0, 0);
        var mapResult = new PrtgHostMapResult(
            Ok: 5,
            Conflict: 1,
            Unmatched: 2,
            SkippedNoIp: 3,
            Manual: 4,
            SkippedExcluded: 6,
            SkippedManualSibling: 7);
        var day = new DateTime(2026, 9, 15);
        var elapsed = TimeSpan.FromSeconds(42.5);
        var now = new DateTime(2026, 9, 15, 3, 30, 0);

        var status = PrtgDailyPipeline.BuildNightlySyncStatus(
            structureSyncSkipped: false,
            fetchResult: fetchResult,
            fetchThrew: false,
            mapResult: mapResult,
            day: day,
            elapsed: elapsed,
            now: now);

        Assert.NotNull(status);
        Assert.Equal("nightly", status.Source);
        Assert.True(status.Success);
        Assert.Null(status.ErrorMessage);
        Assert.Equal(day, status.MapDate);
        Assert.Equal(now, status.CompletedAt);
        Assert.Equal(42.5, status.ElapsedSeconds);
        Assert.Equal(12, status.Devices);
        Assert.Equal(34, status.Sensors);
        Assert.Equal(5, status.MapOk);
        Assert.Equal(4, status.MapManual);
        Assert.Equal(1, status.MapConflict);
        Assert.Equal(2, status.MapUnmatched);
        Assert.Equal(3, status.MapSkippedNoIp);
        Assert.Equal(6, status.MapSkippedExcluded);
        Assert.Equal(7, status.MapSkippedManualSibling);
    }

    [Fact]
    public void 夜間同步狀態_有失敗階段_回傳null()
    {
        var fetchResult = new PrtgFetchResult(12, 34, 0, 0, 1);
        var mapResult = new PrtgHostMapResult(5, 1, 2, 3, 4, 6, 7);
        var day = new DateTime(2026, 9, 15);
        var elapsed = TimeSpan.FromSeconds(42.5);
        var now = new DateTime(2026, 9, 15, 3, 30, 0);

        var status = PrtgDailyPipeline.BuildNightlySyncStatus(
            structureSyncSkipped: false,
            fetchResult: fetchResult,
            fetchThrew: false,
            mapResult: mapResult,
            day: day,
            elapsed: elapsed,
            now: now);

        Assert.Null(status);
    }

    [Fact]
    public void 夜間同步狀態_擷取擲例外_回傳null()
    {
        var fetchResult = new PrtgFetchResult(12, 34, 0, 0, 0);
        var mapResult = new PrtgHostMapResult(5, 1, 2, 3, 4, 6, 7);
        var day = new DateTime(2026, 9, 15);
        var elapsed = TimeSpan.FromSeconds(42.5);
        var now = new DateTime(2026, 9, 15, 3, 30, 0);

        var status = PrtgDailyPipeline.BuildNightlySyncStatus(
            structureSyncSkipped: false,
            fetchResult: fetchResult,
            fetchThrew: true,
            mapResult: mapResult,
            day: day,
            elapsed: elapsed,
            now: now);

        Assert.Null(status);
    }

    [Fact]
    public void 夜間同步狀態_跳過結構同步_回傳null()
    {
        var fetchResult = new PrtgFetchResult(12, 34, 0, 0, 0);
        var mapResult = new PrtgHostMapResult(5, 1, 2, 3, 4, 6, 7);
        var day = new DateTime(2026, 9, 15);
        var elapsed = TimeSpan.FromSeconds(42.5);
        var now = new DateTime(2026, 9, 15, 3, 30, 0);

        var status = PrtgDailyPipeline.BuildNightlySyncStatus(
            structureSyncSkipped: true,
            fetchResult: fetchResult,
            fetchThrew: false,
            mapResult: mapResult,
            day: day,
            elapsed: elapsed,
            now: now);

        Assert.Null(status);
    }

    [Fact]
    public void 夜間同步狀態_對應失敗_回傳null()
    {
        var fetchResult = new PrtgFetchResult(12, 34, 0, 0, 0);
        var day = new DateTime(2026, 9, 15);
        var elapsed = TimeSpan.FromSeconds(42.5);
        var now = new DateTime(2026, 9, 15, 3, 30, 0);

        var status = PrtgDailyPipeline.BuildNightlySyncStatus(
            structureSyncSkipped: false,
            fetchResult: fetchResult,
            fetchThrew: false,
            mapResult: null,
            day: day,
            elapsed: elapsed,
            now: now);

        Assert.Null(status);
    }

    [Fact]
    public async Task 夜間取數連不上PRTG_不覆寫既有同步狀態()
    {
        var store = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
        var fixedTime = new DateTime(2026, 3, 31, 12, 0, 0);
        store.Update(s =>
        {
            s.CompletedAt = fixedTime;
            s.Success = true;
            s.Source = PrtgStructureSyncStatus.SourceManual;
            s.Devices = 10;
            s.Sensors = 50;
            s.MapOk = 8;
        });

        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, _, _, _) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, guard: null, structureSyncGate: null);

        var after = store.GetOrNull();
        Assert.NotNull(after);
        Assert.Equal(fixedTime, after.CompletedAt);
        Assert.True(after.Success);
        Assert.Equal("manual", after.Source);
        Assert.Equal(10, after.Devices);
        Assert.Equal(50, after.Sensors);
        Assert.Equal(8, after.MapOk);
    }

    [Fact]
    public void BuildPrtgDays_回望天數與範圍決定PRTG處理哪些天()
    {
        var today = new DateTime(2026, 9, 15);
        var retention = new RetentionOptions { RetentionDays = 30 };

        // request.BackfillOverride = null 時回傳 [today.AddDays(-1)]
        var daysNull = AnalysisOrchestrator.BuildPrtgDays(new RunRequest { BackfillOverride = null }, retention, today);
        Assert.Equal(new[] { today.AddDays(-1) }, daysNull);

        // request.BackfillOverride = 1 時回傳 [today.AddDays(-1)]
        var days1 = AnalysisOrchestrator.BuildPrtgDays(new RunRequest { BackfillOverride = 1 }, retention, today);
        Assert.Equal(new[] { today.AddDays(-1) }, days1);

        // request.BackfillOverride = 5 時回傳由近到遠 5 天
        var days5 = AnalysisOrchestrator.BuildPrtgDays(new RunRequest { BackfillOverride = 5 }, retention, today);
        Assert.Equal(5, days5.Count);
        Assert.Equal(today.AddDays(-1), days5[0]);
        Assert.Equal(today.AddDays(-2), days5[1]);
        Assert.Equal(today.AddDays(-3), days5[2]);
        Assert.Equal(today.AddDays(-4), days5[3]);
        Assert.Equal(today.AddDays(-5), days5[4]);

        // request.BackfillOverride 超過保留期上限時截斷至保留天數上限
        var expectedMax = LogForesight.Core.Models.NetiqOptions.GetEffectiveBackfillDaysLimit(retention.RetentionDays);
        var daysExceed = AnalysisOrchestrator.BuildPrtgDays(new RunRequest { BackfillOverride = 50 }, retention, today);
        Assert.Equal(expectedMax, daysExceed.Count);
        Assert.Equal(today.AddDays(-1), daysExceed[0]);
        Assert.Equal(today.AddDays(-expectedMax), daysExceed[^1]);

        // request.Scope = NetiqHosts 時回傳 [today-1]（指定主機更新只查昨天）
        var daysNetiq = AnalysisOrchestrator.BuildPrtgDays(
            new RunRequest { Scope = RunScope.NetiqHosts, BackfillOverride = 5 }, retention, today);
        Assert.Equal(new[] { today.AddDays(-1) }, daysNetiq);

        // 只跑本機（LocalOnly）與指定主機更新一樣只處理昨天：對一台主機的更新不該觸發全機房 N 天的 PRTG 查詢
        var localOnly = AnalysisOrchestrator.BuildPrtgDays(
            new RunRequest { Scope = RunScope.LocalOnly, BackfillOverride = 30 }, new RetentionOptions(), today);
        Assert.Single(localOnly);
        Assert.Equal(today.AddDays(-1), localOnly[0]);
    }

    [Fact]
    public async Task 多日執行逐日發佈與單一FindingsReady()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, _, progress, registry) = CreateContext();
        var day3 = DateTime.Today.AddDays(-3);
        var day2 = DateTime.Today.AddDays(-2);
        var day1 = DateTime.Today.AddDays(-1);

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { day1, day2, day3 }, Task.CompletedTask, guard: null);

        // progress 收到 RunPhases.PrtgDateRange
        Assert.Contains(progress.Reports, r => r.Phase == RunPhases.PrtgDateRange && r.Done == 3 && r.Total == 0);

        // 登錄簿逐日已發佈
        Assert.True(registry.IsPublished(day1));
        Assert.True(registry.IsPublished(day2));
        Assert.True(registry.IsPublished(day3));

        // RunPhases.PrtgFindingsReady 只出現一次
        Assert.Equal(1, progress.Phases.Count(p => p == RunPhases.PrtgFindingsReady));

        // 且在 DateRange 之後
        var rangeIndex = progress.Phases.IndexOf(RunPhases.PrtgDateRange);
        var readyIndex = progress.Phases.IndexOf(RunPhases.PrtgFindingsReady);
        Assert.True(readyIndex > rangeIndex);
    }

    [Fact]
    public async Task 過去日查無對應表時安全跳過()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day1 = DateTime.Today.AddDays(-2);
        var day2 = DateTime.Today.AddDays(-1);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-TEST", Active = true, IpAddress = "192.168.1.101" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[] { new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Down" } }, now);

        // 兩天都有 state change
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day1.Date.AddHours(2), Status = "Down" },
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day2.Date.AddHours(2), Status = "Down" }
        });

        var (ctx, console, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day2, day1 }, Task.CompletedTask, guard: null);

        // prtgConsole 輸出包含「無主機對應可用（鏡像晚於該日建立），PRTG finding 未歸戶」
        Assert.Contains(console.Lines, l => l.Contains("無主機對應可用（鏡像晚於該日建立），PRTG finding 未歸戶"));

        // day1 的 finding 不進行主機關聯（登錄簿為空清單）
        Assert.Empty(registry.For(host.HostId, day1));

        // 但不影響 day2 正常評估
        Assert.NotEmpty(registry.For(host.HostId, day2));
    }

    [Fact]
    public async Task 過去日沿用最新歷史對應表()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day3 = DateTime.Today.AddDays(-3);
        var day1 = DateTime.Today.AddDays(-2);
        var day = DateTime.Today.AddDays(-1);

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[] { new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Down" } }, now);

        // 準備 day-3 的 HostMap，但無 day-1 的 HostMap
        prtgStore.ReplaceHostMapForDate(day3, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day3,
                DeviceObjid = 1,
                HostId = 101,
                HostName = "SRV-TEST",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });

        // day-1 發生持續 Down
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow
            {
                SensorObjid = 2001,
                ChangedAt = day1.Date.AddHours(2),
                Status = "Down"
            }
        });

        var (ctx, _, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { day, day1 }, Task.CompletedTask, guard: null);

        // day-1 評估時成功取用 day-3 的對應表，找到主機並完成 finding 歸屬
        var findings = registry.For(101, day1);
        Assert.Single(findings);
        Assert.Equal("prtg:down:2001", findings[0].EventKey);
    }

    [Fact]
    public async Task 多日執行沉默Device規則只在最新日生效()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day1 = DateTime.Today.AddDays(-1);
        var day2 = DateTime.Today;

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-UNKNOWN", Active = true, IpAddress = "192.168.1.102" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        // 準備未暫停且全 Unknown 的 device 2
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 2, Name = "SRV-UNKNOWN", Ip = "192.168.1.102" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 3001, DeviceObjid = 2, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Unknown" }
        }, now);

        // 過去日 day1 的 HostMap
        prtgStore.ReplaceHostMapForDate(day1, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day1,
                DeviceObjid = 2,
                HostId = host.HostId,
                HostName = "SRV-UNKNOWN",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });

        var (ctx, _, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day2, day1 }, Task.CompletedTask, guard: null);

        // day1 不產生 silent finding
        Assert.DoesNotContain(registry.For(host.HostId, day1), f => f.EventKey.StartsWith("prtg:silent"));

        // day2 產生 silent finding
        Assert.Contains(registry.For(host.HostId, day2), f => f.EventKey.StartsWith("prtg:silent"));
    }

    [Fact]
    public async Task 保守策略多日提示回填()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, console, _, _) = CreateContext();
        var days = new[]
        {
            DateTime.Today.AddDays(-1),
            DateTime.Today.AddDays(-2),
            DateTime.Today.AddDays(-3)
        };

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            days, Task.CompletedTask, guard: null);

        Assert.Contains(console.Lines, l => l.Contains("其餘 2 天的 PRTG 數值不在立即執行內取，請用排程作業頁的「開始回填」。"));
    }

    [Fact]
    public async Task 多日執行逐日回報PRTG日期範圍與當前天次()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, _, progress, _) = CreateContext();
        var days = new[]
        {
            DateTime.Today.AddDays(-1),
            DateTime.Today.AddDays(-2),
            DateTime.Today.AddDays(-3)
        };

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            days, Task.CompletedTask, guard: null);

        var dateRangeReports = progress.Reports.Where(r => r.Phase == RunPhases.PrtgDateRange).ToList();
        Assert.Equal(4, dateRangeReports.Count);
        // 第一次為迴圈前：(3, 0)
        Assert.Equal(3, dateRangeReports[0].Done);
        Assert.Equal(0, dateRangeReports[0].Total);
        // 後三次為逐日迴圈開始時：(3, 1), (3, 2), (3, 3)
        Assert.Equal(3, dateRangeReports[1].Done);
        Assert.Equal(1, dateRangeReports[1].Total);
        Assert.Equal(3, dateRangeReports[2].Done);
        Assert.Equal(2, dateRangeReports[2].Total);
        Assert.Equal(3, dateRangeReports[3].Done);
        Assert.Equal(3, dateRangeReports[3].Total);
    }

    [Fact]
    public async Task 評估同代碼多條規則時在Console輸出警告()
    {
        var rules = KnownIssueSeed.CreateRules().ToList();
        rules.Add(new KnownIssueRule
        {
            Id = "a-custom-down",
            Platform = "prtg",
            PrtgRuleCode = "down",
            PrtgThreshold = 60,
            Enabled = true,
            Severity = IssueSeverity.Critical,
            ElevatesDayRisk = true,
            Description = "Custom duplicate down rule"
        });
        KnownIssueCatalog.Initialize(rules);

        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });

        var (ctx, console, _, _) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            new[] { DateTime.Today.AddDays(-1) }, Task.CompletedTask, guard: null);

        Assert.Contains(console.Lines, l => l.Contains("規則代碼 down 有多條啟用規則，採用 a-custom-down"));
    }

    [Fact]
    public async Task Site範圍規則型抑制使PRTG發佈簽章Suppressed且追加後不拉高日風險()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day = DateTime.Today.AddDays(-1);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-TEST", Active = true, IpAddress = "192.168.1.101" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[] { new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Down" } }, now);

        prtgStore.ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day,
                DeviceObjid = 1,
                HostId = host.HostId,
                HostName = "SRV-TEST",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });

        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow
            {
                SensorObjid = 2001,
                ChangedAt = day.Date.AddHours(2),
                Status = "Down"
            }
        });

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            Date = day,
            HostId = host.HostId,
            Host = host.HostName,
            RiskLevel = RiskLevels.Low,
            RiskBasis = "baseline"
        });

        var suppressionStore = new SuppressionStore(_backend.Blob("suppressions"));
        suppressionStore.SaveAll(new List<RuleSuppression>
        {
            new()
            {
                RuleId = "builtin-prtg-down",
                Scope = SuppressionScopes.Site,
                Reason = "測試抑制"
            }
        });

        var (ctx, console, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, guard: null);

        var findings = registry.For(host.HostId, day);
        var downSig = Assert.Single(findings);
        Assert.Equal("builtin-prtg-down", downSig.RuleId);
        Assert.True(downSig.Suppressed);

        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        Assert.Contains(record.TopIssues, i => i.EventKey == "prtg:down:2001");
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
        Assert.DoesNotContain("prtg:down", record.RiskBasis ?? string.Empty);
        Assert.Contains(console.Lines, l => l.Contains("已抑制 1 筆"));
    }

    [Fact]
    public async Task Group範圍規則型抑制當主機不在群組時不抑制且日風險上調()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day = DateTime.Today.AddDays(-1);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost
        {
            HostName = "SRV-TEST",
            Active = true,
            IpAddress = "192.168.1.101",
            GroupIds = new List<long> { 101 }
        });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[] { new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk", SensorType = "SNMP Disk Free", Status = "Down" } }, now);

        prtgStore.ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day,
                DeviceObjid = 1,
                HostId = host.HostId,
                HostName = "SRV-TEST",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });

        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow
            {
                SensorObjid = 2001,
                ChangedAt = day.Date.AddHours(2),
                Status = "Down"
            }
        });

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            Date = day,
            HostId = host.HostId,
            Host = host.HostName,
            RiskLevel = RiskLevels.Low,
            RiskBasis = "baseline"
        });

        var suppressionStore = new SuppressionStore(_backend.Blob("suppressions"));
        suppressionStore.SaveAll(new List<RuleSuppression>
        {
            new()
            {
                RuleId = "builtin-prtg-down",
                Scope = SuppressionScopes.Group,
                HostGroupId = 999,
                Reason = "群組 999 抑制"
            }
        });

        var (ctx, _, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, guard: null);

        var findings = registry.For(host.HostId, day);
        var downSig = Assert.Single(findings);
        Assert.False(downSig.Suppressed);

        // 磁碟 sensor 的 down 走不限分類規則（seed v7 非重大、嚴重度高）→ 日風險上調到「中」
        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        Assert.Equal(RiskLevels.Medium, record.RiskLevel);
        Assert.Equal("prtg:down", record.RiskBasis);
    }

    [Fact]
    public async Task 簽章型抑制只抑制該sensor同規則另一sensor不受影響()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>();
        });

        var day = DateTime.Today.AddDays(-1);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-TEST", Active = true, IpAddress = "192.168.1.101" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TEST", Ip = "192.168.1.101" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Disk C", SensorType = "SNMP Disk Free", Status = "Down" },
            new PrtgSensorRow { Objid = 2002, DeviceObjid = 1, Name = "Disk D", SensorType = "SNMP Disk Free", Status = "Down" }
        }, now);

        prtgStore.ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day,
                DeviceObjid = 1,
                HostId = host.HostId,
                HostName = "SRV-TEST",
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });

        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(2), Status = "Down" },
            new PrtgStateChangeRow { SensorObjid = 2002, ChangedAt = day.Date.AddHours(2), Status = "Down" }
        });

        var targetSig = new LogIssueSignature
        {
            LogName = PrtgFindingMapper.PrtgLogName,
            Source = "PRTG:down",
            EventId = 0,
            EntryType = System.Diagnostics.EventLogEntryType.Warning,
            EventKey = "prtg:down:2001"
        };
        var targetKey = IssueSignatureKey.For(targetSig);

        var suppressionStore = new SuppressionStore(_backend.Blob("suppressions"));
        suppressionStore.SaveAll(new List<RuleSuppression>
        {
            new()
            {
                TargetType = SuppressionTargetTypes.Signature,
                SignatureKey = targetKey,
                Scope = SuppressionScopes.Site,
                Reason = "僅抑制 sensor 2001"
            }
        });

        var (ctx, _, _, registry) = CreateContext();

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, guard: null);

        var findings = registry.For(host.HostId, day);
        Assert.Equal(2, findings.Count);
        var sig2001 = findings.Single(f => f.EventKey == "prtg:down:2001");
        var sig2002 = findings.Single(f => f.EventKey == "prtg:down:2002");

        Assert.True(sig2001.Suppressed);
        Assert.False(sig2002.Suppressed);
    }

    private void EnableConservativePrtgWithDefaultWhitelist()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.invalid.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgSensorTypeWhitelist = new List<string>(SystemSettings.DefaultPrtgSensorTypeWhitelist);
        });
    }

    private void MapDeviceToHost(DateTime day, long deviceObjid, WebHost host)
    {
        _backend.PrtgStore().ReplaceHostMapForDate(day, new[]
        {
            new PrtgHostMapRow
            {
                MapDate = day,
                DeviceObjid = deviceObjid,
                HostId = host.HostId,
                HostName = host.HostName,
                MapStatus = PrtgMapStatus.Ok,
                CreatedAt = DateTime.Now
            }
        });
    }

    [Fact]
    public async Task 預設白名單不含Ping_PingDown仍進規則評估產生Down()
    {
        EnableConservativePrtgWithDefaultWhitelist();
        Assert.DoesNotContain(SystemSettings.DefaultPrtgSensorTypeWhitelist,
            t => string.Equals(t, "Ping", StringComparison.OrdinalIgnoreCase));

        var today = DateTime.Today;
        var day = today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-PING", Active = true, IpAddress = "192.168.1.111" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-PING", Ip = "192.168.1.111" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Ping", SensorType = "Ping", Status = "Down", Category = PrtgSensorCategories.Availability }
        }, now);
        // 22:30 進入 Down，持續到午夜 90 分鐘（預設門檻 60）
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(22).AddMinutes(30), Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low, RiskBasis = "baseline"
        });

        var (ctx, _, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { today, day }, Task.CompletedTask, guard: null);

        // 連通性分類 → 挑到 availability 規則（門檻 30、重大）→ 日風險「高」
        var sig = Assert.Single(registry.For(host.HostId, day), f => f.EventKey == "prtg:down:2001");
        Assert.Equal("builtin-prtg-down-availability", sig.RuleId);
        Assert.Equal(RiskLevels.High, Assert.Single(hostRecordStore.ReadRecent(day, 1)).RiskLevel);
    }

    [Fact]
    public async Task 非連通性sensorDown_走不限分類規則_日風險只到中()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day = DateTime.Today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-TRAFFIC", Active = true, IpAddress = "192.168.1.112" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-TRAFFIC", Ip = "192.168.1.112" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Traffic", SensorType = "SNMP Traffic 64bit", Status = "Down", Category = PrtgSensorCategories.Traffic }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(22).AddMinutes(30), Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low, RiskBasis = "baseline"
        });

        var (ctx, _, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, guard: null);

        var sig = Assert.Single(registry.For(host.HostId, day), f => f.EventKey == "prtg:down:2001");
        Assert.Equal("builtin-prtg-down", sig.RuleId);
        Assert.False(sig.ElevatesDayRisk);
        Assert.Equal(RiskLevels.Medium, Assert.Single(hostRecordStore.ReadRecent(day, 1)).RiskLevel);
    }

    [Fact]
    public async Task 同裝置PingDown時TrafficDown被合併_執行輸出含已合併筆數()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var today = DateTime.Today;
        var day = today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-FOLD", Active = true, IpAddress = "192.168.1.112" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-FOLD", Ip = "192.168.1.112" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Ping", SensorType = "Ping", Status = "Down", Category = PrtgSensorCategories.Availability },
            new PrtgSensorRow { Objid = 2002, DeviceObjid = 1, Name = "Port 1", SensorType = "SNMP Traffic 64bit", Status = "Down", Category = PrtgSensorCategories.Traffic },
            new PrtgSensorRow { Objid = 2003, DeviceObjid = 1, Name = "Port 2", SensorType = "SNMP Traffic 64bit", Status = "Down", Category = PrtgSensorCategories.Traffic }
        }, now);
        var downAt = day.Date.AddHours(22).AddMinutes(30);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = downAt, Status = "Down" },
            new PrtgStateChangeRow { SensorObjid = 2002, ChangedAt = downAt, Status = "Down" },
            new PrtgStateChangeRow { SensorObjid = 2003, ChangedAt = downAt, Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var (ctx, console, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { today, day }, Task.CompletedTask, guard: null);

        var signatures = registry.For(host.HostId, day);
        Assert.Contains(signatures, f => f.EventKey == "prtg:down:2001");
        Assert.DoesNotContain(signatures, f => f.EventKey == "prtg:down:2002" || f.EventKey == "prtg:down:2003");
        Assert.Contains(console.Lines, l => l.Contains($"PRTG 規則評估完成（{day:yyyy-MM-dd}）") && l.Contains("已合併 2 筆"));
    }

    /// <summary>
    /// 兩段式的核心：回望 3 天、同一 sensor 三天都 Warning、資料庫沒有任何歷史。
    /// 最新一天處理的當下，較舊兩天還沒寫進資料庫，只靠第一段保存的本趟簽章才數得到第 3 次。
    /// </summary>
    [Fact]
    public async Task 多日回望同sensor連續Warning_最新日跨日標註並升級且就緒只送一次()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day1 = DateTime.Today.AddDays(-1);
        var day2 = DateTime.Today.AddDays(-2);
        var day3 = DateTime.Today.AddDays(-3);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-WARN", Active = true, IpAddress = "192.168.1.121" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-WARN", Ip = "192.168.1.121" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "CPU", SensorType = "WMI CPU Load", Status = "Warning" }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day3.Date.AddMinutes(30), Status = "Warning" },
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day2.Date.AddMinutes(30), Status = "Warning" },
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day1.Date.AddMinutes(30), Status = "Warning" }
        });
        MapDeviceToHost(day3, 1, host);
        MapDeviceToHost(day2, 1, host);
        MapDeviceToHost(day1, 1, host);

        var (ctx, console, progress, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day1, day2, day3 }, Task.CompletedTask, guard: null);

        var newestSig = Assert.Single(registry.For(host.HostId, day1), f => f.EventKey == "prtg:warning:2001");
        Assert.Contains("第 3 次，連續第 3 日", newestSig.SampleMessages[0]);
        Assert.Equal(IssueSeverity.High, newestSig.Severity);

        var middleSig = Assert.Single(registry.For(host.HostId, day2), f => f.EventKey == "prtg:warning:2001");
        Assert.Contains("第 2 次，連續第 2 日", middleSig.SampleMessages[0]);
        Assert.Equal(IssueSeverity.Medium, middleSig.Severity);

        var oldestSig = Assert.Single(registry.For(host.HostId, day3), f => f.EventKey == "prtg:warning:2001");
        Assert.DoesNotContain("近 14 日", oldestSig.SampleMessages[0]);

        Assert.Equal(1, progress.Phases.Count(p => p == RunPhases.PrtgFindingsReady));
        Assert.Contains(console.Lines, l => l.Contains($"PRTG 規則評估完成（{day1:yyyy-MM-dd}）") && l.Contains("跨日升級 1 筆、長期 Down 0 筆"));
    }

    /// <summary>
    /// 重跑時本趟重評的日期只認本趟結果：資料庫裡前一天留有上一趟寫入的 warning（這一趟那天已不成立），
    /// 不得被算成歷史，最新一天不應出現「第 2 次」。
    /// </summary>
    [Fact]
    public async Task 回望重跑時本趟重評日期不採用資料庫的舊命中()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day1 = DateTime.Today.AddDays(-1);
        var day2 = DateTime.Today.AddDays(-2);

        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-RERUN", Active = true, IpAddress = "192.168.1.123" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-RERUN", Ip = "192.168.1.123" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "CPU", SensorType = "WMI CPU Load", Status = "Warning" }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day2.Date.AddMinutes(10), Status = "Up" },
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day1.Date.AddMinutes(30), Status = "Warning" }
        });
        MapDeviceToHost(day2, 1, host);
        MapDeviceToHost(day1, 1, host);

        // 上一趟在 day2 留下的 warning（門檻或狀態改過後這一趟已不成立）
        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            Date = day2, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low,
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = PrtgFindingMapper.PrtgLogName, Source = "PRTG:warning", EventId = 0,
                    EntryType = System.Diagnostics.EventLogEntryType.Warning, EventKey = "prtg:warning:2001",
                    Count = 1, Severity = IssueSeverity.Medium
                }
            }
        });

        var (ctx, _, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day1, day2 }, Task.CompletedTask, guard: null);

        Assert.Empty(registry.For(host.HostId, day2));
        var sig = Assert.Single(registry.For(host.HostId, day1), f => f.EventKey == "prtg:warning:2001");
        Assert.DoesNotContain("近 14 日", sig.SampleMessages[0]);
    }

    /// <summary>
    /// 長期 Down 不再拉高日風險：資料庫已有連續 13 天的 down（重大規則 availability），
    /// 當日第 14 天的 finding 不帶重大旗標，執行輸出計入長期 Down。
    /// </summary>
    [Fact]
    public async Task 資料庫已有連續13日Down_當日視為長期Down不拉高日風險()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day = DateTime.Today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-DEAD", Active = true, IpAddress = "192.168.1.122" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-DEAD", Ip = "192.168.1.122" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Ping", SensorType = "Ping", Status = "Down", Category = PrtgSensorCategories.Availability }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(1), Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        for (var n = 1; n <= 13; n++)
        {
            hostRecordStore.Append(new DailyAnalysisRecord
            {
                Date = day.AddDays(-n),
                HostId = host.HostId,
                Host = host.HostName,
                RiskLevel = RiskLevels.High,
                TopIssues = new List<LogIssueSignature>
                {
                    new()
                    {
                        LogName = PrtgFindingMapper.PrtgLogName, Source = "PRTG:down", EventId = 0,
                        EntryType = System.Diagnostics.EventLogEntryType.Warning, EventKey = "prtg:down:2001",
                        Count = 1, Severity = IssueSeverity.High, ElevatesDayRisk = true
                    }
                }
            });
        }
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low, RiskBasis = "baseline"
        });

        var (ctx, console, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, guard: null);

        var sig = Assert.Single(registry.For(host.HostId, day), f => f.EventKey == "prtg:down:2001");
        Assert.Equal("builtin-prtg-down-availability", sig.RuleId);
        Assert.False(sig.ElevatesDayRisk);
        Assert.Contains("已連續 14 日，建議在 PRTG 暫停該 sensor 或建立抑制", sig.SampleMessages[0]);

        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        // 長期 Down 關掉重大旗標且嚴重度封頂「中」→ 不再拉日風險，維持原本的「低」
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
        Assert.Contains(console.Lines, l => l.Contains("長期 Down 1 筆"));
    }

    /// <summary>
    /// 端到端：評估帶出 sensor 分類 → 映射進簽章 → 補追加時與事件日誌的非預期關機佐證，
    /// 執行輸出計入「跨來源佐證（補追加階段）」。
    /// </summary>
    [Fact]
    public async Task 補追加時事件日誌非預期關機與PingDown佐證_寫入關聯欄位並計入執行輸出()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day = DateTime.Today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-OUTAGE", Active = true, IpAddress = "192.168.1.123" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-OUTAGE", Ip = "192.168.1.123" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Ping", SensorType = "Ping", Status = "Down", Category = PrtgSensorCategories.Availability }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(22).AddMinutes(30), Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "Microsoft-Windows-Kernel-Power", EventId = 41, Count = 1 }
            }
        });

        var (ctx, console, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, guard: null);

        var sig = Assert.Single(registry.For(host.HostId, day), f => f.EventKey == "prtg:down:2001");
        Assert.Equal(PrtgSensorCategories.Availability, sig.PrtgSensorCategory);

        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        Assert.Equal(CorrelationPatternIds.PrtgOutageCorroborated, Assert.Single(record.CorrelationAlertRefs).PatternId);
        Assert.StartsWith("【失聯獲 PRTG 證實】", Assert.Single(record.CorrelationAlerts));
        Assert.Contains(console.Lines, l => l.Contains($"PRTG 規則評估完成（{day:yyyy-MM-dd}）") && l.Contains("跨來源佐證（補追加階段）1 筆"));
    }

    /// <summary>
    /// 關聯抑制（TargetType=Correlation）在 PRTG 路徑發佈時一併算進登錄簿：
    /// 佐證模式被抑制時只進已抑制清單、不進關聯告警、不計入執行輸出。
    /// </summary>
    [Fact]
    public async Task 佐證模式被關聯抑制時登錄簿帶出集合且補追加只進已抑制清單()
    {
        EnableConservativePrtgWithDefaultWhitelist();

        var day = DateTime.Today.AddDays(-1);
        var hostStore = new HostStore(_backend.Blob("hosts"));
        var host = hostStore.Upsert(new WebHost { HostName = "SRV-OUTAGE2", Active = true, IpAddress = "192.168.1.124" });

        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices(new[] { new PrtgDeviceRow { Objid = 1, Name = "SRV-OUTAGE2", Ip = "192.168.1.124" } }, now);
        prtgStore.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 2001, DeviceObjid = 1, Name = "Ping", SensorType = "Ping", Status = "Down", Category = PrtgSensorCategories.Availability }
        }, now);
        prtgStore.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 2001, ChangedAt = day.Date.AddHours(22).AddMinutes(30), Status = "Down" }
        });
        MapDeviceToHost(day, 1, host);

        var hostRecordStore = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        hostRecordStore.Append(new DailyAnalysisRecord
        {
            Date = day, HostId = host.HostId, Host = host.HostName, RiskLevel = RiskLevels.Low,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "Microsoft-Windows-Kernel-Power", EventId = 41, Count = 1 }
            }
        });

        new SuppressionStore(_backend.Blob("suppressions")).SaveAll(new List<RuleSuppression>
        {
            new()
            {
                TargetType = SuppressionTargetTypes.Correlation,
                CorrelationPatternId = CorrelationPatternIds.PrtgOutageCorroborated,
                Scope = SuppressionScopes.Site,
                Reason = "機房例行斷電演練"
            }
        });

        var (ctx, console, _, registry) = CreateContext();
        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, hostStore,
            new[] { day }, Task.CompletedTask, guard: null);

        Assert.Contains(CorrelationPatternIds.PrtgOutageCorroborated, registry.SuppressedPatternIdsFor(host.HostId, day));

        var record = Assert.Single(hostRecordStore.ReadRecent(day, 1));
        Assert.Empty(record.CorrelationAlerts);
        Assert.StartsWith("【失聯獲 PRTG 證實】", Assert.Single(record.SuppressedCorrelationAlerts));
        Assert.Contains(console.Lines, l => l.Contains($"PRTG 規則評估完成（{day:yyyy-MM-dd}）") && l.Contains("跨來源佐證（補追加階段）0 筆"));
    }
}
