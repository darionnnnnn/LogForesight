using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// <see cref="IIssueAggregateQuery"/> 的 SQL 實作（docs/SCALE-ISSUE-FIRST-PLAN.md §4.2）。
///
/// 整個聚合是**一句 GROUP BY**，取代改版前「撈回 18 萬筆紀錄的完整 JSON 再於記憶體 GroupBy」。
/// 唯一在應用層做的是相異完整簽章的收集——一個 (Source, EventId) 底下可能有多個
/// LogName／EntryType 組合，那是 join 處理狀態的鍵，數量極少（通常 1~2 個）。
/// </summary>
public sealed class EfIssueAggregateQuery : IIssueAggregateQuery
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;
    private readonly SqlPerformanceMonitor? _performance;

    public EfIssueAggregateQuery(Func<LfDbContext> contextFactory, SqlPerformanceMonitor? performance = null)
    {
        _contextFactory = contextFactory;
        _performance = performance;
    }

    public List<IssueAggregate> Aggregate(DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds)
    {
        // 空集合＝可見範圍為空 → 零結果（與 RecordQueryFilter.Hosts 同一套授權語意，
        // 不是「不限制」——這個慣例反過來就是授權缺口）
        if (hostIds != null && hostIds.Count == 0) return new List<IssueAggregate>();

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;

        using var ctx = _contextFactory();

        var q = ctx.TopIssues.AsNoTracking()
            // 未回填的舊列（record_date 仍是 MinValue）不參與聚合——
            // 讓它們以 0001-01-01 混進來會把 FirstSeen 拉到西元 1 年。
            // 回填未完成期間數字會偏低，由呼叫端誠實標示「統計中」（P4 的既定取捨）
            .Where(x => x.RecordDate >= f && x.RecordDate <= t);

        if (hostIds != null)
        {
            var ids = hostIds.ToList();   // EF 8：單一 JSON 參數（OPENJSON／json_each）
            q = q.Where(x => ids.Contains(x.HostId));
        }

        var grouped = q
            .GroupBy(x => new { x.SourceName, x.EventId })
            .Select(g => new
            {
                g.Key.SourceName,
                g.Key.EventId,
                // 類別由規則以簽章為鍵決定，同一組內恆定——取 MIN 只是為了在 SQL 端
                // 有個確定性的選法，不必為此多一趟「哪個類別出現最多」的 GROUP BY
                Category = g.Min(x => x.Category),
                MaxSeverityRank = g.Max(x => x.SeverityRank),
                Elevates = g.Max(x => x.ElevatesDayRisk ? 1 : 0),
                HostCount = g.Select(x => x.HostId).Distinct().Count(),
                ActiveDays = g.Select(x => x.RecordDate).Distinct().Count(),
                FirstSeen = g.Min(x => x.RecordDate),
                LastSeen = g.Max(x => x.RecordDate),
                TotalCount = g.Sum(x => (long)x.EventCount),
                Rows = g.Count()
            })
            .ToList();

        // 主機日數與相異簽章各補一趟輕量查詢：兩者都無法在同一個 GROUP BY 裡表達
        //（前者是 COUNT(DISTINCT host_id, record_date) 的組合、後者是字串集合），
        // 而且回傳量都遠小於原始列數
        var hostDays = HostDayCounts(ctx, f, t, hostIds);
        var signatures = DistinctSignatures(ctx, f, t, hostIds);

        var result = grouped.Select(g =>
        {
            var key = (g.SourceName, g.EventId);
            return new IssueAggregate
            {
                Source = g.SourceName,
                EventId = g.EventId,
                Category = g.Category ?? string.Empty,
                MaxSeverityRank = g.MaxSeverityRank,
                ElevatesDayRisk = g.Elevates == 1,
                HostCount = g.HostCount,
                DayCount = hostDays.TryGetValue(key, out var d) ? d : g.Rows,
                ActiveDays = g.ActiveDays,
                FirstSeen = g.FirstSeen,
                LastSeen = g.LastSeen,
                TotalCount = g.TotalCount,
                IssueKeys = signatures.TryGetValue(key, out var keys) ? keys : Array.Empty<string>()
            };
        }).ToList();

        Log.Debug("[SQL] IssueAggregate（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}，主機 {Hosts}）→ {Count} 種問題、{Ms}ms",
            f, t, hostIds?.Count, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:Aggregate", sw.ElapsedMilliseconds);

        return result;
    }

    /// <summary>主機日數＝相異 (host_id, record_date) 組合數（同一台主機多天各算一次）</summary>
    private static Dictionary<(string, int), int> HostDayCounts(
        LfDbContext ctx, DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds)
    {
        var q = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= from && x.RecordDate <= to);
        if (hostIds != null)
        {
            var ids = hostIds.ToList();
            q = q.Where(x => ids.Contains(x.HostId));
        }

        return q
            .Select(x => new { x.SourceName, x.EventId, x.HostId, x.RecordDate })
            .Distinct()
            .GroupBy(x => new { x.SourceName, x.EventId })
            .Select(g => new { g.Key.SourceName, g.Key.EventId, Count = g.Count() })
            .ToList()
            .ToDictionary(x => (x.SourceName, x.EventId), x => x.Count);
    }

    /// <summary>
    /// 每個 (Source, EventId) 底下出現過的相異完整簽章。
    /// 這是 join 處理狀態的鍵（處理狀態以 <c>IssueSignatureKey</c> 為鍵，含 LogName 與 EntryType），
    /// 沒有它就答不出「這個問題是不是已經有結論」——§10.6 的前提。
    /// </summary>
    private static Dictionary<(string, int), IReadOnlyList<string>> DistinctSignatures(
        LfDbContext ctx, DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds)
    {
        var q = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= from && x.RecordDate <= to);
        if (hostIds != null)
        {
            var ids = hostIds.ToList();
            q = q.Where(x => ids.Contains(x.HostId));
        }

        return q
            .Select(x => new { x.SourceName, x.EventId, x.LogName, x.EntryType })
            .Distinct()
            .ToList()
            .GroupBy(x => (x.SourceName, x.EventId))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g
                    .Select(x => IssueSignatureKey.For(x.LogName, x.SourceName, x.EventId,
                        (System.Diagnostics.EventLogEntryType)x.EntryType))
                    .Distinct(StringComparer.Ordinal)
                    .ToList());
    }
}
