namespace LogForesight.Web.Models.Dto;

public class ScheduleOptionsDto
{
    public bool Enabled { get; set; }
    public List<ScheduleWindow> Windows { get; set; } = new();
    public bool DebugDump { get; set; }

    /// <summary>是否分析本機主機（回饋十八輪批次D）：預設 true，見 ScheduleOptions.LocalAnalysisEnabled。</summary>
    public bool LocalAnalysisEnabled { get; set; } = true;

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

    /// <summary>是否分析本機主機（回饋十八輪批次D）：預設 true。</summary>
    public bool LocalAnalysisEnabled { get; set; } = true;
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

    /// <summary>執行進度（docs/archive/FEEDBACK-8-PLAN.md #2）：netiq 專屬（回饋十七輪批次E 起，
    /// 本機進度改回報到 <see cref="LocalProgressPhase"/>——本機與 NetIQ 並行執行後不能再共用一組
    /// 欄位）。ProgressTotal=0 代表尚未有量化進度可畫（清理／掃描中），前端改顯示不定進度。</summary>
    public string? ProgressPhase { get; set; }
    public int ProgressDone { get; set; }
    public int ProgressTotal { get; set; }

    /// <summary>子進度（回饋十四輪 UI-6）：netiq-ai／netiq-backpressure，AI 佇列在背景消化，
    /// 與上面的主進度同時在跑——null＝這次執行沒有（或還沒進到）子進度可畫，前端不畫子進度條。</summary>
    public string? SubProgressPhase { get; set; }
    public int SubProgressDone { get; set; }
    public int SubProgressTotal { get; set; }

    /// <summary>本機分析進度（回饋十七輪批次E）：與上面的 NetIQ 主／子進度完全獨立的第三條軌，
    /// 本機與 NetIQ 現在並行執行，可能同時都在回報——null＝這次執行本機階段還沒開始或已跳過
    /// （NetiqHosts 範圍），前端不畫這條軌。</summary>
    public string? LocalProgressPhase { get; set; }
    public int LocalProgressDone { get; set; }
    public int LocalProgressTotal { get; set; }

    public bool CanStop { get; set; }
    public bool ScheduleEnabled { get; set; }
    public DateTime? NextTriggerTime { get; set; }
    public bool? LastRunSuccess { get; set; }
    public string? LastRunMessage { get; set; }
    public string? LastRunTriggerText { get; set; }
    public DateTime? LastRunEndedAt { get; set; }
}

/// <summary>
/// 給**一般使用者**看的執行中告示（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3）。
///
/// 與 <see cref="ScheduleStatusDto"/> 刻意不共用：那個是維運視角（觸發來源、下次觸發時間、
/// 上次成敗、可否停止），需要 DevMonitor／Maintain；這個只回答「現在慢是不是因為在跑分析、
/// 跑到哪了」，任何登入者都看得到，也不透露排程設定。
///
/// 分析跑在站台同一個行程（本輪定案不拆 worker），畫面在分析期間變慢是**預期內的代價**——
/// 這一行告示是那個代價的配套，不是加值功能：使用者知道原因就不會把它當故障回報。
/// </summary>
public class RunActivityDto
{
    public bool IsRunning { get; set; }

    /// <summary>已完成／總數。Total=0＝還沒有量化進度可講（掃描中／清理中），前端只講「進行中」</summary>
    public int Done { get; set; }
    public int Total { get; set; }

    /// <summary>進度的量詞（「台」／「天」）——由後端依階段決定，前端不猜</summary>
    public string? UnitText { get; set; }
}

/// <summary>執行前預覽：範圍實際會涵蓋幾台主機（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4，複用
/// HostListSelection 的清單語意，與主機頁「不靜默少幾台」同一原則）</summary>
public class RunPreviewDto
{
    public int HostCount { get; set; }

    /// <summary>AI 未設定時為 true（統計模式）。前端可據此在「只補跑失敗或未執行的主機」
    /// 選項旁顯示提示文字，說明此選項在 AI 未設定時的實際語意。</summary>
    public bool AiDisabled { get; set; }
}

/// <summary>Scope：all（全部主機）｜segment（網段範圍，NetIQ 主機）｜host（單一主機）</summary>
public class TriggerRunRequest
{
    public string Scope { get; set; } = "all";
    public string? Segment { get; set; }
    public long? HostId { get; set; }

    /// <summary>一次性回補天數覆寫（1..14），不落地設定</summary>
    public int? BackfillDays { get; set; }

    /// <summary>只補跑失敗或未執行的主機（略過已成功且有 AI 分析的），預設 false</summary>
    public bool OnlyMissingOrFailed { get; set; }
}

public class TriggerRunResultDto
{
    public bool Started { get; set; }
    public string Message { get; set; } = string.Empty;
}
