using LogForesight.Core.Service;
using LogForesight.Core.Persistence;
using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourceConsumerContractTests
{
    [Fact]
    public void SelectedSensorFingerprintIsStableForOrderingAndChangesWithScope()
    {
        var policy = new PrtgMonitoringPolicy { Revision = "rev-4", SourceGeneration = "src-a" };
        var first = PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(policy,
            [20, 10], [400, 300]);
        var reordered = PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(policy,
            [10, 20], [300, 400]);
        var changedScope = PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(policy,
            [10, 21], [300, 400]);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changedScope);
    }

    [Fact]
    public void SelectionEpochChangesWhenAnySelectedResourceGenerationAdvances()
    {
        var first = new PrtgResourceIdentity
        {
            SensorId = 400, HostId = 10, DeviceId = 20, SourceGeneration = "src-a",
            Generation = "resource-a", ChannelGeneration = "channel-a", Epoch = 3,
            Active = true
        };
        var original = PrtgResourcePeriodConsumer.ComputeSelectionEpoch([first]);
        var changedIdentity = new PrtgResourceIdentity
        {
            SensorId = first.SensorId, HostId = first.HostId, DeviceId = first.DeviceId,
            SourceGeneration = first.SourceGeneration, Generation = "resource-b",
            ChannelGeneration = first.ChannelGeneration, Epoch = 4, Active = true
        };
        var changed = PrtgResourcePeriodConsumer.ComputeSelectionEpoch([changedIdentity]);

        Assert.NotEqual(original, changed);
    }

    [Fact]
    public void StableMeasurementFingerprintIgnoresProbeRefreshButTracksSemanticContract()
    {
        var original = Profile();
        var refreshed = original with
        {
            SourceMetadataObservedAtUtc = DateTimeOffset.UtcNow,
            PhysicalSampleReference = "sample-reference-next",
            ComparedSnapshotValue = 91,
            ComparedPrimaryChannelValue = 91,
            MetadataDigest = "digest-next"
        };

        Assert.Equal(PrtgResourcePeriodConsumer.StableProfileFingerprint(original),
            PrtgResourcePeriodConsumer.StableProfileFingerprint(refreshed));
        Assert.NotEqual(PrtgResourcePeriodConsumer.StableProfileFingerprint(original),
            PrtgResourcePeriodConsumer.StableProfileFingerprint(original with { Unit = "bytes" }));
        Assert.NotEqual(PrtgResourcePeriodConsumer.StableProfileFingerprint(original),
            PrtgResourcePeriodConsumer.StableProfileFingerprint(original with { PrimaryChannelId = "channel-b" }));
        Assert.NotEqual(PrtgResourcePeriodConsumer.StableProfileFingerprint(original),
            PrtgResourcePeriodConsumer.StableProfileFingerprint(original with { ConfirmedScanInterval = TimeSpan.FromMinutes(5) }));
    }

    [Fact]
    public void CurrentRuleAdmissionFingerprintTracksExactRuleAndIndependentTrendEnablement()
    {
        using var fixture = new EfSqliteFixture();
        var store = new KnownIssueRuleStore(fixture.Blob("rules"));
        var rules = KnownIssueSeed.CreateRules();
        store.Save(new RuleFileContent { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        var baseline = PrtgResourceCurrentRuleCatalog.Load(store);
        var cpu = baseline.For(PrtgResourceFamily.Cpu)!;
        Assert.Equal(PrtgResourceCurrentRuleCatalog.ComputeRuleFingerprint(cpu.Rule), cpu.Fingerprint);
        Assert.False(baseline.DiskTrendEnabled);

        var trend = rules.Single(r => r.Id == "builtin-prtg-disk-free-trend");
        var trendJson = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(trend))!;
        trendJson[nameof(KnownIssueRule.Enabled)] = true;
        trend = System.Text.Json.JsonSerializer.Deserialize<KnownIssueRule>(trendJson.ToJsonString())!;
        rules = rules.Select(rule => rule.Id == trend.Id ? trend : rule).ToList();
        store.Save(new RuleFileContent { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        var trendEnabled = PrtgResourceCurrentRuleCatalog.Load(store);
        Assert.True(trendEnabled.DiskTrendEnabled);
        Assert.Equal(trend.Id, trendEnabled.DiskTrendRuleId);
        Assert.Equal(PrtgResourceCurrentRuleCatalog.ComputeRuleFingerprint(trend), trendEnabled.DiskTrendRuleFingerprint);
        Assert.NotEqual(baseline.AdmissionFingerprintFor(PrtgResourceFamily.Disk),
            trendEnabled.AdmissionFingerprintFor(PrtgResourceFamily.Disk));

        var disk = rules.Single(r => r.Id == "builtin-prtg-resource-disk-pressure");
        disk.Severity = IssueSeverity.Medium;
        store.Save(new RuleFileContent { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        var changedAggregateRule = PrtgResourceCurrentRuleCatalog.Load(store);
        Assert.NotEqual(trendEnabled.AdmissionFingerprintFor(PrtgResourceFamily.Disk),
            changedAggregateRule.AdmissionFingerprintFor(PrtgResourceFamily.Disk));
    }

    private static PrtgTrustedSamplingProfile Profile()
    {
        var effective = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc).AddDays(-2);
        return new(400, "src-a", "resource-a", 3, "channel-a", "cpu",
            "primary-channel", "CPU Usage", PrtgTrustedQuantitySemantic.CpuLoadPercent,
            "%", 1, "direct", "semantic-1", "strategy-1", 15, effective,
            TimeSpan.FromMinutes(15), "seconds", "UTC", "UTC", "UTC",
            DateTimeOffset.UtcNow.AddHours(-1), "metadata-ref-1", "physical-ref-1", true,
            90, 90, 45000, 45000, "digest-1");
    }
}
