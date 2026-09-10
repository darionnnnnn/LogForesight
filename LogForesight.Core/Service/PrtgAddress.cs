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

    /// <summary>
    /// 判定 <see cref="HostToken"/> 的結果是否「值得送 DNS 解析」。純語法、無 IO。
    /// 只有長得像主機名稱的值才回 true：英數、<c>-</c>、<c>_</c>、<c>.</c>，總長 ≤ 253，
    /// 每段 1–63 字且不以 <c>-</c> 開頭或結尾。
    /// 另外把「壞掉的 IPv4」擋掉：整串沒有任何字母（<c>10.2.3.256</c>、<c>10.2.3.4.5</c>），
    /// 或第一段全數字**且每一段都不超過 3 字**（<c>10.2xx.x.x</c>、<c>10.20.3x.4</c>）——
    /// 這些是打錯的 IP，不是主機名稱，送 DNS 只會白付一次逾時。
    /// 「每段 ≤ 3 字」這個條件是為了不誤擋 <c>1.dc.corp.local</c>、<c>0.pool.ntp.org</c>
    /// 這種第一段是數字的真 FQDN。結尾單一個點（root-qualified，<c>srv.corp.local.</c>）先去掉再判。
    /// 只接受 ASCII：非 ASCII（IDN）名稱不送 DNS，字面比對仍可命中（docs/PRTG-SPEC.md §4）。
    /// 合法 IP 早在 <see cref="Normalize"/> 通過，不會走到這裡。
    /// </summary>
    public static bool IsDnsCandidate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var s = token.Trim();
        if (s.EndsWith('.')) s = s[..^1];   // root-qualified FQDN；第二個點會在下面的空 label 檢查被擋
        if (s.Length is 0 or > 253) return false;
        if (s.Contains(':')) return false;

        var hasLetter = false;
        foreach (var c in s)
        {
            if (char.IsAsciiLetter(c)) hasLetter = true;
            else if (!char.IsAsciiDigit(c) && c != '-' && c != '_' && c != '.') return false;
        }
        if (!hasLetter) return false;

        var labels = s.Split('.');
        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            if (label.Length is 0 or > 63) return false;
            if (label[0] == '-' || label[^1] == '-') return false;
        }

        // 第一段全數字且每段都很短：那是打壞的 IPv4（10.2xx.x.x），不是主機名稱。
        // 真 FQDN 至少會有一段像 corp／local／org 這種超過 3 字的字，靠這點分開。
        var firstAllDigits = true;
        foreach (var c in labels[0])
        {
            if (!char.IsAsciiDigit(c)) { firstAllDigits = false; break; }
        }
        if (!firstAllDigits) return true;

        foreach (var label in labels)
        {
            if (label.Length > 3) return true;
        }
        return false;
    }
}
