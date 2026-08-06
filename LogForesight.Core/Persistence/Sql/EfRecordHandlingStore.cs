using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// <see cref="IRecordHandlingStore"/> 的真表實作（快照 ↔ lf_record_handling；
/// 歷程仍走 append-only 的 <see cref="EfJsonLogStore"/>，docs/SCALE-ISSUE-FIRST-PLAN.md P3）。
///
/// **兩個改動，各修一個體檢項目**：
///   - 快照從整份 blob 改真表（根因 B）。
///   - 歷程的**讀取**下推到 SQL（體檢 S5／規劃 N4）：舊實作的 <c>GetLogs</c> 是
///     <c>ReadAllLogs()</c> 讀回全部行再過濾，而且建構式為了取 <c>_lastLogId</c>
///     也整份讀一次——這個 store 是 Singleton，等於**站台啟動時同步讀千萬列**，
///     第一個觸發它的請求長時間無回應。改為 log_id 由資料庫序號決定、
///     GetLogs 以日期範圍先在 SQL 端窄化。
/// </summary>
public sealed class EfRecordHandlingStore : IRecordHandlingStore
{
    private readonly Func<LfDbContext> _contextFactory;
    private readonly EfJsonLogStore _logStore;
    private readonly object _logLock = new();

    /// <summary>
    /// 歷程序號。**不再於建構式整份讀取**（N4）：改為第一次寫入時才以
    /// <c>MAX(seq)</c> 之外的方式決定起點——這裡採「延遲初始化＋只讀最後一行」，
    /// 避免站台啟動被千萬列的解析卡住。
    /// </summary>
    private long? _lastLogId;

    public EfRecordHandlingStore(Func<LfDbContext> contextFactory, EfJsonLogStore logStore)
    {
        _contextFactory = contextFactory;
        _logStore = logStore;
    }

    // ── 快照 ────────────────────────────────────────────────────────────────

    public RecordHandling? Get(string hostName, DateTime date)
    {
        var key = HostNameKey.Of(hostName);
        var day = date.Date;

        using var ctx = _contextFactory();
        var row = ctx.RecordHandlings.AsNoTracking()
            .FirstOrDefault(h => h.HostNameKey == key && h.RecordDate == day);
        return row == null ? null : ToModel(row);
    }

    public List<RecordHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to)
    {
        var keys = hostNames.Select(HostNameKey.Of).Distinct().ToList();
        if (keys.Count == 0) return new List<RecordHandling>();

        var f = from.Date;
        var t = to.Date;

        using var ctx = _contextFactory();
        return ctx.RecordHandlings.AsNoTracking()
            .Where(h => keys.Contains(h.HostNameKey) && h.RecordDate >= f && h.RecordDate <= t)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<RecordHandling> GetUnresolved()
    {
        // Unresolved 的定義在 Core 的 HandlingStatuses，下推成 IN 條件（EF 8：單一 JSON 參數）
        var unresolved = HandlingStatuses.Unresolved.ToList();

        using var ctx = _contextFactory();
        return ctx.RecordHandlings.AsNoTracking()
            .Where(h => unresolved.Contains(h.Status))
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<RecordHandling> GetByHandler(long userId)
    {
        using var ctx = _contextFactory();
        return ctx.RecordHandlings.AsNoTracking()
            .Where(h => h.HandlerId == userId)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public void Save(RecordHandling handling)
    {
        var key = HostNameKey.Of(handling.HostName);
        var day = handling.Date.Date;

        using var ctx = _contextFactory();
        var row = ctx.RecordHandlings.FirstOrDefault(h => h.HostNameKey == key && h.RecordDate == day);

        if (row == null)
        {
            row = new RecordHandlingRow { HostName = handling.HostName, HostNameKey = key, RecordDate = day };
            ctx.RecordHandlings.Add(row);
        }

        row.HostName = handling.HostName;
        row.Status = handling.Status;
        row.HandlerId = handling.HandlerId;
        row.DueDate = handling.DueDate;
        row.Note = handling.Note;
        row.UpdatedAt = handling.UpdatedAt;

        ctx.SaveChanges();
    }

    // ── 歷程（append-only，仍走 lf_log_lines）────────────────────────────────

    public void AppendLog(RecordHandlingLog log)
    {
        lock (_logLock)
        {
            // 首次寫入才初始化序號（N4：不在建構式整份讀，改為只讀最後一行）
            _lastLogId ??= ReadLastLogId();

            var next = _lastLogId.Value + 1;
            _lastLogId = next;
            log.LogId = next;
            if (log.CreatedAt == default) log.CreatedAt = DateTime.Now;

            _logStore.AppendLine(JsonSerializer.Serialize(log, LfJsonOptions.Compact));
        }
    }

    /// <summary>
    /// 單一風險日的處理歷程。**先在 SQL 端以插入時間窄化候選集**再於記憶體精確比對——
    /// 歷程列的業務日期在 JSON 內容裡（不是欄位），無法直接下推；但一天的歷程必然寫在
    /// 那一天之後，用插入時間當粗篩就能把千萬列縮到幾百列。
    ///
    /// 下界取業務日期當天 00:00（歷程不可能寫在事件發生之前）；上界不設——
    /// 使用者可能在事件發生後很久才回頭標記。
    /// </summary>
    public List<RecordHandlingLog> GetLogs(string hostName, DateTime date)
    {
        var lines = _logStore.ReadLines(from: date.Date, to: null);

        return JsonLogParser.Parse<RecordHandlingLog>(lines, LfJsonOptions.Compact)
            .Where(l => string.Equals(l.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
                        l.Date.Date == date.Date)
            .OrderBy(l => l.CreatedAt)
            .ThenBy(l => l.LogId)
            .ToList();
    }

    /// <summary>續號起點＝最後一行的 LogId；沒有歷程或最後一行壞掉時從 0 起算</summary>
    private long ReadLastLogId()
    {
        var line = _logStore.ReadLastLine();
        if (string.IsNullOrWhiteSpace(line)) return 0;

        var parsed = JsonLogParser.Parse<RecordHandlingLog>(new[] { line }, LfJsonOptions.Compact);
        return parsed.Count > 0 ? parsed[0].LogId : 0;
    }

    private static RecordHandling ToModel(RecordHandlingRow row) => new()
    {
        HostName = row.HostName,
        Date = row.RecordDate,
        Status = row.Status,
        HandlerId = row.HandlerId,
        DueDate = row.DueDate,
        Note = row.Note,
        UpdatedAt = row.UpdatedAt
    };
}
