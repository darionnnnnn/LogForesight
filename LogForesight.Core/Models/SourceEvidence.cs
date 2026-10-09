using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers.Binary;

namespace LogForesight.Core.Models;

/// <summary>Provenance for one source observation. Missing native facts stay null/Unknown.</summary>
public sealed class SourceEvidence
{
    public const int MaximumTextLength = 512;
    public const int MaximumSerializedBytes = 8 * 1024;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SourceEvidenceKind SourceKind { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SourceEvidenceRelation RelationContract { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SourceResourceScope ResourceScope { get; set; }

    /// <summary>Canonical exact identity only when sourced from a verified field or mapping.</summary>
    public string? ExactResourceKey { get; set; }

    /// <summary>Canonical owner host, required even when ExactResourceKey is a volume/device-local name.</summary>
    public string? ExactHostKey { get; set; }

    /// <summary>Exact UTC event instant when the source supplies one.</summary>
    public DateTimeOffset? EventTimeUtc { get; set; }

    /// <summary>Explicit half-open UTC interval [start,end) when the source proves an interval.</summary>
    public DateTimeOffset? WindowStartUtc { get; set; }
    public DateTimeOffset? WindowEndUtc { get; set; }
    public string? WindowResolution { get; set; }

    public string? SourceReference { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SourceReferenceQuality SourceReferenceQuality { get; set; }

    /// <summary>Fingerprint of mapped source fields/message, never an addressable source reference.</summary>
    public string? ProjectionFingerprint { get; set; }

    public bool HasValidWindow => WindowStartUtc is { } start && WindowEndUtc is { } end &&
        start.Offset == TimeSpan.Zero && end.Offset == TimeSpan.Zero && end > start;

    public bool HasExactReference => SourceReferenceQuality == SourceReferenceQuality.ExactNative &&
        !string.IsNullOrWhiteSpace(SourceReference);

    public SourceEvidence BoundedCopy() => new()
    {
        SourceKind = SourceKind,
        RelationContract = RelationContract,
        ResourceScope = ResourceScope,
        ExactResourceKey = Bound(ExactResourceKey),
        ExactHostKey = Bound(ExactHostKey),
        EventTimeUtc = Utc(EventTimeUtc),
        WindowStartUtc = Utc(WindowStartUtc),
        WindowEndUtc = Utc(WindowEndUtc),
        WindowResolution = Bound(WindowResolution),
        SourceReference = Bound(SourceReference),
        SourceReferenceQuality = SourceReferenceQuality,
        ProjectionFingerprint = IsSha256Hex(ProjectionFingerprint) ? ProjectionFingerprint!.ToUpperInvariant() : null
    };

    public static string? Serialize(SourceEvidence? evidence)
    {
        if (evidence == null) return null;
        var json = JsonSerializer.Serialize(evidence.BoundedCopy());
        return Encoding.UTF8.GetByteCount(json) <= MaximumSerializedBytes ? json : null;
    }

    public static SourceEvidence WithCanonicalHost(SourceEvidence evidence, string? hostKey)
    {
        var copy = evidence.BoundedCopy();
        var boundedKey = Bound(hostKey);
        copy.ExactHostKey = boundedKey;
        if (copy.ResourceScope == SourceResourceScope.Host) copy.ExactResourceKey = boundedKey;
        return copy;
    }

    public static SourceEvidence? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes) return null;
        try { return JsonSerializer.Deserialize<SourceEvidence>(json)?.BoundedCopy(); }
        catch (JsonException) { return null; }
    }

    public static List<SourceEvidence> BoundObservations(IEnumerable<SourceEvidence?> evidence,
        out bool truncated)
    {
        const int maximumCount = 64;
        var result = new List<SourceEvidence>(maximumCount);
        truncated = false;
        foreach (var item in evidence)
        {
            if (item == null) continue;
            var copy = item.BoundedCopy();
            var candidate = result.Append(copy).ToList();
            var json = JsonSerializer.Serialize(candidate);
            if (result.Count >= maximumCount || Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes)
            {
                truncated = true;
                break;
            }
            result.Add(copy);
        }
        return result;
    }

    public static string? SerializeObservations(IEnumerable<SourceEvidence> evidence)
        => SerializeObservations(evidence, out _);

    public static string? SerializeObservations(IEnumerable<SourceEvidence> evidence, out bool truncated)
    {
        var bounded = BoundObservations(evidence, out truncated);
        var json = JsonSerializer.Serialize(bounded);
        return Encoding.UTF8.GetByteCount(json) <= MaximumSerializedBytes ? json : null;
    }

    public static List<SourceEvidence> DeserializeObservations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes) return new();
        try
        {
            return BoundObservations(JsonSerializer.Deserialize<List<SourceEvidence>>(json) ?? new(), out _);
        }
        catch (JsonException) { return new(); }
    }

    public static string Fingerprint(params string?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var byteBuffer = new byte[4096];
        var charBuffer = new char[2048];
        var lengthBytes = new byte[4];
        var encoder = Encoding.UTF8.GetEncoder();
        foreach (var field in fields)
        {
            var value = field ?? string.Empty;
            BinaryPrimitives.WriteInt32BigEndian(lengthBytes, Encoding.UTF8.GetByteCount(value));
            hash.AppendData(lengthBytes);
            var index = 0;
            while (index < value.Length)
            {
                var charCount = Math.Min(charBuffer.Length, value.Length - index);
                value.CopyTo(index, charBuffer, 0, charCount);
                encoder.Convert(charBuffer, 0, charCount, byteBuffer, 0, byteBuffer.Length, index + charCount == value.Length,
                    out var charsUsed, out var bytesUsed, out _);
                hash.AppendData(byteBuffer, 0, bytesUsed);
                index += charsUsed;
            }
            encoder.Reset();
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string? Bound(string? value) => string.IsNullOrEmpty(value) || value.Length > MaximumTextLength
        ? null
        : value;

    private static DateTimeOffset? Utc(DateTimeOffset? value) => value is { } date
        ? date.ToUniversalTime()
        : null;

    private static bool IsSha256Hex(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceEvidenceKind
{
    Unknown = 0,
    SentinelWindows = 1,
    SentinelLinux = 2,
    LocalClassicEventLog = 3,
    LocalEventRecord = 4,
    Prtg = 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceResourceScope
{
    Unknown = 0,
    Host = 1,
    Volume = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceReferenceQuality
{
    Unknown = 0,
    ExactNative = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceEvidenceRelation
{
    Unknown = 0,
    DiskIoToHardware = 1,
    CapacityToDiskSensor = 2,
    ShutdownToAvailability = 3
}

public sealed record SourceEvidenceMatch(bool SameResourceAndWindow, string ReasonCode);

/// <summary>Fail-closed matcher for typed, exact cross-source evidence.</summary>
public static class SourceEvidenceMatcher
{
    public static SourceEvidenceMatch Match(SourceEvidence? left, SourceEvidence? right)
    {
        if (left == null || right == null || left.ResourceScope == SourceResourceScope.Unknown ||
            right.ResourceScope == SourceResourceScope.Unknown || string.IsNullOrWhiteSpace(left.ExactResourceKey) ||
            string.IsNullOrWhiteSpace(right.ExactResourceKey) || string.IsNullOrWhiteSpace(left.ExactHostKey) ||
            string.IsNullOrWhiteSpace(right.ExactHostKey))
            return new(false, "resource-identity-unavailable");

        if (!string.Equals(left.ExactHostKey, right.ExactHostKey, StringComparison.Ordinal))
            return new(false, "host-identity-mismatch");

        if (left.ResourceScope != right.ResourceScope ||
            !string.Equals(left.ExactResourceKey, right.ExactResourceKey, StringComparison.Ordinal))
            return new(false, "resource-identity-mismatch");

        if (!IsEventPrtgPair(left.SourceKind, right.SourceKind) ||
            left.RelationContract == SourceEvidenceRelation.Unknown ||
            !Enum.IsDefined(left.RelationContract) || left.RelationContract != right.RelationContract)
            return new(false, "signal-pair-unverified");

        if (!HasTemporalAlignment(left, right))
            return new(false, "utc-window-unavailable-or-no-overlap");

        if (!left.HasExactReference || !right.HasExactReference)
            return new(false, "source-reference-unavailable");

        return new(true, "exact-resource-window-source-references");
    }

    private static bool IsEventPrtgPair(SourceEvidenceKind left, SourceEvidenceKind right) =>
        left == SourceEvidenceKind.Prtg && IsEventSource(right) ||
        right == SourceEvidenceKind.Prtg && IsEventSource(left);

    private static bool IsEventSource(SourceEvidenceKind kind) => kind is
        SourceEvidenceKind.SentinelWindows or SourceEvidenceKind.SentinelLinux or
        SourceEvidenceKind.LocalClassicEventLog or SourceEvidenceKind.LocalEventRecord;

    private static bool HasTemporalAlignment(SourceEvidence left, SourceEvidence right)
    {
        if (left.EventTimeUtc is { Offset: var leftOffset } leftInstant && leftOffset == TimeSpan.Zero &&
            right.HasValidWindow && right.WindowStartUtc is { } rightStart && right.WindowEndUtc is { } rightEnd &&
            leftInstant >= rightStart && leftInstant < rightEnd) return true;
        if (right.EventTimeUtc is { Offset: var rightOffset } rightInstant && rightOffset == TimeSpan.Zero &&
            left.HasValidWindow && left.WindowStartUtc is { } leftStart && left.WindowEndUtc is { } leftEnd &&
            rightInstant >= leftStart && rightInstant < leftEnd) return true;

        return left.HasValidWindow && right.HasValidWindow &&
            left.WindowStartUtc is { } leftWindowStart && left.WindowEndUtc is { } leftWindowEnd &&
            right.WindowStartUtc is { } rightWindowStart && right.WindowEndUtc is { } rightWindowEnd &&
            leftWindowStart < rightWindowEnd && rightWindowStart < leftWindowEnd;
    }
}

public static class SourceEvidenceSummary
{
    public static string Describe(SourceEvidence? evidence)
    {
        if (evidence == null) return "來源佐證：unknown";
        var time = evidence.EventTimeUtc?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            ?? (evidence.HasValidWindow
                ? $"[{evidence.WindowStartUtc:O},{evidence.WindowEndUtc:O})"
                : "UTC 時間窗未提供");
        var identity = evidence.ResourceScope == SourceResourceScope.Unknown || string.IsNullOrWhiteSpace(evidence.ExactResourceKey)
            ? "資源身分 unknown" : $"{evidence.ResourceScope} identity exact";
        if (!string.IsNullOrWhiteSpace(evidence.ExactResourceKey) && !string.IsNullOrWhiteSpace(evidence.ExactHostKey))
            identity += ", host key exact";
        var reference = evidence.HasExactReference ? "native reference exact" : "source reference unavailable";
        return $"來源佐證：{evidence.SourceKind}; {identity}; {time}; {reference}";
    }

    public static string DescribeObservations(IEnumerable<SourceEvidence> evidence, bool truncated)
    {
        var items = evidence.Take(64).ToList();
        var timeCount = items.Count(e => e.EventTimeUtc.HasValue || e.HasValidWindow);
        var hostCount = items.Count(e => e.ResourceScope == SourceResourceScope.Host && !string.IsNullOrWhiteSpace(e.ExactResourceKey));
        var volumeCount = items.Count(e => e.ResourceScope == SourceResourceScope.Volume && !string.IsNullOrWhiteSpace(e.ExactResourceKey));
        var referenceCount = items.Count(e => e.HasExactReference);
        return $"來源觀測：{items.Count} 筆；UTC時間 {timeCount}，Host鍵 {hostCount}，Volume鍵 {volumeCount}，native reference {referenceCount}" +
               (truncated ? "；清單有界截斷" : "");
    }
}
