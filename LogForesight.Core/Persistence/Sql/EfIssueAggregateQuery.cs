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

    public List<IssueAggregate> Aggregate(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null)
    {
        // 空集合＝可見範圍為空 → 零結果（與 RecordQueryFilter.Hosts 同一套授權語意，
        // 不是「不限制」——這個慣例反過來就是授權缺口）
        if (hostIds != null && hostIds.Count == 0) return new List<IssueAggregate>();

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());
        var visibleRanks = visibleSeverities == null ? null : LegacySeverityRank.ExpandVisibleRanks(visibleSeverities);

        using var ctx = _contextFactory();

        var q = ctx.TopIssues.AsNoTracking()
            // 未回填的舊列（record_date 仍是 MinValue）不參與聚合——
            // 讓它們以 0001-01-01 混進來會把 FirstSeen 拉到西元 1 年。
            // 回填未完成期間數字會偏低，由呼叫端誠實標示「統計中」（P4 的既定取捨）
            .Where(x => x.RecordDate >= f && x.RecordDate <= t);

        // SiteHidden 模式（RecordRepository.ApplySeverityVisibility 的 SQL 端等價物）：
        // 這裡繞過 RecordRepository 的單一咽喉，隱藏的嚴重度要在這裡自己擋掉，
        // 否則依問題視角會讓「應該被隱藏」的問題重新冒出來
        if (visibleRanks != null) q = q.Where(x => visibleRanks.Contains(x.SeverityRank));

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
        var hostCounts = SurvivingHostCounts(ctx, f, t, expandedHostIds, aliasIndex, visibleRanks);
        var hostDays = SurvivingHostDayCounts(ctx, f, t, expandedHostIds, aliasIndex, visibleRanks);
        var signatures = DistinctSignatures(ctx, f, t, expandedHostIds, visibleRanks);

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

    public Dictionary<(string SourceKey, int EventId), HashSet<long>> HostIdsByIssue(
        IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds)
    {
        var empty = new Dictionary<(string, int), HashSet<long>>();
        if (issues.Count == 0) return empty;
        if (hostIds != null && hostIds.Count == 0) return empty;

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());

        var wanted = issues.Select(i => (SourceKey: i.Source.ToUpperInvariant(), i.EventId)).ToHashSet();
        var eventIds = wanted.Select(w => w.EventId).ToHashSet();

        using var ctx = _contextFactory();

        var q = ctx.TopIssues.AsNoTracking()
            .Where(x => x.RecordDate >= f && x.RecordDate <= t && x.HostId != 0 && eventIds.Contains(x.EventId));

        if (hostIds != null)
        {
            var expanded = ExpandToAliasIds(aliasIndex, hostIds);
            q = q.Where(x => expanded.Contains(x.HostId));
        }

        var rows = q
            .Select(x => new { x.SourceName, x.EventId, x.HostId })
            .Distinct()
            .ToList()
            .Where(x => wanted.Contains((x.SourceName.ToUpperInvariant(), x.EventId)))
            .ToList();

        var result = rows
            .GroupBy(x => (SourceKey: x.SourceName.ToUpperInvariant(), x.EventId))
            .ToDictionary(g => g.Key, g => g.Select(x => Surviving(aliasIndex, x.HostId)).ToHashSet());

        Log.Debug("[SQL] IssueAggregate.HostIdsByIssue（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}，{IssueCount} 個問題）→ {Count} 種問題、{Ms}ms",
            f, t, issues.Count, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:HostIdsByIssue", sw.ElapsedMilliseconds);

        return result;
    }

    public List<HostIssueOccurrence> LatestOccurrences(
        IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds, IReadOnlySet<IssueSeverity>? visibleSeverities = null)
    {
        if (issues.Count == 0) return new List<HostIssueOccurrence>();
        if (hostIds != null && hostIds.Count == 0) return new List<HostIssueOccurrence>();

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());
        var visibleRanks = visibleSeverities == null ? null : LegacySeverityRank.ExpandVisibleRanks(visibleSeverities);

        var wanted = issues.Select(i => (Source: i.Source.ToUpperInvariant(), i.EventId)).ToHashSet();
        var eventIds = wanted.Select(w => w.EventId).ToHashSet();

        using var ctx = _contextFactory();

        var q = ctx.TopIssues.AsNoTracking()
            .Where(x => x.RecordDate >= f && x.RecordDate <= t && eventIds.Contains(x.EventId));

        if (visibleRanks != null) q = q.Where(x => visibleRanks.Contains(x.SeverityRank));

        if (hostIds != null)
        {
            var expanded = ExpandToAliasIds(aliasIndex, hostIds);
            q = q.Where(x => expanded.Contains(x.HostId));
        }

        // 目標範圍就是呼叫端已經算出的少數幾種問題，不是整份表——與 HostIdsFor 同一個規模假設
        var rows = q
            .Select(x => new
            {
                x.HostId, x.LogName, x.SourceName, x.EventId, x.EntryType, x.RecordDate, x.SeverityRank, x.KnownIssue
            })
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
                    SeverityRank = latest.SeverityRank,
                    KnownIssue = latest.KnownIssue
                };
            })
            .ToList();

        Log.Debug("[SQL] IssueAggregate.LatestOccurrences（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}，{IssueCount} 個問題）→ {Count} 筆、{Ms}ms",
            f, t, issues.Count, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:LatestOccurrences", sw.ElapsedMilliseconds);

        return result;
    }

    public List<IssueDailyHostCount> DailyHostCounts(
        IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds)
    {
        if (issues.Count == 0) return new List<IssueDailyHostCount>();
        if (hostIds != null && hostIds.Count == 0) return new List<IssueDailyHostCount>();

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

        // 目標範圍是呼叫端已經算出的少數幾種問題（通常是當頁排行結果），不是整份表——
        // 與 LatestOccurrences／HostIdsFor 同一個規模假設
        var rows = q
            .Select(x => new { x.HostId, x.SourceName, x.EventId, x.RecordDate })
            .ToList()
            .Where(x => wanted.Contains((x.SourceName.ToUpperInvariant(), x.EventId)))
            .ToList();

        var result = rows
            // host_id 先解析成存活主機再去重：合併前後兩個 id 同一天各自出現過要算一台，不是兩台。
            // 分組鍵的 Source 同樣要正規化大小寫（回饋十九輪批次I 體檢修正，與本檔其餘方法的
            // wanted-normalization 慣例一致）——大小寫變體會被拆成同日兩列，下游
            // IssueBaselineCalculator 的「一筆＝一天」假設被破壞：出現日數虛胖、中位數偏低、
            // 偏離倍數因此偏高，且污染 PriorityScore 的 spreadW
            .GroupBy(x => (SourceKey: x.SourceName.ToUpperInvariant(), x.EventId, x.RecordDate))
            .Select(g => new IssueDailyHostCount
            {
                Source = g.Key.SourceKey,
                EventId = g.Key.EventId,
                Date = g.Key.RecordDate,
                HostCount = g.Select(x => Surviving(aliasIndex, x.HostId)).Distinct().Count()
            })
            .ToList();

        Log.Debug("[SQL] IssueAggregate.DailyHostCounts（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}，{IssueCount} 個問題）→ {Count} 筆、{Ms}ms",
            f, t, issues.Count, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:DailyHostCounts", sw.ElapsedMilliseconds);

        return result;
    }

    public Dictionary<(string SourceKey, int EventId), DateTime> FirstSeenFor(
        IReadOnlyCollection<(string Source, int EventId)> issues)
    {
        if (issues.Count == 0) return new Dictionary<(string, int), DateTime>();

        var wanted = issues.Select(i => (SourceKey: i.Source.ToUpperInvariant(), i.EventId)).ToHashSet();
        var eventIds = wanted.Select(w => w.EventId).ToHashSet();

        using var ctx = _contextFactory();

        var rows = ctx.IssueFirstSeen.AsNoTracking()
            .Where(x => eventIds.Contains(x.EventId))
            .Select(x => new { x.SourceKey, x.EventId, x.FirstSeen })
            .ToList()
            .Where(x => wanted.Contains((x.SourceKey, x.EventId)))
            .ToList();

        return rows.ToDictionary(x => (x.SourceKey, x.EventId), x => x.FirstSeen);
    }

    public List<HostIssueOccurrence> ActionableOccurrences(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null)
    {
        if (hostIds != null && hostIds.Count == 0) return new List<HostIssueOccurrence>();

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());
        var visibleRanks = visibleSeverities == null ? null : LegacySeverityRank.ExpandVisibleRanks(visibleSeverities);

        using var ctx = _contextFactory();

        // 母體與 HandlingHistoryQueryService.GetTodo 的既有定義一致：日層級 RiskLevel 為高或中
        // （不是問題自身嚴重度）——一句 join 取代「先撈整段期間紀錄、再在記憶體篩高中風險日」
        var q =
            from ti in ctx.TopIssues.AsNoTracking()
            join dr in ctx.DailyRecords.AsNoTracking() on ti.RecordId equals dr.RecordId
            where dr.RecordDate >= f && dr.RecordDate <= t &&
                  (dr.RiskLevel == RiskLevels.High || dr.RiskLevel == RiskLevels.Medium)
            select ti;

        if (visibleRanks != null) q = q.Where(x => visibleRanks.Contains(x.SeverityRank));

        if (hostIds != null)
        {
            var expanded = ExpandToAliasIds(aliasIndex, hostIds);
            q = q.Where(x => expanded.Contains(x.HostId));
        }

        // 每個 (主機,問題) 的最近出現日與期間內最高嚴重度：SQL 端 GROUP BY 做完，
        // 拉回的量受限於相異 (主機,問題) 組合數，不是可行動日的原始問題列數
        var grouped = q
            .GroupBy(x => new { x.HostId, x.LogName, x.SourceName, x.EventId, x.EntryType })
            .Select(g => new
            {
                g.Key.HostId,
                g.Key.LogName,
                g.Key.SourceName,
                g.Key.EventId,
                g.Key.EntryType,
                LastSeen = g.Max(x => x.RecordDate),
                MaxSeverityRank = g.Max(x => x.SeverityRank)
            })
            .ToList();

        var result = grouped
            // host_id 解析成存活主機再合併：同 LatestOccurrences 的既有規則
            .GroupBy(x => (SurvivingHostId: Surviving(aliasIndex, x.HostId), x.LogName, x.SourceName, x.EventId, x.EntryType))
            .Select(g => new HostIssueOccurrence
            {
                HostId = g.Key.SurvivingHostId,
                IssueKey = IssueSignatureKey.For(
                    g.Key.LogName, g.Key.SourceName, g.Key.EventId, (System.Diagnostics.EventLogEntryType)g.Key.EntryType),
                LastSeen = g.Max(x => x.LastSeen),
                SeverityRank = g.Max(x => x.MaxSeverityRank)
            })
            .ToList();

        Log.Debug("[SQL] IssueAggregate.ActionableOccurrences（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}）→ {Count} 筆、{Ms}ms",
            f, t, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:ActionableOccurrences", sw.ElapsedMilliseconds);

        return result;
    }

    private sealed record DayHandlingRaw(
        long HostId, DateTime RecordDate, string? DayLevelStatus, long? DayHandlerId,
        int Total, int Closed, bool AnyInProgress, bool HasCaseHandler,
        DateTime? DayDueDate, bool AnyOverdueIssue);

    private List<DayHandlingRaw> GetDayHandlingRaw(
        LfDbContext ctx, DateTime f, DateTime t,
        HashSet<long>? expandedHostIds, IReadOnlyCollection<long> excludedHostIds,
        List<int> unhandledRanks, DateTime todayForOverdue)
    {
        var recordsQuery = ctx.DailyRecords.AsNoTracking()
            .Where(r => r.RecordDate >= f && r.RecordDate <= t &&
                        (r.RiskLevel == RiskLevels.High || r.RiskLevel == RiskLevels.Medium));

        if (expandedHostIds != null)
        {
            recordsQuery = recordsQuery.Where(r => expandedHostIds.Contains(r.HostId));
        }

        if (excludedHostIds.Count > 0)
        {
            recordsQuery = recordsQuery.Where(r => !excludedHostIds.Contains(r.HostId));
        }

        var q = from dr in recordsQuery
                let hostNameKey = dr.HostName.ToUpper()

                join rh in ctx.RecordHandlings.AsNoTracking()
                    on new { HostNameKey = hostNameKey, dr.RecordDate } equals new { rh.HostNameKey, rh.RecordDate } into rhs
                from rh in rhs.DefaultIfEmpty()

                let total = ctx.TopIssues.AsNoTracking()
                    .Count(ti => ti.RecordId == dr.RecordId &&
                                 (unhandledRanks.Contains(ti.SeverityRank) ||
                                  ctx.IssueHandlings.AsNoTracking().Any(ih =>
                                      ih.HostNameKey == hostNameKey &&
                                      ih.RecordDate == dr.RecordDate &&
                                      ih.IssueKey == (ti.EventKey != null && ti.EventKey != ""
                                          ? ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType + "|" + ti.EventKey
                                          : ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType))))

                let closed = ctx.TopIssues.AsNoTracking()
                    .Count(ti => ti.RecordId == dr.RecordId &&
                                 (unhandledRanks.Contains(ti.SeverityRank) ||
                                  ctx.IssueHandlings.AsNoTracking().Any(ih =>
                                      ih.HostNameKey == hostNameKey &&
                                      ih.RecordDate == dr.RecordDate &&
                                      ih.IssueKey == (ti.EventKey != null && ti.EventKey != ""
                                          ? ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType + "|" + ti.EventKey
                                          : ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType))) &&
                                 ctx.IssueHandlings.AsNoTracking().Any(ih =>
                                      ih.HostNameKey == hostNameKey &&
                                      ih.RecordDate == dr.RecordDate &&
                                      ih.IssueKey == (ti.EventKey != null && ti.EventKey != ""
                                          ? ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType + "|" + ti.EventKey
                                          : ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType) &&
                                      (ih.Status == HandlingStatuses.Resolved || ih.Status == HandlingStatuses.WontFix || ih.Status == HandlingStatuses.FalsePositive || ih.Status == HandlingStatuses.KnownNoise)))

                let anyInProgress = ctx.TopIssues.AsNoTracking()
                    .Any(ti => ti.RecordId == dr.RecordId &&
                               ctx.IssueHandlings.AsNoTracking().Any(ih =>
                                   ih.HostNameKey == hostNameKey &&
                                   ih.RecordDate == dr.RecordDate &&
                                   ih.IssueKey == (ti.EventKey != null && ti.EventKey != ""
                                       ? ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType + "|" + ti.EventKey
                                       : ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType) &&
                                   (ih.Status == HandlingStatuses.InProgress || ih.Status == IssueHandlingStatuses.Observing || ih.Status == HandlingStatuses.Escalated)))

                let anyCaseHandler = ctx.TopIssues.AsNoTracking()
                    .Any(ti => ti.RecordId == dr.RecordId &&
                               ctx.IssueCases.AsNoTracking().Any(ic =>
                                   ic.HostNameKey == hostNameKey &&
                                   ic.ClosedAt == null &&
                                   ic.HandlerId != null &&
                                   ic.IssueKey == (ti.EventKey != null && ti.EventKey != ""
                                       ? ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType + "|" + ti.EventKey
                                       : ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType)))

                let anyOverdueIssue = ctx.TopIssues.AsNoTracking()
                    .Any(ti => ti.RecordId == dr.RecordId &&
                               ctx.IssueHandlings.AsNoTracking().Any(ih =>
                                   ih.HostNameKey == hostNameKey &&
                                   ih.RecordDate == dr.RecordDate &&
                                   ih.IssueKey == (ti.EventKey != null && ti.EventKey != ""
                                       ? ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType + "|" + ti.EventKey
                                       : ti.LogName + "|" + ti.SourceName + "|" + ti.EventId + "|" + ti.EntryType) &&
                                   ih.DueDate != null && ih.DueDate < todayForOverdue &&
                                   (ih.Status == HandlingStatuses.InProgress || ih.Status == IssueHandlingStatuses.Observing)))

                select new DayHandlingRaw(
                    dr.HostId,
                    dr.RecordDate,
                    rh.Status,
                    rh.HandlerId,
                    total,
                    closed,
                    anyInProgress,
                    anyCaseHandler,
                    rh.DueDate,
                    anyOverdueIssue
                );

        return q.ToList();
    }

    public List<DayHandlingProjection> DeriveDayHandling(
        DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity> unhandledSeverities,
        IReadOnlyCollection<long> excludedHostIds)
    {
        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());
        var unhandledRanks = unhandledSeverities.Select(s => (int)s).ToList();

        using var ctx = _contextFactory();

        HashSet<long>? expandedHostIds = null;
        if (hostIds != null)
        {
            if (hostIds.Count == 0) return new List<DayHandlingProjection>();
            expandedHostIds = ExpandToAliasIds(aliasIndex, hostIds);
        }

        var projected = GetDayHandlingRaw(ctx, f, t, expandedHostIds, excludedHostIds, unhandledRanks, DateTime.MinValue);

        var result = projected.Select(x =>
        {
            string status;
            if (x.Total > 0 && x.Closed == x.Total) status = HandlingStatuses.Resolved;
            else if (x.Closed > 0 || x.AnyInProgress) status = HandlingStatuses.InProgress;
            else status = string.IsNullOrEmpty(x.DayLevelStatus) ? HandlingStatuses.Open : x.DayLevelStatus;

            return new DayHandlingProjection(
                Surviving(aliasIndex, x.HostId),
                x.RecordDate,
                status,
                x.DayHandlerId != null || x.HasCaseHandler
            );
        }).ToList();

        Log.Debug("[SQL] IssueAggregate.DeriveDayHandling（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}）→ {Count} 筆、{Ms}ms",
            f, t, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:DeriveDayHandling", sw.ElapsedMilliseconds);

        return result;
    }

    public DayTodoAggregate AggregateDayTodo(
        DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity> unhandledSeverities,
        IReadOnlyCollection<long> excludedHostIds,
        DateTime today)
    {
        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());
        var unhandledRanks = unhandledSeverities.Select(s => (int)s).ToList();

        using var ctx = _contextFactory();

        HashSet<long>? expandedHostIds = null;
        if (hostIds != null)
        {
            if (hostIds.Count == 0) return new DayTodoAggregate(0, 0, 0, 0, 0);
            expandedHostIds = ExpandToAliasIds(aliasIndex, hostIds);
        }

        var projected = GetDayHandlingRaw(ctx, f, t, expandedHostIds, excludedHostIds, unhandledRanks, today.Date);

        int totalCount = 0;
        int openCount = 0;
        int inProgressCount = 0;
        int resolvedCount = 0;
        int overdueCount = 0;

        foreach (var x in projected)
        {
            totalCount++;

            string status;
            if (x.Total > 0 && x.Closed == x.Total) status = HandlingStatuses.Resolved;
            else if (x.Closed > 0 || x.AnyInProgress) status = HandlingStatuses.InProgress;
            else status = string.IsNullOrEmpty(x.DayLevelStatus) ? HandlingStatuses.Open : x.DayLevelStatus;

            var external = HandlingStatuses.ExternalOf(status);
            if (external == HandlingStatuses.Open) openCount++;
            else if (external == HandlingStatuses.InProgress) inProgressCount++;
            else resolvedCount++;

            bool isUnresolved = HandlingStatuses.Unresolved.Contains(status);
            bool dayOverdue = x.DayDueDate.HasValue && x.DayDueDate.Value.Date < today.Date && isUnresolved;

            if (dayOverdue || x.AnyOverdueIssue)
            {
                overdueCount++;
            }
        }

        Log.Debug("[SQL] IssueAggregate.AggregateDayTodo（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}）→ {Count} 筆、{Ms}ms",
            f, t, totalCount, sw.ElapsedMilliseconds);
        _performance?.Record("issues:AggregateDayTodo", sw.ElapsedMilliseconds);

        return new DayTodoAggregate(totalCount, openCount, inProgressCount, resolvedCount, overdueCount);
    }

    public ReportKpiAggregate AggregateReportKpi(DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds, IReadOnlySet<string>? riskLevels, IReadOnlySet<IssueSeverity>? visibleSeverities)
    {
        if (hostIds != null && hostIds.Count == 0) return new ReportKpiAggregate(0, 0, 0, 0, 0);

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());
        var visibleRanks = visibleSeverities == null ? null : LegacySeverityRank.ExpandVisibleRanks(visibleSeverities);

        using var ctx = _contextFactory();

        var qRecords = ctx.DailyRecords.AsNoTracking().Where(r => r.RecordDate >= f && r.RecordDate <= t);
        if (hostIds != null)
        {
            var expanded = ExpandToAliasIds(aliasIndex, hostIds);
            qRecords = qRecords.Where(r => expanded.Contains(r.HostId));
        }
        if (riskLevels != null) qRecords = qRecords.Where(r => riskLevels.Contains(r.RiskLevel));

        var stats = qRecords.Select(r => new { r.RiskLevel, r.DataIncomplete, r.SecurityLogAvailable }).ToList();
        var highRiskDays = stats.Count(r => r.RiskLevel == RiskLevels.High);
        var mediumRiskDays = stats.Count(r => r.RiskLevel == RiskLevels.Medium);
        var coverageGapDays = stats.Count(r => r.DataIncomplete || r.SecurityLogAvailable == false);

        var affectedHosts = qRecords.Where(r => r.RiskLevel == RiskLevels.High || r.RiskLevel == RiskLevels.Medium)
            .Select(r => r.HostId)
            .Distinct()
            .ToList()
            .Select(h => Surviving(aliasIndex, h))
            .Distinct()
            .Count();

        var qIssues = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= f && x.RecordDate <= t);
        if (hostIds != null)
        {
            var expanded = ExpandToAliasIds(aliasIndex, hostIds);
            qIssues = qIssues.Where(x => expanded.Contains(x.HostId));
        }
        if (riskLevels != null)
        {
            var allowedRecordIds = qRecords.Select(r => r.RecordId);
            qIssues = qIssues.Where(x => allowedRecordIds.Contains(x.RecordId));
        }
        if (visibleRanks != null) qIssues = qIssues.Where(x => visibleRanks.Contains(x.SeverityRank));

        var totalIssues = qIssues.Count();

        Log.Debug("[SQL] IssueAggregate.AggregateReportKpi（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}）→ {Ms}ms", f, t, sw.ElapsedMilliseconds);
        _performance?.Record("issues:AggregateReportKpi", sw.ElapsedMilliseconds);

        return new ReportKpiAggregate(totalIssues, highRiskDays, mediumRiskDays, affectedHosts, coverageGapDays);
    }

    public List<TrendAggregate> AggregateReportTrend(DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds, IReadOnlySet<string>? riskLevels, IReadOnlySet<IssueSeverity>? visibleSeverities)
    {
        if (hostIds != null && hostIds.Count == 0) return new List<TrendAggregate>();

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());

        using var ctx = _contextFactory();

        var qRecords = ctx.DailyRecords.AsNoTracking().Where(r => r.RecordDate >= f && r.RecordDate <= t);
        if (hostIds != null)
        {
            var expanded = ExpandToAliasIds(aliasIndex, hostIds);
            qRecords = qRecords.Where(r => expanded.Contains(r.HostId));
        }
        if (riskLevels != null) qRecords = qRecords.Where(r => riskLevels.Contains(r.RiskLevel));

        var trend = qRecords
            .GroupBy(r => r.RecordDate)
            .Select(g => new {
                Date = g.Key,
                HighRisk = g.Count(r => r.RiskLevel == RiskLevels.High),
                MediumRisk = g.Count(r => r.RiskLevel == RiskLevels.Medium),
                ErrorCount = g.Sum(r => r.ErrorCount)
            })
            .ToList()
            .Select(x => new TrendAggregate(x.Date, x.HighRisk, x.MediumRisk, x.ErrorCount))
            .ToList();

        Log.Debug("[SQL] IssueAggregate.AggregateReportTrend（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}）→ {Count} 天、{Ms}ms", f, t, trend.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:AggregateReportTrend", sw.ElapsedMilliseconds);

        return trend;
    }


    public List<CategoryAggregate> AggregateByCategory(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds, IReadOnlySet<IssueSeverity>? allowedSeverities)
    {
        if (hostIds != null && hostIds.Count == 0) return new List<CategoryAggregate>();

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());

        using var ctx = _contextFactory();

        var q = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= f && x.RecordDate <= t);

        if (hostIds != null)
        {
            var expanded = ExpandToAliasIds(aliasIndex, hostIds);
            q = q.Where(x => expanded.Contains(x.HostId));
        }

        // 風險資訊層級（去重前）：SQL 端 GROUP BY 拿到每個 (類別,主機,問題) 組合在期間內的
        // 最高嚴重度／是否曾重大／累計次數與事件數——組數受限於相異風險資訊數，不是原始列數
        var riskItems = q
            .GroupBy(x => new { x.Category, x.HostId, x.SourceName, x.EventId })
            .Select(g => new
            {
                g.Key.Category,
                g.Key.HostId,
                g.Key.SourceName,
                g.Key.EventId,
                MaxSeverityRank = g.Max(x => x.SeverityRank),
                AnyElevates = g.Max(x => x.ElevatesDayRisk ? 1 : 0),
                Occurrences = g.Count(),
                EventTotal = g.Sum(x => (long)x.EventCount)
            })
            .ToList();

        var result = riskItems
            // host_id 解析成存活主機再去重合併：合併前後的兩個 id 代表同一筆風險資訊，
            // 語意與 SurvivingHostCounts／SurvivingHostDayCounts 一致
            .GroupBy(x => new { x.Category, SurvivingHostId = Surviving(aliasIndex, x.HostId), x.SourceName, x.EventId })
            .Select(g => new
            {
                g.Key.Category,
                g.Key.SurvivingHostId,
                // 舊資料相容（LegacySeverityRank）：與 Aggregate 同一條規則，否則這張卡跟
                // 依問題視角／重點問題卡對同一筆資料的嚴重度用詞會對不上
                MaxSeverityRank = LegacySeverityRank.Normalize(g.Max(x => x.MaxSeverityRank)),
                Elevates = g.Any(x => x.AnyElevates == 1 || LegacySeverityRank.ForcesElevate(x.MaxSeverityRank)),
                Occurrences = g.Sum(x => x.Occurrences),
                EventTotal = g.Sum(x => x.EventTotal)
            })
            // 嚴重度可見性：依「這筆風險資訊」正規化後的最高嚴重度篩，不是逐筆出現各自篩——
            // 風險資訊是這張卡的顯示單位，可見性也該以它為單位判斷（規劃 D1）
            .Where(item => allowedSeverities == null || allowedSeverities.Contains((IssueSeverity)item.MaxSeverityRank))
            .GroupBy(item => item.Category)
            .Select(g => new CategoryAggregate
            {
                Category = g.Key,
                RiskItemCount = g.Count(),
                CumulativeCount = g.Sum(x => x.Occurrences),
                AffectedHosts = g.Select(x => x.SurvivingHostId).Distinct().Count(),
                TotalEvents = g.Sum(x => x.EventTotal),
                HighCount = g.Count(x => x.MaxSeverityRank == (int)IssueSeverity.High),
                MediumCount = g.Count(x => x.MaxSeverityRank == (int)IssueSeverity.Medium),
                LowCount = g.Count(x => x.MaxSeverityRank == (int)IssueSeverity.Low),
                ElevatesCount = g.Count(x => x.Elevates)
            })
            .ToList();

        Log.Debug("[SQL] IssueAggregate.AggregateByCategory（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}）→ {Count} 個類別、{Ms}ms",
            f, t, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:AggregateByCategory", sw.ElapsedMilliseconds);

        return result;
    }

    public List<DateRiskAggregate> AggregateByDate(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<string>? riskLevels = null, IReadOnlySet<IssueCategory>? categories = null,
        int? eventId = null, string? source = null, IssueSeverity? minSeverity = null,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null)
    {
        if (hostIds != null && hostIds.Count == 0) return new List<DateRiskAggregate>();

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());
        var visibleRanks = visibleSeverities == null ? null : LegacySeverityRank.ExpandVisibleRanks(visibleSeverities);

        using var ctx = _contextFactory();

        var recordsQuery = ctx.DailyRecords.AsNoTracking().Where(r => r.RecordDate >= f && r.RecordDate <= t);
        var issuesQuery = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= f && x.RecordDate <= t);

        HashSet<long>? expanded = null;
        if (hostIds != null)
        {
            expanded = ExpandToAliasIds(aliasIndex, hostIds);
            recordsQuery = recordsQuery.Where(r => expanded.Contains(r.HostId));
            issuesQuery = issuesQuery.Where(x => expanded.Contains(x.HostId));
        }
        if (riskLevels != null) recordsQuery = recordsQuery.Where(r => riskLevels.Contains(r.RiskLevel));
        recordsQuery = ApplyIssueExistsFilters(ctx, recordsQuery, categories, eventId, source, minSeverity);

        // SiteHidden 模式：只影響風險類型 chips（issuesQuery），不動 recordsQuery——
        // 同 RecordRepository.ApplySeverityVisibility 只砍 TopIssues、不排除整筆紀錄／不動
        // RiskLevel 的既有原則，高/中/低風險日分桶因此不受這個設定影響
        if (visibleRanks != null) issuesQuery = issuesQuery.Where(x => visibleRanks.Contains(x.SeverityRank));

        // 輕量列（不含 ContentJson／Headline），量級＝期間天數 × 可見主機數，不是整份 blob
        var rows = recordsQuery
            .Select(r => new { r.RecordId, r.HostId, r.RecordDate, r.RiskLevel, r.HasCorrelation })
            .ToList()
            // host_id 解析成存活主機再依 (存活主機,日期) 去重——合併前後的兩個 id 若剛好
            // 同一天都有紀錄（理論上不會發生），較高風險等級的那筆勝出，不重複計數同一台主機
            .GroupBy(x => (SurvivingHostId: Surviving(aliasIndex, x.HostId), x.RecordDate))
            .Select(g => g.OrderByDescending(x => RiskLevels.Rank(x.RiskLevel)).First())
            .ToList();

        // 風險類型清單的母體要跟著日風險篩選收斂——篩「只看高」時，中/低風險日被砍掉的問題
        // 不該還出現在風險類型 chips 裡
        if (riskLevels != null)
        {
            var allowedRecordIds = rows.Select(x => x.RecordId).ToHashSet();
            issuesQuery = issuesQuery.Where(x => allowedRecordIds.Contains(x.RecordId));
        }

        var categoriesByDate = CategoriesByDate(issuesQuery);

        var result = rows
            .GroupBy(x => x.RecordDate)
            .Select(g => new DateRiskAggregate
            {
                Date = g.Key,
                HighRiskHosts = g.Count(x => x.RiskLevel == RiskLevels.High),
                MediumRiskHosts = g.Count(x => x.RiskLevel == RiskLevels.Medium),
                LowRiskHosts = g.Count(x => x.RiskLevel == RiskLevels.Low),
                CorrelationHosts = g.Count(x => x.HasCorrelation),
                HostCount = g.Count(),
                Categories = categoriesByDate.TryGetValue(g.Key, out var cats) ? cats : Array.Empty<string>()
            })
            .ToList();

        Log.Debug("[SQL] IssueAggregate.AggregateByDate（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}）→ {Count} 天、{Ms}ms",
            f, t, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:AggregateByDate", sw.ElapsedMilliseconds);

        return result;
    }

    public List<HostRiskAggregate> AggregateByHost(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<string>? riskLevels = null, IReadOnlySet<IssueCategory>? categories = null,
        int? eventId = null, string? source = null, IssueSeverity? minSeverity = null,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null)
    {
        if (hostIds != null && hostIds.Count == 0) return new List<HostRiskAggregate>();

        var sw = Stopwatch.StartNew();
        var f = from.Date;
        var t = to.Date;
        var aliasIndex = new HostAliasIndex(_hosts.GetAll());
        var visibleRanks = visibleSeverities == null ? null : LegacySeverityRank.ExpandVisibleRanks(visibleSeverities);

        using var ctx = _contextFactory();

        var recordsQuery = ctx.DailyRecords.AsNoTracking().Where(r => r.RecordDate >= f && r.RecordDate <= t);
        var issuesQuery = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= f && x.RecordDate <= t);

        HashSet<long>? expanded = null;
        if (hostIds != null)
        {
            expanded = ExpandToAliasIds(aliasIndex, hostIds);
            recordsQuery = recordsQuery.Where(r => expanded.Contains(r.HostId));
            issuesQuery = issuesQuery.Where(x => expanded.Contains(x.HostId));
        }
        if (riskLevels != null) recordsQuery = recordsQuery.Where(r => riskLevels.Contains(r.RiskLevel));
        recordsQuery = ApplyIssueExistsFilters(ctx, recordsQuery, categories, eventId, source, minSeverity);
        if (visibleRanks != null) issuesQuery = issuesQuery.Where(x => visibleRanks.Contains(x.SeverityRank));

        // 1. 主聚合在 SQL 端：依 HostId 分組算天數與最新日期，拉回量受限於主機數而非紀錄數
        var hostRecordsAgg = recordsQuery
            .GroupBy(r => r.HostId)
            .Select(g => new
            {
                HostId = g.Key,
                HighRiskDays = g.Count(r => r.RiskLevel == RiskLevels.High),
                MediumRiskDays = g.Count(r => r.RiskLevel == RiskLevels.Medium),
                LowRiskDays = g.Count(r => r.RiskLevel == RiskLevels.Low),
                CorrelationDays = g.Count(r => r.HasCorrelation),
                MaxDate = g.Max(r => r.RecordDate)
            })
            .ToList();

        // 2. 每台主機最新一天的風險等級與標題：以相關子查詢取 MAX(RecordDate) 那列，同樣是主機數量級
        var latestRaw = recordsQuery
            .Where(r => r.RecordDate == recordsQuery.Where(x => x.HostId == r.HostId).Max(x => x.RecordDate))
            .Select(r => new { r.HostId, r.RiskLevel, r.Headline })
            .ToList()
            .GroupBy(r => r.HostId)
            .ToDictionary(g => g.Key, g => g.First());

        // 3. 類別：問題子列 join 篩選後的紀錄，SQL 端依 (HostId, Category) 分組，拉回主機 × 類別列
        if (riskLevels != null)
        {
            var allowedRecordIds = recordsQuery.Select(x => x.RecordId);
            issuesQuery = issuesQuery.Where(x => allowedRecordIds.Contains(x.RecordId));
        }

        var categoriesRaw = issuesQuery
            .GroupBy(x => new { x.HostId, x.Category })
            .Select(g => new
            {
                g.Key.HostId,
                g.Key.Category,
                MaxSeverityRank = g.Max(x => x.SeverityRank),
                IssueCount = g.Count()
            })
            .ToList();

        var categoriesByHost = categoriesRaw
            .GroupBy(x => new { SurvivingHostId = Surviving(aliasIndex, x.HostId), x.Category })
            .Select(g => new
            {
                g.Key.SurvivingHostId,
                g.Key.Category,
                MaxSeverityRank = LegacySeverityRank.Normalize(g.Max(x => x.MaxSeverityRank)),
                IssueCount = g.Sum(x => x.IssueCount)
            })
            .GroupBy(x => x.SurvivingHostId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g
                    .OrderByDescending(x => x.MaxSeverityRank)
                    .ThenByDescending(x => x.IssueCount)
                    .Select(x => x.Category)
                    .ToList());

        // 4. 記憶體內折併別名（Surviving）：天數相加、Latest 取日期最大的別名、Categories 依既有排序去重
        var result = hostRecordsAgg
            .GroupBy(x => Surviving(aliasIndex, x.HostId))
            .Select(g =>
            {
                var newest = g.OrderByDescending(x => x.MaxDate).First();
                latestRaw.TryGetValue(newest.HostId, out var head);
                return new HostRiskAggregate
                {
                    HostId = g.Key,
                    HighRiskDays = g.Sum(x => x.HighRiskDays),
                    MediumRiskDays = g.Sum(x => x.MediumRiskDays),
                    LowRiskDays = g.Sum(x => x.LowRiskDays),
                    CorrelationDays = g.Sum(x => x.CorrelationDays),
                    Categories = categoriesByHost.TryGetValue(g.Key, out var cats) ? cats : Array.Empty<string>(),
                    LatestDate = newest.MaxDate,
                    LatestRiskLevel = head?.RiskLevel ?? string.Empty,
                    LatestHeadline = head?.Headline ?? string.Empty
                };
            })
            .ToList();

        Log.Debug("[SQL] IssueAggregate.AggregateByHost（{From:yyyy-MM-dd}~{To:yyyy-MM-dd}）→ {Count} 台主機、{Ms}ms",
            f, t, result.Count, sw.ElapsedMilliseconds);
        _performance?.Record("issues:AggregateByHost", sw.ElapsedMilliseconds);

        return result;
    }

    /// <summary>
    /// Categories／EventId／Source 窄化紀錄——語意同 <see cref="RecordQueryFilter"/> 的同名欄位
    /// （紀錄中任一問題簽章符合即命中該紀錄），與 <c>EfAnalysisRecordStore.ApplyPushableFilters</c>
    /// 的 <c>ctx.TopIssues.Any(...)</c> exists 子查詢是同一個規則的另一份實作——
    /// 這裡查的是 <see cref="DailyRecordRow"/> 不是它，兩邊資料表不同無法直接共用同一個 LINQ 運算式，
    /// 但條件邏輯逐位相同。
    /// </summary>
    private static IQueryable<DailyRecordRow> ApplyIssueExistsFilters(
        LfDbContext ctx, IQueryable<DailyRecordRow> q,
        IReadOnlySet<IssueCategory>? categories, int? eventId, string? source, IssueSeverity? minSeverity)
    {
        if (categories is { Count: > 0 })
        {
            var names = categories.Select(c => c.ToString()).ToList();
            q = q.Where(r => ctx.TopIssues.Any(t => t.RecordId == r.RecordId && names.Contains(t.Category)));
        }
        if (eventId.HasValue)
        {
            var id = eventId.Value;
            q = q.Where(r => ctx.TopIssues.Any(t => t.RecordId == r.RecordId && t.EventId == id));
        }
        if (!string.IsNullOrWhiteSpace(source))
        {
            var src = source.ToUpperInvariant();
            q = q.Where(r => ctx.TopIssues.Any(t => t.RecordId == r.RecordId && t.SourceName.ToUpper() == src));
        }
        if (minSeverity.HasValue)
        {
            var rank = (int)minSeverity.Value;
            q = q.Where(r => ctx.TopIssues.Any(t => t.RecordId == r.RecordId && t.SeverityRank >= rank));
        }
        return q;
    }

    /// <summary>依日期分組的風險類型清單，依「最高嚴重度、問題數」排序
    /// （<see cref="CategoryAggregator"/> 同一套規則的 SQL 端等價物）</summary>
    private static Dictionary<DateTime, IReadOnlyList<string>> CategoriesByDate(IQueryable<TopIssueRow> issuesQuery)
    {
        return issuesQuery
            .Select(x => new { x.RecordDate, x.Category, x.SeverityRank })
            .ToList()
            .GroupBy(x => new { x.RecordDate, x.Category })
            .Select(g => new
            {
                g.Key.RecordDate,
                g.Key.Category,
                MaxSeverityRank = LegacySeverityRank.Normalize(g.Max(x => x.SeverityRank)),
                IssueCount = g.Count()
            })
            .GroupBy(x => x.RecordDate)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g
                    .OrderByDescending(x => x.MaxSeverityRank)
                    .ThenByDescending(x => x.IssueCount)
                    .Select(x => x.Category)
                    .ToList());
    }

    /// <summary>存活主機數＝相異問題底下，host_id 解析成存活主機 id 後的去重數
    /// （合併前後的兩個 id 代表同一台實體機器，只能算一台）</summary>
    private static Dictionary<(string, int), int> SurvivingHostCounts(
        LfDbContext ctx, DateTime from, DateTime to, IReadOnlyCollection<long>? expandedHostIds,
        HostAliasIndex aliasIndex, IReadOnlySet<int>? visibleRanks)
    {
        var q = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= from && x.RecordDate <= to);
        if (expandedHostIds != null) q = q.Where(x => expandedHostIds.Contains(x.HostId));
        if (visibleRanks != null) q = q.Where(x => visibleRanks.Contains(x.SeverityRank));

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
        LfDbContext ctx, DateTime from, DateTime to, IReadOnlyCollection<long>? expandedHostIds,
        HostAliasIndex aliasIndex, IReadOnlySet<int>? visibleRanks)
    {
        var q = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= from && x.RecordDate <= to);
        if (expandedHostIds != null) q = q.Where(x => expandedHostIds.Contains(x.HostId));
        if (visibleRanks != null) q = q.Where(x => visibleRanks.Contains(x.SeverityRank));

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
        LfDbContext ctx, DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<int>? visibleRanks = null)
    {
        var q = ctx.TopIssues.AsNoTracking().Where(x => x.RecordDate >= from && x.RecordDate <= to);
        if (hostIds != null)
        {
            var ids = hostIds.ToList();
            q = q.Where(x => ids.Contains(x.HostId));
        }
        if (visibleRanks != null) q = q.Where(x => visibleRanks.Contains(x.SeverityRank));

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
