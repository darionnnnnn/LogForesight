using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

/// <summary>隔離 SQLite application-contract; does not claim deployed PRTG source acceptance.</summary>
public sealed class PrtgE03ExactSamplesAcceptanceTests : IDisposable
{
    private const long HostId = 43100;
    private const long MemoryDeviceId = 43101;
    private const long MemorySensorId = 43102;
    private const long DiskDeviceId = 43103;
    private const long DiskSensorId = 43104;
    private const long CpuDeviceId = 43105;
    private const long CpuSensorId = 43106;
    private const string Url = "http://prtg.example.test";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-e03-exact-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public PrtgE03ExactSamplesAcceptanceTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_directory, "e03.db")}"
        }, _directory);
    }

    [Fact]
    public void SavedCpuMemoryAndDiskAuthorityFlowsThroughParserAccumulatorStoreAndPeriodConsumer()
    {
        new KnownIssueRuleStore(_backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = KnownIssueSeed.CreateRules() });
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        settings.Update(value => { value.PrtgEnabled = true; value.PrtgUrl = Url; });
        var policies = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policies.Update(value =>
        {
            value.Revision = "e03-exact-samples-policy";
            value.CoreSystemId = "e03-test";
            value.SourceGeneration = "source-e03-current";
            value.EndpointHint = EfPrtgObservationStore.SourceHintFor(Url);
            value.ValidFrom = DateTimeOffset.UtcNow.AddDays(-10);
            value.HostIds = [HostId];
            value.SensorIds = [CpuSensorId, MemorySensorId, DiskSensorId];
            value.ConfirmedBy = "test-maintainer";
            value.SourceTimeZoneId = "UTC";
            value.SourceCultureName = "en-US";
            value.RawTimestampTimeZoneId = "UTC";
            value.AnalysisTimeZoneId = "UTC";
            value.TimeBasisEvidenceReference = "e03-controlled-time-basis";
        });
        using (var context = _backend.CreateContext())
        {
            context.PrtgDevices.AddRange(
                new PrtgDeviceRow { Objid = MemoryDeviceId, Name = "e03-memory-device" },
                new PrtgDeviceRow { Objid = DiskDeviceId, Name = "e03-disk-device" },
                new PrtgDeviceRow { Objid = CpuDeviceId, Name = "e03-cpu-device" });
            context.PrtgSensors.AddRange(
                new PrtgSensorRow { Objid = MemorySensorId, DeviceObjid = MemoryDeviceId, SensorType = "memory", Category = PrtgSensorCategories.Memory },
                new PrtgSensorRow { Objid = DiskSensorId, DeviceObjid = DiskDeviceId, SensorType = "disk", Category = PrtgSensorCategories.Disk },
                new PrtgSensorRow { Objid = CpuSensorId, DeviceObjid = CpuDeviceId, SensorType = "cpu", Category = PrtgSensorCategories.Cpu });
            context.PrtgManualMaps.Add(new PrtgManualMapRow { DeviceObjid = MemoryDeviceId, HostId = HostId });
            context.PrtgManualMaps.Add(new PrtgManualMapRow { DeviceObjid = DiskDeviceId, HostId = HostId });
            context.PrtgManualMaps.Add(new PrtgManualMapRow { DeviceObjid = CpuDeviceId, HostId = HostId });
            context.SaveChanges();
        }

        // Virtual acceptance clock: explicitly saved authority predates both complete sample hours.
        // Keeping the samples after qualification prevents this fixture from retroactively trusting history.
        var now = DateTime.UtcNow.AddHours(-6);
        var store = _backend.PrtgStore();
        var cpuIdentity = Bind(store, CpuSensorId, CpuDeviceId, "primary-cpu", now);
        var memoryIdentity = Bind(store, MemorySensorId, MemoryDeviceId, "primary-memory", now);
        var diskIdentity = Bind(store, DiskSensorId, DiskDeviceId, "primary-disk", now);
        var policy = policies.Get();
        var settingsValue = settings.Get();
        var strategyName = PrtgFetchStrategy.Normalize(settingsValue.PrtgFetchStrategy);
        var strategyProfile = PrtgFetchStrategy.Profile(strategyName);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            _backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(policy,
            strategyName, strategyProfile.SnapshotIntervalMinutes, now.AddDays(-2));
        Assert.True(strategy.Ready);

        var bindings = new PrtgTrustedSamplingBindingStore(_backend);
        var cpuProfile = SaveAuthority(store, bindings, settings, policies, cpuIdentity,
            CpuSensorId, "cpu", "3", "CPU Load", PrtgTrustedQuantitySemantic.CpuLoadPercent,
            "direct", 25.0834, strategy, now);
        var memoryProfile = SaveAuthority(store, bindings, settings, policies, memoryIdentity,
            MemorySensorId, "memory", "4", "Available Memory", PrtgTrustedQuantitySemantic.MemoryAvailablePercent,
            "direct", 25.0834, strategy, now);
        var diskProfile = SaveAuthority(store, bindings, settings, policies, diskIdentity,
            DiskSensorId, "disk", "5", "Free Space", PrtgTrustedQuantitySemantic.DiskFreePercent,
            "direct", 90.0887, strategy, now);
        var profileStore = new PrtgTrustedSamplingProfileStore(_backend);
        profileStore.RecordProbeResult(cpuProfile);
        profileStore.RecordProbeResult(memoryProfile);
        profileStore.RecordProbeResult(diskProfile);
        cpuIdentity = store.GetResourceIdentity(CpuSensorId);
        memoryIdentity = store.GetResourceIdentity(MemorySensorId);
        diskIdentity = store.GetResourceIdentity(DiskSensorId);

        var anchor = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
        var firstHour = anchor;
        var secondHour = anchor.AddHours(1);
        var accumulator = new PrtgSnapshotAccumulator();
        var parser = new PrtgTrustedSnapshotParser();
        AddTwoCompleteHours(parser, accumulator, cpuProfile, cpuIdentity, policy, strategyName,
            strategyProfile.SnapshotIntervalMinutes, firstHour, secondHour, 25.0834, "cpu");
        AddTwoCompleteHours(parser, accumulator, memoryProfile, memoryIdentity, policy, strategyName,
            strategyProfile.SnapshotIntervalMinutes, firstHour, secondHour, 25.0834, "memory");
        AddTwoCompleteHours(parser, accumulator, diskProfile, diskIdentity, policy, strategyName,
            strategyProfile.SnapshotIntervalMinutes, firstHour, secondHour, 90.0887, "disk");

        var rows = accumulator.DrainAll(4, DateTime.UtcNow);
        Assert.Equal(6, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.TrustedProof));
        Assert.Equal(25.0834, Assert.Single(rows, row => row.SensorObjid == CpuSensorId && row.PeriodStart == firstHour).AvgValue!.Value, 8);
        Assert.Equal(25.0834, Assert.Single(rows, row => row.SensorObjid == CpuSensorId && row.PeriodStart == secondHour).AvgValue!.Value, 8);
        Assert.Equal(25.0834, Assert.Single(rows, row => row.SensorObjid == MemorySensorId && row.PeriodStart == firstHour).AvgValue!.Value, 8);
        Assert.Equal(25.0834, Assert.Single(rows, row => row.SensorObjid == MemorySensorId && row.PeriodStart == secondHour).AvgValue!.Value, 8);
        Assert.Equal(90.0887, Assert.Single(rows, row => row.SensorObjid == DiskSensorId && row.PeriodStart == firstHour).AvgValue!.Value, 8);
        Assert.Equal(90.0887, Assert.Single(rows, row => row.SensorObjid == DiskSensorId && row.PeriodStart == secondHour).AvgValue!.Value, 8);
        Assert.Equal(6, store.MergeSampledValues(rows));

        using (var context = _backend.CreateContext())
        {
            var persisted = context.PrtgValues.OrderBy(row => row.SensorObjid).ThenBy(row => row.PeriodStart).ToArray();
            Assert.Equal(6, persisted.Length);
            Assert.All(persisted, row => Assert.Equal(100, row.Coverage));
            Assert.All(persisted, row => Assert.Equal(1, row.TrustVersion));
            Assert.All(persisted, row => Assert.False(string.IsNullOrWhiteSpace(row.TrustedProof)));
        }

        var result = new PrtgResourcePeriodConsumer(_backend, settings).EvaluateBatch(
            [CpuSensorId, MemorySensorId, DiskSensorId], secondHour.AddHours(1).AddMinutes(1), DateTime.UtcNow);
        Assert.Empty(result.FormalFindings);
        Assert.Equal(new[] { MemorySensorId, DiskSensorId, CpuSensorId }.Order().ToArray(),
            result.Assessments.Select(item => item.SensorObjid).Order().ToArray());
        var cpu = Assert.Single(result.Assessments, item => item.SensorObjid == CpuSensorId);
        Assert.Equal(PrtgResourceDecisionKind.Recovery, cpu.Decision.Kind);
        Assert.Equal(25.0834, cpu.Decision.EarlierHourAveragePercent!.Value, 8);
        Assert.Equal(25.0834, cpu.Decision.LatestHourAveragePercent!.Value, 8);
        Assert.Equal(100, cpu.Decision.WindowCoveragePercent);
        Assert.Equal(2, cpu.Decision.Window.Count);
        Assert.All(cpu.Decision.Window, hour => Assert.Equal(25.0834, hour.AveragePercent, 8));
        var memory = Assert.Single(result.Assessments, item => item.SensorObjid == MemorySensorId);
        Assert.Equal(PrtgResourceDecisionKind.Recovery, memory.Decision.Kind);
        Assert.Equal(PrtgResourceDecisionMode.Hint, memory.Decision.Mode);
        Assert.Equal(74.9166, memory.Decision.EarlierHourAveragePercent!.Value, 8);
        Assert.Equal(74.9166, memory.Decision.LatestHourAveragePercent!.Value, 8);
        Assert.Equal(100, memory.Decision.WindowCoveragePercent);
        Assert.Equal(2, memory.Decision.Window.Count);
        Assert.All(memory.Decision.Window, hour => Assert.Equal(74.9166, hour.AveragePercent, 8));
        var disk = Assert.Single(result.Assessments, item => item.SensorObjid == DiskSensorId);
        Assert.Equal(PrtgResourceDecisionKind.Recovery, disk.Decision.Kind);
        Assert.Equal(90.0887, disk.Decision.EarlierHourAveragePercent!.Value, 8);
        Assert.Equal(90.0887, disk.Decision.LatestHourAveragePercent!.Value, 8);
        Assert.Equal(100, disk.Decision.WindowCoveragePercent);
        Assert.Equal(2, disk.Decision.Window.Count);
        Assert.All(disk.Decision.Window, hour => Assert.Equal(90.0887, hour.AveragePercent, 8));
    }

    [Fact]
    public void PingAndTrafficRemainCompatibilityDiagnosticsWithoutTrustedResourceSemantics()
    {
        Assert.Contains(PrtgCompatibilityProbe.TargetCategories, target => target.Category == "ping");
        Assert.Contains(PrtgCompatibilityProbe.TargetCategories, target => target.Category == "traffic");
        Assert.DoesNotContain(Enum.GetNames<PrtgTrustedQuantitySemantic>(), name =>
            name.Contains("Ping", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Traffic", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { PrtgResourceFamily.Cpu, PrtgResourceFamily.Memory, PrtgResourceFamily.Disk },
            Enum.GetValues<PrtgResourceFamily>());
    }

    private PrtgResourceIdentity Bind(EfPrtgStore store, long sensorId, long deviceId, string channelFingerprint,
        DateTime observedAt)
    {
        var identity = store.BindObservedResource(sensorId, HostId, "source-e03-current",
            PrtgTimelineResourceIdentity.BuildResourceFingerprint(deviceId.ToString(),
                sensorId == CpuSensorId ? "cpu" : sensorId == MemorySensorId ? "memory" : "disk", $"created-{sensorId}", 0));
        return store.SetObservedChannel(sensorId, "source-e03-current", channelFingerprint, identity.Generation);
    }

    private static PrtgTrustedSamplingProfile SaveAuthority(EfPrtgStore store, PrtgTrustedSamplingBindingStore bindings,
        ISystemSettingsStore settings, PrtgMonitoringPolicyStore policies, PrtgResourceIdentity identity,
        long sensorId, string sensorType, string channelId, string caption,
        PrtgTrustedQuantitySemantic quantity, string direction, double comparedValue,
        PrtgTrustedSamplingStrategyContext strategy, DateTime now)
    {
        var settingsRevision = settings.Get().Revision;
        var policy = policies.Get();
        var saved = bindings.Save(new(sensorId, settingsRevision, policy.Revision,
            identity.Epoch, identity.ChannelGeneration, 0, channelId, caption, quantity, "%", 1,
            direction, "seconds", "UTC", "UTC", policy.TimeBasisEvidenceReference));
        var observedAt = new DateTimeOffset(now);
        var measuredOa = now.ToOADate();
        var qualified = bindings.RecordQualification(sensorId, saved.BindingRevision,
            saved.BindingFingerprint, settingsRevision, policy.Revision, comparedValue,
            measuredOa, "e03-controlled-source-v1", observedAt, new string('D', 64));
        identity = store.GetResourceIdentity(sensorId);
        var profile = PrtgTrustedSamplingProfile.FromProbe(sensorId, identity, sensorType, channelId,
            caption, quantity, "%", 1, direction, "e03-exact-sample-v1", strategy.StrategyFingerprint,
            strategy.StrategyMinutes, strategy.EffectiveFromHourUtc, TimeSpan.FromMinutes(15), "seconds",
            "UTC", "UTC", "UTC", observedAt, "controlled-source-metadata-reference",
            "controlled-physical-sample-comparison", false, comparedValue, comparedValue,
            measuredOa, measuredOa, PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind,
            qualified.BindingRevision, qualified.BindingFingerprint, channelId,
            qualified.QualificationProofReference, qualified.SettingsRevision, qualified.PolicyRevision,
            qualified.AuthorityContextFingerprint);
        return profile;
    }

    private static void AddTwoCompleteHours(PrtgTrustedSnapshotParser parser, PrtgSnapshotAccumulator accumulator,
        PrtgTrustedSamplingProfile profile, PrtgResourceIdentity identity, PrtgMonitoringPolicy policy,
        string strategyName, int strategyMinutes, DateTime firstHour, DateTime secondHour,
        double rawValue, string sensorType)
    {
        var resolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy,
            profile.SensorObjid, sensorType, strategyName, strategyMinutes,
            profile.StrategyEffectiveFromHourUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow,
            DateTime.UtcNow);
        Assert.True(resolution.Ready, resolution.RejectionReason);
        Assert.Equal(profile.PrimaryChannelId, resolution.Context!.SelectedPrimaryChannelId);
        Assert.True(resolution.Context.RequireCurrentNativePrimaryChannelId);
        var hours = new[] { firstHour, secondHour };
        foreach (var hour in hours)
        for (var slot = 0; slot < 4; slot++)
        {
            var measuredAt = hour.AddMinutes(slot * 15);
            var receivedAt = measuredAt.AddSeconds(10);
            var asOf = measuredAt.AddSeconds(20);
            var context = resolution.Context with { ReceivedAt = receivedAt, AsOf = asOf };
            var raw = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["objid"] = profile.SensorObjid,
                ["lastvalue_raw"] = rawValue,
                ["lastcheck_raw"] = measuredAt.ToOADate(),
                ["interval_raw"] = 900,
                ["status_raw"] = 3
            });
            var bracket = new PrtgNativePrimaryChannelBracket(profile.SensorObjid,
                profile.PrimaryChannelId, profile.PrimaryChannelId,
                receivedAt.AddSeconds(-1), receivedAt, receivedAt.AddSeconds(1));
            var disposition = parser.AddToAccumulator(raw, context, accumulator, bracket, out var rejection);
            Assert.True(disposition == PrtgTrustedSampleDisposition.Accepted,
                $"{profile.SensorObjid} {hour:O} slot={slot}: {disposition} ({rejection ?? "no rejection detail"})");
            Assert.Null(rejection);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
