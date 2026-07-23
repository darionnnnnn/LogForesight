using System.Globalization;

namespace LogForesight;

/// <summary>解析後的 IPv4 網段：以「網路位址 ＋ 遮罩」表示，比對只是一次位元 AND。</summary>
public sealed class CidrRange
{
    public uint Network { get; init; }
    public uint Mask { get; init; }

    /// <summary>前綴長度（0~32），供顯示用</summary>
    public int PrefixLength { get; init; }
}

/// <summary>
/// IPv4 網段比對（docs/SCALE-2000-PLAN.md §3）。支援三種輸入：
///   - CIDR：<c>10.1.2.0/24</c>
///   - 萬用字元（只允許尾端連續段）：<c>10.1.2.*</c>（＝/24）、<c>10.1.*</c>（＝/16）
///   - 單一 IP：<c>10.1.2.15</c>（＝/32）
///
/// 純函數、無狀態，邊界由單元測試釘死。IPv4 only——機房環境無 IPv6 需求，
/// 支援它要另一套解析與遮罩運算，複雜度不成比例（同 CsvParser 不支援跨行引號的取捨）。
/// </summary>
public static class CidrMatcher
{
    /// <summary>解析網段字串；格式非法回 null（呼叫端轉成可顯示的驗證錯誤）。</summary>
    public static CidrRange? Parse(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        pattern = pattern.Trim();

        if (pattern.Contains('/')) return ParseCidr(pattern);
        if (pattern.Contains('*')) return ParseWildcard(pattern);
        return ParseSingle(pattern);
    }

    /// <summary>IP 是否落在網段內。IP 本身格式非法時回 false（不是命中）。</summary>
    public static bool Matches(CidrRange range, string? ipAddress)
    {
        if (!TryParseIp(ipAddress, out var ip)) return false;
        return (ip & range.Mask) == range.Network;
    }

    private static CidrRange? ParseCidr(string pattern)
    {
        var parts = pattern.Split('/');
        if (parts.Length != 2) return null;
        if (!TryParseIp(parts[0], out var ip)) return null;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)) return null;
        if (prefix < 0 || prefix > 32) return null;

        var mask = MaskFromPrefix(prefix);
        return new CidrRange { Network = ip & mask, Mask = mask, PrefixLength = prefix };
    }

    private static CidrRange? ParseWildcard(string pattern)
    {
        var parts = pattern.Split('.');
        if (parts.Length == 0 || parts.Length > 4) return null;

        // 找第一個 '*'：它之前必須是合法八位元組，它與之後必須全是 '*'（只允許尾端連續萬用）
        var firstStar = Array.IndexOf(parts, "*");
        if (firstStar < 0) return null;                 // 沒有 '*'（理論上不會進來）
        if (firstStar == 0) return null;                // 「*」或「*.*」沒有固定前綴，不接受

        for (var i = firstStar; i < parts.Length; i++)
            if (parts[i] != "*") return null;           // 非尾端的萬用（如 10.*.2.*）不接受

        uint ip = 0;
        for (var i = 0; i < firstStar; i++)
        {
            if (!TryParseOctet(parts[i], out var octet)) return null;
            ip |= (uint)octet << (8 * (3 - i));
        }

        var prefix = firstStar * 8;
        var mask = MaskFromPrefix(prefix);
        return new CidrRange { Network = ip & mask, Mask = mask, PrefixLength = prefix };
    }

    private static CidrRange? ParseSingle(string pattern)
    {
        if (!TryParseIp(pattern, out var ip)) return null;
        return new CidrRange { Network = ip, Mask = 0xFFFFFFFF, PrefixLength = 32 };
    }

    private static uint MaskFromPrefix(int prefix) =>
        prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);

    private static bool TryParseIp(string? value, out uint result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var octets = value.Trim().Split('.');
        if (octets.Length != 4) return false;

        for (var i = 0; i < 4; i++)
        {
            if (!TryParseOctet(octets[i], out var octet)) return false;
            result |= (uint)octet << (8 * (3 - i));
        }
        return true;
    }

    /// <summary>八位元組：0~255，拒收前導零（"01"）與非數字——寬鬆解析會讓打錯的網段靜默命中錯的範圍。</summary>
    private static bool TryParseOctet(string value, out int result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Length > 1 && value[0] == '0') return false;   // 前導零
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)) return false;
        return result is >= 0 and <= 255;
    }
}
