using LogForesight.Core.Models;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>
/// Opaque, request-scoped assessment state. Its constructor and captured candidate/metadata snapshots
/// are private to Core; Web receives only the candidate count and can ask Core to assess pages.
/// It is intentionally not serializable or cacheable across HTTP requests.
/// </summary>
public sealed class PrtgDiskAssessmentOperation
{
    private int _completed;

    internal PrtgDiskAssessmentOperation(object owner, DateOnly completedDay, DateTime candidateMappingThrough,
        KnownIssueRule? rule, PrtgDiskDecisionMode mode, long[]? selectedHostIds, long[]? selectedSensorObjids,
        PrtgHostSnapshot hostSnapshot, long[] activeHostIds, long hostVersion, string settingsRevision,
        string settingsFingerprint, string[] capturedWhitelist, string ruleFingerprint,
        PrtgDiskCandidateSnapshot candidateSnapshot, PrtgDiskMetadataSnapshot metadataSnapshot)
    {
        Owner = owner;
        CompletedDay = completedDay;
        CandidateMappingThrough = candidateMappingThrough;
        var ruleJson = rule is null ? null : System.Text.Json.JsonSerializer.Serialize(rule);
        Rule = ruleJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<KnownIssueRule>(ruleJson);
        Mode = mode;
        SelectedHostIds = selectedHostIds?.ToArray();
        SelectedSensorObjids = selectedSensorObjids?.ToArray();
        HostSnapshot = hostSnapshot;
        ActiveHostIds = activeHostIds.ToArray();
        HostVersion = hostVersion;
        SettingsRevision = settingsRevision;
        SettingsFingerprint = settingsFingerprint;
        CapturedWhitelist = capturedWhitelist.ToArray();
        SensorTypeWhitelist = Array.AsReadOnly(CapturedWhitelist);
        RuleFingerprint = ruleFingerprint;
        CandidateSnapshot = candidateSnapshot;
        MetadataSnapshot = metadataSnapshot;
    }

    public int CandidateCount => CandidateSnapshot.Total;
    public IReadOnlyList<string> SensorTypeWhitelist { get; }

    internal object Owner { get; }
    internal DateOnly CompletedDay { get; }
    internal DateTime CandidateMappingThrough { get; }
    internal KnownIssueRule? Rule { get; }
    internal PrtgDiskDecisionMode Mode { get; }
    internal long[]? SelectedHostIds { get; }
    internal long[]? SelectedSensorObjids { get; }
    internal PrtgHostSnapshot HostSnapshot { get; }
    internal long[] ActiveHostIds { get; }
    internal long HostVersion { get; }
    internal string SettingsRevision { get; }
    internal string SettingsFingerprint { get; }
    internal string[] CapturedWhitelist { get; }
    internal string RuleFingerprint { get; }
    internal PrtgDiskCandidateSnapshot CandidateSnapshot { get; }
    internal PrtgDiskMetadataSnapshot MetadataSnapshot { get; }
    internal bool IsCompleted => Volatile.Read(ref _completed) != 0;

    internal void MarkCompleted()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            throw new InvalidOperationException("PRTG 評估作業已完成。");
    }

    /// <summary>Returns only a non-sensitive lifecycle explanation from this operation's private evidence snapshot.</summary>
    public string? GetEvidenceIdentityMismatchReason(long sensorObjid)
    {
        var candidate = CandidateSnapshot.FindCandidate(sensorObjid);
        if (candidate is null) return null;
        var evidence = MetadataSnapshot.GetEvidence(sensorObjid);
        if (evidence is null) return null;
        if (evidence.DeviceObjid != candidate.Value.DeviceObjid || evidence.HostId != candidate.Value.HostId)
            return "主機或裝置對應已變更。";
        if (!string.Equals(evidence.SensorType?.Trim(), candidate.Value.SensorType?.Trim(), StringComparison.OrdinalIgnoreCase))
            return "sensor 類型已變更。";
        return null;
    }
}
