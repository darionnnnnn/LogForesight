using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogForesight.Core.Service;

/// <summary>Deidentified observation of the exact multi-filter result set; never grants risk authority.</summary>
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
        if (requestedIds.Count is < 2 or > 5 || requestedIds.Any(id => id <= 0) ||
            requestedIds.Distinct().Count() != requestedIds.Count)
            throw new ArgumentException("Batch capability requires two to five distinct positive sensor IDs.");
        var aliases = requestedIds.Select((id, index) => (id, alias: $"b{index + 1}"))
            .ToDictionary(pair => pair.id, pair => pair.alias);
        var result = new PrtgSensorBatchCapability { RequestedAliases = aliases.Values.ToArray(), Status = "partial" };
        if (Encoding.UTF8.GetByteCount(json) > 512 * 1024)
        { result.Reason = "response-byte-limit"; return result; }
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                doc.RootElement.EnumerateObject().Count(p => string.Equals(p.Name, "sensors", StringComparison.OrdinalIgnoreCase)) != 1 ||
                !doc.RootElement.TryGetProperty("sensors", out var rows) || rows.ValueKind != JsonValueKind.Array)
            { result.Reason = "sensor-table-missing-or-ambiguous"; return result; }
            if (rows.GetArrayLength() > requestedIds.Count + 1)
            { result.Reason = "response-row-limit"; return result; }
            var seen = new HashSet<long>();
            var returned = new List<string>();
            var foreign = new Dictionary<long, string>();
            var invalid = false;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    row.EnumerateObject().Count(p => string.Equals(p.Name, "objid", StringComparison.OrdinalIgnoreCase)) != 1 ||
                    !row.TryGetProperty("objid", out var value) || !TryId(value, out var id))
                { returned.Add("invalid"); invalid = true; continue; }
                if (!seen.Add(id)) invalid = true;
                if (aliases.TryGetValue(id, out var alias)) returned.Add(alias);
                else
                {
                    if (!foreign.TryGetValue(id, out alias)) foreign[id] = alias = $"foreign{foreign.Count + 1}";
                    returned.Add(alias);
                }
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
                value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out id)) && id > 0;
    }
}
