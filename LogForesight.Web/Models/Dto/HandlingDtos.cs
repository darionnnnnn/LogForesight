using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

public class HandlingDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;

    /// <summary>
    /// 由問題標記推導出的日狀態（docs/HISTORY.md #6）——與 Status 不同：
    /// Status 是存的日層級快照，這個是「現在真正的狀態」，兩者在批次套用問題標記後可能不同步。
    /// null＝呼叫端未補算（Update/Assign 的回傳值），前端應 fallback 用 Status/StatusText。
    /// </summary>
    public string? DerivedStatus { get; set; }
    public string? DerivedStatusText { get; set; }

    /// <summary>當日問題總數與已結案數（供「處理中（3/5 已結案）」這類提示使用）</summary>
    public int TotalIssues { get; set; }
    public int ClosedIssues { get; set; }

    /// <summary>處理人（事件層級，admin 可改派）</summary>
    public long? HandlerId { get; set; }
    public string? HandlerName { get; set; }

    public string? DueDate { get; set; }
    public bool IsOverdue { get; set; }
    public string? Note { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>主機負責人（長期屬性，唯讀顯示——改派處理人不會動到它）</summary>
    public List<string> OwnerNames { get; set; } = new();

    /// <summary>前端據此決定處理人欄位是下拉還是唯讀文字（真正的防線在後端）</summary>
    public bool CanAssign { get; set; }
    public bool CanHandle { get; set; }

    /// <summary>
    /// 本日問題中屬進行中案件的數量（docs/FEEDBACK-4-PLAN.md §2）：「N 項屬進行中案件」提示，
    /// 讓使用者知道為什麼某些問題狀態會「自己動」（案件同步的結果）。
    /// </summary>
    public int OpenCaseCount { get; set; }

    /// <summary>本次指派新建立的案件數（僅 Assign 回應有意義，其餘呼叫端固定為 0）</summary>
    public int CasesCreated { get; set; }

    /// <summary>
    /// 本次指派因該問題已有他人進行中案件而略過的處理人姓名（去重）——2.1「同主機同問題只由
    /// 一個人處理」：不搶走既有案件，前端據此提示「N 個問題已由 ○○○ 的案件涵蓋，未變更」。
    /// </summary>
    public List<string> CasesSkippedHandlerNames { get; set; } = new();
}

public class UpdateHandlingRequest
{
    [Required]
    public string Status { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "處理說明長度不可超過 1000 字元")]
    public string? Note { get; set; }

    public DateTime? DueDate { get; set; }
}

public class AssignHandlerRequest
{
    /// <summary>null = 取消指派</summary>
    public long? HandlerId { get; set; }
}

/// <summary>設定單一問題的處理狀態（低風險預設不處理／已知雜訊自動判讀的快速動作用）</summary>
public class SetIssueStatusRequest
{
    /// <summary>問題簽章鍵（IssueSignatureKey）</summary>
    [Required]
    public string IssueKey { get; set; } = string.Empty;

    /// <summary>resolved | wont_fix | false_positive | known_noise | in_progress | open；空字串＝清除標記（回到未處理）</summary>
    public string Status { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "處理說明長度不可超過 1000 字元")]
    public string? Note { get; set; }

    /// <summary>預計完成日——只有 Status=in_progress 時才會被存下</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// status=open 時才有意義：這個問題目前是「低風險預設不處理」或「已知雜訊記憶自動判讀」
    /// 而使用者要「調回未處理」——true 時一併刪除對應的雜訊記憶，之後同簽章不再自動判讀成雜訊。
    /// </summary>
    public bool ForgetNoise { get; set; }
}

/// <summary>
/// 批次設定多個問題的處理狀態（風險日詳情：勾選多個問題 → 右側處理狀態區塊填一次 → 套用）。
/// 取代逐列各自開面板填寫的舊流程。
/// </summary>
public class BatchSetIssueStatusRequest
{
    /// <summary>勾選的問題簽章鍵清單</summary>
    [Required]
    [MinLength(1, ErrorMessage = "請至少勾選一個問題")]
    public List<string> IssueKeys { get; set; } = new();

    /// <summary>resolved | wont_fix | false_positive | known_noise | in_progress | open；空字串＝清除標記（回到未處理）</summary>
    public string Status { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "處理說明長度不可超過 1000 字元")]
    public string? Note { get; set; }

    /// <summary>預計完成日——只有 Status=in_progress 時才會被存下</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>同 <see cref="SetIssueStatusRequest.ForgetNoise"/>，套用到每一個勾選的問題</summary>
    public bool ForgetNoise { get; set; }
}

/// <summary>批次套用後的結果：更新了哪些問題＋更新後的當日進度</summary>
public class BatchIssueStatusResultDto
{
    public List<string> UpdatedIssueKeys { get; set; } = new();
    public int TotalIssues { get; set; }
    public int ClosedIssues { get; set; }
    public string DayStatus { get; set; } = string.Empty;
    public string DayStatusText { get; set; } = string.Empty;

    /// <summary>本批次因案件同步而額外更新的天數總和（跨全部勾選問題加總，不含各自的觸發日）；
    /// 0＝這批問題目前都沒有進行中案件（docs/FEEDBACK-4-PLAN.md §2）</summary>
    public int CaseSyncedDayCount { get; set; }
}

/// <summary>問題狀態更新後回傳的當日進度（讓前端就地更新「N/M 已處理」與日層級推導狀態）</summary>
public class IssueStatusResultDto
{
    public string IssueKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;

    /// <summary>當日問題總數與已結案數（清單／標題顯示 N/M 已處理）</summary>
    public int TotalIssues { get; set; }
    public int ClosedIssues { get; set; }

    /// <summary>由問題層推導出的日層級狀態（全結案＝resolved，否則沿用日層級 open/in_progress）</summary>
    public string DayStatus { get; set; } = string.Empty;
    public string DayStatusText { get; set; } = string.Empty;

    /// <summary>這個問題若有進行中案件，這次標記同步展開到的天數（不含觸發日本身）；
    /// 0＝目前沒有進行中案件（docs/FEEDBACK-4-PLAN.md §2）</summary>
    public int CaseSyncedDayCount { get; set; }
}

public class HandlingLogDto
{
    public string Action { get; set; } = string.Empty;
    public string ActionText { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string? HandlerName { get; set; }
    public string? Note { get; set; }

    /// <summary>問題層級操作才有值（「Source EventId」）——docs/HISTORY.md #6</summary>
    public string? IssueLabel { get; set; }

    public string ActorAccount { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class HandlingTodoDto
{
    public int OpenCount { get; set; }
    public int InProgressCount { get; set; }
    public int OverdueCount { get; set; }

    /// <summary>
    /// 已處理的風險日數——docs/HISTORY.md #6、docs/HISTORY.md #12
    /// 報表「處理進度」用。對外三態（<see cref="HandlingStatuses.ExternalOf"/>）：
    /// 日層級 fallback 若為 wont_fix／false_positive／known_noise 也算已處理——這三態是
    /// 「這天已有結論」，對外部檢視（report/dashboard 只看還有沒有事要做）而言等同已處理，
    /// 不再像舊版那樣落在 Open/InProgress/Resolved 三個桶都數不到、讓「未完成」把已結案的
    /// 日子誤算進去。**與問題層級（詳情頁）「已處理」計數是不同的哲學**：詳情頁的三段計數器
    /// （見 record-detail.js renderProgress）刻意把 wont_fix/誤報/已知雜訊排除在「已處理」外，
    /// 那是「這件事的處理方式」的細節，只在單筆問題層級才有意義。</summary>
    public int ResolvedCount { get; set; }

    /// <summary>母體：期間內高＋中風險日總數（分母）——docs/HISTORY.md #6 報表「處理進度」用</summary>
    public int TotalCount { get; set; }
}

// ── 問題案件跨主機批次指派（docs/FEEDBACK-4-PLAN.md §4）────────────────────────

/// <summary>批次指派 modal 開啟時的受影響主機預覽——已有進行中案件的主機標出既有處理人，
/// 讓使用者在送出前就知道哪些主機會被跳過（2.1，不搶走）</summary>
public class IssueCasePreviewHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;

    /// <summary>null＝目前沒有進行中案件，可以指派</summary>
    public string? ExistingHandlerName { get; set; }
}

public class BulkAssignIssueCaseRequest
{
    [Required]
    public string Source { get; set; } = string.Empty;

    public int EventId { get; set; }

    /// <summary>使用者在預覽清單中勾選要指派的主機（可排除部分主機）</summary>
    [Required]
    [MinLength(1, ErrorMessage = "請至少選擇一台主機")]
    public List<long> HostIds { get; set; } = new();

    [Required]
    public long HandlerId { get; set; }

    [StringLength(1000, ErrorMessage = "說明長度不可超過 1000 字元")]
    public string? Note { get; set; }

    public DateTime? DueDate { get; set; }

    /// <summary>受影響主機認定的日期範圍（Q6：範圍認定與回溯深度是兩件事——
    /// 建案後的回溯關聯仍走全部留存歷史，這裡只決定「查哪些主機」）</summary>
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public class BulkAssignSkippedDto
{
    public string HostName { get; set; } = string.Empty;
    public string ExistingHandlerName { get; set; } = string.Empty;
}

public class BulkAssignIssueCaseResultDto
{
    public int Created { get; set; }
    public List<BulkAssignSkippedDto> Skipped { get; set; } = new();
}

// ── 權限異動（§9.5）────────────────────────────────────────────────────────

public class PermissionChangeDto
{
    public string ChangeId { get; set; } = string.Empty;
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
    public string Target { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public string Before { get; set; } = string.Empty;
    public string After { get; set; } = string.Empty;
    public string AlertText { get; set; } = string.Empty;

    public string Status { get; set; } = PermissionConfirmStatuses.Pending;
    public string? ConfirmedByAccount { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string? ConfirmNote { get; set; }
}

public class ConfirmPermissionChangeRequest
{
    /// <summary>authorized（確認為授權操作）| suspicious（標記可疑）</summary>
    [Required]
    public string Status { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Note { get; set; }
}

// ── 操作紀錄（§9.11）───────────────────────────────────────────────────────

public class AuditEntryDto
{
    public long AuditId { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Account { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ActionText { get; set; } = string.Empty;
    public string? TargetKind { get; set; }
    public string? TargetId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? DetailJson { get; set; }
    public string? IpAddress { get; set; }
    public string Result { get; set; } = string.Empty;
}
