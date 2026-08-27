using System.Text;
using System.Text.Json;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 「整份 JSON 陣列」的共用存取基底（users、hosts、host_groups… 等 webdata blob）。
///
/// 為什麼要有這個類別：整份型資料的兩條規則——「原子更新（不留半截）」與「讀改寫整段
/// 互斥（不遺失更新）」——如果讓每個 store 各自實作，遲早有人漏掉。規則寫在這裡一次，
/// 所有 store 繼承取得，與 RecordStorageShaper「精簡策略單點化」是同一個理由。
///
/// 底層一律走 <see cref="EfJsonBlobStore"/>（SQLite 測試／SqlServer 正式），
/// store 的方法本體完全不受後端影響。
/// </summary>
public abstract class JsonBlobCollection<T> where T : class
{
    private readonly EfJsonBlobStore _blob;
    private readonly bool _cached;
    private readonly object _cacheLock = new();

    /// <summary>快取內容與它對應的版本，**綁在同一個不可變物件上**：兩個獨立欄位的話，
    /// 讀取端可能看到「新清單配舊版本」（或反之）的撕裂組合，那會讓快取永遠不再更新。</summary>
    private sealed record CacheSnapshot(List<T> Items, long Version);

    /// <summary>volatile：命中路徑刻意不進 <see cref="_cacheLock"/>（見 <see cref="Read"/>），
    /// 需要保證別的執行緒剛換上的快照立刻看得到。</summary>
    private volatile CacheSnapshot? _snapshot;

    protected JsonBlobCollection(EfJsonBlobStore blob, bool cached = false)
    {
        _blob = blob;
        _cached = cached;
    }

    /// <summary>供 log／Location 顯示（相容原本的名稱；DB 後端回傳的是位置描述而非真實路徑）</summary>
    protected string FilePath => _blob.Location;

    /// <summary>目前資料版本（每次寫入前進一號）。只查一個整數欄、不拉整份內容，
    /// 供上層判定「用這份資料建出來的索引要不要重建」——與 <see cref="Read"/> 同一個探測點。</summary>
    protected long CurrentVersion => _blob.ReadVersion();

    /// <summary>
    /// 讀取整份清單。內容不存在時回空清單（首次執行的正常情況，不是錯誤）。
    /// <para>為什麼要快取：主機清單（3000 台約 4 MB）在單一請求內會被讀取十幾次，若無快取反序列化成本極高。</para>
    /// <para>為什麼每次都探測版本不設 TTL：主機清單是授權可見範圍的來源，過期快取會導致使用者看到不該看的主機。探測成本在 SQLite 是本機檔案存取（微秒級），在 SqlServer 是一次網路往返（次毫秒級）——仍遠低於反序列化整份 blob，但不是零；實際次數與耗時見效能監控的 <c>blob:{key}:ReadVersion</c>。</para>
    /// <para>注意：呼叫端不可修改回傳清單中的物件屬性！回傳的淺複製只保護清單本身（增刪排序安全），但物件是共用的參考。</para>
    /// <para><b>命中路徑不進鎖</b>：版本探測與快照比對都在鎖外完成，只有真的要重新載入時才鎖。
    /// 若連命中也要搶同一把鎖，單一請求十幾次的 <c>GetAll()</c> 加上多人併發就全部排隊在這裡——
    /// 反序列化是省掉了，卻換來一個新的咽喉點，等於抵消掉加快取的目的。</para>
    /// </summary>
    protected List<T> Read() => new(ReadSnapshot());

    /// <summary>
    /// 與 <see cref="Read"/> 同一條讀取路徑，但**不複製清單**——供「只是要查一筆」的呼叫端使用
    /// （回饋三十四輪 A5）：3682 台規模下，逐主機迴圈裡每次查一台都複製整份清單，
    /// 光是配置就是可觀的 GC 壓力。回傳的是共用快照，呼叫端只能讀。
    /// </summary>
    protected IReadOnlyList<T> ReadSnapshot()
    {
        if (!_cached) return Deserialize(_blob.Read());

        // 鎖外：版本探測是單列主鍵查詢，快照是不可變物件，兩者都不需要互斥
        var version = _blob.ReadVersion();
        var snapshot = _snapshot;
        if (snapshot != null && snapshot.Version == version) return snapshot.Items;

        lock (_cacheLock)
        {
            // 雙重檢查：等鎖期間可能已有別的執行緒載入同一版本，不必重做一次 4 MB 的反序列化
            var current = _snapshot;
            if (current == null || current.Version != version)
            {
                // 用 ReadWithVersion 而不是沿用上面探測到的 version：兩次讀之間內容可能又被改過，
                // 拿舊版本號配新內容存進快照，快取就永遠不會再更新
                var (content, loadedVersion) = _blob.ReadWithVersion();
                current = new CacheSnapshot(Deserialize(content), loadedVersion);
                _snapshot = current;
            }
            return current.Items;
        }
    }

    /// <summary>
    /// 讀→改→寫的原子更新（底層保證整段互斥、不留半截、不遺失更新，見 <see cref="EfJsonBlobStore"/>）。
    /// 為什麼要整段互斥而非只保證寫入原子：原子替換擋得住半截檔案，擋不住**更新遺失**——
    /// hosts.json 是批次與 Web 共同寫入的檔案，撞號等於紀錄歸錯主機、跨越授權邊界。
    /// </summary>
    protected TResult Mutate<TResult>(Func<List<T>, TResult> mutation) =>
        _blob.Mutate(raw =>
        {
            var items = Deserialize(raw);
            var result = mutation(items);
            return (JsonSerializer.Serialize(items, LfJsonOptions.Pretty), result);
        });

    protected void Mutate(Action<List<T>> mutation) =>
        Mutate<object?>(items => { mutation(items); return null; });

    /// <summary>解析整份清單。解析失敗不吞——使用者/授權資料靜默當空會讓整站看起來「沒有任何使用者」，比報錯難查。</summary>
    private static List<T> Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new List<T>()
            : JsonSerializer.Deserialize<List<T>>(json, LfJsonOptions.Pretty) ?? new List<T>();

    /// <summary>新識別碼＝現有最大值 + 1（單一寫入者前提下足夠；SQL 後端亦沿用，寫入仍走整段互斥）</summary>
    protected static long NextId(IEnumerable<long> existingIds)
    {
        var ids = existingIds.ToList();
        return ids.Count == 0 ? 1 : ids.Max() + 1;
    }
}
