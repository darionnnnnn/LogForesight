using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>
/// 「問題負責人可見的主機」（<c>HostVisibilityResolver.GetIssueOwnedHostIds</c>）結果的
/// 跨請求快取（回饋四十五輪 B4）。
///
/// 為什麼只包這一條：可見範圍的四條路徑裡，群組授權／主機負責人／停用過濾都是記憶體集合
/// 運算（來源 blob 自己有版本快取），成本低；問題負責人那條卻要打一支橫跨整個保留期的聚合
/// 查詢。於是只要某人被指派為問題負責人，他的**每一個** API 請求都要付一次那支查詢的成本，
/// 而走 ViewAll 短路徑的管理者完全感受不到——慢只發生在一般使用者身上。
/// 刻意**不快取整個 GetVisibleHostIds()**：那是授權邊界，快取範圍越大、算錯時的越權面越大，
/// 其餘三條路徑本來就不貴，沒有必要一起冒險。
///
/// **鍵含三個維度，少一個就是錯**（見 <see cref="KeyOf"/>）：使用者 id（少了是跨使用者洩漏）、
/// 資料版本戳（少了則指派變更後不會反映）、保留天數（少了則設定改完窗口不跟著變）。
///
/// **授權變更不必等 TTL**：鍵含版本戳，任何會改到可見範圍的寫入路徑推進版本戳後，
/// 舊鍵當下就再也命不中、下一次呼叫即重算。TTL 與條目上限純粹是清理機制
/// （避免離線使用者的舊條目無限累積），不是新鮮度的保證來源。
/// </summary>
public class IssueOwnedHostIdsCache : BoundedTtlCache<HashSet<long>>
{
    /// <summary>存活秒數：與 <see cref="IssueRankingCache.TtlSeconds"/> 同值同理由
    /// （全站快取的清理節奏一致）。</summary>
    public const int TtlSeconds = IssueRankingCache.TtlSeconds;

    /// <summary>條目上限：同 <see cref="SummaryCache"/>——鍵含使用者與設定維度，組合無限，
    /// 滿了逐筆淘汰（先清過期、再清最久沒被存取的），不整批清空。</summary>
    private const int MaxEntries = 256;

    public IssueOwnedHostIdsCache(DataVersionStamp stamp, Func<DateTime>? now = null)
        : base("IssueOwnedHostIdsCache", TtlSeconds, MaxEntries, long.MaxValue, h => h.Count, () => stamp.Current, now)
    {
    }

    /// <summary>組鍵：使用者 id｜資料版本戳｜保留天數。三個維度都是必要的，理由見類別註解。</summary>
    public static string KeyOf(long userId, long version, int retentionDays) =>
        $"{userId}|{version}|{retentionDays}";

    /// <summary>
    /// 取快取；未命中（不存在／逾時／版本已推進）時呼叫 <paramref name="factory"/> 算一次並存起來（同鍵並發只算一次）。
    /// 回傳的是**副本**——呼叫端（VisibilityService 會對它做 UnionWith）不得改到共用集合。
    /// 版本推進時舊版條目在下一次寫入新鍵時整批釋放（<see cref="BoundedTtlCache{T}"/>）；
    /// 晚到的舊版本請求不會清掉較新版本的條目。
    /// </summary>
    public IReadOnlySet<long> GetOrAdd(long userId, int retentionDays, Func<IReadOnlySet<long>> factory)
    {
        var version = CurrentVersion;
        var shared = GetOrCompute(KeyOf(userId, version, retentionDays), version, () => new HashSet<long>(factory()));
        return new HashSet<long>(shared);
    }
}
