using System.Net;
using System.Net.Sockets;

namespace LogForesight.Core.Service;

/// <summary>把位址字串解析成可比對的 IP 字串；無法解析時回 null。</summary>
public interface IPrtgAddressResolver
{
    /// <summary>把位址字串解析成可比對的 IP 字串；無法解析時回 null。</summary>
    string? Resolve(string? value);
}

/// <summary>
/// 位址解析器：先嘗試純語法正規化（<see cref="PrtgAddress.Normalize"/>），
/// 若輸入不是合法 IP 再進行 DNS 解析取第一個 IPv4 位址。
/// 同一個實例內相同 token 只解析一次（含失敗結果），避免逾時位址重複付 2 秒。
/// </summary>
public sealed class PrtgAddressResolver : IPrtgAddressResolver
{
    private readonly Func<string, IPAddress[]> _dnsLookup;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>正式建構子，使用真正的 DNS 解析。</summary>
    public PrtgAddressResolver() : this(DnsLookupWithTimeout)
    {
    }

    /// <summary>
    /// 可注入假 DNS 的建構子，僅供測試使用。
    /// </summary>
    internal PrtgAddressResolver(Func<string, IPAddress[]> dnsLookup)
    {
        _dnsLookup = dnsLookup;
    }

    /// <inheritdoc/>
    public string? Resolve(string? value)
    {
        // 1. 先嘗試純語法正規化：合法 IP 直接回傳，不呼叫 DNS
        var normalized = PrtgAddress.Normalize(value);
        if (normalized != null)
            return normalized;

        // 2. 取主機名稱 token（去 scheme／路徑／port 後轉小寫）
        var token = PrtgAddress.HostToken(value);
        if (token == null)
            return null;

        // 3. 查實例快取（含失敗結果）
        if (_cache.TryGetValue(token, out var cached))
            return cached;

        // 4. DNS 解析，取第一個 IPv4 位址
        string? result = null;
        try
        {
            var addresses = _dnsLookup(token);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 != null)
                result = PrtgAddress.Normalize(ipv4.ToString());
        }
        catch
        {
            // DNS 解析失敗 → result 維持 null
        }

        _cache[token] = result;
        return result;
    }

    /// <summary>
    /// 對主機名稱進行 DNS 解析（帶 2 秒逾時保護）。
    /// 理由：Sentinel 常以 DNS 名稱設定、PRTG device 常填 IPv4，純字串比對會全數落空。
    /// 為避免解析不到的主機或網路問題拖慢整段批次偵測，使用 Task.Run 加上逾時保護。
    /// 解析失敗或逾時一律視為找不到，不擲例外。
    /// </summary>
    private static IPAddress[] DnsLookupWithTimeout(string host)
    {
        try
        {
            var task = Task.Run(() => Dns.GetHostAddresses(host));
            if (task.Wait(2000))
            {
                return task.Result;
            }
            return Array.Empty<IPAddress>();
        }
        catch (Exception)
        {
            return Array.Empty<IPAddress>();
        }
    }
}
