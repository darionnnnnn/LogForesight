using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 問題排行的組裝（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P4／§10）：儀表板「重點問題」卡與
/// 報表「問題排行」共用同一份投影，兩頁的數字因此必然一致。
///
/// **取代的是什麼**：<c>RecordStatsBuilder.BuildIssueRanking</c> 過去在**記憶體**對
/// 整段期間的紀錄做 GroupBy——6000 台 × 30 天約 18 萬筆紀錄、近 GB 的 ContentJson，
/// 儀表板每次載入都做一遍，報表還要再做一次前期對比（雙倍）。現在走
/// <see cref="IIssueAggregateQuery"/> 的一句 GROUP BY。
///
/// **順帶修掉一個既有的不一致**（規劃 §8.1 缺陷 2）：舊實作用 <c>record.Host</c>
/// （紀錄自帶的原始名稱快照）算主機數，而依問題視角用映射到存活主機的名稱——
/// 環境裡有合併過的主機時，儀表板與依問題視角的主機數會對不上，
/// 而 WEB-SPEC §9.1 宣稱「兩頁數字必然一致」。改走 lf_top_issues 的
/// **存活主機 id** 之後，同一台實體機器不再被算成兩台。
/// </summary>
public class IssueRankingBuilder
{
    private readonly IIssueAggregateQuery _aggregates;
    private readonly IHostStore _hosts;
    private readonly IssueHandlingRollupQuery? _rollup;
    private readonly TopIssueBackfiller? _backfiller;
    private readonly StorageBackend? _backend;
    private readonly DailyRecordBackfiller? _dailyBackfiller;

    /// <param name="rollup">處理概況彙總（§10.6）。null 時 OpenHostCount／ResolvedHostCount 恆為 0——
    /// 只有測試在不需要處理狀態的情境下才會不傳，正式 DI 一律注入（見 ServiceCollectionExtensions）。
    /// 刻意內建計算而不是留給呼叫端傳字典：這個參數過去就是以「呼叫端自己組字典」的形式存在，
    /// 結果兩個正式呼叫端都忘了傳，OpenHostCount 因此死了一整輪（回饋十九輪查證抓到）。</param>
    public IssueRankingBuilder(
        IIssueAggregateQuery aggregates,
        IHostStore hosts,
        IssueHandlingRollupQuery? rollup = null,
        TopIssueBackfiller? backfiller = null,
        StorageBackend? backend = null,
        DailyRecordBackfiller? dailyBackfiller = null)
    {
        _aggregates = aggregates;
        _hosts = hosts;
        _rollup = rollup;
        _backfiller = backfiller;
        _backend = backend;
        _dailyBackfiller = dailyBackfiller;
    }

    /// <summary>
    /// 問題統計目前準不準（docs/archive/SCALE-FIX-PLAN-2026-08-06.md G2）。
    ///
    /// 兩種背景工作都會讓聚合數字**偏低但看起來完全正常**：
    ///   - 遷移未完成：處理狀態還沒搬完（連帶影響處理概況）；
    ///   - 回填未完成：舊的 lf_top_issues 列還沒有聚合維度，不會被計入。
    /// 使用者不需要分辨是哪一種，他只需要知道「現在看到的還不是最終值」——
    /// 所以合成同一個旗標與同一句說明，由儀表板與報表共用。
    /// </summary>
    public (bool Pending, string? Hint) StatsPending()
    {
        var migration = _backend?.HandlingMigrator.State;
        if (migration is { ShouldBlockWrites: true })
        {
            var done = new[] { migration.IssueHandlingDone, migration.IssueCasesDone, migration.RecordHandlingDone }
                .Count(x => x);
            return (true, $"處理狀態的資料搬移中（已完成 {done}/3），統計數字可能不完整。");
        }

        var progress = _backfiller?.Progress;
        if (progress is { InProgress: true })
        {
            return (true, $"問題統計回填中（已完成 {progress.Done:N0}/{progress.Total:N0}），" +
                          "次數與影響範圍可能偏低。");
        }

        // 批次E 讀取面 SQL 化後的第三種背景工作（回饋十九輪批次I 體檢補上）：抽出欄回填
        // 未完成時，舊列的 headline／錯誤警告數等抽出欄還是 DDL 預設值（''／0），畫面上
        // 「0 錯誤」比「數字偏低」更會誤導成「這天很乾淨」——同一句誠實提示必須涵蓋它
        var dailyProgress = _dailyBackfiller?.Progress;
        if (dailyProgress is { InProgress: true })
        {
            return (true, $"畫面欄位回填中（已完成 {dailyProgress.Done:N0}/{dailyProgress.Total:N0}），" +
                          "標題與錯誤／警告數可能暫時顯示為空。");
        }

        return (false, null);
    }

    /// <summary>
    /// 期間內的問題排行。<paramref name="visibleHostIds"/> 為可見範圍
    /// （null＝不限制；空集合＝零結果，與查詢層同一套授權語意）。
    /// <paramref name="totalHosts"/> 供影響率使用，0 時影響率為 0。
    /// </summary>
    public List<IssueRankingDto> Build(
        DateTime from, DateTime to, IReadOnlyCollection<long>? visibleHostIds, int totalHosts)
    {
        var periodDays = Math.Max(1, (to.Date - from.Date).Days + 1);

        // 前期對比用**等長**的前一個期間——拿一週跟一個月比毫無意義（沿用報表既有規則）
        var previousTo = from.Date.AddDays(-1);
        var previousFrom = previousTo.AddDays(-periodDays + 1);
        // 鍵一律正規化成大寫（回饋二十輪 I）：Aggregate 已把大小寫不同的同名來源合併成一筆，
        // 但輸出的 Source 是該期間內任一個原始寫法——本期與前期各自取到的寫法可能不同，
        // 用原始字串當鍵會讓前期對比靜默落空、老問題被當成新問題
        var previous = _aggregates.Aggregate(previousFrom, previousTo, visibleHostIds)
            .ToDictionary(a => IssueProfile.KeyOf(a.Source, a.EventId));

        var current = _aggregates.Aggregate(from, to, visibleHostIds);

        // 機房級基準線（§G1）與 fleet 首見（§G4）：只對「當頁組」（current 命中的問題）查，
        // 不是整份表——與 LatestOccurrences 同一個規模假設。基準期固定「to 往前 30 天」，
        // 與查詢期間本身無關（同批次C「不另外抓一次真實時鐘」的既定原則，這裡的錨點也是 to）
        var issuesOnPage = current.Select(a => (a.Source, a.EventId)).Distinct().ToList();
        var (baselineFrom, baselineTo) = IssueBaselineCalculator.Window(to);
        var baselines = IssueBaselineCalculator.Compute(
            _aggregates.DailyHostCounts(issuesOnPage, baselineFrom, baselineTo, visibleHostIds));
        var fleetFirstSeen = _aggregates.FirstSeenFor(issuesOnPage);

        // PriorityScore 的 tierW（§G3）：受影響主機各自的分級，取最高者代表這個問題的分級權重——
        // 一台核心主機中鏢，即使其餘都是測試機，也不該被稀釋成「一般」
        var hostIdsByIssue = _aggregates.HostIdsByIssue(issuesOnPage, from, to, visibleHostIds);
        var tierByHostId = _hosts.GetAll().ToDictionary(h => h.HostId, h => h.Tier);

        // 處理概況（§10.6）：由本類別內建計算，不留給呼叫端傳字典——這個參數過去就是
        // 「呼叫端自己組字典」的形式存在，結果兩個正式呼叫端都忘了傳，一直是死碼
        // （回饋十九輪查證抓到）。_rollup 為 null 時（測試未注入）OpenHostCount／
        // ResolvedHostCount 維持 0，不影響其餘欄位。
        var handlingByIssue = _rollup?.Build(current, from, to, visibleHostIds);

        // 距今天數以查詢期間的 to 為準，不是另外抓一次真實今天（回饋十九輪批次C）：
        // 分析資料只到昨天，呼叫端（Dashboard/Report）傳的 to 本來就是「現在能看到的最後一天」；
        // 用真實 DateTime.Today 算距今天數，在 to=昨天 時會讓每個問題的天數多算一天，
        // 報表檢視歷史區間時更是離譜（會算出「距今天」而不是「距檢視區間終點」）。
        var today = to.Date;

        return current
            .Select(a =>
            {
                previous.TryGetValue(IssueProfile.KeyOf(a.Source, a.EventId), out var prev);
                var rollup = LookupRollup(handlingByIssue, a);
                var baselineKey = (SourceKey: a.Source.ToUpperInvariant(), a.EventId);
                baselines.TryGetValue(baselineKey, out var baseline);
                fleetFirstSeen.TryGetValue(baselineKey, out var firstSeenInFleet);
                var resolvedFleetFirstSeen = firstSeenInFleet == default ? a.FirstSeen : firstSeenInFleet;

                var highestTier = hostIdsByIssue.TryGetValue(baselineKey, out var affectedHostIds)
                    ? HighestTier(affectedHostIds, tierByHostId)
                    : WebHost.TierStandard;

                var score = IssuePriorityScorer.Score(new IssuePriorityScorer.ScoreInput(
                    MaxSeverity: (IssueSeverity)a.MaxSeverityRank,
                    HostRatio: totalHosts > 0 ? (double)a.HostCount / totalHosts : 0,
                    BaselineDeviationMultiplier: baseline.DeviationMultiplier,
                    FleetFirstSeenDaysAgo: Math.Max(0, (today - resolvedFleetFirstSeen.Date).Days),
                    OpenHostCount: rollup?.OpenHostCount ?? 0,
                    HostCount: a.HostCount,
                    HighestAffectedTier: highestTier));

                return new IssueRankingDto
                {
                    Source = a.Source,
                    EventId = a.EventId,
                    Category = a.Category,
                    MaxSeverity = ((IssueSeverity)a.MaxSeverityRank).ToString(),
                    HostCount = a.HostCount,
                    DayCount = a.DayCount,
                    TotalCount = (int)Math.Min(a.TotalCount, int.MaxValue),

                    FirstSeen = a.FirstSeen.ToString("yyyy-MM-dd"),
                    LastSeen = a.LastSeen.ToString("yyyy-MM-dd"),
                    ActiveDays = a.ActiveDays,
                    PeriodDays = periodDays,
                    DaysSinceLastSeen = Math.Max(0, (today - a.LastSeen.Date).Days),
                    HostRatio = totalHosts > 0 ? (double)a.HostCount / totalHosts : 0,
                    ElevatesDayRisk = a.ElevatesDayRisk,

                    PreviousHostCount = prev?.HostCount ?? 0,
                    PreviousTotalCount = (int)Math.Min(prev?.TotalCount ?? 0, int.MaxValue),
                    IsNew = prev == null,

                    OpenHostCount = rollup?.OpenHostCount ?? 0,
                    ResolvedHostCount = rollup?.ResolvedHostCount ?? 0,

                    BaselineMedianHostCount = baseline.MedianHostCount,
                    BaselineLatestHostCount = baseline.LatestHostCount,
                    BaselineDeviationMultiplier = baseline.DeviationMultiplier,
                    BaselineOccurrenceDays = baseline.OccurrenceDays,

                    FleetFirstSeen = resolvedFleetFirstSeen.ToString("yyyy-MM-dd"),

                    PriorityScore = score.Total,
                    PriorityScoreSeverityWeight = score.SeverityWeight,
                    PriorityScoreHostRatioFactor = score.HostRatioFactor,
                    PriorityScoreSpreadWeight = score.SpreadWeight,
                    PriorityScoreNoveltyWeight = score.NoveltyWeight,
                    PriorityScoreOpenWeight = score.OpenWeight,
                    PriorityScoreTierWeight = score.TierWeight
                };
            })
            // 重點問題卡改依 PriorityScore 排序（§G3），取代單純的「嚴重度→主機數→總次數」——
            // 那套排序看不出「這個問題今天特別該優先處理」。同分時仍以嚴重度→主機數→總次數
            // 為次要排序鍵，確保排序穩定、不會因為 SQL 回傳順序不保證而每次跑出不同順位
            .OrderByDescending(i => i.PriorityScore)
            .ThenByDescending(i => Enum.TryParse<IssueSeverity>(i.MaxSeverity, out var s) ? (int)s : -1)
            .ThenByDescending(i => i.HostCount)
            .ThenByDescending(i => i.TotalCount)
            .ToList();
    }

    /// <summary>
    /// 從已排序的問題排行中濾掉「全部主機都已有結論」的問題（§10.6）——那些問題不該佔用
    /// 重點清單的版面。回傳過濾後的清單與被排除的筆數（呼叫端用於「另有 N 個問題已有結論
    /// （未列入）」）。<see cref="IssueRankingDto.ResolvedHostCount"/> 需要 <see cref="Build"/>
    /// 已注入 <see cref="IssueHandlingRollupQuery"/> 才有值——未注入時（測試）恆為 0，
    /// 此處不會誤排除任何問題。
    /// </summary>
    public static (List<IssueRankingDto> Kept, int ConcludedCount) ExcludeConcluded(IReadOnlyList<IssueRankingDto> ranked)
    {
        var kept = new List<IssueRankingDto>();
        var concluded = 0;
        foreach (var issue in ranked)
        {
            if (issue.HostCount > 0 && issue.ResolvedHostCount == issue.HostCount) concluded++;
            else kept.Add(issue);
        }
        return (kept, concluded);
    }

    /// <summary>
    /// 處理狀態以**完整簽章**為鍵，而排行以 (Source, EventId) 為單位——
    /// 一個群組底下可能有多個完整簽章，任一個有未處理主機就算未處理（規劃 §8.1 缺陷 1）。
    ///
    /// **合併必須是主機 id 集合的聯集，不是計數相加**（回饋十九輪批次I 體檢修正）：同一台
    /// 主機常同時出現在同組的多個簽章下，計數相加會把它算成兩台——OpenHostCount 因此可能
    /// 大於 HostCount，讓 PriorityScore 的 openW 突破 [0.5,1.0] 值域、
    /// <see cref="ExcludeConcluded"/> 的「全部主機已有結論」永遠不成立。
    /// ResolvedHostCount 取「已有結論且**沒有任何簽章仍未處理**」的主機數（resolved 差集 open）
    /// ——一台主機在簽章 A 已結論、簽章 B 仍未處理，對「這個問題處理完了沒」的答案是還沒。
    /// </summary>
    private static IssueHandlingRollup? LookupRollup(
        IReadOnlyDictionary<string, IssueHostStatusSets>? byIssueKey, IssueAggregate aggregate)
    {
        if (byIssueKey == null || aggregate.IssueKeys.Count == 0) return null;

        HashSet<long>? openHosts = null;
        HashSet<long>? resolvedHosts = null;
        foreach (var key in aggregate.IssueKeys)
        {
            if (!byIssueKey.TryGetValue(key, out var sets)) continue;
            (openHosts ??= new HashSet<long>()).UnionWith(sets.OpenHosts);
            (resolvedHosts ??= new HashSet<long>()).UnionWith(sets.ResolvedHosts);
        }
        if (openHosts == null && resolvedHosts == null) return null;

        var open = openHosts ?? new HashSet<long>();
        var fullyResolved = (resolvedHosts ?? new HashSet<long>()).Count(id => !open.Contains(id));
        return new IssueHandlingRollup(open.Count, fullyResolved);
    }

    /// <summary>受影響主機裡最高的分級（§G3 tierW 用：一台核心主機中鏢，不該被其餘測試機稀釋）。
    /// 查不到 Tier（理論上不會發生，WebHost.Tier 恆有值）或主機清單為空時退回一般。</summary>
    private static string HighestTier(IReadOnlyCollection<long> hostIds, IReadOnlyDictionary<long, string> tierByHostId)
    {
        var best = WebHost.TierStandard;
        var bestRank = TierRank(best);
        foreach (var hostId in hostIds)
        {
            if (!tierByHostId.TryGetValue(hostId, out var tier)) continue;
            var rank = TierRank(tier);
            if (rank > bestRank) { best = tier; bestRank = rank; }
        }
        return best;
    }

    private static int TierRank(string tier) => tier switch
    {
        WebHost.TierCore => 2,
        WebHost.TierTest => 0,
        _ => 1
    };
}

/// <summary>單一問題簽章的處理概況彙總（§10.6：全部有結論的問題不進重點清單）</summary>
public sealed record IssueHandlingRollup(int OpenHostCount, int ResolvedHostCount);
