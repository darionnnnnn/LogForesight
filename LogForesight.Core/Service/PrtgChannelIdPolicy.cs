using System.Globalization;

namespace LogForesight.Core.Service;

/// <summary>Validates canonical PRTG channel identifiers without conflating them with sensor object IDs.</summary>
public static class PrtgChannelIdPolicy
{
    /// <summary>PRTG's documented negative channel identifiers: traffic sum, internal markers, and downtime.</summary>
    public static bool IsAllowed(long id) => id >= 0 || id is -1 or -2 or -3 or -4;

    /// <summary>Accepts only canonical decimal spellings, so aliases cannot collide in identity comparisons.</summary>
    public static bool TryParseCanonical(string? text, out long id)
    {
        id = default;
        if (string.IsNullOrEmpty(text) || text.Length > 20 ||
            !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id) ||
            !IsAllowed(id))
            return false;
        return string.Equals(text, id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
