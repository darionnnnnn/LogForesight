using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

public class HandlingDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;

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

/// <summary>設定單一問題的處理狀態（詳情頁逐列狀態鈕）</summary>
public class SetIssueStatusRequest
{
    /// <summary>問題簽章鍵（IssueSignatureKey）</summary>
    [Required]
    public string IssueKey { get; set; } = string.Empty;

    /// <summary>resolved | wont_fix | false_positive | known_noise | open；空字串＝清除標記（回到未處理）</summary>
    public string Status { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "處理說明長度不可超過 1000 字元")]
    public string? Note { get; set; }

    /// <summary>
    /// status=open 時才有意義：這個問題目前是「低風險預設不處理」或「已知雜訊記憶自動判讀」
    /// 而使用者要「調回未處理」——true 時一併刪除對應的雜訊記憶，之後同簽章不再自動判讀成雜訊。
    /// </summary>
    public bool ForgetNoise { get; set; }
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
}

public class HandlingLogDto
{
    public string Action { get; set; } = string.Empty;
    public string ActionText { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string? HandlerName { get; set; }
    public string? Note { get; set; }
    public string ActorAccount { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class HandlingTodoDto
{
    public int OpenCount { get; set; }
    public int InProgressCount { get; set; }
    public int OverdueCount { get; set; }
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
