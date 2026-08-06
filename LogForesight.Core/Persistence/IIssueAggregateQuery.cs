namespace LogForesight.Core.Persistence;

/// <summary>
/// 一個問題（Source＋EventId）在某段期間內的聚合結果
/// （docs/SCALE-ISSUE-FIRST-PLAN.md §4.2／根因 C）。
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
}
