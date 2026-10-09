using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgQualificationJobStateStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-qual-job-store-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly PrtgQualificationJobStateStore store;

    public PrtgQualificationJobStateStoreTests()
    {
        backend = new(new StorageSettings { Type = "Sqlite" }, root);
        store = new PrtgQualificationJobStateStore(backend);
    }

    [Fact]
    public void ProofAttemptRequiresCurrentOwnerLeaseScopeAndPageVersion()
    {
        var now = DateTimeOffset.UtcNow;
        var job = Start(now, [Sensor(11), Sensor(12)]);
        var (page, version) = store.ReadPageWithVersion(job.JobId, 0);
        var fence = Fence(job, page.Sensors[0], version, 0);

        Assert.Throws<OperationCanceledException>(() => store.RecordAttemptResult(
            fence with { Owner = "wrong-owner" }, now, "synthetic-raw-missing"));
        Assert.Throws<InvalidOperationException>(() => store.RecordAttemptResult(
            fence with { SourceGeneration = "new-source" }, now, "synthetic-raw-missing"));

        store.RecordAttemptResult(fence, now, "synthetic-raw-missing");
        var updated = store.ReadCurrent()!;
        var updatedPage = store.ReadPageWithVersion(job.JobId, 0);
        Assert.Equal(1, updated.Attempts);
        Assert.Equal(1, updated.Cursor);
        Assert.Equal(now.AddMinutes(1), updatedPage.Page.Sensors[0].NextAttemptUtc);
        Assert.Throws<InvalidOperationException>(() => store.RecordAttemptResult(
            fence, now.AddSeconds(1), "stale-page-cas"));

        // Finite fair retry cap: second retry is delayed 10 minutes; the third attempt becomes final.
        var secondFence = Fence(updated, updatedPage.Page.Sensors[0], updatedPage.Version, 0);
        store.RecordAttemptResult(secondFence, now.AddMinutes(1), "synthetic-raw-missing");
        var second = store.ReadPageWithVersion(job.JobId, 0);
        Assert.Equal(now.AddMinutes(11), second.Page.Sensors[0].NextAttemptUtc);
        store.ReleaseLease(job.JobId, updated.Owner, updated.Version, now.AddMinutes(1).AddSeconds(1));
        Assert.True(store.TryAcquireLease("worker-a-retry", now.AddMinutes(11), TimeSpan.FromMinutes(2), out var retryJob));
        var thirdFence = Fence(Assert.IsType<PrtgQualificationJobStateStore.Job>(retryJob),
            second.Page.Sensors[0], second.Version, 0);
        store.RecordAttemptResult(thirdFence, now.AddMinutes(11), "synthetic-raw-missing");
        var final = store.ReadPage(job.JobId, 0).Sensors[0];
        Assert.Equal("failed", final.Status);
        Assert.Equal(3, final.Attempts);
    }

    [Fact]
    public void SameWaveCancellationSurvivesWorkerLeaseVersionButWrongWaveDoesNotCancel()
    {
        var now = DateTimeOffset.UtcNow;
        var original = Start(now, [Sensor(11)]);
        store.ReleaseLease(original.JobId, original.Owner, original.Version, now);
        Assert.True(store.TryAcquireLease("next-worker", now.AddSeconds(1), TimeSpan.FromSeconds(180), out var acquired));
        Assert.NotEqual(original.Version, acquired!.Version);
        Assert.Throws<InvalidOperationException>(() => store.Cancel(original.JobId, "maintainer",
            original.Version, now.AddSeconds(2), original.Wave + 1));
        Assert.Equal("running", store.ReadCurrent()!.Status);
        store.Cancel(original.JobId, "maintainer", original.Version, now.AddSeconds(2), original.Wave);
        Assert.Equal("cancelled", store.ReadCurrent()!.Status);
        Assert.Null(store.ReadOverview().ActiveQualification);
        Assert.Single(store.ReadPage(original.JobId, 0).Sensors);
    }

    [Fact]
    public void RestartDoesNotExtendDeadlineAndExplicitResumeCreatesNewDeadlineAndPages()
    {
        var now = DateTimeOffset.UtcNow;
        var job = Start(now, [Sensor(11)]);
        var reloaded = new PrtgQualificationJobStateStore(new StorageBackend(new StorageSettings { Type = "Sqlite" }, root))
            .ReadCurrent()!;
        Assert.Equal(job.DeadlineUtc, reloaded.DeadlineUtc);

        store.Cancel(job.JobId, "maintainer", job.Version, now);
        var resumed = Start(now.AddMinutes(1), [Sensor(11)]);
        Assert.NotEqual(job.JobId, resumed.JobId);
        Assert.Equal(now.AddMinutes(1).AddHours(72), resumed.DeadlineUtc);
        var missingOldPage = Assert.Throws<InvalidDataException>(() => store.ReadPage(job.JobId, 0));
        Assert.Equal("qualification-job-page-fence-invalid", missingOldPage.Message);
    }

    [Fact]
    public void ExpiredJobStopsAdmissionAndRetainsProgressForExplicitResume()
    {
        var now = DateTimeOffset.UtcNow;
        var job = Start(now, [Sensor(11)], duration: 1);
        store.ExpireIfDeadlinePassed(job.DeadlineUtc.AddSeconds(1));
        var expired = store.ReadCurrent()!;
        Assert.Equal("expired", expired.Status);
        Assert.Equal(job.DeadlineUtc, expired.DeadlineUtc);
        Assert.Equal(0, expired.Qualified);
    }

    [Fact]
    public void InitialNullRetryTimeIsRunnableAndFutureBackoffIsNot()
    {
        var now = DateTimeOffset.UtcNow;
        var job = Start(now, [Sensor(11)]);
        var sensor = store.ReadPage(job.JobId, 0).Sensors.Single();

        Assert.Null(sensor.NextAttemptUtc);
        Assert.True(PrtgQualificationJobStateStore.IsRunnableNow(sensor, job, now));
        sensor.NextAttemptUtc = now.AddMinutes(1);
        Assert.False(PrtgQualificationJobStateStore.IsRunnableNow(sensor, job, now));
        Assert.True(PrtgQualificationJobStateStore.IsRunnableNow(sensor, job, now.AddMinutes(1)));
        sensor.BindingRevision = 0;
        Assert.False(PrtgQualificationJobStateStore.IsRunnableNow(sensor, job, now.AddMinutes(1)));
    }

    [Fact]
    public void BindingAddedAfterAdmissionCannotIncreaseEligiblePopulationDuringInitialization()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var job = store.Start(id, "worker-a", new string('A', 64), "source-r1",
            "settings-r1", "policy-r1", new string('B', 64), 1, 0, 72, now, 1);

        var ex = Assert.Throws<InvalidOperationException>(() => store.InitializePage(id, job.Owner,
            job.Version, now, 0, [Sensor(11)]));

        Assert.Equal("qualification-job-capacity-contract-changed", ex.Message);
        Assert.Equal(0, store.ReadCurrent()!.InitializedPages);
        Assert.Throws<InvalidDataException>(() => store.ReadPage(id, 0));
    }

    [Fact]
    public void RuntimeAdmissionRecoveryReturnsWaitingCapacityJobToRunningUnderLease()
    {
        var now = DateTimeOffset.UtcNow;
        var job = Start(now, [Sensor(11)]);
        store.SetStatus(job.JobId, job.Owner, job.Version, now, "waiting-capacity",
            "runtime-table-admission-unavailable", releaseQuota: false);
        store.SetStatus(job.JobId, job.Owner, job.Version, now.AddSeconds(1), "running",
            "runtime-table-admission-restored", releaseQuota: false);

        Assert.Equal("running", store.ReadCurrent()!.Status);
    }

    [Fact]
    public void CircularWorkerOrderVisitsLaterPagesBeforeWrappingToCurrentPagePrefix()
    {
        var segments = PrtgTrustedSamplingQualificationJobHostedService.CircularPageSegments(105, 250, 100);
        var orderedIds = segments.SelectMany(segment =>
            Enumerable.Range(segment.PageIndex * 100 + segment.StartInclusive,
                segment.EndExclusive - segment.StartInclusive)).ToArray();

        Assert.Equal(Enumerable.Range(105, 145).Concat(Enumerable.Range(0, 105)), orderedIds);
        Assert.Equal((1, 5, 100), segments[0]);
        Assert.Equal((2, 0, 50), segments[1]);
        Assert.Equal((0, 0, 100), segments[2]);
        Assert.Equal((1, 0, 5), segments[3]);
    }

    [Fact]
    public void ExpiredDuringPageInitializationCanResumeSameJobAndContinueAtInitializationWatermark()
    {
        var now = DateTimeOffset.UtcNow;
        const string url = "https://prtg.qualification-resume.test";
        const string source = "source-r1";
        const string policyRevision = "policy-r1";
        var ids = Enumerable.Range(1, 101).Select(value => (long)value).ToArray();
        new SystemSettingsStore(backend.Blob("system_settings")).Update(settings =>
        {
            settings.PrtgEnabled = true;
            settings.PrtgUrl = url;
            settings.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        var settingsRevision = new SystemSettingsStore(backend.Blob("system_settings")).Get().Revision;
        var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.Revision = policyRevision;
            policy.CoreSystemId = "fixture-core";
            policy.SourceGeneration = source;
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(url);
            policy.ValidFrom = now.AddDays(-1);
            policy.HostIds = [1];
            policy.SensorIds = ids.ToList();
            policy.ConfirmedBy = "maintainer";
            policy.ContinuityEvidenceReference = "fixture-continuity";
            policy.SourceTimeZoneId = "UTC";
            policy.SourceCultureName = "en-US";
            policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "fixture-time-basis";
        });
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = policyStore.Get();
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
        var context = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy, strategyName, strategyMinutes);
        var scope = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(settings, policy, null).ScopeFingerprint;
        var job = store.Start(Guid.NewGuid().ToString("N"), "worker-start", scope, source,
            settingsRevision, policyRevision, context, ids.Length, 0, 1, now, 1);
        var firstPage = ids.Take(100).Select(id => new PrtgQualificationJobStateStore.Sensor
        {
            SensorObjid = id, Status = "waiting", Reason = "binding-missing"
        }).ToArray();
        store.InitializePage(job.JobId, job.Owner, job.Version, now, 0, firstPage);
        store.ExpireIfDeadlinePassed(job.DeadlineUtc.AddSeconds(1));
        var expired = store.ReadCurrent()!;
        Assert.Equal(1, expired.InitializedPages);
        Assert.Equal(1, expired.InitializationCursor);

        var resumedAt = job.DeadlineUtc.AddMinutes(2);
        var resumed = store.ResumeExisting(job.JobId, expired.Version, "worker-resumed", scope,
            source, settingsRevision, policyRevision, context, 0, 72, 1, resumedAt, TimeSpan.FromSeconds(180));

        Assert.Equal(job.JobId, resumed.JobId);
        Assert.Equal(1, resumed.InitializedPages);
        Assert.Equal(1, resumed.InitializationCursor);
        Assert.Equal("initializing", resumed.Status);
        Assert.Equal(100, store.ReadPage(job.JobId, 0).Sensors.Count);
        store.InitializePage(job.JobId, resumed.Owner, resumed.Version, resumedAt, 1,
            [new PrtgQualificationJobStateStore.Sensor { SensorObjid = ids[100], Reason = "binding-missing" }]);
        var completedInitialization = store.ReadCurrent()!;
        Assert.Equal(2, completedInitialization.InitializedPages);
        Assert.Equal(2, completedInitialization.InitializationCursor);
        Assert.Equal("running", completedInitialization.Status);
    }

    private PrtgQualificationJobStateStore.Job Start(DateTimeOffset now,
        IReadOnlyList<PrtgQualificationJobStateStore.Sensor> sensors, int duration = 72)
    {
        var id = Guid.NewGuid().ToString("N");
        var job = store.Start(id, "worker-a", new string('A', 64), "source-r1",
            "settings-r1", "policy-r1", new string('B', 64), sensors.Count,
            sensors.Count(sensor => sensor.BindingRevision > 0 && sensor.Status == "waiting"), duration, now, 3);
        store.InitializePage(id, job.Owner, job.Version, now, 0, sensors);
        return job;
    }

    private static PrtgQualificationJobStateStore.Sensor Sensor(long id) => new()
    { SensorObjid = id, BindingRevision = 1, BindingFingerprint = new string('C', 64) };

    private static PrtgQualificationWriteFence Fence(PrtgQualificationJobStateStore.Job job,
        PrtgQualificationJobStateStore.Sensor sensor, long pageVersion, int pageIndex) => new(
        job.JobId, job.Owner, job.Version, job.ScopeFingerprint, job.SourceGeneration,
        job.SettingsRevision, job.PolicyRevision, job.AuthorityContextFingerprint,
        pageIndex, pageVersion, sensor.SensorObjid, sensor.BindingRevision, sensor.BindingFingerprint);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
