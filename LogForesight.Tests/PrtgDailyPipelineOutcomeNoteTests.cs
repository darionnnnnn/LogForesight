using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 63-2 輪批次A-3：PRTG 取數前置條件不成立（未啟用／尚未設定認證資訊／憑證無法解密）時，
/// 原因寫在趟次層的 <see cref="BatchRun.PrtgOutcomeNote"/>，<see cref="BatchRun.PrtgDays"/> 維持 null
/// （硬補一筆逐日統計會改變 <c>LastRunIssue</c> 與補評估判定）。
/// 「憑證無法解密」會設全域解密失敗旗標，所以放 CryptoKeyState 集合序列化並在建構／釋放時重設。
/// </summary>
[Collection("CryptoKeyState")]
public class PrtgDailyPipelineOutcomeNoteTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;

    public PrtgDailyPipelineOutcomeNoteTests()
    {
        CryptoHelper.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-note-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
    }

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class Lines : IRunConsole
    {
        public List<string> Items { get; } = new();
        public void WriteLine(string message = "") => Items.Add(message);
    }

    private sealed class NullProgress : IRunProgress
    {
        public void Report(string phase, int done, int total) { }
    }

    private async Task<(BatchRun Run, Lines Console, PrtgFindingsRegistry Registry)> RunPipelineAsync(Action<SystemSettings> configure)
    {
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        settingsStore.Update(configure);
        var settings = settingsStore.Get();

        var console = new Lines();
        var registry = new PrtgFindingsRegistry();
        var hosts = new HostStore(_backend.Blob("hosts"));
        var issueOwners = new IssueOwnerStore(_backend.Blob("issue_owners"));
        var caseCoordinator = new IssueCaseCoordinator(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(),
            _backend.RecordStore(), hosts, issueOwners);
        var dispatch = NightlyDispatchFakes.Create(
            _backend.IssueCaseStore(), _backend.IssueHandlingStore(), _backend.RecordHandlingStore(),
            hosts, issueOwners, caseCoordinator);
        var recorder = new BatchRunRecorder(
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
            "test-host", Array.Empty<string>());
        var ctx = new AnalysisRunContext(
            new RunRequest(), new AppSettings(), new RetentionOptions(), console, CancellationToken.None,
            new EventLogService(), caseCoordinator, _backend.RiskyEventStore(), recorder,
            new OrchestratorResult(), UseAi: false, new NullProgress(), registry, dispatch,
            PrtgEnabled: settings.PrtgEnabled, Workflow: null);

        await PrtgDailyPipeline.RunAsync(ctx, _backend, hosts, new[] { DateTime.Today.AddDays(-1) },
            Task.CompletedTask, hostIds: null, guard: null);

        recorder.Finish(0);
        var run = new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")).GetRun(recorder.RunId)!;
        return (run, console, registry);
    }

    private static void AssertDisabledWithNote(BatchRun run, string expectedNote)
    {
        Assert.Equal(BatchRun.PrtgOutcomeDisabled, run.PrtgOutcome);
        Assert.Equal(expectedNote, run.PrtgOutcomeNote);
        // 原因放趟次層；逐日統計維持 null，LastRunIssue／補評估判定因此不受影響
        Assert.Null(run.PrtgDays);
        var phase = Assert.Single(run.Phases!, p => p.Key == RunTimingPhases.Prtg);
        Assert.Equal(RunPhaseOutcomes.Skipped, phase.Outcome);
        Assert.Equal(expectedNote, phase.Note);
    }

    [Fact]
    public async Task 未啟用_寫入未啟用原因()
    {
        var (run, console, registry) = await RunPipelineAsync(s => s.PrtgEnabled = false);

        AssertDisabledWithNote(run, BatchRun.PrtgNoteNotEnabled);
        Assert.Equal("PRTG 未啟用", run.PrtgOutcomeNote);
        Assert.Contains(console.Items, l => l.Contains("PRTG 未啟用"));
        Assert.True(registry.IsReady);
    }

    [Fact]
    public async Task 已啟用但尚未設定認證_寫入尚未設定原因()
    {
        var (run, console, _) = await RunPipelineAsync(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://127.0.0.1:1";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = "";
        });

        AssertDisabledWithNote(run, BatchRun.PrtgNoteNotConfigured);
        Assert.Equal("尚未設定認證資訊", run.PrtgOutcomeNote);
        Assert.Contains(console.Items, l => l.Contains("尚未設定認證資訊"));
        Assert.DoesNotContain(console.Items, l => l.Contains("無法解密"));
    }

    [Fact]
    public async Task 已啟用但連線位址空白_也算尚未設定()
    {
        var (run, _, _) = await RunPipelineAsync(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
        });

        AssertDisabledWithNote(run, BatchRun.PrtgNoteNotConfigured);
    }

    [Fact]
    public async Task 憑證無法解密_寫入無法解密原因且與尚未設定分開()
    {
        var (run, console, _) = await RunPipelineAsync(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://127.0.0.1:1";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CredentialTestData.Undecryptable;
        });

        AssertDisabledWithNote(run, BatchRun.PrtgNoteUndecryptable);
        Assert.Equal("憑證無法解密，請到 PRTG 維護頁重新輸入", run.PrtgOutcomeNote);
        Assert.Contains(console.Items, l => l.Contains("無法解密") && l.Contains("PRTG 維護頁"));
        Assert.DoesNotContain(console.Items, l => l.Contains("尚未設定認證資訊"));
    }

    [Fact]
    public void 前置條件判定_三種原因與可用狀態()
    {
        SystemSettings Make(bool enabled, string url, string tokenEnc) => new()
        {
            PrtgEnabled = enabled, PrtgUrl = url, PrtgAuthMode = PrtgAuthModes.Token, PrtgApiTokenEnc = tokenEnc
        };

        Assert.Equal(BatchRun.PrtgNoteNotEnabled, PrtgDailyPipeline.DisabledReason(Make(false, "https://p", CryptoHelper.Encrypt("t")))!.Value.Note);
        // 未啟用優先於憑證問題：沒開的功能不該提醒憑證
        Assert.Equal(BatchRun.PrtgNoteNotEnabled, PrtgDailyPipeline.DisabledReason(Make(false, "https://p", CredentialTestData.Undecryptable))!.Value.Note);
        Assert.Equal(BatchRun.PrtgNoteNotConfigured, PrtgDailyPipeline.DisabledReason(Make(true, "https://p", ""))!.Value.Note);
        Assert.Equal(BatchRun.PrtgNoteNotConfigured, PrtgDailyPipeline.DisabledReason(Make(true, "  ", CryptoHelper.Encrypt("t")))!.Value.Note);
        Assert.Equal(BatchRun.PrtgNoteUndecryptable, PrtgDailyPipeline.DisabledReason(Make(true, "https://p", CredentialTestData.Undecryptable))!.Value.Note);
        Assert.Null(PrtgDailyPipeline.DisabledReason(Make(true, "https://p", CryptoHelper.Encrypt("t"))));
    }
}

/// <summary>
/// 第 63-2 輪批次A-3：設定 DTO 與排程狀態 DTO 的 <c>prtgCredentialState</c> 來自
/// <see cref="PrtgClientFactory.CredentialState"/> 單一出口，三種狀態都要帶得出來（含只讀狀態的 DevMonitor 路徑）。
/// </summary>
[Collection("CryptoKeyState")]
public class PrtgCredentialStateDtoTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly FakeSystemSettingsStore _store = new();

    public PrtgCredentialStateDtoTests()
    {
        CryptoHelper.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-cred-dto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
    }

    public void Dispose()
    {
        CryptoHelper.ResetForTests();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private SystemSettingsService SettingsService() => new(_store, FakeCurrentUser.WithCapabilities(), new RecordingAuditService(),
        new FakeUserStore(),
        new LogForesight.Web.Services.Mail.MailNotificationService(_store, new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
            new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
            new MailNotifyStateStore(_backend.Blob("mail_notify_state")),
            new ScheduleFreshnessService(new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(_backend.Blob("schedule_options"))),
            new WorkOrderOutboxStore(_backend.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore()),
        new FakeReportUsageQuery(), new FakeSentinelStore());

    private ScheduleController ScheduleController() => new(
        new ScheduleOptionsStore(_backend.Blob("schedule_options")), scheduler: null!, new SchedulerRunState(),
        hosts: new FakeHostStore(), sentinels: new FakeSentinelStore(), audit: new RecordingAuditService(),
        currentUser: new FakeCurrentUser(), users: new FakeUserStore(), records: new FakeAnalysisRecordQuery(),
        settingsStore: _store, userDisplayNames: new UserDisplayNameService(_store),
        aiScheduler: null!, aiRunState: new AiAnalysisRunState());

    [Theory]
    [InlineData("good", "ok")]
    [InlineData("", "not_configured")]
    [InlineData("undecryptable", "undecryptable")]
    public void 設定DTO與排程狀態DTO帶出憑證狀態(string tokenKind, string expected)
    {
        _store.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = tokenKind switch
            {
                "good" => CryptoHelper.Encrypt("token"),
                "undecryptable" => CredentialTestData.Undecryptable,
                _ => ""
            };
        });

        var settingsDto = SettingsService().Get();
        var statusDto = ScheduleController().GetStatus().Data!;

        Assert.Equal(expected, settingsDto.PrtgCredentialState);
        Assert.Equal(expected, statusDto.PrtgCredentialState);
        // 解不開時舊的布林仍是「密文欄有值」，這正是前端不能再自己湊判定的原因
        Assert.Equal(tokenKind != "", settingsDto.PrtgHasApiToken);
    }

    [Fact]
    public void 憑證狀態以camelCase欄位序列化且不含任何憑證內容()
    {
        _store.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CredentialTestData.Undecryptable;
        });

        var json = System.Text.Json.JsonSerializer.Serialize(ScheduleController().GetStatus().Data!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"prtgCredentialState\":\"undecryptable\"", json);
        Assert.DoesNotContain(CredentialTestData.Undecryptable, json);
    }
}
