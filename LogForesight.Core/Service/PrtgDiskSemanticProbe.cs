using System.Globalization;
using System.Text.Json;

namespace LogForesight.Core.Service;

public enum PrtgDiskSemanticProbeStatus { Verified, NeedsManualReview, Mismatch }

public sealed record PrtgDiskSemanticPersistedPoint(DateTime TimestampUtc, double Value);
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

    public PrtgDiskSemanticProbe(PrtgClient client)
        : this((path, ct) => client.GetJsonAsync(path, ct)) { }

    public PrtgDiskSemanticProbe(Func<string, CancellationToken, Task<string>> getJson) =>
        _getJson = getJson ?? throw new ArgumentNullException(nameof(getJson));

    public async Task<PrtgDiskSemanticProbeResult> ProbeAsync(
        long sensorObjid, DateTime startUtc, DateTime endUtc,
        IReadOnlyCollection<PrtgDiskSemanticPersistedPoint>? persistedPoints = null,
        CancellationToken cancellationToken = default)
    {
        if (sensorObjid <= 0) throw new ArgumentOutOfRangeException(nameof(sensorObjid));
        if (startUtc.Kind != DateTimeKind.Utc || endUtc.Kind != DateTimeKind.Utc || endUtc <= startUtc || endUtc - startUtc > TimeSpan.FromDays(1))
            throw new ArgumentException("Probe interval must be UTC, positive, and no longer than one day.");

        cancellationToken.ThrowIfCancellationRequested();
        var channelJson = await _getJson($"/api/table.json?content=channels&id={sensorObjid}&columns=objid,channel,unit,scaling,primary&count=100", cancellationToken);
        var channel = ParsePrimary(channelJson);
        // PrtgFetchService builds historic ranges from local calendar dates; translate the
        // explicit UTC interval to local wall time before formatting PRTG's timezone-less API dates.
        var sdate = Uri.EscapeDataString(startUtc.ToLocalTime().ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture));
        var edate = Uri.EscapeDataString(endUtc.ToLocalTime().ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture));
        var historyJson = await _getJson($"/api/historicdata.json?id={sensorObjid}&avg=3600&sdate={sdate}&edate={edate}", cancellationToken);
        var raw = ParseRawPoints(historyJson);
        // Historic value_raw arrays follow channel order, but the endpoint does not carry
        // channel identifiers alongside each array element. Only index zero is safe to
        // compare here; never infer a later index from channel metadata alone.
        var compare = persistedPoints?.Take(5).ToArray() ?? [];
        var aligned = channel is not null && channel.Value.Index == 0;
        var matches = aligned ? Compare(raw, compare) : null;
        var reasons = new List<string>();
        if (channel is null) reasons.Add("No unambiguous primary channel was identified.");
        else if (channel.Value.Index != 0) reasons.Add("Primary channel is not the first historic value channel; value alignment cannot be established.");
        else if (string.IsNullOrWhiteSpace(channel.Value.Unit)) reasons.Add("Channel unit is missing.");
        else if (!IsPercent(channel.Value.Unit)) reasons.Add("Channel unit does not explicitly identify percent.");
        if (channel is not null && !IsFreeChannel(channel.Value.Name)) reasons.Add("Primary channel name does not unambiguously indicate free or available capacity.");
        if (channel is not null && (string.IsNullOrWhiteSpace(channel.Value.Id) || channel.Value.Scale is not > 0 || !double.IsFinite(channel.Value.Scale.Value)))
            reasons.Add("Channel identifier or scale is missing or invalid.");
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

    private static (string? Id, string? Name, string? Unit, double? Scale, int Index)? ParsePrimary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("channels", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;
        var list = rows.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToArray();
        if (list.Length == 0) return null;
        // PRTG's primary channel is conventionally the first row. Do not select among multiple rows
        // when the response explicitly marks primary and that marker is not unique.
        var marked = list.Select((row, index) => (Row: row, Index: index)).Where(x => x.Row.TryGetProperty("primary", out var p) && IsPrimaryMarker(p)).ToArray();
        var chosen = marked.Length == 1 ? marked[0] : marked.Length > 1 ? default : list.Length == 1 ? (Row: list[0], Index: 0) : default;
        if (chosen.Row.ValueKind != JsonValueKind.Object) return null;
        return (Text(chosen.Row, "objid"), Text(chosen.Row, "channel"), Text(chosen.Row, "unit"), Number(chosen.Row, "scaling"), chosen.Index);
    }

    private static bool IsPrimaryMarker(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetInt32(out var number) && number == 1,
        JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() is "1" or "true",
        _ => false
    };

    private static List<(DateTime Time, double Value)> ParseRawPoints(string json)
    {
        var output = new List<(DateTime, double)>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("histdata", out var rows) || rows.ValueKind != JsonValueKind.Array) return output;
        foreach (var row in rows.EnumerateArray())
        {
            if (!TryRaw(row, out var value)) continue;
            if (!TryResolveHistoricTimestamp(row, out var time)) continue;
            output.Add((time, value));
        }
        return output;
    }

    // Match PrtgFetchService semantics: prefer the localized display interval's start, validate
    // against datetime_raw (PRTG OLE Automation date), and fall back to that OA value if needed.
    private static bool TryResolveHistoricTimestamp(JsonElement row, out DateTime utc)
    {
        utc = default;
        DateTime? oa = null;
        if (row.TryGetProperty("datetime_raw", out var raw))
        {
            double? serial = raw.ValueKind == JsonValueKind.Number && raw.TryGetDouble(out var numeric) ? numeric :
                raw.ValueKind == JsonValueKind.String && double.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out numeric) ? numeric : null;
            if (serial.HasValue)
            {
                try { oa = DateTime.FromOADate(serial.Value); }
                catch (ArgumentException) { }
            }
        }
        var display = Text(row, "datetime");
        if (!string.IsNullOrWhiteSpace(display))
        {
            var cut = display.IndexOf(" - ", StringComparison.Ordinal);
            var head = (cut >= 0 ? display[..cut] : display).Trim();
            foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
            {
                if (!DateTime.TryParse(head, culture, DateTimeStyles.None, out var parsed)) continue;
                if (oa.HasValue && Math.Abs((parsed - oa.Value).TotalHours) > 24) continue;
                utc = DateTime.SpecifyKind(parsed, DateTimeKind.Local).ToUniversalTime();
                return true;
            }
        }
        if (oa.HasValue)
        {
            utc = DateTime.SpecifyKind(oa.Value, DateTimeKind.Local).ToUniversalTime();
            return true;
        }
        return false;
    }

    private static bool? Compare(List<(DateTime Time, double Value)> raw, PrtgDiskSemanticPersistedPoint[] points)
    {
        if (points.Length == 0) return null;
        foreach (var point in points)
        {
            var match = raw.FirstOrDefault(x => Math.Abs((x.Time - point.TimestampUtc).TotalMinutes) <= 60);
            if (match == default || Math.Abs(match.Value - point.Value) > Math.Max(0.01, Math.Abs(point.Value) * 0.001)) return false;
        }
        return true;
    }

    private static bool TryRaw(JsonElement row, out double value)
    {
        value = 0;
        if (!row.TryGetProperty("value_raw", out var raw)) return false;
        if (raw.ValueKind == JsonValueKind.Array) raw = raw.EnumerateArray().FirstOrDefault();
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
