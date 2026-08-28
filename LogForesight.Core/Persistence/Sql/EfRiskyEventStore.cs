using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary><see cref="IRiskyEventStore"/> 的 SQL 後端實作（docs/archive/WEB-SCHEDULER-PLAN.md §2）。</summary>
public class EfRiskyEventStore : IRiskyEventStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;

    public EfRiskyEventStore(Func<LfDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public void ReplaceDay(long hostId, DateTime date, List<RiskyEvent> events)
    {
        var day = date.Date;

        // 舊列以 ExecuteDelete 直接在 SQL 端刪除（回饋三十五輪批次E）：原本是把整天的列
        // 載進 ChangeTracker 再 RemoveRange，吵雜主機一天數千筆全部進記憶體只為了刪掉。
        // 刪除與寫入仍包在同一個交易裡——中途失敗會讓那一天變成空的，語意同原本的
        // 「RemoveRange + Add 一次 SaveChanges」（交易寫法比照 EfAnalysisRecordStore.Append）。
        using var probe = _contextFactory();
        var strategy = probe.Database.CreateExecutionStrategy();

        var removed = 0;
        strategy.Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction();

            removed = ctx.RiskyEvents.Where(r => r.HostId == hostId && r.Date == day).ExecuteDelete();

            foreach (var e in events)
            {
                ctx.RiskyEvents.Add(new RiskyEventRow
                {
                    HostId = hostId,
                    Date = day,
                    LogName = e.LogName,
                    Source = e.Source,
                    EventId = e.EventId,
                    EntryType = e.EntryType,
                    EventTime = e.EventTime,
                    Message = e.Message,
                    RuleId = e.RuleId,
                    CreatedAt = e.CreatedAt
                });
            }

            ctx.SaveChanges();
            tx.Commit();
        });

        Log.Info("[SQL] RiskyEvent.ReplaceDay 主機（id={HostId}）{Date:yyyy-MM-dd}：清除 {Removed} 筆、寫入 {Added} 筆",
            hostId, day, removed, events.Count);
    }

    public List<RiskyEvent> Query(long hostId, DateTime date, string source, int eventId, int maxResults)
    {
        var day = date.Date;
        using var ctx = _contextFactory();

        // Source 用 UPPER() 正規化比對——provider collation 不保證一致（SQLite 預設區分大小寫、
        // SqlServer 常見不分），與 SentinelEventFetchService 的 OrdinalIgnoreCase 比對同一慣例
        // （EfAnalysisRecordStore.ApplyPushableFilters 的 Source 過濾同款理由）
        var upperSource = source.ToUpperInvariant();
        var rows = ctx.RiskyEvents.AsNoTracking()
            .Where(r => r.HostId == hostId && r.Date == day && r.EventId == eventId && r.Source.ToUpper() == upperSource)
            .OrderByDescending(r => r.EventTime)
            .Take(maxResults)
            .ToList();

        return rows.Select(Map).ToList();
    }

    public List<RiskyEvent> QueryDay(long hostId, DateTime date)
    {
        var day = date.Date;
        using var ctx = _contextFactory();

        var rows = ctx.RiskyEvents.AsNoTracking()
            .Where(r => r.HostId == hostId && r.Date == day)
            .OrderByDescending(r => r.EventTime)
            .ToList();

        return rows.Select(Map).ToList();
    }

    public int Prune(int retentionDays)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        using var ctx = _contextFactory();

        var stale = ctx.RiskyEvents.Where(r => r.Date < cutoff).ToList();
        if (stale.Count == 0)
        {
            Log.Info("[SQL] RiskyEvent.Prune（保留 {Days} 天）：無可清除紀錄", retentionDays);
            return 0;
        }

        ctx.RiskyEvents.RemoveRange(stale);
        ctx.SaveChanges();

        Log.Info("[SQL] RiskyEvent.Prune（保留 {Days} 天，cutoff {Cutoff:yyyy-MM-dd}）：清除 {Count} 筆", retentionDays, cutoff, stale.Count);
        return stale.Count;
    }

    private static RiskyEvent Map(RiskyEventRow row) => new()
    {
        Id = row.Id,
        HostId = row.HostId,
        Date = row.Date,
        LogName = row.LogName,
        Source = row.Source,
        EventId = row.EventId,
        EntryType = row.EntryType,
        EventTime = row.EventTime,
        Message = row.Message,
        RuleId = row.RuleId,
        CreatedAt = row.CreatedAt
    };
}
