using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    public string? ScopeSummary { get; init; }
    public string? ReadinessSummary { get; init; }
}

public sealed class PrtgCompatibilityProbeEvidence
{
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
}

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
}

public sealed class PrtgChannelsEvidence
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("parent_identity")]
    public string ParentIdentity { get; set; } = "unknown (filtered request; channel objid identifies a channel, response parent sensor id is unavailable)";

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

    public static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static readonly IReadOnlyList<(string Category, string SensorType, string Alias)> TargetCategories = new[]
    {
        ("cpu", "SNMP CPU Load", "s1"),
        ("memory", "SNMP Memory", "s2"),
        ("disk", "SNMP Disk Free", "s3")
    };

    private static readonly HashSet<string> AllowedSemanticWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "free", "used", "available", "total", "cpu", "memory", "disk",
        "可用", "已用", "剩餘", "總計", "記憶體", "磁碟"
    };

    private static readonly HashSet<string> WhitelistSnapshotColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "objid", "type", "status", "status_raw", "lastvalue_raw", "lastcheck", "lastcheck_raw", "interval", "interval_raw"
    };

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
            (url, token) => client.GetJsonAsync(url, token),
            console,
            sensorSamples,
            context,
            ct,
            onEvidenceJsonProduced);
    }

    public static async Task<PrtgCompatibilityProbeEvidence> ExecuteCoreAsync(
        Func<string, CancellationToken, Task<string>> getJson,
        IRunConsole console,
        IReadOnlyList<PrtgProbeRunner.SensorTypeSample> sensorSamples,
        PrtgProbeEvidenceContext? context,
        CancellationToken ct = default,
        Action<string>? onEvidenceJsonProduced = null)
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
                Limitations = "來源時區未確認"
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

            // 判定該 target 的整體狀態
            if (isPaused)
            {
                target.Status = "paused";
                target.Reason = "Sensor is in paused state; not evaluated for readiness";
            }
            else if (target.Snapshot.Status == "ok" && target.Channels.Status == "ok" && target.History.Status == "ok")
            {
                target.Status = "ok";
            }
            else if (target.Snapshot.Status is "error" or "malformed_json" or "timeout" ||
                     target.Channels.Status is "error" or "malformed_json" or "timeout" ||
                     target.History.Status is "error" or "malformed_json" or "timeout")
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

    private static async Task<PrtgSnapshotEvidence> ProbeSnapshotAsync(
        Func<string, CancellationToken, Task<string>> getJson,
        long actualObjid,
        string alias,
        IRunConsole console,
        CancellationToken probeToken,
        CancellationToken userToken)
    {
        var evidence = new PrtgSnapshotEvidence();
        var url = $"/api/table.json?content=sensors&columns=objid,type,status,status_raw,lastvalue_raw,lastcheck,lastcheck_raw,interval,interval_raw&filter_objid={actualObjid}";
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
                evidence.ReturnedFields.Add(colName);

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
                    if (!string.IsNullOrWhiteSpace(rawStr) && rawStr.Length <= MaxStringLength &&
                        (DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ||
                         DateTime.TryParse(rawStr, out dt)))
                    {
                        evidence.Fields.Add(new PrtgSnapshotFieldEntry { Field = colName, ValueType = "datetime_string", Value = dt.ToString("o") });
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

            foreach (var col in WhitelistSnapshotColumns)
            {
                if (!returnedProps.Contains(col))
                {
                    evidence.MissingFields.Add(col);
                }
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
        var evidence = new PrtgChannelsEvidence();
        var url = $"/api/table.json?content=channels&id={actualObjid}&columns=objid,channel,unit,scaling,primary&count={MaxChannelRows}";
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
                    if (!new[] { "objid", "channel", "unit", "scaling", "primary" }.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                        evidence.UnrecognizedFieldsCount++;
                }

                var rawName = GetStringProperty(ch, "channel");
                var (semanticName, isKnown) = SanitizeChannelName(rawName);

                var returnedFields = new List<string>();
                foreach (var field in new[] { "objid", "channel", "unit", "scaling", "primary" })
                {
                    if (ch.TryGetProperty(field, out _)) returnedFields.Add(field);
                }
                var missingFields = new[] { "objid", "channel", "unit", "scaling", "primary" }
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
        var url = $"/api/historicdata.json?id={actualObjid}&avg=3600&sdate={sdate}&edate={edate}";
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
                    if (!WhitelistHistoryColumns.Contains(colName))
                    {
                        evidence.UnrecognizedFieldsCount++;
                        continue;
                    }

                    if (colName == "datetime")
                    {
                        var rawStr = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(rawStr) && rawStr.Length <= MaxStringLength &&
                            (DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ||
                             DateTime.TryParse(rawStr, out dt)))
                        {
                            rowEntry.Entries.Add(new PrtgHistoryFieldEntry
                            {
                                Name = "datetime",
                                Type = "datetime_string",
                                Value = dt.ToString("o")
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

                    // datetime_raw, value, value_raw, coverage, coverage_raw 必須為數值
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (prop.Value.TryGetInt64(out var l))
                        {
                            rowEntry.Entries.Add(new PrtgHistoryFieldEntry { Name = colName, Type = "json_number", Value = l });
                        }
                        else if (prop.Value.TryGetDouble(out var d) && double.IsFinite(d))
                        {
                            rowEntry.Entries.Add(new PrtgHistoryFieldEntry { Name = colName, Type = "json_number", Value = d });
                        }
                        else
                        {
                            rowEntry.Entries.Add(new PrtgHistoryFieldEntry { Name = colName, Type = "invalid_number", Value = null });
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String &&
                             double.TryParse(prop.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNum) && double.IsFinite(parsedNum))
                    {
                        rowEntry.Entries.Add(new PrtgHistoryFieldEntry { Name = colName, Type = "numeric_string", Value = parsedNum });
                    }
                    else
                    {
                        // 包含嵌套 array/object 或任意非數值字串：絕不遞迴輸出內容
                        rowEntry.Entries.Add(new PrtgHistoryFieldEntry { Name = colName, Type = "invalid_number", Value = null });
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
        return "Request execution error";
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
        if (trimmed.Length <= 32 && Regex.IsMatch(trimmed, @"^\d+(?:\.\d+){1,3}$", RegexOptions.CultureInvariant))
        {
            return trimmed;
        }
        return "unknown";
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

        var matchedWords = new List<string>();
        foreach (var token in tokens)
        {
            var t = token.Trim().ToLowerInvariant();
            if (AllowedSemanticWords.Contains(t))
            {
                matchedWords.Add(t);
            }
            else
            {
                // 出現非白名單 token（可能帶有主機名稱或業務標識）：整串視為 unknown / redacted
                return ("[redacted]", false);
            }
        }

        var result = string.Join(" ", matchedWords);
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
