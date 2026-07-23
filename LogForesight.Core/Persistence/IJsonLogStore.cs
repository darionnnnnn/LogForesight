using System.Text;

namespace LogForesight;

/// <summary>
/// append-only 逐行 JSONL 的讀寫底層（稽核、執行紀錄、匯入紀錄、處理歷程…）。
///
/// 與 <see cref="IJsonBlobStore"/> 分開的理由：這些是**高頻附加**的資料，
/// 每次都重寫整份內容會隨資料量線性變慢（history.txt 選 JSONL 的同一個理由）。
/// 因此提供 O(1) 的 <see cref="AppendLine"/>：檔案版 File.AppendAllText、DB 版 INSERT 一列。
/// 讀取一律回全部行，呼叫端逐行解析（單行損毀跳過，容錯與 history.txt 一致）。
/// </summary>
public interface IJsonLogStore
{
    string Location { get; }

    /// <summary>全部行，依附加順序</summary>
    IReadOnlyList<string> ReadLines();

    /// <summary>附加一行（O(1)）</summary>
    void AppendLine(string line);
}

/// <summary>檔案後端：沿用原 append-only JSONL 行為（File.AppendAllText／File.ReadLines）</summary>
public sealed class FileJsonLogStore : IJsonLogStore
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public FileJsonLogStore(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public string Location => _filePath;

    public IReadOnlyList<string> ReadLines()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return Array.Empty<string>();
            return File.ReadAllLines(_filePath, Encoding.UTF8);
        }
    }

    public void AppendLine(string line)
    {
        lock (_lock)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}
