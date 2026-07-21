using System.Text;
using System.Text.Json;

namespace LogForesight;

/// <summary>
/// <see cref="IRecordHandlingStore"/> 的 JSONL 後端實作。
/// 快照存整檔型 handling.json（會更新，需原子替換），
/// 歷程存 handling_log.jsonl（append-only，逐行獨立）。
/// </summary>
public class JsonRecordHandlingStore : JsonCollectionFile<RecordHandling>, IRecordHandlingStore
{
    private readonly string _logPath;
    private readonly object _logLock = new();
    private long _lastLogId;

    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public JsonRecordHandlingStore(string snapshotPath, string logPath) : base(snapshotPath)
    {
        _logPath = logPath;
        _lastLogId = ReadAllLogs().LastOrDefault()?.LogId ?? 0;
    }

    public RecordHandling? Get(string hostName, DateTime date) =>
        Read().FirstOrDefault(h => Matches(h, hostName, date));

    public List<RecordHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to)
    {
        var names = hostNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Read()
            .Where(h => names.Contains(h.HostName) && h.Date.Date >= from.Date && h.Date.Date <= to.Date)
            .ToList();
    }

    public List<RecordHandling> GetUnresolved() =>
        Read().Where(h => HandlingStatuses.Unresolved.Contains(h.Status)).ToList();

    public void Save(RecordHandling handling)
    {
        Mutate(items =>
        {
            var existing = items.FirstOrDefault(h => Matches(h, handling.HostName, handling.Date));
            if (existing == null)
            {
                items.Add(handling);
                return;
            }

            existing.Status = handling.Status;
            existing.HandlerId = handling.HandlerId;
            existing.DueDate = handling.DueDate;
            existing.Note = handling.Note;
            existing.UpdatedAt = handling.UpdatedAt;
        });
    }

    public void AppendLog(RecordHandlingLog log)
    {
        lock (_logLock)
        {
            log.LogId = ++_lastLogId;
            if (log.CreatedAt == default) log.CreatedAt = DateTime.Now;

            File.AppendAllText(_logPath,
                JsonSerializer.Serialize(log, LogJsonOptions) + Environment.NewLine,
                new UTF8Encoding(false));
        }
    }

    public List<RecordHandlingLog> GetLogs(string hostName, DateTime date) =>
        ReadAllLogs()
            .Where(l => string.Equals(l.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
                        l.Date.Date == date.Date)
            .OrderBy(l => l.CreatedAt)
            .ThenBy(l => l.LogId)
            .ToList();

    private static bool Matches(RecordHandling handling, string hostName, DateTime date) =>
        string.Equals(handling.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
        handling.Date.Date == date.Date;

    private List<RecordHandlingLog> ReadAllLogs()
    {
        lock (_logLock)
        {
            if (!File.Exists(_logPath)) return new List<RecordHandlingLog>();

            var result = new List<RecordHandlingLog>();
            foreach (var line in File.ReadLines(_logPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var log = JsonSerializer.Deserialize<RecordHandlingLog>(line, LogJsonOptions);
                    if (log != null) result.Add(log);
                }
                catch (JsonException)
                {
                    // 逐行獨立：單行損毀只跳過該行
                }
            }
            return result;
        }
    }
}
