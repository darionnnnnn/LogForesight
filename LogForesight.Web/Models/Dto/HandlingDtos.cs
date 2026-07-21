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
