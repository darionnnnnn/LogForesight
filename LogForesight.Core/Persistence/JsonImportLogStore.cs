using System.Text;
using System.Text.Json;

namespace LogForesight;

/// <summary>匯入紀錄（↔ lf_import_logs）：誰、何時、匯了什麼</summary>
public class ImportLogEntry
{
    public long ImportId { get; set; }
    public long? UserId { get; set; }
    public string Account { get; set; } = string.Empty;

    /// <summary>Users | Hosts | GroupAccess</summary>
    public string Kind { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
    public int AddedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int RemovedCount { get; set; }
    public List<string> CreatedGroups { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public interface IImportLogStore
{
    void Append(ImportLogEntry entry);

    /// <summary>近期匯入紀錄，新到舊</summary>
    List<ImportLogEntry> GetRecent(int count);
}

/// <summary>JSONL 後端實作：webdata\import_logs.jsonl（append-only）</summary>
public class JsonImportLogStore : IImportLogStore
{
    private readonly IJsonLogStore _log;
    private readonly object _lock = new();
    private long _lastId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public JsonImportLogStore(string filePath) : this(new FileJsonLogStore(filePath)) { }

    public JsonImportLogStore(IJsonLogStore log)
    {
        _log = log;
        _lastId = ReadAll().LastOrDefault()?.ImportId ?? 0;
    }

    public void Append(ImportLogEntry entry)
    {
        lock (_lock)
        {
            entry.ImportId = ++_lastId;
            _log.AppendLine(JsonSerializer.Serialize(entry, JsonOptions));
        }
    }

    public List<ImportLogEntry> GetRecent(int count) =>
        ReadAll().OrderByDescending(e => e.CreatedAt).Take(count).ToList();

    private List<ImportLogEntry> ReadAll()
    {
        var result = new List<ImportLogEntry>();
        foreach (var line in _log.ReadLines())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<ImportLogEntry>(line, JsonOptions);
                if (entry != null) result.Add(entry);
            }
            catch (JsonException)
            {
                // 逐行獨立：單行損毀只跳過該行
            }
        }
        return result;
    }
}
