namespace LogForesight.Web.Models.Dto;

public class DashboardDto
{
    public int Days { get; set; }
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;

    public int TotalHosts { get; set; }
    public int HighRiskDays { get; set; }
    public int MediumRiskDays { get; set; }

    /// <summary>資料不完整或 Security log 未讀取的天數——「沒告警 ≠ 沒問題」的量化指標</summary>
    public int CoverageGapDays { get; set; }

    /// <summary>近 24 小時的 Web 登入失敗次數（僅具 ViewAudit 能力者可見）</summary>
    public int? RecentLoginFailures { get; set; }

    /// <summary>待辦：未處理/處理中/逾期的風險日數——供 IssueTodo 的「未處理風險日 M」副標用，
    /// 不再是 KPI 卡的主要數字（回饋十九輪批次D2）</summary>
    public HandlingTodoDto Todo { get; set; } = new();

    /// <summary>待辦的問題口徑（回饋十九輪批次D2）：KPI 卡主要數字，見 <see cref="IssueTodoDto"/></summary>
    public IssueTodoDto IssueTodo { get; set; } = new();

    /// <summary>待確認的權限異動筆數</summary>
    public int PendingPermissionChanges { get; set; }

    public List<DashboardCategoryDto> Categories { get; set; } = new();
    public List<DashboardHostDto> HostRanking { get; set; } = new();

    /// <summary>
    /// 重點問題 Top N（docs/archive/FEEDBACK-11-PLAN.md §8-1）：儀表板原本只有「類別」與「主機」
    /// 兩個維度，看不出「現在最該處理的是哪幾個問題」——而問題是全站的第一視角（§8）。
    /// 點列下鑽問題查詢的依問題視角（帶 source＋eventId）。
    /// </summary>
    public List<IssueRankingDto> TopIssues { get; set; } = new();

    /// <summary>本期問題排行中「全部主機都已有結論」而未列入 <see cref="TopIssues"/> 的筆數
    /// （§10.6：不讓已結案的問題佔用重點清單版面，但卡底要誠實說出來，不是悄悄少了幾筆）</summary>
    public int ConcludedTopIssueCount { get; set; }

    /// <summary>
    /// 問題統計是否還在背景整理（回填或搬移中，docs/archive/SCALE-FIX-PLAN-2026-08-06.md G2）。
    ///
    /// **為什麼放在這裡而不是 /api/health/detail**：那支需要 <c>Maintain</c>，
    /// 而看排行的是全部角色；而且「這張卡的數字準不準」本來就是這張卡的資料，
    /// 放同一份 DTO 就不可能有人忘了查。未完成時數字**偏低但看起來完全正常**——
    /// 那正是本專案最忌諱的「靜默給錯數字」。
    /// </summary>
    public bool IssueStatsPending { get; set; }

    /// <summary>可直接顯示的說明（非 pending 時為 null）</summary>
    public string? IssueStatsPendingHint { get; set; }

    /// <summary>未回報主機數（§5.4 D-4：計數卡＋下鑽，不再整表渲染逐台清單——
    /// 兩千台規模下這個清單可能本身就有數百筆）。點卡片導向主機頁的「未回報」篩選</summary>
    public int SilentHostsCount { get; set; }

    /// <summary>依主機群組的風險概況（§5.4 D-4）：兩千台規模下「先看部門、再下鑽」是主要動線</summary>
    public List<DashboardGroupRiskDto> GroupRisk { get; set; } = new();
}

/// <summary>
/// 問題排行的一列（docs/archive/FEEDBACK-11-PLAN.md §8）：儀表板「重點問題」卡與報表「問題排行」
/// 共用同一個投影（<see cref="LogForesight.Web.Services.IssueRankingBuilder"/>，SQL 端聚合），
/// 兩邊的數字因此必然一致。
///
/// 刻意**不含處理狀態**：處理概況要逐問題查 handling 標記（依問題視角才做的事），
/// 排行卡回答的是「哪幾個問題影響最大」；點進去的依問題視角就有完整的處理概況。
/// </summary>
public class IssueRankingDto
{
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string Category { get; set; } = string.Empty;

    /// <summary>期間內這個問題出現過的最高嚴重度</summary>
    public string MaxSeverity { get; set; } = string.Empty;

    /// <summary>出現過這個問題的相異主機數（需求：包含此問題的主機數量）</summary>
    public int HostCount { get; set; }

    /// <summary>出現過的主機日總數（同一台主機多天各算一次）</summary>
    public int DayCount { get; set; }

    /// <summary>全部主機加總的事件次數</summary>
    public int TotalCount { get; set; }

    // ── 時間形狀（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §10.3／P4）──────────────────
    //
    // 這些是「今天該先看哪一個」的唯一來源。改版前排行只看數量，結果是 2000 台環境下
    // DCOM 10016 這種「每台都有、每天都一樣」的雜訊恆在第一名（資訊量為零），
    // 而 disk 153 這種「3 台、都在近 3 天、高嚴重度」的硬碟前兆排在很後面。
    // 全部由既有資料推導，不需要新的儲存。

    /// <summary>期間內最早出現的日期（需求：期間跨度的起點）</summary>
    public string FirstSeen { get; set; } = string.Empty;

    /// <summary>期間內最近出現的日期（需求：期間跨度的終點）</summary>
    public string LastSeen { get; set; } = string.Empty;

    /// <summary>相異出現日數——與 <see cref="PeriodDays"/> 相除即出現密度（「2/90 天」）</summary>
    public int ActiveDays { get; set; }

    /// <summary>查詢期間的天數（密度的分母）</summary>
    public int PeriodDays { get; set; }

    /// <summary>距今幾天沒再出現（0＝今天還在發生）。回答「還要不要處理，還是已經自己好了」</summary>
    public int DaysSinceLastSeen { get; set; }

    /// <summary>影響率＝主機數 ÷ 可見主機總數。「600 台」在 2000 台環境是 30%、
    /// 在 50 台環境是全滅——絕對值無法跨環境解讀（§10.2 維度 2）</summary>
    public double HostRatio { get; set; }

    /// <summary>期間內是否曾命中「重大」旗標（過去只在詳情頁看得到，排行清單上缺這個維度）</summary>
    public bool ElevatesDayRisk { get; set; }

    /// <summary>白話說明（取自命中規則的 PlainExplanation；未命中或規則無說明時為 null）</summary>
    public string? PlainExplanation { get; set; }

    /// <summary>前一個等長期間的主機數（變化幅度的基準；0＝本期新出現）</summary>
    public int PreviousHostCount { get; set; }

    /// <summary>前一個等長期間的總次數</summary>
    public int PreviousTotalCount { get; set; }

    /// <summary>本期新出現＝前一個等長期間完全沒有出現過。回答「今天有什麼不一樣」</summary>
    public bool IsNew { get; set; }

    /// <summary>未處理的主機數（§10.6）：全部主機都已有結論的問題不該霸佔重點清單</summary>
    public int OpenHostCount { get; set; }

    /// <summary>已有結論的主機數</summary>
    public int ResolvedHostCount { get; set; }

    // ── 機房級基準線（回饋十九輪批次G1）──────────────────────────────────────
    //
    // 基準＝過去 30 天出現日台數中位數；偏離倍數＝最近出現日台數 ÷ 基準。
    // BaselineOccurrenceDays < 3（規劃定案 N=3）時 Median/Multiplier 皆為 null，
    // 前端顯示「新問題，無基準」——見 IssueBaselineCalculator。

    /// <summary>基準期（過去 30 天）出現日台數中位數，無基準時為 null</summary>
    public double? BaselineMedianHostCount { get; set; }

    /// <summary>基準期最近一次出現日的台數，無基準時為 null</summary>
    public int? BaselineLatestHostCount { get; set; }

    /// <summary>偏離倍數＝BaselineLatestHostCount ÷ BaselineMedianHostCount，無基準時為 null</summary>
    public double? BaselineDeviationMultiplier { get; set; }

    /// <summary>基準期實際出現的天數（呼叫端用於判斷「新問題，無基準」的門檻是否達到）</summary>
    public int BaselineOccurrenceDays { get; set; }

    // ── fleet 首見（回饋十九輪批次G4）───────────────────────────────────────

    /// <summary>問題在整個機房第一次出現的日期（不受查詢期間截斷，↔ lf_issue_first_seen）。
    /// 查無資料時退回 FirstSeen（理論上不會發生，見 IIssueAggregateQuery.FirstSeenFor）。</summary>
    public string FleetFirstSeen { get; set; } = string.Empty;

    // ── 優先度分數（回饋十九輪批次G3，見 IssuePriorityScorer）─────────────────
    //
    // 重點問題卡（儀表板／報表共用本投影）改依這個分數排序，取代單純的
    // 「嚴重度→主機數→總次數」——後者看不出「這個問題今天特別該優先處理」。
    // 六個成分權重原樣附上，前端展開列直接顯示「為什麼是 N 分」，不必重算拆解。

    public double PriorityScore { get; set; }
    public double PriorityScoreSeverityWeight { get; set; }
    public double PriorityScoreHostRatioFactor { get; set; }
    public double PriorityScoreSpreadWeight { get; set; }
    public double PriorityScoreNoveltyWeight { get; set; }
    public double PriorityScoreOpenWeight { get; set; }
    public double PriorityScoreTierWeight { get; set; }
}

/// <summary>
/// 風險類型卡（回饋十九輪批次D，外部審查§零-2「大數字該是去重筆數」）。
/// 兩種計數口徑並存，語意見 <see cref="CategoryAggregate"/>：
/// <see cref="RiskItemCount"/> 是大數字（去重），<see cref="CumulativeCount"/> 是小字（累計）。
/// High/Medium/Low/ElevatesCount 皆依 <see cref="RiskItemCount"/> 的去重口徑分桶，
/// 三桶之和＝RiskItemCount，不是舊版的 IssueCount。
/// </summary>
public class DashboardCategoryDto
{
    public string Category { get; set; } = string.Empty;
    public int RiskItemCount { get; set; }
    public long CumulativeCount { get; set; }
    public long TotalEvents { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }

    /// <summary>命中「重大」旗標（或舊資料 Critical 正規化強制）的風險資訊數，去重口徑</summary>
    public int ElevatesCount { get; set; }

    public int AffectedHosts { get; set; }
}

public class DashboardHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public int HighRiskDays { get; set; }
    public int MediumRiskDays { get; set; }
    public int CorrelationDays { get; set; }
    public string LatestRiskLevel { get; set; } = string.Empty;
    public string LatestHeadline { get; set; } = string.Empty;
}

/// <summary>依主機群組的風險概況（§5.4 D-4）</summary>
public class DashboardGroupRiskDto
{
    public long GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public int HostCount { get; set; }
    public int HighRiskDays { get; set; }
    public int MediumRiskDays { get; set; }
    public int UnhandledCount { get; set; }
}

// ── 報表（§9.6）────────────────────────────────────────────────────────────

public class ReportSummaryDto
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;

    /// <summary>套用的處理狀態顯示範圍（§5）：all｜unresolved｜open｜unassigned。前端據此
    /// 隱藏「處理進度」小圖（scope≠all 時母體已抽掉，恆 0%／100% 無資訊量）。</summary>
    public string HandlingScope { get; set; } = "all";

    /// <summary>KPI 卡：本期數字＋與前一個等長期間的對比</summary>
    public ReportKpiDto Kpi { get; set; } = new();

    /// <summary>趨勢折線：逐日的高/中風險數</summary>
    public List<ReportTrendPointDto> Trend { get; set; } = new();

    /// <summary>類型分布：類別 × 嚴重度（堆疊長條）</summary>
    public List<DashboardCategoryDto> Categories { get; set; } = new();

    /// <summary>主機排行（水平長條 Top 10）</summary>
    public List<DashboardHostDto> HostRanking { get; set; } = new();

    /// <summary>本期有風險日的主機總數——主機量大時 Top 10 之外還有多少台，畫面要說得出來</summary>
    public int RankedHostCount { get; set; }

    /// <summary>Top 10 以外主機的合計（高＋中風險日），供「其他 N 台」彙總條；無其他主機時為 null</summary>
    public HostRankingOthersDto? Others { get; set; }

    /// <summary>
    /// 問題排行（docs/archive/FEEDBACK-11-PLAN.md §8-2）：與主機排行**同一張卡的另一個模式**
    /// （卡 header 的「主機｜問題」切換），不另開第五張卡——報表一頁化的高度是由外而內
    /// 分配的（docs/archive/FEEDBACK-10-PLAN.md §5），多一張常駐卡會讓整組高度重算。
    /// </summary>
    public List<IssueRankingDto> IssueRanking { get; set; } = new();

    /// <summary>本期出現過的相異問題總數——Top 10 之外還有多少個，畫面要說得出來</summary>
    public int RankedIssueCount { get; set; }

    /// <summary>Top 10 以外問題的合計；無其他問題時為 null</summary>
    public IssueRankingOthersDto? IssueOthers { get; set; }

    /// <summary>本期問題排行中「全部主機都已有結論」而未列入 <see cref="IssueRanking"/> 的筆數
    /// （§10.6，與 <see cref="DashboardDto.ConcludedTopIssueCount"/> 同一件事）</summary>
    public int ConcludedIssueCount { get; set; }

    /// <summary>問題統計是否還在背景整理——與 <see cref="DashboardDto.IssueStatsPending"/> 同一件事</summary>
    public bool IssueStatsPending { get; set; }

    public string? IssueStatsPendingHint { get; set; }

    /// <summary>可見且啟用的主機總數（docs/archive/HISTORY.md #6）——與儀表板 TotalHosts 同一來源，
    /// 供「受影響主機占比」圖表當分母（Kpi.AffectedHosts / TotalHosts）</summary>
    public int TotalHosts { get; set; }

    /// <summary>期間內高＋中風險日的處理彙總（docs/archive/HISTORY.md #6）——與儀表板待辦
    /// 同一套 HandlingHistoryQueryService.GetTodo 規則，供「處理進度」圖表（ResolvedCount / TotalCount）</summary>
    public HandlingTodoDto Handling { get; set; } = new();

    public string ComparisonMode { get; set; } = string.Empty;

    /// <summary>
    /// 為什麼需要這一欄：RetentionDays 預設 120 天，去年同期的資料早就被清掉了。
    /// 不申報的話畫面會顯示比較期各項為 0，使用者會讀成「去年完全沒問題」——
    /// 那是最糟的一種錯，因為它看起來完全正常。前端會用這個旗標顯示提示。
    /// </summary>
    public bool ComparisonOutOfRetention { get; set; }
}

/// <summary>主機排行 Top 10 之外的彙總（避免主機量大時尾端主機完全隱形）</summary>
public class HostRankingOthersDto
{
    public int HostCount { get; set; }
    public int HighRiskDays { get; set; }
    public int MediumRiskDays { get; set; }
}

/// <summary>問題排行 Top 10 之外的彙總（§8-2，同主機排行的理由：尾端不隱形）</summary>
public class IssueRankingOthersDto
{
    public int IssueCount { get; set; }
    public int TotalCount { get; set; }
    public int HostCount { get; set; }
}

public class ReportKpiDto
{
    public int TotalIssues { get; set; }
    public int TotalIssuesPrevious { get; set; }
    public int HighRiskDays { get; set; }
    public int HighRiskDaysPrevious { get; set; }
    public int AffectedHosts { get; set; }
    public int AffectedHostsPrevious { get; set; }
    public int CoverageGapDays { get; set; }
}

public class ReportTrendPointDto
{
    public string Date { get; set; } = string.Empty;
    public int HighRisk { get; set; }
    public int MediumRisk { get; set; }
    public int ErrorCount { get; set; }
}
