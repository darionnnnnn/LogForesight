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

        // 3. 不像主機名稱的值（打壞的 IPv4、含空白或備註）不送 DNS，直接視為解析不到。
        //    沒付 IO 所以不寫快取。
        if (!PrtgAddress.IsDnsCandidate(token))
            return null;

        // 4. 查實例快取（含失敗結果）
        if (_cache.TryGetValue(token, out var cached))
            return cached;

        // 5. DNS 解析，取第一個 IPv4 位址
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

    /// <summary>DNS 解析逾時。來源位址只有幾筆、裝置側已由候選判定與守門預算減量，1 秒足夠。</summary>
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 對主機名稱進行 DNS 解析（帶逾時保護，只查 IPv4）。
    /// 理由：Sentinel 常以 DNS 名稱設定、PRTG device 常填 IPv4，純字串比對會全數落空。
    /// 用 <see cref="Dns.GetHostAddressesAsync(string, AddressFamily, CancellationToken)"/> 加逾時取消，
    /// 而不是 <c>Task.Run + Wait</c>：後者逾時後那個 task 沒人回收、執行緒繼續卡在 OS 解析，
    /// 而且例外在 lambda 內擲出會讓偵錯器以「使用者未處理」中斷。
    /// 介面維持同步簽章（改 async 連動主機對應與守門全部呼叫端，見 docs/BACKLOG.md），
    /// 這裡同步等待非同步結果；解析失敗或逾時一律視為找不到，不擲例外。
    /// </summary>
    private static IPAddress[] DnsLookupWithTimeout(string host)
    {
        try
        {
            using var cts = new CancellationTokenSource(DnsTimeout);
            return Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token)
                .GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return Array.Empty<IPAddress>();
        }
    }
}
