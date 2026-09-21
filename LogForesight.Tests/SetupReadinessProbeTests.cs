using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 初始設定就緒狀態探活與驗證測試（回饋五十輪批次 F-1b）。
/// 涵蓋 AI 實際探活（成功、失敗、快取、並發排隊、逾時降級）與
/// 郵件四項條件判定、PRTG 既有邏輯回歸驗證。
/// </summary>
public class SetupReadinessProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-probe-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly FakeGroupAccessStore _groupAccess = new();
    private readonly ScheduleOptionsStore _scheduleOptions;
    private readonly SetupWizardStateStore _stateStore;
    private readonly PrtgStructureSyncStatusStore _prtgSyncStore;

    public SetupReadinessProbeTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "s.db")}" }, _dir);
        _scheduleOptions = new ScheduleOptionsStore(_backend.Blob("schedule_options"));
        _stateStore = new SetupWizardStateStore(_backend.Blob("setup_wizard_state"));
        _prtgSyncStore = new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private SetupReadinessService CreateService(IAiProbeService aiProbe)
    {
        var appSettings = new WebAppSettings
        {
            Auth = new AuthSettings
            {
                Provider = "Stub",
                ServerAdmin = new ServerAdminSettings()
            }
        };
        var freshness = new ScheduleFreshnessService(
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
            _scheduleOptions);
        var mailService = new MailNotificationService(
            _settings, new FakeSmtpMailSender(), _hosts, _users,
            _groups, _groupAccess, new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
            new MailNotifyStateStore(_backend.Blob("mail_notify_state")), freshness);
        var health = new HealthService(_backend, new SchedulerRunState(), _backend.TopIssueBackfiller(), mailService, freshness);
        var identity = new IdentityService(
            _users, _groups, _hosts, new StubAuthenticationProvider(),
            new ServerAdminAuthenticator(appSettings),
            new RecordingAuditService(), new UserCapabilityResolver(_groups, _hosts));

        return new SetupReadinessService(
            health, identity, _settings, _sentinels, _hosts, _groupAccess, _groups,
            _scheduleOptions, _stateStore, appSettings, _prtgSyncStore, aiProbe);
    }

    private sealed class ProbeTestHttpMessageHandler : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? HandlerFunc { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (HandlerFunc != null)
            {
                return await HandlerFunc(request, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CountingSettingsStore : ISystemSettingsStore
    {
        private readonly ISystemSettingsStore _inner;
        private readonly Action _onGet;

        public CountingSettingsStore(ISystemSettingsStore inner, Action onGet)
        {
            _inner = inner;
            _onGet = onGet;
        }

        public SystemSettings Get()
        {
            _onGet();
            return _inner.Get();
        }

        public SystemSettings Update(Action<SystemSettings> mutation) => _inner.Update(mutation);
    }

    // ── AI 探活測試 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AI1_AI位址為出廠預設但探活失敗_AI步驟未完成且不拋例外()
    {
        string? requestedUri = null;
        var handler = new ProbeTestHttpMessageHandler
        {
            HandlerFunc = (req, _) =>
            {
                requestedUri = req.RequestUri?.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
        };
        using var client = new HttpClient(handler);
        var probeService = new AiProbeService(_settings, client);
        var setupService = CreateService(probeService);

        // 出廠預設位址確認
        Assert.Equal("http://localhost:8080", _settings.Get().AiBaseUrl);

        // 探活前尚未探活
        var initialStatus = setupService.GetStatus();
        var initialAi = initialStatus.Steps.Single(s => s.Id == "ai");
        Assert.False(initialAi.Done);
        Assert.Contains("尚未探活", initialAi.Detail);

        // 執行刷新探活，探活失敗（回傳 500）
        var probeResult = await setupService.RefreshProbeAsync();

        Assert.False(probeResult.IsReady);
        Assert.Equal(AiProbeStatus.Failed, probeResult.Status);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("http://localhost:8080/v1/chat/completions", requestedUri);

        var status = setupService.GetStatus();
        var aiStep = status.Steps.Single(s => s.Id == "ai");
        Assert.False(aiStep.Done);
        Assert.Contains("探活失敗", aiStep.Detail);
    }

    [Fact]
    public async Task AI2_AI探活成功_完成且60秒內連續兩次狀態請求_HTTP替身只收到一次()
    {
        var handler = new ProbeTestHttpMessageHandler();
        using var client = new HttpClient(handler);
        var probeService = new AiProbeService(_settings, client);
        var setupService = CreateService(probeService);

        // 第一次探活
        var probe1 = await setupService.RefreshProbeAsync();
        Assert.True(probe1.IsReady);
        Assert.Equal(AiProbeStatus.Ready, probe1.Status);

        var status1 = setupService.GetStatus();
        var aiStep1 = status1.Steps.Single(s => s.Id == "ai");
        Assert.True(aiStep1.Done);
        Assert.Contains("探活成功", aiStep1.Detail);
        Assert.Equal(1, handler.CallCount);

        // 第二次請求刷新探活（在 60 秒內），快取命中
        var probe2 = await setupService.RefreshProbeAsync();
        Assert.True(probe2.IsReady);
        Assert.Equal(AiProbeStatus.Ready, probe2.Status);

        var status2 = setupService.GetStatus();
        var aiStep2 = status2.Steps.Single(s => s.Id == "ai");
        Assert.True(aiStep2.Done);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AI3_兩個並發刷新_HTTP替身只收到一次()
    {
        var handler = new ProbeTestHttpMessageHandler
        {
            HandlerFunc = async (_, ct) =>
            {
                await Task.Delay(50, ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
                };
            }
        };
        using var client = new HttpClient(handler);
        var probeService = new AiProbeService(_settings, client);

        var task1 = probeService.RefreshAsync();
        var task2 = probeService.RefreshAsync();
        var results = await Task.WhenAll(task1, task2);

        Assert.True(results[0].IsReady);
        Assert.True(results[1].IsReady);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AI4_handler永不自行完成_使用可注入的短逾時_合理時間內回Failed且不擲出到SetupController()
    {
        var handler = new ProbeTestHttpMessageHandler
        {
            HandlerFunc = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        var probeService = new AiProbeService(_settings, client);
        var setupService = CreateService(probeService);
        var controller = new SetupController(setupService, new RecordingAuditService());

        var sw = Stopwatch.StartNew();
        // SetupController.Status 會在取狀態前先呼叫 RefreshProbeAsync
        var actionResult = await controller.Status(CancellationToken.None);
        sw.Stop();

        Assert.NotNull(actionResult);
        Assert.True(actionResult.Success);
        Assert.NotNull(actionResult.Data);

        var aiStep = actionResult.Data.Steps.Single(s => s.Id == "ai");
        Assert.False(aiStep.Done);
        Assert.Equal(AiProbeStatus.Failed, probeService.LatestResult.Status);
        Assert.Contains("逾時", probeService.LatestResult.Detail);
        Assert.Equal(1, handler.CallCount);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"探活應於短逾時後返回，實耗 {sw.ElapsedMilliseconds} ms");
    }

    // ── 郵件條件測試 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Mail1_郵件已啟用且SMTP非空_但全部觸發關閉_未完成()
    {
        _settings.Update(s =>
        {
            s.MailEnabled = true;
            s.SmtpServer = "smtp.corp.local";
            s.MailDailyEnabled = false;
            s.MailWeeklyEnabled = false;
            s.MailUrgentEnabled = false;
            s.MailOnRunCompleted = false;
            s.MailNotifyWorkOrders = false;
            s.MailRecipients = new List<string> { "admin@corp.local" };
        });

        var setupService = CreateService(new FakeAiProbeService());
        var status = setupService.GetStatus();
        var mailStep = status.Steps.Single(s => s.Id == "mail");

        Assert.False(mailStep.Done);
        Assert.Contains("觸發", mailStep.Detail);
    }

    [Fact]
    public void Mail2_郵件已啟用且有觸發_但收件人為空或皆無效_未完成()
    {
        _settings.Update(s =>
        {
            s.MailEnabled = true;
            s.SmtpServer = "smtp.corp.local";
            s.MailDailyEnabled = true;
            s.MailRecipients = new List<string> { "   ", "invalid-email-no-at" };
        });

        var setupService = CreateService(new FakeAiProbeService());
        var status = setupService.GetStatus();
        var mailStep = status.Steps.Single(s => s.Id == "mail");

        Assert.False(mailStep.Done);
        Assert.Contains("收件人", mailStep.Detail);
    }

    [Fact]
    public void Mail3_郵件已啟用SMTP非空有觸發且至少一位有效收件人_完成()
    {
        _settings.Update(s =>
        {
            s.MailEnabled = true;
            s.SmtpServer = "smtp.corp.local";
            s.MailWeeklyEnabled = true;
            s.MailRecipients = new List<string> { "   ", "secops@corp.local  " };
        });

        var setupService = CreateService(new FakeAiProbeService());
        var status = setupService.GetStatus();
        var mailStep = status.Steps.Single(s => s.Id == "mail");

        Assert.True(mailStep.Done);
        Assert.Contains("收件人皆已就緒", mailStep.Detail);
    }

    // ── PRTG 既有邏輯驗證 ─────────────────────────────────────────────────────────

    [Fact]
    public void PRTG_既有判定_啟用且最後同步成功且MapOk大於零才算完成()
    {
        var setupService = CreateService(new FakeAiProbeService());

        // 1. 未啟用
        _settings.Update(s => s.PrtgEnabled = false);
        Assert.False(setupService.GetStatus().Steps.Single(s => s.Id == "prtg").Done);

        // 2. 啟用但無同步紀錄
        _settings.Update(s => s.PrtgEnabled = true);
        Assert.False(setupService.GetStatus().Steps.Single(s => s.Id == "prtg").Done);

        // 3. 啟用且同步失敗
        _prtgSyncStore.Update(s =>
        {
            s.CompletedAt = DateTime.Now;
            s.Success = false;
            s.MapOk = 5;
        });
        Assert.False(setupService.GetStatus().Steps.Single(s => s.Id == "prtg").Done);

        // 4. 啟用且同步成功但 MapOk == 0
        _prtgSyncStore.Update(s =>
        {
            s.CompletedAt = DateTime.Now;
            s.Success = true;
            s.MapOk = 0;
        });
        Assert.False(setupService.GetStatus().Steps.Single(s => s.Id == "prtg").Done);

        // 5. 啟用且同步成功且 MapOk > 0
        _prtgSyncStore.Update(s =>
        {
            s.CompletedAt = DateTime.Now;
            s.Success = true;
            s.MapOk = 2;
        });
        Assert.True(setupService.GetStatus().Steps.Single(s => s.Id == "prtg").Done);
    }

    // ── 單次 status 不可為同一設定 blob 額外重讀 ───────────────────────────────────

    [Fact]
    public void Status_單次呼叫_不額外重讀SystemSettingsBlob()
    {
        int getCount = 0;
        var countingSettings = new CountingSettingsStore(_settings, () => getCount++);
        var appSettings = new WebAppSettings
        {
            Auth = new AuthSettings
            {
                Provider = "Stub",
                ServerAdmin = new ServerAdminSettings()
            }
        };
        var freshness = new ScheduleFreshnessService(
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
            _scheduleOptions);
        var mailService = new MailNotificationService(
            countingSettings, new FakeSmtpMailSender(), _hosts, _users,
            _groups, _groupAccess, new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
            new MailNotifyStateStore(_backend.Blob("mail_notify_state")), freshness);
        var health = new HealthService(_backend, new SchedulerRunState(), _backend.TopIssueBackfiller(), mailService, freshness);
        var identity = new IdentityService(
            _users, _groups, _hosts, new StubAuthenticationProvider(),
            new ServerAdminAuthenticator(appSettings),
            new RecordingAuditService(), new UserCapabilityResolver(_groups, _hosts));

        var setupService = new SetupReadinessService(
            health, identity, countingSettings, _sentinels, _hosts, _groupAccess, _groups,
            _scheduleOptions, _stateStore, appSettings, _prtgSyncStore, new FakeAiProbeService());

        // 模擬升級情境（StepsVersion < 2），會觸發舊七步遷移檢查
        _stateStore.Update(s => s.StepsVersion = 1);

        getCount = 0;
        var status = setupService.GetStatus();

        Assert.NotNull(status);
        Assert.Equal(1, getCount);
    }
}
