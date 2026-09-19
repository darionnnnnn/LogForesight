using System.Net;

namespace LogForesight.Web.Auth;

/// <summary>被暫停的對象（健康頁顯示用）。Kind：account＝帳號、ip＝來源 IP</summary>
public record LoginThrottleEntry(string Key, string Kind, DateTime BlockedUntil);

/// <summary>
/// 登入節流：以「帳號」與「來源 IP」兩個維度各自用滑動窗口計算失敗次數，超過門檻就暫停一段時間。
///
/// 目的：一般 AD 帳號原本沒有任何失敗計數，未認證者可以做密碼噴灑，也能拿來把整批員工的
/// AD 帳號鎖出網域（每次失敗都同步打 AD 並寫稽核）。暫停期間登入端點不呼叫 AD。
///
/// 狀態只存在記憶體，站台重啟即清空——這是可接受的取捨：重啟需要伺服器權限，
/// 不是攻擊者繞過節流的實際路徑；寫進資料庫反而讓每次失敗登入多一趟 DB 寫入。
///
/// 迴路位址與未知來源（null）不計入 IP 維度：本機反向代理未設定 TrustedProxies 時
/// 所有人都會是 127.0.0.1，計入的話一個人打錯密碼就會連坐全站。
/// 時間一律由呼叫端傳入，測試可控。
/// </summary>
public class LoginThrottle
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(5);
    public const int AccountThreshold = 10;
    public const int IpThreshold = 50;
    public const int MaxKeys = 10000;

    public const string KindAccount = "account";
    public const string KindIp = "ip";

    private sealed class Bucket
    {
        public readonly Queue<DateTime> Failures = new();
        public DateTime? BlockedUntil;
        /// <summary>本次暫停是否已寫過「登入嘗試過多暫停」稽核（同一次暫停只寫一筆）</summary>
        public bool BlockAudited;
        public DateTime LastTouched;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Bucket> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bucket> _ips = new(StringComparer.Ordinal);

    public bool IsBlocked(string account, IPAddress? ip, DateTime now, out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            Prune(now);
            retryAfter = TimeSpan.Zero;
            foreach (var bucket in BlockedBuckets(account, ip, now))
            {
                var remaining = bucket.BlockedUntil!.Value - now;
                if (remaining > retryAfter) retryAfter = remaining;
            }
            return retryAfter > TimeSpan.Zero;
        }
    }

    /// <summary>
    /// 被擋時呼叫：若擋住這次的任一暫停尚未寫過稽核，標記並回 true（呼叫端寫一筆稽核）；
    /// 同一次暫停內之後再被擋回 false。
    /// </summary>
    public bool MarkBlockAudited(string account, IPAddress? ip, DateTime now)
    {
        lock (_lock)
        {
            var first = false;
            foreach (var bucket in BlockedBuckets(account, ip, now))
            {
                if (bucket.BlockAudited) continue;
                bucket.BlockAudited = true;
                first = true;
            }
            return first;
        }
    }

    public void RecordFailure(string account, IPAddress? ip, DateTime now)
    {
        lock (_lock)
        {
            Prune(now);
            var accountKey = NormalizeAccount(account);
            if (accountKey.Length > 0)
                AddFailure(_accounts, accountKey, AccountThreshold, now);

            var ipKey = CountableIpKey(ip);
            if (ipKey != null)
                AddFailure(_ips, ipKey, IpThreshold, now);

            EnforceCap();
        }
    }

    /// <summary>登入成功：清除該帳號的計數（IP 維度不清——同一 IP 上其他帳號的失敗仍算數）</summary>
    public void RecordSuccess(string account)
    {
        lock (_lock)
        {
            _accounts.Remove(NormalizeAccount(account));
        }
    }

    public IReadOnlyList<LoginThrottleEntry> GetBlocked(DateTime now)
    {
        lock (_lock)
        {
            Prune(now);
            return _accounts
                .Where(kv => kv.Value.BlockedUntil > now)
                .Select(kv => new LoginThrottleEntry(kv.Key, KindAccount, kv.Value.BlockedUntil!.Value))
                .Concat(_ips
                    .Where(kv => kv.Value.BlockedUntil > now)
                    .Select(kv => new LoginThrottleEntry(kv.Key, KindIp, kv.Value.BlockedUntil!.Value)))
                .OrderBy(e => e.BlockedUntil)
                .ToList();
        }
    }

    /// <summary>手動解除：key 為帳號或 IP 字串；兩個維度都找，任一有移除即回 true</summary>
    public bool Clear(string key)
    {
        lock (_lock)
        {
            var removed = _accounts.Remove(NormalizeAccount(key));
            if (IPAddress.TryParse(key.Trim(), out var ip))
                removed |= _ips.Remove(IpKey(ip));
            return removed;
        }
    }

    private IEnumerable<Bucket> BlockedBuckets(string account, IPAddress? ip, DateTime now)
    {
        if (_accounts.TryGetValue(NormalizeAccount(account), out var a) && a.BlockedUntil > now)
            yield return a;
        var ipKey = CountableIpKey(ip);
        if (ipKey != null && _ips.TryGetValue(ipKey, out var i) && i.BlockedUntil > now)
            yield return i;
    }

    private static void AddFailure(Dictionary<string, Bucket> map, string key, int threshold, DateTime now)
    {
        if (!map.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket();
            map[key] = bucket;
        }
        bucket.LastTouched = now;
        if (bucket.BlockedUntil > now) return;

        bucket.Failures.Enqueue(now);
        TrimWindow(bucket, now);
        if (bucket.Failures.Count >= threshold)
        {
            bucket.BlockedUntil = now + BlockDuration;
            bucket.BlockAudited = false;
            bucket.Failures.Clear();
        }
    }

    private static void TrimWindow(Bucket bucket, DateTime now)
    {
        while (bucket.Failures.Count > 0 && bucket.Failures.Peek() <= now - Window)
            bucket.Failures.Dequeue();
    }

    /// <summary>移除過期資料：暫停已結束且窗口內沒有失敗的鍵整個刪掉，避免字典無限成長</summary>
    private void Prune(DateTime now)
    {
        PruneMap(_accounts, now);
        PruneMap(_ips, now);
    }

    private static void PruneMap(Dictionary<string, Bucket> map, DateTime now)
    {
        List<string>? dead = null;
        foreach (var (key, bucket) in map)
        {
            TrimWindow(bucket, now);
            if (bucket.BlockedUntil <= now) bucket.BlockedUntil = null;
            if (bucket.BlockedUntil == null && bucket.Failures.Count == 0)
                (dead ??= new List<string>()).Add(key);
        }
        if (dead == null) return;
        foreach (var key in dead) map.Remove(key);
    }

    /// <summary>總鍵數上限：超過時移除最久沒被碰過的鍵（大量亂數帳號的噴灑不能把記憶體撐爆）</summary>
    private void EnforceCap()
    {
        var overflow = _accounts.Count + _ips.Count - MaxKeys;
        if (overflow <= 0) return;

        var oldest = _accounts.Select(kv => (Map: _accounts, kv.Key, kv.Value.LastTouched))
            .Concat(_ips.Select(kv => (Map: _ips, kv.Key, kv.Value.LastTouched)))
            .OrderBy(x => x.LastTouched)
            .Take(overflow)
            .ToList();
        foreach (var x in oldest) x.Map.Remove(x.Key);
    }

    private static string NormalizeAccount(string? account) =>
        (account ?? string.Empty).Trim().ToLowerInvariant();

    private static string? CountableIpKey(IPAddress? ip)
    {
        if (ip == null || IPAddress.IsLoopback(ip)) return null;
        var key = IpKey(ip);
        return IPAddress.IsLoopback(IPAddress.Parse(key)) ? null : key;
    }

    private static string IpKey(IPAddress ip) =>
        (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
}
