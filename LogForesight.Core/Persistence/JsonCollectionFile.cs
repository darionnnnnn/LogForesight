using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogForesight;

/// <summary>
/// 「整份 JSON 陣列」的共用存取基底（webdata\users.json、groups.json… 等 Web 自有資料）。
///
/// 為什麼要有這個類別：整份型資料的兩條規則——「原子更新（不留半截）」與「讀改寫整段
/// 互斥（不遺失更新）」——如果讓每個 store 各自實作，遲早有人漏掉。規則寫在這裡一次，
/// 所有 store 繼承取得，與 RecordStorageShaper「精簡策略單點化」是同一個理由。
///
/// 2026-07-23 起底層改走 <see cref="IJsonBlobStore"/>：文字放檔案（現行）或資料庫
/// （SQLite 測試／SqlServer 正式）由注入的 blob 決定，store 的方法本體完全不變。
/// </summary>
public abstract class JsonCollectionFile<T> where T : class
{
    private readonly IJsonBlobStore _blob;

    /// <summary>檔案後端的便利建構子（沿用原本以路徑建立的呼叫端與測試）</summary>
    protected JsonCollectionFile(string filePath) : this(new FileJsonBlobStore(filePath)) { }

    /// <summary>指定底層（檔案或 DB）</summary>
    protected JsonCollectionFile(IJsonBlobStore blob) => _blob = blob;

    /// <summary>供 log／Location 顯示（相容原本的名稱；DB 後端回傳的是位置描述而非真實路徑）</summary>
    protected string FilePath => _blob.Location;

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>讀取整份清單。內容不存在時回空清單（首次執行的正常情況，不是錯誤）。</summary>
    protected List<T> Read() => Deserialize(_blob.Read());

    /// <summary>
    /// 讀→改→寫的原子更新（底層保證整段互斥、不留半截、不遺失更新，見 <see cref="IJsonBlobStore"/>）。
    /// 為什麼要整段互斥而非只保證寫入原子：原子替換擋得住半截檔案，擋不住**更新遺失**——
    /// hosts.json 是批次與 Web 共同寫入的檔案，撞號等於紀錄歸錯主機、跨越授權邊界。
    /// </summary>
    protected TResult Mutate<TResult>(Func<List<T>, TResult> mutation) =>
        _blob.Mutate(raw =>
        {
            var items = Deserialize(raw);
            var result = mutation(items);
            return (JsonSerializer.Serialize(items, JsonOptions), result);
        });

    protected void Mutate(Action<List<T>> mutation) =>
        Mutate<object?>(items => { mutation(items); return null; });

    /// <summary>解析整份清單。解析失敗不吞——使用者/授權資料靜默當空會讓整站看起來「沒有任何使用者」，比報錯難查。</summary>
    private static List<T> Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new List<T>()
            : JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();

    /// <summary>新識別碼＝現有最大值 + 1（單一寫入者前提下足夠；SQL 後端亦沿用，寫入仍走整段互斥）</summary>
    protected static long NextId(IEnumerable<long> existingIds)
    {
        var ids = existingIds.ToList();
        return ids.Count == 0 ? 1 : ids.Max() + 1;
    }
}
