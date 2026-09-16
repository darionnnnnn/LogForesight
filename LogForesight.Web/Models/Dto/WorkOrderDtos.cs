namespace LogForesight.Web.Models.Dto;

// ── 交辦單命令 API（/api/work-orders）────────────────────────────────────────

/// <summary>
/// 依篩選建單（預覽與建單共用同一個請求）：管理者給「問題＋篩選條件＋分派方式」，
/// 後端解析受影響主機、排除、分攤。篩選語意同問題查詢「依問題」視角。
/// </summary>
public class CreateWorkOrderRequest
{
    public string Source { get; set; } = string.Empty;

    /// <summary>必填；null＝未帶</summary>
    public int? EventId { get; set; }

    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public List<long>? HostIds { get; set; }
    public List<long>? GroupIds { get; set; }

    /// <summary>管理者在預覽取消勾選的主機（整台排除）</summary>
    public List<long>? ExcludeHostIds { get; set; }

    /// <summary><c>single</c>／<c>group</c></summary>
    public string AssignMode { get; set; } = string.Empty;

    /// <summary><c>single</c> 時必填</summary>
    public long? HandlerId { get; set; }

    /// <summary><c>group</c> 時必填：使用者群組 id</summary>
    public long? GroupId { get; set; }

    /// <summary><c>group</c> 時必填：<c>byLoad</c>／<c>roundRobin</c></summary>
    public string? SplitMode { get; set; }

    /// <summary>他人已有進行中案件的主機是否改派</summary>
    public bool ReassignConflicts { get; set; }

    /// <summary><see cref="WorkOrderScopes"/> 值域；<c>Groups</c> 時 <see cref="GroupIds"/> 不可空</summary>
    public string ScopeKind { get; set; } = string.Empty;

    /// <summary><c>ScopeKind=Hosts</c> 時一律視為 false（忽略傳入值）</summary>
    public bool AutoAttach { get; set; }

    public string? Note { get; set; }
    public DateTime? DueDate { get; set; }

    /// <summary>預覽主機清單頁碼（1 起，每頁 100）</summary>
    public int Page { get; set; } = 1;
}

public class WorkOrderPreviewDto
{
    /// <summary>扣除手動排除與雜訊排除後的主機數</summary>
    public int AffectedHosts { get; set; }

    /// <summary>扣除排除後的成員數（主機×完整簽章）</summary>
    public int AffectedMembers { get; set; }

    /// <summary>期間內主機日：依問題視角同範圍中該問題的 DayCount</summary>
    public int EstimatedHostDays { get; set; }

    public string EstimatedHostDaysLabel { get; set; } = "期間內";

    public int NoiseExcludedHosts { get; set; }
    public int ManuallyExcludedHosts { get; set; }

    /// <summary>群組分攤時排除的暫停接單成員數</summary>
    public int PausedMembersExcluded { get; set; }

    public List<WorkOrderConflictDto> Conflicts { get; set; } = new();
    public List<WorkOrderAllocationDto> Allocation { get; set; } = new();

    /// <summary>本頁主機（依主機名稱排序），含被排除或略過的主機</summary>
    public List<WorkOrderPreviewHostDto> Hosts { get; set; } = new();

    public int TotalHosts { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }

    /// <summary>只列前 50 筆，總數見 <see cref="AssigneeNoAccessTotal"/></summary>
    public List<AssigneeNoAccessDto> AssigneeNoAccess { get; set; } = new();
    public int AssigneeNoAccessTotal { get; set; }

    public List<AssigneeCannotHandleDto> AssigneeCannotHandle { get; set; } = new();
}

public class WorkOrderConflictDto
{
    public long HandlerId { get; set; }
    public string HandlerName { get; set; } = string.Empty;
    public int HostCount { get; set; }
}

public class WorkOrderAllocationDto
{
    public long HandlerId { get; set; }
    public string HandlerName { get; set; } = string.Empty;

    /// <summary>實際會寫入（不含略過）的主機數</summary>
    public int HostCount { get; set; }

    /// <summary>處理人對這個問題已有進行中單時，會併入的單號</summary>
    public long? MergeIntoWorkOrderId { get; set; }
}

public class WorkOrderPreviewHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;

    /// <summary>null＝不會寫入（手動排除、雜訊排除或略過）</summary>
    public long? AllocatedHandlerId { get; set; }

    /// <summary>他人進行中案件的處理人（有衝突時）</summary>
    public string? ExistingHandlerName { get; set; }

    public bool NoiseExcluded { get; set; }
    public bool ManuallyExcluded { get; set; }
}

public class CreateWorkOrderResultDto
{
    public List<WorkOrderCreatedDto> Orders { get; set; } = new();
    public List<WorkOrderSkippedDto> Skipped { get; set; } = new();
    public int NoiseExcludedHosts { get; set; }
    public int DaySyncPendingCases { get; set; }
    public List<AssigneeNoAccessDto> AssigneeNoAccess { get; set; } = new();
    public int AssigneeNoAccessTotal { get; set; }
    public List<AssigneeCannotHandleDto> AssigneeCannotHandle { get; set; } = new();
}

public class WorkOrderCreatedDto
{
    public long WorkOrderId { get; set; }
    public long HandlerId { get; set; }
    public string HandlerName { get; set; } = string.Empty;

    /// <summary>false＝併入既有進行中單</summary>
    public bool CreatedOrder { get; set; }

    public int NewCases { get; set; }
    public int LinkedExisting { get; set; }
    public int Reassigned { get; set; }
}

public class WorkOrderSkippedDto
{
    public string HostName { get; set; } = string.Empty;
    public string? ExistingHandlerName { get; set; }
}

public class AppendWorkOrderRequest
{
    public List<long> HostIds { get; set; } = new();
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public bool ReassignConflicts { get; set; }
}

public class AppendWorkOrderResultDto
{
    public long WorkOrderId { get; set; }
    public int NewCases { get; set; }
    public int LinkedExisting { get; set; }
    public int Reassigned { get; set; }

    /// <summary>他人進行中、未改派而略過的成員數</summary>
    public int SkippedConflicts { get; set; }

    public int NoiseExcludedHosts { get; set; }

    /// <summary>不在目前使用者可見範圍、被忽略的主機數</summary>
    public int IgnoredHosts { get; set; }

    public int DaySyncPendingCases { get; set; }
}

public class ReassignWorkOrderRequest
{
    public long HandlerId { get; set; }
}

public class SplitWorkOrderRequest
{
    public List<string> CaseIds { get; set; } = new();
    public long HandlerId { get; set; }
}

/// <summary>改派／拆單的結果</summary>
public class WorkOrderMoveResultDto
{
    public long WorkOrderId { get; set; }
    public long TargetWorkOrderId { get; set; }
    public bool MergedIntoExisting { get; set; }
    public int MovedCases { get; set; }
    public long PreviousHandlerId { get; set; }
    public List<AssigneeCannotHandleDto> AssigneeCannotHandle { get; set; } = new();
}

public class CancelWorkOrderRequest
{
    public string? Reason { get; set; }
}

public class AdminCloseWorkOrderRequest
{
    public string Status { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

/// <summary>取消／代為結案的結果</summary>
public class WorkOrderCloseResultDto
{
    public long WorkOrderId { get; set; }
    public int ClosedCases { get; set; }
    public int DaySyncPendingCases { get; set; }
}
