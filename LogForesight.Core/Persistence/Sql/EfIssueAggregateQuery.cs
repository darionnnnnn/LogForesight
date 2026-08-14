using System.Diagnostics;
using LogForesight.Core.Models;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// <see cref="IIssueAggregateQuery"/> 的 SQL 實作（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §4.2）。
///
/// 整個聚合是**一句 GROUP BY**，取代改版前「撈回 18 萬筆紀錄的完整 JSON 再於記憶體 GroupBy」。
/// 唯一在應用層做的是相異完整簽章的收集——一個 (Source, EventId) 底下可能有多個
/// LogName／EntryType 組合，那是 join 處理狀態的鍵，數量極少（通常 1~2 個）。
///
/// **主機合併的處理**：<c>lf_top_issues.host_id</c> 是紀錄當下的識別（歷史事實，
/// 與 blob 路徑的 <c>DailyAnalysisRecord.HostId</c> 同一個值，見 <see cref="HostMatcher"/>
/// 的 PK-first 比對規則），合併主機時**不回寫**——回寫會讓 <c>UnmergeHost</c> 失去反向依據。
/// 因此這裡跟 blob 路徑（<see cref="HostLookup"/>／<see cref="HostAliasIndex"/>）一樣，
/// 查詢當下用主機清單的合併鏈把 host_id 解析成存活主機再計數／篩選：
///   - 篩選：<paramref name="hostIds"/>（呼叫端傳入的一律是存活主機 id）先展開回涵蓋的
///     墓碑列 id，否則合併後舊識別下的歷史會被 WHERE 整段濾掉、從畫面上消失；
///   - 計數：COUNT(DISTINCT host_id) 前先把每個 host_id 解析成存活 id 再去重，
///     否則同一台實體機器合併前後的兩個 id 會被算成兩台。
/// </summary>
public sealed class EfIssueAggregateQuery : IIssueAggregateQuery
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;
    private readonly IHostStore _hosts;
    private readonly SqlPerformanceMonitor? _performance;

    public EfIssueAggregateQuery(Func<LfDbContext> contextFactory, IHostStore hosts, SqlPerformanceMonitor? performance = null)
    {
        _contextFactory = contextFactory;
        _hosts = hosts;
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
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());

        using var ctx = _contextFactory();

        var q = ctx.TopIssues.AsNoTracking()
            // 未回填的舊列（record_date 仍是 MinValue）不參與聚合——
            // 讓它們以 0001-01-01 混進來會把 FirstSeen 拉到西元 1 年。
            // 回填未完成期間數字會偏低，由呼叫端誠實標示「統計中」（P4 的既定取捨）
            .Where(x => x.RecordDate >= f && x.RecordDate <= t);

        HashSet<long>? expandedHostIds = null;
        if (hostIds != null)
        {
            expandedHostIds = ExpandToAliasIds(aliasIndex, hostIds);
            q = q.Where(x => expandedHostIds.Contains(x.HostId));   // EF 8：單一 JSON 參數（OPENJSON／json_each）
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
                ActiveDays = g.Select(x => x.RecordDate).Distinct().Count(),
                FirstSeen = g.Min(x => x.RecordDate),
                LastSeen = g.Max(x => x.RecordDate),
                TotalCount = g.Sum(x => (long)x.EventCount),
                Rows = g.Count()
            })
            .ToList();

        // 主機數／主機日數／相異簽章各補一趟輕量查詢：主機數與主機日數需要先把 host_id
        // 解析成存活主機再去重（無法在 SQL 端表達合併鏈的 CASE 映射），相異簽章是字串集合，
        // 三者都無法併進同一句 GROUP BY，而且回傳量都遠小於原始列數
        var hostCounts = SurvivingHostCounts(ctx, f, t, expandedHostIds, aliasIndex);
        var hostDays = SurvivingHostDayCounts(ctx, f, t, expandedHostIds, aliasIndex);
        var signatures = DistinctSignatures(ctx, f, t, expandedHostIds);

        var result = grouped.Select(g =>
        {
            var key = (g.SourceName, g.EventId);
            return new IssueAggregate
            {
                Source = g.SourceName,
                EventId = g.EventId,
                Category = g.Category ?? string.Empty,
                // 舊資料相容（LegacySeverityRank）：SQL 端存的是三級化前寫入的原始值（Critical=3），
                // blob 路徑在讀取時正規化成 High＋elevates，這裡是同一條規則的 SQL 端版本——
                // 沒有這一步，依問題視角與重點問題卡會顯示「Critical」而報表／詳情頁顯示「高，重大」，
                // 正是這輪要解的「畫面數字/用詞對不起來」那類缺陷
                MaxSeverityRank = LegacySeverityRank.Normalize(g.MaxSeverityRank),
                ElevatesDayRisk = g.Elevates == 1 || LegacySeverityRank.ForcesElevate(g.MaxSeverityRank),
                HostCount = hostCounts.TryGetValue(key, out var hc) ? hc : 0,
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

    /// <summary>可見範圍（存活主機 id）展開回涵蓋的墓碑列 id；索引裡找不到的 id
    /// （理論上不會發生——可見範圍本來就是從主機清單算出來的）至少保留自己，不整台消失。</summary>
    private static HashSet<long> ExpandToAliasIds(HostAliasIndex index, IReadOnlyCollection<long> hostIds)
    {
        var result = new HashSet<long>();
        foreach (var id in hostIds)
        {
            var aliases = index.Aliases(id);
            if (aliases.Count == 0) { result.Add(id); continue; }
            foreach (var alias in aliases) result.Add(alias.HostId);
        }
        return result;
    }

    /// <summary>host_id 解析成存活主機 id；索引裡找不到（合併前的舊資料、或測試未登錄主機清單）
    /// 就用原始 id 當自己的存活 id——不強求資料乾淨（同 <see cref="HostIdentityResolver"/> 慣例）。</summary>
    private static long Surviving(HostAliasIndex index, long hostId) => index.Surviving(hostId)?.HostId ?? hostId;

    public HashSet<long> HostIdsFor(IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to)
    {
        if (issues.Count == 0) return new HashSet<long>();

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        // 大小寫不分（同 EfAnalysisRecordStore.ApplyPushableFilters 的 Source 篩選理由：
        // provider collation 不保證一致，兩邊都正規化才與比對邏輯逐位一致）
        var wanted = issues
            .Select(i => (Source: i.Source.ToUpperInvariant(), i.EventId))
            .ToHashSet();
        var eventIds = wanted.Select(w => w.EventId).ToHashSet();

        using var ctx = _contextFactory();

        // SQL 端先用 EventId 粗篩（高選擇度、可下推，Source 不分大小寫的精確比對留在記憶體），
        // 拉回的是相異三元組，數量遠小於原始列數
        var rows = ctx.TopIssues.AsNoTracking()
            .Where(x => x.RecordDate >= f && x.RecordDate <= t && x.HostId != 0 && eventIds.Contains(x.EventId))
            .Select(x => new { x.SourceName, x.EventId, x.HostId })
            .Distinct()
            .ToList();

        var result = rows
            .Where(r => wanted.Contains((r.SourceName.ToUpperInvariant(), r.EventId)))
            .Select(r => r.HostId)
            .ToHashSet();

        Log.Debug("[SQL] IssueAggregate.HostIdsFor（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}，{IssueCount} 個問題）→ {HostCount} 台主機、{Ms}ms",
            f, t, issues.Count, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:HostIdsFor", sw.ElapsedMilliseconds);

        return result;
    }

    public List<HostIssueOccurrence> LatestOccurrences(
        IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds)
    {
        if (issues.Count == 0) return new List<HostIssueOccurrence>();
        if (hostIds != null && hostIds.Count == 0) return new List<HostIssueOccurrence>();

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());

        var wanted = issues.Select(i => (Source: i.Source.ToUpperInvariant(), i.EventId)).ToHashSet();
        var eventIds = wanted.Select(w => w.EventId).ToHashSet();

        using var ctx = _contextFactory();

        var q = ctx.TopIssues.AsNoTracking()
            .Where(x => x.RecordDate >= f && x.RecordDate <= t && eventIds.Contains(x.EventId));

        if (hostIds != null)
        {
            var expanded = ExpandToAliasIds(aliasIndex, hostIds);
            q = q.Where(x => expanded.Contains(x.HostId));
        }

        // 目標範圍就是呼叫端已經算出的少數幾種問題，不是整份表——與 HostIdsFor 同一個規模假設
        var rows = q
            .Select(x => new { x.HostId, x.LogName, x.SourceName, x.EventId, x.EntryType, x.RecordDate, x.SeverityRank })
            .Distinct()
            .ToList()
            .Where(x => wanted.Contains((x.SourceName.ToUpperInvariant(), x.EventId)))
            .ToList();

        var result = rows
            // host_id 先解析成存活主機再分組：合併前後兩個 id 在同一天或不同天各自出現過，
            // 併起來取真正最近的一次，不是各自留一筆
            .GroupBy(x => (SurvivingHostId: Surviving(aliasIndex, x.HostId), x.LogName, x.SourceName, x.EventId, x.EntryType))
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.RecordDate).ThenByDescending(x => x.SeverityRank).First();
                return new HostIssueOccurrence
                {
                    HostId = g.Key.SurvivingHostId,
                    IssueKey = IssueSignatureKey.For(
                        g.Key.LogName, g.Key.SourceName, g.Key.EventId, (System.Diagnostics.EventLogEntryType)g.Key.EntryType),
                    LastSeen = latest.RecordDate,
                    SeverityRank = latest.SeverityRank
                };
            })
            .ToList();

        Log.Debug("[SQL] IssueAggregate.LatestOccurrences（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}，{IssueCount} 個問題）→ {Count} 筆、{Ms}ms",
            f, t, issues.Count, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:LatestOccurrences", sw.ElapsedMilliseconds);

        return result;
    }

    /// <summary>存活主機數＝相異問題底下，host_id 解析成存活主機 id 後的去重數
    /// （合併前後的兩個 id 代表同一台實體機器，只能算一台）</summary>
    private static Dictionary<(string, int), int> SurvivingHostCounts(
        LfDbContext ctx, DateTime from, DateTime to, IReadOnlyCollection<long>? expandedHostIds, HostAliasIndex aliasIndex)
    {
        var q = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= from && x.RecordDate <= to);
        if (expandedHostIds != null) q = q.Where(x => expandedHostIds.Contains(x.HostId));

        return q
            .Select(x => new { x.SourceName, x.EventId, x.HostId })
            .Distinct()
            .ToList()   // host_id → 存活 id 的解析非 SQL 可翻譯，先拉回輕量投影再算
            .GroupBy(x => (x.SourceName, x.EventId))
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => Surviving(aliasIndex, x.HostId)).Distinct().Count());
    }

    /// <summary>主機日數＝相異 (存活主機, record_date) 組合數（同一台主機多天各算一次）</summary>
    private static Dictionary<(string, int), int> SurvivingHostDayCounts(
        LfDbContext ctx, DateTime from, DateTime to, IReadOnlyCollection<long>? expandedHostIds, HostAliasIndex aliasIndex)
    {
        var q = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= from && x.RecordDate <= to);
        if (expandedHostIds != null) q = q.Where(x => expandedHostIds.Contains(x.HostId));

        return q
            .Select(x => new { x.SourceName, x.EventId, x.HostId, x.RecordDate })
            .Distinct()
            .ToList()
            .Select(x => new { x.SourceName, x.EventId, HostId = Surviving(aliasIndex, x.HostId), x.RecordDate })
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
