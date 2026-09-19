using System.Reflection;
using LogForesight.Core;
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 階段 A1：排程／PRTG 後端守門與狀態碼統一。
/// 同一類語意（沒有可停止的對象、被另一個執行互斥擋下）在各 controller 必須回同一個狀態碼，
/// 前端才能一致判斷；手動 AI 分析在取數執行中要直接擋在 controller，不可下沉到 service
///（取數流程自己會以 fetch-followup 內部觸發 AI，下沉會讓「AI 跟隨取數」整個失效）。
/// </summary>
public class ScheduleGuardStatusCodeTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly Func<LfDbContext> _contextFactory;
    private readonly FakeSystemSettingsStore _aiSettingsStore = new();
    private readonly RecordingAuditService _audit = new();
    private readonly SystemSettingsStore _settingsStore;

    public ScheduleGuardStatusCodeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-a1-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        var field = typeof(StorageBackend).GetField("_dbFactory", BindingFlags.Instance | BindingFlags.NonPublic);
        _contextFactory = (Func<LfDbContext>)field!.GetValue(_backend)!;
        _settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── 共用替身與工廠 ────────────────────────────────────────────────

    /// <summary>只回答「AI 有沒有設定好」的假服務（與 AiAnalysisSchedulerTests 同一套判準）。</summary>
    private sealed class StubWebAi : IWebAiService
    {
        public StubWebAi(bool available) => Available = available;
        public bool Available { get; }
        public Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class =>
            Task.FromResult<T?>(null);
        public Task<string?> ChatOnceAsync(string systemPrompt, string userPrompt) => Task.FromResult<string?>(null);
    }

    private AiAnalysisHostedService CreateAiScheduler(SchedulerRunState schedulerState, AiAnalysisRunState aiRunState)
    {
        var backfiller = new DailyRecordBackfiller(_contextFactory);
        backfiller.Run(CancellationToken.None); // 空庫零候選，立即完成，讓 AI 的回填閘門放行

        return new AiAnalysisHostedService(
            new ScheduleOptionsStore(_backend.Blob("schedule_options")),
            schedulerState,
            aiRunState,
            _backend.RecordStore(),
            _backend,
            _aiSettingsStore,
            new DataVersionStamp(),
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
            backfiller,
            new StubWebAi(true),
            new FakeSuppressionStore(),
            new FakeAiService());
    }

    /// <summary>
    /// aiScheduler 預設傳 null：守門若沒擋住，呼叫端會炸 NullReferenceException 而不是靜靜通過，
    /// 「AI 沒有被觸發」因此有硬證據（再加上稽核未寫入）。
    /// </summary>
    private ScheduleController CreateScheduleController(
        SchedulerRunState runState,
        AiAnalysisRunState aiRunState,
        IWebAiService? webAi,
        AiAnalysisHostedService? aiScheduler = null)
    {
        var controller = new ScheduleController(
            new ScheduleOptionsStore(_backend.Blob("schedule_options")),
            scheduler: null!,
            runState,
            hosts: new HostStore(_backend.Blob("hosts")),
            sentinels: null!,
            audit: _audit,
            currentUser: new FakeCurrentUser(),
            users: null!,
            records: _backend.RecordStore(),
            settingsStore: _aiSettingsStore,
            userDisplayNames: new UserDisplayNameService(_aiSettingsStore),
            aiScheduler: aiScheduler!,
            aiRunState: aiRunState,
            webAi: webAi);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    // ── C1：取數執行中拒絕手動 AI ────────────────────────────────────

    [Fact]
    public async Task C1_取數執行中呼叫ai_run_回409且AI未被觸發()
    {
        var runState = new SchedulerRunState();
        var aiRunState = new AiAnalysisRunState();
        Assert.True(runState.TryBeginRun("manual", out _));

        // aiScheduler 故意留 null：守門一旦失效就會是 NullReferenceException，不會偽裝成通過
        var controller = CreateScheduleController(runState, aiRunState, new StubWebAi(true));

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => controller.RunAi(new TriggerAiRunRequest { ForceRerun = false }));

        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
        Assert.Contains("取數執行中", ex.Message);
        Assert.False(aiRunState.IsRunning);   // AI 沒有進入執行
        Assert.Empty(_audit.Entries);          // 連稽核都還沒寫，代表擋在觸發之前
    }

    [Fact]
    public async Task C1_強制重新分析在取數執行中同樣回409()
    {
        var runState = new SchedulerRunState();
        var aiRunState = new AiAnalysisRunState();
        Assert.True(runState.TryBeginRun("manual", out _));
        var controller = CreateScheduleController(runState, aiRunState, new StubWebAi(true));

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => controller.RunAi(new TriggerAiRunRequest { ForceRerun = true }));

        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
        Assert.False(aiRunState.IsRunning);
    }

    /// <summary>
    /// 反例：守門不得下沉到 service。取數執行中，取數流程自己以 fetch-followup 內部呼叫
    /// TriggerRunAsync 必須仍能開跑，否則「AI 跟隨取數進度」整個失效。
    /// </summary>
    [Fact]
    public async Task C1反例_取數執行中_fetch_followup直呼TriggerRunAsync仍能開始執行()
    {
        var runState = new SchedulerRunState();
        var aiRunState = new AiAnalysisRunState();
        Assert.True(runState.TryBeginRun("schedule", out _));

        var service = CreateAiScheduler(runState, aiRunState);

        var started = await service.TriggerRunAsync(forceRerun: false, trigger: "fetch-followup");

        Assert.True(started);
        await aiRunState.WaitForCompletionAsync(TimeSpan.FromSeconds(10));
    }

    // ── C2：AI 服務不可用 ───────────────────────────────────────────

    [Fact]
    public async Task C2_AI不可用時呼叫ai_run_回400()
    {
        var runState = new SchedulerRunState(); // 取數沒在跑，排除 C1 的干擾
        var aiRunState = new AiAnalysisRunState();
        var controller = CreateScheduleController(runState, aiRunState, new StubWebAi(false));

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => controller.RunAi(new TriggerAiRunRequest { ForceRerun = false }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("AI 服務", ex.Message);
        Assert.False(aiRunState.IsRunning);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>反例：_webAi 為 null（可選相依未註冊）時視為可用，不得被 C2 擋下。</summary>
    [Fact]
    public async Task C2反例_webAi為null時ai_run不被擋下()
    {
        var runState = new SchedulerRunState();
        var aiRunState = new AiAnalysisRunState();
        var service = CreateAiScheduler(runState, aiRunState);
        var controller = CreateScheduleController(runState, aiRunState, webAi: null, aiScheduler: service);

        var resp = await controller.RunAi(new TriggerAiRunRequest { ForceRerun = false });

        Assert.True(resp.Success);
        Assert.True(resp.Data!.Started);
        await aiRunState.WaitForCompletionAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task C1與C2同時成立時以409為準()
    {
        var runState = new SchedulerRunState();
        var aiRunState = new AiAnalysisRunState();
        Assert.True(runState.TryBeginRun("manual", out _));
        var controller = CreateScheduleController(runState, aiRunState, new StubWebAi(false));

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => controller.RunAi(new TriggerAiRunRequest { ForceRerun = false }));

        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
    }

    // ── C3：停止類端點一律 409 ──────────────────────────────────────

    [Fact]
    public void C3_取數停止_沒有執行中回409()
    {
        var controller = CreateScheduleController(new SchedulerRunState(), new AiAnalysisRunState(), new StubWebAi(true));

        var ex = Assert.Throws<DomainException>(() => controller.Cancel());

        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
        Assert.Equal("目前沒有正在執行的分析。", ex.Message); // 訊息文字不變
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void C3_AI停止_沒有執行中回409()
    {
        var controller = CreateScheduleController(new SchedulerRunState(), new AiAnalysisRunState(), new StubWebAi(true));

        var ex = Assert.Throws<DomainException>(() => controller.CancelAi());

        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
        Assert.Equal("目前沒有可停止的 AI 分析執行（可能已結束或已在停止中）。", ex.Message);
        Assert.Empty(_audit.Entries);
    }

    // ── C4：回填啟動端點區分互斥與設定錯誤 ──────────────────────────

    private sealed class StubSystemSettingsService : ISystemSettingsService
    {
        public SystemSettingsDto Get() => new();
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    // ── 環境探測：與回填、結構同步同一套分岔（體檢輪補上，文件 §7.2 早已把探測列進 409 那一類） ──

    [Fact]
    public void 探測啟動_回填執行中回409()
    {
        EnablePrtgWithMirror();
        var backfillState = new PrtgBackfillRunState();
        Assert.True(backfillState.TryBeginRun(out _));
        var controller = CreateProbeController(backfillState);

        var ex = Assert.Throws<DomainException>(() => controller.StartPrtgProbe());

        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
        Assert.Contains("回填執行中", ex.Message); // 訊息文字不變
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 探測啟動_未設定連線位址維持400()
    {
        var controller = CreateProbeController(new PrtgBackfillRunState());

        var ex = Assert.Throws<DomainException>(() => controller.StartPrtgProbe());

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("連線位址", ex.Message);
    }

    private SettingsController CreateProbeController(PrtgBackfillRunState backfillState)
    {
        var probe = new PrtgProbeService(_settingsStore, new PrtgProbeRunState(), backfillState,
            _backend, new FakeHostStore(), new FakeSentinelStore());
        var controller = new SettingsController(
            new StubSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            _audit,
            prtgProbe: probe,
            backend: _backend);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private void EnablePrtgWithMirror()
    {
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
        });
        _backend.PrtgStore().UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "CPU", SensorType = "wmicpu", Paused = false }
        }, DateTime.Now);
        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today.AddDays(-1), new List<PrtgHostMapRow>
        {
            new() { DeviceObjid = 1001, HostId = 101, MapStatus = PrtgMapStatus.Ok }
        });
    }

    private SettingsController CreateBackfillController(SchedulerRunState schedulerState)
    {
        var backfill = new PrtgBackfillService(
            _settingsStore, _backend, new PrtgBackfillRunState(), new PrtgProbeRunState(),
            new HostStore(_backend.Blob("hosts")), schedulerState, new PrtgStructureSyncRunState(), new FakeSentinelStore(),
            new PrtgStructureSyncService(_settingsStore, _backend, new PrtgStructureSyncRunState(), schedulerState, new HostStore(_backend.Blob("hosts")), new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey)), new PrtgBackfillRunState(), new FakeSentinelStore(), new DataVersionStamp()));

        var controller = new SettingsController(
            new StubSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            _audit,
            prtgBackfill: backfill,
            backend: _backend);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public void C4_回填啟動_取數執行中回409()
    {
        EnablePrtgWithMirror();
        var schedulerState = new SchedulerRunState();
        Assert.True(schedulerState.TryBeginRun("manual", out _));
        var controller = CreateBackfillController(schedulerState);

        var ex = Assert.Throws<DomainException>(() => controller.StartPrtgBackfill());

        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);
        Assert.Contains("取數執行中", ex.Message); // 訊息文字不變
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void C4_回填啟動_PRTG未啟用維持400()
    {
        // 不啟用 PRTG：設定／前提類的拒絕仍是輸入面的問題
        var controller = CreateBackfillController(new SchedulerRunState());

        var ex = Assert.Throws<DomainException>(() => controller.StartPrtgBackfill());

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("PRTG 擷取未啟用", ex.Message);
    }

    [Fact]
    public void C4_回填啟動_鏡像無感測器改由背景工作先同步()
    {
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgTimeoutSeconds = 5;
        });
        var controller = CreateBackfillController(new SchedulerRunState());

        var response = controller.StartPrtgBackfill();

        Assert.True(response.Data?.Started);
    }

    [Fact]
    public void C4_回填服務未啟用維持400()
    {
        var controller = new SettingsController(
            new StubSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            _audit,
            backend: _backend);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var ex = Assert.Throws<DomainException>(() => controller.StartPrtgBackfill());

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }
}
