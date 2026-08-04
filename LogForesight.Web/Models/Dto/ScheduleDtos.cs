namespace LogForesight.Web.Models.Dto;

public class ScheduleOptionsDto
{
    public bool Enabled { get; set; }
    public List<ScheduleWindow> Windows { get; set; } = new();
    public bool DebugDump { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByAccount { get; set; }

    /// <summary>顯示格式統一「顯示名稱(帳號)」的素材（docs/archive/FEEDBACK-8-PLAN.md #6）；
    /// 查無對應使用者時為 null，前端退回只顯示帳號</summary>
    public string? UpdatedByDisplayName { get; set; }

    /// <summary>Enabled=false 時為 null（沒有下一次）</summary>
    public DateTime? NextTriggerTime { get; set; }
}

public class SaveScheduleOptionsRequest
{
    public bool Enabled { get; set; }
    public List<ScheduleWindow> Windows { get; set; } = new();
    public bool DebugDump { get; set; }
}

/// <summary>docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4：目前執行中/閒置、觸發來源、最新進度、下次觸發時刻。
/// LastRun* 系列（docs/archive/FEEDBACK-7-PLAN.md）：閒置時顯示「上次執行到底成不成功」，
/// 不然失敗只留在 log 檔，使用者只看過「已開始執行」的 toast 就再也沒有回饋。
/// 站台重啟後 LastRun* 全部為 null（行程內狀態），完整歷史請查執行總表。</summary>
public class ScheduleStatusDto
{
    public bool IsRunning { get; set; }
    public string? TriggerText { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? LatestMessage { get; set; }

    /// <summary>執行進度（docs/archive/FEEDBACK-8-PLAN.md #2）：local｜netiq；ProgressTotal=0 代表尚未有
    /// 量化進度可畫（清理／掃描中），前端改顯示不定進度。</summary>
    public string? ProgressPhase { get; set; }
    public int ProgressDone { get; set; }
    public int ProgressTotal { get; set; }

    public bool CanStop { get; set; }
    public bool ScheduleEnabled { get; set; }
    public DateTime? NextTriggerTime { get; set; }
    public bool? LastRunSuccess { get; set; }
    public string? LastRunMessage { get; set; }
    public string? LastRunTriggerText { get; set; }
    public DateTime? LastRunEndedAt { get; set; }
}

/// <summary>執行前預覽：範圍實際會涵蓋幾台主機（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4，複用
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
