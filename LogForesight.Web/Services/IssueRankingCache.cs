using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// <see cref="IssueRankingBuilder.Build"/> 結果的跨請求快取（回饋二十七輪作業 F4）。
///
/// 為什麼需要它：儀表板與報表共用同一份問題排行投影，使用者從儀表板點進報表，
/// 等於幾秒內把同一組聚合（內部 6 個以上查詢）整套重算一次。builder 本身是 Scoped
/// （逐請求），快取必須獨立成 Singleton 才跨請求生效。
///
/// 鍵含資料版本戳（<see cref="DataVersionStamp"/>）；背景寫入執行中版本不推進，
/// 故保留 TTL 作為上限：背景寫入造成的變動最多延遲 <see cref="TtlSeconds"/> 秒反映，
/// 頁面本身也不是即時看板，可接受。
///
/// 鍵含**可見主機集合**：不同授權範圍的使用者各自一份，不得串味。
/// </summary>
/// <remarks>
/// 版本清除、元素總數上限、單筆超限與統計計數的邏輯與 <see cref="ActionableSnapshotCache"/> 逐行相同、只差條目型別——
/// 改這裡的逐出規則要同步改那邊（兩個 tuple 型別各異，暫不抽共用基底，見 BACKLOG）。
/// </remarks>
public class IssueRankingCache : ICacheStatistics
{
    /// <summary>存活秒數（暫定 30）：長到涵蓋「儀表板→報表」的頁面切換，短到處理狀態
    /// 變更不會停留在畫面上超過一次手動重整的自然間隔。</summary>
    public const int TtlSeconds = 30;

    /// <summary>條目上限：鍵含日期區間與授權範圍，理論組合無限，不設上限就是慢速記憶體洩漏。
    /// 滿了逐筆淘汰（先清過期、再清最久沒被存取的），不整批清空——多人同時使用時整批清會讓命中率趨近 0。</summary>
    private const int MaxEntries = 256;

    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, (List<IssueRankingDto> Items, DateTime CachedAt, DateTime LastAccess, long Version)> _entries = new();

    private readonly DataVersionStamp _versionStamp;

    // 統計計數（皆在 _lock 內維護；供健康頁記憶體詳情，見 ICacheStatistics）
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _oversizeRejected;

    /// <summary>目前所有條目的元素總數（Items.Count 加總）；增刪條目時同步更新，須在 _lock 內存取。</summary>
    private long _elements;

    private readonly long _maxElements;

    /// <param name="maxElements">元素總數上限（暫定 20 萬）：條目數上限擋不住「少數條目各自極大」，
    /// 這是以元素數近似的記憶體上限；單筆超限不快取，總數超限淘汰最久沒存取的條目。</param>
    public IssueRankingCache(DataVersionStamp versionStamp, Func<DateTime>? now = null, long maxElements = 200_000)
    {
        _versionStamp = versionStamp;
        _maxElements = maxElements;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>目前資料版本；呼叫端組鍵時傳給 <see cref="KeyOf"/>。</summary>
    public long Version => _versionStamp.Current;

    /// <summary>組鍵：資料版本戳＋區間＋可見範圍＋主機總數＋靜音排除（<see cref="IssueExclusion.CacheToken"/>）。
    /// 主機 id 排序後串起，順序不同的同一集合是同一鍵。靜音設定或換日改變時鍵跟著變，不等 TTL。</summary>
    public static string KeyOf(long version, DateTime from, DateTime to, IReadOnlyCollection<long>? visibleHostIds, int totalHosts, string exclusionToken)
    {
        var hosts = visibleHostIds == null
            ? "*"
            : string.Join(",", visibleHostIds.OrderBy(id => id));
        return $"v{version}|{from:yyyyMMdd}|{to:yyyyMMdd}|{totalHosts}|{exclusionToken}|{hosts}";
    }

    /// <summary>命中直接回傳快取內的清單（不複製）：型別是唯讀，呼叫端不能改到共用清單；
    /// 需要變動請自行 ToList()。</summary>
    public IReadOnlyList<IssueRankingDto>? TryGet(string key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry)) { _misses++; return null; }
            var now = _now();
            if ((now - entry.CachedAt).TotalSeconds >= TtlSeconds)
            {
                RemoveEntry(key);
                _misses++;
                return null;
            }
            _hits++;
            _entries[key] = entry with { LastAccess = now };
            return entry.Items;
        }
    }

    /// <param name="version">組鍵時用的資料版本（<see cref="KeyOf"/> 的第一個參數）；條目以它標記版本，
    /// 計算期間版本若已前進就不寫入（舊版本的結果不會再被命中）。省略時視為目前版本。</param>
    public void Set(string key, List<IssueRankingDto> items, long? version = null)
    {
        lock (_lock)
        {
            // 單筆就超過總上限：不寫入，呼叫端照常使用自己算出的結果；
            // 同鍵若有舊條目一併移除，避免之後命中與這次查詢不一致的舊結果
            if (items.Count > _maxElements)
            {
                _oversizeRejected++;
                if (_entries.Remove(key, out var stale)) { _elements -= stale.Items.Count; _evictions++; }
                return;
            }

            var now = _now();
            var current = _versionStamp.Current;
            // 計算期間資料版本已前進：這份結果屬於舊版本，鍵也是舊版本，之後不會再被命中，不必佔位
            if (version is { } keyVersion && keyVersion != current) return;
            if (_entries.TryGetValue(key, out var existing))
            {
                _elements -= existing.Items.Count;
            }
            else
            {
                // 版本一變舊版整批釋放（不等滿了才清），再走既有的過期／LRU 騰位
                EvictStaleVersions(current);
                EvictForInsert(now);
            }

            var copy = new List<IssueRankingDto>(items);
            _entries[key] = (copy, now, now, current);
            _elements += copy.Count;
            EvictForElements(key);
        }
    }

    /// <summary>移除條目並同步元素總數（呼叫端須持鎖）。</summary>
    private void RemoveEntry(string key)
    {
        if (_entries.Remove(key, out var removed)) _elements -= removed.Items.Count;
    }

    /// <summary>寫入新鍵前移除所有非目前版本的條目（呼叫端須持鎖），計入逐出。</summary>
    private void EvictStaleVersions(long version)
    {
        var stale = _entries.Where(e => e.Value.Version != version).Select(e => e.Key).ToList();
        foreach (var k in stale) RemoveEntry(k);
        _evictions += stale.Count;
    }

    /// <summary>元素總數超過上限時，逐筆移除最後存取最舊的條目，直到不超過上限；不移除剛寫入的那筆（呼叫端須持鎖）。</summary>
    private void EvictForElements(string justWritten)
    {
        while (_elements > _maxElements)
        {
            var victim = _entries.Where(e => e.Key != justWritten).MinBy(e => e.Value.LastAccess);
            if (victim.Key == null) break;
            RemoveEntry(victim.Key);
            _evictions++;
        }
    }

    /// <summary>已達上限時騰出空位（呼叫端須持鎖）：先移除逾時項目，仍滿就逐筆移除最後存取時間最舊的一筆。</summary>
    private void EvictForInsert(DateTime now)
    {
        if (_entries.Count < MaxEntries) return;

        var expired = _entries
            .Where(e => (now - e.Value.CachedAt).TotalSeconds >= TtlSeconds)
            .Select(e => e.Key)
            .ToList();
        foreach (var k in expired) RemoveEntry(k);
        _evictions += expired.Count;

        while (_entries.Count >= MaxEntries)
        {
            RemoveEntry(_entries.MinBy(e => e.Value.LastAccess).Key);
            _evictions++;
        }
    }

    /// <summary>目前條目數與累計命中／未命中／逐出（健康頁記憶體詳情用）。</summary>
    public CacheStatisticsSnapshot GetStatistics()
    {
        lock (_lock)
        {
            return new CacheStatisticsSnapshot("IssueRankingCache", _entries.Count, _elements, _hits, _misses, _evictions, _oversizeRejected);
        }
    }
}
