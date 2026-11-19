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
/// 快取機制（TTL、版本清除、元素總數上限、LRU、統計、同鍵合併）在 <see cref="BoundedTtlCache{T}"/>，這裡只負責組鍵與型別形狀。
/// </remarks>
public class IssueRankingCache : BoundedTtlCache<List<IssueRankingDto>>
{
    /// <summary>存活秒數（暫定 30）：長到涵蓋「儀表板→報表」的頁面切換，短到處理狀態
    /// 變更不會停留在畫面上超過一次手動重整的自然間隔。</summary>
    public const int TtlSeconds = 30;

    /// <summary>條目上限：鍵含日期區間與授權範圍，理論組合無限，不設上限就是慢速記憶體洩漏。
    /// 滿了逐筆淘汰（先清過期、再清最久沒被存取的），不整批清空——多人同時使用時整批清會讓命中率趨近 0。</summary>
    private const int MaxEntries = 256;

    /// <param name="maxElements">元素總數上限（暫定 20 萬）：條目數上限擋不住「少數條目各自極大」，
    /// 這是以元素數近似的記憶體上限；單筆超限不快取，總數超限淘汰最久沒存取的條目。</param>
    public IssueRankingCache(DataVersionStamp versionStamp, Func<DateTime>? now = null, long maxElements = 200_000)
        : base("IssueRankingCache", TtlSeconds, MaxEntries, maxElements, l => l.Count,
            () => versionStamp.Current, now, l => new List<IssueRankingDto>(l))
    {
    }

    /// <summary>目前資料版本；呼叫端組鍵時傳給 <see cref="KeyOf"/>。</summary>
    public long Version => CurrentVersion;

    /// <summary>組鍵：資料版本戳＋區間＋可見範圍＋主機總數＋靜音排除（<see cref="IssueExclusion.CacheToken"/>）。
    /// 主機 id 排序後串起，順序不同的同一集合是同一鍵。靜音設定或換日改變時鍵跟著變，不等 TTL。</summary>
    public static string KeyOf(long version, DateTime from, DateTime to, IReadOnlyCollection<long>? visibleHostIds, int totalHosts, string exclusionToken)
    {
        var hosts = visibleHostIds == null
            ? "*"
            : VisibleHostKeyPart.Of(visibleHostIds);
        return $"v{version}|{from:yyyyMMdd}|{to:yyyyMMdd}|{totalHosts}|{exclusionToken}|{hosts}";
    }

    /// <summary>命中直接回傳快取內的清單（不複製）：型別是唯讀，呼叫端不能改到共用清單；
    /// 需要變動請自行 ToList()。</summary>
    public IReadOnlyList<IssueRankingDto>? TryGet(string key) => Lookup(key, version: null);

    /// <summary>取快取；未命中時呼叫 <paramref name="compute"/> 算一次並以 <paramref name="version"/> 寫入（同鍵並發只算一次）。</summary>
    public IReadOnlyList<IssueRankingDto> GetOrAdd(string key, long version, Func<List<IssueRankingDto>> compute) =>
        GetOrCompute(key, version, compute);

    /// <param name="version">組鍵時用的資料版本（<see cref="KeyOf"/> 的第一個參數）；條目以它標記版本，
    /// 計算期間版本若已前進就不寫入（舊版本的結果不會再被命中）。省略時視為目前版本。</param>
    public void Set(string key, List<IssueRankingDto> items, long? version = null) => Store(key, items, version);
}
