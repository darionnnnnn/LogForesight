using System.Security.Claims;
using System.Text.RegularExpressions;
using LogForesight.Core;
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
using Xunit;

namespace LogForesight.Tests;

[Collection("PrtgSnapshotSharedBudget")]
public class PrtgRunsCardAndProbeTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly FakeSystemSettingsStore _settingsStore;
    private readonly RecordingAuditService _audit = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _groups = new();
    private readonly FakeSentinelStore _sentinels = new();

    public PrtgRunsCardAndProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-test-prtg-runs-card-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "test.db")}" },
            _dir);
        _settingsStore = new FakeSystemSettingsStore();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    // ── 驗收點 1：/runs PRTG 卡清理與按鈕樣式 ──────────────────────────────

    [Fact]
    public void RunsCshtml不含啟動類按鈕_且runsJs中btnPrimary只出現在立即執行相關處()
    {
        var root = FindRepoRoot();
        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Runs.cshtml");
        Assert.True(File.Exists(cshtmlPath), $"找不到 {cshtmlPath}");
        var cshtml = File.ReadAllText(cshtmlPath);

        // Runs.cshtml PRTG 卡不含「開始回填」與「同步結構」啟動按鈕
        Assert.DoesNotContain("prtg-backfill-start", cshtml);
        Assert.DoesNotContain("prtg-sync-start", cshtml);
        Assert.DoesNotContain("id=\"prtg-structure-sync-start\"", cshtml);
        Assert.DoesNotContain("id=\"prtg-backfill-output-wrap\"", cshtml);
        Assert.DoesNotContain("id=\"prtg-backfill-output-toggle\"", cshtml);
        Assert.DoesNotContain("id=\"prtg-backfill-copy\"", cshtml);

        // 卡片底部包含連結至 /admin/prtg 的提示文字
        Assert.Contains("admin/prtg", cshtml);
        Assert.Contains("前往 PRTG 維護", cshtml);

        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        Assert.True(File.Exists(jsPath), $"找不到 {jsPath}");
        var js = File.ReadAllText(jsPath);

        // 以正則找出 runs.js 中所有出現 btn-primary 的行與所在函式
        var matches = Regex.Matches(js, @"(?m)^.*btn-primary.*$");
        Assert.NotEmpty(matches);

        var allowedFunctions = new List<string>();
        foreach (Match m in matches)
        {
            var lineIndex = m.Index;
            // 往回找最近的 function 定義
            var prevCode = js[..lineIndex];
            var funcMatch = Regex.Match(prevCode, @"function\s+([a-zA-Z0-9_]+)\s*\(", RegexOptions.RightToLeft);
            var funcName = funcMatch.Success ? funcMatch.Groups[1].Value : "global";
            allowedFunctions.Add(funcName);

            // 斷言該處確實是「立即執行」相關函式
            Assert.Contains(funcName, new[] { "confirmRunWithPrtgValues" });
        }

        // 確保只有 confirmRunWithPrtgValues 包含 btn-primary
        Assert.All(allowedFunctions, fn => Assert.Equal("confirmRunWithPrtgValues", fn));
    }

    // ── 驗收點 2：共用標籤文字函式 ────────────────────────────────────────

    [Fact]
    public void PrtgScopeLabels匯出新文字函式_且runsJs與prtgAdminJs皆import並呼叫()
    {
        var root = FindRepoRoot();
        var labelsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "core", "prtg-scope-labels.js");
        Assert.True(File.Exists(labelsJsPath), $"找不到 {labelsJsPath}");
        var labelsJs = File.ReadAllText(labelsJsPath);

        Assert.Contains("export function prtgScopeInapplicableText", labelsJs);

        var runsJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "runs.js");
        var runsJs = File.ReadAllText(runsJsPath);
        // 執行卡透過共用模組標籤呼叫，不能為了靜態測試再重複一次判定。
        Assert.Contains("prtgModuleStateText(enabled, scope, strategy)", runsJs);
        Assert.Contains("prtgScopeInapplicableText(true)", labelsJs);

        var prtgAdminJsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        var prtgAdminJs = File.ReadAllText(prtgAdminJsPath);
        Assert.Contains("prtgScopeInapplicableText", prtgAdminJs);
        Assert.Matches(@"import\s*\{[^}]*prtgScopeInapplicableText[^}]*\}\s*from", prtgAdminJs);
        Assert.Contains("prtgScopeInapplicableText(", prtgAdminJs);
    }

    // ── 驗收點 3：設定 DTO 的 ValueFetchScopeApplies ───────────────────────

    [Fact]
    public void 設定DTO_保守策略ValueFetchScopeApplies為false_激進為true()
    {
        var store = new FakeSystemSettingsStore();
        var service = new SystemSettingsService(store, FakeCurrentUser.WithCapabilities(), _audit, new FakeUserStore(),
            new LogForesight.Web.Services.Mail.MailNotificationService(store, new FakeSmtpMailSender(), _hosts, new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
                new MailNotifyStateStore(_backend.Blob("mail_notify_state")),
                new ScheduleFreshnessService(new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
                    new ScheduleOptionsStore(_backend.Blob("schedule_options")))), new FakeReportUsageQuery());

        store.Update(s =>
        {
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        var conservativeDto = service.Get();
        Assert.False(conservativeDto.ValueFetchScopeApplies);

        store.Update(s =>
        {
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
        });
        var aggressiveDto = service.Get();
        Assert.True(aggressiveDto.ValueFetchScopeApplies);
    }

    // ── 驗收點 4：快照服務 RequestScopeRefresh 在一個 PollInterval 內執行補抓 ──

    private sealed class ScopeRefreshTrackingSnapshotService : PrtgSnapshotHostedService
    {


        public ScopeRefreshTrackingSnapshotService(
            ISystemSettingsStore settingsStore,
            StorageBackend backend,
            IHostStore hostStore,
            ISentinelStore sentinelStore,
            PrtgProbeRunState probeState,
            TimeSpan pollInterval)
            : base(
                settingsStore,
                backend,
                new SchedulerRunState(),
                new PrtgStructureSyncService(settingsStore, backend, new PrtgStructureSyncRunState(), new SchedulerRunState(), hostStore, new PrtgStructureSyncStatusStore(backend.Blob("sync_status")), new PrtgBackfillRunState(), sentinelStore, new DataVersionStamp(), new FakeHostApplicationLifetime()),
                new PrtgBackfillService(settingsStore, backend, new PrtgBackfillRunState(), probeState, hostStore, new SchedulerRunState(), new PrtgStructureSyncRunState(), sentinelStore, null!),
                hostStore,
                sentinelStore,
                probeState,
                new FakeHostApplicationLifetime(),
                pollInterval)
        {
        }

    }

    private sealed class ScopeRefreshHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource<bool> MessagesRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = request.RequestUri!.Query;
            lock (Requests) Requests.Add(query);
            var messages = query.Contains("content=messages");
            var device21 = Regex.IsMatch(query, @"[?&]id=21(&|$)");
            // The pre-existing device 20 can be drained first; wait for the actual new-device handoff.
            if (messages && device21) MessagesRequested.TrySetResult(true);
            var snapshot = query.Contains("columns=objid,lastvalue,interval,lastcheck,status", StringComparison.Ordinal);
            var lastCheck = DateTimeOffset.UtcNow.ToString("O");
            var json = messages
                ? "{\"treesize\":0,\"messages\":[]}"
                : snapshot
                    ? $"{{\"treesize\":1,\"sensors\":[{{\"objid\":201,\"parentid\":20,\"sensor\":\"Old sensor name\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"lastcheck\":\"{lastCheck}\"}}]}}"
                    : device21
                        ? "{\"treesize\":1,\"sensors\":[{\"objid\":211,\"parentid\":21,\"sensor\":\"S\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false}]}"
                        : "{\"treesize\":0,\"sensors\":[]}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 快照服務補抓感測器及狀態變更_忙碌時保留請求(bool initiallyBusy)
    {
        var shortInterval = TimeSpan.FromMilliseconds(300);
        var probeState = new PrtgProbeRunState();
        var service = new ScopeRefreshTrackingSnapshotService(_settingsStore, _backend, _hosts, _sentinels, probeState, shortInterval);
        // This fixture exercises snapshot/backfill and queue handoff. The independent timeline
        // worker has its own source client, so isolate it instead of contacting prtg.example.
        service.TimelineTick = _ => Task.FromResult(new PrtgSensorTimelineTick(
            TimeSpan.FromSeconds(5), false, "scope-refresh-owned-fixture"));

        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "https://prtg.example";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("token");
            s.PrtgSensorTypeWhitelist = ["ping"];
        });

        var host = _hosts.Upsert(new WebHost { HostName = "Server-20", IpAddress = "192.0.2.20", Active = true });
        var missingSensorHost = _hosts.Upsert(new WebHost { HostName = "Server-21", IpAddress = "192.0.2.21", Active = true });
        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today, new[]
        {
            new PrtgHostMapRow { DeviceObjid = 20, HostId = host.HostId, HostName = host.HostName, MapStatus = PrtgMapStatus.Ok, MapDate = DateTime.Today },
            new PrtgHostMapRow { DeviceObjid = 21, HostId = missingSensorHost.HostId, HostName = missingSensorHost.HostName, MapStatus = PrtgMapStatus.Ok, MapDate = DateTime.Today }
        });
        // Device 20 supplies the already mirrored target used by admission. Device 21 is mapped
        // and active but has no sensor rows, so the scope refresh must perform a real backfill.
        _backend.PrtgStore().UpsertSensors(new[]
        {
            new PrtgSensorRow
            {
                Objid = 201, DeviceObjid = 20, Name = "Old sensor name", SensorType = "ping",
                Status = "Up", Paused = false
            }
        }, DateTime.Now);
        using var handler = new ScopeRefreshHandler();
        SnapshotAdmissionTestFixture.Seed(_backend, _hosts, _settingsStore, host.HostId, 20, (201, "ping"));
        service.ClientFactory = () => new PrtgClient("https://prtg.example", "token", 30, true, handler, PrtgAuthModes.Token, "", "", "");
        using var ackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (initiallyBusy)
        {
            Assert.True(probeState.TryBeginRun(out _));
            await service.ScopeRefreshTickAsync();
            Assert.Empty(handler.Requests);
            probeState.FinishRun(success: true, cancelled: false);
        }
        else service.RequestScopeRefresh();
        await service.StartAsync(CancellationToken.None);
        try
        {
            string FailureDiagnostics(string stage)
            {
                var status = service.GetStatus();
                string[] requests;
                lock (handler.Requests) requests = handler.Requests.ToArray();
                var queue = _backend.PrtgStore().ReadRecentStateChangeQueue().Select(item =>
                    $"device={item.DeviceObjid},from={item.FromLocalDate:O},to={item.ToLocalDate:O},attempts={item.Attempts},completed={item.CompletedAtUtc:O}");
                return $"{stage}; SnapshotStatus={status}; Outputs=[{string.Join(" || ", service.ExecutionOutputs)}]; " +
                    $"Requests=[{string.Join(" || ", requests)}]; Queue=[{string.Join(" || ", queue)}]";
            }

            try
            {
                await handler.MessagesRequested.Task.WaitAsync(ackDeadline.Token);
            }
            catch (OperationCanceledException) when (ackDeadline.IsCancellationRequested)
            {
                throw new Xunit.Sdk.XunitException(
                    FailureDiagnostics("Message request was not reached within the fixed 5-second deadline"));
            }
            var mirroredSensors = _backend.PrtgStore().GetAllSensors();
            Assert.Contains(mirroredSensors, sensor =>
                sensor.Objid == 201 && sensor.DeviceObjid == 20 && sensor.Name == "Old sensor name");
            Assert.Contains(mirroredSensors, sensor =>
                sensor.Objid == 211 && sensor.DeviceObjid == 21 && sensor.Name == "S");
            string[] requests;
            lock (handler.Requests) requests = handler.Requests.ToArray();
            Assert.Contains(requests, query => query.Contains("columns=objid,lastvalue,interval,lastcheck,status", StringComparison.Ordinal) &&
                Regex.IsMatch(query, @"[?&]filter_objid=201(&|$)"));
            Assert.Contains(requests, query => query.Contains("content=sensors", StringComparison.Ordinal) &&
                Regex.IsMatch(query, @"[?&]id=21(&|$)"));
            Assert.DoesNotContain(requests, query => query.Contains("content=sensors", StringComparison.Ordinal) &&
                Regex.IsMatch(query, @"[?&]id=20(&|$)"));
            Assert.Contains(requests, query => query.Contains("content=messages", StringComparison.Ordinal) &&
                Regex.IsMatch(query, @"[?&]id=21(&|$)"));
            Assert.All(requests.Where(query => Regex.IsMatch(query, @"[?&]id=\d+(&|$)")), query =>
                Assert.Matches(@"[?&]id=(20|21)(&|$)", query));
            PrtgRecentStateChangeQueueItem handoff;
            do
            {
                var queueItems = _backend.PrtgStore().ReadRecentStateChangeQueue();
                Assert.Equal(new long[] { 20, 21 }, queueItems.Select(item => item.DeviceObjid).Order());
                handoff = Assert.Single(queueItems, item => item.DeviceObjid == 21);
                if (queueItems.All(item => item.CompletedAtUtc.HasValue)) break;
                try { await Task.Delay(TimeSpan.FromMilliseconds(20), ackDeadline.Token); }
                catch (OperationCanceledException) when (ackDeadline.IsCancellationRequested)
                {
                    throw new Xunit.Sdk.XunitException(FailureDiagnostics(
                        "Recent-state queue acknowledgement was not reached within the fixed 5-second deadline"));
                }
            } while (true);
            Assert.NotNull(handoff.CompletedAtUtc);
            Assert.Equal(21, handoff.DeviceObjid);
            Assert.All(_backend.PrtgStore().ReadRecentStateChangeQueue(), item =>
            {
                Assert.NotNull(item.CompletedAtUtc);
                Assert.Equal(DateTime.Today.AddDays(-1), item.FromLocalDate);
                Assert.Equal(DateTime.Today, item.ToLocalDate);
            });
            Assert.Equal(DateTime.Today.AddDays(-1), handoff.FromLocalDate);
            Assert.Equal(DateTime.Today, handoff.ToLocalDate);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }
    // ── 驗收點 5：HostAdminService 呼叫 RequestScopeRefresh 的條件 ────────

    private sealed class TrackingSnapshotService : PrtgSnapshotHostedService
    {
        public int Calls { get; private set; }

        public TrackingSnapshotService(StorageBackend backend, IHostStore hostStore)
            : base(
                new FakeSystemSettingsStore(),
                backend,
                new SchedulerRunState(),
                new PrtgStructureSyncService(new FakeSystemSettingsStore(), backend, new PrtgStructureSyncRunState(), new SchedulerRunState(), hostStore, new PrtgStructureSyncStatusStore(backend.Blob("sync_status")), new PrtgBackfillRunState(), new FakeSentinelStore(), new DataVersionStamp(), new FakeHostApplicationLifetime()),
                new PrtgBackfillService(new FakeSystemSettingsStore(), backend, new PrtgBackfillRunState(), new PrtgProbeRunState(), hostStore, new SchedulerRunState(), new PrtgStructureSyncRunState(), new FakeSentinelStore(), null!),
                hostStore,
                new FakeSentinelStore(),
                new PrtgProbeRunState(),
                new FakeHostApplicationLifetime())
        {
        }

        public void Reset() => Calls = 0;

        public override void RequestScopeRefresh()
        {
            Calls++;
            base.RequestScopeRefresh();
        }
    }

    private sealed class NoopRefresher : IPrtgHostMapRefresher
    {
        public string? WarningToReturn { get; set; }
        public string? TryRefreshToday() => WarningToReturn;
    }

    [Fact]
    public void 主機新增有IP的主機呼叫RequestScopeRefresh一次_只改名稱不改IP呼叫0次()
    {
        var snapshotService = new TrackingSnapshotService(_backend, _hosts);
        var mapRefresher = new NoopRefresher();

        var service = new HostAdminService(
            _hosts,
            _groups,
            new FakeUserStore(),
            new FakeNetiqServerCatalog("SENTINEL-A"),
            new FakeNetiqHostServiceForAdmin(),
            _audit,
            new UserDisplayNameService(new FakeSystemSettingsStore()),
            _backend.PrtgStore(),
            mapRefresher,
            _settingsStore,
            TestPermissionStamps.Shared,
            snapshotService);

        // 1. 新增有 IP 的主機 → RequestScopeRefresh 呼叫 1 次
        service.SaveHost(new SaveHostRequest
        {
            HostName = "SRV-IP-1",
            IpAddress = "192.168.1.100",
            Active = true,
            Os = "windows",
            Tier = "standard"
        });
        Assert.Equal(1, snapshotService.Calls);

        // 2. 只改名稱／角色描述，不改 IP → 呼叫 0 次
        snapshotService.Reset();
        service.SaveHost(new SaveHostRequest
        {
            HostName = "SRV-IP-1",
            IpAddress = "192.168.1.100",
            RoleDesc = "全新角色描述",
            Active = true,
            Os = "windows",
            Tier = "standard"
        });
        Assert.Equal(0, snapshotService.Calls);

        // 3. 變更 IP → 呼叫 1 次
        snapshotService.Reset();
        service.SaveHost(new SaveHostRequest
        {
            HostName = "SRV-IP-1",
            IpAddress = "192.168.1.101",
            Active = true,
            Os = "windows",
            Tier = "standard"
        });
        Assert.Equal(1, snapshotService.Calls);

        // 4. 新增無 IP 的主機 → 呼叫 0 次
        snapshotService.Reset();
        service.SaveHost(new SaveHostRequest
        {
            HostName = "SRV-NO-IP",
            IpAddress = null,
            Active = true,
            Os = "windows",
            Tier = "standard"
        });
        Assert.Equal(0, snapshotService.Calls);

        // 5. 停用主機改為啟用 → 呼叫 1 次
        service.SaveHost(new SaveHostRequest
        {
            HostName = "SRV-INACTIVE",
            IpAddress = null,
            Active = false,
            Os = "windows",
            Tier = "standard"
        });
        snapshotService.Reset();
        service.SaveHost(new SaveHostRequest
        {
            HostName = "SRV-INACTIVE",
            IpAddress = null,
            Active = true,
            Os = "windows",
            Tier = "standard"
        });
        Assert.Equal(1, snapshotService.Calls);
    }

    // ── 驗收點 6：探測取消端點與狀態 ────────────────────────────────────

    private SettingsController CreateSettingsController(PrtgProbeService probeService)
    {
        var controller = new SettingsController(
            new StubSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")),
            _audit,
            prtgProbe: probeService,
            backend: _backend);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test-admin"),
                new Claim(JwtTokenService.AccountClaim, "test-admin")
            }, "TestAuth"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private sealed class StubSystemSettingsService : ISystemSettingsService
    {
        public SystemSettingsDto Settings { get; set; } = new();
        public SystemSettingsDto Get() => Settings;
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void 探測執行中呼叫Cancel回200且狀態為已停止_沒有執行中回409()
    {
        var probeState = new PrtgProbeRunState();
        var backfillState = new PrtgBackfillRunState();
        var probeService = new PrtgProbeService(_settingsStore, probeState, backfillState, _backend, _hosts, _sentinels,
            new SchedulerRunState(), new PrtgStructureSyncRunState());
        var controller = CreateSettingsController(probeService);

        // 1. 沒有執行中呼叫 cancel → 回 409
        var ex = Assert.Throws<DomainException>(() => controller.CancelPrtgProbe());
        Assert.Equal(ApiErrorCodes.Conflict, ex.Code);

        // 2. 探測執行中呼叫 cancel → 回 200，且探測結束狀態為已停止
        Assert.True(probeState.TryBeginRun(out var ct));
        Assert.True(probeService.GetStatus().IsRunning);

        var response = controller.CancelPrtgProbe();
        Assert.NotNull(response);

        // 模擬背景探測捕捉到取消權杖後結束執行
        probeState.FinishRun(success: false, cancelled: true);

        var status = probeService.GetStatus();
        Assert.False(status.IsRunning);
        Assert.True(status.Cancelled);
        Assert.False(status.Success);
    }

    // ── 驗收點 7：相容性探測去識別 JSON 下載 UI 與邏輯 ──────────────────

    [Fact]
    public void PrtgCshtml_包含下載相容性探測JSON按鈕與去識別說明()
    {
        var root = FindRepoRoot();
        var cshtmlPath = Path.Combine(root, "LogForesight.Web", "Views", "Pages", "Prtg.cshtml");
        Assert.True(File.Exists(cshtmlPath), $"找不到 {cshtmlPath}");
        var cshtml = File.ReadAllText(cshtmlPath);

        // 應具備下載證據按鈕
        Assert.Contains("id=\"prtg-probe-download-evidence\"", cshtml);
        Assert.Contains("下載去識別相容性證據 (JSON)", cshtml);
        Assert.Contains("disabled", cshtml);

        // 應包含去識別安全說明
        Assert.Contains("去識別", cshtml);
        Assert.Contains("後端獨立保存", cshtml);
        Assert.Contains("完整探測文字檔可能含主機名稱、IP", cshtml);
    }

    [Fact]
    public void PrtgAdminJs_包含證據下載函式與按鈕互動()
    {
        var root = FindRepoRoot();
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "pages", "prtg-admin.js");
        Assert.True(File.Exists(jsPath), $"找不到 {jsPath}");
        var js = File.ReadAllText(jsPath);

        // 引用 evidence 工具模組
        Assert.Contains("extractProbeEvidenceJson", js);
        Assert.Contains("isProbeEvidenceDownloadable", js);

        // 綁定下載按鈕點擊與狀態
        Assert.Contains("prtg-probe-download-evidence", js);
        Assert.Contains("application/json", js);
        Assert.Contains("prtg-compatibility-evidence-", js);
        Assert.Contains("status.evidenceJson", js);
        Assert.DoesNotContain("extractProbeEvidenceJson(outputText)", js);
    }

    [Fact]
    public void PrtgProbeEvidenceJs_檔案存在且包含邊界防護邏輯()
    {
        var root = FindRepoRoot();
        var jsPath = Path.Combine(root, "LogForesight.Web", "wwwroot", "js", "core", "prtg-probe-evidence.js");
        Assert.True(File.Exists(jsPath), $"找不到 {jsPath}");
        var js = File.ReadAllText(jsPath);

        Assert.Contains("extractProbeEvidenceJson", js);
        Assert.Contains("isProbeEvidenceDownloadable", js);
        Assert.Contains("typeof evidenceJson !== 'string'", js);
        Assert.Contains("parsed.evidence_ready !== false", js);
        Assert.DoesNotContain("indexOf(beginMarker)", js);
    }

    [Fact]
    public void PrtgProbeRunState_證據欄獨立於文字且每趟重設()
    {
        var state = new PrtgProbeRunState();
        Assert.True(state.TryBeginRun(out _));
        state.AppendLine("BEGIN_PRTG_COMPATIBILITY_JSON {\"password\":\"SENTINEL_SECRET_9\"}");
        state.SetEvidenceJson("{\"schema_version\":\"1.0.0\",\"status\":\"partial\",\"evidence_ready\":false,\"targets\":[]}");
        state.FinishRun(true, false);
        Assert.Contains("SENTINEL_SECRET_9", string.Join("\n", state.Snapshot().Output));
        Assert.DoesNotContain("SENTINEL_SECRET_9", state.Snapshot().EvidenceJson);

        Assert.True(state.TryBeginRun(out _));
        Assert.Null(state.Snapshot().EvidenceJson);
        state.SetEvidenceJson("{\"schema_version\":\"1.0.0\",\"status\":\"partial\",\"evidence_ready\":false,\"targets\":[]}");
        Assert.True(state.TryCancel());
        Assert.Null(state.Snapshot().EvidenceJson);
        state.FinishRun(false, true);
        Assert.True(state.TryBeginRun(out _));
        Assert.Null(state.Snapshot().EvidenceJson);
        state.FinishRun(false, false);
    }
}
