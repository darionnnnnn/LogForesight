using System.Globalization;
using System.Text.Json;

namespace LogForesight.Core.Service;

public enum PrtgDiskSemanticProbeStatus { Verified, NeedsManualReview, Mismatch }

public sealed record PrtgDiskSemanticPersistedPoint(DateTime TimestampUtc, double Value);
public sealed record PrtgDiskSemanticSourceContract(string RawTimestampTimeZoneId,
    string SourceApiTimeZoneId, string PrimaryChannelId, string PrimaryChannelCaption);
public sealed record PrtgDiskSemanticProbeResult(
    long SensorObjid,
    PrtgDiskSemanticProbeStatus Status,
    string Summary,
    string? ChannelIdentifier,
    string? ChannelName,
    string? Unit,
    double? Scale,
    string? Direction,
    int ComparedPointCount,
    bool? ValuesMatch,
    IReadOnlyList<string> Evidence);

/// <summary>Bounded, read-only PRTG channel and historic sample semantic probe.</summary>
public sealed class PrtgDiskSemanticProbe
{
    public const int MaxApiCalls = 2;
    private readonly Func<string, CancellationToken, Task<string>> _getJson;
    private readonly PrtgDiskSemanticSourceContract? _contract;

    public PrtgDiskSemanticProbe(PrtgClient client, PrtgDiskSemanticSourceContract? contract = null)
        : this((path, ct) => client.GetBoundedJsonAsync(path, 512 * 1024, ct), contract) { }

    public PrtgDiskSemanticProbe(Func<string, CancellationToken, Task<string>> getJson,
        PrtgDiskSemanticSourceContract? contract = null)
    { _getJson = getJson ?? throw new ArgumentNullException(nameof(getJson)); _contract = contract; }

    public async Task<PrtgDiskSemanticProbeResult> ProbeAsync(
        long sensorObjid, DateTime startUtc, DateTime endUtc,
        IReadOnlyCollection<PrtgDiskSemanticPersistedPoint>? persistedPoints = null,
        CancellationToken cancellationToken = default)
    {
        if (sensorObjid <= 0) throw new ArgumentOutOfRangeException(nameof(sensorObjid));
        if (startUtc.Kind != DateTimeKind.Utc || endUtc.Kind != DateTimeKind.Utc || endUtc <= startUtc || endUtc - startUtc > TimeSpan.FromDays(1))
            throw new ArgumentException("Probe interval must be UTC, positive, and no longer than one day.");

        cancellationToken.ThrowIfCancellationRequested();
        var channelJson = await _getJson($"/api/table.json?content=channels&id={sensorObjid}&columns=objid,name,unit,scaling,primary&count=100", cancellationToken);
        var channel = ParsePrimary(channelJson, _contract);
        if (!TryZones(_contract, out var rawZone, out var apiZone))
            return new(sensorObjid, PrtgDiskSemanticProbeStatus.NeedsManualReview,
                "Source time basis or primary channel authority is unconfirmed; no physical sample comparison is authorized.",
                channel?.Id, channel?.Name, channel?.Unit, channel?.Scale, null, 0, null, ["source-contract-unconfirmed"]);
        var sdate = Uri.EscapeDataString(TimeZoneInfo.ConvertTimeFromUtc(startUtc, apiZone!).ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture));
        var edate = Uri.EscapeDataString(TimeZoneInfo.ConvertTimeFromUtc(endUtc, apiZone!).ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture));
        var historyJson = await _getJson($"/api/historicdata.json?id={sensorObjid}&avg=0&usecaption=1&sdate={sdate}&edate={edate}", cancellationToken);
        var raw = ParseRawPoints(historyJson, _contract!.PrimaryChannelCaption, rawZone!);
        var compare = persistedPoints?.Take(5).ToArray() ?? [];
        var aligned = channel is not null && channel.Value.Id == _contract.PrimaryChannelId && channel.Value.Name == _contract.PrimaryChannelCaption;
        var matches = aligned ? Compare(raw, compare) : null;
        var reasons = new List<string>();
        if (channel is null) reasons.Add("No unambiguous primary channel was identified.");
        else if (!aligned) reasons.Add("Source primary channel ID/caption differs from current channel metadata.");
        else if (string.IsNullOrWhiteSpace(channel.Value.Unit)) reasons.Add("Channel unit is missing.");
        else if (!IsPercent(channel.Value.Unit)) reasons.Add("Channel unit does not explicitly identify percent.");
        if (channel is not null && !IsFreeChannel(channel.Value.Name)) reasons.Add("Primary channel name does not unambiguously indicate free or available capacity.");
        if (channel is not null && (string.IsNullOrWhiteSpace(channel.Value.Id) || channel.Value.Scale is not > 0 || !double.IsFinite(channel.Value.Scale.Value)))
            reasons.Add("Channel identifier or scale is missing or invalid.");
        if (channel is not null && channel.Value.Scale != 1) reasons.Add("Percent scale must be explicitly confirmed as 1.");
        if (raw.Count == 0) reasons.Add("No usable historic raw points were returned.");
        if (matches == false) reasons.Add("Historic raw points differ from persisted values.");
        if (matches != true) reasons.Add("At least one persisted value must match a historic raw point.");

        var status = matches == false ? PrtgDiskSemanticProbeStatus.Mismatch :
            reasons.Count == 0 ? PrtgDiskSemanticProbeStatus.Verified : PrtgDiskSemanticProbeStatus.NeedsManualReview;
        var evidence = new List<string> { $"Historic raw point count: {raw.Count}.", $"Compared persisted points: {compare.Length}." };
        if (channel is not null) evidence.Add($"Channel unit: {(string.IsNullOrWhiteSpace(channel.Value.Unit) ? "missing" : Safe(channel.Value.Unit))}.");
        return new PrtgDiskSemanticProbeResult(sensorObjid, status, string.Join(" ", reasons.DefaultIfEmpty("Channel and historic values are consistent with an explicit percent unit.")),
            channel?.Id, channel?.Name, channel?.Unit, channel?.Scale,
            channel is not null && IsFreeChannel(channel.Value.Name) ? "descending-danger" : null,
            matches.HasValue ? compare.Length : 0, matches, evidence);
    }

    private static (string? Id, string? Name, string? Unit, double? Scale, int Index)? ParsePrimary(string json,
        PrtgDiskSemanticSourceContract? contract)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 512 * 1024) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("channels", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;
        if (rows.GetArrayLength() > 100) return null;
        var list = rows.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToArray();
        if (list.Length == 0) return null;
        var marked = list.Select((row, index) => (Row: row, Index: index)).Where(x => x.Row.TryGetProperty("primary", out var p) && IsPrimaryMarker(p)).ToArray();
        if (marked.Length > 1) return null;
        var matches = contract is null ? [] : list.Select((row, index) => (Row: row, Index: index))
            .Where(x => Text(x.Row, "objid") == contract.PrimaryChannelId && Text(x.Row, "name") == contract.PrimaryChannelCaption).ToArray();
        var chosen = marked.Length == 1 ? marked[0] : matches.Length == 1 ? matches[0] : default;
        if (chosen.Row.ValueKind != JsonValueKind.Object) return null;
        if (list.Count(row => Text(row, "objid") == Text(chosen.Row, "objid") || Text(row, "name") == Text(chosen.Row, "name")) != 1) return null;
        return (Text(chosen.Row, "objid"), Text(chosen.Row, "name"), Text(chosen.Row, "unit"), Number(chosen.Row, "scaling"), chosen.Index);
    }

    private static bool IsPrimaryMarker(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetInt32(out var number) && number == 1,
        JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() is "1" or "true",
        _ => false
    };

    private static bool TryZones(PrtgDiskSemanticSourceContract? contract,
        out TimeZoneInfo? rawZone, out TimeZoneInfo? apiZone)
    {
        rawZone = null; apiZone = null;
        if (contract is null || string.IsNullOrWhiteSpace(contract.PrimaryChannelId) ||
            string.IsNullOrWhiteSpace(contract.PrimaryChannelCaption) || contract.PrimaryChannelCaption.Length > 128 ||
            string.IsNullOrWhiteSpace(contract.RawTimestampTimeZoneId) || string.IsNullOrWhiteSpace(contract.SourceApiTimeZoneId)) return false;
        try
        {
            rawZone = TimeZoneInfo.FindSystemTimeZoneById(contract.RawTimestampTimeZoneId);
            apiZone = TimeZoneInfo.FindSystemTimeZoneById(contract.SourceApiTimeZoneId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return false; }
    }

    private static List<(DateTime Time, double Value)> ParseRawPoints(string json, string caption, TimeZoneInfo rawZone)
    {
        var output = new List<(DateTime, double)>();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 512 * 1024) return output;
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (!doc.RootElement.TryGetProperty("histdata", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 2000) return output;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)) return [];
            if (!TryRaw(row, caption + "_raw", out var value) || !TryRaw(row, "datetime_raw", out var serial)) continue;
            try
            {
                var wall = DateTime.SpecifyKind(DateTime.FromOADate(serial), DateTimeKind.Unspecified);
                if (rawZone.IsInvalidTime(wall) || rawZone.IsAmbiguousTime(wall)) continue;
                var utc = TimeZoneInfo.ConvertTimeToUtc(wall, rawZone);
                if (output.Any(p => p.Item1 == utc)) return []; // 重複時間無法唯一對齊物理樣本。
                output.Add((utc, value));
            }
            catch (ArgumentException) { }
        }
        return output;
    }

    private static bool? Compare(List<(DateTime Time, double Value)> raw, PrtgDiskSemanticPersistedPoint[] points)
    {
        if (points.Length == 0 || raw.Count == 0) return null;
        foreach (var point in points)
        {
            if (point.TimestampUtc.Kind != DateTimeKind.Utc || !double.IsFinite(point.Value)) return null;
            var match = raw.FirstOrDefault(x => Math.Abs((x.Time - point.TimestampUtc).TotalSeconds) <= 1);
            if (match == default || Math.Abs(match.Value - point.Value) > Math.Max(0.01, Math.Abs(point.Value) * 0.001)) return false;
        }
        return true;
    }

    private static bool TryRaw(JsonElement row, string field, out double value)
    {
        value = 0;
        if (!row.TryGetProperty(field, out var raw)) return false;
        if (raw.ValueKind == JsonValueKind.Number) return raw.TryGetDouble(out value) && double.IsFinite(value);
        return raw.ValueKind == JsonValueKind.String && double.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
    private static string? Text(JsonElement row, string key) => row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : row.TryGetProperty(key, out v) && v.ValueKind == JsonValueKind.Number ? v.ToString() : null;
    private static double? Number(JsonElement row, string key) => row.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) || v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) && double.IsFinite(n) ? n : null;
    private static bool IsPercent(string unit) => unit.Trim().Equals("%", StringComparison.Ordinal) || unit.Trim().Equals("percent", StringComparison.OrdinalIgnoreCase);
    private static bool IsFreeChannel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = string.Join(' ', name.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Contains("used") || normalized.Contains("total") || normalized.Contains("utiliz") || normalized.Contains("consumed")) return false;
        return normalized is "free" or "free space" or "free capacity" or "available" or "available space" or "available capacity";
    }
    private static string Safe(string text) => new(text.Where(c => !char.IsControl(c)).Take(80).ToArray());
}
