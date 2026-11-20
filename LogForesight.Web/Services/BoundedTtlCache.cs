using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>
/// 跨請求快取的唯一實作：條目數上限、元素總數上限、TTL、資料版本戳、逐筆 LRU 逐出、命中統計
/// （<see cref="ICacheStatistics"/>），未命中一律經 <see cref="SingleFlight"/>。
/// <see cref="IssueRankingCache"/>、<see cref="ActionableSnapshotCache"/>、<see cref="SummaryCache"/>、
/// <see cref="NextUnhandledSequenceCache"/>、<see cref="IssueOwnedHostIdsCache"/>、<see cref="PrtgDeviceIndexCache"/>
/// 都繼承它，各自只負責**組鍵**（授權維度、版本戳由子類別的 KeyOf 與呼叫端提供）與對外的型別形狀；
/// 這裡不做任何授權判斷。
/// <para>演算法（以前六份各寫一次、逐出策略已分歧，現統一為最完整的那一份）：</para>
/// <list type="bullet">
/// <item>讀取時順手釋放逾時與舊版本條目，每個 TTL 週期最多掃一次（尖峰過後沒人寫入也會釋放）。</item>
/// <item>寫入新鍵前先釋放所有「版本比目前舊」的條目；版本只前進，比目前新的條目（晚到的舊版請求不得清掉它）不動。</item>
/// <item>條目數滿了：先清逾時、再逐筆清最後存取最舊的一筆，不整批清空。</item>
/// <item>元素總數超限：逐筆清最後存取最舊的（不清剛寫入那筆）；單筆就超限則不入快取並移除同鍵舊條目。</item>
/// <item>計算期間版本已前進：結果不寫入（鍵是舊版本，之後不會再被命中）。</item>
/// <item>同鍵並發未命中只算一次（結果過大不入快取時也一樣）；合併內的二次查詢不再計命中／未命中。</item>
/// </list>
/// </summary>
/// <remarks>
/// 以繼承而非組合：長駐集合守門（<c>LongLivedCollectionGuardTests</c>）會沿著 singleton 的基底型別找可變集合，
/// 字典只在這一個類別，登錄也只有一筆。
/// </remarks>
public abstract class BoundedTtlCache<T> : ICacheStatistics where T : class
{
    private readonly string _name;
    private readonly int _ttlSeconds;
    private readonly int _maxEntries;
    private readonly long _maxElements;
    private readonly Func<T, int> _sizeOf;
    private readonly Func<T, T>? _copyOnStore;
    private readonly Func<long>? _currentVersion;
    private readonly Func<DateTime> _now;

    private readonly object _lock = new();
    private readonly SingleFlight _flight = new();
    private DateTime _lastSweep = DateTime.MinValue;
    private readonly Dictionary<string, Entry> _entries = new();

    // 統計計數（皆在 _lock 內維護；供健康頁記憶體詳情）
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _oversizeRejected;

    /// <summary>目前所有條目的元素總數（<c>sizeOf</c> 加總）；增刪條目時同步更新，須在 _lock 內存取。</summary>
    private long _elements;

    private readonly record struct Entry(T Value, int Size, DateTime CachedAt, DateTime LastAccess, long Version);

    /// <param name="name">統計用的快取名稱（健康頁顯示）</param>
    /// <param name="maxElements">元素總數上限：條目數上限擋不住「少數條目各自極大」，這是以元素數近似的記憶體上限；
    /// 不需要這層限制的快取傳 <see cref="long.MaxValue"/>（元素數仍會統計）。</param>
    /// <param name="sizeOf">條目的元素數（清單筆數、主機數；單一物件傳 1）</param>
    /// <param name="currentVersion">目前資料版本；與資料版本戳無關的快取傳 null（版本恆為 0，不做版本清除）</param>
    /// <param name="copyOnStore">寫入時複製一份再存（呼叫端之後改動自己的清單不能影響快取）；不需要傳 null</param>
    protected BoundedTtlCache(
        string name, int ttlSeconds, int maxEntries, long maxElements,
        Func<T, int> sizeOf, Func<long>? currentVersion, Func<DateTime>? now, Func<T, T>? copyOnStore = null)
    {
        _name = name;
        _ttlSeconds = ttlSeconds;
        _maxEntries = maxEntries;
        _maxElements = maxElements;
        _sizeOf = sizeOf;
        _currentVersion = currentVersion;
        _copyOnStore = copyOnStore;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>目前資料版本（沒有版本戳的快取恆為 0）；呼叫端組鍵與計算前讀一次。</summary>
    protected long CurrentVersion => _currentVersion?.Invoke() ?? 0;

    /// <summary>
    /// 取快取；未命中時呼叫 <paramref name="compute"/> 算一次並以 <paramref name="version"/> 寫入。
    /// 同鍵並發只算一次（<see cref="SingleFlight"/>）——結果超過元素上限不入快取時尤其重要，
    /// 否則每個並發請求都各算一整份。回傳的是 compute 的原始結果（miss）或快取內的共用實例（hit）。
    /// </summary>
    /// <param name="version">組鍵時用的資料版本：命中要求條目版本相同、寫入時版本若已前進就不存。</param>
    protected T GetOrCompute(string key, long version, Func<T> compute)
    {
        if (Lookup(key, version, countStats: true) is { } hit) return hit;
        // 合併鍵帶版本：不同版本的請求不共用同一份計算
        return _flight.Run<T>(key + "|" + version, () =>
        {
            // 前一個同鍵合併可能剛寫入並結束：再看一次，避免緊接著重算
            if (Lookup(key, version, countStats: false) is { } again) return again;
            var value = compute();
            Store(key, value, version);
            return value;
        });
    }

    /// <summary>查快取；<paramref name="version"/> 為 null 表示不檢查條目版本（鍵本身已含版本的呼叫端用）。
    /// 逾時或版本不符回 null 並（<paramref name="countStats"/> 時）計一次未命中。</summary>
    protected T? Lookup(string key, long? version, bool countStats = true)
    {
        lock (_lock)
        {
            var now = _now();
            SweepIfDue(now);
            if (!_entries.TryGetValue(key, out var entry)) { if (countStats) _misses++; return null; }
            if ((now - entry.CachedAt).TotalSeconds >= _ttlSeconds || (version is { } v && entry.Version != v))
            {
                // 逾時才移除；版本不符的條目留給掃描與寫入時的舊版釋放（可能是晚到的舊版請求，不動較新的條目）
                if ((now - entry.CachedAt).TotalSeconds >= _ttlSeconds) RemoveEntry(key);
                if (countStats) _misses++;
                return null;
            }
            // 合併內的二次查詢（countStats＝false）已記過一次未命中，不再記命中，否則同一請求會被算成一次未命中加一次命中
            if (countStats) _hits++;
            _entries[key] = entry with { LastAccess = now };
            return entry.Value;
        }
    }

    /// <summary>寫入；<paramref name="version"/> 為組鍵時用的資料版本，計算期間版本若已前進就不寫入，省略時視為目前版本。</summary>
    protected void Store(string key, T value, long? version = null)
    {
        var size = _sizeOf(value);
        lock (_lock)
        {
            // 單筆就超過總上限：不寫入，呼叫端照常使用自己算出的結果；
            // 同鍵若有舊條目一併移除，避免之後命中與這次查詢不一致的舊結果
            if (size > _maxElements)
            {
                _oversizeRejected++;
                if (_entries.Remove(key, out var stale)) { _elements -= stale.Size; _evictions++; }
                return;
            }

            var now = _now();
            var current = CurrentVersion;
            // 計算期間資料版本已前進：這份結果屬於舊版本，之後不會再被命中，不必佔位
            if (version is { } keyVersion && keyVersion != current) return;
            if (_entries.TryGetValue(key, out var existing))
            {
                _elements -= existing.Size;
            }
            else
            {
                // 版本一變舊版整批釋放（不等滿了才清），再走既有的過期／LRU 騰位
                EvictOlderVersions(current);
                EvictForInsert(now);
            }

            var stored = _copyOnStore != null ? _copyOnStore(value) : value;
            _entries[key] = new Entry(stored, size, now, now, current);
            _elements += size;
            EvictForElements(key);
        }
    }

    /// <summary>
    /// 讀取時順手釋放逾時與舊版本的條目（呼叫端須持鎖）：只在寫入新鍵時才清的話，尖峰過後沒人再寫入，
    /// 過期內容會一直佔著記憶體。每個 TTL 週期最多掃一次。
    /// </summary>
    private void SweepIfDue(DateTime now)
    {
        if ((now - _lastSweep).TotalSeconds < _ttlSeconds) return;
        _lastSweep = now;
        var current = CurrentVersion;
        var stale = _entries
            .Where(e => e.Value.Version < current || (now - e.Value.CachedAt).TotalSeconds >= _ttlSeconds)
            .Select(e => e.Key)
            .ToList();
        foreach (var k in stale) RemoveEntry(k);
        _evictions += stale.Count;
    }

    /// <summary>移除條目並同步元素總數（呼叫端須持鎖）。</summary>
    private void RemoveEntry(string key)
    {
        if (_entries.Remove(key, out var removed)) _elements -= removed.Size;
    }

    /// <summary>寫入新鍵前移除所有版本比目前舊的條目（呼叫端須持鎖），計入逐出。
    /// 版本只前進：比目前新的條目是晚到的舊版請求遇到的「未來」，不能清掉。</summary>
    private void EvictOlderVersions(long current)
    {
        var stale = _entries.Where(e => e.Value.Version < current).Select(e => e.Key).ToList();
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

    /// <summary>已達條目上限時騰出空位（呼叫端須持鎖）：先移除逾時項目，仍滿就逐筆移除最後存取時間最舊的一筆。</summary>
    private void EvictForInsert(DateTime now)
    {
        if (_entries.Count < _maxEntries) return;

        var expired = _entries
            .Where(e => (now - e.Value.CachedAt).TotalSeconds >= _ttlSeconds)
            .Select(e => e.Key)
            .ToList();
        foreach (var k in expired) RemoveEntry(k);
        _evictions += expired.Count;

        while (_entries.Count >= _maxEntries)
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
            return new CacheStatisticsSnapshot(_name, _entries.Count, _elements, _hits, _misses, _evictions, _oversizeRejected);
        }
    }
}
