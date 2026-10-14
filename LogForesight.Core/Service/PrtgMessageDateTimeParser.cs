using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LogForesight.Core.Service;

/// <summary>Strict parser for the PRTG account's configured message date and clock format.</summary>
public static class PrtgMessageDateTimeParser
{
    public const string Dmy24 = "dmy-24";
    public const string Dmy12 = "dmy-12";
    public const string Mdy24 = "mdy-24";
    public const string Mdy12 = "mdy-12";
    public const string Ymd24 = "ymd-24";
    public const string Ymd12 = "ymd-12";
    public const string ParserVersion = "prtg-message-datetime-v1";

    private static readonly IReadOnlyDictionary<string, string[]> DatePatterns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Dmy24] = ["d.M.yyyy", "dd.MM.yyyy"],
            [Dmy12] = ["d.M.yyyy", "dd.MM.yyyy"],
            [Mdy24] = ["M/d/yyyy", "MM/dd/yyyy"],
            [Mdy12] = ["M/d/yyyy", "MM/dd/yyyy"],
            [Ymd24] = ["yyyy-MM-dd"],
            [Ymd12] = ["yyyy-MM-dd"]
        };

    public static IReadOnlyList<string> SupportedFormats { get; } =
        [Dmy24, Dmy12, Mdy24, Mdy12, Ymd24, Ymd12];

    public static bool IsSupported(string? format) =>
        format is not null && DatePatterns.ContainsKey(format);

    /// <summary>
    /// Parses a full date and time using only the configured order/clock convention.
    /// Offset-bearing strings are accepted only when the stated offset agrees with the
    /// configured source zone; ambiguous local wall times require an explicit offset.
    /// </summary>
    public static bool TryParse(string? text, string? format, CultureInfo culture, TimeZoneInfo sourceZone,
        out DateTimeOffset timestamp, out string reason)
    {
        timestamp = default;
        reason = "message-time-unverified";
        if (string.IsNullOrWhiteSpace(text)) { reason = "message-time-missing"; return false; }
        if (!IsSupported(format)) { reason = "message-date-format-unconfigured"; return false; }

        var datePatterns = DatePatterns[format!];
        var clockPatterns = format!.EndsWith("-24", StringComparison.Ordinal)
            ? new[] { "H:mm:ss", "HH:mm:ss", "H:mm", "HH:mm" }
            : new[] { "h:mm:ss tt", "hh:mm:ss tt", "h:mm tt", "hh:mm tt" };
        var wallPatterns = datePatterns.SelectMany(date => clockPatterns.Select(clock => $"{date} {clock}"))
            .Distinct(StringComparer.Ordinal).ToArray();

        if (HasExplicitOffset(text))
        {
            var raw = text.Trim();
            DateTimeOffset withOffset;
            if (raw.EndsWith('Z') || raw.EndsWith('z'))
            {
                if (!DateTime.TryParseExact(raw[..^1].TrimEnd(), wallPatterns, culture,
                        DateTimeStyles.AllowWhiteSpaces, out var utcWall)) return false;
                withOffset = new DateTimeOffset(DateTime.SpecifyKind(utcWall, DateTimeKind.Unspecified), TimeSpan.Zero);
            }
            else
            {
                var offsetPatterns = wallPatterns.Select(pattern => pattern + " zzz").ToArray();
                if (!DateTimeOffset.TryParseExact(raw, offsetPatterns, culture,
                        DateTimeStyles.AllowWhiteSpaces, out withOffset)) return false;
            }
            var local = DateTime.SpecifyKind(withOffset.DateTime, DateTimeKind.Unspecified);
            if (sourceZone.IsInvalidTime(local)) { reason = "source-time-zone-gap"; return false; }
            if (sourceZone.IsAmbiguousTime(local))
            {
                if (!sourceZone.GetAmbiguousTimeOffsets(local).Contains(withOffset.Offset))
                { reason = "source-time-zone-offset-mismatch"; return false; }
            }
            else if (sourceZone.GetUtcOffset(local) != withOffset.Offset)
            { reason = "source-time-zone-offset-mismatch"; return false; }
            timestamp = withOffset;
            reason = "verified";
            return true;
        }

        if (!DateTime.TryParseExact(text.Trim(), wallPatterns, culture,
                DateTimeStyles.AllowWhiteSpaces, out var wallTime))
            return false;
        wallTime = DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified);
        if (sourceZone.IsInvalidTime(wallTime)) { reason = "source-time-zone-gap"; return false; }
        if (sourceZone.IsAmbiguousTime(wallTime)) { reason = "source-time-zone-fold"; return false; }
        timestamp = new DateTimeOffset(wallTime, sourceZone.GetUtcOffset(wallTime));
        reason = "verified";
        return true;
    }

    public static string BuildFingerprint(PrtgMonitoringPolicy policy, string url)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MessageTimeAuthorityRevision <= 0 || !policy.Ready(url) ||
            !IsSupported(policy.MessageDateFormat) || string.IsNullOrWhiteSpace(policy.SourceTimeZoneId) ||
            string.IsNullOrWhiteSpace(policy.SourceCultureName) || policy.SourceGeneration.Length > 256 ||
            policy.EndpointHint.Length > 256 || policy.SourceTimeZoneId.Length > 128 ||
            policy.SourceCultureName.Length > 128)
            return string.Empty;
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId);
            _ = CultureInfo.GetCultureInfo(policy.SourceCultureName);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { return string.Empty; }

        // This is deliberately independent of raw-OA and analysis-time authority.
        var components = new[]
        {
            ParserVersion, policy.MessageDateFormat, policy.SourceTimeZoneId,
            policy.SourceCultureName, policy.SourceGeneration, policy.EndpointHint,
            policy.MessageTimeAuthorityRevision.ToString(CultureInfo.InvariantCulture)
        };
        var material = string.Concat(components.Select(component =>
            component.Length.ToString(CultureInfo.InvariantCulture) + ":" + component));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static bool HasExplicitOffset(string text)
    {
        var value = text.Trim();
        return value.EndsWith('Z') || value.EndsWith('z') ||
            value.Length >= 6 && (value[^6] == '+' || value[^6] == '-') &&
            char.IsDigit(value[^5]) && char.IsDigit(value[^4]) && value[^3] == ':' &&
            char.IsDigit(value[^2]) && char.IsDigit(value[^1]);
    }
}
