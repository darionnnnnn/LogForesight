using System.Text.Json;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 「整份 JSON 存一筆」的單一物件型 store 共用基底（system_settings、schedule_options、
/// netiq_options 等 webdata blob）——本體是單一物件而非清單，故不繼承
/// <see cref="JsonBlobCollection{T}"/>（它的 Read/Mutate 是針對 List&lt;T&gt; 設計的）。
///
/// 讀→改→寫的原子更新（同 <see cref="JsonBlobCollection{T}.Mutate"/> 的互斥保證）與「內容不存在
/// 時回預設值」的語意集中在這裡一次，避免每個單一物件型 store 各自重寫一份幾乎相同的邏輯。
/// </summary>
public abstract class JsonBlobSingleton<T> where T : new()
{
    private readonly EfJsonBlobStore _blob;
    private readonly bool _cached;
    private readonly object _cacheLock = new();

    /// <summary>快取的**原始內容**與它對應的版本，綁在同一個不可變物件上：兩個獨立欄位的話，
    /// 讀取端可能看到「新內容配舊版本」（或反之）的撕裂組合，那會讓快取永遠不再更新
    /// （同 <see cref="JsonBlobCollection{T}"/> 的理由）。</summary>
    private sealed record CacheSnapshot(string? Content, long Version);

    /// <summary>volatile：命中路徑刻意不進 <see cref="_cacheLock"/>（見 <see cref="Get"/>），
    /// 需要保證別的執行緒剛換上的快照立刻看得到。</summary>
    private volatile CacheSnapshot? _snapshot;

    /// <param name="cached">是否啟用版本探測快取。預設關閉——只有真的被高頻讀取的 store
    /// （目前僅 system_settings）才開，其餘子類維持原本每次讀 DB 的行為。</param>
    protected JsonBlobSingleton(EfJsonBlobStore blob, bool cached = false)
    {
        _blob = blob;
        _cached = cached;
    }

    /// <summary>
    /// 讀取整份設定。內容不存在時回預設值（首次執行的正常情況，不是錯誤）。
    /// <para>快取機制比照 <see cref="JsonBlobCollection{T}"/>：每次仍探測版本（單列整數欄，
    /// 不拉內容），版本沒變就不必再讀一次整份 blob；真的要重載時才進鎖並做雙重檢查，
    /// 且用 <c>ReadWithVersion</c> 一次取得內容與版本，而不是沿用上面探測到的版本
    /// （兩次讀之間內容可能又被改過，舊版本號配新內容會讓快取永遠不再更新）。</para>
    /// <para><b>與 collection 的關鍵差異：這裡快取的是原始內容（字串），命中時仍各自
    /// 反序列化出全新的物件。</b>單一物件型 store 的呼叫端會做「讀→改→寫」
    /// （<c>SystemSettingsService</c> 明講它假設每次 <c>Get()</c> 都是不同執行個體），
    /// 若共用同一個實例，讀改寫的前後快照會變成同一個物件、把「有沒有變」的比較變成恆等。
    /// 因此只省掉「讀取 blob 內容」這一趟，不省反序列化——安全性優先於再多省一點。</para>
    /// <para><b>命中路徑不進鎖</b>：版本探測與快照比對都在鎖外完成，避免單一請求數十次的
    /// 設定讀取全部排隊在同一把鎖上，反而製造新的咽喉點。</para>
    /// </summary>
    public T Get()
    {
        if (!_cached) return Deserialize(_blob.Read());

        // 鎖外：版本探測是單列主鍵查詢，快照是不可變物件，兩者都不需要互斥
        var version = _blob.ReadVersion();
        var snapshot = _snapshot;
        if (snapshot != null && snapshot.Version == version) return Deserialize(snapshot.Content);

        lock (_cacheLock)
        {
            // 雙重檢查：等鎖期間可能已有別的執行緒載入同一版本，不必重讀一次整份 blob
            var current = _snapshot;
            if (current == null || current.Version != version)
            {
                var (content, loadedVersion) = _blob.ReadWithVersion();
                current = new CacheSnapshot(content, loadedVersion);
                _snapshot = current;
            }
            return Deserialize(current.Content);
        }
    }

    /// <summary>mutation 直接修改傳入的物件；成功後由 <see cref="Touch"/> 蓋章（如 UpdatedAt）</summary>
    public T Update(Action<T> mutation) =>
        _blob.Mutate(raw =>
        {
            var value = Deserialize(raw);
            mutation(value);
            Touch(value);
            return (JsonSerializer.Serialize(value, LfJsonOptions.Pretty), value);
        });

    /// <summary>Update 成功後的蓋章動作（例如 UpdatedAt=DateTime.Now）；預設不做事</summary>
    protected virtual void Touch(T value) { }

    /// <summary>反序列化後的掛勾（例如舊設定遷移）；預設不做事</summary>
    protected virtual void OnDeserialized(T value) { }

    /// <summary>內容不存在（首次執行）時回預設值——沿用型別的欄位預設值</summary>
    private T Deserialize(string? json)
    {
        var value = string.IsNullOrWhiteSpace(json)
            ? new T()
            : JsonSerializer.Deserialize<T>(json, LfJsonOptions.Pretty) ?? new T();
        OnDeserialized(value);
        return value;
    }
}
