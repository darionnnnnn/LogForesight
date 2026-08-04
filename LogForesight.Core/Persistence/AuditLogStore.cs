using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogForesight.Sql;

namespace LogForesight;

/// <summary>
/// 操作稽核的儲存（↔ lf_audit_logs，log key=audit，append-only）。
/// **只有 Append 與 Query，沒有更新或刪除**——這是稽核資料的本質要求。
///
/// 走逐列的 <see cref="EfJsonLogStore"/> 而不是整份 blob：稽核是 append-only 的高頻寫入，
/// 每次都重寫整份文件會隨資料量線性變慢。
/// </summary>
public class AuditLogStore
{
    private readonly EfJsonLogStore _log;
    private readonly object _lock = new();
    private long _lastId;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public AuditLogStore(EfJsonLogStore log)
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
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(query.Page, 1);

        // 完全沒有篩選條件：純粹分頁瀏覽全部，SQL 端直接分頁，不必先讀全表
        // （docs/archive/HISTORY.md P1-2——稽核頁預設檢視就是這個情境，2000 台規模下
        // 稽核表可能有數十萬列，每次開頁都整表讀回記憶體會是第一個效能瓶頸）。
        // 只要有任何篩選條件就不走這條路：From/To 篩選涉及 CreatedAt 為 null 的既存列
        // （schema 升級前寫入）在 SQL 端無法精確判斷是否落在範圍內，全表無篩選時這個顧慮不存在。
        var noFilter = query.From == null && query.To == null && query.UserId == null &&
                       query.Result == null && string.IsNullOrEmpty(query.TargetKind) &&
                       query.Actions is not { Count: > 0 };

        if (noFilter)
        {
            var (lines, total) = _log.ReadPage((page - 1) * pageSize, pageSize, query.Ascending);
            return new PagedResult<AuditEntry>
            {
                Items = JsonLogParser.Parse<AuditEntry>(lines, JsonOptions),
                Page = page,
                PageSize = pageSize,
                Total = total
            };
        }

        // 有篩選條件：以日期範圍先在 SQL 端窄化候選集（沒有時間戳記的既存列一律視為候選，
        // 精確判斷交給下面的 Matches），其餘欄位在記憶體過濾——仍避免了讀回「範圍外」的舊資料
        var ordered = JsonLogParser.Parse<AuditEntry>(_log.ReadLines(query.From, query.To), JsonOptions)
            .Where(e => Matches(e, query));
        var filtered = (query.Ascending
                ? ordered.OrderBy(e => e.OccurredAt).ThenBy(e => e.AuditId)
                : ordered.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.AuditId))
            .ToList();

        return new PagedResult<AuditEntry>
        {
            Items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = filtered.Count
        };
    }

    /// <summary>指定期間內、指定動作的筆數（儀表板的「近 24 小時登入失敗」卡片用）</summary>
    public int Count(DateTime from, DateTime to, string action) =>
        JsonLogParser.Parse<AuditEntry>(_log.ReadLines(from, to), JsonOptions)
            .Count(e => e.OccurredAt >= from && e.OccurredAt <= to &&
                        string.Equals(e.Action, action, StringComparison.OrdinalIgnoreCase));

    /// <summary>清除超過保留天數的稽核紀錄，回傳刪除筆數（docs/archive/HISTORY.md P0-3）</summary>
    public int Prune(int retentionDays) => _log.Prune(DateTime.Today.AddDays(-retentionDays));

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

    private List<AuditEntry> ReadAll() => JsonLogParser.Parse<AuditEntry>(_log.ReadLines(), JsonOptions);
}
