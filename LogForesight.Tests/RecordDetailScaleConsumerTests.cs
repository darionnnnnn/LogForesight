using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using System.Reflection;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Synthetic consumer contract: normalized trusted percent evidence must remain visible in the
/// real host-detail projection when a currently qualified profile uses a non-unit scale.
/// This fixture does not assert support from any deployed PRTG source.
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class RecordDetailScaleConsumerTests
{
    [Fact]
    public void QualifiedHalfScaleDiskEvidenceRemainsVisibleThroughGetHostDetail()
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        var host = fixture.SeedFormalResourceCase(PrtgResourceFamily.Disk, [5d, 5d]);
        var backend = fixture.Backend;
        var profiles = new PrtgTrustedSamplingProfileStore(backend);
        var prior = profiles.GetMany([PrtgFormalRuleCaseFixture.SensorId])[PrtgFormalRuleCaseFixture.SensorId];
        var identity = backend.PrtgStore().GetResourceIdentity(PrtgFormalRuleCaseFixture.SensorId);

        var scaledSource = PrtgTrustedSamplingProfile.FromProbe(prior.SensorObjid, identity,
            prior.SensorType, prior.PrimaryChannelId, prior.PrimaryChannelCaption, prior.Quantity,
            prior.Unit, 0.5d, prior.Direction, prior.SemanticVersion, prior.StrategyFingerprint,
            prior.StrategyMinutes, prior.StrategyEffectiveFromHourUtc, prior.ConfirmedScanInterval,
            prior.IntervalRawUnit, prior.RawTimestampTimeZoneId, prior.SourceApiTimeZoneId,
            prior.AnalysisTimeZoneId, DateTimeOffset.UtcNow, "synthetic-scale-consumer-metadata",
            "synthetic-scale-consumer-sample", true, 5d, 5d,
            DateTime.UtcNow.ToOADate(), DateTime.UtcNow.ToOADate());
        var profile = PrtgConsumerProfileFixtureClosure.Publish(backend, scaledSource);

        var assessment = SeedCurrentWindowAndEvaluate(backend, profile, 2.5d);
        Assert.Equal(PrtgResourceDecisionKind.Hit, assessment.Decision.Kind);

        var detail = CreateDetailQuery(backend, host).GetHostDetail(host.HostId, days: 7);
        var hint = Assert.Single(detail.ResourcePressureHints, item => item.SensorObjid == profile.SensorObjid);
        Assert.Equal("Hit", hint.State);
        Assert.NotEmpty(hint.FormalReasons);
        Assert.NotEqual("current-proof-unavailable", detail.ResourcePressureAvailability);
    }

    [Fact]
    public void InvalidScaleProfileIsNotProjectedByGetHostDetail()
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        var host = fixture.SeedFormalResourceCase(PrtgResourceFamily.Disk, [5d, 5d]);
        var backend = fixture.Backend;
        var valid = new PrtgTrustedSamplingProfileStore(backend).GetMany([PrtgFormalRuleCaseFixture.SensorId])
            [PrtgFormalRuleCaseFixture.SensorId];
        Assert.Equal(PrtgResourceDecisionKind.Hit,
            SeedCurrentWindowAndEvaluate(backend, valid, 5d).Decision.Kind);
        var detail = CreateDetailQuery(backend, host).GetHostDetail(host.HostId, days: 7);
        Assert.NotEmpty(detail.ResourcePressureHints);

        var key = PrtgTrustedSamplingProfile.StorePrefix + PrtgFormalRuleCaseFixture.SensorId;
        var store = new PrtgTrustedSamplingProfileStore(backend);
        var current = store.GetMany([PrtgFormalRuleCaseFixture.SensorId])[PrtgFormalRuleCaseFixture.SensorId];
        var invalidScale = current with { Scale = 0d, MetadataDigest = "" };
        var computeDigest = typeof(PrtgTrustedSamplingProfile).GetMethod("ComputeDigest",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Profile digest helper unavailable to the invalid-profile fixture.");
        invalidScale = invalidScale with
        { MetadataDigest = (string)(computeDigest.Invoke(invalidScale, null) ?? throw new InvalidOperationException()) };
        backend.Blob(key).Mutate(_ => (System.Text.Json.JsonSerializer.Serialize(invalidScale), true));

        var changed = CreateDetailQuery(backend, host).GetHostDetail(host.HostId, days: 7);
        Assert.DoesNotContain(changed.ResourcePressureHints, item => item.SensorObjid == current.SensorObjid);
    }

    [Fact]
    public void SelfConsistentButBindingMismatchedScaleIsNotProjectedByGetHostDetail()
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        var host = fixture.SeedFormalResourceCase(PrtgResourceFamily.Disk, [5d, 5d]);
        var backend = fixture.Backend;
        var key = PrtgTrustedSamplingProfile.StorePrefix + PrtgFormalRuleCaseFixture.SensorId;
        var current = new PrtgTrustedSamplingProfileStore(backend).GetMany([PrtgFormalRuleCaseFixture.SensorId])
            [PrtgFormalRuleCaseFixture.SensorId];
        Assert.Equal(PrtgResourceDecisionKind.Hit,
            SeedCurrentWindowAndEvaluate(backend, current, 5d).Decision.Kind);
        Assert.Contains(CreateDetailQuery(backend, host).GetHostDetail(host.HostId, days: 7).ResourcePressureHints,
            item => item.SensorObjid == current.SensorObjid);
        var forged = current with { Scale = 100d, ComparedSnapshotValue = 0.05d,
            ComparedPrimaryChannelValue = 0.05d, MetadataDigest = "" };
        var computeDigest = typeof(PrtgTrustedSamplingProfile).GetMethod("ComputeDigest",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Profile digest helper unavailable to the forged-profile fixture.");
        forged = forged with
        { MetadataDigest = (string)(computeDigest.Invoke(forged, null) ?? throw new InvalidOperationException()) };
        forged.Validate(); // Structurally valid; only the saved semantic authority must reject it.
        backend.Blob(key).Mutate(_ => (System.Text.Json.JsonSerializer.Serialize(forged), true));

        var changed = CreateDetailQuery(backend, host).GetHostDetail(host.HostId, days: 7);
        Assert.DoesNotContain(changed.ResourcePressureHints, item => item.SensorObjid == current.SensorObjid);
    }

    [Fact]
    public void ChangedSamplingAuthorityRemovesPreviouslyCurrentHintInGetHostDetail()
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        var host = fixture.SeedFormalResourceCase(PrtgResourceFamily.Disk, [5d, 5d]);
        var backend = fixture.Backend;
        var valid = new PrtgTrustedSamplingProfileStore(backend).GetMany([PrtgFormalRuleCaseFixture.SensorId])
            [PrtgFormalRuleCaseFixture.SensorId];
        Assert.Equal(PrtgResourceDecisionKind.Hit,
            SeedCurrentWindowAndEvaluate(backend, valid, 5d).Decision.Kind);
        var initial = CreateDetailQuery(backend, host).GetHostDetail(host.HostId, days: 7);
        Assert.Contains(initial.ResourcePressureHints, item => item.SensorObjid == PrtgFormalRuleCaseFixture.SensorId);

        new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
            policy.TimeBasisEvidenceReference += "-changed-after-profile");

        var changed = CreateDetailQuery(backend, host).GetHostDetail(host.HostId, days: 7);
        Assert.DoesNotContain(changed.ResourcePressureHints,
            item => item.SensorObjid == PrtgFormalRuleCaseFixture.SensorId);
    }

    private static RecordDetailQueryService CreateDetailQuery(StorageBackend backend, WebHost host)
    {
        var hosts = new HostStore(backend.Blob("hosts"));
        var visibility = new SingleHostVisibility(hosts, host.HostId);
        var repository = new RecordRepository(backend.RecordStore(), hosts, visibility,
            new FakeSystemSettingsService());
        return new RecordDetailQueryService(repository, new NullReportReader(), hosts,
            new FakeUserStore(), new FakeHostGroupStore(), visibility, new FakeIssueHandlingStore(),
            new FakeIssueCaseStore(), new FakeNoiseMarkStore(),
            new FakeRuleStore { Content = new RuleFileContent { Rules = KnownIssueSeed.CreateRules() } },
            FakeCurrentUser.WithCapabilities(Capability.Maintain),
            new SystemSettingsStore(backend.Blob("system_settings")),
            new FixedIssueExclusionSource(IssueExclusion.None), new FakeIssueOwnerStore(), backend);
    }

    private static PrtgResourcePeriodAssessment SeedCurrentWindowAndEvaluate(StorageBackend backend,
        PrtgTrustedSamplingProfile profile, double normalizedValue)
    {
        var nowUtc = DateTime.UtcNow;
        var cutoffUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0,
            DateTimeKind.Utc);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(profile.AnalysisTimeZoneId);
        var latestWallHour = TimeZoneInfo.ConvertTimeFromUtc(cutoffUtc.AddHours(-1), zone);
        latestWallHour = DateTime.SpecifyKind(latestWallHour.Date.AddHours(latestWallHour.Hour),
            DateTimeKind.Unspecified);
        using (var context = backend.CreateContext())
        {
            var stale = context.PrtgValues.Where(row => row.SensorObjid == profile.SensorObjid).ToArray();
            context.PrtgValues.RemoveRange(stale);
            context.SaveChanges();
        }
        backend.PrtgStore().MergeSampledValues(new[] { latestWallHour.AddHours(-1), latestWallHour }
            .Select(hour => TrustedHour(profile, hour, normalizedValue)).ToArray());
        return Assert.Single(new PrtgResourcePeriodConsumer(backend,
            new SystemSettingsStore(backend.Blob("system_settings"))).EvaluateBatch(
            [profile.SensorObjid], cutoffUtc, nowUtc).Assessments);
    }

    private static PrtgValueRow TrustedHour(PrtgTrustedSamplingProfile profile, DateTime wallHour,
        double normalizedValue)
    {
        var analysisZone = TimeZoneInfo.FindSystemTimeZoneById(profile.AnalysisTimeZoneId);
        var instantHour = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(wallHour, DateTimeKind.Unspecified), analysisZone);
        var slots = Enumerable.Range(0, profile.StrategyMinutes == 15 ? 4 : 12).Select(slot =>
        {
            var measuredAt = instantHour.AddMinutes(slot * profile.StrategyMinutes);
            return new PrtgTrustedSampleSlot(slot, Guid.NewGuid().ToString("N"), measuredAt,
                measuredAt.AddSeconds(30), normalizedValue);
        }).ToArray();
        var sample = new PrtgTrustedSample(profile.SensorObjid, normalizedValue, profile.SourceGeneration,
            profile.ResourceGeneration, profile.ChannelGeneration,
            profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile.SemanticVersion, profile.StrategyFingerprint, profile.StrategyMinutes,
            profile.StrategyEffectiveFromHourUtc, slots[0].MeasuredAt, slots[0].ReceivedAt,
            profile.ConfirmedScanInterval, PrtgTrustedSampleQuality.Good, "synthetic-scale-consumer-hour",
            profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId);
        var proof = PrtgTrustedSampleProof.From(sample, slots);
        return new PrtgValueRow
        {
            SensorObjid = profile.SensorObjid,
            PeriodStart = DateTime.SpecifyKind(wallHour, DateTimeKind.Unspecified),
            AvgValue = normalizedValue,
            MinValue = normalizedValue,
            MaxValue = normalizedValue,
            Coverage = 100,
            Quality = PrtgDataQuality.Sampled,
            TrustVersion = 1,
            TrustedProof = PrtgTrustedSampleProof.Serialize(proof)
        };
    }

    private sealed class SingleHostVisibility(IHostStore hosts, long hostId) : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => new HashSet<long> { hostId };
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => new HashSet<long>();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => GetVisibleHostIds();
        public List<WebHost> GetVisibleHosts() => [hosts.Get(hostId)!];
        public void EnsureVisible(long requestedHostId)
        {
            if (requestedHostId != hostId) throw new InvalidOperationException("Host is outside synthetic test visibility.");
        }
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long requestedHostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long requestedHostId) => null;
    }
}
