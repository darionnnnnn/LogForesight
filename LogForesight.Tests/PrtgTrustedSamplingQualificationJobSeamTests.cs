using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTrustedSamplingQualificationJobSeamTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-qual-seam-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly SystemSettingsStore settings;
    private readonly PrtgMonitoringPolicyStore policy;

    public PrtgTrustedSamplingQualificationJobSeamTests()
    {
        backend = new(new StorageSettings { Type = "Sqlite" }, root);
        settings = new(backend.Blob("system_settings"));
        settings.Update(value => { value.PrtgEnabled = true; value.PrtgUrl = "https://source.example";
            value.PrtgAuthMode = PrtgAuthModes.Token; value.PrtgApiTokenEnc = "synthetic-contract-only";
            value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative; });
        policy = new(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value => { value.Revision = "policy-r1"; value.CoreSystemId = "core";
            value.SourceGeneration = "source-r1";
            value.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.Get().PrtgUrl);
            value.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1); value.HostIds = [7]; value.SensorIds = [11];
            value.SourceTimeZoneId = "UTC"; value.SourceCultureName = "en-US";
            value.RawTimestampTimeZoneId = "UTC"; value.AnalysisTimeZoneId = "UTC";
            value.TimeBasisEvidenceReference = "synthetic-time-basis"; });
        backend.PrtgStore().UpsertSensors([new PrtgSensorRow { Objid = 11, DeviceObjid = 22, SensorType = "CPU" }], DateTime.UtcNow);
        backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
            [new PrtgHostMapRow { DeviceObjid = 22, HostId = 7, MapDate = DateTime.Today, MapStatus = PrtgMapStatus.Ok }]);
        backend.PrtgStore().BindObservedResource(11, 7, "source-r1",
            PrtgTimelineResourceIdentity.BuildResourceFingerprint("22", "CPU", "created", 0));
    }

    [Fact]
    public async Task SyntheticSaveQualificationTransactionalProofThenNormalRefreshResolverSeam_NoNativeSourceAcceptance()
    {
        // This synthetic response validates only application wiring. It is not a claim about the
        // native response shape or readiness of any deployed PRTG source.
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var strategyStore = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey));
        Assert.True(strategyStore.GetCurrent(currentPolicy, PrtgFetchStrategy.Conservative, 15,
            DateTime.UtcNow.AddHours(-2)).Ready);
        var authority = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            currentPolicy, PrtgFetchStrategy.Conservative, 15);
        var transport = PrtgProfileTransportCapacityPilot.BuildContract(backend, null,
            currentSettings, currentPolicy, [11]);
        var capacityAt = DateTimeOffset.UtcNow;
        var snapshotEstimate = new PrtgSnapshotCapacityEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            50, 1, 5, capacityAt, 1, 1, 600, .25, "qualified");
        var profileEstimate = new PrtgProfileTransportEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            1, 5, capacityAt, .1, .1, 62100, .25, "qualified");
        var jointEstimate = PrtgJointCapacityEvaluator.Evaluate(snapshotEstimate, profileEstimate,
            new PrtgRequestBudgetUsage(0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero),
            currentSettings.PrtgTimeoutSeconds);
        var admissionPlan = PrtgJointCapacityEvaluator.CreatePlan(jointEstimate, transport.SourceFingerprint,
            new string('F', 64), transport.ScopeFingerprint, transport.StrategyFingerprint,
            new string('7', 64), transport.RequestShapeFingerprint, transport.VersionFingerprint,
            capacityAt, currentSettings.Revision, currentPolicy.Revision) with
        { Owner = "qualification-seam-test", Version = 1, LeaseUntilUtc = capacityAt.AddHours(1) };
        var bindings = new PrtgTrustedSamplingBindingStore(backend);
        var binding = bindings.Save(new(11, currentSettings.Revision, currentPolicy.Revision,
            identity.Epoch, identity.ChannelGeneration, 0, "3", "Load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds",
            "UTC", "UTC", "synthetic-time-basis"));

        var jobs = new PrtgQualificationJobStateStore(backend);
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid().ToString("N");
        var job = jobs.Start(jobId, "synthetic-worker", new string('A', 64),
            currentPolicy.SourceGeneration, currentSettings.Revision, currentPolicy.Revision,
            authority, 1, 1, 72, now, 3);
        jobs.InitializePage(jobId, job.Owner, job.Version, now, 0,
            [new PrtgQualificationJobStateStore.Sensor { SensorObjid = 11,
                BindingRevision = binding.BindingRevision, BindingFingerprint = binding.BindingFingerprint }]);
        var (page, pageVersion) = jobs.ReadPageWithVersion(jobId, 0);
        var fence = new PrtgQualificationWriteFence(jobId, job.Owner, job.Version,
            new string('A', 64), currentPolicy.SourceGeneration, currentSettings.Revision,
            currentPolicy.Revision, authority, 0, pageVersion, 11,
            page.Sensors[0].BindingRevision, page.Sensors[0].BindingFingerprint);
        var budget = new PrtgRequestBudget();
        budget.SetAdmissionPlan(admissionPlan);
        budget.SetHistoricCoordinator(new SqlPrtgHistoricRequestCoordinator(backend));
        var qualifyingService = Service("synthetic-explicit-binding", budget);

        var row = Assert.Single(await qualifyingService.ProbeAsync([11], CancellationToken.None,
            allowQualification: true, requestPurpose: PrtgRequestPurpose.ProfileRefresh,
            admissionPlanFingerprint: admissionPlan.Fingerprint,
            expectedBindingRevision: binding.BindingRevision,
            expectedBindingFingerprint: binding.BindingFingerprint,
            qualificationOnly: true, qualificationFence: fence));

        Assert.Equal("qualified", row.Status);
        Assert.False(row.ProfileRecorded);
        Assert.True(row.QualificationRecorded);
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
        Assert.NotEmpty(Assert.IsType<PrtgTrustedSamplingBinding>(bindings.Get(11)).QualificationProofReference);
        Assert.Equal("qualified", jobs.ReadPage(jobId, 0).Sensors.Single().Status);
        Assert.Equal(1, jobs.ReadCurrent()!.Qualified);

        var refreshBudget = new PrtgRequestBudget();
        refreshBudget.SetAdmissionPlan(admissionPlan);
        refreshBudget.SetHistoricCoordinator(new SqlPrtgHistoricRequestCoordinator(backend));
        var refreshed = Assert.Single(await Service("synthetic-explicit-binding", refreshBudget)
            .ProbeAsync([11], CancellationToken.None, requestPurpose: PrtgRequestPurpose.ProfileRefresh,
                admissionPlanFingerprint: admissionPlan.Fingerprint));
        Assert.True(refreshed.ProfileRecorded);
        var profile = Assert.Single(backend.PrtgStore().GetTrustedSamplingProfiles([11]).Values);
        var identityAfter = backend.PrtgStore().GetResourceIdentity(11);
        var strategy = strategyStore.GetCurrent(currentPolicy, PrtgFetchStrategy.Conservative, 15, DateTime.UtcNow);
        var resolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, identityAfter,
            currentPolicy, 11, "CPU", strategy, DateTime.UtcNow, DateTime.UtcNow);
        Assert.True(resolution.Ready, resolution.RejectionReason);

        var completedAt = DateTimeOffset.UtcNow;
        jobs.SetStatus(jobId, job.Owner, job.Version, completedAt, "completed", "synthetic-wave-complete", releaseQuota: true);
        Assert.Throws<InvalidOperationException>(() => jobs.ResumeExisting(jobId, job.Version,
            "synthetic-resume-owner", job.ScopeFingerprint, "different-source", currentSettings.Revision,
            currentPolicy.Revision, authority, 0, 72, 1, completedAt.AddSeconds(1), TimeSpan.FromMinutes(3)));
        Assert.Equal(1, Assert.Single(jobs.ReadPage(jobId, 0).Sensors).Attempts);
        var resumed = jobs.ResumeExisting(jobId, job.Version, "synthetic-resume-owner",
            job.ScopeFingerprint, currentPolicy.SourceGeneration, currentSettings.Revision, currentPolicy.Revision,
            authority, 0, 72, 1, completedAt.AddSeconds(1), TimeSpan.FromMinutes(3));
        Assert.Equal(jobId, resumed.JobId);
        Assert.Equal(2, resumed.Wave);
        Assert.Equal(1, resumed.Attempts);
        Assert.Equal("running", resumed.Status);
        var resumedSensor = Assert.Single(jobs.ReadPage(jobId, 0).Sensors);
        Assert.Equal("qualified", resumedSensor.Status);
        Assert.Equal(1, resumedSensor.Attempts);
        Assert.True(resumedSensor.HasQualificationProof);
        Assert.Equal(Assert.IsType<PrtgTrustedSamplingBinding>(bindings.Get(11)).BindingFingerprint,
            resumedSensor.BindingFingerprint);

        // A source observation captured before lease expiry must not publish after the SQL
        // transaction starts. Exercise the real BindingStore path, not only the state-store guard.
        var currentBinding = Assert.IsType<PrtgTrustedSamplingBinding>(bindings.Get(11));
        var (_, currentPageVersion) = jobs.ReadPageWithVersion(jobId, 0);
        var delayedFence = new PrtgQualificationWriteFence(jobId, resumed.Owner, resumed.Version,
            resumed.ScopeFingerprint, resumed.SourceGeneration, resumed.SettingsRevision,
            resumed.PolicyRevision, resumed.AuthorityContextFingerprint, 0, currentPageVersion,
            11, currentBinding.BindingRevision, currentBinding.BindingFingerprint);
        var capturedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        backend.Blob(PrtgHistoricAdmissionStore.BlobKey).MutateWithContext((_, raw) =>
        {
            var overview = JsonSerializer.Deserialize<PrtgHistoricAdmissionStore.State>(raw!)!;
            overview.Job!.DeadlineUtc = expiredAt;
            overview.Job.LeaseUntilUtc = expiredAt;
            overview.ActiveQualification!.DeadlineUtc = expiredAt;
            overview.ActiveQualification.LeaseUntilUtc = expiredAt;
            return (JsonSerializer.Serialize(overview), true);
        }, PrtgHistoricAdmissionStore.MaximumJsonBytes, System.Data.IsolationLevel.Serializable);

        Assert.Throws<OperationCanceledException>(() => bindings.RecordQualification(11,
            currentBinding.BindingRevision, currentBinding.BindingFingerprint, currentSettings.Revision,
            currentPolicy.Revision, 93, capturedAt.UtcDateTime.AddSeconds(-1).ToOADate(),
            "synthetic-contract-only", capturedAt, new string('D', 64), delayedFence));
    }

    [Theory]
    [InlineData("prtg-client")]
    [InlineData("invalid-json")]
    public async Task HostedSliceRecordsBoundedSourceFailuresAndDoesNotRetryAfterWaveCap_NoNativeSourceAcceptance(
        string failureKind)
    {
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var authority = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            currentPolicy, PrtgFetchStrategy.Conservative, 15);
        var binding = new PrtgTrustedSamplingBindingStore(backend).Save(new(11, currentSettings.Revision,
            currentPolicy.Revision, identity.Epoch, identity.ChannelGeneration, 0, "3", "Load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds",
            "UTC", "UTC", "synthetic-time-basis"));
        var sourceContract = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(
            currentSettings, currentPolicy, new HostStore(backend.Blob("hosts")).CapturePrtgSnapshot());
        var jobs = new PrtgQualificationJobStateStore(backend);
        var now = DateTimeOffset.UtcNow;
        var job = jobs.Start(Guid.NewGuid().ToString("N"), "http-start-owner", sourceContract.ScopeFingerprint,
            currentPolicy.SourceGeneration, currentSettings.Revision, currentPolicy.Revision, authority,
            1, 1, 72, now, maximumAttempts: 1);
        jobs.ReleaseLease(job.JobId, job.Owner, job.Version, DateTimeOffset.UtcNow);

        var sourceCalls = 0;
        Exception sourceFailure = failureKind switch
        {
            "prtg-client" => new PrtgClientException("synthetic-source-http-or-timeout"),
            "invalid-json" => new JsonException("synthetic-invalid-source-json"),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        var worker = new PrtgTrustedSamplingQualificationJobHostedService(backend,
            NullLogger<PrtgTrustedSamplingQualificationJobHostedService>.Instance,
            (_, _, _, _) =>
            {
                sourceCalls++;
                return Task.FromException<IReadOnlyList<PrtgTrustedSamplingProbeRow>>(sourceFailure);
            },
            (_, _) => (true, new string('E', 64), "synthetic-test-table-admission"));

        Assert.True(await worker.RunOneSliceAsync(CancellationToken.None));
        var current = Assert.IsType<PrtgQualificationJobStateStore.Job>(jobs.ReadCurrent());
        var sensor = Assert.Single(jobs.ReadPage(job.JobId, 0).Sensors);
        Assert.Equal(1, sourceCalls);
        Assert.Equal("failed", sensor.Status);
        Assert.Equal(1, sensor.Attempts);
        Assert.Equal(1, sensor.WaveAttempts);
        Assert.Equal("bounded-source-probe-unavailable", sensor.Reason);
        Assert.Equal(1, current.Attempts);
        Assert.Equal(1, current.Failed);
        Assert.Equal("completed-with-waiting", current.Status);

        Assert.False(await worker.RunOneSliceAsync(CancellationToken.None));
        Assert.Equal(1, sourceCalls);
        Assert.Equal(1, Assert.Single(jobs.ReadPage(job.JobId, 0).Sensors).Attempts);
    }

    [Theory]
    [InlineData("resource-identity-not-current:11")]
    [InlineData("source-policy-not-ready-or-outside-pilot")]
    public async Task HostedSliceClosesQuotaOnExplicitSourceAuthorityInvalidation_NoNativeSourceAcceptance(
        string invalidationReason)
    {
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var authority = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            currentPolicy, PrtgFetchStrategy.Conservative, 15);
        var bindingStore = new PrtgTrustedSamplingBindingStore(backend);
        var binding = bindingStore.Save(new(11, currentSettings.Revision, currentPolicy.Revision,
            identity.Epoch, identity.ChannelGeneration, 0, "3", "Load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds",
            "UTC", "UTC", "synthetic-time-basis"));
        var sourceContract = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(
            currentSettings, currentPolicy, new HostStore(backend.Blob("hosts")).CapturePrtgSnapshot());
        var jobs = new PrtgQualificationJobStateStore(backend);
        var job = jobs.Start(Guid.NewGuid().ToString("N"), "http-start-owner", sourceContract.ScopeFingerprint,
            currentPolicy.SourceGeneration, currentSettings.Revision, currentPolicy.Revision, authority,
            1, 1, 72, DateTimeOffset.UtcNow, maximumAttempts: 1);
        jobs.ReleaseLease(job.JobId, job.Owner, job.Version, DateTimeOffset.UtcNow);

        var probeCalls = 0;
        var worker = new PrtgTrustedSamplingQualificationJobHostedService(backend,
            NullLogger<PrtgTrustedSamplingQualificationJobHostedService>.Instance,
            (_, _, _, _) =>
            {
                probeCalls++;
                return Task.FromException<IReadOnlyList<PrtgTrustedSamplingProbeRow>>(
                    new InvalidOperationException(invalidationReason));
            },
            (_, _) => (true, new string('E', 64), "synthetic-test-table-admission"));

        Assert.True(await worker.RunOneSliceAsync(CancellationToken.None));
        var current = Assert.IsType<PrtgQualificationJobStateStore.Job>(jobs.ReadCurrent());
        Assert.Equal("failed-stale", current.Status);
        Assert.Equal("source-authority-invalidated", current.WaitingReason);
        Assert.Equal(0, current.Attempts);
        var sensor = Assert.Single(jobs.ReadPage(job.JobId, 0).Sensors);
        Assert.Equal("waiting", sensor.Status);
        Assert.Equal(0, sensor.Attempts);
        Assert.Equal(1, probeCalls);
        Assert.Null(jobs.ReadOverview().ActiveQualification);
        Assert.True(string.IsNullOrEmpty(
            Assert.IsType<PrtgTrustedSamplingBinding>(bindingStore.Get(11)).QualificationProofReference));

        Assert.False(await worker.RunOneSliceAsync(CancellationToken.None));
        Assert.Equal(1, probeCalls);
        Assert.Equal("failed-stale", jobs.ReadCurrent()!.Status);
    }

    [Fact]
    public async Task HostedSliceRejectsQualifiedProbeRowWithoutJobProofReceipt_NoFakeAttempt_NoNativeSourceAcceptance()
    {
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var authority = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            currentPolicy, PrtgFetchStrategy.Conservative, 15);
        var binding = new PrtgTrustedSamplingBindingStore(backend).Save(new(11, currentSettings.Revision,
            currentPolicy.Revision, identity.Epoch, identity.ChannelGeneration, 0, "3", "Load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds",
            "UTC", "UTC", "synthetic-time-basis"));
        var sourceContract = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(
            currentSettings, currentPolicy, new HostStore(backend.Blob("hosts")).CapturePrtgSnapshot());
        var jobs = new PrtgQualificationJobStateStore(backend);
        var job = jobs.Start(Guid.NewGuid().ToString("N"), "http-start-owner", sourceContract.ScopeFingerprint,
            currentPolicy.SourceGeneration, currentSettings.Revision, currentPolicy.Revision, authority,
            1, 1, 72, DateTimeOffset.UtcNow, maximumAttempts: 1);
        jobs.ReleaseLease(job.JobId, job.Owner, job.Version, DateTimeOffset.UtcNow);

        var probeCalls = 0;
        var worker = new PrtgTrustedSamplingQualificationJobHostedService(backend,
            NullLogger<PrtgTrustedSamplingQualificationJobHostedService>.Instance,
            (_, _, _, _) =>
            {
                probeCalls++;
                return Task.FromResult<IReadOnlyList<PrtgTrustedSamplingProbeRow>>(
                [new PrtgTrustedSamplingProbeRow(11, "qualified", Array.Empty<string>(), 22, "CPU", 3, 93,
                    DateTime.UtcNow.ToOADate(), 60, Array.Empty<PrtgTrustedSamplingChannelProbeRow>(), false,
                    Array.Empty<string>(), identity.Generation, identity.Epoch, identity.ChannelGeneration,
                    DateTimeOffset.UtcNow, false, "3") { QualificationRecorded = false }]);
            },
            (_, _) => (true, new string('E', 64), "synthetic-test-table-admission"));

        Assert.True(await worker.RunOneSliceAsync(CancellationToken.None));
        var current = Assert.IsType<PrtgQualificationJobStateStore.Job>(jobs.ReadCurrent());
        Assert.Equal("failed-stale", current.Status);
        Assert.Equal("qualification-proof-not-recorded-for-job", current.WaitingReason);
        Assert.Equal(0, current.Attempts);
        Assert.Equal(1, probeCalls);
        var sensor = Assert.Single(jobs.ReadPage(job.JobId, 0).Sensors);
        Assert.Equal("waiting", sensor.Status);
        Assert.Equal(0, sensor.Attempts);
        Assert.Equal(binding.BindingRevision, sensor.BindingRevision);
        Assert.True(string.IsNullOrEmpty(
            Assert.IsType<PrtgTrustedSamplingBinding>(new PrtgTrustedSamplingBindingStore(backend).Get(11))
                .QualificationProofReference));
        Assert.Null(jobs.ReadOverview().ActiveQualification);

        Assert.False(await worker.RunOneSliceAsync(CancellationToken.None));
        Assert.Equal(1, probeCalls);
    }

    private PrtgTrustedSamplingProbeService Service(string scenario, PrtgRequestBudget budget) =>
        new(backend, value => PrtgClientFactory.Create(value,
            new SyntheticPrtgHandler(scenario), budget));

    private sealed class SyntheticPrtgHandler(string scenario) : HttpMessageHandler
    {
        private readonly double measured = DateTime.UtcNow.AddSeconds(-2).ToOADate();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var id = ReadId(request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath.EndsWith("getobjectproperty.htm", StringComparison.Ordinal))
                return Xml("<prtg><result>3</result></prtg>");
            if (request.RequestUri.AbsolutePath.EndsWith("historicdata.xml", StringComparison.Ordinal))
            {
                var oa = measured.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                return Xml($"<histdata totalcount=\"1\"><prtg-version>synthetic-contract-only</prtg-version><item><datetime_raw>{oa}</datetime_raw><value_raw channel=\"Load\" channelid=\"3\">93</value_raw></item></histdata>");
            }
            object response = request.RequestUri.Query.Contains("content=sensors")
                ? new { sensors = new[] { new { objid = id, parentid = 22, type = "CPU", cumsince_raw = "created",
                    status_raw = 3, lastvalue_raw = 93d, lastcheck_raw = measured, interval_raw = 60d,
                    raw_timestamp_timezone_id = "UTC" } } }
                : new { channels = new[] { new { objid = 3, name = "Load", lastvalue_raw = 93d } } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") });
        }
        private static Task<HttpResponseMessage> Xml(string value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(value, Encoding.UTF8, "application/xml") });
        private static long ReadId(string query)
        {
            var token = query.Split('&').FirstOrDefault(part => part.StartsWith("id=", StringComparison.Ordinal));
            return token is not null && long.TryParse(token.AsSpan(3), out var id) ? id : 11;
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
