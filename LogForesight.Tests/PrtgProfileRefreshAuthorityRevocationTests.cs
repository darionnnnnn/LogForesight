using System.Globalization;
using System.Text.Json.Nodes;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Crosses the scheduled-refresh revocation seam into the real period consumer. These synthetic
/// fixture profiles exercise storage/consumer behavior only and are not native-source evidence.
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class PrtgProfileRefreshAuthorityRevocationTests
{
    [Fact]
    public void WorkerCompletedWaitingProbeBatchRevokesOldProfileAndConsumerCannotGrantFormalCpuFinding()
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [95d, 95d]);
        var backend = fixture.Backend;
        var profiles = new PrtgTrustedSamplingProfileStore(backend);
        var oldProfile = profiles.GetMany([PrtgFormalRuleCaseFixture.SensorId])[PrtgFormalRuleCaseFixture.SensorId];
        var evidenceAsOfUtc = AnalysisCutoffUtc(fixture.AnalysisDay);
        var consumer = new PrtgResourcePeriodConsumer(backend,
            new SystemSettingsStore(backend.Blob("system_settings")));

        var before = consumer.EvaluateBatch([PrtgFormalRuleCaseFixture.SensorId], evidenceAsOfUtc, DateTime.UtcNow);
        Assert.Contains(before.FormalFindings, finding => finding.RuleCode == "resource_cpu_sustained_pressure");
        int savedRowsBefore;
        using (var context = backend.CreateContext())
            savedRowsBefore = context.PrtgValues.Count(row => row.SensorObjid == PrtgFormalRuleCaseFixture.SensorId);

        var leaseStore = new PrtgTrustedSamplingProfileRefreshStateStore(backend);
        Assert.True(leaseStore.TryAcquire("candidate-refresh-worker", DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(2), out var leaseVersion));
        var observedWaiting = WaitingRow(oldProfile, "native_primary_channel_missing");
        PrtgTrustedSamplingProfileRefreshHostedService.RevokeCompletedWaitingProfiles(backend,
            [observedWaiting], new Dictionary<long, PrtgTrustedSamplingProfile>
            { [oldProfile.SensorObjid] = oldProfile }, "candidate-refresh-worker", leaseVersion);

        Assert.Empty(profiles.GetMany([oldProfile.SensorObjid]));
        using (var context = backend.CreateContext())
            Assert.Equal(savedRowsBefore, context.PrtgValues.Count(row => row.SensorObjid == PrtgFormalRuleCaseFixture.SensorId));
        var after = consumer.EvaluateBatch([PrtgFormalRuleCaseFixture.SensorId], evidenceAsOfUtc, DateTime.UtcNow);
        Assert.DoesNotContain(after.FormalFindings,
            finding => finding.RuleCode == "resource_cpu_sustained_pressure");
        Assert.DoesNotContain(after.Assessments,
            assessment => assessment.Decision.Mode == PrtgResourceDecisionMode.FormalRisk);
    }

    [Fact]
    public void OldWaitingRefreshCannotDeleteNewerSuccessfulProfile()
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [95d, 95d]);
        var backend = fixture.Backend;
        var profiles = new PrtgTrustedSamplingProfileStore(backend);
        var oldProfile = profiles.GetMany([PrtgFormalRuleCaseFixture.SensorId])[PrtgFormalRuleCaseFixture.SensorId];
        var identity = backend.PrtgStore().GetResourceIdentity(oldProfile.SensorObjid);
        var newerProfile = Reobserve(oldProfile, identity, DateTimeOffset.UtcNow);
        profiles.RecordProbeResult(newerProfile);

        var leaseVersion = CreateCurrentLease(backend, "candidate-refresh-worker");
        var observedWaiting = WaitingRow(oldProfile, "native_primary_channel_mismatch");
        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.RevokeCompletedWaitingProfile(
            backend, observedWaiting, oldProfile, "candidate-refresh-worker", leaseVersion));
        Assert.Equal(newerProfile.MetadataDigest,
            profiles.GetMany([oldProfile.SensorObjid])[oldProfile.SensorObjid].MetadataDigest);
    }

    [Fact]
    public void OldWaitingRefreshAfterBindingChangeCannotDeleteNewBindingProfile()
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [95d, 95d]);
        var backend = fixture.Backend;
        var profiles = new PrtgTrustedSamplingProfileStore(backend);
        var oldProfile = profiles.GetMany([PrtgFormalRuleCaseFixture.SensorId])[PrtgFormalRuleCaseFixture.SensorId];
        var bindings = new PrtgTrustedSamplingBindingStore(backend);
        var priorBinding = bindings.Get(oldProfile.SensorObjid)!;
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var identity = backend.PrtgStore().GetResourceIdentity(oldProfile.SensorObjid);
        var newChannelId = (long.Parse(priorBinding.ChannelObjectId, CultureInfo.InvariantCulture) + 1)
            .ToString(CultureInfo.InvariantCulture);
        bindings.Save(new(oldProfile.SensorObjid, settings.Revision, policy.Revision,
            identity.Epoch, identity.ChannelGeneration, priorBinding.BindingRevision, newChannelId,
            priorBinding.ExpectedCaption, priorBinding.Quantity, priorBinding.Unit, priorBinding.Scale,
            priorBinding.Direction, priorBinding.IntervalRawUnit, priorBinding.RawTimestampTimeZoneId,
            priorBinding.AnalysisTimeZoneId, priorBinding.TimeBasisEvidenceReference));

        // The standard fixture closure qualifies and publishes a valid profile under the new
        // binding. The waiting result still carries the digest captured before that binding save.
        identity = backend.PrtgStore().GetResourceIdentity(oldProfile.SensorObjid);
        var newBindingSourceProfile = SourceProfileForChannel(oldProfile, identity, newChannelId);
        var newBindingProfile = PrtgConsumerProfileFixtureClosure.Publish(backend, newBindingSourceProfile);
        var leaseVersion = CreateCurrentLease(backend, "candidate-refresh-worker");
        var observedWaiting = WaitingRow(oldProfile, "native_primary_channel_mismatch");

        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.RevokeCompletedWaitingProfile(
            backend, observedWaiting, oldProfile, "candidate-refresh-worker", leaseVersion));
        Assert.Equal(newBindingProfile.MetadataDigest,
            profiles.GetMany([oldProfile.SensorObjid])[oldProfile.SensorObjid].MetadataDigest);
        Assert.Equal(newChannelId, bindings.Get(oldProfile.SensorObjid)!.ChannelObjectId);
    }

    [Fact]
    public void ExpiredRefreshLeaseCannotRevokeProfile()
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [95d, 95d]);
        var backend = fixture.Backend;
        var profiles = new PrtgTrustedSamplingProfileStore(backend);
        var oldProfile = profiles.GetMany([PrtgFormalRuleCaseFixture.SensorId])[PrtgFormalRuleCaseFixture.SensorId];
        const string owner = "expired-refresh-worker";
        var leaseVersion = CreateCurrentLease(backend, owner);
        var progress = backend.Blob(PrtgTrustedSamplingProfileRefreshHostedService.ProgressBlobKey);
        var overview = JsonNode.Parse(progress.Read()!)!.AsObject();
        overview["LeaseUntilUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture);
        progress.Mutate(_ => (overview.ToJsonString(), true));

        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.RevokeCompletedWaitingProfile(
            backend, WaitingRow(oldProfile, "native_primary_channel_missing"), oldProfile, owner, leaseVersion));
        Assert.Equal(oldProfile.MetadataDigest,
            profiles.GetMany([oldProfile.SensorObjid])[oldProfile.SensorObjid].MetadataDigest);
    }

    [Fact]
    public void FailedOrAbsentProbeOutcomeDoesNotRevokeProfile()
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [95d, 95d]);
        var backend = fixture.Backend;
        var profiles = new PrtgTrustedSamplingProfileStore(backend);
        var oldProfile = profiles.GetMany([PrtgFormalRuleCaseFixture.SensorId])[PrtgFormalRuleCaseFixture.SensorId];
        const string owner = "noncompleted-refresh-worker";
        var leaseVersion = CreateCurrentLease(backend, owner);

        var transportFailure = WaitingRow(oldProfile, "transport_unavailable") with { Status = "failed" };
        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.RevokeCompletedWaitingProfile(
            backend, transportFailure, oldProfile, owner, leaseVersion));
        // Timeouts, caller cancellation, and lease loss leave ProbeAsync without a completed row;
        // they are not sent through the worker's completed-row batch handler.
        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.RevokeCompletedWaitingProfile(
            backend, null, oldProfile, owner, leaseVersion));
        PrtgTrustedSamplingProfileRefreshHostedService.RevokeCompletedWaitingProfiles(backend,
            Array.Empty<PrtgTrustedSamplingProbeRow>(),
            new Dictionary<long, PrtgTrustedSamplingProfile> { [oldProfile.SensorObjid] = oldProfile },
            owner, leaseVersion);

        Assert.Equal(oldProfile.MetadataDigest,
            profiles.GetMany([oldProfile.SensorObjid])[oldProfile.SensorObjid].MetadataDigest);
    }

    private static PrtgTrustedSamplingProbeRow WaitingRow(PrtgTrustedSamplingProfile profile, string missingField) =>
        new(profile.SensorObjid, "waiting", Array.Empty<string>(), null, profile.SensorType, null,
            null, null, null, Array.Empty<PrtgTrustedSamplingChannelProbeRow>(), false, [missingField],
            profile.ResourceGeneration, profile.IdentityEpoch, profile.ChannelGeneration,
            DateTimeOffset.UtcNow, false, null);

    private static PrtgTrustedSamplingProfile Reobserve(PrtgTrustedSamplingProfile old,
        PrtgResourceIdentity identity, DateTimeOffset observedAt) => PrtgTrustedSamplingProfile.FromProbe(
            old.SensorObjid, identity, old.SensorType, old.PrimaryChannelId, old.PrimaryChannelCaption,
            old.Quantity, old.Unit, old.Scale, old.Direction, old.SemanticVersion, old.StrategyFingerprint,
            old.StrategyMinutes, old.StrategyEffectiveFromHourUtc, old.ConfirmedScanInterval,
            old.IntervalRawUnit, old.RawTimestampTimeZoneId, old.SourceApiTimeZoneId,
            old.AnalysisTimeZoneId, observedAt, "synthetic-refreshed-metadata-reference",
            "synthetic-refreshed-physical-sample-reference", old.SourceMarkedPrimary,
            old.ComparedSnapshotValue, old.ComparedPrimaryChannelValue,
            old.ComparedSnapshotMeasurementOaDate, old.ComparedPrimaryChannelMeasurementOaDate,
            old.AuthorityKind, old.BindingRevision, old.BindingFingerprint,
            old.NativePrimaryChannelPropertyId, old.QualificationProofReference,
            old.BindingSettingsRevision, old.BindingPolicyRevision, old.AuthorityContextFingerprint);

    private static PrtgTrustedSamplingProfile SourceProfileForChannel(PrtgTrustedSamplingProfile old,
        PrtgResourceIdentity identity, string channelId) => PrtgTrustedSamplingProfile.FromProbe(
            old.SensorObjid, identity, old.SensorType, channelId, old.PrimaryChannelCaption,
            old.Quantity, old.Unit, old.Scale, old.Direction, old.SemanticVersion, old.StrategyFingerprint,
            old.StrategyMinutes, old.StrategyEffectiveFromHourUtc, old.ConfirmedScanInterval,
            old.IntervalRawUnit, old.RawTimestampTimeZoneId, old.SourceApiTimeZoneId,
            old.AnalysisTimeZoneId, DateTimeOffset.UtcNow, "synthetic-rebound-metadata-reference",
            "synthetic-rebound-physical-sample-reference", true,
            old.ComparedSnapshotValue, old.ComparedPrimaryChannelValue,
            old.ComparedSnapshotMeasurementOaDate, old.ComparedPrimaryChannelMeasurementOaDate);

    private static long CreateCurrentLease(StorageBackend backend, string owner)
    {
        var state = new PrtgTrustedSamplingProfileRefreshStateStore(backend);
        if (!state.TryAcquire(owner, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), out var version))
            throw new InvalidOperationException("A test refresh lease could not be acquired.");
        return version;
    }

    private static DateTime AnalysisCutoffUtc(DateTime analysisDay)
    {
        var wallCutoff = DateTime.SpecifyKind(analysisDay.Date.AddDays(1), DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(wallCutoff, TimeZoneInfo.Local), DateTimeKind.Utc);
    }
}
