using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogForesight.Core.Persistence;

/// <summary>Revokes only existing disk-lane evidence whose raw timestamp basis changed.</summary>
internal static class PrtgDiskTimeBasisInvalidator
{
    internal const string SemanticInvalidationMarker = "time-basis-invalidated";
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    internal static string InvalidateSemantic(string raw, IReadOnlySet<long> affectedSensors)
    {
        ValidateDocument(raw, PrtgDiskSemanticEvidenceStore.ValidateRepairDocument);
        return Transform(raw, affectedSensors, entry =>
            Set(entry, "ParserSemanticVersion", JsonValue.Create(SemanticInvalidationMarker)));
    }

    internal static string InvalidateVerification(string raw, IReadOnlySet<long> affectedSensors)
    {
        ValidateDocument(raw, PrtgDiskVerificationResultStore.ValidateRepairDocument);
        return Transform(raw, affectedSensors, entry =>
        {
            Set(entry, "ParserSemanticVersion", JsonValue.Create(SemanticInvalidationMarker));
            Set(entry, "Status", JsonValue.Create("Stale"));
            Set(entry, "ValuesMatch", null);
            Set(entry, "ComparedPointCount", JsonValue.Create(0));
        });
    }

    private static void ValidateDocument(string raw, Action<string> validate)
    {
        try { validate(raw); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException("Existing disk evidence is malformed or invalid; time basis was not changed.", ex);
        }
    }

    private static string Transform(string raw, IReadOnlySet<long> affectedSensors, Action<JsonObject> invalidate)
    {
        var root = JsonNode.Parse(raw) as JsonObject
            ?? throw new InvalidDataException("Disk evidence must be one complete JSON object.");
        var changed = false;
        foreach (var pair in root.ToArray())
        {
            if (!long.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var sensorId))
                throw new InvalidDataException("Disk evidence has an invalid sensor key.");
            if (!affectedSensors.Contains(sensorId)) continue;
            var entry = pair.Value as JsonObject
                ?? throw new InvalidDataException("Disk evidence entry is not an object.");
            invalidate(entry);
            changed = true;
        }
        if (!changed) return raw;
        var candidate = root.ToJsonString(Compact);
        if (candidate.Length > Math.Max(PrtgDiskSemanticEvidenceStore.MaximumSnapshotCharacters,
                PrtgDiskVerificationResultStore.MaximumSnapshotCharacters))
            throw new InvalidDataException("Invalidated disk evidence exceeds its bounded document limit.");
        return candidate;
    }

    private static void Set(JsonObject entry, string name, JsonNode? value)
    {
        var existing = entry.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) throw new InvalidDataException($"Disk evidence is missing required property '{name}'.");
        entry[existing] = value;
    }
}
