using System.Reflection;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTrustedSamplingQualificationJobsControllerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-qual-jobs-api-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly HostStore hosts;
    private readonly SystemSettingsStore settings;
    private readonly PrtgMonitoringPolicyStore policy;
    private readonly WebHost host;

    public PrtgTrustedSamplingQualificationJobsControllerTests()
    {
        backend = new(new StorageSettings { Type = "Sqlite" }, root);
        hosts = new(backend.Blob("hosts"));
        settings = new(backend.Blob("system_settings"));
        settings.Update(value =>
        {
            value.PrtgEnabled = true; value.PrtgUrl = "https://source.example.test";
            value.PrtgAuthMode = PrtgAuthModes.Token; value.PrtgApiTokenEnc = "synthetic-api-contract";
            value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        host = hosts.Upsert(new WebHost { HostName = "qualification-job-api", IpAddress = "192.0.2.72", Active = true });
        policy = new(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value =>
        {
            value.Revision = "qualification-job-policy-r1"; value.CoreSystemId = "qualification-job-api";
            value.SourceGeneration = "qualification-job-source-r1";
            value.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.Get().PrtgUrl);
            value.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1); value.HostIds = [host.HostId]; value.SensorIds = [11, 12];
            value.SourceTimeZoneId = "UTC"; value.SourceCultureName = "en-US";
            value.RawTimestampTimeZoneId = "UTC"; value.AnalysisTimeZoneId = "UTC";
            value.TimeBasisEvidenceReference = "synthetic-job-api-time-basis";
        });
    }

    [Fact]
    public void MaintainPermissionFullVisibilityAndCaseOnlyScopeAreRequired()
    {
        var attributes = typeof(PrtgTrustedSamplingQualificationJobsController)
            .GetCustomAttributes<PermissionAttribute>(inherit: true).ToArray();
        var required = Assert.IsType<Capability[]>(Assert.Single(Assert.Single(attributes).Arguments!));
        Assert.Equal([Capability.Maintain], required);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/prtg/monitoring/trusted-sampling/qualification-jobs/contract";
        var action = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(action, new List<IFilterMetadata>());
        new PermissionFilter(required, new TestUser(maintain: false), new NoAudit()).OnAuthorization(filterContext);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(filterContext.Result).StatusCode);

        Assert.IsType<ForbidResult>(Controller(new Visibility([])).Contract());
        Assert.IsType<ForbidResult>(Controller(new Visibility([host.HostId], caseOnly: true)).Contract());
        Assert.IsType<OkObjectResult>(Controller(new Visibility([host.HostId])).Contract());
    }

    [Fact]
    public void StartAndResumeRejectOutOfRangeDurationAndAttemptCountsBeforeCreatingJob()
    {
        var controller = Controller(new Visibility([host.HostId]));
        Assert.IsType<BadRequestObjectResult>(controller.Start(
            new QualificationJobStartRequest(721, "settings-r1", "policy-r1", new string('A', 64), 1)));
        Assert.IsType<BadRequestObjectResult>(controller.Start(
            new QualificationJobStartRequest(72, "settings-r1", "policy-r1", new string('A', 64), 4)));
        Assert.IsType<BadRequestObjectResult>(controller.Resume("missing", new QualificationJobResumeRequest(1, 0, 1)));
        Assert.Null(new PrtgQualificationJobStateStore(backend).ReadCurrent());
    }

    [Fact]
    public async Task Enabled_qualification_pilot_waits_for_current_fenced_table_plan_before_any_source_call()
    {
        var controller = Controller(new Visibility([host.HostId]));

        var result = Assert.IsType<OkObjectResult>(await controller.Pilot(
            new QualificationPilotRequest([11]), CancellationToken.None));
        var response = Assert.IsType<ApiResponse<QualificationPilotResult>>(result.Value);
        var pilot = Assert.IsType<QualificationPilotResult>(response.Data);

        Assert.Equal("waiting-capacity", pilot.Status);
        Assert.StartsWith("qualification-pilot-table-admission-", pilot.Reason);
        Assert.Empty(pilot.Rows);
        Assert.Null(new PrtgQualificationCapacityPilotStore(backend).Read());
    }

    [Fact]
    public void PageRejectsNegativeOrOversizedCursorAndHidesOutOfScopeJob()
    {
        var now = DateTimeOffset.UtcNow;
        var visibleController = Controller(new Visibility([host.HostId]));
        var scopeFingerprint = Contract(visibleController).ScopeFingerprint;
        var job = new PrtgQualificationJobStateStore(backend).Start(Guid.NewGuid().ToString("N"), "owner",
            scopeFingerprint, "qualification-job-source-r1", "settings-r1", "policy-r1",
            new string('B', 64), 1, 0, 72, now, 1);
        Assert.IsType<BadRequestObjectResult>(visibleController.Page(job.JobId, -1, 100));
        Assert.IsType<BadRequestObjectResult>(visibleController.Page(job.JobId, 0, 101));
        Assert.IsType<ForbidResult>(Controller(new Visibility([])).Page(job.JobId, 0, 100));
    }

    [Fact]
    public void CurrentPageAndCancelRefuseAnOldWiderScopeAfterPolicyShrinks()
    {
        var visibleController = Controller(new Visibility([host.HostId]));
        var contractResponse = Assert.IsType<ApiResponse<QualificationJobContract>>(
            Assert.IsType<OkObjectResult>(visibleController.Contract()).Value);
        var oldWideScope = Assert.IsType<QualificationJobContract>(contractResponse.Data).ScopeFingerprint;
        var jobs = new PrtgQualificationJobStateStore(backend);
        var job = jobs.Start(Guid.NewGuid().ToString("N"), "owner",
            oldWideScope, "qualification-job-source-r1", "settings-r1", "policy-r1",
            new string('B', 64), 2, 0, 72, DateTimeOffset.UtcNow, 1);

        policy.Update(value => value.SensorIds = [11]);

        var current = Assert.IsType<ConflictObjectResult>(visibleController.Current());
        Assert.Equal("stale_job_scope", Assert.IsType<ApiResponse<object>>(current.Value).Error!.Code);
        var page = Assert.IsType<ConflictObjectResult>(visibleController.Page(job.JobId, 0, 100));
        Assert.Equal("stale_job_scope", Assert.IsType<ApiResponse<object>>(page.Value).Error!.Code);
        var cancel = Assert.IsType<ConflictObjectResult>(visibleController.Cancel(job.JobId,
            new QualificationJobCancelRequest(job.Version)));
        Assert.Equal("stale_job_scope", Assert.IsType<ApiResponse<object>>(cancel.Value).Error!.Code);
        Assert.Equal("initializing", jobs.ReadCurrent()!.Status);
    }

    [Fact]
    public void StartCannotReplaceAnActiveOldWideJobButCanReplaceItAfterWorkerClosesIt()
    {
        var controller = Controller(new Visibility([host.HostId]));
        var originalContract = Contract(controller);
        var jobs = new PrtgQualificationJobStateStore(backend);
        var active = jobs.Start(Guid.NewGuid().ToString("N"), "owner",
            originalContract.ScopeFingerprint, "qualification-job-source-r1", "settings-r1", "policy-r1",
            new string('B', 64), 2, 0, 72, DateTimeOffset.UtcNow, 1);

        policy.Update(value => value.SensorIds = [11]);
        var narrowedContract = Contract(controller);
        var request = new QualificationJobStartRequest(72, settings.Get().Revision, policy.Get().Revision,
            narrowedContract.ScopeFingerprint, 1);
        var conflict = Assert.IsType<ConflictObjectResult>(controller.Start(request));
        Assert.Equal("qualification_job_active", Assert.IsType<ApiResponse<object>>(conflict.Value).Error!.Code);
        var unchanged = Assert.IsType<PrtgQualificationJobStateStore.Job>(jobs.ReadCurrent());
        Assert.Equal(active.JobId, unchanged.JobId);
        Assert.Equal(active.Version, unchanged.Version);
        Assert.Equal(active.ScopeFingerprint, unchanged.ScopeFingerprint);
        Assert.Equal("initializing", unchanged.Status);

        var releasedAt = DateTimeOffset.UtcNow;
        jobs.ReleaseLease(active.JobId, active.Owner, active.Version, releasedAt);
        Assert.True(jobs.TryAcquireLease("qualification-worker-test", releasedAt.AddSeconds(1), TimeSpan.FromMinutes(3),
            out var workerJob));
        var leased = Assert.IsType<PrtgQualificationJobStateStore.Job>(workerJob);
        jobs.SetStatus(leased.JobId, leased.Owner, leased.Version, releasedAt.AddSeconds(2),
            "failed-stale", "source-context-changed", releaseQuota: true);
        PublishCurrentPlan();
        var accepted = Assert.IsType<AcceptedResult>(controller.Start(request));
        var response = Assert.IsType<ApiResponse<PrtgQualificationStartResult>>(accepted.Value);
        Assert.Equal("initializing", Assert.IsType<PrtgQualificationStartResult>(response.Data).Job!.Status);
        Assert.NotEqual(active.JobId, jobs.ReadCurrent()!.JobId);
    }

    private static QualificationJobContract Contract(PrtgTrustedSamplingQualificationJobsController controller) =>
        Assert.IsType<QualificationJobContract>(Assert.IsType<ApiResponse<QualificationJobContract>>(
            Assert.IsType<OkObjectResult>(controller.Contract()).Value).Data);

    private void PublishCurrentPlan()
    {
        var transport = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(
            settings.Get(), policy.Get(), hosts.CapturePrtgSnapshot());
        var now = DateTimeOffset.UtcNow;
        var settingsRevision = settings.Get().Revision;
        var policyRevision = policy.Get().Revision;
        var plan = new PrtgCapacityAdmissionPlan(new string('A', 64), transport.SourceFingerprint,
            new string('C', 64), transport.ScopeFingerprint, transport.StrategyFingerprint,
            new string('D', 64), transport.RequestShapeFingerprint, transport.VersionFingerprint,
            settingsRevision, policyRevision, 0.1, 0.1, 0.1, now, now.AddHours(1), "test-plan-owner", 1);
        new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey))
            .Publish(plan, "test-plan-owner", now, TimeSpan.FromHours(1), settingsRevision, () => true);
    }

    private PrtgTrustedSamplingQualificationJobsController Controller(IVisibilityService visibility) =>
        new(backend, hosts, visibility, new TestUser());

    private sealed class TestUser : ICurrentUser
    {
        public TestUser(bool maintain = true) => Capabilities = maintain
            ? new HashSet<Capability> { Capability.Maintain }
            : new HashSet<Capability>();
        public bool IsAuthenticated => true;
        public long UserId => 71;
        public string Account => "qualification-job-maintainer";
        public string DisplayName => "Qualification Job Maintainer";
        public IReadOnlySet<Capability> Capabilities { get; }
        public bool IsServerAdmin => false;
        public bool Has(Capability capability) => Capabilities.Contains(capability);
    }

    private sealed class Visibility(IEnumerable<long> visible, bool caseOnly = false) : IVisibilityService
    {
        private readonly HashSet<long> allowed = visible.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIds() => allowed;
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => allowed;
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => allowed;
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long hostId) { if (!allowed.Contains(hostId)) throw new UnauthorizedAccessException(); }
        public IReadOnlyList<string> GetCaseGrantHostNames() => caseOnly ? ["qualification-job-api"] : [];
        public bool IsCaseGrantOnly(long hostId) => caseOnly && allowed.Contains(hostId);
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => allowed;
    }

    private sealed class NoAudit : IAuditService
    {
        public void Record(string action, string summary, string? targetKind = null, string? targetId = null,
            object? detail = null, AuditResult result = AuditResult.Ok) { }
        public void RecordAuth(string action, string account, long? userId, string summary, AuditResult result) { }
        public void RecordSystem(string action, string summary, string? targetKind = null, string? targetId = null) { }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
