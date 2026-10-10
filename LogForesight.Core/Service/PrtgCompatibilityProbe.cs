using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 環境相容性證據探測上下文 DTO（由呼叫端或 Web service 提供）。
/// </summary>
public sealed class PrtgProbeEvidenceContext
{
    public string SchemaVersion { get; init; } = "1.0.0";
    public string? BuildVersion { get; init; }
    public string? SourcePrtgVersion { get; init; }
    public string? PrtgVersion { get => SourcePrtgVersion; init => SourcePrtgVersion = value; }
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? HostUtcOffset { get; init; }
    public string? SourceFingerprint { get; init; }
    public string? PrtgBaseUrlFingerprint { get => SourceFingerprint; init => SourceFingerprint = value; }
    public string? SettingsRevision { get; set; }
    public string SourceTimezone { get; init; } = "unknown";
    public string SourceLocale { get; init; } = "unknown";
    public string StorageProvider { get; init; } = "unknown";
    public string? StorageEngine { get => StorageProvider; init => StorageProvider = value ?? "unknown"; }
    public string? EfCoreProvider { get; init; }
    public int? RetentionDays { get; init; }
    public int? DataRetentionDays { get => RetentionDays; init => RetentionDays = value; }
    public PrtgStorageEnvironmentFacts? StorageEnvironment { get; init; }
    public string? ScopeSummary { get; init; }
    public string? ReadinessSummary { get; init; }
}

public sealed class PrtgCompatibilityProbeEvidence
{
    [JsonPropertyName("sensor_batch_identity")]
    public PrtgSensorBatchCapability? SensorBatchIdentity { get; set; }

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0.0";

    [JsonPropertyName("generated_at_utc")]
    public string GeneratedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("host_utc_offset")]
    public string HostUtcOffset { get; set; } = string.Empty;

    [JsonPropertyName("build_version")]
    public string BuildVersion { get; set; } = "unknown";

    [JsonPropertyName("source_prtg_version")]
    public string SourcePrtgVersion { get; set; } = "unknown";

    [JsonPropertyName("deployment_resources")]
    public PrtgDeploymentResources DeploymentResources { get; set; } = new();

    [JsonPropertyName("storage_environment")]
    public PrtgStorageEnvironmentFacts StorageEnvironment { get; set; } = new();

    [JsonPropertyName("settings_revision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SettingsRevision { get; set; }

    [JsonPropertyName("source_fingerprint")]
    public string SourceFingerprint { get; set; } = "unknown";

    [JsonPropertyName("source_timezone")]
    public string SourceTimezone { get; set; } = "unknown";

    [JsonPropertyName("source_locale")]
    public string SourceLocale { get; set; } = "unknown";

    [JsonPropertyName("storage_provider")]
    public string StorageProvider { get; set; } = "unknown";

    [JsonPropertyName("ef_core_provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EfCoreProvider { get; set; }

    [JsonPropertyName("retention_days")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetentionDays { get; set; }

    [JsonPropertyName("scope_summary")]
    public string ScopeSummary { get; set; } = "unknown";

    [JsonPropertyName("readiness_summary")]
    public string ReadinessSummary { get; set; } = "unknown";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("evidence_ready")]
    public bool EvidenceReady { get; set; } = false;

    [JsonPropertyName("historical_date_boundary")]
    public PrtgDateBoundary? HistoricalDateBoundary { get; set; }

    [JsonPropertyName("summary")]
    public PrtgProbeEvidenceSummary Summary { get; set; } = new();

    [JsonPropertyName("targets")]
    public List<PrtgProbeTargetEvidence> Targets { get; set; } = new();
}

public sealed class PrtgDeploymentResources
{
    [JsonPropertyName("processor_count")]
    public int ProcessorCount { get; set; } = Environment.ProcessorCount;

    [JsonPropertyName("process_architecture")]
    public string ProcessArchitecture { get; set; } = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();

    [JsonPropertyName("os_architecture")]
    public string OsArchitecture { get; set; } = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

    [JsonPropertyName("dotnet_framework_description")]
    public string DotnetFrameworkDescription { get; set; } = GetDotnetVersion();

    [JsonPropertyName("total_available_memory_bytes")]
    public long TotalAvailableMemoryBytes { get; set; } = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    [JsonPropertyName("working_set_bytes")]
    public long WorkingSetBytes { get; set; } = Environment.WorkingSet;

    [JsonPropertyName("sql_host_resources")]
    public string SqlHostResources { get; set; } = "unknown (remote database host resources not observable from app process)";

    private static string GetDotnetVersion()
    {
        var match = Regex.Match(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, @"^\.NET(?: Framework)? \d+(?:\.\d+){1,3}$", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : "unknown";
    }
}

public sealed class PrtgDateBoundary
{
    [JsonPropertyName("start")]
    public string Start { get; set; } = string.Empty;

    [JsonPropertyName("end")]
    public string End { get; set; } = string.Empty;

    [JsonPropertyName("limitations")]
    public string Limitations { get; set; } = "來源時區未確認";
}

public sealed class PrtgProbeEvidenceSummary
{
    [JsonPropertyName("target_categories_count")]
    public int TargetCategoriesCount { get; set; } = 3;

    [JsonPropertyName("targeted_sensors_count")]
    public int TargetedSensorsCount { get; set; }

    [JsonPropertyName("targets_ok")]
    public int TargetsOk { get; set; }

    [JsonPropertyName("targets_partial")]
    public int TargetsPartial { get; set; }

    [JsonPropertyName("targets_missing")]
    public int TargetsMissing { get; set; }

    [JsonPropertyName("targets_error")]
    public int TargetsError { get; set; }

    [JsonPropertyName("api_operation_attempts")]
    public int RequestsAttempted { get; set; }

    [JsonPropertyName("api_operation_responses")]
    public int RequestsSucceeded { get; set; }

    [JsonPropertyName("api_operation_errors")]
    public int RequestsFailed { get; set; }

    [JsonPropertyName("api_operation_timeouts")]
    public int RequestsTimedOut { get; set; }

    [JsonPropertyName("api_operations_skipped")]
    public int RequestsSkipped { get; set; }

    // 相容別名供既有測試或消費者存取
    [JsonIgnore]
    [JsonPropertyName("total_probes")]
    public int TotalProbes => RequestsAttempted;

    [JsonIgnore]
    [JsonPropertyName("successful_probes")]
    public int SuccessfulProbes => RequestsSucceeded;

    [JsonIgnore]
    [JsonPropertyName("failed_probes")]
    public int FailedProbes => RequestsFailed;

    [JsonIgnore]
    [JsonPropertyName("unknown_probes")]
    public int UnknownProbes => RequestsSkipped;
}

public sealed class PrtgProbeTargetEvidence
{
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("target_type")]
    public string TargetType { get; set; } = string.Empty;

    [JsonPropertyName("selection_scope")]
    public string SelectionScope { get; set; } = "global-compatibility-sample";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("is_paused")]
    public bool IsPaused { get; set; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    [JsonPropertyName("snapshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrtgSnapshotEvidence? Snapshot { get; set; }

    [JsonPropertyName("channels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrtgChannelsEvidence? Channels { get; set; }

    [JsonPropertyName("history")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrtgHistoryEvidence? History { get; set; }

    [JsonPropertyName("identity_preserving_raw_history")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrtgRawChannelIdentityEvidence? RawChannelIdentity { get; set; }

    [JsonPropertyName("native_primary_capability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrtgNativePrimaryCapabilityEvidence? NativePrimaryCapability { get; set; }
}

public sealed class PrtgNativePrimaryCapabilityEvidence
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";
    [JsonPropertyName("property_support")]
    public string PropertySupport { get; set; } = "unknown";
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "getobjectproperty.htm";
    [JsonPropertyName("requested_property_name")]
    public string RequestedPropertyName { get; set; } = "primarychannel";
    [JsonPropertyName("requested_object_alias")]
    public string RequestedObjectAlias { get; set; } = string.Empty;
    [JsonPropertyName("primary_channel_id")]
    public string? PrimaryChannelId { get; set; }
    [JsonPropertyName("matches_raw_observed_channel_ids")]
    public bool? MatchesRawObservedChannelIds { get; set; }
    [JsonPropertyName("matches_snapshot_primary_channel_id")]
    public bool? MatchesSnapshotPrimaryChannelId { get; set; }
    [JsonPropertyName("property_value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PropertyValue { get; set; }
    [JsonPropertyName("response_status")]
    public string ResponseStatus { get; set; } = "unknown";
    [JsonPropertyName("http_status")]
    public string HttpStatus { get; set; } = "unknown";
    [JsonPropertyName("http_date_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HttpDateUtc { get; set; }
    [JsonPropertyName("response_format")]
    public string ResponseFormat { get; set; } = "unknown";
    [JsonPropertyName("requested_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedAtUtc { get; set; }
    [JsonPropertyName("received_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReceivedAtUtc { get; set; }
    [JsonPropertyName("elapsed_ms")]
    public double ElapsedMs { get; set; }
    [JsonPropertyName("source_version")]
    public string SourceVersion { get; set; } = "unknown";
    [JsonPropertyName("authorizes_formal_profile")]
    public bool AuthorizesFormalProfile { get; set; }
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

public sealed class PrtgRawChannelIdentityEvidence
{
    public string Status { get; set; } = "unknown";
    public string? HttpDateUtc { get; set; }
    public string? ReceivedAtUtc { get; set; }
    public string? SourceVersion { get; set; }
    public string RequestedWallStart { get; set; } = "";
    public string RequestedWallEnd { get; set; } = "";
    public string RawTimestampBasis { get; set; } = "unknown";
    public bool AuthorizesFormalProfile { get; set; }
    public int SampleCount { get; set; }
    public int? DeclaredCount { get; set; }
    public double? FirstRawOaDate { get; set; }
    public double? LastRawOaDate { get; set; }
    public List<PrtgRawChannelIdentitySample> Samples { get; set; } = [];
    public string? Error { get; set; }
}

public sealed record PrtgRawChannelIdentitySample(double RawOaDate,
    IReadOnlyList<PrtgRawChannelIdentityValue> Channels);
public sealed record PrtgRawChannelIdentityValue(string ChannelId, string SemanticCaption,
    bool SemanticKnown, double RawValue, bool ExplicitPercentDisplay);

public sealed class PrtgSnapshotEvidence
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("elapsed_ms")]
    public double ElapsedMs { get; set; }

    [JsonPropertyName("requested_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedAtUtc { get; set; }

    [JsonPropertyName("received_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReceivedAtUtc { get; set; }

    [JsonPropertyName("returned_fields")]
    public List<string> ReturnedFields { get; set; } = new();

    [JsonPropertyName("requested_fields")]
    public List<string> RequestedFields { get; set; } = new();

    [JsonPropertyName("missing_requested_fields")]
    public List<string> MissingRequestedFields { get; set; } = new();

    [JsonPropertyName("missing_fields")]
    public List<string> MissingFields { get; set; } = new();

    [JsonPropertyName("unrecognized_fields_count")]
    public int UnrecognizedFieldsCount { get; set; }

    [JsonPropertyName("fields")]
    public List<PrtgSnapshotFieldEntry> Fields { get; set; } = new();

    [JsonPropertyName("has_unfiltered_objects")]
    public bool HasUnfilteredObjects { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("native_primary_channel_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativePrimaryChannelId { get; set; }

    [JsonPropertyName("primary_channel_field_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryChannelFieldName { get; set; }

    // The bulk sensor snapshot reports a configured channel ID, not a measurement timestamp.
    [JsonPropertyName("reported_at_sample_time")]
    public bool ReportedAtSampleTime { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

public sealed class PrtgSnapshotFieldEntry
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("value_type")]
    public string ValueType { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public object? Value { get; set; }

    [JsonPropertyName("timestamp_components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimestampComponents { get; set; }

    [JsonPropertyName("timestamp_basis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimestampBasis { get; set; }

    [JsonPropertyName("timestamp_candidates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PrtgTimestampCandidate>? TimestampCandidates { get; set; }
}

public sealed class PrtgChannelsEvidence
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("parent_identity")]
    public string ParentIdentity { get; set; } = "unknown (filtered request; channel objid identifies a channel, response parent sensor id is unavailable)";

    [JsonPropertyName("request_sensor_alias")]
    public string RequestSensorAlias { get; set; } = string.Empty;

    [JsonPropertyName("request_sensor_id_provenance")]
    public string RequestSensorIdProvenance { get; set; } = "filtered request used the selected sensor objid; response parent sensor objid is unavailable";

    [JsonPropertyName("requested_fields")]
    public List<string> RequestedFields { get; set; } = new();

    [JsonPropertyName("field_presence")]
    public List<PrtgProbeFieldPresence> FieldPresence { get; set; } = new();

    [JsonPropertyName("elapsed_ms")]
    public double ElapsedMs { get; set; }

    [JsonPropertyName("requested_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedAtUtc { get; set; }

    [JsonPropertyName("received_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReceivedAtUtc { get; set; }

    [JsonPropertyName("total_rows")]
    public int TotalRows { get; set; }

    [JsonPropertyName("non_object_rows_count")]
    public int NonObjectRowsCount { get; set; }

    [JsonPropertyName("unrecognized_fields_count")]
    public int UnrecognizedFieldsCount { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("rows")]
    public List<PrtgChannelRowEntry> Rows { get; set; } = new();

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

public sealed record PrtgProbeFieldPresence(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("present_rows")] int PresentRows,
    [property: JsonPropertyName("missing_rows")] int MissingRows);

public sealed class PrtgChannelRowEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("channel_id_type")]
    public string ChannelIdType { get; set; } = "missing";

    [JsonPropertyName("semantic_name")]
    public string SemanticName { get; set; } = "[redacted]";

    [JsonPropertyName("semantic_known")]
    public bool SemanticKnown { get; set; }

    [JsonPropertyName("returned_fields")]
    public List<string> ReturnedFields { get; set; } = new();

    [JsonPropertyName("missing_fields")]
    public List<string> MissingFields { get; set; } = new();

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("unit_type")]
    public string UnitType { get; set; } = "string";

    [JsonPropertyName("scaling")]
    public double? Scaling { get; set; }

    [JsonPropertyName("scaling_type")]
    public string ScalingType { get; set; } = "missing";

    [JsonPropertyName("is_primary")]
    public bool IsPrimary { get; set; }

    [JsonPropertyName("primary_type")]
    public string PrimaryType { get; set; } = "missing";
}

public sealed class PrtgHistoryEvidence
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("elapsed_ms")]
    public double ElapsedMs { get; set; }

    [JsonPropertyName("requested_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedAtUtc { get; set; }

    [JsonPropertyName("received_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReceivedAtUtc { get; set; }

    [JsonPropertyName("total_rows")]
    public int TotalRows { get; set; }

    [JsonPropertyName("non_object_rows_count")]
    public int NonObjectRowsCount { get; set; }

    [JsonPropertyName("unrecognized_fields_count")]
    public int UnrecognizedFieldsCount { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("rows")]
    public List<PrtgHistoryRowEntry> Rows { get; set; } = new();

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

public sealed class PrtgHistoryRowEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// 有序欄位陣列，保留歷史資料原始順序與多個重複 value_raw 欄位。
    /// </summary>
    [JsonPropertyName("entries")]
    public List<PrtgHistoryFieldEntry> Entries { get; set; } = new();
}

public sealed class PrtgHistoryFieldEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public object? Value { get; set; }

    [JsonPropertyName("semantic_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SemanticLabel { get; set; }

    [JsonPropertyName("semantic_known")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SemanticKnown { get; set; }

    [JsonPropertyName("timestamp_components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimestampComponents { get; set; }

    [JsonPropertyName("timestamp_basis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimestampBasis { get; set; }

    [JsonPropertyName("timestamp_candidates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PrtgTimestampCandidate>? TimestampCandidates { get; set; }
}

public sealed class PrtgTimestampCandidate
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("start_components")]
    public string StartComponents { get; set; } = string.Empty;

    [JsonPropertyName("end_components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndComponents { get; set; }

    [JsonPropertyName("reported_offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReportedOffset { get; set; }

    [JsonPropertyName("end_reported_offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndReportedOffset { get; set; }

}

public static class PrtgCompatibilityProbe
{
    public const string BeginMarker = "BEGIN_PRTG_COMPATIBILITY_JSON";
    public const string EndMarker = "END_PRTG_COMPATIBILITY_JSON";

    public const int MaxJsonSizeBytes = 64 * 1024; // 64 KiB
    public const int MaxStringLength = 128;
    public const int MaxResponseFields = 64;
    public const int MaxChannelRows = 16;
    public const int MaxHistoryRows = 3;
    public const int MaxNativePrimaryXmlBytes = 64 * 1024;

    public static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static readonly IReadOnlyList<(string Category, string SensorType, string Alias)> TargetCategories = new[]
    {
        ("cpu", "SNMP CPU Load", "s1"),
        ("memory", "SNMP Memory", "s2"),
        ("disk", "SNMP Disk Free", "s3")
    };

    private static readonly HashSet<string> AllowedSemanticLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "total", "cpu", "cpu total", "total cpu", "cpu load", "cpu cores", "cpu usage",
        "total memory", "memory total", "available memory", "memory available", "percent available memory",
        "free memory", "memory free", "used memory", "memory used",
        "free disk", "disk free", "available disk", "disk available", "percent available disk",
        "used disk", "disk used", "total disk", "disk total",
        "可用", "已用", "剩餘", "總計", "記憶體", "磁碟", "剩餘 記憶體"
    };

    private static readonly HashSet<string> WhitelistSnapshotColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "objid", "type", "status", "status_raw", "lastvalue_raw", "lastcheck", "lastcheck_raw", "interval", "interval_raw",
        "primarychannel", "primarychannel_raw"
    };

    private static readonly string[] SnapshotRequestedColumns = ["objid", "type", "status", "lastvalue_raw", "lastcheck", "interval", "primarychannel"];
    private static readonly string[] ChannelRequestedColumns = ["objid", "name", "lastvalue", "unit", "scaling", "primary"];

    private static readonly HashSet<string> WhitelistHistoryColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime", "datetime_raw", "value", "value_raw", "coverage", "coverage_raw"
    };

    private static readonly HashSet<string> AllowedUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "%", "ms", "s", "sec", "second", "seconds", "m", "min", "minute", "minutes",
        "h", "hr", "hour", "hours", "d", "day", "days",
        "b", "byte", "bytes", "kb", "kbyte", "kbytes", "mb", "mbyte", "mbytes",
        "gb", "gbyte", "gbytes", "tb", "tbyte", "tbytes",
        "bit", "bits", "kbit", "kbits", "mbit", "mbits", "gbit", "gbits", "tbit", "tbits",
        "bit/s", "kbit/s", "mbit/s", "gbit/s", "byte/s", "kbyte/s", "mbyte/s", "gbyte/s",
        "°c", "c", "°f", "f", "k", "v", "a", "w", "kw", "mw", "hz", "khz", "mhz", "ghz",
        "rpm", "count", "items", "units", "req/s", "requests/s", "pkg/s", "packets/s",
        "/s", "/min", "個", "次", "百分比", "條"
    };

    private static readonly HashSet<string> AllowedSensorStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Up", "Down", "Warning", "Paused", "Unknown", "Unusual",
        "Down (Acknowledged)", "Down (Partial)", "None"
    };

    private static readonly HashSet<string> AllowedSensorTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "snmp cpu load", "snmpcpuload",
        "snmp memory", "snmpmemory",
        "snmp disk free", "snmpdiskfree"
    };

    private static readonly Regex IpRegex = new(
        @"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b",
        RegexOptions.Compiled);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// 計算來源連線位址的去識別匿名指紋（正規化後 SHA-256，絕不帶憑證與查詢字串）。
    /// </summary>
    public static string ComputeUrlFingerprint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "unknown";
        try
        {
            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                var builder = new UriBuilder(uri)
                {
                    UserName = "",
                    Password = "",
                    Query = "",
                    Fragment = ""
                };
                var normalized = builder.Uri.ToString().TrimEnd('/').ToLowerInvariant();
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
        }
        catch
        {
            // fallback
        }

        return "unknown";
    }

    public static Task<PrtgCompatibilityProbeEvidence> ExecuteAsync(
        PrtgClient client,
        IRunConsole console,
        IReadOnlyList<PrtgProbeRunner.SensorTypeSample> sensorSamples,
        PrtgProbeEvidenceContext? context,
        CancellationToken ct = default,
        Action<string>? onEvidenceJsonProduced = null)
    {
        return ExecuteCoreAsync(
            (url, token) => client.GetBoundedJsonAsync(url, PrtgHistoricXmlReader.MaximumBytes, token),
            console,
            sensorSamples,
            context,
            ct,
            onEvidenceJsonProduced,
            (url, token) => client.GetBoundedXmlAsync(url, PrtgHistoricXmlReader.MaximumBytes, token),
            (url, token) => client.GetBoundedXmlAsync(url, MaxNativePrimaryXmlBytes, token),
            (url, token) => client.GetBoundedJsonAsync(url, 512 * 1024, token));
    }

    public static async Task<PrtgCompatibilityProbeEvidence> ExecuteCoreAsync(
        Func<string, CancellationToken, Task<string>> getJson,
        IRunConsole console,
        IReadOnlyList<PrtgProbeRunner.SensorTypeSample> sensorSamples,
        PrtgProbeEvidenceContext? context,
        CancellationToken ct = default,
        Action<string>? onEvidenceJsonProduced = null,
        Func<string, CancellationToken, Task<PrtgSourceResponse>>? getHistoricXml = null,
        Func<string, CancellationToken, Task<PrtgSourceResponse>>? getNativePrimaryXml = null,
        Func<string, CancellationToken, Task<string>>? getSensorBatchJson = null)
    {
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(OverallTimeout);
        var probeToken = overallCts.Token;

        var yesterday = DateTime.Today.AddDays(-1);
        var sdate = yesterday.ToString("yyyy-MM-dd-00-00-00", CultureInfo.InvariantCulture);
        var edate = yesterday.AddDays(1).ToString("yyyy-MM-dd-00-00-00", CultureInfo.InvariantCulture);

        var evidence = new PrtgCompatibilityProbeEvidence
        {
            SchemaVersion = "1.0.0",
            GeneratedAtUtc = (context?.GeneratedAtUtc ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            HostUtcOffset = SafeUtcOffset(context?.HostUtcOffset),
            BuildVersion = SafeBuildVersion(context?.BuildVersion),
            SourcePrtgVersion = SanitizePrtgVersion(context?.SourcePrtgVersion) ?? "unknown",
            DeploymentResources = new PrtgDeploymentResources(),
            StorageEnvironment = SanitizeStorageEnvironment(context?.StorageEnvironment),
            SettingsRevision = SafeSettingsRevision(context?.SettingsRevision),
            SourceFingerprint = SafeFingerprint(context?.SourceFingerprint),
            SourceTimezone = "unknown",
            SourceLocale = "unknown",
            StorageProvider = SafeProvider(context?.StorageProvider),
            EfCoreProvider = SafeEfProvider(context?.EfCoreProvider),
            RetentionDays = context?.RetentionDays is >= 1 and <= 10000 ? context.RetentionDays : null,
            ScopeSummary = "unknown (no bounded summary available)",
            ReadinessSummary = "unknown (no bounded summary available)",
            Status = "unknown",
            EvidenceReady = false,
            HistoricalDateBoundary = new PrtgDateBoundary
            {
                Start = $"{yesterday:yyyy-MM-dd} 00:00:00",
                End = $"{yesterday.AddDays(1):yyyy-MM-dd} 00:00:00",
                Limitations = "來源時區及時間欄位基準未確認"
            }
        };

        var targetedSensorsCount = 0;
        var requestsAttempted = 0;
        var requestsSucceeded = 0;
        var requestsFailed = 0;
        var requestsTimedOut = 0;
        var requestsSkipped = 0;

        foreach (var (category, sensorType, alias) in TargetCategories)
        {
            ct.ThrowIfCancellationRequested();

            var matching = sensorSamples
                .Where(s => s.Objid.HasValue && string.Equals(s.Type, sensorType, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Objid!.Value)
                .ToList();

            if (matching.Count == 0)
            {
                console.WriteLine($"     相容性樣本 [{alias}] {category}（{sensorType}）：無可用樣本（missing）");
                requestsSkipped += 3;
                evidence.Targets.Add(new PrtgProbeTargetEvidence
                {
                    Alias = alias,
                    Category = category,
                    TargetType = sensorType,
                    Status = "missing",
                    IsPaused = false,
                    Reason = "No sensor sample found for type in step 3"
                });
                continue;
            }

            targetedSensorsCount++;
            var candidate = matching.FirstOrDefault(s => s.Status == null || s.Status.IndexOf("paus", StringComparison.OrdinalIgnoreCase) < 0)
                            ?? matching[0];
            var isPaused = candidate.Status != null && candidate.Status.IndexOf("paus", StringComparison.OrdinalIgnoreCase) >= 0;
            var actualObjid = candidate.Objid!.Value;

            console.WriteLine($"     相容性樣本 [{alias}] {category}（{sensorType}）：選定 objid={actualObjid}{(isPaused ? "（已暫停）" : "")}");

            var target = new PrtgProbeTargetEvidence
            {
                Alias = alias,
                Category = category,
                TargetType = sensorType,
                IsPaused = isPaused
            };

            // 1. Filtered snapshot
            requestsAttempted++;
            target.Snapshot = await ProbeSnapshotAsync(getJson, actualObjid, alias, console, probeToken, ct);
            TrackRequestStatus(target.Snapshot.Status, ref requestsSucceeded, ref requestsFailed, ref requestsTimedOut);

            // 2. Channels
            requestsAttempted++;
            target.Channels = await ProbeChannelsAsync(getJson, actualObjid, alias, console, probeToken, ct);
            TrackRequestStatus(target.Channels.Status, ref requestsSucceeded, ref requestsFailed, ref requestsTimedOut);

            // 3. Hourly history
            requestsAttempted++;
            target.History = await ProbeHistoryAsync(getJson, actualObjid, alias, sdate, edate, console, probeToken, ct);
            TrackRequestStatus(target.History.Status, ref requestsSucceeded, ref requestsFailed, ref requestsTimedOut);

            if (getHistoricXml is not null)
            {
                requestsAttempted++;
                target.RawChannelIdentity = await ProbeRawChannelIdentityAsync(getHistoricXml, actualObjid,
                    sdate, yesterday.ToString("yyyy-MM-dd-01-00-00", CultureInfo.InvariantCulture), probeToken, ct);
                TrackRequestStatus(target.RawChannelIdentity.Status, ref requestsSucceeded, ref requestsFailed, ref requestsTimedOut);
            }

            if (getNativePrimaryXml is not null)
            {
                var rawChannelIds = target.RawChannelIdentity?.Samples
                    .SelectMany(sample => sample.Channels)
                    .Select(channel => channel.ChannelId)
                    .Where(IsNativePrimaryObjectId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                requestsAttempted++;
                target.NativePrimaryCapability = await ProbeNativePrimaryCapabilityAsync(
                    getNativePrimaryXml, actualObjid, alias,
                    target.RawChannelIdentity?.SourceVersion ?? context?.SourcePrtgVersion,
                    rawChannelIds is { Length: > 0 } ? rawChannelIds : null,
                    target.Snapshot!.NativePrimaryChannelId, probeToken, ct);
                TrackRequestStatus(target.NativePrimaryCapability.Status, ref requestsSucceeded, ref requestsFailed, ref requestsTimedOut);
            }

            // 判定該 target 的整體狀態
            if (isPaused)
            {
                target.Status = "paused";
                target.Reason = "Sensor is in paused state; not evaluated for readiness";
            }
            else if (target.Snapshot.Status == "ok" && target.Channels.Status == "ok" && target.History.Status == "ok" &&
                     (target.RawChannelIdentity is null || target.RawChannelIdentity.Status == "ok"))
            {
                target.Status = "ok";
            }
            else if (target.Snapshot.Status is "error" or "malformed_json" or "timeout" ||
                     target.Channels.Status is "error" or "malformed_json" or "timeout" ||
                     target.History.Status is "error" or "malformed_json" or "timeout" ||
                     target.RawChannelIdentity?.Status is "error" or "timeout")
            {
                target.Status = "error";
            }
            else if (target.Snapshot.Status == "missing" && target.Channels.Status == "missing" && target.History.Status == "missing")
            {
                target.Status = "missing";
            }
            else
            {
                target.Status = "partial";
            }

            evidence.Targets.Add(target);
        }

        if (getSensorBatchJson is not null)
        {
            var batchIds = sensorSamples.Where(s => s.Objid > 0).Select(s => s.Objid!.Value)
                .Distinct().Order().Take(5).ToArray();
            if (batchIds.Length < 2)
                evidence.SensorBatchIdentity = new() { Reason = "insufficient-distinct-samples" };
            else
            {
                requestsAttempted++;
                var began = DateTimeOffset.UtcNow;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(probeToken);
                    request.CancelAfter(RequestTimeout);
                    var json = await getSensorBatchJson("api/table.json?content=sensors&columns=objid,parentid,type,status,lastvalue,lastcheck,interval,cumsince" +
                        PrtgResourceGuardProbe.BuildObjidFilter(batchIds) + "&count=" + (batchIds.Length + 1), request.Token);
                    evidence.SensorBatchIdentity = PrtgSensorBatchCapability.Parse(json, batchIds);
                    requestsSucceeded++;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { evidence.SensorBatchIdentity = new() { Status = "timeout", Reason = "bounded-request-deadline" }; requestsTimedOut++; }
                catch (Exception ex) when (ex is PrtgClientException or InvalidDataException or JsonException or HttpRequestException or InvalidOperationException)
                { evidence.SensorBatchIdentity = new() { Status = "error", Reason = "bounded-source-unavailable" }; requestsFailed++; }
                elapsed.Stop();
                evidence.SensorBatchIdentity.RequestedAtUtc = began.ToString("o");
                evidence.SensorBatchIdentity.ReceivedAtUtc = DateTimeOffset.UtcNow.ToString("o");
                evidence.SensorBatchIdentity.ElapsedMilliseconds = (long)Math.Ceiling(elapsed.Elapsed.TotalMilliseconds);
            }
            console.WriteLine($"     多感測器精確集合探測：{evidence.SensorBatchIdentity.Status}（{evidence.SensorBatchIdentity.Reason}）；不授權正式 profile。");
        }

        var targetsOk = 0;
        var targetsPartial = 0;
        var targetsMissing = 0;
        var targetsError = 0;

        foreach (var t in evidence.Targets)
        {
            switch (t.Status)
            {
                case "ok": targetsOk++; break;
                case "partial": targetsPartial++; break;
                case "missing": targetsMissing++; break;
                case "paused": targetsMissing++; break;
                default: targetsError++; break;
            }
        }

        evidence.Summary = new PrtgProbeEvidenceSummary
        {
            TargetCategoriesCount = TargetCategories.Count,
            TargetedSensorsCount = targetedSensorsCount,
            TargetsOk = targetsOk,
            TargetsPartial = targetsPartial,
            TargetsMissing = targetsMissing,
            TargetsError = targetsError,
            RequestsAttempted = requestsAttempted,
            RequestsSucceeded = requestsSucceeded,
            RequestsFailed = requestsFailed,
            RequestsTimedOut = requestsTimedOut,
            RequestsSkipped = requestsSkipped
        };

        // 依規格：環境探測證據整體 status 保持 partial 或 unknown，且 evidenceReady 必為 false
        if (targetsOk > 0 || targetsPartial > 0)
        {
            evidence.Status = "partial";
        }
        else if (targetsMissing == TargetCategories.Count)
        {
            evidence.Status = "unknown";
        }
        else if (targetsError > 0)
        {
            evidence.Status = "error";
        }
        else
        {
            evidence.Status = "unknown";
        }

        var serializedJson = SerializeEvidence(evidence);
        console.WriteLine();
        console.WriteLine(BeginMarker);
        console.WriteLine(serializedJson);
        console.WriteLine(EndMarker);
        console.WriteLine();

        onEvidenceJsonProduced?.Invoke(serializedJson);

        return evidence;
    }

    private static void TrackRequestStatus(string status, ref int succeeded, ref int failed, ref int timedOut)
    {
        switch (status)
        {
            case "ok":
            case "partial":
            case "missing":
                succeeded++;
                break;
            case "timeout":
                timedOut++;
                break;
            default:
                failed++;
                break;
        }
    }

    private static bool IsNativePrimaryObjectId(string? value) =>
        value is { Length: > 0 and <= 20 } &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0;

    private static async Task<PrtgNativePrimaryCapabilityEvidence> ProbeNativePrimaryCapabilityAsync(
        Func<string, CancellationToken, Task<PrtgSourceResponse>> getXml, long sensorId, string alias,
        string? sourceVersion, IReadOnlyCollection<string>? rawChannelIds, string? snapshotPrimaryChannelId,
        CancellationToken probeToken, CancellationToken userToken)
    {
        var evidence = new PrtgNativePrimaryCapabilityEvidence
        {
            RequestedObjectAlias = alias,
            SourceVersion = SanitizePrtgVersion(sourceVersion) ?? "unknown"
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(probeToken);
            request.CancelAfter(RequestTimeout);
            evidence.RequestedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var response = await getXml($"/api/getobjectproperty.htm?id={sensorId}&name=primarychannel", request.Token);
            evidence.ReceivedAtUtc = response.ReceivedAtUtc.ToString("o", CultureInfo.InvariantCulture);
            evidence.HttpDateUtc = response.HttpDateUtc?.ToString("o", CultureInfo.InvariantCulture);
            evidence.ResponseStatus = "success";
            evidence.HttpStatus = "2xx";
            evidence.ResponseFormat = "xml";

            if (!string.IsNullOrWhiteSpace(sourceVersion) && evidence.SourceVersion == "unknown")
            {
                evidence.Error = "source-version-malformed";
                return evidence;
            }

            if (Encoding.UTF8.GetByteCount(response.Content) > MaxNativePrimaryXmlBytes)
            {
                evidence.Error = "response_over_limit";
                return evidence;
            }

            XElement root;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxNativePrimaryXmlBytes,
                    MaxCharactersFromEntities = 0
                };
                using (var depthReader = XmlReader.Create(new StringReader(response.Content), settings))
                {
                    while (depthReader.Read())
                        if (depthReader.Depth > 8) throw new InvalidDataException("property-xml-depth-limit");
                }
                using var reader = XmlReader.Create(new StringReader(response.Content), settings);
                var document = XDocument.Load(reader, LoadOptions.None);
                root = document.Root ?? throw new InvalidDataException("property-response-root-missing");
            }
            catch (XmlException)
            {
                evidence.ResponseFormat = "malformed_xml";
                evidence.Error = "malformed_xml";
                return evidence;
            }
            catch (InvalidDataException ex)
            {
                evidence.ResponseFormat = "invalid_xml";
                evidence.Error = ex.Message == "property-xml-depth-limit" ? "xml_depth_limit" : "invalid_xml";
                return evidence;
            }

            if (root.Name != XName.Get("prtg"))
            {
                evidence.ResponseFormat = "unexpected_xml";
                evidence.Error = "unexpected_xml_root";
                return evidence;
            }

            var results = root.DescendantsAndSelf()
                .Where(element => string.Equals(element.Name.LocalName, "result", StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (results.Length != 1)
            {
                evidence.Error = results.Length == 0 ? "property_result_missing" : "ambiguous_property_value";
                return evidence;
            }

            if (results[0].Parent != root || results[0].Name != XName.Get("result") || results[0].HasElements)
            {
                evidence.Error = "invalid_property_result_shape";
                return evidence;
            }

            var propertyValue = results[0].Value.Trim();
            if (propertyValue.Length is < 1 or > 20 ||
                !long.TryParse(propertyValue, NumberStyles.None, CultureInfo.InvariantCulture, out var primaryChannelId) ||
                primaryChannelId < 0)
            {
                evidence.Error = "invalid_property_value";
                return evidence;
            }

            evidence.Status = "ok";
            evidence.PropertySupport = "supported";
            evidence.PropertyValue = propertyValue;
            evidence.PrimaryChannelId = primaryChannelId.ToString(CultureInfo.InvariantCulture);
            if (rawChannelIds is { Count: > 0 })
                evidence.MatchesRawObservedChannelIds = rawChannelIds.Any(id =>
                    long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var rawId) && rawId == primaryChannelId);
            if (IsNativePrimaryObjectId(snapshotPrimaryChannelId))
                evidence.MatchesSnapshotPrimaryChannelId =
                    long.TryParse(snapshotPrimaryChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out var snapshotId) && snapshotId == primaryChannelId;
            // Bulk snapshot and native property observations are diagnostic only. Neither
            // establishes measurement-time identity, source semantics, or a formal profile.
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            evidence.Status = "timeout";
            evidence.ResponseStatus = "timeout";
            evidence.Error = "request-timeout";
        }
        catch (PrtgClientException ex)
        {
            evidence.ResponseStatus = "error";
            if (ex.Message.Contains("探測回應超過有界讀取上限", StringComparison.Ordinal))
            {
                evidence.Error = "response_over_limit";
                evidence.ResponseFormat = "unknown";
            }
            else
            {
                evidence.Status = "error";
                evidence.Error = FormatSafeError(ex);
            }
            evidence.HttpStatus = ExtractHttpStatus(evidence.Error);
        }
        catch (Exception ex)
        {
            evidence.Status = "error";
            evidence.ResponseStatus = "error";
            evidence.Error = FormatSafeError(ex);
            evidence.HttpStatus = ExtractHttpStatus(evidence.Error);
        }
        finally
        {
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        }

        return evidence;
    }

    private static string ExtractHttpStatus(string? safeError)
    {
        if (safeError is null) return "unknown";
        var match = Regex.Match(safeError, @"\bHTTP\s+([1-5][0-9]{2})\b", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : "unknown";
    }

    private static async Task<PrtgRawChannelIdentityEvidence> ProbeRawChannelIdentityAsync(
        Func<string, CancellationToken, Task<PrtgSourceResponse>> getXml, long sensorId,
        string start, string end, CancellationToken probeToken, CancellationToken userToken)
    {
        var evidence = new PrtgRawChannelIdentityEvidence { RequestedWallStart = start, RequestedWallEnd = end };
        try
        {
            // Budget admission can wait for the shared 5/min historic allowance.
            // The whole compatibility probe still has the existing five-minute deadline.
            using var request = CancellationTokenSource.CreateLinkedTokenSource(probeToken);
            request.CancelAfter(TimeSpan.FromSeconds(90));
            var response = await getXml($"/api/historicdata.xml?id={sensorId}&avg=0&usecaption=1&sdate={start}&edate={end}", request.Token);
            var parsed = PrtgHistoricXmlReader.Parse(response.Content);
            evidence.HttpDateUtc = response.HttpDateUtc?.ToString("o", CultureInfo.InvariantCulture);
            evidence.ReceivedAtUtc = response.ReceivedAtUtc.ToString("o", CultureInfo.InvariantCulture);
            evidence.SourceVersion = SanitizePrtgVersion(parsed.Version);
            evidence.SampleCount = parsed.Samples.Count;
            evidence.DeclaredCount = parsed.DeclaredCount;
            evidence.FirstRawOaDate = parsed.Samples.FirstOrDefault()?.MeasuredOaDate;
            evidence.LastRawOaDate = parsed.Samples.LastOrDefault()?.MeasuredOaDate;
            foreach (var sample in parsed.Samples.Take(3))
            {
                var values = sample.Channels.Take(8).Select(channel =>
                {
                    var (caption, known) = SanitizeChannelName(channel.Caption);
                    return new PrtgRawChannelIdentityValue(channel.ChannelId, caption, known,
                        channel.RawValue, channel.DisplayValue?.Trim().EndsWith('%') == true);
                }).ToArray();
                evidence.Samples.Add(new(sample.MeasuredOaDate, values));
            }
            evidence.Status = parsed.Samples.Count > 0 ? "ok" : "missing";
            // Successful decoding proves neither the raw timestamp clock nor which
            // channel is primary. This evidence never authorizes a formal profile.
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { evidence.Status = "timeout"; evidence.Error = "raw-channel-probe-deadline"; }
        catch (Exception ex) when (ex is InvalidDataException or PrtgClientException)
        { evidence.Status = "error"; evidence.Error = "raw-channel-source-shape-or-transport-invalid"; }
        return evidence;
    }

    private static async Task<PrtgSnapshotEvidence> ProbeSnapshotAsync(
        Func<string, CancellationToken, Task<string>> getJson,
        long actualObjid,
        string alias,
        IRunConsole console,
        CancellationToken probeToken,
        CancellationToken userToken)
    {
        var evidence = new PrtgSnapshotEvidence { RequestedFields = SnapshotRequestedColumns.ToList() };
        // PRTG expands requested base columns with their _raw forms. Asking for both
        // spellings duplicates properties in the response and obscures the field shape.
        // Request primarychannel as discovery only; PRTG versions may omit or shape it differently.
        var url = $"/api/table.json?content=sensors&columns=objid,type,status,lastvalue_raw,lastcheck,interval,primarychannel&filter_objid={actualObjid}";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(probeToken);
            reqCts.CancelAfter(RequestTimeout);

            evidence.RequestedAtUtc = DateTime.UtcNow.ToString("o");
            var json = await getJson(url, reqCts.Token);
            evidence.ReceivedAtUtc = DateTime.UtcNow.ToString("o");
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sensors", out var sensorsArr) || sensorsArr.ValueKind != JsonValueKind.Array)
            {
                evidence.Status = "malformed_json";
                evidence.Error = "Response does not contain a sensors array";
                return evidence;
            }

            JsonElement? matchingRow = null;
            var hasUnfiltered = false;

            foreach (var row in sensorsArr.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    hasUnfiltered = true;
                    continue;
                }
                var rowIdStr = GetStringProperty(row, "objid");
                if (!long.TryParse(rowIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowId))
                {
                    hasUnfiltered = true;
                    continue;
                }
                if (rowId != actualObjid) hasUnfiltered = true;
                else if (matchingRow.HasValue) hasUnfiltered = true;
                else matchingRow = row;
            }

            evidence.HasUnfilteredObjects = hasUnfiltered;

            if (!matchingRow.HasValue)
            {
                evidence.Status = "missing";
                evidence.Error = $"Target sensor objid was not returned in sensors array (count={sensorsArr.GetArrayLength()})";
                return evidence;
            }

            var rowEl = matchingRow.Value;
            var returnedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryChannelValues = new List<(string FieldName, long? Value)>();

            foreach (var prop in rowEl.EnumerateObject().Take(MaxResponseFields))
            {
                var colName = prop.Name.ToLowerInvariant();
                if (!WhitelistSnapshotColumns.Contains(colName))
                {
                    // 未知或非白名單欄位：不輸出欄位名稱與值，僅累計數量以防洩漏
                    evidence.UnrecognizedFieldsCount++;
                    continue;
                }

                returnedProps.Add(colName);
                if (!evidence.ReturnedFields.Contains(colName, StringComparer.Ordinal))
                    evidence.ReturnedFields.Add(colName);

                if (colName is "primarychannel" or "primarychannel_raw")
                {
                    long? primaryValue = null;
                    if (prop.Value.ValueKind == JsonValueKind.Number &&
                        prop.Value.TryGetInt64(out var primaryId) && primaryId >= 0)
                        primaryValue = primaryId;
                    primaryChannelValues.Add((colName, primaryValue));
                    continue;
                }

                if (colName == "objid")
                {
                    evidence.Fields.Add(new PrtgSnapshotFieldEntry
                    {
                        Field = "objid",
                        ValueType = "alias",
                        Value = alias
                    });
                    continue;
                }

                if (colName == "lastcheck_raw" && TryGetOaTimestamp(prop.Value, out var oaTimestamp))
                {
                    evidence.Fields.Add(new PrtgSnapshotFieldEntry
                    {
                        Field = colName,
                        ValueType = prop.Value.ValueKind == JsonValueKind.String ? "numeric_string" : "json_number",
                        Value = ReadFiniteNumber(prop.Value),
                        TimestampComponents = FormatTimestampComponents(oaTimestamp),
                        TimestampBasis = "unknown",
                        TimestampCandidates = new List<PrtgTimestampCandidate>
                        {
                            new() { Format = "prtg-raw-date-time", StartComponents = FormatTimestampComponents(oaTimestamp) }
                        }
                    });
                    continue;
                }

                if (colName is "status_raw" or "lastvalue_raw" or "lastcheck_raw" or "interval_raw" or "interval")
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (prop.Value.TryGetInt64(out var n))
                        {
                            evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "json_number", Value = n });
                        }
                        else if (prop.Value.TryGetDouble(out var d) && double.IsFinite(d))
                        {
                            evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "json_number", Value = d });
                        }
                        else
                        {
                            evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "invalid_number", Value = null });
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String &&
                             double.TryParse(prop.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var num) && double.IsFinite(num))
                    {
                        evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "numeric_string", Value = num });
                    }
                    else if (colName == "interval" && prop.Value.ValueKind == JsonValueKind.String &&
                             prop.Value.GetString() is string duration && duration.Length <= 64 &&
                             Regex.IsMatch(duration.Trim(), @"\A[0-9]{1,9}(?:\.[0-9]{1,6})?\s*(?:s|sec|second|seconds|m|min|minute|minutes|h|hr|hour|hours)\z",
                                 RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        // Retain only a constrained duration, never arbitrary source text.
                        // This records the actual field shape without inferring raw-field units.
                        evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "duration_string", Value = duration.Trim() });
                    }
                    else
                    {
                        evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "invalid_number", Value = null });
                    }
                    continue;
                }

                if (colName == "lastcheck")
                {
                    var rawStr = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                    var timestampCandidates = ParseTimestampCandidates(rawStr);
                    if (timestampCandidates.Count > 0)
                    {
                        var uniquePoint = timestampCandidates.Count == 1 && timestampCandidates[0].EndComponents == null;
                        var uniqueRange = timestampCandidates.Count == 1 && timestampCandidates[0].EndComponents != null;
                        evidence.Fields.Add(new PrtgSnapshotFieldEntry
                        {
                            Field = colName,
                            ValueType = uniquePoint ? "datetime_string" : uniqueRange ? "datetime_range" : "datetime_candidates",
                            Value = uniquePoint ? timestampCandidates[0].StartComponents : uniqueRange ? "[range]" : "[ambiguous]",
                            TimestampComponents = uniquePoint ? timestampCandidates[0].StartComponents : null,
                            TimestampBasis = "unknown",
                            TimestampCandidates = timestampCandidates
                        });
                    }
                    else
                    {
                        evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = $"invalid_datetime_{JsonKind(prop.Value)}", Value = "[redacted]" });
                    }
                    continue;
                }

                if (colName == "status")
                {
                    var rawStr = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()?.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(rawStr) && AllowedSensorStatuses.Contains(rawStr))
                    {
                        evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "status", Value = rawStr });
                    }
                    else
                    {
                        evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "status", Value = "[unknown_status]" });
                    }
                    continue;
                }

                if (colName == "type")
                {
                    var rawStr = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()?.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(rawStr) && AllowedSensorTypes.Contains(rawStr))
                    {
                        evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "type", Value = rawStr.ToLowerInvariant() });
                    }
                    else
                    {
                        evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "type", Value = "[unknown_type]" });
                    }
                    continue;
                }
            }
            evidence.Truncated = rowEl.EnumerateObject().Skip(MaxResponseFields).Any();

            if (primaryChannelValues.Count == 1)
            {
                evidence.PrimaryChannelFieldName = primaryChannelValues[0].FieldName;
                if (primaryChannelValues[0].Value.HasValue)
                    evidence.NativePrimaryChannelId = primaryChannelValues[0].Value.Value.ToString(CultureInfo.InvariantCulture);
            }
            else if (primaryChannelValues.Count > 1 &&
                     primaryChannelValues.All(value => value.Value.HasValue) &&
                     primaryChannelValues.Select(value => value.Value!.Value).Distinct().Count() == 1)
            {
                evidence.NativePrimaryChannelId = primaryChannelValues[0].Value!.Value.ToString(CultureInfo.InvariantCulture);
                evidence.PrimaryChannelFieldName = primaryChannelValues.Any(value => value.FieldName == "primarychannel_raw")
                    ? "primarychannel_raw"
                    : "primarychannel";
            }

            foreach (var col in WhitelistSnapshotColumns)
            {
                if (col == "primarychannel_raw")
                    continue;
                if (col == "primarychannel" && (returnedProps.Contains("primarychannel") || returnedProps.Contains("primarychannel_raw")))
                    continue;
                if (!returnedProps.Contains(col))
                {
                    evidence.MissingFields.Add(col);
                }
            }
            evidence.ReturnedFields.Sort(StringComparer.Ordinal);
            evidence.MissingFields.Sort(StringComparer.Ordinal);
            foreach (var requested in SnapshotRequestedColumns)
            {
                var present = requested switch
                {
                    "lastcheck" => returnedProps.Contains("lastcheck") || returnedProps.Contains("lastcheck_raw"),
                    "interval" => returnedProps.Contains("interval") || returnedProps.Contains("interval_raw"),
                    "status" => returnedProps.Contains("status") || returnedProps.Contains("status_raw"),
                    // A present but malformed/conflicting discovery field remains unknown.
                    "primarychannel" => evidence.NativePrimaryChannelId is not null,
                    _ => returnedProps.Contains(requested)
                };
                if (!present) evidence.MissingRequestedFields.Add(requested);
            }

            // 依規格：若缺失欄位或有額外物件，標記為 partial 而非全部 ok
            if (evidence.MissingFields.Count > 0 || hasUnfiltered || evidence.Truncated)
            {
                evidence.Status = "partial";
            }
            else
            {
                evidence.Status = "ok";
            }
            return evidence;
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!userToken.IsCancellationRequested && (probeToken.IsCancellationRequested || ex is TimeoutException or OperationCanceledException))
        {
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            evidence.Status = "timeout";
            evidence.Error = "Snapshot request timed out";
            console.WriteLine($"     ⚠ [{alias}] 快照查詢逾時（30s）");
            return evidence;
        }
        catch (JsonException)
        {
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            evidence.Status = "malformed_json";
            evidence.Error = "Response is not valid JSON";
            console.WriteLine($"     ⚠ [{alias}] 快照回應非合法 JSON：{evidence.Error}");
            return evidence;
        }
        catch (Exception ex)
        {
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            evidence.Status = "error";
            evidence.Error = FormatSafeError(ex);
            console.WriteLine($"     ⚠ [{alias}] 快照查詢失敗：{evidence.Error}");
            return evidence;
        }
    }

    private static async Task<PrtgChannelsEvidence> ProbeChannelsAsync(
        Func<string, CancellationToken, Task<string>> getJson,
        long actualObjid,
        string alias,
        IRunConsole console,
        CancellationToken probeToken,
        CancellationToken userToken)
    {
        var evidence = new PrtgChannelsEvidence
        {
            RequestSensorAlias = alias,
            RequestedFields = ChannelRequestedColumns.ToList(),
            FieldPresence = ChannelRequestedColumns.Select(field => new PrtgProbeFieldPresence(field, 0, 0)).ToList()
        };
        var url = $"/api/table.json?content=channels&id={actualObjid}&columns=objid,name,lastvalue,unit,scaling,primary&count={MaxChannelRows}";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(probeToken);
            reqCts.CancelAfter(RequestTimeout);

            evidence.RequestedAtUtc = DateTime.UtcNow.ToString("o");
            var json = await getJson(url, reqCts.Token);
            evidence.ReceivedAtUtc = DateTime.UtcNow.ToString("o");
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("channels", out var channelsArr) || channelsArr.ValueKind != JsonValueKind.Array)
            {
                evidence.Status = "malformed_json";
                evidence.Error = "Response does not contain a channels array";
                return evidence;
            }

            var totalRows = channelsArr.GetArrayLength();
            evidence.TotalRows = totalRows;
            evidence.Truncated = totalRows > MaxChannelRows;

            if (totalRows == 0)
            {
                evidence.Status = "missing";
                return evidence;
            }

            var idx = 0;
            var processedFields = 0;
            var presentRowCounts = ChannelRequestedColumns.ToDictionary(field => field, _ => 0, StringComparer.Ordinal);
            foreach (var ch in channelsArr.EnumerateArray().Take(MaxChannelRows))
            {
                if (ch.ValueKind != JsonValueKind.Object)
                {
                    evidence.NonObjectRowsCount++;
                    continue;
                }

                foreach (var prop in ch.EnumerateObject())
                {
                    if (processedFields++ >= MaxResponseFields)
                    {
                        evidence.Truncated = true;
                        continue;
                    }
                    if (!new[] { "objid", "name", "channel", "lastvalue", "unit", "scaling", "primary" }.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                        evidence.UnrecognizedFieldsCount++;
                }

                var hasName = ch.TryGetProperty("name", out var nameElement);
                var hasLegacyChannel = ch.TryGetProperty("channel", out var legacyChannelElement);
                var rawName = hasName
                    ? (nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : nameElement.GetRawText())
                    : hasLegacyChannel
                        ? (legacyChannelElement.ValueKind == JsonValueKind.String ? legacyChannelElement.GetString() : legacyChannelElement.GetRawText())
                        : null;
                var (semanticName, isKnown) = SanitizeChannelName(rawName);

                var returnedFields = new List<string>();
                foreach (var (field, present) in new[]
                         {
                             ("objid", ch.TryGetProperty("objid", out _)),
                             ("name", hasName),
                             ("channel", hasLegacyChannel),
                             ("unit", ch.TryGetProperty("unit", out _)),
                             ("scaling", ch.TryGetProperty("scaling", out _)),
                             ("primary", ch.TryGetProperty("primary", out _))
                         })
                {
                    if (present) returnedFields.Add(field);
                }
                foreach (var requested in ChannelRequestedColumns)
                {
                    var present = requested switch
                    {
                        "name" => hasName,
                        _ => ch.TryGetProperty(requested, out _)
                    };
                    if (present) presentRowCounts[requested]++;
                }
                var missingFields = new[] { "objid", "name", "unit", "scaling", "primary" }
                    .Where(field => !returnedFields.Contains(field, StringComparer.Ordinal)).ToList();

                var rawUnit = GetStringProperty(ch, "unit")?.Trim();
                string? unit = null;
                string unitType = ch.TryGetProperty("unit", out var unitEl) ? JsonKind(unitEl) : "missing";
                if (!string.IsNullOrWhiteSpace(rawUnit))
                {
                    if (AllowedUnits.Contains(rawUnit))
                    {
                        unit = rawUnit.ToLowerInvariant();
                    }
                    else
                    {
                        unit = "[redacted]";
                    }
                }

                double? scaling = null;
                var scalingPresent = ch.TryGetProperty("scaling", out var scEl);
                var scalingType = scalingPresent ? JsonKind(scEl) : "missing";
                if (scalingPresent)
                {
                    if (scEl.ValueKind == JsonValueKind.Number && scEl.TryGetDouble(out var d) && double.IsFinite(d))
                    {
                        scaling = d;
                    }
                    else if (scEl.ValueKind == JsonValueKind.String && double.TryParse(scEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
                    {
                        scaling = parsed;
                    }
                }

                var isPrimary = false;
                var primaryPresent = ch.TryGetProperty("primary", out var primEl);
                var primaryType = primaryPresent ? JsonKind(primEl) : "missing";
                if (primaryPresent)
                {
                    isPrimary = IsPrimaryMarker(primEl);
                }

                evidence.Rows.Add(new PrtgChannelRowEntry
                {
                    Index = idx++,
                    ChannelIdType = ch.TryGetProperty("objid", out var channelId) ? JsonKind(channelId) : "missing",
                    SemanticName = semanticName,
                    SemanticKnown = isKnown,
                    ReturnedFields = returnedFields,
                    MissingFields = missingFields,
                    Unit = unit,
                    UnitType = unitType,
                    Scaling = scaling,
                    ScalingType = scalingType,
                    IsPrimary = isPrimary,
                    PrimaryType = primaryType
                });
            }

            evidence.FieldPresence = ChannelRequestedColumns.Select(field => new PrtgProbeFieldPresence(
                field, presentRowCounts[field], Math.Max(0, evidence.Rows.Count - presentRowCounts[field]))).ToList();

            // Channel objid identifies the channel itself. The response has no verified parent sensor field,
            // so filtered request scope remains partial even when every returned field is recognized.
            evidence.Status = evidence.Rows.Count == 0 ? "missing" : "partial";
            return evidence;
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!userToken.IsCancellationRequested && (probeToken.IsCancellationRequested || ex is TimeoutException or OperationCanceledException))
        {
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            evidence.Status = "timeout";
            evidence.Error = "Channels request timed out";
            console.WriteLine($"     ⚠ [{alias}] 頻道查詢逾時（30s）");
            return evidence;
        }
        catch (JsonException)
        {
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            evidence.Status = "malformed_json";
            evidence.Error = "Response is not valid JSON";
            console.WriteLine($"     ⚠ [{alias}] 頻道回應非合法 JSON：{evidence.Error}");
            return evidence;
        }
        catch (Exception ex)
        {
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            evidence.Status = "error";
            evidence.Error = FormatSafeError(ex);
            console.WriteLine($"     ⚠ [{alias}] 頻道查詢失敗：{evidence.Error}");
            return evidence;
        }
    }

    private static async Task<PrtgHistoryEvidence> ProbeHistoryAsync(
        Func<string, CancellationToken, Task<string>> getJson,
        long actualObjid,
        string alias,
        string sdate,
        string edate,
        IRunConsole console,
        CancellationToken probeToken,
        CancellationToken userToken)
    {
        var evidence = new PrtgHistoryEvidence();
        var url = $"/api/historicdata.json?id={actualObjid}&avg=3600&sdate={sdate}&edate={edate}&usecaption=1";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(probeToken);
            reqCts.CancelAfter(RequestTimeout);

            evidence.RequestedAtUtc = DateTime.UtcNow.ToString("o");
            var json = await getJson(url, reqCts.Token);
            evidence.ReceivedAtUtc = DateTime.UtcNow.ToString("o");
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("histdata", out var histArr) || histArr.ValueKind != JsonValueKind.Array)
            {
                evidence.Status = "malformed_json";
                evidence.Error = "Response does not contain a histdata array";
                return evidence;
            }

            var totalRows = histArr.GetArrayLength();
            evidence.TotalRows = totalRows;
            evidence.Truncated = totalRows > MaxHistoryRows;

            if (totalRows == 0)
            {
                evidence.Status = "missing";
                return evidence;
            }

            var rowIndex = 0;
            var processedFields = 0;
            foreach (var rowEl in histArr.EnumerateArray().Take(MaxHistoryRows))
            {
                if (rowEl.ValueKind != JsonValueKind.Object)
                {
                    evidence.NonObjectRowsCount++;
                    continue;
                }

                var rowEntry = new PrtgHistoryRowEntry { Index = rowIndex++ };

                // 依規格：以有序 entries 陣列保留重複欄位（如多個 value_raw），不折疊成字典
                // 同時嚴格過濾未知屬性名稱與非預期資料型別（防範密碼、主機名稱洩漏）
                foreach (var prop in rowEl.EnumerateObject())
                {
                    if (processedFields++ >= MaxResponseFields)
                    {
                        evidence.Truncated = true;
                        continue;
                    }
                    var colName = prop.Name.ToLowerInvariant();
                    var outputName = colName;
                    string? semanticLabel = null;
                    bool? semanticKnown = null;
                    var captionedField = !WhitelistHistoryColumns.Contains(colName);
                    if (captionedField && !TryMapCaptionedHistoryField(prop.Name, out outputName, out semanticLabel))
                    {
                        evidence.UnrecognizedFieldsCount++;
                        continue;
                    }
                    if (captionedField) semanticKnown = true;

                    if (colName == "datetime")
                    {
                        var rawStr = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                        var timestampCandidates = ParseTimestampCandidates(rawStr);
                        if (timestampCandidates.Count > 0)
                        {
                            var uniquePoint = timestampCandidates.Count == 1 && timestampCandidates[0].EndComponents == null;
                            var uniqueRange = timestampCandidates.Count == 1 && timestampCandidates[0].EndComponents != null;
                            rowEntry.Entries.Add(new PrtgHistoryFieldEntry
                            {
                                Name = "datetime",
                                Type = uniquePoint ? "datetime_string" : uniqueRange ? "datetime_range" : "datetime_candidates",
                                Value = uniquePoint ? timestampCandidates[0].StartComponents : uniqueRange ? "[range]" : "[ambiguous]",
                                TimestampComponents = uniquePoint ? timestampCandidates[0].StartComponents : null,
                                TimestampBasis = "unknown",
                                TimestampCandidates = timestampCandidates
                            });
                        }
                        else
                        {
                            rowEntry.Entries.Add(new PrtgHistoryFieldEntry
                            {
                                Name = "datetime",
                                Type = $"invalid_datetime_{JsonKind(prop.Value)}",
                                Value = "[redacted]"
                            });
                        }
                        continue;
                    }

                    if (colName == "datetime_raw" && TryGetOaTimestamp(prop.Value, out var oaTimestamp))
                    {
                        rowEntry.Entries.Add(new PrtgHistoryFieldEntry
                        {
                            Name = "datetime_raw",
                            Type = prop.Value.ValueKind == JsonValueKind.String ? "numeric_string" : "json_number",
                            Value = ReadFiniteNumber(prop.Value),
                            TimestampComponents = FormatTimestampComponents(oaTimestamp),
                            TimestampBasis = "unknown",
                            TimestampCandidates = new List<PrtgTimestampCandidate>
                            {
                                new() { Format = "prtg-raw-date-time", StartComponents = FormatTimestampComponents(oaTimestamp) }
                            }
                        });
                        continue;
                    }

                    // Uncaptioned history JSON repeats value/value_raw for every channel.
                    // Keep the ordered relationship but explicitly leave the channel identity unknown.
                    if (colName is "value" or "value_raw")
                    {
                        semanticLabel = "[unknown]";
                        semanticKnown = false;
                    }

                    // Values and coverage must be finite numbers; nested payloads and free text are rejected.
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (prop.Value.TryGetInt64(out var l))
                        {
                            rowEntry.Entries.Add(new PrtgHistoryFieldEntry { Name = outputName, Type = "json_number", Value = l, SemanticLabel = semanticLabel, SemanticKnown = semanticKnown });
                        }
                        else if (prop.Value.TryGetDouble(out var d) && double.IsFinite(d))
                        {
                            rowEntry.Entries.Add(new PrtgHistoryFieldEntry { Name = outputName, Type = "json_number", Value = d, SemanticLabel = semanticLabel, SemanticKnown = semanticKnown });
                        }
                        else
                        {
                            rowEntry.Entries.Add(new PrtgHistoryFieldEntry { Name = outputName, Type = "invalid_number", Value = null, SemanticLabel = semanticLabel, SemanticKnown = semanticKnown });
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String &&
                             double.TryParse(prop.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNum) && double.IsFinite(parsedNum))
                    {
                        rowEntry.Entries.Add(new PrtgHistoryFieldEntry { Name = outputName, Type = "numeric_string", Value = parsedNum, SemanticLabel = semanticLabel, SemanticKnown = semanticKnown });
                    }
                    else
                    {
                        // 包含嵌套 array/object 或任意非數值字串：絕不遞迴輸出內容
                        rowEntry.Entries.Add(new PrtgHistoryFieldEntry { Name = outputName, Type = "invalid_number", Value = null, SemanticLabel = semanticLabel, SemanticKnown = semanticKnown });
                    }
                }

                evidence.Rows.Add(rowEntry);
            }

            evidence.Status = evidence.Truncated || evidence.UnrecognizedFieldsCount > 0 || evidence.NonObjectRowsCount > 0 ||
                evidence.Rows.Any(r => r.Entries.Any(e => e.Type.StartsWith("invalid_", StringComparison.Ordinal)))
                ? "partial" : "ok";
            return evidence;
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!userToken.IsCancellationRequested && (probeToken.IsCancellationRequested || ex is TimeoutException or OperationCanceledException))
        {
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            evidence.Status = "timeout";
            evidence.Error = "History request timed out";
            console.WriteLine($"     ⚠ [{alias}] 歷史數值查詢逾時（30s）");
            return evidence;
        }
        catch (JsonException)
        {
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            evidence.Status = "malformed_json";
            evidence.Error = "Response is not valid JSON";
            console.WriteLine($"     ⚠ [{alias}] 歷史數值回應非合法 JSON：{evidence.Error}");
            return evidence;
        }
        catch (Exception ex)
        {
            sw.Stop();
            evidence.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            evidence.Status = "error";
            evidence.Error = FormatSafeError(ex);
            console.WriteLine($"     ⚠ [{alias}] 歷史數值查詢失敗：{evidence.Error}");
            return evidence;
        }
    }

    private static string FormatSafeError(Exception ex)
    {
        if (ex is LogForesight.Core.PrtgClientException clientEx)
        {
            // Preserve useful API failure classes without forwarding arbitrary server text,
            // which can contain object names or other private source data.
            var statusMatch = Regex.Match(clientEx.Message, @"\bHTTP\s+([1-5][0-9]{2})\b", RegexOptions.CultureInvariant);
            if (statusMatch.Success) return $"HTTP {statusMatch.Groups[1].Value}";
            if (clientEx.Message.Contains("HTML", StringComparison.OrdinalIgnoreCase)) return "PRTG returned HTML";
            if (clientEx.Message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                clientEx.Message.Contains("憑證", StringComparison.Ordinal)) return "PRTG authorization failed";
            return "PRTG client request failed";
        }
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode.HasValue)
            {
                return $"HTTP {(int)httpEx.StatusCode.Value} {httpEx.StatusCode.Value}";
            }
            return "HTTP request failed";
        }
        if (ex is TimeoutException or OperationCanceledException)
        {
            return "Request timed out after 30s";
        }
        if (ex is JsonException)
        {
            return "Response is not valid JSON";
        }
        // Exception type is a bounded diagnostic that distinguishes parser/API-path
        // failures in runner evidence without copying exception text from the source.
        return $"Request execution error ({ex.GetType().Name})";
    }

    private static string JsonKind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "other"
    };

    private static string? SanitizePrtgVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion)) return null;
        var trimmed = rawVersion.Trim();
        if (trimmed.Length <= 32 && Regex.IsMatch(trimmed, @"\A[0-9]+(?:\.[0-9]+){1,3}\+?\z", RegexOptions.CultureInvariant))
        {
            return trimmed;
        }
        return "unknown";
    }

    private sealed record ParsedTimestampPart(string Format, string Components, string? ReportedOffset);

    private static List<PrtgTimestampCandidate> ParseTimestampCandidates(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxStringLength) return new();

        // Only the exact PRTG-style "date range" delimiter is recognized. Both sides
        // must match bounded numeric date/time grammars; free text and markup stay redacted.
        var rangeParts = raw.Split(" - ", StringSplitOptions.None);
        if (rangeParts.Length == 2)
        {
            var starts = ParseStructuredDateTime(rangeParts[0]);
            if (starts.Count == 0) return new();

            var ends = ParseStructuredDateTime(rangeParts[1]);
            if (ends.Count == 0) ends = ParseStructuredTime(rangeParts[1]);
            if (ends.Count == 0) return new();

            var candidates = new List<PrtgTimestampCandidate>();
            foreach (var start in starts)
            foreach (var end in ends)
            {
                candidates.Add(new PrtgTimestampCandidate
                {
                    Format = $"{start.Format}-range-{end.Format}",
                    StartComponents = start.Components,
                    EndComponents = end.Components,
                    ReportedOffset = start.ReportedOffset,
                    EndReportedOffset = end.ReportedOffset
                });
                if (candidates.Count >= 4) return candidates;
            }
            return candidates;
        }

        return ParseStructuredDateTime(raw)
            .Take(4)
            .Select(candidate => new PrtgTimestampCandidate
            {
                Format = candidate.Format,
                StartComponents = candidate.Components,
                ReportedOffset = candidate.ReportedOffset
            })
            .ToList();
    }

    private static List<ParsedTimestampPart> ParseStructuredDateTime(string raw)
    {
        var candidates = new List<ParsedTimestampPart>();

        // Parse explicit UTC/offset forms as DateTimeOffset, then retain their original
        // wall-clock components. Never convert the clock through the app host's timezone.
        var offsetFormats = new[]
        {
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd HH:mm:sszzz", "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz"
        };
        foreach (var format in offsetFormats)
        {
            if (DateTimeOffset.TryParseExact(raw, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var withOffset))
            {
                candidates.Add(new ParsedTimestampPart("iso-offset", FormatTimestampComponents(withOffset.DateTime),
                    FormatUtcOffset(withOffset.Offset)));
                return candidates;
            }
        }

        var utcFormats = new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" };
        foreach (var format in utcFormats)
        {
            if (DateTimeOffset.TryParseExact(raw, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var utc))
            {
                candidates.Add(new ParsedTimestampPart("iso-utc", FormatTimestampComponents(utc.DateTime), "+00:00"));
                return candidates;
            }
        }

        AddDateCandidates(raw, candidates, CultureInfo.InvariantCulture, "iso-ymd",
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF");
        AddDateCandidates(raw, candidates, CultureInfo.InvariantCulture, "ymd-slash",
            "yyyy/M/d H:mm:ss", "yyyy/M/d HH:mm:ss", "yyyy/M/d h:mm:ss tt");

        // Slash dates can be either month/day or day/month. Keep every valid reading
        // with its format label; do not infer a preferred ordering from OA values.
        AddDateCandidates(raw, candidates, CultureInfo.InvariantCulture, "mdy",
            "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt", "M/d/yyyy tt h:mm:ss");
        AddDateCandidates(raw, candidates, CultureInfo.InvariantCulture, "dmy",
            "d/M/yyyy H:mm:ss", "d/M/yyyy h:mm:ss tt", "d/M/yyyy tt h:mm:ss");
        AddDateCandidates(raw, candidates, CultureInfo.InvariantCulture, "dmy-dot",
            "d.M.yyyy H:mm:ss", "d.M.yyyy h:mm:ss tt", "d.M.yyyy tt h:mm:ss");

        var chineseClock = (DateTimeFormatInfo)CultureInfo.InvariantCulture.DateTimeFormat.Clone();
        chineseClock.AMDesignator = "上午";
        chineseClock.PMDesignator = "下午";
        AddDateCandidates(raw, candidates, chineseClock, "mdy-zh-ampm",
            "M/d/yyyy h:mm:ss tt", "M/d/yyyy tt h:mm:ss");
        AddDateCandidates(raw, candidates, chineseClock, "dmy-zh-ampm",
            "d/M/yyyy h:mm:ss tt", "d/M/yyyy tt h:mm:ss");
        AddDateCandidates(raw, candidates, chineseClock, "ymd-zh-ampm",
            "yyyy/M/d h:mm:ss tt", "yyyy/M/d tt h:mm:ss");

        return DeduplicateTimestampParts(candidates);
    }

    private static List<ParsedTimestampPart> ParseStructuredTime(string raw)
    {
        var candidates = new List<ParsedTimestampPart>();
        AddTimeCandidates(raw, candidates, CultureInfo.InvariantCulture, "time-24h", "H:mm:ss", "HH:mm:ss");
        AddTimeCandidates(raw, candidates, CultureInfo.InvariantCulture, "time-12h", "h:mm:ss tt", "tt h:mm:ss");
        var chineseClock = (DateTimeFormatInfo)CultureInfo.InvariantCulture.DateTimeFormat.Clone();
        chineseClock.AMDesignator = "上午";
        chineseClock.PMDesignator = "下午";
        AddTimeCandidates(raw, candidates, chineseClock, "time-zh-ampm", "h:mm:ss tt", "tt h:mm:ss");
        return DeduplicateTimestampParts(candidates);
    }

    private static void AddDateCandidates(string raw, List<ParsedTimestampPart> candidates,
        IFormatProvider formatProvider, string label, params string[] formats)
    {
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(raw, format, formatProvider, DateTimeStyles.None, out var parsed))
                candidates.Add(new ParsedTimestampPart(label, FormatTimestampComponents(parsed), null));
        }
    }

    private static void AddTimeCandidates(string raw, List<ParsedTimestampPart> candidates,
        IFormatProvider formatProvider, string label, params string[] formats)
    {
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(raw, format, formatProvider, DateTimeStyles.None, out var parsed))
                candidates.Add(new ParsedTimestampPart(label, parsed.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), null));
        }
    }

    private static List<ParsedTimestampPart> DeduplicateTimestampParts(IEnumerable<ParsedTimestampPart> candidates) =>
        candidates.GroupBy(candidate => (candidate.Components, candidate.ReportedOffset))
            .Select(group => group.First() with { Format = string.Join("/", group.Select(candidate => candidate.Format).Distinct(StringComparer.Ordinal)) })
            .Take(4)
            .ToList();

    private static bool TryGetOaTimestamp(JsonElement value, out DateTime timestamp)
    {
        timestamp = default;
        double number = 0;
        var isNumber = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number);
        if (!isNumber && value.ValueKind == JsonValueKind.String)
        {
            isNumber = double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }
        if (!isNumber || !double.IsFinite(number) || number < 0 || number > 100000) return false;

        try
        {
            timestamp = DateTime.FromOADate(number);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static object? ReadFiniteNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var integer)) return integer;
            return value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
        }
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed)) return parsed;
        return null;
    }

    private static string FormatTimestampComponents(DateTime timestamp) =>
        timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string FormatUtcOffset(TimeSpan offset)
    {
        var totalMinutes = (int)offset.TotalMinutes;
        var absoluteMinutes = Math.Abs(totalMinutes);
        var sign = totalMinutes < 0 ? "-" : "+";
        return string.Format(CultureInfo.InvariantCulture, "{0}{1:00}:{2:00}",
            sign, absoluteMinutes / 60, absoluteMinutes % 60);
    }

    private static bool TryMapCaptionedHistoryField(string rawName, out string fieldName, out string semanticLabel)
    {
        fieldName = string.Empty;
        semanticLabel = "[redacted]";
        var isRaw = rawName.EndsWith("_raw", StringComparison.OrdinalIgnoreCase);
        var caption = isRaw ? rawName[..^4] : rawName;
        if (string.IsNullOrWhiteSpace(caption) || caption.Length > MaxStringLength) return false;

        var (safeName, known) = SanitizeChannelName(caption);
        if (!known) return false;

        // Captioned history fields carry the semantic label in their property name.
        // Keep raw versus formatted values distinct without retaining the source caption.
        fieldName = isRaw ? "channel_value_raw" : "channel_value";
        semanticLabel = safeName;
        return true;
    }

    private static string SafeBuildVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        Regex.IsMatch(value.Trim(), @"^\d+\.\d+(?:\.\d+){0,2}(?:\+[a-fA-F0-9]{7,40})?$", RegexOptions.CultureInvariant)
            ? value.Trim()
            : "unknown";

    private static string SafeUtcOffset(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), @"^[+-](?:0[0-9]|1[0-4]):[0-5][0-9]$", RegexOptions.CultureInvariant)
            ? value.Trim()
            : DateTimeOffset.Now.ToString("zzz", CultureInfo.InvariantCulture);

    private static string? SafeSettingsRevision(string? value) =>
        Guid.TryParseExact(value, "N", out var revision) ? revision.ToString("N") : null;

    private static string SafeFingerprint(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)
            ? value.ToLowerInvariant()
            : "unknown";

    private static string SafeProvider(string? value) => value?.Trim() switch
    {
        var provider when string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(provider, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.OrdinalIgnoreCase) => "SqlServer",
        var provider when string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(provider, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase) => "Sqlite",
        _ => "unknown"
    };

    private static string? SafeEfProvider(string? value) => value?.Trim() switch
    {
        var provider when string.Equals(provider, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.OrdinalIgnoreCase) => "Microsoft.EntityFrameworkCore.SqlServer",
        var provider when string.Equals(provider, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase) => "Microsoft.EntityFrameworkCore.Sqlite",
        _ => null
    };

    private static PrtgStorageEnvironmentFacts SanitizeStorageEnvironment(PrtgStorageEnvironmentFacts? value)
    {
        if (value is null) return new PrtgStorageEnvironmentFacts();
        var provider = SafeProvider(value.Provider);
        var status = value.Status is "measured" or "partial" or "timeout" or "cancelled" ? value.Status : "unknown";
        var sourceFiles = value.FileCapacities ?? Array.Empty<PrtgStorageCapacityRow>();
        var sourceVolumes = value.VolumeCapacities ?? Array.Empty<PrtgStorageVolumeRow>();
        var files = sourceFiles.Take(PrtgStorageEnvironmentProbe.MaximumRowsPerKind)
            .Select(row => new PrtgStorageCapacityRow(
                row.Role is "database-pages" or "database-file" or "write-ahead-log" or "shared-memory" or "data" or "log" ? row.Role : "unknown",
                NonNegative(row.AllocatedBytes), NonNegative(row.UsedBytes), NonNegative(row.MaximumBytes),
                row.MaximumKind is "bounded" or "unbounded" or "fixed-at-current" ? row.MaximumKind : "unknown",
                NonNegative(row.GrowthBytes), row.GrowthPercent is >= 0 and <= 100 ? row.GrowthPercent : null)).ToArray();
        var volumes = sourceVolumes.Take(PrtgStorageEnvironmentProbe.MaximumRowsPerKind)
            .Select(row => new PrtgStorageVolumeRow(
                row.Role is "owned-data-root" or "data" or "log" ? row.Role : "unknown",
                NonNegative(row.TotalBytes), NonNegative(row.AvailableBytes))).ToArray();
        return new PrtgStorageEnvironmentFacts
        {
            Status = status,
            Provider = provider,
            EngineVersion = System.Text.RegularExpressions.Regex.IsMatch(value.EngineVersion ?? string.Empty,
                @"^\d{1,3}(?:\.\d{1,5}){1,3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                ? value.EngineVersion! : "unknown",
            Edition = value.Edition is "Enterprise" or "Standard" or "Developer" or "Express" or "Web" or "Evaluation" or "Azure" or "unknown"
                ? value.Edition : "unknown",
            EngineEdition = value.EngineEdition is "personal-or-desktop" or "standard" or "enterprise" or "express" or
                "azure-sql-database" or "azure-synapse" or "azure-sql-managed-instance" or "unknown"
                ? value.EngineEdition : "unknown",
            FileStatus = value.FileStatus is "measured" or "partial" or "unknown" ? value.FileStatus : "unknown",
            VolumeStatus = value.VolumeStatus is "measured" or "partial" or "unknown" ? value.VolumeStatus : "unknown",
            LocalFileStatus = value.LocalFileStatus is "measured" or "partial" or "unknown" ? value.LocalFileStatus : "unknown",
            LogStatus = value.LogStatus is "measured" or "partial" or "unknown" ? value.LogStatus : "unknown",
            DatabaseLogAggregate = value.DatabaseLogAggregate is { } log
                ? new PrtgStorageLogAggregate(NonNegative(log.AllocatedBytes), NonNegative(log.UsedBytes))
                : null,
            QueriesAttempted = Math.Clamp(value.QueriesAttempted, 0, PrtgStorageEnvironmentProbe.MaximumMetadataQueries),
            QueriesSucceeded = Math.Clamp(value.QueriesSucceeded, 0, PrtgStorageEnvironmentProbe.MaximumMetadataQueries),
            TimedOut = value.TimedOut,
            Cancelled = value.Cancelled,
            FileRowsTruncated = value.FileRowsTruncated || sourceFiles.Count > PrtgStorageEnvironmentProbe.MaximumRowsPerKind,
            VolumeRowsTruncated = value.VolumeRowsTruncated || sourceVolumes.Count > PrtgStorageEnvironmentProbe.MaximumRowsPerKind,
            FileCapacities = files,
            VolumeCapacities = volumes
        };
    }

    private static long? NonNegative(long? value) => value is >= 0 ? value : null;


    /// <summary>
    /// 嚴格頻道語意名稱白名單檢查：若含有非白名單識別字（如主機名、IP、自訂字元等），
    /// 一律回傳 [redacted] 且標示 semanticKnown=false。
    /// </summary>
    public static (string SemanticName, bool SemanticKnown) SanitizeChannelName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return ("[none]", false);
        }
        if (rawName.Length > MaxStringLength)
        {
            return ("[redacted]", false);
        }

        var cleaned = rawName.Trim();
        if (cleaned.Contains(BeginMarker, StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains(EndMarker, StringComparison.OrdinalIgnoreCase))
        {
            return ("[marker_redacted]", false);
        }

        // 去除 IP 與機敏參數
        if (IpRegex.IsMatch(cleaned) || UrlSecrets.ContainsSecretQuery(cleaned))
        {
            return ("[redacted]", false);
        }

        // 以標點、空白分割 token
        var tokens = cleaned
            .Split(new[] { ' ', '\t', '-', '_', '/', '\\', '(', ')', '[', ']', ':', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return ("[none]", false);
        }

        var result = string.Join(" ", tokens.Select(token => token.Trim().ToLowerInvariant()));
        if (!AllowedSemanticLabels.Contains(result))
        {
            // Require a recognized complete label, not merely a collection of generic words.
            return ("[redacted]", false);
        }
        return (result.Length > MaxStringLength ? result[..MaxStringLength] : result, true);
    }

    public static string? SanitizeText(string? text, int maxLength = MaxStringLength)
    {
        if (text == null) return null;

        var sanitized = text
            .Replace(BeginMarker, "[marker_redacted]")
            .Replace(EndMarker, "[marker_redacted]");

        sanitized = UrlSecrets.Mask(sanitized);
        sanitized = IpRegex.Replace(sanitized, "[redacted_ip]");

        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength];
        }

        return sanitized;
    }

    private static bool IsPrimaryMarker(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetInt32(out var number) && number == 1,
        JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() is "1" or "true",
        _ => false
    };

    private static string? GetStringProperty(JsonElement element, string propName)
    {
        if (element.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.String) return p.GetString();
            if (p.ValueKind != JsonValueKind.Null && p.ValueKind != JsonValueKind.Undefined) return p.GetRawText();
        }
        return null;
    }

    public static string SerializeEvidence(PrtgCompatibilityProbeEvidence evidence)
    {
        var json = JsonSerializer.Serialize(evidence, JsonOptions);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount <= MaxJsonSizeBytes)
        {
            return json;
        }

        evidence.Status = "truncated";
        evidence.EvidenceReady = false;

        // 第 1 級截斷：保留 1 筆歷史與 4 筆頻道
        foreach (var t in evidence.Targets)
        {
            if (t.RawChannelIdentity is { } raw)
                raw.Samples = raw.Samples.Take(1).Select(s => s with { Channels = s.Channels.Take(4).ToArray() }).ToList();
            if (t.History?.Rows != null && t.History.Rows.Count > 1)
            {
                t.History.Rows = t.History.Rows.Take(1).ToList();
                t.History.Truncated = true;
            }
            if (t.Channels?.Rows != null && t.Channels.Rows.Count > 4)
            {
                t.Channels.Rows = t.Channels.Rows.Take(4).ToList();
                t.Channels.Truncated = true;
            }
        }

        json = JsonSerializer.Serialize(evidence, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) <= MaxJsonSizeBytes)
        {
            return json;
        }

        // 第 2 級截斷：清空歷史與頻道列，保留快照重要欄位
        foreach (var t in evidence.Targets)
        {
            t.RawChannelIdentity?.Samples.Clear();
            if (t.History?.Rows != null && t.History.Rows.Count > 0)
            {
                t.History.Rows.Clear();
                t.History.Truncated = true;
            }
            if (t.Channels?.Rows != null && t.Channels.Rows.Count > 0)
            {
                t.Channels.Rows.Clear();
                t.Channels.Truncated = true;
            }
            if (t.Snapshot?.Fields != null && t.Snapshot.Fields.Count > 8)
            {
                t.Snapshot.Fields = t.Snapshot.Fields.Take(8).ToList();
                t.Snapshot.Truncated = true;
            }
        }

        json = JsonSerializer.Serialize(evidence, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) <= MaxJsonSizeBytes)
        {
            return json;
        }

        // 第 3 級截斷：清空 targets 詳細欄位
        foreach (var t in evidence.Targets)
        {
            t.Snapshot = null;
            t.Channels = null;
            t.History = null;
            t.Reason = "Truncated to 64KiB cap";
        }

        json = JsonSerializer.Serialize(evidence, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) <= MaxJsonSizeBytes)
        {
            return json;
        }

        // 第 4 級截斷：保證在病態或超大 metadata 情境下產出有效且 <= 64KiB 的 fallback JSON
        var fallback = new
        {
            schema_version = "1.0.0",
            status = "truncated",
            reason = "Evidence payload exceeded 64KiB cap even after progressive truncation",
            evidence_ready = false,
            original_byte_count = Encoding.UTF8.GetByteCount(json),
            summary = evidence.Summary,
            metadata = new
            {
                build_version = SafeBuildVersion(evidence.BuildVersion),
                source_fingerprint = SafeFingerprint(evidence.SourceFingerprint),
                source_prtg_version = SanitizePrtgVersion(evidence.SourcePrtgVersion) ?? "unknown",
                storage_provider = SafeProvider(evidence.StorageProvider)
            },
            targets_count = evidence.Targets.Count,
            targets = Array.Empty<object>()
        };

        var fallbackJson = JsonSerializer.Serialize(fallback, JsonOptions);
        var fallbackBytes = Encoding.UTF8.GetByteCount(fallbackJson);
        if (fallbackBytes <= MaxJsonSizeBytes)
        {
            return fallbackJson;
        }

        // 極限保障：最小合法 JSON
        return "{\"schema_version\":\"1.0.0\",\"status\":\"truncated\",\"reason\":\"Payload exceeded 64KiB cap\",\"evidence_ready\":false,\"targets\":[]}";
    }
}
