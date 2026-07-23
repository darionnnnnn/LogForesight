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
    public List<DashboardSilentHostDto> SilentHosts { get; set; } = new();
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

public class DashboardSilentHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public DateTime? LastReportAt { get; set; }
    public int? DaysSilent { get; set; }
}

// ── 報表（§9.6）────────────────────────────────────────────────────────────

public class ReportSummaryDto
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;

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
}

/// <summary>主機排行 Top 10 之外的彙總（避免主機量大時尾端主機完全隱形）</summary>
public class HostRankingOthersDto
{
    public int HostCount { get; set; }
    public int HighRiskDays { get; set; }
    public int MediumRiskDays { get; set; }
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

/// <summary>跨主機同簽章查詢的結果列</summary>
public class SignatureHitDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? KnownIssue { get; set; }
}
