namespace LogForesight.Core.Service;

public sealed record PrtgProbeBuildFacts(string Status, string Reason, string SourceState,
    string SourceCommit, string SourceFingerprint, string Token, string WebVersion, string CoreVersion,
    string WebModuleId, string CoreModuleId, string WebFileSha256, string CoreFileSha256)
{
    public static PrtgProbeBuildFacts From(LogForesightBuildIdentity identity) => new(identity.Status,
        identity.Reason, identity.SourceState, identity.SourceCommit, identity.SourceFingerprint, identity.Token,
        identity.Web.InformationalVersion, identity.Core.InformationalVersion,
        identity.Web.ModuleVersionId, identity.Core.ModuleVersionId, identity.Web.FileSha256, identity.Core.FileSha256);
}

/// <summary>Bounded local observations, never source qualification or an acceptance verdict.</summary>
public sealed record PrtgProbeLocalFacts
{
    public const string Schema = "local-probe-facts-v1";
    public string SchemaVersion { get; init; } = Schema;
    public string Status { get; init; } = "unknown";
    public string Reason { get; init; } = "local-facts-not-collected";
    public bool AuthorizesFormalProfile { get; init; }
    public string AuthorityKind { get; init; } = "unknown";
    public string SourceTimeZoneId { get; init; } = "unknown";
    public string RawTimestampTimeZoneId { get; init; } = "unknown";
    public string AnalysisTimeZoneId { get; init; } = "unknown";
    public string SourceCultureName { get; init; } = "unknown";
    public string AuthorityFingerprint { get; init; } = "unknown";
    public int PolicyHosts { get; init; }
    public int PolicySensors { get; init; }
    public int MirrorDevices { get; init; }
    public int MirrorSensors { get; init; }
    public int MappedDevices { get; init; }
    public int ConflictingDevices { get; init; }
    public int GuardOverrideSensors { get; init; }
    public int GuardSensorsInMirror { get; init; }
    public int ReadinessSampledSensors { get; init; }
    public int SavedBindings { get; init; }
    public int QualifiedProfiles { get; init; }
    public string ReadinessReason { get; init; } = "policy-not-ready";
    public string NetiqDay { get; init; } = "unknown";
    public int NetiqReceipts { get; init; }
    public int NetiqSuccess { get; init; }
    public int NetiqWaiting { get; init; }
    public int NetiqParentRows { get; init; }
    public int NetiqEligibleParents { get; init; }
    public string NetiqReason { get; init; } = "netiq-summary-not-collected";
    public string AiProbeStatus { get; init; } = "not-probed";
    public double? AiProbeElapsedMilliseconds { get; init; }
    public int? AiProbeTotalTokens { get; init; }
    public string AiProbeEvidenceOrigin { get; init; } = "not-observed";
    public string AiCostReason { get; init; } = "token-and-price-cost-not-observed";
}
