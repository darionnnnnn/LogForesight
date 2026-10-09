using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;
using System.Security.Cryptography;
using System.Text;

namespace LogForesight.Core.Service;

/// <summary>Explicit completeness of the source read used for device-level silence evaluation.</summary>
public enum PrtgPresenceReadQuality
{
    Unknown,
    Complete,
    Partial,
    Stale,
    Failed
}

/// <summary>Whether a mapped device's silent-presence evidence can support a formal manifest.</summary>
public enum PrtgSilentPresenceReadinessState
{
    QualifiedHit,
    QualifiedNoHit,
    ExplicitlyExcluded,
    Waiting
}

/// <summary>Compact per-device proof outcome. The fingerprint binds source, inventory, map, and as-of.</summary>
public sealed record PrtgSilentPresenceDeviceReadiness(
    long DeviceObjid,
    long HostId,
    PrtgSilentPresenceReadinessState State,
    string Reason,
    string EvidenceFingerprint,
    IReadOnlyList<PrtgFinding> Findings);

/// <summary>Bounded formal result; waiting reasons remain distinct from qualified zero-finding outcomes.</summary>
public sealed record PrtgSilentPresenceEvaluation(
    IReadOnlyList<PrtgFinding> Findings,
    IReadOnlyDictionary<long, PrtgSilentPresenceDeviceReadiness> Devices)
{
    public bool IsReadyForHost(long hostId) => Devices.Values.Where(device => device.HostId == hostId)
        .All(device => device.State is PrtgSilentPresenceReadinessState.QualifiedHit or
            PrtgSilentPresenceReadinessState.QualifiedNoHit or PrtgSilentPresenceReadinessState.ExplicitlyExcluded);

    public IReadOnlyList<string> WaitingReasonsForHost(long hostId) => Devices.Values
        .Where(device => device.HostId == hostId && device.State == PrtgSilentPresenceReadinessState.Waiting)
        .Select(device => device.Reason).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

/// <summary>
/// A source-reported sensor status with its own source as-of time and the matching persisted
/// timeline/resource identity. The as-of must come from the source, never from application time.
/// </summary>
public sealed record PrtgSilentSensorEvidence(
    PrtgSensorStatusInput Sensor,
    string? SourceStatus,
    DateTimeOffset? SourceAsOf,
    DateTimeOffset? UnknownSince,
    bool Paused,
    PrtgSensorTimelineEvidence? Timeline,
    PrtgResourceIdentity? Identity);

/// <summary>Minimal status row captured from one complete, unfiltered native sensors response.</summary>
public sealed record PrtgSilentSensorSnapshot(
    long SensorObjid,
    long DeviceObjid,
    string SensorType,
    string? Category,
    string? SourceStatus,
    bool Paused);

/// <summary>
/// Durable source-response proof. It deliberately stores only the bounded current inventory/status
/// snapshot; timeline coverage and identities are reloaded and fenced when the formal consumer runs.
/// </summary>
public sealed record PrtgSilentDeviceSnapshot(
    long DeviceObjid,
    long HostId,
    string SourceGeneration,
    string PolicyRevision,
    long ScopeRevision,
    string MappingFingerprint,
    DateTime? SourceDay,
    DateTimeOffset? SourceAsOf,
    DateTimeOffset? DeviceStatusAsOf,
    DateTimeOffset? CapturedAtUtc,
    bool DevicePaused,
    int ReportedSensorCount,
    string InventoryFingerprint,
    IReadOnlyList<PrtgSilentSensorSnapshot> Sensors,
    string ReadReason = "complete")
{
    public PrtgPresenceReadQuality ReadQuality { get; init; } = PrtgPresenceReadQuality.Complete;
    public string SourceAuthorityFingerprint { get; init; } = string.Empty;
}

/// <summary>Stable device mapping authority plus a separate whole-work-set digest for producer scheduling.</summary>
public static class PrtgSilentPresenceMappingFingerprint
{
    /// <summary>Persistent proof identity for one device; independent of other mapped hosts.</summary>
    public static string Compute(long deviceObjid, long hostId)
    {
        if (deviceObjid <= 0 || hostId <= 0) return string.Empty;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"{deviceObjid}:{hostId}"))));
    }

    /// <summary>Full inventory digest used only to detect producer work-set changes.</summary>
    public static string Compute(IEnumerable<KeyValuePair<long, long>> deviceToHost)
    {
        var canonical = string.Join("\n", deviceToHost.Where(pair => pair.Key > 0 && pair.Value > 0)
            .Distinct().OrderBy(pair => pair.Key).ThenBy(pair => pair.Value)
            .Select(pair => FormattableString.Invariant($"{pair.Key}:{pair.Value}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>
/// Complete, unfiltered inventory and current presence proof for one PRTG device on one source day.
/// Scope completeness is an explicit producer assertion and must be backed by the source response.
/// </summary>
public sealed record PrtgSilentDeviceEvidence(
    long DeviceObjid,
    long HostId,
    string SourceGeneration,
    PrtgPresenceReadQuality ReadQuality,
    bool ScopeComplete,
    bool HasUnfilteredObjects,
    bool DevicePaused,
    int ExpectedUnpausedSensorCount,
    IReadOnlyList<PrtgSilentSensorEvidence> Sensors)
{
    public string PolicyRevision { get; init; } = string.Empty;
    public long ScopeRevision { get; init; }
    public string InventoryFingerprint { get; init; } = string.Empty;
    public string MappingFingerprint { get; init; } = string.Empty;
    public string ReadReason { get; init; } = string.Empty;
    public DateTime? RequestedAnalysisDay { get; init; }
    public DateTimeOffset? RequestedWindowStartUtc { get; init; }
    public DateTimeOffset? RequestedWindowEndUtc { get; init; }
    public DateTime? SourceDay { get; init; }
    public DateTimeOffset? DeviceStatusAsOf { get; init; }
    public DateTimeOffset? SourceAsOf { get; init; }
    public string SourceAuthorityFingerprint { get; init; } = string.Empty;
}
