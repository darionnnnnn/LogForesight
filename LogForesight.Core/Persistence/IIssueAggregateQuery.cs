namespace LogForesight.Core.Persistence;

/// <summary>
/// 一個問題（Source＋EventId）在某段期間內的聚合結果
/// （docs/archive/SCALE-ISSUE-FIRST-PLAN.md §4.2／根因 C）。
///
/// **這是需求「主視角改成問題」的資料形狀**：主機數與期間跨度是使用者明確要求的兩個數字，
/// 其餘是規劃 §10.3「時間形狀」的五個訊號——它們回答的是
/// 「這是老問題還是新問題／天天都有還是零星爆發／還在不在發生」，
/// 而那正是數量排序永遠答不了、卻決定「今天該先看哪一個」的資訊。
/// </summary>
public sealed class IssueAggregate
{
    public string Source { get; init; } = string.Empty;
    public int EventId { get; init; }

    /// <summary>
    /// 風險類別（<see cref="IssueCategory"/> 的字串）。
    /// 同一個 (Source, EventId) 的類別由規則決定、實務上恆定，因此在 SQL 端取確定性的一個值即可
    /// ——不是「隨便挑」，是「這一組本來就只有一個值」。
    /// </summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>期間內出現過的最高嚴重度（<see cref="IssueSeverity"/> 的整數值）</summary>
    public int MaxSeverityRank { get; init; }

    /// <summary>期間內是否曾命中「重大」旗標</summary>
    public bool ElevatesDayRisk { get; init; }

    /// <summary>影響的相異**存活**主機數（需求：包含此問題的主機數量）</summary>
    public int HostCount { get; init; }

    /// <summary>出現過的主機日總數（同一台主機多天各算一次）</summary>
    public int DayCount { get; init; }

    /// <summary>相異出現日數——與期間天數相除即「出現密度」（§10.3）</summary>
    public int ActiveDays { get; init; }

    /// <summary>期間內最早出現的日期（需求：期間跨度的起點）</summary>
    public DateTime FirstSeen { get; init; }

    /// <summary>期間內最近出現的日期（需求：期間跨度的終點；與今天相減即「是否仍在發生」）</summary>
    public DateTime LastSeen { get; init; }

    /// <summary>全部主機加總的事件次數</summary>
    public long TotalCount { get; init; }

    /// <summary>期間內出現過的相異完整簽章（LogName|Source|EventId|EntryType）——
    /// 處理狀態以完整簽章為鍵，少了它就 join 不到「這個問題有沒有結論」（規劃 §8.1 缺陷 1）</summary>
    public IReadOnlyList<string> IssueKeys { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 問題聚合查詢（P4）。
///
/// **存在的理由**：改版前「這個問題影響幾台、跨哪段期間」只能把整段期間的
/// <c>DailyAnalysisRecord</c> 全部撈回記憶體再 GroupBy——6000 台 × 30 天約 18 萬筆紀錄、
/// 近 GB 的 ContentJson，儀表板每次載入都做一遍，報表還要再做一次前期對比（雙倍）。
/// 這條路沒有優化空間，只能換路：改由 <c>lf_top_issues</c> 一句 GROUP BY 回答。
/// </summary>
public interface IIssueAggregateQuery
{
    /// <summary>
    /// 期間內依 (Source, EventId) 聚合。<paramref name="hostIds"/> 為可見範圍
    /// （null＝不限制；空集合＝零結果，與 <c>RecordQueryFilter.Hosts</c> 同一套授權語意）。
    /// </summary>
    List<IssueAggregate> Aggregate(DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds);

    /// <summary>
    /// 反查：期間內出現過指定問題（Source＋EventId，任一命中即算）的相異存活主機 ID
    /// （回饋十八輪批次F，問題負責人的授權路徑用）。Source 比對不分大小寫；
    /// EventId=0 的問題（未命中規則的 Windows 事件不會落地成 0，這裡單純防禦）不會誤配。
    /// host_id=0（未回填或無主機識別的舊列）不列入——與 <see cref="EfAnalysisRecordStore.ListHostDates"/>
    /// 同一套排除慣例。
    /// </summary>
    HashSet<long> HostIdsFor(IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to);

    /// <summary>
    /// 期間內每個 (存活主機, 完整簽章) 最近一次出現的日期與當時嚴重度（回饋十九輪批次A，
    /// §10.6「排除已有結論的問題」的資料基礎）。這裡只回答「這個組合最後一次出現時長什麼樣子」——
    /// 「有沒有結論」由呼叫端另外查處理狀態／案件表判斷，兩件事分開才不會把 SQL 查詢與
    /// 處理狀態的業務規則（案件優先／觀察到期／預設嚴重度）綁在同一層。
    /// </summary>
    List<HostIssueOccurrence> LatestOccurrences(
        IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds);

    /// <summary>
    /// 依風險類別彙總（回饋十九輪批次D，取代 <c>CategoryAggregator.Merge</c> 在記憶體對
    /// 整段期間紀錄的彙總）。<paramref name="allowedSeverities"/> 為 null 時不限制，
    /// 否則只計入落在集合內的問題（已含 Critical→High 的舊資料相容映射，呼叫端不必自己展開）。
    /// </summary>
    List<CategoryAggregate> AggregateByCategory(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity>? allowedSeverities);
}

/// <summary>單一（存活主機, 完整簽章）在期間內最近一次出現的快照</summary>
public sealed class HostIssueOccurrence
{
    public long HostId { get; init; }
    public string IssueKey { get; init; } = string.Empty;
    public DateTime LastSeen { get; init; }
    public int SeverityRank { get; init; }
}

/// <summary>
/// 單一風險類別在期間內的彙總（回饋十九輪批次D，§二-2「大數字該是去重筆數」）。
///
/// **兩種計數口徑並存，各自回答不同的問題**：
///   - <see cref="RiskItemCount"/>（大數字）＝這段期間「有幾個不同的（主機,問題）風險資訊」
///     ——同一台主機同一個問題連續多天出現只算一筆，答的是「現在有多少個問題要處理」。
///   - <see cref="CumulativeCount"/>（小字）＝主機×日的原始出現次數加總，答的是
///     「這段期間累計看到幾次」，數字大不代表問題多，只代表拖得久。
/// 過去只有後者（<c>CategoryAggregator</c> 的既有語意），且被誤當「有幾個問題」顯示，
/// 是外部審查點名「總覽儀表板看不出真正嚴重程度」的直接原因。
/// </summary>
public sealed class CategoryAggregate
{
    public string Category { get; init; } = string.Empty;

    /// <summary>去重風險資訊筆數：相異 (存活主機, Source, EventId) 組合數</summary>
    public int RiskItemCount { get; init; }

    /// <summary>主機×日累計出現次數（COUNT(*)，即舊 IssueCount 的語意）</summary>
    public long CumulativeCount { get; init; }

    /// <summary>相異存活主機數</summary>
    public int AffectedHosts { get; init; }

    /// <summary>期間內全部事件次數加總（原始日誌筆數，不去重——與風險資訊筆數是不同的量綱）</summary>
    public long TotalEvents { get; init; }

    /// <summary>
    /// 依風險資訊（去重後）的最高嚴重度分桶，三者之和＝<see cref="RiskItemCount"/>。
    /// 三級化後不再有 Critical 分桶（正規化為 High，見 <see cref="LegacySeverityRank"/>）。
    /// </summary>
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }

    /// <summary>命中「重大」旗標（或舊資料 Critical 正規化強制）的風險資訊筆數，去重口徑</summary>
    public int ElevatesCount { get; init; }
}
