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

    /// <summary>待辦：未處理/處理中/逾期的風險日數</summary>
    public HandlingTodoDto Todo { get; set; } = new();

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

    /// <summary>未回報主機數（§5.4 D-4：計數卡＋下鑽，不再整表渲染逐台清單——
    /// 兩千台規模下這個清單可能本身就有數百筆）。點卡片導向主機頁的「未回報」篩選</summary>
    public int SilentHostsCount { get; set; }

    /// <summary>依主機群組的風險概況（§5.4 D-4）：兩千台規模下「先看部門、再下鑽」是主要動線</summary>
    public List<DashboardGroupRiskDto> GroupRisk { get; set; } = new();
}

/// <summary>
/// 問題排行的一列（docs/archive/FEEDBACK-11-PLAN.md §8）：儀表板「重點問題」卡與報表「問題排行」
/// 共用同一個投影（<c>RecordStatsBuilder.BuildIssueRanking</c>），兩邊的數字因此必然一致。
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

    /// <summary>出現過這個問題的相異主機數</summary>
    public int HostCount { get; set; }

    /// <summary>出現過的主機日總數（同一台主機多天各算一次）</summary>
    public int DayCount { get; set; }

    /// <summary>全部主機加總的事件次數</summary>
    public int TotalCount { get; set; }
}

public class DashboardCategoryDto
{
    public string Category { get; set; } = string.Empty;
    public int IssueCount { get; set; }
    public int TotalEvents { get; set; }
    public string MaxSeverity { get; set; } = string.Empty;
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }

    /// <summary>命中「重大」旗標的問題數（docs/archive/HISTORY.md #1）：分類卡的紅框顯著性
    /// 判定改看這個，取代三級化後恆為 0 的 CriticalCount</summary>
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

    /// <summary>可見且啟用的主機總數（docs/archive/HISTORY.md #6）——與儀表板 TotalHosts 同一來源，
    /// 供「受影響主機占比」圖表當分母（Kpi.AffectedHosts / TotalHosts）</summary>
    public int TotalHosts { get; set; }

    /// <summary>期間內高＋中風險日的處理彙總（docs/archive/HISTORY.md #6）——與儀表板待辦
    /// 同一套 HandlingHistoryQueryService.GetTodo 規則，供「處理進度」圖表（ResolvedCount / TotalCount）</summary>
    public HandlingTodoDto Handling { get; set; } = new();
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
