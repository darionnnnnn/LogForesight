using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Small SQLite application-contract negatives; no native-source or fleet acceptance claim.</summary>
public sealed class PrtgE03JoinedNegativeMatrixTests
{
    [Theory]
    [InlineData("missing-value")]
    [InlineData("dst-ambiguous")]
    [InlineData("dst-nonexistent")]
    [InlineData("before-saved-strategy-fence")]
    public void RejectedSnapshotNeverCrossesAccumulatorStoreOrFormalConsumer(string scenario)
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [95d, 95d]);
        ClearValues(fixture.Backend);
        var (profile, identity, policy, strategy) = CurrentFacts(fixture);
        var now = DateTime.UtcNow;
        var baseContext = PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy,
            PrtgFormalRuleCaseFixture.SensorId, profile.SensorType, strategy,
            now.AddSeconds(-10), now, now);
        Assert.True(baseContext.Ready, baseContext.RejectionReason);
        var context = baseContext.Context!;
        var measuredAt = now.AddSeconds(-10);
        if (scenario == "before-saved-strategy-fence")
        {
            measuredAt = DateTime.SpecifyKind(context.StrategyEffectiveFromHour.AddHours(-1), DateTimeKind.Utc);
            // Control the evaluation clock around the historical snapshot so freshness is valid;
            // the strategy fence itself remains the one loaded from the saved SQLite authority.
            context = context with
            {
                ReceivedAt = measuredAt.AddSeconds(5),
                AsOf = measuredAt.AddSeconds(5)
            };
        }
        var expectedReason = scenario switch
        {
            "missing-value" => PrtgTrustedSnapshotParser.MissingValue,
            "dst-ambiguous" => PrtgTrustedSnapshotParser.AmbiguousMeasurementTime,
            "dst-nonexistent" => PrtgTrustedSnapshotParser.InvalidMeasurementTimeInZone,
            "before-saved-strategy-fence" => PrtgTrustedSnapshotParser.InvalidContext,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown negative scenario.")
        };
        var raw = SnapshotJson(profile, measuredAt, omitValue: scenario == "missing-value");
        if (scenario is "dst-ambiguous" or "dst-nonexistent")
        {
            context = context with { RawTimestampTimeZoneId = "Eastern Standard Time" };
            var wallTime = scenario == "dst-ambiguous"
                ? new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified)
                : new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified);
            raw = SnapshotJson(profile, wallTime, omitValue: false);
        }

        var accumulator = new PrtgSnapshotAccumulator();
        var parser = new PrtgTrustedSnapshotParser();
        Assert.Equal(PrtgTrustedSampleDisposition.Rejected,
            parser.AddToAccumulator(raw, context, accumulator, out var rejection));
        Assert.Equal(expectedReason, rejection);
        var rows = accumulator.DrainAll(100, now.AddMinutes(1));
        Assert.Empty(rows);
        Assert.Equal(0, fixture.Backend.PrtgStore().MergeSampledValues(rows));
        Assert.Empty(Evaluate(fixture).FormalFindings);
    }

    [Theory]
    [InlineData("identity-drift")]
    [InlineData("two-hour-gap")]
    public void IdentityDriftOrIncompleteWindowCannotProduceFormalPressure(string scenario)
    {
        using var fixture = new PrtgFormalRuleCaseFixture();
        fixture.SeedFormalResourceCase(PrtgResourceFamily.Cpu, [95d, 95d],
            periodMutation: scenario == "two-hour-gap" ? "gap-between-hours" : null);
        if (scenario == "identity-drift")
        {
            var store = fixture.Backend.PrtgStore();
            var identity = store.GetResourceIdentity(PrtgFormalRuleCaseFixture.SensorId);
            store.BindObservedResource(identity.SensorId, identity.HostId,
                identity.SourceGeneration, "new-resource-after-qualification");
        }

        var result = Evaluate(fixture);

        Assert.Empty(result.FormalFindings);
        if (scenario == "identity-drift")
            Assert.DoesNotContain(result.Assessments, item => item.Decision.Mode == PrtgResourceDecisionMode.FormalRisk);
        else
            Assert.DoesNotContain(result.Assessments, item => item.Decision.Kind == PrtgResourceDecisionKind.Hit &&
                item.Decision.Mode == PrtgResourceDecisionMode.FormalRisk);
    }

    private static PrtgResourcePeriodConsumption Evaluate(PrtgFormalRuleCaseFixture fixture)
    {
        var cutoff = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(fixture.AnalysisDay.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        return new PrtgResourcePeriodConsumer(fixture.Backend,
            new SystemSettingsStore(fixture.Backend.Blob("system_settings")))
            .EvaluateBatch([PrtgFormalRuleCaseFixture.SensorId], cutoff, DateTime.UtcNow);
    }

    private static (PrtgTrustedSamplingProfile Profile, PrtgResourceIdentity Identity,
        PrtgMonitoringPolicy Policy, PrtgTrustedSamplingStrategyContext Strategy) CurrentFacts(
        PrtgFormalRuleCaseFixture fixture)
    {
        var settings = new SystemSettingsStore(fixture.Backend.Blob("system_settings")).Get();
        var store = fixture.Backend.PrtgStore();
        var profile = new PrtgTrustedSamplingProfileStore(fixture.Backend)
            .GetMany([PrtgFormalRuleCaseFixture.SensorId])[PrtgFormalRuleCaseFixture.SensorId];
        var authority = store.GetTrustedSamplingPolicyContext(
            PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
            PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy).SnapshotIntervalMinutes);
        return (profile, store.GetResourceIdentity(profile.SensorObjid), authority.Policy, authority.Strategy);
    }

    private static string SnapshotJson(PrtgTrustedSamplingProfile profile, DateTime measurement,
        bool omitValue)
    {
        var fields = new Dictionary<string, object>
        {
            ["objid"] = profile.SensorObjid,
            ["lastcheck_raw"] = measurement.ToOADate(),
            ["interval_raw"] = profile.ConfirmedScanInterval.TotalSeconds,
            ["status_raw"] = 3,
            ["primarychannel_raw"] = long.Parse(profile.PrimaryChannelId,
                System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!omitValue) fields["lastvalue_raw"] = 95d;
        return JsonSerializer.Serialize(fields);
    }

    private static void ClearValues(StorageBackend backend)
    {
        using var db = backend.CreateContext();
        var rows = db.PrtgValues.Where(row => row.SensorObjid == PrtgFormalRuleCaseFixture.SensorId).ToArray();
        db.PrtgValues.RemoveRange(rows);
        db.SaveChanges();
    }
}
