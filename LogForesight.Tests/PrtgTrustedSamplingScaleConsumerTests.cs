using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

/// <summary>合成 application-contract tests for configured raw-to-percent scale.</summary>
public sealed class PrtgTrustedSamplingScaleConsumerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-profile-scale-" + Guid.NewGuid().ToString("N"));
    private const long SensorId = 42001;
    private const long DeviceId = 42002;
    private const long HostId = 42003;
    private const string Url = "http://prtg.example.test";
    private readonly StorageBackend _backend;

    public PrtgTrustedSamplingScaleConsumerTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "scale.db")}"
        }, _directory);
    }

    [Fact]
    public void HalfScaleNormalizesRawOnceThroughParserConsumerAndFormalFence()
    {
        // Synthetic contract only; these rows do not claim any deployed PRTG source support.
        var backend = _backend;
        new KnownIssueRuleStore(backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = KnownIssueSeed.CreateRules() });
        var settings = new SystemSettingsStore(backend.Blob("system_settings"));
        settings.Update(value => { value.PrtgEnabled = true; value.PrtgUrl = Url; });
        var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(value =>
        {
            value.Revision = "scaled-resource-period-test";
            value.CoreSystemId = "core-test";
            value.SourceGeneration = "source-current";
            value.EndpointHint = EfPrtgObservationStore.SourceHintFor(Url);
            value.ValidFrom = DateTimeOffset.UtcNow.AddDays(-10);
            value.HostIds = [HostId];
            value.SensorIds = [SensorId];
            value.ConfirmedBy = "test-maintainer";
            value.SourceTimeZoneId = "UTC";
            value.SourceCultureName = "en-US";
            value.RawTimestampTimeZoneId = "UTC";
            value.AnalysisTimeZoneId = "UTC";
            value.TimeBasisEvidenceReference = "verified-time-basis";
        });
        using (var context = backend.CreateContext())
        {
            context.PrtgDevices.Add(new PrtgDeviceRow { Objid = DeviceId, Name = "scaled-resource-test-device" });
            context.PrtgSensors.Add(new PrtgSensorRow
                { Objid = SensorId, DeviceObjid = DeviceId, SensorType = "cpu", Category = PrtgSensorCategories.Cpu });
            context.PrtgManualMaps.Add(new PrtgManualMapRow { DeviceObjid = DeviceId, HostId = HostId });
            context.SaveChanges();
        }

        var store = backend.PrtgStore();
        var identity = store.BindObservedResource(SensorId, HostId, "source-current", "scaled-resource-fingerprint");
        identity = store.SetObservedChannel(SensorId, "source-current", "primary-channel|resource-period-v1",
            identity.Generation);
        var policy = policyStore.Get();
        var settingsValue = settings.Get();
        var strategyName = PrtgFetchStrategy.Normalize(settingsValue.PrtgFetchStrategy);
        var strategyProfile = PrtgFetchStrategy.Profile(strategyName);
        var now = DateTime.UtcNow.AddSeconds(-30);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(policy,
            strategyName, strategyProfile.SnapshotIntervalMinutes, now.AddDays(-2));
        Assert.True(strategy.Ready);

        var bindingStore = new PrtgTrustedSamplingBindingStore(backend);
        var saved = bindingStore.Save(new(SensorId, settingsValue.Revision, policy.Revision,
            identity.Epoch, identity.ChannelGeneration, 0, "3", "CPU Usage",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 0.5, "direct", "seconds",
            "UTC", "UTC", policy.TimeBasisEvidenceReference));
        var observedAt = new DateTimeOffset(now);
        var qualified = bindingStore.RecordQualification(SensorId, saved.BindingRevision,
            saved.BindingFingerprint, settings.Get().Revision, policyStore.Get().Revision,
            190, now.AddSeconds(-1).ToOADate(), "synthetic-prtg-v1", observedAt, new string('B', 64));
        identity = store.GetResourceIdentity(SensorId);
        var profile = PrtgTrustedSamplingProfile.FromProbe(SensorId, identity, "cpu", "3", "CPU Usage",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 0.5, "direct", "resource-period-v1",
            strategy.StrategyFingerprint, strategy.StrategyMinutes, strategy.EffectiveFromHourUtc,
            TimeSpan.FromSeconds(60), "seconds", "UTC", "UTC", "UTC", observedAt,
            "synthetic-source-metadata-reference", "synthetic-physical-sample-reference", false,
            190, 190, now.ToOADate(), now.ToOADate(), PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind,
            qualified.BindingRevision, qualified.BindingFingerprint, "3", qualified.QualificationProofReference,
            qualified.SettingsRevision, qualified.PolicyRevision, qualified.AuthorityContextFingerprint);
        new PrtgTrustedSamplingProfileStore(backend).RecordProbeResult(profile);

        var resolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy, SensorId, "cpu",
            strategyName, strategyProfile.SnapshotIntervalMinutes, strategy.EffectiveFromHourUtc,
            now.AddSeconds(5), now.AddSeconds(10), now.AddSeconds(10));
        Assert.True(resolution.Ready, resolution.RejectionReason);
        foreach (var forged in new[]
        {
            RebuildProfile(profile, identity, scale: 100),
            RebuildProfile(profile, identity, quantity: PrtgTrustedQuantitySemantic.MemoryUsedPercent),
            RebuildProfile(profile, identity, caption: "Forged caption"),
            RebuildProfile(profile, identity, proofReference: new string('C', 64))
        })
        {
            var rejected = PrtgTrustedSamplingProfileResolver.Resolve(forged, identity, policy, SensorId, "cpu",
                strategyName, strategyProfile.SnapshotIntervalMinutes, strategy.EffectiveFromHourUtc,
                now.AddSeconds(5), now.AddSeconds(10), now.AddSeconds(10));
            Assert.False(rejected.Ready);
            Assert.Equal("profile_binding_semantics_mismatch", rejected.RejectionReason);
        }
        var rawJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["objid"] = SensorId, ["primarychannel_raw"] = 3, ["lastvalue_raw"] = 190d,
            ["lastcheck_raw"] = now.ToOADate(), ["interval_raw"] = 60, ["status_raw"] = 3
        });
        var parsed = new PrtgTrustedSnapshotParser().Parse(rawJson, resolution.Context!);
        Assert.True(parsed.AcceptedForAccumulation, parsed.RejectionReason);
        Assert.Equal(95d, parsed.Sample!.Value);

        var evidenceCutoff = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        for (var attempt = 0; attempt < 48; attempt++)
        {
            var earlierDay = TimeZoneInfo.ConvertTimeFromUtc(evidenceCutoff.AddHours(-2), TimeZoneInfo.Local).Date;
            var laterDay = TimeZoneInfo.ConvertTimeFromUtc(evidenceCutoff.AddHours(-1), TimeZoneInfo.Local).Date;
            if (earlierDay == laterDay) break;
            evidenceCutoff = evidenceCutoff.AddHours(-1);
        }
        Assert.Equal(TimeZoneInfo.ConvertTimeFromUtc(evidenceCutoff.AddHours(-2), TimeZoneInfo.Local).Date,
            TimeZoneInfo.ConvertTimeFromUtc(evidenceCutoff.AddHours(-1), TimeZoneInfo.Local).Date);
        store.MergeSampledValues(new[] { evidenceCutoff.AddHours(-2), evidenceCutoff.AddHours(-1) }
            .Select(hour => TrustedHour(profile, hour, parsed.Sample.Value)).ToArray());

        var consumer = new PrtgResourcePeriodConsumer(backend, settings);
        var assessment = Assert.Single(consumer.EvaluateBatch([SensorId], evidenceCutoff,
            now.AddSeconds(10)).Assessments);
        Assert.Equal(PrtgResourceDecisionKind.Hit, assessment.Decision.Kind);
        Assert.Equal(95d, assessment.Decision.EarlierHourAveragePercent!.Value);
        Assert.Equal(95d, assessment.Decision.LatestHourAveragePercent!.Value);

        var authorization = new PrtgResourcePressureAuthorizationService(backend, settings);
        var trial = authorization.IssueSuccessfulTrial(assessment, maintainAuthorized: true, now.AddSeconds(10));
        Assert.True(authorization.SetFormalMode(assessment, trial.TrialResultId, enabled: true,
            maintainAuthorized: true, now.AddSeconds(10)));
        var formal = Assert.Single(consumer.EvaluateBatch([SensorId], evidenceCutoff,
            DateTime.UtcNow).FormalFindings);
        var signature = PrtgFindingMapper.ToSignature(formal, assessment.SingleWindowHostDay!.Value);
        Assert.True(PrtgResourceProfileQualification.IsCurrentFormalPressure(backend, HostId, SensorId,
            signature, DateTime.UtcNow));
    }

    private static PrtgValueRow TrustedHour(PrtgTrustedSamplingProfile profile, DateTime wallHour, double value)
    {
        var slots = Enumerable.Range(0, profile.StrategyMinutes == 15 ? 4 : 12).Select(slot =>
        {
            var measuredAt = wallHour.AddMinutes(slot * profile.StrategyMinutes);
            var physicalId = Guid.NewGuid().ToString("N");
            return new PrtgTrustedSampleSlot(slot, physicalId, measuredAt, measuredAt.AddSeconds(30), value);
        }).ToArray();
        var sample = new PrtgTrustedSample(profile.SensorObjid, value, profile.SourceGeneration,
            profile.ResourceGeneration, profile.ChannelGeneration,
            profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile.SemanticVersion, profile.StrategyFingerprint, profile.StrategyMinutes,
            profile.StrategyEffectiveFromHourUtc, slots[0].MeasuredAt, slots[0].ReceivedAt,
            profile.ConfirmedScanInterval, PrtgTrustedSampleQuality.Good, "physical-hour",
            profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId);
        var proof = PrtgTrustedSampleProof.From(sample, slots);
        return new PrtgValueRow
        {
            SensorObjid = profile.SensorObjid,
            PeriodStart = DateTime.SpecifyKind(wallHour, DateTimeKind.Unspecified),
            AvgValue = value, MinValue = value, MaxValue = value, Coverage = 100,
            Quality = PrtgDataQuality.Sampled, TrustVersion = 1,
            TrustedProof = PrtgTrustedSampleProof.Serialize(proof)
        };
    }

    private static PrtgTrustedSamplingProfile RebuildProfile(PrtgTrustedSamplingProfile source,
        PrtgResourceIdentity identity, double? scale = null, PrtgTrustedQuantitySemantic? quantity = null,
        string? caption = null, string? proofReference = null) =>
        PrtgTrustedSamplingProfile.FromProbe(source.SensorObjid, identity, source.SensorType,
            source.PrimaryChannelId, caption ?? source.PrimaryChannelCaption, quantity ?? source.Quantity,
            source.Unit, scale ?? source.Scale, source.Direction, source.SemanticVersion,
            source.StrategyFingerprint, source.StrategyMinutes, source.StrategyEffectiveFromHourUtc,
            source.ConfirmedScanInterval, source.IntervalRawUnit, source.RawTimestampTimeZoneId,
            source.SourceApiTimeZoneId, source.AnalysisTimeZoneId, source.SourceMetadataObservedAtUtc,
            source.SourceMetadataReference, source.PhysicalSampleReference, source.SourceMarkedPrimary,
            scale.HasValue ? source.ComparedSnapshotValue * source.Scale / scale.Value : source.ComparedSnapshotValue,
            scale.HasValue ? source.ComparedPrimaryChannelValue * source.Scale / scale.Value : source.ComparedPrimaryChannelValue,
            source.ComparedSnapshotMeasurementOaDate, source.ComparedPrimaryChannelMeasurementOaDate,
            source.AuthorityKind, source.BindingRevision, source.BindingFingerprint,
            source.NativePrimaryChannelPropertyId,
            proofReference ?? source.QualificationProofReference, source.BindingSettingsRevision,
            source.BindingPolicyRevision, source.AuthorityContextFingerprint);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
