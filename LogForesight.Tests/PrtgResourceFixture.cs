using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;

namespace LogForesight.Tests;

/// <summary>以正式 SQL store 入口建立測試用可信 PRTG 資源與通道身分。</summary>
internal static class PrtgResourceFixture
{
    public static PrtgResourceIdentity Bind(EfPrtgStore store, EfJsonBlobStore policyBlob,
        long sensorId, long deviceId, long hostId, string sourceGeneration,
        string? resourceFingerprint = null, string sensorType = "disk", string? creationReference = null)
    {
        new PrtgMonitoringPolicyStore(policyBlob).Update(policy =>
        {
            policy.Revision = "trusted-resource-fixture";
            policy.SourceGeneration = sourceGeneration;
            policy.HostIds = [hostId];
            policy.SensorIds = [sensorId];
        });
        return store.BindObservedResource(sensorId, hostId, sourceGeneration,
            resourceFingerprint ?? PrtgTimelineResourceIdentity.BuildResourceFingerprint(
                deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture), sensorType,
                creationReference ?? $"fixture-created-{sensorId}", 0));
    }

    public static PrtgResourceIdentity BindChannel(EfPrtgStore store, PrtgResourceIdentity identity,
        string channelIdentifier, string channelName, string unit, double scale, string direction)
    {
        var fingerprint = JsonSerializer.Serialize(new
        {
            ChannelIdentifier = channelIdentifier,
            ChannelName = channelName,
            Unit = unit,
            Scale = (double?)scale,
            Direction = direction
        }) + "|" + PrtgDiskAssessmentService.ParserSemanticVersion;
        return store.SetObservedChannel(identity.SensorId, identity.SourceGeneration,
            fingerprint, identity.Generation);
    }

    public static PrtgTrustedSamplingProfile ConfigureDiskTrustedProfile(EfPrtgStore store,
        EfJsonBlobStore policyBlob, EfJsonBlobStore strategyBlob, long sensorId, long deviceId,
        long hostId, string sensorType, DateTime effectiveFromHourUtc,
        DateTimeOffset? observedAtUtc = null, string? creationReference = null,
        string sourceGeneration = "readiness-source", string channelCaption = "Free Space", bool preserveIdentity = false,
        string channelIdentifier = "free")
    {
        var source = sourceGeneration;
        new PrtgMonitoringPolicyStore(policyBlob).Update(policy =>
        {
            policy.Revision = "trusted-history-fixture";
            policy.SourceGeneration = source;
            policy.SourceTimeZoneId = "UTC";
            policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "fixture-time-basis-proof";
            policy.HostIds = [hostId];
            policy.SensorIds = [sensorId];
        });
        var identity = preserveIdentity ? store.GetResourceIdentity(sensorId) : Bind(store, policyBlob, sensorId, deviceId, hostId, source,
            sensorType: sensorType, creationReference: creationReference);
        if (!preserveIdentity || !PrtgConsumerProfileFixtureClosure.HasCurrentQualifiedBinding(policyBlob, identity))
            identity = BindChannel(store, identity, channelIdentifier, channelCaption, "%", 1, "descending-danger");
        var policy = new PrtgMonitoringPolicyStore(policyBlob).Get();
        var strategy = new PrtgTrustedSamplingStrategyStateStore(strategyBlob).GetCurrent(policy,
            "conservative", 15, effectiveFromHourUtc);
        if (!strategy.Ready || strategy.EffectiveFromHourUtc != effectiveFromHourUtc)
            throw new InvalidOperationException("Trusted history fixture strategy did not resolve to its requested start.");
        var observed = observedAtUtc ?? DateTimeOffset.UtcNow;
        var sourceProfile = PrtgTrustedSamplingProfile.FromProbe(sensorId, identity, sensorType, channelIdentifier, channelCaption,
            PrtgTrustedQuantitySemantic.DiskFreePercent, "%", 1, "direct",
            PrtgDiskAssessmentService.ParserSemanticVersion, strategy.StrategyFingerprint,
            strategy.StrategyMinutes, strategy.EffectiveFromHourUtc, TimeSpan.FromMinutes(15),
            "minutes", "UTC", "UTC", "UTC", observed, "typed-source-metadata-reference",
            "same-physical-sample-compared", true, 50, 50,
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).ToOADate(),
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).ToOADate());
        var profile = PrtgConsumerProfileFixtureClosure.PublishEfFixture(store, policyBlob,
            "synthetic-settings-revision", "conservative", sourceProfile);
        return profile;
    }

    public static PrtgValueRow TrustedDiskHour(long sensorId, DateTime periodStart,
        PrtgTrustedSamplingProfile profile, double value = 50)
    {
        var wallHour = DateTime.SpecifyKind(periodStart, DateTimeKind.Unspecified);
        var instantHour = DateTime.SpecifyKind(wallHour, DateTimeKind.Utc);
        var slots = Enumerable.Range(0, 4).Select(slot =>
        {
            var measured = instantHour.AddMinutes(slot * 15);
            var material = $"{sensorId}|{wallHour:O}|{slot}";
            var physical = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
            return new PrtgTrustedSampleSlot(slot, physical, measured, measured.AddSeconds(10), value);
        }).ToArray();
        var proof = PrtgTrustedSampleProof.From(new PrtgTrustedSample(sensorId, value,
            profile.SourceGeneration, profile.ResourceGeneration, profile.ChannelGeneration,
            profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile.SemanticVersion, profile.StrategyFingerprint, profile.StrategyMinutes,
            profile.StrategyEffectiveFromHourUtc, slots[0].MeasuredAt, slots[0].ReceivedAt,
            profile.ConfirmedScanInterval, PrtgTrustedSampleQuality.Good, "fixture-physical-sample",
            profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId), slots);
        return new PrtgValueRow
        {
            SensorObjid = sensorId,
            PeriodStart = wallHour,
            AvgValue = value,
            MinValue = value,
            MaxValue = value,
            Coverage = 100,
            Quality = PrtgDataQuality.Sampled,
            CreatedAt = DateTime.UtcNow,
            TrustVersion = 1,
            TrustedProof = PrtgTrustedSampleProof.Serialize(proof)
        };
    }
    // Only test setup: rebuild every seeded hourly value with an explicit current-context slot proof.
    // This is never called by application code and does not attest real source compatibility.
    public static void AuthorizeSeededDiskHistory(Func<LfDbContext> contextFactory, long sensorId, long deviceId,
        long hostId, string sensorType, string sourceGeneration, string caption = "Free", string channelIdentifier = "free")
    {
        var store = new EfPrtgStore(contextFactory);
        using var db = contextFactory();
        var hours = db.PrtgValues.Where(row => row.SensorObjid == sensorId).OrderBy(row => row.PeriodStart).ToList();
        if (hours.Count == 0) return;
        var effective = DateTime.SpecifyKind(hours[0].PeriodStart.AddDays(-1), DateTimeKind.Utc);
        var profile = ConfigureDiskTrustedProfile(store,
            new EfJsonBlobStore(contextFactory, PrtgMonitoringPolicyStore.BlobKey),
            new EfJsonBlobStore(contextFactory, PrtgTrustedSamplingStrategyStateStore.BlobKey),
            sensorId, deviceId, hostId, sensorType, effective, sourceGeneration: sourceGeneration,
            channelCaption: caption, preserveIdentity: store.GetResourceIdentity(sensorId).Active,
            channelIdentifier: channelIdentifier);
        foreach (var hour in hours)
        {
            var trusted = TrustedDiskHour(sensorId, hour.PeriodStart, profile, hour.AvgValue!.Value);
            hour.TrustVersion = trusted.TrustVersion; hour.TrustedProof = trusted.TrustedProof;
            hour.Quality = trusted.Quality; hour.Coverage = trusted.Coverage;
            hour.MinValue = trusted.MinValue; hour.MaxValue = trusted.MaxValue;
        }
        db.SaveChanges();
    }

}
