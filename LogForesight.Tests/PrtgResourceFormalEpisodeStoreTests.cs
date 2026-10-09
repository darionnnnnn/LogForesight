using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourceFormalEpisodeStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    [Fact]
    public void AddedReasonKeepsEpisodeAndIssueEvidenceFingerprintChanges()
    {
        var assessment = Assessment(DateTime.UtcNow);
        var store = Store(assessment.HostId);
        var first = store.Reconcile(assessment, [Observation(assessment,
            "disk-two-hour-low-water", PrtgResourceFormalReasonState.Active, "low-water-evidence")])!;
        var nextAssessment = WithAuthority(assessment, DateTime.UtcNow.AddMinutes(1));
        var second = store.Reconcile(nextAssessment,
            [Observation(nextAssessment, "disk-seven-day-low-water-trend", PrtgResourceFormalReasonState.Active,
                "trend-evidence")])!;

        Assert.Equal(first.EpisodeObservedSinceUtc, second.EpisodeObservedSinceUtc);
        Assert.Equal(2, second.Reasons.Count);
        Assert.NotEqual(first.ReasonSetFingerprint, second.ReasonSetFingerprint);
    }

    [Fact]
    public void UnknownEvaluationPreservesActiveReasonAndOnlyExplicitAllReasonRecoveryEndsEpisode()
    {
        var assessment = Assessment(DateTime.UtcNow);
        var store = Store(assessment.HostId);
        var active = store.Reconcile(assessment, [
            Observation(assessment, "disk-two-hour-low-water", PrtgResourceFormalReasonState.Active, "low"),
            Observation(assessment, "disk-seven-day-low-water-trend", PrtgResourceFormalReasonState.Active, "trend")])!;

        var unknownAssessment = WithAuthority(assessment, assessment.AsOfUtc.AddMinutes(1));
        var unknown = store.Reconcile(unknownAssessment, [
            Observation(unknownAssessment, "disk-two-hour-low-water", PrtgResourceFormalReasonState.Unknown, "missing")])!;
        Assert.Equal(active.ReasonSetFingerprint, unknown.ReasonSetFingerprint);

        var remainingAssessment = WithAuthority(assessment, assessment.AsOfUtc.AddMinutes(2));
        var oneRemains = store.Reconcile(remainingAssessment, [
            Observation(remainingAssessment, "disk-two-hour-low-water", PrtgResourceFormalReasonState.Recovered, "recovered")])!;
        Assert.Single(oneRemains.Reasons);
        Assert.Equal("disk-seven-day-low-water-trend", oneRemains.Reasons[0].ReasonCode);

        var recoveredAssessment = WithAuthority(assessment, assessment.AsOfUtc.AddMinutes(3));
        var cleared = store.Reconcile(recoveredAssessment, [
            Observation(recoveredAssessment, "disk-seven-day-low-water-trend", PrtgResourceFormalReasonState.Recovered, "recovered")]);
        Assert.Null(cleared);
        Assert.Null(store.GetCurrent(assessment.HostId, assessment.SensorObjid));
    }

    [Fact]
    public void ResourceOrChannelGenerationDriftStartsANewObservedEpisode()
    {
        var assessment = Assessment(DateTime.UtcNow);
        var store = Store(assessment.HostId);
        var prior = store.Reconcile(assessment, [Observation(assessment,
            "disk-two-hour-low-water", PrtgResourceFormalReasonState.Active, "low")])!;

        var changed = Assessment(assessment.AsOfUtc.AddMinutes(1), resourceGeneration: "resource-next");
        var current = store.Reconcile(changed, [Observation(changed,
            "disk-two-hour-low-water", PrtgResourceFormalReasonState.Active, "low-next")])!;
        Assert.NotEqual(prior.EpisodeObservedSinceUtc, current.EpisodeObservedSinceUtc);
        Assert.Equal("resource-next", current.ResourceGeneration);
    }

    [Fact]
    public void OlderActiveReplayCannotReviveEpisodeAfterNewerQualifiedRecovery()
    {
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
        var store = Store(20);
        var older = Assessment(now.AddMinutes(-10));
        store.Reconcile(older, [Observation(older, "disk-two-hour-low-water",
            PrtgResourceFormalReasonState.Active, "older-active")]);

        var recovered = Assessment(now);
        Assert.Null(store.Reconcile(recovered, [Observation(recovered, "disk-two-hour-low-water",
            PrtgResourceFormalReasonState.Recovered, "newer-recovery")]));
        Assert.Null(store.GetCurrent(20, recovered.SensorObjid));

        var replay = Store(20).ReconcileWithStatus(older, [Observation(older, "disk-two-hour-low-water",
            PrtgResourceFormalReasonState.Active, "older-active")]);
        Assert.False(replay.Accepted);
        Assert.Null(replay.Episode);
        Assert.Null(store.GetCurrent(20, recovered.SensorObjid));
    }

    [Fact]
    public void OlderRecoveryCannotClearNewerEpisodeAndEqualEvidenceUsesCurrentAuthorityOnlyAsTieBreak()
    {
        var evidenceAt = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-5), DateTimeKind.Utc);
        var store = Store(20);
        var first = Assessment(evidenceAt, evidenceAsOfUtc: evidenceAt);
        store.Reconcile(first, [Observation(first, "disk-two-hour-low-water",
            PrtgResourceFormalReasonState.Active, "active-v1")]);

        var newerEvidence = Assessment(evidenceAt.AddMinutes(1));
        store.Reconcile(newerEvidence, [Observation(newerEvidence, "disk-two-hour-low-water",
            PrtgResourceFormalReasonState.Active, "active-v2")]);
        var staleRecovery = Assessment(evidenceAt.AddMinutes(-1));
        Assert.False(store.ReconcileWithStatus(staleRecovery, [Observation(staleRecovery,
            "disk-two-hour-low-water", PrtgResourceFormalReasonState.Recovered, "old-recovery")]).Accepted);
        Assert.NotNull(store.GetCurrent(20, newerEvidence.SensorObjid));

        var sameEvidenceNewAuthority = Assessment(newerEvidence.AuthorityAsOfUtc.AddMinutes(1),
            evidenceAsOfUtc: newerEvidence.EvidenceAsOfUtc);
        var changedProof = store.ReconcileWithStatus(sameEvidenceNewAuthority, [Observation(sameEvidenceNewAuthority,
            "disk-two-hour-low-water", PrtgResourceFormalReasonState.Active, "same-cutoff-new-proof")]);
        Assert.True(changedProof.Accepted);
        Assert.NotNull(changedProof.Episode);

        var changedRule = Assessment(sameEvidenceNewAuthority.AuthorityAsOfUtc.AddMinutes(1),
            evidenceAsOfUtc: newerEvidence.EvidenceAsOfUtc, currentRuleFingerprint: new string('B', 64));
        Assert.True(store.ReconcileWithStatus(changedRule, [Observation(changedRule,
            "disk-two-hour-low-water", PrtgResourceFormalReasonState.Active, "same-cutoff-new-proof")]).Accepted);

        var lowerAuthority = Assessment(sameEvidenceNewAuthority.AuthorityAsOfUtc,
            evidenceAsOfUtc: newerEvidence.EvidenceAsOfUtc);
        Assert.False(store.ReconcileWithStatus(lowerAuthority, [Observation(lowerAuthority,
            "disk-two-hour-low-water", PrtgResourceFormalReasonState.Recovered, "same-cutoff-old-authority")]).Accepted);
        Assert.NotNull(store.GetCurrent(20, newerEvidence.SensorObjid));
    }

    [Fact]
    public void StaleIdentityReasonObservationIsRejected()
    {
        var assessment = Assessment(DateTime.UtcNow);
        var stale = Observation(assessment, "disk-seven-day-low-water-trend",
            PrtgResourceFormalReasonState.Active, "trend") with { ChannelGeneration = "channel-old" };
        Assert.Throws<InvalidDataException>(() => Store(assessment.HostId).Reconcile(assessment, [stale]));
    }

    private PrtgResourceFormalEpisodeStore Store(long hostId) =>
        new(_fixture.Blob(PrtgResourceFormalEpisodeStore.BlobKey(hostId)));

    private static PrtgResourcePeriodAssessment Assessment(DateTime authorityAsOfUtc,
        string resourceGeneration = "resource-1", DateTime? evidenceAsOfUtc = null,
        string currentRuleFingerprint = "")
    {
        var evidence = DateTime.SpecifyKind(evidenceAsOfUtc ?? authorityAsOfUtc, DateTimeKind.Utc);
        var input = new PrtgResourceReadinessInput(PrtgResourceFamily.Disk, null, null, null,
            evidence);
        var decision = PrtgResourcePressureEvaluator.Evaluate(input);
        return new(20, 30, 40, PrtgResourceFamily.Disk, input, decision,
            DateTime.SpecifyKind(authorityAsOfUtc, DateTimeKind.Utc), "source-1", resourceGeneration,
            "channel-1", "epoch-1", "semantic-1", "strategy-1", null, currentRuleFingerprint, "");
    }

    private static PrtgResourceFormalReasonObservation Observation(PrtgResourcePeriodAssessment assessment,
        string reasonCode, PrtgResourceFormalReasonState state, string evidence) => new(
        assessment.SensorObjid, assessment.SourceGeneration, assessment.ResourceGeneration,
        assessment.ChannelGeneration, assessment.ResourceEpoch, reasonCode, state,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))), "verified evidence",
        EvidenceDay: DateTime.SpecifyKind(assessment.EvidenceAsOfUtc.Date, DateTimeKind.Unspecified),
        SourceRuleId: reasonCode == "disk-seven-day-low-water-trend" ? "trend-rule" : null,
        SourceRuleFingerprint: reasonCode == "disk-seven-day-low-water-trend" ? new string('A', 64) : null,
        EvidenceAsOfUtc: assessment.EvidenceAsOfUtc);

    public void Dispose() => _fixture.Dispose();
    private static PrtgResourcePeriodAssessment WithAuthority(PrtgResourcePeriodAssessment assessment,
        DateTime asOfUtc)
    {
        var input = assessment.Input with { AsOfUtc = DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc) };
        var decision = PrtgResourcePressureEvaluator.Evaluate(input);
        return new(assessment.HostId, assessment.DeviceObjid, assessment.SensorObjid, assessment.Family,
            input, decision, DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc), assessment.SourceGeneration,
            assessment.ResourceGeneration, assessment.ChannelGeneration, assessment.ResourceEpoch,
            assessment.SemanticVersion, assessment.StrategyVersion, assessment.CurrentRule,
            assessment.CurrentRuleFingerprint, assessment.RuleAdmissionFingerprint);
    }
}
