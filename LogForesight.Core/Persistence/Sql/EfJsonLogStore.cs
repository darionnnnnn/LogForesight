using Microsoft.EntityFrameworkCore;

namespace LogForesight.Sql;

/// <summary>
/// <see cref="IJsonLogStore"/> 的資料庫後端：逐行存 lf_log_lines（log_key＋自增 seq＋line）。
/// AppendLine＝INSERT 一列（O(1)）；ReadLines＝依 seq 排序讀回。與檔案版的 append-only 語意一致。
/// </summary>
public sealed class EfJsonLogStore : IJsonLogStore
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

    public IReadOnlyList<string> ReadLines()
    {
        using var ctx = _contextFactory();
        return ctx.LogLines.AsNoTracking()
            .Where(l => l.LogKey == _key)
            .OrderBy(l => l.Seq)
            .Select(l => l.Line)
            .ToList();
    }

    public void AppendLine(string line)
    {
        lock (_lock)
        {
            using var ctx = _contextFactory();
            ctx.LogLines.Add(new LogLineRow { LogKey = _key, Line = line, CreatedAt = DateTime.Now });
            ctx.SaveChanges();
        }
    }

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

    public IReadOnlyList<string> ReadLines(DateTime? from, DateTime? to)
    {
        using var ctx = _contextFactory();
        return ApplyDateRange(ctx.LogLines.AsNoTracking().Where(l => l.LogKey == _key), from, to)
            .OrderBy(l => l.Seq)
            .Select(l => l.Line)
            .ToList();
    }

    public (IReadOnlyList<string> Items, int Total) ReadPage(int skip, int take)
    {
        using var ctx = _contextFactory();
        var q = ctx.LogLines.AsNoTracking().Where(l => l.LogKey == _key);

        var total = q.Count();
        var items = q.OrderByDescending(l => l.Seq).Skip(skip).Take(take).Select(l => l.Line).ToList();
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
