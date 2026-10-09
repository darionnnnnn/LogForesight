using System.Globalization;
using System.Text.Json;

namespace LogForesight.Web.Services;

/// <summary>Validates the minimum snapshot table response contract before it can count as capacity evidence.</summary>
public static class PrtgSnapshotCapacityResponseValidator
{
    public static void Validate(string json, IReadOnlyCollection<long> requestedSensorIds)
    {
        var expected = requestedSensorIds.ToHashSet();
        if (expected.Count == 0 || expected.Count != requestedSensorIds.Count || expected.Count > 50)
            throw new InvalidDataException("Capacity sample must use a non-empty distinct sensor ID set of at most 50.");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("sensors", out var sensors) || sensors.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Snapshot response has no sensors array.");

        var actual = new HashSet<long>();
        foreach (var row in sensors.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("objid", out var idElement) ||
                !TryReadId(idElement, out var id) || !actual.Add(id))
                throw new InvalidDataException("Snapshot response contains a malformed or duplicate sensor ID.");
            if (!expected.Contains(id))
                throw new InvalidDataException("Snapshot response contains a sensor outside the requested scope.");
            if (!row.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(status.GetString()))
                throw new InvalidDataException("Snapshot response is missing sensor status.");
            if (!row.TryGetProperty("lastcheck", out var lastCheck) || !HasScalarText(lastCheck))
                throw new InvalidDataException("Snapshot response is missing lastcheck.");
        }

        if (!actual.SetEquals(expected))
            throw new InvalidDataException("Snapshot response is missing one or more requested sensors.");
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
