namespace LogForesight.Web.Services;

/// <summary>
/// <see cref="IssueTodoQuery.ResolveActionable"/> 結果的跨請求快取（回饋三十六輪批次B）。
///
/// 為什麼需要它：可行動快照是儀表板成本最高的查詢之一（lf_top_issues join lf_daily_records
/// 後五欄 GROUP BY），卻是問題彙總家族裡唯一沒有查詢層快取的——`IssueRankingCache` 只包
/// 排行投影，最外層 `SummaryCache` 又會被啟動期背景服務的版本戳推進打掉，於是同一份快照
/// 在幾秒內被完整重算。機制與取捨完全比照 <see cref="IssueRankingCache"/>：短 TTL 而非版本戳
/// （母體橫跨 records／handling／案件多張表，沒有單一版本錨點），鍵含可見主機集合不得串味。
/// </summary>
public class ActionableSnapshotCache
{
    /// <summary>存活秒數：與 <see cref="IssueRankingCache.TtlSeconds"/> 同值同理由。</summary>
    public const int TtlSeconds = IssueRankingCache.TtlSeconds;

    /// <summary>條目上限：同 <see cref="IssueRankingCache"/>——超過整批清掉，重算付得起。</summary>
    private const int MaxEntries = 64;

    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, (List<ResolvedOccurrence> Items, DateTime CachedAt)> _entries = new();

    public ActionableSnapshotCache(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.Now);

    /// <summary>組鍵：區間＋可見主機＋日風險等級＋可見嚴重度——後兩者來自系統設定，
    /// 設定變更後 TTL 內的殘影與其他快取一致，可接受。</summary>
    public static string KeyOf(
        DateTime from, DateTime to,
        IReadOnlyCollection<long>? visibleHostIds,
        IReadOnlyCollection<string>? riskLevels,
        IReadOnlyCollection<string>? visibleSeverities)
    {
        var hosts = visibleHostIds == null ? "*" : string.Join(",", visibleHostIds.OrderBy(id => id));
        var risks = riskLevels == null ? "*" : string.Join(",", riskLevels.OrderBy(r => r, StringComparer.Ordinal));
        var sevs = visibleSeverities == null ? "*" : string.Join(",", visibleSeverities.OrderBy(s => s, StringComparer.Ordinal));
        return $"{from:yyyyMMdd}|{to:yyyyMMdd}|{risks}|{sevs}|{hosts}";
    }

    /// <summary>命中回傳**副本**——呼叫端會依群組子集篩選，不能讓它改到共用清單。</summary>
    public List<ResolvedOccurrence>? TryGet(string key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            if ((_now() - entry.CachedAt).TotalSeconds >= TtlSeconds)
            {
                _entries.Remove(key);
                return null;
            }
            return new List<ResolvedOccurrence>(entry.Items);
        }
    }

    public void Set(string key, List<ResolvedOccurrence> items)
    {
        lock (_lock)
        {
            if (_entries.Count >= MaxEntries) _entries.Clear();
            _entries[key] = (new List<ResolvedOccurrence>(items), _now());
        }
    }
}
