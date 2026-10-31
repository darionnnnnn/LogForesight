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

    // ── 驗收點 1：/runs PRTG 卡清理與按鈕樣式 ──────────────────────────────

    // ── 驗收點 2：共用標籤文字函式 ────────────────────────────────────────

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
                    new ScheduleOptionsStore(_backend.Blob("schedule_options"))),
                new WorkOrderOutboxStore(_backend.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore()), new FakeReportUsageQuery());

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
        public readonly TaskCompletionSource<bool> FirstSamplingTickStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> ReleaseFirstSamplingTick = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockFirstSamplingTick { get; set; }
        private int _samplingTicks;

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

        internal override async Task TickAsync(CancellationToken ct = default)
        {
            if (BlockFirstSamplingTick && Interlocked.Increment(ref _samplingTicks) == 1)
            {
                FirstSamplingTickStarted.TrySetResult(true);
                await ReleaseFirstSamplingTick.Task.WaitAsync(ct);
            }
            await base.TickAsync(ct);
        }

    }

    private sealed class ScopeRefreshHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource<bool> MessagesRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> AnyMessagesRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> ResponseReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Requests = new();
        public readonly List<string> Responses = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = request.RequestUri!.Query;
            lock (Requests) Requests.Add(query);
            var messages = query.Contains("content=messages");
            var device21 = Regex.IsMatch(query, @"[?&]id=21(&|$)");
            if (messages) AnyMessagesRequested.TrySetResult(true);
            // The pre-existing device 20 can be drained first; wait for the actual new-device handoff.
            if (messages && device21) MessagesRequested.TrySetResult(true);
            var snapshot = query.Contains("columns=objid,lastvalue,interval,lastcheck,status,primarychannel", StringComparison.Ordinal);
            var lastCheck = DateTimeOffset.UtcNow.ToString("O");
            var json = messages
                ? "{\"treesize\":0,\"messages\":[]}"
                : snapshot
                    ? $"{{\"treesize\":1,\"sensors\":[{{\"objid\":201,\"parentid\":20,\"sensor\":\"Old sensor name\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false,\"lastvalue_raw\":10,\"interval\":\"60 s\",\"lastcheck\":\"{lastCheck}\"}}]}}"
                    : device21
                        ? "{\"treesize\":1,\"sensors\":[{\"objid\":211,\"parentid\":21,\"sensor\":\"S\",\"type\":\"ping\",\"status\":\"Up\",\"paused\":false}]}"
                        : "{\"treesize\":0,\"sensors\":[]}";
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            lock (Responses) Responses.Add(query);
            ResponseReturned.TrySetResult(true);
            return Task.FromResult(response);
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
        public string? TryApplyRuleUpgrade() => null;
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

    private PrtgBackfillAdminController CreateSettingsController(PrtgProbeService probeService)
    {
        var controller = PrtgControllerFactory.BackfillAdmin(_backend, _audit, probe: probeService);

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

}
