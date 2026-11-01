using System.Collections.Concurrent;
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
///
/// Web 端以全站單例注入（<c>AddSingleton&lt;PrtgAddressResolver&gt;</c>），快取跨請求共用；夜間管線、探測、守門各自每趟自建，逐趟快取。
/// 快取有到期時間（由注入的 <see cref="TimeProvider"/> 判斷）：
/// 解析成功正快取 <see cref="PositiveTtl"/>（5 分鐘）——DNS 記錄會變，單例不能永遠記住；
/// DNS 明確查無（例外或零筆 IPv4）負快取 <see cref="NegativeTtl"/>（60 秒）——避免同一個壞位址每次請求都重付查詢成本，
/// 又能讓使用者修好 DNS 後很快生效；**逾時不寫快取**——逾時多半是 DNS 伺服器暫時不通而不是名稱不存在，
/// 記住會把暫時性故障放大成一分鐘的誤判，下一次直接重查即可。
/// 條目上限 <see cref="MaxEntries"/>：滿了先清掉已過期的條目，仍滿才不寫入新項（不清空未過期的，仍照常回傳解析結果），避免亂值灌爆記憶體。
/// 使用者觸發「同步結構與對應」時呼叫 <see cref="Invalidate"/> 清空，讓對應用到最新的 DNS 結果。
/// </summary>
public sealed class PrtgAddressResolver : IPrtgAddressResolver
{
    /// <summary>解析成功的快取時間</summary>
    internal static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(5);

    /// <summary>查無結果的負快取時間</summary>
    internal static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(60);

    /// <summary>快取條目上限</summary>
    internal const int MaxEntries = 10_000;

    private readonly Func<string, IPAddress[]?> _dnsLookup;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, (string? Value, DateTimeOffset ExpiresAt)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>快取世代：Invalidate 時遞增；清空前就開始的 DNS 查詢回來後不得把修好前的結果寫回</summary>
    private int _generation;

    /// <summary>正式建構子，使用真正的 DNS 解析與系統時鐘。</summary>
    public PrtgAddressResolver() : this(SystemDnsAsync, TimeProvider.System)
    {
    }

    /// <summary>
    /// 逾時保護路徑的注入點：非同步 DNS 查詢與逾時計時器的時間來源皆必填。
    /// 測試以替身 DNS（卡到被取消為止）與手動時鐘驗證「逾時即返回、不擲例外」，不依賴真實 DNS 與真實等待。
    /// </summary>
    internal PrtgAddressResolver(Func<string, CancellationToken, Task<IPAddress[]>> dnsAsync, TimeProvider timeProvider)
        : this(host => DnsLookupWithTimeout(dnsAsync, timeProvider, host), timeProvider)
    {
    }

    /// <summary>
    /// 可注入假 DNS 的建構子，僅供測試使用。替身回傳 null 代表逾時（不寫快取）。
    /// </summary>
    internal PrtgAddressResolver(Func<string, IPAddress[]?> dnsLookup) : this(dnsLookup, TimeProvider.System)
    {
    }

    /// <summary>可注入假 DNS 與時鐘的建構子，僅供測試使用（驗證快取到期）。</summary>
    internal PrtgAddressResolver(Func<string, IPAddress[]?> dnsLookup, TimeProvider timeProvider)
    {
        _dnsLookup = dnsLookup;
        _timeProvider = timeProvider;
    }

    /// <summary>清空快取（使用者觸發同步結構與對應時呼叫）。只在具體類別，不屬於介面。</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _cache.Clear();
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

        // 4. 查快取（含負快取）；到期的條目視為不存在
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(token, out var cached) && cached.ExpiresAt > now)
            return cached.Value;

        // 5. DNS 解析，取第一個 IPv4 位址（記下開始時的世代，查詢期間被 Invalidate 就不寫回）
        var generation = Volatile.Read(ref _generation);
        string? result = null;
        try
        {
            var addresses = _dnsLookup(token);
            if (addresses == null)
                return null; // 逾時：不寫快取，下一次立即重查

            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 != null)
                result = PrtgAddress.Normalize(ipv4.ToString());
        }
        catch
        {
            // DNS 解析失敗 → result 維持 null（視為查無，負快取）
        }

        if (generation == Volatile.Read(ref _generation))
            Store(token, result, now + (result != null ? PositiveTtl : NegativeTtl));
        return result;
    }

    /// <summary>寫入快取；已滿時先清掉過期條目，仍滿才不寫入新項（既有項目仍可被覆寫更新）。
    /// 不清過期項的話，單例被灌滿一次後快取就永久停收新名稱、退化成每次查 DNS。</summary>
    private void Store(string token, string? value, DateTimeOffset expiresAt)
    {
        if (_cache.Count >= MaxEntries && !_cache.ContainsKey(token))
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var entry in _cache)
                if (entry.Value.ExpiresAt <= now) _cache.TryRemove(entry.Key, out _);
            if (_cache.Count >= MaxEntries) return;
        }
        _cache[token] = (value, expiresAt);
    }

    /// <summary>DNS 解析逾時。來源位址只有幾筆、裝置側已由候選判定與守門預算減量，1 秒足夠。</summary>
    internal static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(1);

    private static Task<IPAddress[]> SystemDnsAsync(string host, CancellationToken ct) =>
        Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);

    /// <summary>
    /// 對主機名稱進行 DNS 解析（帶逾時保護，只查 IPv4）。
    /// 理由：Sentinel 常以 DNS 名稱設定、PRTG device 常填 IPv4，純字串比對會全數落空。
    /// 用 <see cref="Dns.GetHostAddressesAsync(string, AddressFamily, CancellationToken)"/> 加逾時取消，
    /// 而不是 <c>Task.Run + Wait</c>：後者逾時後那個 task 沒人回收、執行緒繼續卡在 OS 解析，
    /// 而且例外在 lambda 內擲出會讓偵錯器以「使用者未處理」中斷。
    /// 介面維持同步簽章（改 async 連動主機對應與守門全部呼叫端，見 docs/BACKLOG.md），
    /// 這裡同步等待非同步結果；解析失敗或逾時一律視為找不到，不擲例外；逾時回 null 以便與「查無」區分（逾時不快取）。
    /// </summary>
    private static IPAddress[]? DnsLookupWithTimeout(
        Func<string, CancellationToken, Task<IPAddress[]>> dnsAsync, TimeProvider timeProvider, string host)
    {
        try
        {
            using var cts = new CancellationTokenSource(DnsTimeout, timeProvider);
            return dnsAsync(host, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null; // 逾時：回 null 讓呼叫端不寫快取
        }
        catch (Exception)
        {
            return Array.Empty<IPAddress>(); // 明確失敗（查無）
        }
    }
}
