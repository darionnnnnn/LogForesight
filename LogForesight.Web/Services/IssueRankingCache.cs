using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// <see cref="IssueRankingBuilder.Build"/> 結果的跨請求快取（回饋二十七輪作業 F4）。
///
/// 為什麼需要它：儀表板與報表共用同一份問題排行投影，使用者從儀表板點進報表，
/// 等於幾秒內把同一組聚合（內部 6 個以上查詢）整套重算一次。builder 本身是 Scoped
/// （逐請求），快取必須獨立成 Singleton 才跨請求生效。
///
/// 為什麼是**短 TTL 而不是版本戳**：排行結果橫跨 records／handling／案件多張表，
/// 沒有單一版本錨點可當失效鍵；硬湊一個會做出「該失效沒失效」的假新鮮，讓兩頁數字分岔
/// ——那正是這份投影當初共用化要消滅的症狀。TTL 內處理狀態變更最多延遲
/// <see cref="TtlSeconds"/> 秒反映，頁面本身也不是即時看板，可接受。
///
/// 鍵含**可見主機集合**：不同授權範圍的使用者各自一份，不得串味。
/// </summary>
public class IssueRankingCache
{
    /// <summary>存活秒數（暫定 30）：長到涵蓋「儀表板→報表」的頁面切換，短到處理狀態
    /// 變更不會停留在畫面上超過一次手動重整的自然間隔。</summary>
    public const int TtlSeconds = 30;

    /// <summary>條目上限：鍵含日期區間與授權範圍，理論組合無限，不設上限就是慢速記憶體洩漏。
    /// 超過時整批清掉——這是快取不是儲存，重算的代價本來就付得起。</summary>
    private const int MaxEntries = 64;

    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, (List<IssueRankingDto> Items, DateTime CachedAt)> _entries = new();

    public IssueRankingCache(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.Now);

    /// <summary>組鍵：區間＋可見範圍＋主機總數。主機 id 排序後串起，順序不同的同一集合是同一鍵。</summary>
    public static string KeyOf(DateTime from, DateTime to, IReadOnlyCollection<long>? visibleHostIds, int totalHosts)
    {
        var hosts = visibleHostIds == null
            ? "*"
            : string.Join(",", visibleHostIds.OrderBy(id => id));
        return $"{from:yyyyMMdd}|{to:yyyyMMdd}|{totalHosts}|{hosts}";
    }

    /// <summary>命中回傳**副本**——呼叫端會 Take／篩選，不能讓它改到共用清單。</summary>
    public List<IssueRankingDto>? TryGet(string key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            if ((_now() - entry.CachedAt).TotalSeconds >= TtlSeconds)
            {
                _entries.Remove(key);
                return null;
            }
            return new List<IssueRankingDto>(entry.Items);
        }
    }

    public void Set(string key, List<IssueRankingDto> items)
    {
        lock (_lock)
        {
            if (_entries.Count >= MaxEntries) _entries.Clear();
            _entries[key] = (new List<IssueRankingDto>(items), _now());
        }
    }
}
