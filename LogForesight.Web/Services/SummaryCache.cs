namespace LogForesight.Web.Services;

/// <summary>
/// 全域資料版本戳（回饋三十五輪批次F）：任何會改變儀表板／報表數字的寫入路徑都 bump 一次，
/// 快取鍵帶上它，資料一變舊鍵就再也不會被命中。
///
/// 為什麼是「聯集戳」而不是單一表的版本：儀表板與報表的數字橫跨 records／handling／案件／
/// 權限異動多張表，沒有單一錨點——這正是 <see cref="IssueRankingCache"/> 當初選擇純 TTL 的理由。
/// 這裡的作法是任一相關寫入都推進同一個戳，並且**仍保留 TTL 當保底**：漏 bump 的路徑最多
/// 讓畫面停留一個 TTL，不會變成永久的假新鮮。
/// </summary>
public class DataVersionStamp
{
    private long _version;

    public long Current => Interlocked.Read(ref _version);

    /// <summary>資料有變動：推進版本，讓所有既有快取鍵失效</summary>
    public void Bump() => Interlocked.Increment(ref _version);
}

/// <summary>
/// 儀表板／報表整包回應的跨請求快取（批次F）。單次請求要打 10 支以上聚合查詢，
/// 同一組條件在版本戳沒變的期間重複算是純浪費。
///
/// **鍵必須含授權維度**（可見主機集合）：漏了就是把 A 看得到的資料端給 B，
/// 這是本專案的紅線（docs/BACKLOG.md 對快取的既有告誡）。
///
/// 命中回傳的是**共用實例**：兩支 summary 的 DTO 都是服務算完直接交給
/// <c>ApiResponse</c> 序列化、沒有任何呼叫端會改它。要新增消費端時請維持這個前提，
/// 需要改內容就自己複製一份。
/// </summary>
public class SummaryCache
{
    /// <summary>存活秒數：與 <see cref="IssueRankingCache.TtlSeconds"/> 同值——
    /// 兩層快取的新鮮度不一致會讓同一畫面的區塊互相矛盾。</summary>
    public const int TtlSeconds = IssueRankingCache.TtlSeconds;

    /// <summary>條目上限：鍵含期間與授權範圍，組合無限，不設上限就是慢速記憶體洩漏。
    /// 滿了逐筆淘汰（先清過期、再清最久沒被存取的），不整批清空——多人同時使用時整批清會讓命中率趨近 0。</summary>
    private const int MaxEntries = 256;

    private readonly DataVersionStamp _stamp;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, (object Payload, DateTime CachedAt, long Version, DateTime LastAccess)> _entries = new();

    public SummaryCache(DataVersionStamp stamp, Func<DateTime>? now = null)
    {
        _stamp = stamp;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>組鍵：用途＋條件＋可見範圍。主機 id 排序後串起，順序不同的同一集合是同一鍵。</summary>
    public static string KeyOf(string scope, string parameters, IReadOnlyCollection<long> visibleHostIds) =>
        $"{scope}|{parameters}|{string.Join(",", visibleHostIds.OrderBy(id => id))}";

    /// <summary>取快取；未命中（不存在／逾時／版本已推進）時呼叫 factory 算一次並存起來。</summary>
    public T GetOrAdd<T>(string key, Func<T> factory) where T : class
    {
        var version = _stamp.Current;

        lock (_lock)
        {
            var now = _now();
            if (_entries.TryGetValue(key, out var entry)
                && entry.Version == version
                && (now - entry.CachedAt).TotalSeconds < TtlSeconds)
            {
                _entries[key] = entry with { LastAccess = now };
                return (T)entry.Payload;
            }
        }

        // factory 刻意在鎖外執行：聚合查詢很慢，握著鎖算會讓其他請求全部排隊。
        // 代價是同時進來的請求可能各算一次，結果相同、最後一個寫入為準。
        var value = factory();

        lock (_lock)
        {
            var now = _now();
            if (!_entries.ContainsKey(key)) EvictForInsert(now, version);
            _entries[key] = (value, now, version, now);
        }

        return value;
    }

    /// <summary>已達上限時騰出空位（呼叫端須持鎖）：先移除已過期（逾時或版本已推進、再也命不中）的項目，
    /// 仍滿就逐筆移除最後存取時間最舊的一筆。</summary>
    private void EvictForInsert(DateTime now, long version)
    {
        if (_entries.Count < MaxEntries) return;

        var expired = _entries
            .Where(e => e.Value.Version != version || (now - e.Value.CachedAt).TotalSeconds >= TtlSeconds)
            .Select(e => e.Key)
            .ToList();
        foreach (var k in expired) _entries.Remove(k);

        while (_entries.Count >= MaxEntries)
        {
            _entries.Remove(_entries.MinBy(e => e.Value.LastAccess).Key);
        }
    }
}
