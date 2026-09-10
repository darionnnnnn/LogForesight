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

        // root-qualified FQDN（srv.corp.local.）去掉單一個結尾點：這裡是三層（純語法、解析、字面比對）
        // 共同的入口，只在判定層剝會讓「srv.corp.local」與「srv.corp.local.」在快取與預算裡變成兩把鍵。
        if (s.Length > 1 && s.EndsWith('.'))
            s = s[..^1];

        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// 判定 <see cref="HostToken"/> 的結果是否「值得送 DNS 解析」。純語法、無 IO。
    /// 只有長得像主機名稱的值才回 true：英數、<c>-</c>、<c>_</c>、<c>.</c>，總長 ≤ 253，
    /// 每段 1–63 字且不以 <c>-</c> 開頭或結尾。
    /// 另外把「壞掉的 IPv4」擋掉：整串沒有任何字母（<c>10.2.3.256</c>、<c>10.2.3.4.5</c>），
    /// 或第一段全數字**且每一段都只含數字或佔位字 x**（<c>10.2xx.x.x</c>、<c>10.20.3x.4</c>、
    /// <c>192.168.1.100x</c>）——這些是打錯或用 x 遮掉的 IP，不是主機名稱，送 DNS 只會白付一次逾時。
    /// 條件刻意收得很窄：<c>163.com</c>、<c>104.com.tw</c>、<c>1.dc.hq.tw</c> 這種第一段是數字的真網域
    /// 都有非 x 的字母，照常送 DNS；<c>10.2.3.4-old</c> 這種也放行（付一次逾時、失敗有快取），
    /// 寧可多查一次也不誤擋真主機。結尾點由 <see cref="HostToken"/> 統一去掉，這裡再防一次給直接呼叫者。
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

        // 第一段全數字且每段只有數字或 x：那是打壞或用 x 遮掉的 IPv4（10.2xx.x.x），不是主機名稱。
        var firstAllDigits = true;
        foreach (var c in labels[0])
        {
            if (!char.IsAsciiDigit(c)) { firstAllDigits = false; break; }
        }
        if (!firstAllDigits) return true;

        foreach (var label in labels)
        {
            foreach (var c in label)
            {
                if (!char.IsAsciiDigit(c) && c != 'x' && c != 'X') return true;   // 有別的字母：當主機名稱
            }
        }
        return false;
    }
}
