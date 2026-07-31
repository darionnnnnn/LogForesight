namespace LogForesight.Web.Models.Dto;

public class ScheduleWindowDto
{
    public string Start { get; set; } = string.Empty;
    public string End { get; set; } = string.Empty;
}

public class ScheduleOptionsDto
{
    public bool Enabled { get; set; }
    public List<ScheduleWindowDto> Windows { get; set; } = new();
    public bool DebugDump { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByAccount { get; set; }

    /// <summary>Enabled=false 時為 null（沒有下一次）</summary>
    public DateTime? NextTriggerTime { get; set; }
}

public class SaveScheduleOptionsRequest
{
    public bool Enabled { get; set; }
    public List<ScheduleWindowDto> Windows { get; set; } = new();
    public bool DebugDump { get; set; }
}

/// <summary>docs/WEB-SCHEDULER-PLAN.md §1.4.4：目前執行中/閒置、觸發來源、最新進度、下次觸發時刻</summary>
public class ScheduleStatusDto
{
    public bool IsRunning { get; set; }
    public string? TriggerText { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? LatestMessage { get; set; }
    public bool CanStop { get; set; }
    public bool ScheduleEnabled { get; set; }
    public DateTime? NextTriggerTime { get; set; }
}

/// <summary>執行前預覽：範圍實際會涵蓋幾台主機（docs/WEB-SCHEDULER-PLAN.md §1.4.4，複用
/// StoreHostListProvider 的清單語意，與主機頁「不靜默少幾台」同一原則）</summary>
public class RunPreviewDto
{
    public int HostCount { get; set; }
}

/// <summary>Scope：all（全部主機）｜segment（網段範圍，NetIQ 主機）｜host（單一主機）</summary>
public class TriggerRunRequest
{
    public string Scope { get; set; } = "all";
    public string? Segment { get; set; }
    public long? HostId { get; set; }

    /// <summary>一次性回補天數覆寫（1..14），不落地設定</summary>
    public int? BackfillDays { get; set; }
}

public class TriggerRunResultDto
{
    public bool Started { get; set; }
    public string Message { get; set; } = string.Empty;
}
