using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>Formal consumer for the persisted device-level PRTG silent rule.</summary>
public static class PrtgSilentAbsenceEvaluator
{
    public const int MaxSensorsPerDevice = 512;

    /// <summary>
    /// Emits one monitoring-silence finding only when every unpaused sensor is source-current,
    /// the complete inventory contains a current Up availability sensor, and all remaining
    /// sensors have continuously covered Unknown states for a positive source-reported duration.
    /// It never uses application/host current time to extend presence or silence.
    /// </summary>
    public static IReadOnlyList<PrtgFinding> Evaluate(DateTime day,
        IReadOnlyList<PrtgSilentDeviceEvidence> devices, IReadOnlyList<KnownIssueRule> rules)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(rules);
        if (day != day.Date) throw new ArgumentException("判定日不可包含時間。", nameof(day));

        var rule = rules.Where(r => r.Enabled && string.Equals(r.Platform, "prtg", StringComparison.OrdinalIgnoreCase) &&
                r.PrtgRuleCode == PrtgRuleEvaluator.RuleSilent && r.PrtgThreshold == PrtgRuleCatalog.DefaultSilentThreshold &&
                r.PrtgSensorCategory is null)
            .OrderBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
        if (rule is null) return [];

        var findings = new List<PrtgFinding>();
        var duplicateDeviceIds = devices.GroupBy(d => d.DeviceObjid).Where(g => g.Count() != 1)
            .Select(g => g.Key).ToHashSet();
        foreach (var device in devices)
        {
            if (duplicateDeviceIds.Contains(device.DeviceObjid) || !TryEvaluateDevice(day, device, out var result))
                continue;

            var unknownSensorCount = result.UnknownSensors.Length;
            var durationMinutes = (int)Math.Floor((result.AsOf - result.UnknownSince).TotalMinutes);
            var detail = $"PRTG 監測靜默：來源在 {result.AsOf:yyyy-MM-dd HH:mm:ss zzz} 確認 availability Up；" +
                $"{unknownSensorCount} 個非 availability sensor 在完整涵蓋下持續 Unknown {durationMinutes} 分鐘，" +
                $"自 {result.UnknownSince:yyyy-MM-dd HH:mm:ss zzz} 起。此訊號表示監測缺口，不代表主機故障。";
            findings.Add(new PrtgFinding(device.DeviceObjid, null, PrtgRuleEvaluator.RuleSilent, detail,
                unknownSensorCount, rule, false)
            {
                SourceGeneration = device.SourceGeneration,
                // Device-level findings span several sensor epochs; this stable aggregate digest
                // binds the event to the exact qualified resource set without exposing sensor IDs.
                ResourceGeneration = AggregateResourceGeneration(result.UnknownSensors.Concat(result.AvailabilitySensors)),
                IncidentStartedAt = result.UnknownSince,
                EvidenceWindows = [new(result.UnknownSince.ToUniversalTime(), result.AsOf.ToUniversalTime())],
                ThresholdMagnitude = PrtgRuleCatalog.DefaultSilentThreshold,
                PresenceSourceDay = device.SourceDay,
                PresenceSourceAsOf = result.AsOf,
                PresenceDeviceStatusAsOf = device.DeviceStatusAsOf,
                PresenceSourceAuthorityFingerprint = device.SourceAuthorityFingerprint,
                PresenceMappingFingerprint = device.MappingFingerprint,
                PresenceInventoryFingerprint = device.InventoryFingerprint
            });
        }
        return findings;
    }

    private static bool TryEvaluateDevice(DateTime day, PrtgSilentDeviceEvidence device, out QualifiedResult result)
    {
        result = default!;
        if (device.DeviceObjid <= 0 || device.HostId <= 0 || string.IsNullOrWhiteSpace(device.SourceGeneration) ||
            device.ReadQuality != PrtgPresenceReadQuality.Complete || !device.ScopeComplete || device.HasUnfilteredObjects ||
            device.DevicePaused || device.ExpectedUnpausedSensorCount is < 1 or > MaxSensorsPerDevice ||
            device.Sensors is null || device.Sensors.Count > MaxSensorsPerDevice ||
            device.Sensors.GroupBy(s => s.Sensor.Objid).Any(g => g.Count() != 1)) return false;

        var unpaused = device.Sensors.Where(s => !s.Paused).ToArray();
        if (device.RequestedAnalysisDay != day.Date || unpaused.Length != device.ExpectedUnpausedSensorCount || unpaused.Length == 0 ||
            unpaused.Any(s => s.Sensor.DeviceObjid != device.DeviceObjid || s.Sensor.Objid <= 0 ||
                s.SourceAsOf is null || device.RequestedWindowStartUtc is null || device.RequestedWindowEndUtc is null ||
                device.RequestedWindowEndUtc.Value <= device.RequestedWindowStartUtc.Value ||
                s.SourceAsOf.Value < device.RequestedWindowStartUtc.Value || s.SourceAsOf.Value >= device.RequestedWindowEndUtc.Value ||
                !IsCurrentResource(s, device))) return false;

        var availability = unpaused.Where(s => string.Equals(s.Sensor.Category,
            PrtgSensorCategories.Availability, StringComparison.OrdinalIgnoreCase)).ToArray();
        var unknownSensors = unpaused.Where(s => !string.Equals(s.Sensor.Category,
            PrtgSensorCategories.Availability, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (availability.Length == 0 || unknownSensors.Length == 0 ||
            availability.Any(s => !PrtgSensorStatuses.IsUp(s.SourceStatus) || !HasCoveredCurrentState(s, PrtgSensorStatuses.Up)) ||
            unknownSensors.Any(s => !PrtgSensorStatuses.IsUnknownOrEmpty(s.SourceStatus) || s.UnknownSince is null ||
                s.SourceAsOf!.Value <= s.UnknownSince.Value || !HasContinuousUnknown(s, s.UnknownSince.Value, s.SourceAsOf.Value)))
            return false;

        // A single explicit source as-of binds the presence proof and all sensor statuses.
        // Differently timed or replayed snapshots are insufficient to assert a device-wide state.
        var asOf = unpaused[0].SourceAsOf!.Value;
        if (unpaused.Any(s => s.SourceAsOf!.Value != asOf) ||
            availability.Any(s => s.SourceAsOf!.Value < asOf)) return false;

        var unknownSince = unknownSensors.Max(s => s.UnknownSince!.Value);
        if (unknownSince >= asOf) return false;
        result = new QualifiedResult(asOf, unknownSince, unknownSensors, availability);
        return true;
    }

    private static bool IsCurrentResource(PrtgSilentSensorEvidence sensor, PrtgSilentDeviceEvidence device) =>
        sensor.Timeline is { QualityReason: "covered" } && sensor.Identity is not null &&
        PrtgResourceQualification.IsCurrent(sensor.Timeline, sensor.Identity, device.SourceGeneration,
            sensor.Sensor.Objid, device.DeviceObjid, device.HostId);

    private static bool HasCoveredCurrentState(PrtgSilentSensorEvidence sensor, string expectedStatus)
    {
        var proof = sensor.Timeline!;
        var asOf = sensor.SourceAsOf!.Value;
        var current = proof.States.Where(s => s.At <= asOf &&
                s.SourceGeneration == proof.SourceGeneration && s.ResourceGeneration == proof.ResourceGeneration)
            .OrderBy(s => s.At).LastOrDefault();
        return current is not null && string.Equals(current.Status, expectedStatus, StringComparison.OrdinalIgnoreCase) &&
            proof.Coverage.Any(c => c.SensorObjid == proof.SensorId && c.SourceGeneration == proof.SourceGeneration &&
                c.ResourceGeneration == proof.ResourceGeneration && c.From <= asOf && c.Through > asOf);
    }

    private static bool HasContinuousUnknown(PrtgSilentSensorEvidence sensor, DateTimeOffset unknownSince,
        DateTimeOffset asOf)
    {
        var proof = sensor.Timeline!;
        var stateAtAsOf = proof.States.Where(s => s.At <= asOf &&
                s.SourceGeneration == proof.SourceGeneration && s.ResourceGeneration == proof.ResourceGeneration)
            .OrderBy(s => s.At).LastOrDefault();
        if (stateAtAsOf is null || !PrtgSensorStatuses.IsUnknownOrEmpty(stateAtAsOf.Status)) return false;

        var periods = proof.Periods(unknownSince, asOf).OrderBy(p => p.From).ToArray();
        if (periods.Length == 0 || periods[0].EnteredAt != unknownSince) return false;
        var cursor = unknownSince;
        foreach (var period in periods)
        {
            if (!PrtgSensorStatuses.IsUnknownOrEmpty(period.Status) || period.From > cursor) return false;
            if (period.Through > cursor) cursor = period.Through;
        }
        return cursor >= asOf && asOf > unknownSince;
    }

    private static string AggregateResourceGeneration(IEnumerable<PrtgSilentSensorEvidence> sensors)
    {
        var input = string.Join("\n", sensors.OrderBy(s => s.Sensor.Objid)
            .Select(s => $"{s.Sensor.Objid}:{s.Identity!.Epoch}:{s.Identity.Generation}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private sealed record QualifiedResult(DateTimeOffset AsOf, DateTimeOffset UnknownSince,
        PrtgSilentSensorEvidence[] UnknownSensors, PrtgSilentSensorEvidence[] AvailabilitySensors);
}
