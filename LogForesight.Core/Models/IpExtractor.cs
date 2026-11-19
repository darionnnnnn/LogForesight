using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace LogForesight.Core.Models;

/// <summary>
/// 「從文字裡抽出 IP」的單一入口（IPv4 與 IPv6），與 <see cref="IpSyntax"/> 同一套判斷：
/// 是不是位址一律由 <see cref="IpSyntax"/> 決定，這裡只負責找候選字串與統一寫法。
/// 寫法規則：IPv4 保留原字串（已通過 <see cref="IpSyntax.IsStrictIpv4"/>，與歷史紀錄逐字相同）；
/// <c>::ffff:a.b.c.d</c> 映射位址還原成 IPv4 本體（與同一台主機的 IPv4 紀錄可比對）；
/// 其餘 IPv6 為小寫壓縮標準寫法，並去掉區域識別（<c>%eth0</c>）。
/// 抽不到、或是 <c>999.1.1.1</c> 這類不合法的字串一律不當成位址。
/// </summary>
public static class IpExtractor
{
    /// <summary>供 sshd 等「from &lt;位址&gt; port」樣式使用的位址詞元：取到的字串要再經 <see cref="Normalize"/> 判定。</summary>
    public const string TokenPattern = @"\S+";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // 與歷史 IPv4 樣式相同（\b 邊界），差別只在取到後多過一道 IsStrictIpv4（擋 999.1.1.1）。
    private static readonly Regex Ipv4Candidate = new(
        @"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled, RegexTimeout);

    // IPv6 候選：十六進位、冒號、點（內嵌 IPv4）組成的連續字串，至少兩個冒號。
    // 前後不得緊鄰英數字、底線或冒號——擋掉 Foo::Bar、Class::Add 這類程式識別字被當成 ::ba 之類的位址。
    // 候選一律再過 IpSyntax.TryParse，12:30:45.123 這類時間字串在那裡被剔除。
    private static readonly Regex Ipv6Candidate = new(
        @"(?<![0-9A-Za-z_:.])(?=[0-9A-Fa-f.]*:[0-9A-Fa-f.]*:)[0-9A-Fa-f:.]+(?:%[0-9A-Za-z_.\-]+)?(?![0-9A-Za-z_])",
        RegexOptions.Compiled, RegexTimeout);

    /// <summary>單一詞元轉成統一寫法；不是位址回 null（簡寫、十六進位、超出範圍的 IPv4 都不是）。</summary>
    public static string? Normalize(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var s = token.Trim();

        if (IpSyntax.IsStrictIpv4(s)) return s;
        if (!s.Contains(':')) return null;

        var zone = s.IndexOf('%');
        if (zone >= 0) s = s[..zone];

        if (!IpSyntax.TryParse(s, out var address) || address is null) return null;
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return null;

        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4().ToString();
        return address.ToString();
    }

    /// <summary>文字中所有位址（依出現順序，可重複）；內嵌在 IPv6 裡的 IPv4 不另外算一筆。</summary>
    public static List<string> ExtractAll(string? text)
    {
        var result = new List<(int Index, string Value)>();
        if (string.IsNullOrEmpty(text)) return new List<string>();

        var v6Spans = new List<(int Start, int End)>();
        foreach (Match m in Ipv6Candidate.Matches(text))
        {
            var normalized = NormalizeCandidate(m.Value);
            if (normalized == null) continue;
            v6Spans.Add((m.Index, m.Index + m.Length));
            result.Add((m.Index, normalized));
        }

        foreach (Match m in Ipv4Candidate.Matches(text))
        {
            if (v6Spans.Any(s => m.Index >= s.Start && m.Index < s.End)) continue;
            if (!IpSyntax.IsStrictIpv4(m.Value)) continue;
            result.Add((m.Index, m.Value));
        }

        return result.OrderBy(r => r.Index).Select(r => r.Value).ToList();
    }

    /// <summary>文字中第一個位址；沒有回 null。</summary>
    public static string? First(string? text) => ExtractAll(text).FirstOrDefault();

    // 候選可能黏著句尾標點（「…::1.」「…db8::1:」）：整串不合法時逐步剝掉頭尾單一冒號／句點再試。
    private static string? NormalizeCandidate(string candidate)
    {
        var s = candidate;
        for (var i = 0; i < 3 && s.Length > 0; i++)
        {
            var normalized = Normalize(s);
            if (normalized != null) return normalized;

            if (s.EndsWith('.') || (s.EndsWith(':') && !s.EndsWith("::", StringComparison.Ordinal))) s = s[..^1];
            else if (s.StartsWith(':') && !s.StartsWith("::", StringComparison.Ordinal)) s = s[1..];
            else break;
        }

        return null;
    }
}
