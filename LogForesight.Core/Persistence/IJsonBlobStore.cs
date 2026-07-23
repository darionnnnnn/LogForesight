using System.Text;

namespace LogForesight;

/// <summary>
/// 「一團 JSON 文字」的原子讀寫（webdata 各 store 的儲存底層）。
///
/// 抽出這一層的理由：webdata 的每個 store 最終都是讀/寫一段文字（整份 JSON 陣列、單一
/// JSON 文件、或逐行 JSONL）。把「文字放哪裡、怎麼原子更新」與「store 的業務邏輯」
/// 分開，同一份 store 邏輯就能跑在檔案（現行）或資料庫（SQLite 測試／SqlServer 正式）上——
/// store 的方法本體完全不變（docs/SCALE-2000-PLAN.md §4）。
///
/// <see cref="Mutate{TResult}"/> 是讀→改→寫的原子單位：檔案版以鎖檔＋原子替換實作，
/// DB 版以交易實作。呼叫端拿到目前內容、算出新內容，底層保證中途不被別人插入寫入
/// （避免更新遺失——hosts.json 是批次與 Web 共同寫入的檔案，這點是正確性關鍵）。
/// </summary>
public interface IJsonBlobStore
{
    /// <summary>供 log／Location 顯示（檔案為路徑、DB 為「sqlserver:users」之類）</summary>
    string Location { get; }

    /// <summary>目前內容；不存在回 null（首次執行的正常情況）</summary>
    string? Read();

    /// <summary>讀→改→寫的原子操作。mutation 收目前內容、回 (新內容, 結果)</summary>
    TResult Mutate<TResult>(Func<string?, (string content, TResult result)> mutation);
}

/// <summary>
/// 檔案後端：完整沿用原 <see cref="JsonCollectionFile{T}"/> 的行為——行程內鎖＋跨程序鎖檔
/// ＋「寫 temp → File.Replace」原子替換。行為與重構前逐位相同（由既有 JSONL 測試保證）。
/// </summary>
public sealed class FileJsonBlobStore : IJsonBlobStore
{
    private readonly string _filePath;
    private readonly object _lock = new();

    private const int LockTimeoutSeconds = 15;
    private const int LockRetryMs = 25;

    public FileJsonBlobStore(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public string Location => _filePath;

    public string? Read()
    {
        lock (_lock)
        {
            return ReadNoLock();
        }
    }

    public TResult Mutate<TResult>(Func<string?, (string content, TResult result)> mutation)
    {
        lock (_lock)
        {
            using var fileLock = AcquireCrossProcessLock();

            var current = ReadNoLock();
            var (content, result) = mutation(current);
            WriteAtomic(content);
            return result;
        }
    }

    private string? ReadNoLock()
    {
        if (!File.Exists(_filePath)) return null;
        var text = File.ReadAllText(_filePath, Encoding.UTF8);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private void WriteAtomic(string content)
    {
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(_filePath))
            File.Replace(tempPath, _filePath, null);   // 原子；不留備份
        else
            File.Move(tempPath, _filePath);
    }

    /// <summary>跨程序互斥：獨占開啟 .lock 附屬檔（DeleteOnClose）；逾時讓例外外拋（見原註解）</summary>
    private FileStream AcquireCrossProcessLock()
    {
        var lockPath = _filePath + ".lock";
        var deadline = DateTime.UtcNow.AddSeconds(LockTimeoutSeconds);

        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) throw;
                Thread.Sleep(LockRetryMs);
            }
        }
    }
}
