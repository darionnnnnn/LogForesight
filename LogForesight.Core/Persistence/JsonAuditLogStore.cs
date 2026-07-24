using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogForesight;

/// <summary>
/// 操作稽核的儲存（↔ lf_audit_logs）。
/// **只有 Append 與 Query，沒有更新或刪除**——這是稽核資料的本質要求，
/// 寫成介面約定之後，實作端就沒有「順手加個修正方法」的空間。
/// </summary>
public interface IAuditLogStore
{
    void Append(AuditEntry entry);

    PagedResult<AuditEntry> Query(AuditQuery query);

    /// <summary>指定期間內、指定動作的筆數（儀表板的「近 24 小時登入失敗」卡片用）</summary>
    int Count(DateTime from, DateTime to, string action);
}

/// <summary>
/// <see cref="IAuditLogStore"/> 的實作（log key=audit，append-only）。
///
/// 走逐列的 <see cref="IJsonLogStore"/> 而不是整份 blob：稽核是 append-only 的高頻寫入，
/// 每次都重寫整份文件會隨資料量線性變慢。
/// </summary>
public class JsonAuditLogStore : IAuditLogStore
{
    private readonly IJsonLogStore _log;
    private readonly object _lock = new();
    private long _lastId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonAuditLogStore(IJsonLogStore log)
    {
        _log = log;
        _lastId = ReadAll().LastOrDefault()?.AuditId ?? 0;
    }

    public void Append(AuditEntry entry)
    {
        lock (_lock)
        {
            entry.AuditId = ++_lastId;
            if (entry.OccurredAt == default) entry.OccurredAt = DateTime.Now;

            _log.AppendLine(JsonSerializer.Serialize(entry, JsonOptions));
        }
    }

    public PagedResult<AuditEntry> Query(AuditQuery query)
    {
        var filtered = ReadAll().Where(e => Matches(e, query))
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.AuditId)
            .ToList();

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(query.Page, 1);

        return new PagedResult<AuditEntry>
        {
            Items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = filtered.Count
        };
    }

    public int Count(DateTime from, DateTime to, string action) =>
        ReadAll().Count(e => e.OccurredAt >= from && e.OccurredAt <= to &&
                             string.Equals(e.Action, action, StringComparison.OrdinalIgnoreCase));

    private static bool Matches(AuditEntry entry, AuditQuery query)
    {
        if (query.From.HasValue && entry.OccurredAt < query.From.Value) return false;
        if (query.To.HasValue && entry.OccurredAt > query.To.Value) return false;
        if (query.UserId.HasValue && entry.UserId != query.UserId.Value) return false;
        if (query.Result.HasValue && entry.Result != query.Result.Value) return false;
        if (!string.IsNullOrEmpty(query.TargetKind) &&
            !string.Equals(entry.TargetKind, query.TargetKind, StringComparison.OrdinalIgnoreCase)) return false;
        if (query.Actions is { Count: > 0 } &&
            !query.Actions.Contains(entry.Action, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    private List<AuditEntry> ReadAll()
    {
        var result = new List<AuditEntry>();
        foreach (var line in _log.ReadLines())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions);
                if (entry != null) result.Add(entry);
            }
            catch (JsonException)
            {
                // 逐行獨立：單行損毀只跳過該行（與 history.txt 的容錯策略一致）
            }
        }
        return result;
    }
}
