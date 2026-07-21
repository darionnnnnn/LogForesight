using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogForesight;

/// <summary>
/// 「整份 JSON 陣列檔」的共用存取基底（webdata\users.json、groups.json… 等 Web 自有資料）。
///
/// 為什麼要有這個類別：docs/WEB-SPEC.md §10.4 定下的兩條 JSONL 後端規則——
/// 「整檔型 .json 的寫入＝寫 temp → File.Replace 原子替換」與「每個檔案單一主要寫入者、
/// 跨程序以檔案鎖處理」——如果讓每個 store 各自實作，遲早有人漏掉原子替換那一步，
/// 寫到一半當掉就是一個半截的 JSON 檔（使用者/授權資料全毀）。規則寫在這裡一次，
/// 所有 store 繼承取得，與 RecordStorageShaper「精簡策略單點化」是同一個理由。
///
/// 換 SQL 後端時整個類別不再使用（各 store 改用 EF 實作），介面語意不受影響。
/// </summary>
public abstract class JsonCollectionFile<T> where T : class
{
    private readonly string _filePath;
    private readonly object _lock = new();

    protected JsonCollectionFile(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    protected string FilePath => _filePath;

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>讀取整份清單。檔案不存在時回空清單（首次執行的正常情況，不是錯誤）。</summary>
    protected List<T> Read()
    {
        lock (_lock)
        {
            return ReadNoLock();
        }
    }

    private List<T> ReadNoLock()
    {
        if (!File.Exists(_filePath)) return new List<T>();

        var json = File.ReadAllText(_filePath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();

        // 解析失敗時不要吞掉——這類檔案是使用者與授權資料，靜默當成空清單會讓整站看起來
        // 「沒有任何使用者」，比直接報錯難查得多（同 README 對歷史檔容錯的取捨：
        // 逐行獨立的 JSONL 可以跳過壞行，整檔型的 JSON 壞了就是壞了，必須顯性失敗）。
        return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();
    }

    /// <summary>
    /// 以讀取→修改→寫入的方式更新整份清單，全程同時持有行程內鎖與**跨程序鎖檔**，
    /// 寫入採「寫 temp → File.Replace」原子替換（中途失敗不會留下半截檔案）。
    ///
    /// 為什麼讀改寫要整段互斥、而不是只保證寫入原子：原子替換擋得住半截檔案，
    /// 擋不住**更新遺失**——兩邊各自讀到舊值、後寫的把先寫的整份蓋掉。
    /// `hosts.json` 正是批次與 Web 共同寫入的檔案（docs/WEB-SPEC.md §10.2），
    /// 後果具體且嚴重：同一個 HostId 配給兩台主機（識別碼是紀錄的關聯鍵，
    /// 撞號等於紀錄歸錯主機、跨越授權邊界），或批次的回報時間把 Web 剛設好的群組蓋掉。
    /// </summary>
    protected TResult Mutate<TResult>(Func<List<T>, TResult> mutation)
    {
        lock (_lock)
        {
            using var fileLock = AcquireCrossProcessLock();

            var items = ReadNoLock();
            var result = mutation(items);
            WriteAtomic(items);
            return result;
        }
    }

    protected void Mutate(Action<List<T>> mutation) =>
        Mutate<object?>(items => { mutation(items); return null; });

    /// <summary>
    /// 跨程序互斥：獨占開啟一個 `.lock` 附屬檔，取不到就短暫重試。
    ///
    /// 用鎖檔而不是具名 Mutex：批次由工作排程器執行、Web 是另一個行程，兩者可能不在
    /// 同一個登入工作階段——`Local\` 的 Mutex 跨不過工作階段，`Global\` 又需要額外權限
    /// （SeCreateGlobalPrivilege），一般使用者身分執行時會直接失敗。鎖檔沒有這些前提。
    ///
    /// 逾時就讓例外往外拋：等不到鎖代表另一個行程卡住，此時硬寫下去就是更新遺失——
    /// 顯性失敗遠優於靜默覆蓋（呼叫端如 Program.cs 的主機登記已有降級處理）。
    /// </summary>
    private FileStream AcquireCrossProcessLock()
    {
        var lockPath = _filePath + ".lock";
        var deadline = DateTime.UtcNow.AddSeconds(LockTimeoutSeconds);

        while (true)
        {
            try
            {
                // DeleteOnClose：正常釋放時一併移除，不在資料目錄留下累積的鎖檔
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // UnauthorizedAccessException 也要重試：另一個行程正在 DeleteOnClose 移除同一個檔案時，
                // 這一側的開啟會落在「標記為刪除中」的短暫視窗上
                if (DateTime.UtcNow >= deadline) throw;
                Thread.Sleep(LockRetryMs);
            }
        }
    }

    private const int LockTimeoutSeconds = 15;
    private const int LockRetryMs = 25;

    private void WriteAtomic(List<T> items)
    {
        var json = JsonSerializer.Serialize(items, JsonOptions);
        var tempPath = _filePath + ".tmp";

        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(_filePath))
        {
            // File.Replace 是原子操作；destinationBackupFileName 傳 null 不留備份檔
            File.Replace(tempPath, _filePath, null);
        }
        else
        {
            File.Move(tempPath, _filePath);
        }
    }

    /// <summary>新識別碼＝現有最大值 + 1（單一寫入者前提下足夠；SQL 後端改用 identity/sequence）</summary>
    protected static long NextId(IEnumerable<long> existingIds)
    {
        var ids = existingIds.ToList();
        return ids.Count == 0 ? 1 : ids.Max() + 1;
    }
}
