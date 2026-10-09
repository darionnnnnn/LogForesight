using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTrustedSamplingProfileTests
{
    private static readonly DateTime Effective = new(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AsOf = Effective.AddMinutes(6);

    private static PrtgResourceIdentity Identity() => new()
    {
        SensorId = 77, Epoch = 4, Generation = "resource-g4", SourceGeneration = "source-g2",
        DeviceId = 88, HostId = 99, ChannelGeneration = "channel-g3", Active = true,
        PendingReconciliation = false
    };

    private static PrtgMonitoringPolicy Policy() => new()
    {
        Revision = "policy-r8", SourceGeneration = "source-g2", EndpointHint = "endpoint-hash",
        SourceTimeZoneId = "UTC", SourceCultureName = "en-US", RawTimestampTimeZoneId = "UTC",
        AnalysisTimeZoneId = "UTC", TimeBasisEvidenceReference = "probe-time-basis:20261005",
        HostIds = [99], SensorIds = [77]
    };

    private static PrtgTrustedSamplingProfile Profile(PrtgResourceIdentity? identity = null,
        PrtgTrustedQuantitySemantic quantity = PrtgTrustedQuantitySemantic.CpuLoadPercent,
        string direction = "direct", string intervalUnit = "seconds", DateTimeOffset? observed = null,
        string rawZone = "UTC", string analysisZone = "UTC", bool primary = true,
        double snapshotValue = 12.5, double primaryValue = 12.5,
        string sourceMetadataReference = "probe-metadata:cpu-77",
        string physicalSampleReference = "sample-compare:cpu-77", double channelTimeDeltaSeconds = 0)
    {
        var id = identity ?? Identity();
        var strategy = PrtgTrustedSamplingProfileResolver.StrategyFingerprint(
            id.SourceGeneration, rawZone, "UTC", analysisZone, PrtgFetchStrategy.Conservative, 15);
        return PrtgTrustedSamplingProfile.FromProbe(77, id, "CPU", "channel-id-1", "Load",
            quantity, "%", 1, direction, "cpu-percent-v1", strategy, 15, Effective,
            TimeSpan.FromSeconds(60), intervalUnit, rawZone, "UTC", analysisZone,
            observed ?? new DateTimeOffset(AsOf.AddMinutes(-1)), sourceMetadataReference,
            physicalSampleReference, primary, snapshotValue, primaryValue,
            new DateTime(2026, 10, 5, 10, 6, 0).ToOADate(),
            new DateTime(2026, 10, 5, 10, 6, 0).AddSeconds(channelTimeDeltaSeconds).ToOADate());
    }

    private static PrtgTrustedSamplingProfileResolution Resolve(PrtgTrustedSamplingProfile? profile,
        PrtgResourceIdentity? identity = null, PrtgMonitoringPolicy? policy = null,
        string sensorType = "CPU", string strategy = PrtgFetchStrategy.Conservative,
        int minutes = 15, DateTime? effective = null, DateTime? asOf = null)
    {
        var at = asOf ?? AsOf;
        return PrtgTrustedSamplingProfileResolver.Resolve(profile, identity ?? Identity(),
            policy ?? Policy(), 77, sensorType, strategy, minutes, effective ?? Effective,
            at.AddSeconds(-1), at);
    }

    [Theory]
    [InlineData(PrtgTrustedQuantitySemantic.CpuLoadPercent, "direct", 12.5, 12.5)]
    [InlineData(PrtgTrustedQuantitySemantic.MemoryUsedPercent, "direct", 12.5, 12.5)]
    [InlineData(PrtgTrustedQuantitySemantic.MemoryAvailablePercent, "inverse", -12.5, 12.5)]
    [InlineData(PrtgTrustedQuantitySemantic.DiskFreePercent, "absolute", -12.5, 12.5)]
    [InlineData(PrtgTrustedQuantitySemantic.DiskUsedPercent, "direct", 12.5, 12.5)]
    public void 完整近期profile依currentidentity與strategy解析成固定時區slot脈絡(
        PrtgTrustedQuantitySemantic quantity, string direction, double rawValue, double expected)
    {
        var profile = Profile(quantity: quantity, direction: direction, snapshotValue: rawValue, primaryValue: rawValue);
        var result = Resolve(profile);
        Assert.True(result.Ready, result.RejectionReason);
        Assert.Empty(result.MissingFacts);
        Assert.Equal(profile.PrimaryChannelId, result.Context!.SelectedPrimaryChannelId);
        Assert.Equal(15, result.Context.StrategyMinutes);
        Assert.Equal(TimeSpan.FromSeconds(60), result.Context.ConfirmedScanInterval);
        Assert.Equal(expected, result.Context.NormalizeConfirmedQuantity!(rawValue));
    }

    [Fact]
    public void 一般mappingrevision變化不混入profilestrategyfingerprint()
    {
        var before = Policy();
        var after = Policy();
        after.Revision = "policy-r9";
        var profile = Profile();
        Assert.True(Resolve(profile, policy: after).Ready);
        Assert.NotEqual(before.Revision, after.Revision);
    }

    [Fact]
    public void 選定量測intervalunit只依probe確認轉換而不猜單位()
    {
        var seconds = Profile(intervalUnit: "seconds");
        var minutes = Profile(intervalUnit: "minutes");
        Assert.True(Resolve(seconds).Ready);
        Assert.Equal(TimeSpan.FromSeconds(60), Resolve(seconds).Context!.ParseConfirmedIntervalValue!(60));
        Assert.Equal(TimeSpan.FromSeconds(60), Resolve(minutes).Context!.ParseConfirmedIntervalValue!(1));
        Assert.Null(Resolve(minutes).Context!.ParseConfirmedIntervalValue!(60));
    }

    [Fact]
    public void Profile要求物理sample時間在一秒內且接受明確小誤差()
    {
        Assert.True(Resolve(Profile(channelTimeDeltaSeconds: 0.5)).Ready);
        Assert.Throws<InvalidDataException>(() => Profile(channelTimeDeltaSeconds: 1.1));
    }

    [Fact]
    public void 已確認profile可讓批次rawsample進入trustedaccumulator()
    {
        var at = new DateTime(2026, 10, 5, 10, 6, 0, DateTimeKind.Utc);
        var result = PrtgTrustedSamplingProfileResolver.Resolve(Profile(), Identity(), Policy(), 77,
            "CPU", PrtgFetchStrategy.Conservative, 15, Effective, at.AddSeconds(5), at.AddSeconds(10));
        Assert.True(result.Ready, result.RejectionReason);
        var row = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["objid"] = 77, ["lastvalue_raw"] = "12.5", ["lastcheck_raw"] = at.ToOADate().ToString("R", CultureInfo.InvariantCulture),
            ["interval_raw"] = "60", ["status_raw"] = 3
        });
        var accumulator = new PrtgSnapshotAccumulator();
        var parser = new PrtgTrustedSnapshotParser();
        Assert.Equal(PrtgTrustedSampleDisposition.Accepted,
            parser.AddToAccumulator(row, result.Context!, accumulator, out var rejection));
        Assert.Null(rejection);
        var checkpoint = Assert.Single(accumulator.Capture());
        Assert.Equal(12.5, checkpoint.Sum / checkpoint.Count);
    }

    [Fact]
    public void 缺少probeprofile或currentidentity保持waiting()
    {
        Assert.False(Resolve(null).Ready);
        var missingIdentity = PrtgTrustedSamplingProfileResolver.Resolve(Profile(), null, Policy(), 77,
            "CPU", PrtgFetchStrategy.Conservative, 15, Effective, AsOf.AddSeconds(-1), AsOf);
        Assert.False(missingIdentity.Ready);
        Assert.Contains("current_resource_identity", missingIdentity.MissingFacts);
    }

    [Fact]
    public void Current24SafeProbe回放只有batch值與caption不足以產生可信profile()
    {
        // 24.1 safe probe established batch sensor/channel values, but no raw OA timezone,
        // selected-primary authority, typed quantity direction, or per-channel physical time.
        var result = Resolve(null);
        Assert.False(result.Ready);
        Assert.Contains("profile", result.MissingFacts);
        Assert.Equal("source_authority_incomplete", result.RejectionReason);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("pending")]
    [InlineData("epoch")]
    [InlineData("source")]
    [InlineData("resource")]
    [InlineData("channel")]
    public void Identitydrift拒絕舊profile(string drift)
    {
        var id = Identity();
        switch (drift)
        {
            case "inactive": id.Active = false; break;
            case "pending": id.PendingReconciliation = true; break;
            case "epoch": id.Epoch++; break;
            case "source": id.SourceGeneration += "-new"; break;
            case "resource": id.Generation += "-new"; break;
            case "channel": id.ChannelGeneration += "-new"; break;
        }
        var result = Resolve(Profile(), id);
        Assert.False(result.Ready);
        Assert.Equal(drift is "inactive" or "pending" ? "source_authority_incomplete" : "profile_identity_or_policy_stale", result.RejectionReason);
        if (drift is "inactive" or "pending") Assert.Contains("current_resource_identity", result.MissingFacts);
    }

    [Theory]
    [InlineData("raw-zone")]
    [InlineData("source-api-zone")]
    [InlineData("analysis-zone")]
    [InlineData("time-reference")]
    [InlineData("source-generation")]
    public void Timebasis或sourcegeneration未確認拒絕profile(string drift)
    {
        var policy = Policy();
        switch (drift)
        {
            case "raw-zone": policy.RawTimestampTimeZoneId = ""; break;
            case "source-api-zone": policy.SourceTimeZoneId = "Asia/Tokyo"; break;
            case "analysis-zone": policy.AnalysisTimeZoneId = ""; break;
            case "time-reference": policy.TimeBasisEvidenceReference = ""; break;
            case "source-generation": policy.SourceGeneration += "-new"; break;
        }
        Assert.False(Resolve(Profile(), policy: policy).Ready);
    }

    [Theory]
    [InlineData("sensor-type")]
    [InlineData("strategy")]
    [InlineData("minutes")]
    [InlineData("effective-hour")]
    [InlineData("stale")]
    [InlineData("future")]
    public void Profile與live策略或新鮮度不符拒絕(string drift)
    {
        var now = AsOf;
        var sensorType = "CPU";
        var strategy = PrtgFetchStrategy.Conservative;
        var minutes = 15;
        var effective = Effective;
        if (drift == "sensor-type") sensorType = "Memory";
        if (drift == "strategy") strategy = PrtgFetchStrategy.Aggressive;
        if (drift == "minutes") minutes = 5;
        if (drift == "effective-hour") effective = Effective.AddHours(-1);
        if (drift == "stale") now = AsOf.AddHours(25);
        if (drift == "future") now = AsOf.AddMinutes(-2);
        var profile = Profile(observed: new DateTimeOffset(AsOf.AddMinutes(-1)));
        Assert.Equal("profile_identity_or_policy_stale", Resolve(profile, sensorType: sensorType,
            strategy: strategy, minutes: minutes, effective: effective, asOf: now).RejectionReason);
    }

    [Theory]
    [InlineData("unknown-semantic")]
    [InlineData("missing-primary")]
    [InlineData("physical-value-mismatch")]
    [InlineData("physical-time-mismatch")]
    [InlineData("missing-metadata-ref")]
    [InlineData("missing-physical-ref")]
    [InlineData("unknown-zone")]
    [InlineData("unknown-interval-unit")]
    [InlineData("invalid-scale")]
    [InlineData("invalid-direction")]
    [InlineData("invalid-channel-id")]
    public void Incompleteprobeauthoritycannotcreateprofile(string defect)
    {
        var args = new ProfileArgs();
        switch (defect)
        {
            case "unknown-semantic": args.Quantity = PrtgTrustedQuantitySemantic.Unknown; break;
            case "missing-primary": args.Primary = false; break;
            case "physical-value-mismatch": args.PrimaryValue = 99; break;
            case "physical-time-mismatch": args.ChannelTimeDeltaSeconds = 2; break;
            case "missing-metadata-ref": args.SourceReference = ""; break;
            case "missing-physical-ref": args.PhysicalReference = ""; break;
            case "unknown-zone": args.RawZone = "not-a-zone"; break;
            case "unknown-interval-unit": args.IntervalUnit = "unknown"; break;
            case "invalid-scale": args.Scale = double.NaN; break;
            case "invalid-direction": args.Direction = "unspecified"; break;
            case "invalid-channel-id": args.ChannelId = ""; break;
        }
        Assert.Throws<InvalidDataException>(() => ProfileFromArgs(args));
    }

    private static PrtgTrustedSamplingProfile ProfileFromArgs(ProfileArgs a)
    {
        var id = Identity();
        var strategy = PrtgTrustedSamplingProfileResolver.StrategyFingerprint(id.SourceGeneration,
            a.RawZone, "UTC", "UTC", PrtgFetchStrategy.Conservative, 15);
        return PrtgTrustedSamplingProfile.FromProbe(77, id, "CPU", a.ChannelId, "Load", a.Quantity,
            "%", a.Scale, a.Direction, "cpu-percent-v1", strategy, 15, Effective,
            TimeSpan.FromSeconds(60), a.IntervalUnit, a.RawZone, "UTC", "UTC",
            new DateTimeOffset(AsOf.AddMinutes(-1)), a.SourceReference, a.PhysicalReference,
            a.Primary, 12.5, a.PrimaryValue, new DateTime(2026, 10, 5, 10, 6, 0).ToOADate(),
            new DateTime(2026, 10, 5, 10, 6, 0).AddSeconds(a.ChannelTimeDeltaSeconds).ToOADate());
    }

    private sealed class ProfileArgs
    {
        public PrtgTrustedQuantitySemantic Quantity = PrtgTrustedQuantitySemantic.CpuLoadPercent;
        public string Direction = "direct";
        public string IntervalUnit = "seconds";
        public string RawZone = "UTC";
        public bool Primary = true;
        public double PrimaryValue = 12.5;
        public double ChannelTimeDeltaSeconds;
        public double Scale = 1;
        public string SourceReference = "probe-metadata:cpu-77";
        public string PhysicalReference = "sample-compare:cpu-77";
        public string ChannelId = "channel-id-1";
    }
}
