using System.Security.Claims;
using System.Net;
using System.Text;
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
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Controller authorization for full and selected PRTG backfill operations.</summary>
public sealed class PrtgBackfillAuthorizationControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-prtg-backfill-auth-" + Guid.NewGuid().ToString("N"));
    private StorageBackend _backend = null!;
    private HostStore _hosts = null!;
    private SystemSettingsStore _settings = null!;
    private readonly RecordingAuditService _audit = new();

    public PrtgBackfillAuthorizationControllerTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "test.db")}"
        }, _directory);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        _hosts.Upsert(new WebHost { HostName = "auth-host-1", Active = true });
        _hosts.Upsert(new WebHost { HostName = "auth-host-2", Active = true });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Status_未授權的full回填不得讀取輸出且無執行時不顯示上一輪輸出()
    {
        var state = new PrtgBackfillRunState();
        Assert.True(state.TryBeginIdentifiedRun("full", out _));
        state.AppendLine("confidential-full-run-output");
        var partial = new FixedVisibility(_hosts, new HashSet<long> { _hosts.GetAll()[0].HostId });
        var controller = CreateController(state, partial);

        var denied = Assert.Throws<DomainException>(() => controller.GetPrtgBackfillStatus());
        Assert.Equal(ApiErrorCodes.Forbidden, denied.Code);

        state.FinishRun(success: true, cancelled: false);
        var idle = controller.GetPrtgBackfillStatus().Data!;
        Assert.False(idle.IsRunning);
        Assert.Empty(idle.Output);
        Assert.DoesNotContain("confidential-full-run-output", idle.Output);
    }

    [Fact]
    public void Status_fullScope可讀完成結果_selected完成後只讀自己的主機()
    {
        var hostIds = _hosts.GetAll().Select(host => host.HostId).ToArray();
        var fullState = new PrtgBackfillRunState();
        Assert.True(fullState.TryBeginIdentifiedRun("full", out _));
        fullState.AppendLine("completed-full-output");
        fullState.FinishRun(success: true, cancelled: false);
        var full = CreateController(fullState, new FixedVisibility(_hosts, hostIds.ToHashSet()));
        Assert.Contains("completed-full-output", full.GetPrtgBackfillStatus().Data!.Output);

        var selectedState = new PrtgBackfillRunState();
        var mutableSelectedHostIds = new List<long> { hostIds[0] };
        Assert.True(selectedState.TryBeginIdentifiedRun("selected", out _, mutableSelectedHostIds));
        mutableSelectedHostIds.Add(hostIds[1]);
        selectedState.AppendLine("selected-only-output");
        selectedState.FinishRun(success: true, cancelled: false);
        var selected = CreateController(selectedState, new FixedVisibility(_hosts, new HashSet<long> { hostIds[0] }));
        Assert.Equal(new HashSet<long> { hostIds[0] }, selectedState.GetAccessSnapshot().SelectedHostIds);
        Assert.Contains("selected-only-output", selected.GetPrtgBackfillStatus().Data!.Output);
        var outsider = CreateController(selectedState, new FixedVisibility(_hosts, new HashSet<long> { hostIds[1] }));
        Assert.Empty(outsider.GetPrtgBackfillStatus().Data!.Output);
        var caseOnly = CreateController(selectedState,
            new FixedVisibility(_hosts, new HashSet<long> { hostIds[0] }, new HashSet<long> { hostIds[0] }));
        Assert.Empty(caseOnly.GetPrtgBackfillStatus().Data!.Output);
    }

    [Fact]
    public void SelectedRun缺少或空白主機範圍時status與cancel都拒絕()
    {
        var visibleHostId = _hosts.GetAll()[0].HostId;
        foreach (var selectedHostIds in new IReadOnlyCollection<long>?[] { null, Array.Empty<long>() })
        {
            var state = new PrtgBackfillRunState();
            Assert.True(state.TryBeginIdentifiedRun("selected", out var token, selectedHostIds));
            var runId = state.GetRunIdentity().RunId!;
            var controller = CreateController(state, new FixedVisibility(_hosts, new HashSet<long> { visibleHostId }));

            Assert.Equal(ApiErrorCodes.Forbidden,
                Assert.Throws<DomainException>(() => controller.GetPrtgBackfillStatus()).Code);
            Assert.Equal(ApiErrorCodes.Forbidden,
                Assert.Throws<DomainException>(() => controller.CancelPrtgBackfill(
                    new CancelPrtgSelectedBackfillRequest { RunId = runId })).Code);
            Assert.False(token.IsCancellationRequested);
        }
    }

    [Fact]
    public void 新回填identity與progress重設以單一快照呈現()
    {
        var state = new PrtgBackfillRunState();
        Assert.True(state.TryBeginIdentifiedRun("full", out _));
        state.UpdateDay(8, 12, DateTime.UtcNow.Date);
        Assert.Equal(8, state.GetAccessSnapshot().Status.DaysDone);
        state.FinishRun(success: true, cancelled: false);

        Assert.True(state.TryBeginIdentifiedRun("selected", out _, [_hosts.GetAll()[0].HostId]));
        var snapshot = state.GetAccessSnapshot();
        Assert.Equal("selected", snapshot.RunKind);
        Assert.True(snapshot.Status.IsRunning);
        Assert.Equal(0, snapshot.Status.DaysDone);
        Assert.Equal(0, snapshot.Status.DaysTotal);
        Assert.Null(snapshot.Status.CurrentDate);
    }

    [Fact]
    public void SelectedPreview_partial與caseOnly拒絕_full與selectedHost可見時放行()
    {
        ConfigureSelectedBackfill();
        var hostIds = _hosts.GetAll().Select(host => host.HostId).ToArray();
        var request = Request(hostIds[0]);

        var partial = CreateController(new PrtgBackfillRunState(), new FixedVisibility(_hosts, new HashSet<long> { hostIds[1] }));
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => partial.PreviewSelectedPrtgBackfill(request)).Code);

        var caseOnly = CreateController(new PrtgBackfillRunState(),
            new FixedVisibility(_hosts, new HashSet<long> { hostIds[0] }, new HashSet<long> { hostIds[0] }));
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => caseOnly.PreviewSelectedPrtgBackfill(request)).Code);

        var selected = CreateController(new PrtgBackfillRunState(), new FixedVisibility(_hosts, new HashSet<long> { hostIds[0] }));
        var preview = selected.PreviewSelectedPrtgBackfill(request).Data!;
        Assert.Equal(new[] { hostIds[0] }, preview.HostIds);

        var full = CreateController(new PrtgBackfillRunState(), new FixedVisibility(_hosts, hostIds.ToHashSet()));
        Assert.Equal(new[] { hostIds[0] }, full.PreviewSelectedPrtgBackfill(request).Data!.HostIds);
    }

    [Fact]
    public async Task SelectedStart_未授權零副作用_已授權保存不可變主機範圍並能精確停止()
    {
        ConfigureSelectedBackfill();
        var hostId = _hosts.GetAll()[0].HostId;
        AddSelectedTarget(hostId);
        var request = Request(hostId);
        var rejectedState = new PrtgBackfillRunState();
        var rejectedHandler = new BlockingHandler();
        var rejectedCalls = 0;
        var rejected = CreateController(rejectedState, new FixedVisibility(_hosts, new HashSet<long> { _hosts.GetAll()[1].HostId }),
            settings => { Interlocked.Increment(ref rejectedCalls); return CreateClient(settings, rejectedHandler); });

        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => rejected.StartSelectedPrtgBackfill(request)).Code);
        Assert.Equal(0, rejectedCalls);
        Assert.False(rejectedState.Snapshot().IsRunning);
        Assert.Empty(_audit.Entries);

        var caseOnlyState = new PrtgBackfillRunState();
        var caseOnlyHandler = new BlockingHandler();
        var caseOnlyCalls = 0;
        var caseOnly = CreateController(caseOnlyState,
            new FixedVisibility(_hosts, new HashSet<long> { hostId }, new HashSet<long> { hostId }),
            settings => { Interlocked.Increment(ref caseOnlyCalls); return CreateClient(settings, caseOnlyHandler); });
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => caseOnly.StartSelectedPrtgBackfill(request)).Code);
        Assert.Equal(0, caseOnlyCalls);
        Assert.False(caseOnlyState.Snapshot().IsRunning);
        Assert.Empty(_audit.Entries);

        var state = new PrtgBackfillRunState();
        var handler = new BlockingHandler();
        var controller = CreateController(state, new FixedVisibility(_hosts, new HashSet<long> { hostId }),
            settings => CreateClient(settings, handler));
        var result = controller.StartSelectedPrtgBackfill(request).Data!;
        Assert.True(result.Started);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var access = state.GetAccessSnapshot();
        var run = access.RunId;
        var scope = access.SelectedHostIds;
        Assert.Equal(new HashSet<long> { hostId }, scope);
        Assert.True(controller.GetPrtgBackfillStatus().Data!.IsRunning);
        var selectedOutsider = CreateController(state,
            new FixedVisibility(_hosts, new HashSet<long> { _hosts.GetAll()[1].HostId }));
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => selectedOutsider.GetPrtgBackfillStatus()).Code);
        var selectedCaseOnly = CreateController(state,
            new FixedVisibility(_hosts, new HashSet<long> { hostId }, new HashSet<long> { hostId }));
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => selectedCaseOnly.GetPrtgBackfillStatus()).Code);

        var wrongIdentity = Assert.Throws<DomainException>(() => controller.CancelPrtgBackfill(
            new CancelPrtgSelectedBackfillRequest { RunId = Guid.NewGuid().ToString("N") }));
        Assert.Equal(ApiErrorCodes.Conflict, wrongIdentity.Code);
        Assert.True(state.Snapshot().IsRunning);
        var cancel = controller.CancelPrtgBackfill(new CancelPrtgSelectedBackfillRequest { RunId = run! });
        Assert.True(cancel.Success);
        await WaitForIdle(state);
    }

    [Fact]
    public void GenericCancel_只取消精確擷取的fullRun且必須fullNonCaseOnlyScope()
    {
        var hostIds = _hosts.GetAll().Select(host => host.HostId).ToArray();
        var state = new PrtgBackfillRunState();
        Assert.True(state.TryBeginIdentifiedRun("full", out var token));
        var partial = CreateController(state, new FixedVisibility(_hosts, new HashSet<long> { hostIds[0] }));
        Assert.Equal(ApiErrorCodes.Forbidden,
            Assert.Throws<DomainException>(() => partial.CancelPrtgBackfill()).Code);
        Assert.False(token.IsCancellationRequested);

        var full = CreateController(state, new FixedVisibility(_hosts, hostIds.ToHashSet()));
        Assert.True(full.CancelPrtgBackfill().Success);
        Assert.True(token.IsCancellationRequested);

        state.FinishRun(success: false, cancelled: true);
        Assert.True(state.TryBeginIdentifiedRun("selected", out var selectedToken, [hostIds[0]]));
        var selectedId = state.GetRunIdentity().RunId;
        state.FinishRun(success: false, cancelled: false);
        Assert.True(state.TryBeginIdentifiedRun("full", out var replacementToken));
        Assert.NotEqual(selectedId, state.GetRunIdentity().RunId);
        Assert.False(state.TryCancelIdentifiedRun("selected", selectedId));
        Assert.False(replacementToken.IsCancellationRequested);
        Assert.False(selectedToken.IsCancellationRequested);
    }

    private void ConfigureSelectedBackfill() => _settings.Update(settings =>
    {
        settings.PrtgSensorTypeWhitelist = ["diskfree"];
        settings.PrtgRetentionDays = 180;
        settings.RetentionDays = 180;
        settings.PrtgEnabled = true;
        settings.PrtgUrl = "http://127.0.0.1:1";
        settings.PrtgApiTokenEnc = "test-token";
    });

    private static PrtgSelectedBackfillRequest Request(long hostId)
    {
        var day = DateTime.Today.AddDays(-1);
        return new PrtgSelectedBackfillRequest { HostIds = [hostId], FromDate = day, ToDate = day };
    }

    private void AddSelectedTarget(long hostId)
    {
        var day = DateTime.Today.AddDays(-1);
        var store = _backend.PrtgStore();
        store.ReplaceHostMapForDate(day, [new PrtgHostMapRow { DeviceObjid = 100, HostId = hostId, MapStatus = PrtgMapStatus.Ok }]);
        store.UpsertSensors([new PrtgSensorRow { Objid = 1001, DeviceObjid = 100, Name = "Disk", SensorType = "diskfree" }], DateTime.Now);
    }

    private static PrtgClient CreateClient(SystemSettings settings, BlockingHandler handler) =>
        new(settings.PrtgUrl, "test-token", 30, true, handler, PrtgAuthModes.Token, "", "", "", new PrtgRequestBudget());

    private static async Task WaitForIdle(PrtgBackfillRunState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (state.Snapshot().IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.False(state.Snapshot().IsRunning, "Selected backfill did not finish after cancellation.");
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    private SettingsController CreateController(PrtgBackfillRunState runState, IVisibilityService visibility,
        Func<SystemSettings, PrtgClient>? clientFactory = null)
    {
        var scheduler = new SchedulerRunState();
        var structureState = new PrtgStructureSyncRunState();
        var structure = new PrtgStructureSyncService(_settings, _backend, structureState, scheduler,
            _hosts, new PrtgStructureSyncStatusStore(_backend.Blob(PrtgStructureSyncStatusStore.BlobKey)),
            runState, new FakeSentinelStore(), new DataVersionStamp());
        var service = new PrtgBackfillService(_settings, _backend, runState, new PrtgProbeRunState(),
            _hosts, scheduler, structureState, new FakeSentinelStore(), structure, clientFactory: clientFactory);
        var controller = new SettingsController(new FakeSystemSettingsService(),
            new AiUsageStore(_backend.Blob("ai_usage")), _audit, prtgBackfill: service,
            backend: _backend, hosts: _hosts, visibility: visibility,
            currentUser: FakeCurrentUser.WithCapabilities(Capability.Maintain));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("capability", nameof(Capability.Maintain))], "test"))
        }};
        return controller;
    }

    private sealed class FixedVisibility(IHostStore hosts, IReadOnlySet<long> visible, IReadOnlySet<long>? caseOnly = null)
        : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => visible;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long id) => visible;
        public IReadOnlySet<long> GetOwnedHostIdsFor(long id) => visible;
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long id) => visible;
        public IReadOnlyList<string> GetCaseGrantHostNames() => Array.Empty<string>();
        public bool IsCaseGrantOnly(long id) => caseOnly?.Contains(id) == true;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => IsCaseGrantOnly(id) ? new HashSet<string>() : null;
        public List<WebHost> GetVisibleHosts() => hosts.GetAll().Where(host => visible.Contains(host.HostId)).ToList();
        public void EnsureVisible(long hostId)
        {
            if (!visible.Contains(hostId)) throw DomainException.Forbidden("Host not visible.");
        }
    }
}
