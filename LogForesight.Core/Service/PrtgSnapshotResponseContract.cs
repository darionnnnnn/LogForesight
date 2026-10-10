using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LogForesight.Core.Service;

/// <summary>Shared runtime response contract for bounded PRTG snapshot table reads (1–100 IDs).</summary>
public static class PrtgSnapshotResponseContract
{
    public const int MaximumResponseBytes = 512 * 1024;
    public const int MaximumDepth = 32;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = MaximumDepth };

    public sealed record ValidationResult(bool Compatible, bool MinimumFieldsObserved, string Reason);

    /// <summary>
    /// Validates a bounded snapshot response. Reasons are stable codes and contain no source values.
    /// IDs must be one to one hundred distinct positive integers.
    /// </summary>
    public static ValidationResult Validate(string json, IReadOnlyCollection<long> requestedSensorIds)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(requestedSensorIds);
        var expected = requestedSensorIds.ToHashSet();
        if (expected.Count is < 1 or > 100 || expected.Count != requestedSensorIds.Count || expected.Any(id => id <= 0))
            throw new ArgumentException("Snapshot contract requires one to one hundred distinct positive sensor IDs.", nameof(requestedSensorIds));
        if (Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes)
            return new(false, false, "response-byte-limit");

        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            var root = document.RootElement;
            if (ContainsDuplicateProperties(root, 0))
                return new(false, false, "duplicate-or-case-ambiguous-properties");
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sensors", out var sensors) || sensors.ValueKind != JsonValueKind.Array)
                return new(false, false, "sensors-array-missing");

            var actual = new HashSet<long>();
            var minimumFieldsObserved = sensors.GetArrayLength() > 0;
            foreach (var row in sensors.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("objid", out var idElement) ||
                    !TryReadId(idElement, out var id) || !actual.Add(id))
                    return new(false, false, "malformed-or-duplicate-sensor-id");
                if (!expected.Contains(id))
                    return new(false, false, "sensor-outside-requested-scope");
                var statusValid = row.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(status.GetString());
                var lastCheckValid = row.TryGetProperty("lastcheck", out var lastCheck) && HasScalarText(lastCheck);
                if (!statusValid || !lastCheckValid) return new(false, false, "minimum-snapshot-fields-missing");
            }
            if (!actual.SetEquals(expected)) return new(false, minimumFieldsObserved, "requested-sensor-set-mismatch");
            return new(true, minimumFieldsObserved, "runtime-snapshot-contract-compatible");
        }
        catch (JsonException)
        {
            return new(false, false, "malformed-json");
        }
    }

    private static bool ContainsDuplicateProperties(JsonElement element, int depth)
    {
        if (depth > MaximumDepth) return true;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || ContainsDuplicateProperties(property.Value, depth + 1)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (ContainsDuplicateProperties(item, depth + 1)) return true;
        return false;
    }

    private static bool TryReadId(JsonElement element, out long id)
    {
        id = 0;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetInt64(out id);
        return element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static bool HasScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
        JsonValueKind.Number => true,
        _ => false
    };
}
