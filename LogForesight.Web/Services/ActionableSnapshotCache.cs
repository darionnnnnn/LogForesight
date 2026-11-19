using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>
/// <see cref="IssueTodoQuery.ResolveActionable"/> 結果的跨請求快取（回饋三十六輪批次B）。
///
/// 為什麼需要它：可行動快照是儀表板成本最高的查詢之一（lf_top_issues join lf_daily_records
/// 後五欄 GROUP BY）。`IssueRankingCache` 只包排行投影、最外層 `SummaryCache` 的鍵各不相同，
/// 所以同一份資料版本下，不同外層鍵的請求仍會各自重算一次快照；本快取的價值就在這裡——
/// 鍵含資料版本戳，同版本時不同外層鍵共用同一份快照，版本推進時鍵自然換新。
/// 機制與取捨完全比照 <see cref="IssueRankingCache"/>：背景寫入執行中版本不推進，
/// 故保留 TTL 作為上限。鍵含可見主機集合不得串味。
/// </summary>
/// <remarks>
/// 快取機制在 <see cref="BoundedTtlCache{T}"/>，這裡只負責組鍵與型別形狀。
/// </remarks>
public class ActionableSnapshotCache : BoundedTtlCache<List<ResolvedOccurrence>>
{
    /// <summary>存活秒數：與 <see cref="IssueRankingCache.TtlSeconds"/> 同值同理由。</summary>
    public const int TtlSeconds = IssueRankingCache.TtlSeconds;

    /// <summary>條目上限：同 <see cref="IssueRankingCache"/>——滿了逐筆淘汰（先清過期、再清最久沒被存取的）。</summary>
    private const int MaxEntries = 256;

    /// <param name="maxElements">元素總數上限（暫定 20 萬），語意同 <see cref="IssueRankingCache"/>。</param>
    public ActionableSnapshotCache(DataVersionStamp versionStamp, Func<DateTime>? now = null, long maxElements = 200_000)
        : base("ActionableSnapshotCache", TtlSeconds, MaxEntries, maxElements, l => l.Count,
            () => versionStamp.Current, now, l => new List<ResolvedOccurrence>(l))
    {
    }

    /// <summary>目前資料版本；呼叫端組鍵時傳給 <see cref="KeyOf"/>。</summary>
    public long Version => CurrentVersion;

    /// <summary>組鍵：資料版本戳＋區間＋可見主機＋日風險等級＋可見嚴重度＋靜音排除（<see cref="IssueExclusion.CacheToken"/>）——
    /// 日風險等級與可見嚴重度來自系統設定，設定變更後 TTL 內的殘影與其他快取一致，可接受；
    /// 靜音設定或換日改變時鍵直接跟著變。</summary>
    public static string KeyOf(
        long version, DateTime from, DateTime to,
        IReadOnlyCollection<long>? visibleHostIds,
        IReadOnlyCollection<string>? riskLevels,
        IReadOnlyCollection<string>? visibleSeverities,
        string exclusionToken)
    {
        var hosts = visibleHostIds == null ? "*" : VisibleHostKeyPart.Of(visibleHostIds);
        var risks = riskLevels == null ? "*" : string.Join(",", riskLevels.OrderBy(r => r, StringComparer.Ordinal));
        var sevs = visibleSeverities == null ? "*" : string.Join(",", visibleSeverities.OrderBy(s => s, StringComparer.Ordinal));
        return $"v{version}|{from:yyyyMMdd}|{to:yyyyMMdd}|{risks}|{sevs}|{exclusionToken}|{hosts}";
    }

    /// <summary>命中直接回傳快取內的清單（不複製）：型別是唯讀，呼叫端不能改到共用清單；
    /// 需要變動請自行 ToList()。</summary>
    public IReadOnlyList<ResolvedOccurrence>? TryGet(string key) => Lookup(key, version: null);

    /// <summary>取快取；未命中時呼叫 <paramref name="compute"/> 算一次並以 <paramref name="version"/> 寫入（同鍵並發只算一次）。</summary>
    public IReadOnlyList<ResolvedOccurrence> GetOrAdd(string key, long version, Func<List<ResolvedOccurrence>> compute) =>
        GetOrCompute(key, version, compute);

    /// <param name="version">組鍵時用的資料版本（<see cref="KeyOf"/> 的第一個參數）；計算期間版本若已前進就不寫入。省略時視為目前版本。</param>
    public void Set(string key, List<ResolvedOccurrence> items, long? version = null) => Store(key, items, version);
}
