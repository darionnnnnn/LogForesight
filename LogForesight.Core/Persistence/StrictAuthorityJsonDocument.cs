using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LogForesight.Core.Persistence;

/// <summary>
/// Rejects JSON shapes that System.Text.Json would otherwise silently collapse while
/// deserializing persisted authority. The raw document is checked before any typed read.
/// </summary>
internal static class StrictAuthorityJsonDocument
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<KnownProperty>> KnownPropertyCache = new();

    internal static void Validate<T>(string raw) => Validate(raw, typeof(T));

    internal static void Validate(string raw, Type valueType)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(valueType);
        using var document = JsonDocument.Parse(raw);
        ValidateElement(document.RootElement, valueType);
    }

    internal static string PreserveUnknownDictionaryEntryProperties<TEntry>(string? originalRaw, string serialized)
    {
        if (string.IsNullOrWhiteSpace(originalRaw)) return serialized;
        var original = JsonNode.Parse(originalRaw) as JsonObject
            ?? throw new JsonException("Persisted authority must be a JSON object.");
        var output = JsonNode.Parse(serialized) as JsonObject
            ?? throw new JsonException("Serialized authority must be a JSON object.");
        var knownNames = GetKnownProperties(typeof(TEntry)).Select(property => property.JsonName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (entryKey, oldValue) in original)
        {
            if (oldValue is not JsonObject oldEntry || !output.TryGetPropertyValue(entryKey, out var newValue) ||
                newValue is not JsonObject newEntry)
                continue;
            foreach (var (propertyName, propertyValue) in oldEntry)
                if (!knownNames.Contains(propertyName))
                    newEntry[propertyName] = propertyValue?.DeepClone();
        }
        return output.ToJsonString(LfJsonOptions.Pretty);
    }

    private static void ValidateElement(JsonElement element, Type declaredType)
    {
        if (element.ValueKind == JsonValueKind.Null) return;
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (TryGetDictionaryTypes(type, out var keyType, out var valueType))
        {
            if (element.ValueKind != JsonValueKind.Object) return;
            ValidateObjectProperties(element, null);
            foreach (var property in element.EnumerateObject())
            {
                if (keyType == typeof(long)) ValidateCanonicalPositiveIntegerKey(property.Name);
                ValidateElement(property.Value, valueType);
            }
            return;
        }

        if (TryGetEnumerableElementType(type, out var itemType))
        {
            if (element.ValueKind != JsonValueKind.Array) return;
            foreach (var item in element.EnumerateArray()) ValidateElement(item, itemType);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            // Even values not represented by a known DTO still need exact duplicate
            // detection in nested extension objects.
            ValidateUnknownSubtree(element);
            return;
        }

        var known = GetKnownProperties(type);
        ValidateObjectProperties(element, known);
        foreach (var property in element.EnumerateObject())
        {
            var match = known.FirstOrDefault(candidate =>
                string.Equals(candidate.JsonName, property.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) ValidateElement(property.Value, match.Property.PropertyType);
            else ValidateUnknownSubtree(property.Value);
        }
    }

    private static void ValidateUnknownSubtree(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObjectProperties(element, null);
                foreach (var property in element.EnumerateObject()) ValidateUnknownSubtree(property.Value);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) ValidateUnknownSubtree(item);
                break;
        }
    }

    private static void ValidateObjectProperties(JsonElement element, IReadOnlyList<KnownProperty>? known)
    {
        var exactNames = new HashSet<string>(StringComparer.Ordinal);
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!exactNames.Add(property.Name))
                throw new JsonException($"Duplicate JSON property '{property.Name}' is not allowed in persisted authority.");

            if (known is null) continue;
            var match = known.FirstOrDefault(candidate =>
                string.Equals(candidate.JsonName, property.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !knownNames.Add(match.JsonName))
                throw new JsonException($"Multiple JSON aliases target the same typed property '{match.JsonName}'.");
        }
    }

    private static IReadOnlyList<KnownProperty> GetKnownProperties(Type type) =>
        KnownPropertyCache.GetOrAdd(type, static candidateType => BuildKnownProperties(candidateType));

    private static IReadOnlyList<KnownProperty> BuildKnownProperties(Type type)
    {
        if (type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonDocument)) return [];
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0 &&
                               (property.SetMethod is not null || property.GetCustomAttribute<JsonExtensionDataAttribute>() is not null) &&
                               property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .Select(property => new KnownProperty(property,
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name))
            .ToArray();
    }

    private static bool TryGetDictionaryTypes(Type type, out Type keyType, out Type valueType)
    {
        var match = type.GetInterfaces().Append(type).FirstOrDefault(candidate =>
            candidate.IsGenericType && candidate.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)));
        if (match is null)
        {
            keyType = valueType = typeof(object);
            return false;
        }
        var arguments = match.GetGenericArguments();
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private static bool TryGetEnumerableElementType(Type type, out Type itemType)
    {
        if (type != typeof(string))
        {
            var match = type.GetInterfaces().Append(type).FirstOrDefault(candidate =>
                candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (match is not null)
            {
                itemType = match.GetGenericArguments()[0];
                return true;
            }
        }
        itemType = typeof(object);
        return false;
    }

    private static void ValidateCanonicalPositiveIntegerKey(string key)
    {
        if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0 ||
            !string.Equals(key, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new JsonException($"Dictionary key '{key}' is not a canonical positive integer.");
    }

    private sealed record KnownProperty(PropertyInfo Property, string JsonName);
}
