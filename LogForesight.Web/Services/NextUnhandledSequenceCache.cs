using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>
/// 「下一筆未處理」捷徑清單的跨請求快取（回饋四十五輪 B2）。
///
/// 為什麼需要它：這條捷徑依「處理狀態」找案子，窗口必須是整個保留期（把窗口縮成近 N 天，
/// 一件開了幾個月還沒結的案子就會從捷徑裡消失——那是使用者的待辦不見了），
/// 而帶處理狀態篩選的查詢天生走記錄查詢的慢路徑（整批撈回逐筆推導處理狀態）。
/// 窗口不能縮，效能就由這層快取吸收：進詳情頁與每次批次儲存後都會再問一次，
/// 三十秒內的重複詢問直接命中。
///
/// 機制與取捨比照 <see cref="SummaryCache"/>：資料版本戳＋TTL 保底雙保險
/// （漏 bump 的寫入路徑最多讓捷徑停留一個 TTL，不會變成永久的假新鮮）。
///
/// **鍵必須含授權維度**（可見主機集合）與可見度設定（可見嚴重度／可見日風險等級）：
/// 少任何一個維度就是把 A 看得到的待辦端給 B，或是拿甲設定算出來的清單回答乙設定。
///
/// 存的是**輕量序列**（只有 hostId 與 date），不是清單 DTO——呼叫端只需要組一個連結。
/// </summary>
public class NextUnhandledSequenceCache : BoundedTtlCache<List<(long HostId, string Date)>>
{
    /// <summary>存活秒數：與 <see cref="IssueRankingCache.TtlSeconds"/> 同值同理由。</summary>
    public const int TtlSeconds = IssueRankingCache.TtlSeconds;

    /// <summary>條目上限：同 <see cref="SummaryCache"/>——鍵含授權範圍，組合無限，
    /// 滿了逐筆淘汰（先清過期、再清最久沒被存取的），不整批清空。</summary>
    private const int MaxEntries = 256;

    public NextUnhandledSequenceCache(DataVersionStamp stamp, Func<DateTime>? now = null)
        : base("NextUnhandledSequenceCache", TtlSeconds, MaxEntries, long.MaxValue, l => l.Count, () => stamp.Current, now)
    {
    }

    /// <summary>組鍵：可見主機集合＋可見嚴重度＋可見日風險等級。主機 id 與字串都先排序，
    /// 順序不同的同一集合是同一鍵；null 代表「不限」，與空集合（什麼都看不到）是不同的鍵。</summary>
    public static string KeyOf(
        IReadOnlyCollection<long> visibleHostIds,
        IReadOnlyCollection<string>? visibleSeverities,
        IReadOnlyCollection<string>? visibleDayRiskLevels)
    {
        var hosts = VisibleHostKeyPart.Of(visibleHostIds);
        var sevs = visibleSeverities == null ? "*" : string.Join(",", visibleSeverities.OrderBy(s => s, StringComparer.Ordinal));
        var risks = visibleDayRiskLevels == null ? "*" : string.Join(",", visibleDayRiskLevels.OrderBy(r => r, StringComparer.Ordinal));
        return $"{sevs}|{risks}|{hosts}";
    }

    /// <summary>取快取；未命中（不存在／逾時／版本已推進）時呼叫 factory 算一次並存起來（同鍵並發只算一次）。
    /// 回傳**副本**——快取裡的清單不外流，呼叫端不得改到共用清單。</summary>
    public List<(long HostId, string Date)> GetOrAdd(string key, Func<List<(long HostId, string Date)>> factory)
    {
        var shared = GetOrCompute(key, CurrentVersion, () => new List<(long HostId, string Date)>(factory()));
        return new List<(long, string)>(shared);
    }
}
