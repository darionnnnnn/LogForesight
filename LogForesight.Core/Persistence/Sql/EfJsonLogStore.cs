using Microsoft.EntityFrameworkCore;

namespace LogForesight.Sql;

/// <summary>
/// append-only 逐行紀錄的讀寫底層（稽核、執行紀錄、匯入紀錄、處理歷程…）：逐行存 lf_log_lines
/// （log_key＋自增 seq＋line）。與 <see cref="EfJsonBlobStore"/> 分開的理由：這些是**高頻附加**
/// 的資料，每次都重寫整份內容會隨資料量線性變慢。<see cref="AppendLine"/> 是 O(1) 的 INSERT 一列；
/// <see cref="ReadLines()"/> 依 seq 排序讀回全部行，呼叫端逐行解析（單行損毀跳過）。
/// </summary>
public sealed class EfJsonLogStore
{
    private readonly Func<LfDbContext> _contextFactory;
    private readonly string _key;
    private readonly object _lock = new();

    public EfJsonLogStore(Func<LfDbContext> contextFactory, string key)
    {
        _contextFactory = contextFactory;
        _key = key;
    }

    public string Location => $"db:{_key}";

    /// <summary>全部行，依附加順序</summary>
    public IReadOnlyList<string> ReadLines()
    {
        using var ctx = _contextFactory();
        return ctx.LogLines.AsNoTracking()
            .Where(l => l.LogKey == _key)
            .OrderBy(l => l.Seq)
            .Select(l => l.Line)
            .ToList();
    }

    /// <summary>附加一行（O(1)）</summary>
    public void AppendLine(string line)
    {
        lock (_lock)
        {
            using var ctx = _contextFactory();
            ctx.LogLines.Add(new LogLineRow { LogKey = _key, Line = line, CreatedAt = DateTime.Now });
            ctx.SaveChanges();
        }
    }

    /// <summary>
    /// 刪除附加時間早於 cutoff 的行，回傳刪除筆數（docs/archive/HISTORY.md P0-3）。
    /// 附加時間是**寫入時的插入時間戳記**，不是行內容解析出的業務日期——這批資料多是
    /// append-only 執行歷程，插入時間就是事件發生時間，不必逐行解析 JSON。
    /// schema 升級前寫入的既存列沒有時間戳記，一律保留（無法判斷年代，寧可留著）。
    /// </summary>
    public int Prune(DateTime cutoff)
    {
        lock (_lock)
        {
            using var ctx = _contextFactory();
            // CreatedAt == null 的既存列（schema 升級前寫入）不受影響地保留——無法判斷年代，寧可留著
            var stale = ctx.LogLines
                .Where(l => l.LogKey == _key && l.CreatedAt != null && l.CreatedAt < cutoff)
                .ToList();
            if (stale.Count == 0) return 0;

            ctx.LogLines.RemoveRange(stale);
            ctx.SaveChanges();
            return stale.Count;
        }
    }

    /// <summary>
    /// 依附加時間範圍讀取（不分頁，依附加順序），供呼叫端先在 SQL 端窄化候選集再於記憶體精確過濾
    /// （docs/archive/HISTORY.md P1-2）。from/to 為 null 代表不限；沒有時間戳記的既存列
    /// （schema 升級前寫入）一律視為在範圍內——**這裡的窄化不保證絕對精確**，呼叫端仍須對
    /// 精確欄位做最終判斷（見 <c>AuditLogStore.Count</c> 的用法）。
    /// </summary>
    public IReadOnlyList<string> ReadLines(DateTime? from, DateTime? to)
    {
        using var ctx = _contextFactory();
        return ApplyDateRange(ctx.LogLines.AsNoTracking().Where(l => l.LogKey == _key), from, to)
            .OrderBy(l => l.Seq)
            .Select(l => l.Line)
            .ToList();
    }

    /// <summary>
    /// 全表分頁讀取（依附加順序，預設新到舊），SQL 端 OFFSET/FETCH，不必先讀全表。
    /// 僅適合「完全不篩選、只是要分頁瀏覽全部」的情境——若還有其他篩選條件，
    /// 呼叫端應改用 <see cref="ReadLines(DateTime?,DateTime?)"/> 撈候選集後在記憶體精確過濾與分頁。
    /// ascending：稽核頁「時間」表頭排序用（docs/WEB-SPEC.md §9.11），預設 false 維持既有新到舊。
    /// </summary>
    public (IReadOnlyList<string> Items, int Total) ReadPage(int skip, int take, bool ascending = false)
    {
        using var ctx = _contextFactory();
        var q = ctx.LogLines.AsNoTracking().Where(l => l.LogKey == _key);

        var total = q.Count();
        var ordered = ascending ? q.OrderBy(l => l.Seq) : q.OrderByDescending(l => l.Seq);
        var items = ordered.Skip(skip).Take(take).Select(l => l.Line).ToList();
        return (items, total);
    }

    /// <summary>
    /// CreatedAt 為 null 的既存列一律視為在範圍內——與 <see cref="Prune"/>「無法判斷年代就不清」
    /// 是同一個保守方向的鏡像：這裡是「無法判斷年代就不排除」，精確判斷交給呼叫端。
    /// </summary>
    private static IQueryable<LogLineRow> ApplyDateRange(IQueryable<LogLineRow> q, DateTime? from, DateTime? to)
    {
        if (from.HasValue) { var f = from.Value; q = q.Where(l => l.CreatedAt == null || l.CreatedAt >= f); }
        if (to.HasValue) { var t = to.Value; q = q.Where(l => l.CreatedAt == null || l.CreatedAt <= t); }
        return q;
    }
}
