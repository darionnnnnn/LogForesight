using LogForesight.Core;
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
public class PrtgDailyPipelineTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;

    public PrtgDailyPipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
    }

    public void Dispose()
    {
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
        public void Report(string phase, int done, int total) => Phases.Add(phase);
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
            DateTime.Today.AddDays(-1), Task.CompletedTask, guard: null);

        Assert.True(registry.IsReady);
        Assert.Equal(0, registry.HostCount);
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

        await PrtgDailyPipeline.RunAsync(
            ctx, _backend, new HostStore(_backend.Blob("hosts")),
            DateTime.Today.AddDays(-1), Task.CompletedTask, guard: null);

        Assert.True(registry.IsReady);
        Assert.Contains(RunPhases.PrtgFindingsReady, progress.Phases);
        Assert.Contains(RunPhases.PrtgDone, progress.Phases);
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
            DateTime.Today.AddDays(-1), Task.CompletedTask, guard: null);

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
            DateTime.Today.AddDays(-1), Task.CompletedTask, guard: null, structureSyncGate: gate);

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
            DateTime.Today.AddDays(-1), Task.CompletedTask, guard: null, structureSyncGate: gate);

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
            DateTime.Today.AddDays(-1), Task.CompletedTask, guard: null, structureSyncGate: gate);

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
            DateTime.Today.AddDays(-1), Task.CompletedTask, guard: null, structureSyncGate: null);

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
            DateTime.Today.AddDays(-1), Task.CompletedTask, guard: null);

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
            DateTime.Today.AddDays(-1), Task.CompletedTask, guard: null);

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
            DateTime.Today.AddDays(-1), Task.CompletedTask, guard: null, structureSyncGate: null);

        var after = store.GetOrNull();
        Assert.NotNull(after);
        Assert.Equal(fixedTime, after.CompletedAt);
        Assert.True(after.Success);
        Assert.Equal("manual", after.Source);
        Assert.Equal(10, after.Devices);
        Assert.Equal(50, after.Sensors);
        Assert.Equal(8, after.MapOk);
    }
}
