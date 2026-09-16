using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>
/// PRTG 裝置索引（objid → 裝置、正規化 IP → 同 IP 裝置清單）的唯讀快照。
/// 內容一旦建好就不再改動，因此快取可以直接把同一份實例交給每個呼叫端，不必逐次複製
/// （數千台裝置的字典複製並不便宜，而它本來就只是被查詢、不被修改）。
/// </summary>
public sealed class PrtgDeviceIndex
{
    private static readonly IReadOnlyList<PrtgDeviceRow> Empty = Array.Empty<PrtgDeviceRow>();

    private readonly Dictionary<long, PrtgDeviceRow> _byObjid;
    private readonly Dictionary<string, List<PrtgDeviceRow>> _byNormIp;

    public PrtgDeviceIndex(IReadOnlyList<PrtgDeviceRow> devices)
    {
        _byObjid = new Dictionary<long, PrtgDeviceRow>();
        _byNormIp = new Dictionary<string, List<PrtgDeviceRow>>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in devices)
        {
            // objid 是鏡像表的主鍵，理論上不重覆；用索引子而非 Add，重覆時取後者而不是炸掉畫面
            _byObjid[d.Objid] = d;

            // 正規化回 null 代表「不是合法 IP」——不進索引，免得兩邊都 null 時湊出假命中
            var normIp = PrtgHostMapper.NormalizeIp(d.Ip);
            if (normIp == null) continue;
            if (!_byNormIp.TryGetValue(normIp, out var list))
            {
                list = new List<PrtgDeviceRow>();
                _byNormIp[normIp] = list;
            }
            list.Add(d);
        }
    }

    /// <summary>裝置台數（建索引時的來源列數；診斷與測試用）</summary>
    internal int DeviceCount => _byObjid.Count;

    /// <summary>依 objid 取裝置；不存在回 null</summary>
    public PrtgDeviceRow? ByObjid(long objid) =>
        _byObjid.TryGetValue(objid, out var d) ? d : null;

    /// <summary>取同一個正規化 IP 上的全部裝置；normIp 為 null 或查無時回空清單</summary>
    public IReadOnlyList<PrtgDeviceRow> ByNormIp(string? normIp) =>
        normIp != null && _byNormIp.TryGetValue(normIp, out var list) ? list : Empty;
}

/// <summary>
/// PRTG 裝置索引的跨請求快取（回饋四十五輪 B5）。
///
/// 為什麼需要它：PRTG 衝突清單每翻一頁都要把裝置全表讀回來、重建兩份索引，
/// 只為了替該頁的二十列填上裝置名稱、群組路徑與同 IP 候選。一頁換一次全表讀取，
/// 而裝置表是結構鏡像、一天只在夜間同步時變動一次。
///
/// **刻意不綁資料版本戳**（不是漏綁）：PRTG 鏡像是夜間批次直接寫資料庫、不經過 HTTP 管線，
/// <see cref="DataVersionStamp"/> 根本不會被推進，綁了也不會因為鏡像更新而失效——
/// 只會多一個永遠不動的鍵維度，製造「有在管新鮮度」的錯覺。
/// 這份索引只影響衝突清單的衝突型別判定與候選主機顯示，慢個一個 TTL 無害，
/// 新鮮度就單純由 TTL 負責。
///
/// **內容與使用者無關**：裝置索引是 PRTG 鏡像的全站結構快照，不含任何可見範圍資訊；
/// 可見範圍的過濾發生在呼叫端各自的授權檢查，不在這份索引裡。
/// （若哪天索引本身要帶使用者維度，就必須改成鍵含授權範圍，不能沿用這個無鍵版本。）
/// </summary>
public class PrtgDeviceIndexCache
{
    /// <summary>存活秒數：與 <see cref="IssueRankingCache.TtlSeconds"/> 同值同理由
    /// （全站快取的節奏一致）。</summary>
    public const int TtlSeconds = IssueRankingCache.TtlSeconds;

    /// <summary>條目上限：比照 <see cref="SummaryCache"/>／<see cref="IssueOwnedHostIdsCache"/>，
    /// 超過上限整批清掉、重建付得起。目前呼叫端只用一個固定鍵
    /// （<see cref="GlobalKey"/>，索引與使用者無關），上限只是沿用慣例的保險。</summary>
    private const int MaxEntries = 64;

    /// <summary>唯一的鍵：索引是全站共用的結構快照，沒有使用者或設定維度可分。</summary>
    public const string GlobalKey = "all-devices";

    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, (PrtgDeviceIndex Index, DateTime CachedAt)> _entries = new();

    public PrtgDeviceIndexCache(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>
    /// 取快取；未命中（不存在／逾時）時呼叫 <paramref name="loadDevices"/> 讀一次裝置全表、
    /// 建好索引再存起來。回傳的 <see cref="PrtgDeviceIndex"/> 是唯讀快照（見該類別註解）。
    /// </summary>
    public PrtgDeviceIndex GetOrAdd(string key, Func<IReadOnlyList<PrtgDeviceRow>> loadDevices)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry)
                && (_now() - entry.CachedAt).TotalSeconds < TtlSeconds)
            {
                return entry.Index;
            }
        }

        // 讀表與建索引刻意在鎖外做：全表讀取很慢，握著鎖算會讓其他請求全部排隊
        // （同 SummaryCache 的既有取捨：同時進來的請求可能各建一次，結果相同）
        var index = new PrtgDeviceIndex(loadDevices());

        lock (_lock)
        {
            if (_entries.Count >= MaxEntries) _entries.Clear();
            _entries[key] = (index, _now());
        }

        return index;
    }
}
