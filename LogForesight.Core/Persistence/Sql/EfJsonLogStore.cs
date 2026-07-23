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
            ctx.LogLines.Add(new LogLineRow { LogKey = _key, Line = line });
            ctx.SaveChanges();
        }
    }
}
