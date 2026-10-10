using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogForesight.Core.Service;

/// <summary>Deidentified observation of the exact legacy two-to-five-ID profile query; never grants risk authority.</summary>
public sealed class PrtgSensorBatchCapability
{
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("requested_aliases")] public string[] RequestedAliases { get; set; } = [];
    [JsonPropertyName("returned_aliases")] public string[] ReturnedAliases { get; set; } = [];
    [JsonPropertyName("exact_requested_set")] public bool ExactRequestedSet { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "not-observed";
    [JsonPropertyName("requested_at_utc")] public string? RequestedAtUtc { get; set; }
    [JsonPropertyName("received_at_utc")] public string? ReceivedAtUtc { get; set; }
    [JsonPropertyName("elapsed_ms")] public long? ElapsedMilliseconds { get; set; }
    [JsonPropertyName("authorizes_formal_profile")] public bool AuthorizesFormalProfile => false;

    public static PrtgSensorBatchCapability Parse(string json, IReadOnlyList<long> requestedIds)
    {
        if (requestedIds.Count is < 2 or > 5 || requestedIds.Any(id => id <= 0) || requestedIds.Distinct().Count() != requestedIds.Count)
            throw new ArgumentException("Batch capability requires two to five distinct positive sensor IDs.");
        var aliases = requestedIds.Select((id, index) => (id, alias: $"b{index + 1}")).ToDictionary(pair => pair.id, pair => pair.alias);
        var result = new PrtgSensorBatchCapability { RequestedAliases = aliases.Values.ToArray(), Status = "partial" };
        if (Encoding.UTF8.GetByteCount(json) > 512 * 1024) { result.Reason = "response-byte-limit"; return result; }
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                doc.RootElement.EnumerateObject().Count(p => string.Equals(p.Name, "sensors", StringComparison.OrdinalIgnoreCase)) != 1 ||
                !doc.RootElement.TryGetProperty("sensors", out var rows) || rows.ValueKind != JsonValueKind.Array)
            { result.Reason = "sensor-table-missing-or-ambiguous"; return result; }
            if (rows.GetArrayLength() > requestedIds.Count + 1) { result.Reason = "response-row-limit"; return result; }
            var seen = new HashSet<long>(); var returned = new List<string>(); var foreign = new Dictionary<long, string>(); var invalid = false;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count(p => string.Equals(p.Name, "objid", StringComparison.OrdinalIgnoreCase)) != 1 ||
                    !row.TryGetProperty("objid", out var value) || !TryId(value, out var id))
                { returned.Add("invalid"); invalid = true; continue; }
                if (!seen.Add(id)) invalid = true;
                if (aliases.TryGetValue(id, out var alias)) returned.Add(alias);
                else { if (!foreign.TryGetValue(id, out alias)) foreign[id] = alias = $"foreign{foreign.Count + 1}"; returned.Add(alias); }
            }
            result.ReturnedAliases = returned.ToArray();
            result.ExactRequestedSet = !invalid && rows.GetArrayLength() == requestedIds.Count && seen.SetEquals(requestedIds);
            result.Status = result.ExactRequestedSet ? "ok" : "partial";
            result.Reason = result.ExactRequestedSet ? "exact-unique-requested-set" : "missing-foreign-duplicate-or-invalid-sensor";
        }
        catch (JsonException) { result.Reason = "malformed-json"; }
        return result;
    }

    private static bool TryId(JsonElement value, out long id)
    {
        id = 0;
        return (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out id) ||
            value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out id)) && id > 0;
    }
}

/// <summary>Separate observation of the runtime-matching 100-ID snapshot query; never grants capacity or risk authority.</summary>
public sealed class PrtgSnapshotBatch100Capability
{
    public const int MaximumBatchSize = 100;
    public const int MaximumResponseBytes = 512 * 1024;
    public const int MaximumRelativeUrlBytes = 4096;
    public const int MaximumDepth = 32;
    public const string SnapshotColumns = "objid,lastvalue,interval,lastcheck,status,primarychannel";
    public const string RequestShapeVersion = "snapshot-filter-batch100-v1";
    public const string SelectionScope = "existing-bounded-step3-sample";

    [JsonPropertyName("observation_schema_version")] public string ObservationSchemaVersion { get; set; } = "2.0.0";
    [JsonPropertyName("request_shape_version")] public string RequestShapeVersionValue { get; set; } = RequestShapeVersion;
    [JsonPropertyName("runtime_request_contract_version")] public string RuntimeRequestContractVersion => RequestShapeVersion;
    [JsonPropertyName("selection_scope")] public string SelectionScopeValue { get; set; } = SelectionScope;
    [JsonPropertyName("request_method")] public string RequestMethod => "GET";
    [JsonPropertyName("native_columns")] public string NativeColumns => SnapshotColumns;
    [JsonPropertyName("filter_mode")] public string FilterMode => "sorted-repeated-filter_objid";
    [JsonPropertyName("request_count_parameter")] public int? RequestCountParameter => RequestedCount >= 1 ? RequestedCount + 1 : null;
    [JsonPropertyName("sentinel_rows")] public int SentinelRows => 1;
    [JsonPropertyName("uses_caption")] public bool UsesCaption => false;
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("requested_aliases")] public string[] RequestedAliases { get; set; } = [];
    [JsonPropertyName("returned_aliases")] public string[] ReturnedAliases { get; set; } = [];
    [JsonPropertyName("requested_count")] public int RequestedCount { get; set; }
    [JsonPropertyName("responded_count")] public int? RespondedCount { get; set; }
    [JsonPropertyName("exact_requested_set")] public bool ExactRequestedSet { get; set; }
    [JsonPropertyName("runtime_response_compatible")] public bool RuntimeResponseCompatible { get; set; }
    [JsonPropertyName("minimum_fields_observed")] public bool MinimumFieldsObserved { get; set; }
    [JsonPropertyName("runtime_response_reason")] public string RuntimeResponseReason { get; set; } = "not-observed";
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("response_bytes")] public int? ResponseBytes { get; set; }
    [JsonPropertyName("request_url_bytes")] public int? RequestUrlBytes { get; set; }
    [JsonPropertyName("maximum_response_bytes")] public int MaximumResponseByteLimit => MaximumResponseBytes;
    [JsonPropertyName("maximum_relative_url_bytes")] public int MaximumRelativeUrlByteLimit => MaximumRelativeUrlBytes;
    [JsonPropertyName("maximum_json_depth")] public int MaximumJsonDepth => MaximumDepth;
    [JsonPropertyName("shape_fingerprint")] public string ShapeFingerprint { get; set; } = "";
    [JsonPropertyName("full_batch100_observed")] public bool FullBatch100Observed =>
        RequestedCount == MaximumBatchSize && ExactRequestedSet && RuntimeResponseCompatible && MinimumFieldsObserved;
    [JsonPropertyName("profile_authorized")] public bool ProfileAuthorized => false;
    [JsonPropertyName("capacity_accepted")] public bool CapacityAccepted => false;
    // Legacy wire name retained for evidence consumers from the prior five-ID probe.
    [JsonPropertyName("authorizes_formal_profile")] public bool AuthorizesFormalProfile => false;
    [JsonPropertyName("reason")] public string Reason { get; set; } = "not-observed";
    [JsonPropertyName("requested_at_utc")] public string? RequestedAtUtc { get; set; }
    [JsonPropertyName("received_at_utc")] public string? ReceivedAtUtc { get; set; }
    [JsonPropertyName("elapsed_ms")] public long? ElapsedMilliseconds { get; set; }

    public static string ComputeShapeFingerprint(int batchSize)
    {
        if (batchSize is < 1 or > MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var material = $"{RequestShapeVersion}\nGET\n/api/table.json?content=sensors&columns={SnapshotColumns}" +
            $"&filter_objid={{{batchSize.ToString(CultureInfo.InvariantCulture)} IDs}}&count={batchSize + 1}" +
            $"\nfilter=sorted-repeated-filter_objid\nsentinel=1\nusecaption=false" +
            $"\nurl-utf8<={MaximumRelativeUrlBytes}\nresponse<={MaximumResponseBytes}\ndepth<={MaximumDepth}\n" +
            $"selection={SelectionScope}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public static PrtgSnapshotBatch100Capability CreateUnavailable(IReadOnlyList<long> requestedIds, int? requestUrlBytes,
        string status, string reason)
    {
        if (requestedIds.Count > MaximumBatchSize || requestedIds.Any(id => id <= 0) ||
            requestedIds.Distinct().Count() != requestedIds.Count)
            throw new ArgumentException("Batch capability allows at most one hundred distinct positive sensor IDs.");
        return new PrtgSnapshotBatch100Capability
        {
            Status = status,
            Reason = reason,
            RequestedCount = requestedIds.Count,
            RequestedAliases = requestedIds.Select((_, index) => $"b{index + 1}").ToArray(),
            RequestUrlBytes = requestUrlBytes,
            ShapeFingerprint = requestedIds.Count >= 1 ? ComputeShapeFingerprint(requestedIds.Count) : ""
        };
    }

    public static PrtgSnapshotBatch100Capability Parse(string json, IReadOnlyList<long> requestedIds,
        int? requestUrlBytes = null)
    {
        if (requestedIds.Count is < 1 or > MaximumBatchSize || requestedIds.Any(id => id <= 0) ||
            requestedIds.Distinct().Count() != requestedIds.Count)
            throw new ArgumentException("Batch capability requires one to one hundred distinct positive sensor IDs.");

        var aliases = requestedIds.Select((id, index) => (id, alias: $"b{index + 1}"))
            .ToDictionary(pair => pair.id, pair => pair.alias);
        var result = new PrtgSnapshotBatch100Capability
        {
            RequestedCount = requestedIds.Count,
            RequestedAliases = aliases.Values.ToArray(),
            RequestUrlBytes = requestUrlBytes,
            ShapeFingerprint = ComputeShapeFingerprint(requestedIds.Count),
            Status = "partial"
        };

        var responseBytes = Encoding.UTF8.GetByteCount(json);
        result.ResponseBytes = responseBytes;
        if (responseBytes > MaximumResponseBytes)
        { result.Reason = "response-byte-limit"; result.RuntimeResponseReason = "response-byte-limit"; return result; }
        var runtimeValidation = PrtgSnapshotResponseContract.Validate(json, requestedIds);
        result.RuntimeResponseCompatible = runtimeValidation.Compatible;
        result.MinimumFieldsObserved = runtimeValidation.MinimumFieldsObserved;
        result.RuntimeResponseReason = runtimeValidation.Reason;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                doc.RootElement.EnumerateObject().Count(p => string.Equals(p.Name, "sensors", StringComparison.OrdinalIgnoreCase)) != 1 ||
                !doc.RootElement.TryGetProperty("sensors", out var rows) || rows.ValueKind != JsonValueKind.Array)
            { result.Reason = "sensor-table-missing-or-ambiguous"; return result; }
            result.RespondedCount = rows.GetArrayLength();
            result.Truncated = result.RespondedCount > requestedIds.Count;
            if (rows.GetArrayLength() > requestedIds.Count + 1)
            { result.Reason = "response-row-limit"; return result; }
            var seen = new HashSet<long>();
            var returned = new List<string>(Math.Min(rows.GetArrayLength(), requestedIds.Count + 1));
            var foreignIds = new Dictionary<long, string>();
            var foreignOverflow = false;
            var invalidCount = 0;
            var invalid = false;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    row.EnumerateObject().Count(p => string.Equals(p.Name, "objid", StringComparison.OrdinalIgnoreCase)) != 1 ||
                    !row.TryGetProperty("objid", out var value) || !TryId(value, out var id))
                { if (invalidCount < 8) returned.Add("invalid"); invalidCount++; invalid = true; continue; }
                if (!seen.Add(id)) invalid = true;
                if (aliases.TryGetValue(id, out var alias)) returned.Add(alias);
                else if (foreignIds.TryGetValue(id, out alias)) returned.Add(alias);
                else if (foreignIds.Count < 8)
                { foreignIds[id] = alias = $"foreign{foreignIds.Count + 1}"; returned.Add(alias); }
                else { foreignOverflow = true; }
            }
            if (foreignOverflow) returned.Add("foreign-other");
            if (invalidCount > 8) returned.Add("invalid-other");
            result.ReturnedAliases = returned.ToArray();
            result.ExactRequestedSet = !invalid && !result.Truncated && rows.GetArrayLength() == requestedIds.Count && seen.SetEquals(requestedIds);
            result.Status = result.ExactRequestedSet ? "ok" : "partial";
            result.Reason = result.ExactRequestedSet ? "exact-unique-requested-set" :
                result.Truncated ? "sentinel-or-extra-row-observed" : "missing-foreign-duplicate-or-invalid-sensor";
        }
        catch (JsonException) { result.Reason = "malformed-json"; }
        return result;
    }

    private static bool TryId(JsonElement value, out long id)
    {
        id = 0;
        return (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out id) ||
                value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out id)) && id > 0;
    }
}
