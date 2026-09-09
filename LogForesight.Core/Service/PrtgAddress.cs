using System.Net;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 位址的純語法正規化工具。不做 DNS 解析，不做任何 IO。
/// </summary>
public static class PrtgAddress
{
    /// <summary>
    /// 將位址字串正規化為純 IP 表示。去除 scheme、路徑、port，
    /// 再透過 <see cref="IPAddress.TryParse"/> 消除前導零與大小寫差異。
    /// 若輸入不是合法 IP 則回傳 null。
    /// </summary>
    public static string? Normalize(string? value)
    {
        var s = HostToken(value);
        if (s == null) return null;

        // 若看起來像 IPv4（含 '.' 且不含 ':'），去除各段前導零以避免八進位解讀
        if (s.Contains('.') && !s.Contains(':'))
        {
            var parts = s.Split('.');
            for (var i = 0; i < parts.Length; i++)
                parts[i] = parts[i].TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0";
            s = string.Join('.', parts);
        }

        // 透過 IPAddress.TryParse 正規化
        if (IPAddress.TryParse(s, out var parsed))
            return parsed.ToString();

        return null;
    }

    /// <summary>
    /// 取出位址字串的「主機部分」：去除 scheme、路徑與 port，轉小寫。
    /// 結果可能是 IP，也可能是 DNS 名稱——<see cref="Normalize"/> 在此之上再要求必須是合法 IP。
    /// 沒有可用內容時回傳 null。
    /// </summary>
    public static string? HostToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var s = value.Trim();

        // 去掉 http:// 或 https:// 前綴
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            s = s[7..];
        else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = s[8..];

        // 去掉第一個 / 及其之後的所有字元（路徑與 query）
        var slashIndex = s.IndexOf('/');
        if (slashIndex >= 0)
            s = s[..slashIndex];

        // 拆掉 port
        if (s.StartsWith('[') && s.Contains(']'))
        {
            // IPv6 中括號格式：[::1]:8080 → ::1
            var closeBracket = s.IndexOf(']');
            s = s[1..closeBracket];
        }
        else
        {
            // 只有在 ':' 出現次數剛好為 1 時才拆 port（避免拆裸 IPv6）
            var colonCount = 0;
            foreach (var c in s)
            {
                if (c == ':') colonCount++;
            }

            if (colonCount == 1)
            {
                var colonIndex = s.IndexOf(':');
                s = s[..colonIndex];
            }
        }

        s = s.Trim().ToLowerInvariant();
        return s.Length == 0 ? null : s;
    }
}
