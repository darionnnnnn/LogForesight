using System.Text;
using System.Text.Json;
using LogForesight.Sql;

namespace LogForesight;

/// <summary>匯入紀錄（↔ lf_import_logs）：誰、何時、匯了什麼</summary>
public class ImportLogEntry
{
    public long ImportId { get; set; }
    public long? UserId { get; set; }
    public string Account { get; set; } = string.Empty;

    /// <summary>Users | Hosts | GroupAccess | Owners | Netiq</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>CSV 匯入＝檔名；NetIQ 掃描匯入＝所屬 Sentinel 名稱（沒有檔案可言，借用這個欄位顯示來源）</summary>
    public string FileName { get; set; } = string.Empty;
    public int AddedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int RemovedCount { get; set; }

    /// <summary>NetIQ 掃描匯入專用：孤兒主機重疊而復活重綁的台數（CSV 匯入恆為 0）</summary>
    public int RevivedCount { get; set; }

    public List<string> CreatedGroups { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public interface IImportLogStore
{
    void Append(ImportLogEntry entry);

    /// <summary>近期匯入紀錄，新到舊</summary>
    List<ImportLogEntry> GetRecent(int count);

    /// <summary>清除超過保留天數的匯入紀錄，回傳刪除筆數（docs/OPS-HARDENING-PLAN.md P0-3）</summary>
    int Prune(int retentionDays);
}

/// <summary><see cref="IImportLogStore"/> 的實作（log key=import_logs，append-only）</summary>
public class ImportLogStore : IImportLogStore
{
    private readonly EfJsonLogStore _log;
    private readonly object _lock = new();
    private long _lastId;

    public ImportLogStore(EfJsonLogStore log)
    {
        _log = log;
        _lastId = ReadAll().LastOrDefault()?.ImportId ?? 0;
    }

    public void Append(ImportLogEntry entry)
    {
        lock (_lock)
        {
            entry.ImportId = ++_lastId;
            _log.AppendLine(JsonSerializer.Serialize(entry, LfJsonOptions.Compact));
        }
    }

    public List<ImportLogEntry> GetRecent(int count) =>
        ReadAll().OrderByDescending(e => e.CreatedAt).Take(count).ToList();

    public int Prune(int retentionDays) => _log.Prune(DateTime.Today.AddDays(-retentionDays));

    private List<ImportLogEntry> ReadAll() => JsonLogParser.Parse<ImportLogEntry>(_log.ReadLines(), LfJsonOptions.Compact);
}
