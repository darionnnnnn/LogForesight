using System.Net;

namespace LogForesight.Core.Models;

/// <summary>
/// 「這個字串是不是 IP」的單一入口。<c>IPAddress.TryParse</c> 會把簡寫當成合法 IPv4
/// （"10.1" 解成 10.0.0.1、"12345" 解成 0.0.48.57、"0x7f.1" 解成 127.0.0.1），
/// 自由輸入的位址若直接交給它，短名稱或打錯的值會被錯認成另一台主機。
/// </summary>
public static class IpSyntax
{
    /// <summary>完整四段十進位 IPv4 且每段 0~255（前後空白先 Trim）。刻意不接受簡寫、十六進位或前綴——
    /// 呼叫端用它判斷「這條目是不是一台明確的主機」，也用它濾掉進不了 Lucene 子句的位址，寬鬆一點就會
    /// 把版本號、序號之類的字串當成 IP 收進來。</summary>
    public static bool IsStrictIpv4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var octets = value.Trim().Split('.');
        return octets.Length == 4 &&
               octets.All(o => o.Length is >= 1 and <= 3 && o.All(char.IsAsciiDigit) && int.TryParse(o, out var v) && v is >= 0 and <= 255);
    }

    /// <summary>
    /// 嚴格版的 <c>IPAddress.TryParse</c>：含 ':' 視為 IPv6 交給框架判定；否則必須先通過
    /// <see cref="IsStrictIpv4"/> 才解析，簡寫與十六進位寫法一律回 false。
    /// </summary>
    public static bool TryParse(string? value, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var s = value.Trim();
        if (!s.Contains(':') && !IsStrictIpv4(s)) return false;

        if (!IPAddress.TryParse(s, out var parsed)) return false;
        address = parsed;
        return true;
    }
}
