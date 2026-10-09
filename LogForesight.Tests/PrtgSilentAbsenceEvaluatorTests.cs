using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSilentAbsenceEvaluatorTests
{
    private static readonly DateTime Day = new(2026, 10, 4);
    private static readonly DateTimeOffset DayStart = new(Day, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset AsOf = DayStart.AddHours(18);
    private static readonly KnownIssueRule SilentRule = new()
    {
        Id = "builtin-prtg-silent", Platform = "prtg", Enabled = true,
        PrtgRuleCode = PrtgRuleEvaluator.RuleSilent,
        PrtgThreshold = PrtgRuleCatalog.DefaultSilentThreshold,
        Severity = IssueSeverity.Medium, ElevatesDayRisk = false,
        Category = IssueCategory.Service, Description = "PRTG monitoring is silent"
    };

    private static PrtgSilentDeviceEvidence ValidDevice(DateTimeOffset? asOf = null,
        DateTimeOffset? unknownSince = null, params (long Id, string? Category, string? Status)[]? otherSensors)
    {
        var effectiveAsOf = asOf ?? AsOf;
        var effectiveUnknownSince = unknownSince ?? effectiveAsOf.AddHours(-3);
        var sensors = new List<PrtgSilentSensorEvidence>
        {
            Sensor(100, 10, PrtgSensorCategories.Availability, "Up", effectiveAsOf,
                effectiveAsOf.AddHours(-1)),
            Sensor(101, 10, PrtgSensorCategories.Cpu, "Unknown", effectiveAsOf,
                effectiveUnknownSince)
        };
        if (otherSensors is not null)
        {
            var id = 102L;
            foreach (var sensor in otherSensors)
            {
                var since = sensor.Status is null ? effectiveUnknownSince : effectiveAsOf.AddHours(-1);
                sensors.Add(Sensor(id++, 10, sensor.Category, sensor.Status, effectiveAsOf, since));
            }
        }
        return new(10, 20, "core-generation", PrtgPresenceReadQuality.Complete,
            true, false, false, sensors.Count, sensors)
        {
            RequestedAnalysisDay = Day,
            RequestedWindowStartUtc = new DateTimeOffset(Day, effectiveAsOf.Offset),
            RequestedWindowEndUtc = new DateTimeOffset(Day.AddDays(1), effectiveAsOf.Offset)
        };
    }

    private static PrtgSilentSensorEvidence Sensor(long sensorId, long deviceId, string? category,
        string? status, DateTimeOffset asOf, DateTimeOffset? statusEnteredAt, bool paused = false)
    {
        var source = "core-generation";
        var generation = $"resource-{sensorId}";
        var timeline = new PrtgSensorTimelineEvidence
        {
            QualityReason = "covered", SensorId = sensorId, HostId = 20,
            SourceGeneration = source, ResourceGeneration = generation, IdentityEpoch = 1,
            ValidFrom = asOf.AddDays(-2), LastAttemptAt = asOf.AddMinutes(1),
            Coverage = [new(sensorId, asOf.AddDays(-2), asOf.AddMinutes(1), source, generation)],
            States = statusEnteredAt.HasValue
                ? [new(sensorId, statusEnteredAt.Value, status ?? "Unknown", source, generation)]
                : []
        };
        var identity = new PrtgResourceIdentity
        {
            SensorId = sensorId, Epoch = 1, Generation = generation, SourceGeneration = source,
            DeviceId = deviceId, HostId = 20, Active = true, PendingReconciliation = false
        };
        return new(new(sensorId, deviceId, status, "test sensor", category), status, asOf,
            string.Equals(category, PrtgSensorCategories.Availability, StringComparison.OrdinalIgnoreCase)
                ? null : statusEnteredAt,
            paused, timeline, identity);
    }

    [Fact]
    public void CompleteUnknownWithCurrentAvailabilityUp_ProducesOneMonitoringSilenceFinding()
    {
        var finding = Assert.Single(Evaluate(ValidDevice()));
        Assert.Equal(10, finding.DeviceObjid);
        Assert.Null(finding.SensorObjid);
        Assert.Equal(PrtgRuleEvaluator.RuleSilent, finding.RuleCode);
        Assert.Equal(1, finding.Magnitude);
        Assert.Equal(AsOf.AddHours(-3), finding.IncidentStartedAt);
        Assert.Equal(0, finding.ThresholdMagnitude);
        Assert.Contains("不代表主機故障", finding.Detail);
    }

    [Fact]
    public void UnknownPeriodCrossingMidnight_UsesLastSensorToBecomeUnknown()
    {
        var firstUnknown = DayStart.AddMinutes(-30);
        var lastUnknown = DayStart.AddMinutes(10);
        var device = ValidDevice(DayStart.AddMinutes(40), firstUnknown,
            (102, PrtgSensorCategories.Memory, "Unknown"));
        var sensors = device.Sensors.ToArray();
        sensors[2] = Sensor(102, 10, PrtgSensorCategories.Memory, "Unknown",
            DayStart.AddMinutes(40), lastUnknown);
        device = device with { Sensors = sensors };

        var finding = Assert.Single(Evaluate(device));
        Assert.Equal(lastUnknown, finding.IncidentStartedAt);
        Assert.Contains("30 分鐘", finding.Detail);
    }

    [Fact]
    public void AllNonAvailabilitySensorsMustBeUnknownAndOneCurrentAvailabilitySensorIsEnough()
    {
        var device = ValidDevice(otherSensors:
        [
            (102, PrtgSensorCategories.Memory, "Unknown"),
            (103, PrtgSensorCategories.Hardware, "Unknown")
        ]);
        var finding = Assert.Single(Evaluate(device));
        Assert.Equal(3, finding.Magnitude);
    }

    [Fact]
    public void PausedSensorsAreExcludedOnlyWhenExplicitlyOutsideExpectedUnpausedScope()
    {
        var device = ValidDevice();
        var sensors = device.Sensors.Append(new PrtgSilentSensorEvidence(
            new(999, device.DeviceObjid, "Paused", "Paused", "hardware"),
            "Paused", null, null, true, null, null)).ToArray();
        var finding = Assert.Single(Evaluate(device with { Sensors = sensors }));
        Assert.Equal(1, finding.Magnitude);
    }

    [Fact]
    public void ReplayingSameQualifiedEvidence_IsDeterministicAndDoesNotExtendDuration()
    {
        var evidence = ValidDevice();
        var first = Assert.Single(Evaluate(evidence));
        var replay = Assert.Single(Evaluate(evidence));
        Assert.Equal(first.IncidentStartedAt, replay.IncidentStartedAt);
        Assert.Equal(first.Detail, replay.Detail);
        Assert.Equal(first.ResourceGeneration, replay.ResourceGeneration);
    }

    [Fact]
    public void CoveredRuleEvaluator_InvokesSilentConsumerWhenTypedEvidenceIsProvided()
    {
        var result = PrtgCoveredRuleEvaluator.Evaluate(Day, Array.Empty<PrtgSensorStatusInput>(),
            new Dictionary<long, PrtgSensorTimelineEvidence>(), [SilentRule], [ValidDevice()]);
        Assert.Equal(PrtgRuleEvaluator.RuleSilent, Assert.Single(result).RuleCode);
    }

    public static IEnumerable<object[]> InvalidCases => new[]
    {
        new object[] { "device-paused" },
        new object[] { "partial-read" },
        new object[] { "stale-read" },
        new object[] { "failed-read" },
        new object[] { "unknown-read" },
        new object[] { "incomplete-scope" },
        new object[] { "unfiltered-rows" },
        new object[] { "scope-count-mismatch" },
        new object[] { "duplicate-sensor" },
        new object[] { "missing-availability" },
        new object[] { "availability-down" },
        new object[] { "availability-unknown" },
        new object[] { "no-monitor-sensors" },
        new object[] { "monitor-up" },
        new object[] { "monitor-warning" },
        new object[] { "monitor-down" },
        new object[] { "duration-zero" },
        new object[] { "unknown-start-after-asof" },
        new object[] { "stale-asof-prior-day" },
        new object[] { "future-asof-next-day" },
        new object[] { "asof-differs-between-sensors" },
        new object[] { "wrong-device" },
        new object[] { "wrong-host" },
        new object[] { "wrong-source-generation" },
        new object[] { "wrong-resource-generation" },
        new object[] { "wrong-identity-epoch" },
        new object[] { "inactive-identity" },
        new object[] { "pending-reconciliation" },
        new object[] { "partial-timeline" },
        new object[] { "coverage-gap" },
        new object[] { "timeline-state-not-unknown" },
        new object[] { "missing-asof" },
        new object[] { "only-disabled-rule" },
        new object[] { "no-rule" },
        new object[] { "duplicate-device-evidence" }
    };

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void UnqualifiedOrNonSilentEvidence_NeverProducesFinding(string caseName)
    {
        var device = ValidDevice();
        var sensors = device.Sensors.ToArray();
        switch (caseName)
        {
            case "device-paused": device = device with { DevicePaused = true }; break;
            case "partial-read": device = device with { ReadQuality = PrtgPresenceReadQuality.Partial }; break;
            case "stale-read": device = device with { ReadQuality = PrtgPresenceReadQuality.Stale }; break;
            case "failed-read": device = device with { ReadQuality = PrtgPresenceReadQuality.Failed }; break;
            case "unknown-read": device = device with { ReadQuality = PrtgPresenceReadQuality.Unknown }; break;
            case "incomplete-scope": device = device with { ScopeComplete = false }; break;
            case "unfiltered-rows": device = device with { HasUnfilteredObjects = true }; break;
            case "scope-count-mismatch": device = device with { ExpectedUnpausedSensorCount = 3 }; break;
            case "duplicate-sensor": device = device with { Sensors = sensors.Append(sensors[1]).ToArray() }; break;
            case "missing-availability": device = device with { Sensors = sensors.Skip(1).ToArray(), ExpectedUnpausedSensorCount = 1 }; break;
            case "availability-down": sensors[0] = Sensor(100, 10, PrtgSensorCategories.Availability, "Down", AsOf, AsOf.AddHours(-1)); device = device with { Sensors = sensors }; break;
            case "availability-unknown": sensors[0] = Sensor(100, 10, PrtgSensorCategories.Availability, "Unknown", AsOf, AsOf.AddHours(-1)); device = device with { Sensors = sensors }; break;
            case "no-monitor-sensors": device = device with { Sensors = sensors.Take(1).ToArray(), ExpectedUnpausedSensorCount = 1 }; break;
            case "monitor-up": sensors[1] = Sensor(101, 10, PrtgSensorCategories.Cpu, "Up", AsOf, AsOf.AddHours(-1)); device = device with { Sensors = sensors }; break;
            case "monitor-warning": sensors[1] = Sensor(101, 10, PrtgSensorCategories.Cpu, "Warning", AsOf, AsOf.AddHours(-1)); device = device with { Sensors = sensors }; break;
            case "monitor-down": sensors[1] = Sensor(101, 10, PrtgSensorCategories.Cpu, "Down", AsOf, AsOf.AddHours(-1)); device = device with { Sensors = sensors }; break;
            case "duration-zero": sensors[1] = Sensor(101, 10, PrtgSensorCategories.Cpu, "Unknown", AsOf, AsOf); device = device with { Sensors = sensors }; break;
            case "unknown-start-after-asof": sensors[1] = Sensor(101, 10, PrtgSensorCategories.Cpu, "Unknown", AsOf, AsOf.AddMinutes(1)); device = device with { Sensors = sensors }; break;
            case "stale-asof-prior-day": device = ValidDevice(DayStart.AddDays(-1).AddHours(23), DayStart.AddDays(-1).AddHours(20)); break;
            case "future-asof-next-day": device = ValidDevice(DayStart.AddDays(1), DayStart.AddHours(18)); break;
            case "asof-differs-between-sensors": sensors[0] = sensors[0] with { SourceAsOf = AsOf.AddSeconds(-1) }; device = device with { Sensors = sensors }; break;
            case "wrong-device": sensors[1] = Sensor(101, 11, PrtgSensorCategories.Cpu, "Unknown", AsOf, AsOf.AddHours(-3)); device = device with { Sensors = sensors }; break;
            case "wrong-host": sensors[1] = WithIdentity(sensors[1], identity => identity.HostId = 21); device = device with { Sensors = sensors }; break;
            case "wrong-source-generation": sensors[1] = WithTimeline(sensors[1], proof => proof.SourceGeneration = "other"); device = device with { Sensors = sensors }; break;
            case "wrong-resource-generation": sensors[1] = WithIdentity(sensors[1], identity => identity.Generation = "other"); device = device with { Sensors = sensors }; break;
            case "wrong-identity-epoch": sensors[1] = WithIdentity(sensors[1], identity => identity.Epoch = 2); device = device with { Sensors = sensors }; break;
            case "inactive-identity": sensors[1] = WithIdentity(sensors[1], identity => identity.Active = false); device = device with { Sensors = sensors }; break;
            case "pending-reconciliation": sensors[1] = WithIdentity(sensors[1], identity => identity.PendingReconciliation = true); device = device with { Sensors = sensors }; break;
            case "partial-timeline": sensors[1] = WithTimeline(sensors[1], proof => proof.QualityReason = "identity-warmup"); device = device with { Sensors = sensors }; break;
            case "coverage-gap": sensors[1] = WithTimeline(sensors[1], proof => proof.Coverage = []); device = device with { Sensors = sensors }; break;
            case "timeline-state-not-unknown": sensors[1] = WithTimeline(sensors[1], proof => proof.States =
                [new(101, AsOf.AddHours(-1), "Up", proof.SourceGeneration, proof.ResourceGeneration)]); device = device with { Sensors = sensors }; break;
            case "missing-asof": sensors[1] = sensors[1] with { SourceAsOf = null }; device = device with { Sensors = sensors }; break;
        }

        IReadOnlyList<KnownIssueRule> rules = caseName switch
        {
            "no-rule" => Array.Empty<KnownIssueRule>(),
            "only-disabled-rule" => [new KnownIssueRule
            {
                Id = "disabled-silent", Platform = "prtg", Enabled = false,
                PrtgRuleCode = PrtgRuleEvaluator.RuleSilent,
                PrtgThreshold = PrtgRuleCatalog.DefaultSilentThreshold
            }],
            _ => [SilentRule]
        };
        var devices = caseName == "duplicate-device-evidence" ? new[] { device, device } : new[] { device };
        Assert.Empty(PrtgSilentAbsenceEvaluator.Evaluate(Day, devices, rules));
    }

    private static PrtgSilentSensorEvidence WithIdentity(PrtgSilentSensorEvidence sensor, Action<PrtgResourceIdentity> mutate)
    {
        var identity = CloneIdentity(sensor.Identity!);
        mutate(identity);
        return sensor with { Identity = identity };
    }

    private static PrtgSilentSensorEvidence WithTimeline(PrtgSilentSensorEvidence sensor, Action<PrtgSensorTimelineEvidence> mutate)
    {
        var timeline = sensor.Timeline!;
        mutate(timeline);
        return sensor with { Timeline = timeline };
    }

    private static PrtgResourceIdentity CloneIdentity(PrtgResourceIdentity source) => new()
    {
        SensorId = source.SensorId, Epoch = source.Epoch, Generation = source.Generation,
        SourceGeneration = source.SourceGeneration, DeviceId = source.DeviceId, HostId = source.HostId,
        ResourceFingerprint = source.ResourceFingerprint, InventoryFingerprint = source.InventoryFingerprint,
        ChannelFingerprint = source.ChannelFingerprint, ChannelGeneration = source.ChannelGeneration,
        Active = source.Active, PendingReconciliation = source.PendingReconciliation,
        ChangedAtUtc = source.ChangedAtUtc
    };

    private static IReadOnlyList<PrtgFinding> Evaluate(PrtgSilentDeviceEvidence evidence) =>
        PrtgSilentAbsenceEvaluator.Evaluate(Day, [evidence], [SilentRule]);
}
