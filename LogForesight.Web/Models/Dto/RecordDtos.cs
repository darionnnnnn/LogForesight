namespace LogForesight.Web.Models.Dto;

/// <summary>問題查詢的清單列</summary>
public class RecordListItemDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }

    /// <summary>當日出現的風險類型（依最高嚴重度排序）</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>有關聯訊號（攻擊鏈/故障鏈）——緊急程度排序的第二順位依據</summary>
    public bool HasCorrelation { get; set; }

    /// <summary>資料不完整或 Security log 未讀取，清單上要標示「沒告警 ≠ 沒問題」</summary>
    public bool HasCoverageGap { get; set; }

    public bool AiAnalyzed { get; set; }

    /// <summary>處理狀態（未處理過的風險日為 open）</summary>
    public string HandlingStatus { get; set; } = HandlingStatuses.Open;
    public string HandlingStatusText { get; set; } = string.Empty;
    public string? HandlerName { get; set; }
    public bool IsOverdue { get; set; }
}

/// <summary>
/// 問題查詢「依主機」視角的彙總列（把同一台主機的多天合併成一列）。
/// 回答的是「哪台主機在這段期間最該擔心」，與明細視角看的是同一批紀錄、同一組篩選。
/// </summary>
public class RecordHostGroupDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public int HighRiskDays { get; set; }
    public int MediumRiskDays { get; set; }
    public int LowRiskDays { get; set; }

    /// <summary>有關聯訊號（攻擊鏈／故障鏈）的日數——緊急程度排序的第二順位</summary>
    public int CorrelationDays { get; set; }

    /// <summary>期間內出現過的風險類型（跨日去重，依最高嚴重度排序）</summary>
    public List<string> Categories { get; set; } = new();

    public string LatestDate { get; set; } = string.Empty;
    public string LatestRiskLevel { get; set; } = string.Empty;
    public string LatestHeadline { get; set; } = string.Empty;
}

/// <summary>
/// 問題查詢「依日期」視角的彙總列（把同一天的多台主機合併成一列）。
/// 回答的是「哪一天整體最不平靜」，適合看事件是否集中爆發。
/// </summary>
public class RecordDateGroupDto
{
    public string Date { get; set; } = string.Empty;
    public int HighRiskHosts { get; set; }
    public int MediumRiskHosts { get; set; }
    public int LowRiskHosts { get; set; }

    /// <summary>當天有關聯訊號的主機數</summary>
    public int CorrelationHosts { get; set; }

    /// <summary>當天出現過的風險類型（跨主機去重，依最高嚴重度排序）</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>當天有風險紀錄的主機數（去重）</summary>
    public int HostCount { get; set; }
}

/// <summary>風險日詳情</summary>
public class RecordDetailDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string HostRoleDesc { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;

    public string Headline { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string TrendAssessment { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public bool AiAnalyzed { get; set; }

    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int AuditEventCount { get; set; }

    public List<IssueDto> TopIssues { get; set; } = new();
    public List<CategorySummaryDto> Categories { get; set; } = new();
    public List<string> TrendAlerts { get; set; } = new();
    public List<string> CorrelationAlerts { get; set; } = new();
    public List<DeepDiveDto> DeepDives { get; set; } = new();

    /// <summary>資料完整性申報（§「沒告警 ≠ 沒問題」）</summary>
    public bool DataIncomplete { get; set; }
    public bool? SecurityLogAvailable { get; set; }
    public List<string> UncoveredChecks { get; set; } = new();

    public bool HasReport { get; set; }
    public WeeklyCheckupDto? WeeklyCheckup { get; set; }
}

public class IssueDto
{
    public string LogName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public int Count { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string? KnownIssue { get; set; }
    public string FirstSeen { get; set; } = string.Empty;
    public string LastSeen { get; set; } = string.Empty;
    public int DistinctMessageCount { get; set; }
    public string? KeyDetails { get; set; }
    public List<string> SampleMessages { get; set; } = new();
    public bool Suppressed { get; set; }

    /// <summary>趨勢的白話描述（含比對數字），前端直接顯示不再自行組裝</summary>
    public string TrendText { get; set; } = string.Empty;
    public string Trend { get; set; } = string.Empty;
}

public class CategorySummaryDto
{
    public string Category { get; set; } = string.Empty;
    public int IssueCount { get; set; }
    public int TotalEvents { get; set; }
    public string MaxSeverity { get; set; } = string.Empty;
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }
}

public class DeepDiveDto
{
    public string Category { get; set; } = string.Empty;
    public List<DeepDiveFindingDto> Findings { get; set; } = new();
}

public class DeepDiveFindingDto
{
    public string Problem { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public List<string> LikelyCauses { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();
}

public class WeeklyCheckupDto
{
    public string CheckupDate { get; set; } = string.Empty;
    public bool HasFindings { get; set; }
    public string Conclusion { get; set; } = string.Empty;
}

/// <summary>主機時間軸的單日格子</summary>
public class TimelineDayDto
{
    public string Date { get; set; } = string.Empty;
    public string? RiskLevel { get; set; }
    public string Headline { get; set; } = string.Empty;
    public bool HasRecord { get; set; }
    public bool HasCoverageGap { get; set; }
}

public class HostDetailDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string RoleDesc { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? NetiqServer { get; set; }
    public DateTime? LastReportAt { get; set; }
    public List<string> GroupNames { get; set; } = new();
    public List<string> OwnerNames { get; set; } = new();
    public List<TimelineDayDto> Timeline { get; set; } = new();
    public WeeklyCheckupDto? LatestCheckup { get; set; }
}
