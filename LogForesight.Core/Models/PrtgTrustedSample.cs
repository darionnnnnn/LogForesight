using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogForesight.Core.Models;

/// <summary>有明確來源量測時間與來源身分的 PRTG 樣本輸入。</summary>
public sealed record PrtgTrustedSample(
    long SensorObjid,
    double Value,
    string SourceGeneration,
    string ResourceGeneration,
    string ChannelGeneration,
    string ResourceEpoch,
    string SemanticVersion,
    string StrategyVersion,
    int StrategyMinutes,
    DateTime StrategyEffectiveFromHour,
    DateTime MeasuredAt,
    DateTime ReceivedAt,
    TimeSpan ConfirmedScanInterval,
    PrtgTrustedSampleQuality Quality,
    string PhysicalMeasurementId,
    string RawTimestampTimeZoneId = "",
    string AnalysisTimeZoneId = "");

public enum PrtgTrustedSampleQuality { Good = 1, Paused = 2, Unknown = 3, ProbeUnavailable = 4 }
public enum PrtgTrustedSampleDisposition { Accepted, Duplicate, Replaced, DeferredFuture, Rejected }

/// <summary>小時層級只保存一次長身分；每個 slot 僅留量測值、時間與物理樣本雜湊。</summary>
public sealed record PrtgTrustedSampleProof(
    [property: System.Text.Json.Serialization.JsonPropertyName("c")] string ContextHash,
    [property: System.Text.Json.Serialization.JsonPropertyName("sg")] string SourceGeneration,
    [property: System.Text.Json.Serialization.JsonPropertyName("rg")] string ResourceGeneration,
    [property: System.Text.Json.Serialization.JsonPropertyName("cg")] string ChannelGeneration,
    [property: System.Text.Json.Serialization.JsonPropertyName("re")] string ResourceEpoch,
    [property: System.Text.Json.Serialization.JsonPropertyName("sv")] string SemanticVersion,
    [property: System.Text.Json.Serialization.JsonPropertyName("pv")] string StrategyVersion,
    [property: System.Text.Json.Serialization.JsonPropertyName("pm")] int StrategyMinutes,
    [property: System.Text.Json.Serialization.JsonPropertyName("ef")] DateTime StrategyEffectiveFromHour,
    [property: System.Text.Json.Serialization.JsonPropertyName("ci")] TimeSpan ConfirmedScanInterval,
    [property: System.Text.Json.Serialization.JsonPropertyName("rtz")] string RawTimestampTimeZoneId,
    [property: System.Text.Json.Serialization.JsonPropertyName("atz")] string AnalysisTimeZoneId,
    [property: System.Text.Json.Serialization.JsonPropertyName("s")] IReadOnlyList<PrtgTrustedSampleSlot> Slots)
{
    public const int MaximumSerializedBytes = 4 * 1024;

    public static PrtgTrustedSampleProof From(PrtgTrustedSample sample, IEnumerable<PrtgTrustedSampleSlot> slots)
    {
        var hash = ContextHashFor(sample.SourceGeneration, sample.ResourceGeneration, sample.ChannelGeneration,
            sample.ResourceEpoch, sample.SemanticVersion, sample.StrategyVersion, sample.StrategyMinutes,
            sample.StrategyEffectiveFromHour, sample.ConfirmedScanInterval, sample.RawTimestampTimeZoneId,
            sample.AnalysisTimeZoneId);
        return new(hash, sample.SourceGeneration, sample.ResourceGeneration, sample.ChannelGeneration,
            sample.ResourceEpoch, sample.SemanticVersion, sample.StrategyVersion, sample.StrategyMinutes,
            sample.StrategyEffectiveFromHour, sample.ConfirmedScanInterval, sample.RawTimestampTimeZoneId,
            sample.AnalysisTimeZoneId, slots.ToArray());
    }

    public static string Serialize(PrtgTrustedSampleProof proof)
    {
        var json = JsonSerializer.Serialize(proof);
        if (Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes)
            throw new InvalidDataException("可信樣本 proof 超過 4 KiB 上限。");
        return json;
    }

    public static PrtgTrustedSampleProof Deserialize(string json)
    {
        if (json.Length > MaximumSerializedBytes || Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes)
            throw new InvalidDataException("可信樣本 proof 超過 4 KiB 上限。");
        return JsonSerializer.Deserialize<PrtgTrustedSampleProof>(json, new JsonSerializerOptions { MaxDepth = 24 })
            ?? throw new InvalidDataException("可信樣本 proof 無效");
    }

    public static PrtgTrustedSampleProof Merge(PrtgTrustedSampleProof left, PrtgTrustedSampleProof right)
    {
        if (left.ContextHash != right.ContextHash || left.StrategyMinutes != right.StrategyMinutes)
            throw new InvalidDataException("可信樣本來源或策略身分衝突");
        var physicalSamples = left.Slots.ToDictionary(s => s.PhysicalIdHash, StringComparer.OrdinalIgnoreCase);
        foreach (var incoming in right.Slots)
            if (physicalSamples.TryGetValue(incoming.PhysicalIdHash, out var priorPhysical) &&
                (priorPhysical.MeasuredAt != incoming.MeasuredAt || priorPhysical.Value != incoming.Value))
                throw new InvalidDataException("physical_measurement_conflict: 同一物理樣本不可改變量測時間或值");
        var slots = left.Slots.ToDictionary(s => s.Slot);
        foreach (var incoming in right.Slots)
        {
            if (!slots.TryGetValue(incoming.Slot, out var prior) || incoming.MeasuredAt > prior.MeasuredAt ||
                incoming.MeasuredAt == prior.MeasuredAt && incoming.ReceivedAt > prior.ReceivedAt)
                slots[incoming.Slot] = incoming;
        }
        var byPhysicalId = slots.Values.GroupBy(s => s.PhysicalIdHash, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(s => s.MeasuredAt).ThenBy(s => s.ReceivedAt).First()).ToArray();
        var uniqueSlots = new Dictionary<int, PrtgTrustedSampleSlot>();
        foreach (var item in byPhysicalId.OrderBy(s => s.MeasuredAt).ThenBy(s => s.ReceivedAt))
            if (!uniqueSlots.TryGetValue(item.Slot, out var prior) || item.MeasuredAt > prior.MeasuredAt ||
                item.MeasuredAt == prior.MeasuredAt && item.ReceivedAt > prior.ReceivedAt)
                uniqueSlots[item.Slot] = item;
        return left with { Slots = uniqueSlots.Values.OrderBy(s => s.Slot).ToArray() };
    }

    public bool HasValidContextHash() => ContextHash == ContextHashFor(SourceGeneration, ResourceGeneration,
        ChannelGeneration, ResourceEpoch, SemanticVersion, StrategyVersion, StrategyMinutes,
        StrategyEffectiveFromHour, ConfirmedScanInterval, RawTimestampTimeZoneId, AnalysisTimeZoneId);

    public bool IsStructurallyValid()
    {
        if (!HasValidContextHash() || StrategyMinutes is not (5 or 15) ||
            StrategyEffectiveFromHour.Kind != DateTimeKind.Utc ||
            !TryZone(RawTimestampTimeZoneId, out _) || !TryZone(AnalysisTimeZoneId, out var analysisZone) ||
            !IsAnalysisHourInstant(StrategyEffectiveFromHour, analysisZone)) return false;
        return
        ValidText(SourceGeneration, 256) && ValidText(ResourceGeneration, 256) &&
        ValidText(ChannelGeneration, 256) && ValidText(ResourceEpoch, 256) &&
        ValidText(SemanticVersion, 128) && ValidText(StrategyVersion, 128) &&
        ConfirmedScanInterval > TimeSpan.Zero && ConfirmedScanInterval <= TimeSpan.FromHours(1) &&
        Slots is { Count: > 0 and <= 12 } &&
        Slots.All(s => IsValidSlot(s, analysisZone)) &&
        Slots.Select(s => s.Slot).Distinct().Count() == Slots.Count &&
        Slots.Select(s => s.PhysicalIdHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Slots.Count;
    }

    public bool MatchesHour(DateTime hour) => TryZone(AnalysisTimeZoneId, out var zone) &&
        Slots.All(s => TryAnalysisBucket(s.MeasuredAt, zone, out var wallHour) && SameWallHour(wallHour, hour));

    private static string ContextHashFor(string source, string resource, string channel, string epoch,
        string semantic, string strategy, int minutes, DateTime effectiveHour, TimeSpan interval,
        string rawTimeZone, string analysisTimeZone)
    {
        var material = JsonSerializer.Serialize(new object[] { source, resource, channel, epoch, semantic,
            strategy, minutes, effectiveHour.Ticks, interval.Ticks, rawTimeZone, analysisTimeZone });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static bool TryZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static bool TryAnalysisBucket(DateTime instant, TimeZoneInfo zone, out DateTime wallHour)
    {
        wallHour = default;
        if (instant.Kind != DateTimeKind.Utc) return false;
        var wall = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
        if (zone.IsAmbiguousTime(wall) || zone.IsInvalidTime(wall)) return false;
        wallHour = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, 0, 0, DateTimeKind.Unspecified);
        return true;
    }

    private bool IsValidSlot(PrtgTrustedSampleSlot? slot, TimeZoneInfo analysisZone)
    {
        if (slot == null || slot.MeasuredAt.Kind != DateTimeKind.Utc || slot.ReceivedAt.Kind != DateTimeKind.Utc ||
            !TryAnalysisBucket(slot.MeasuredAt, analysisZone, out _)) return false;
        var wall = TimeZoneInfo.ConvertTimeFromUtc(slot.MeasuredAt, analysisZone);
        return slot.Slot >= 0 && slot.Slot < 60 / StrategyMinutes &&
            slot.Slot == wall.Minute / StrategyMinutes && slot.MeasuredAt >= StrategyEffectiveFromHour &&
            slot.PhysicalIdHash?.Length == 32 && slot.PhysicalIdHash.All(Uri.IsHexDigit) &&
            slot.ReceivedAt >= slot.MeasuredAt &&
            slot.ReceivedAt - slot.MeasuredAt <= TimeSpan.FromTicks(ConfirmedScanInterval.Ticks * 2) + TimeSpan.FromSeconds(30) &&
            double.IsFinite(slot.Value);
    }

    private static bool IsAnalysisHourInstant(DateTime instant, TimeZoneInfo zone) =>
        instant.Kind == DateTimeKind.Utc && TryAnalysisBucket(instant, zone, out _) &&
        IsWholeWallHour(TimeZoneInfo.ConvertTimeFromUtc(instant, zone));

    private static bool IsWholeWallHour(DateTime value) => value.Minute == 0 && value.Second == 0 &&
        value.Millisecond == 0 && value.Ticks % TimeSpan.TicksPerHour == 0;

    private static bool SameWallHour(DateTime left, DateTime right) => right.Kind == DateTimeKind.Unspecified &&
        left.Year == right.Year && left.Month == right.Month && left.Day == right.Day && left.Hour == right.Hour;

    private static bool ValidText(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max;
}

public sealed record PrtgTrustedSampleSlot(
    [property: System.Text.Json.Serialization.JsonPropertyName("s")] int Slot,
    [property: System.Text.Json.Serialization.JsonPropertyName("p")] string PhysicalIdHash,
    [property: System.Text.Json.Serialization.JsonPropertyName("m")] DateTime MeasuredAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("r")] DateTime ReceivedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("v")] double Value);
