using System.ComponentModel.DataAnnotations;

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

    [StringLength(1000, ErrorMessage = "說明不可超過 1000 字")]
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
    [StringLength(1000, ErrorMessage = "說明不可超過 1000 字")]
    public string? Reason { get; set; }
}

public class AdminCloseWorkOrderRequest
{
    public string Status { get; set; } = string.Empty;
    [StringLength(1000, ErrorMessage = "說明不可超過 1000 字")]
    public string? Reason { get; set; }
}

/// <summary>取消／代為結案的結果</summary>
public class WorkOrderCloseResultDto
{
    public long WorkOrderId { get; set; }
    public int ClosedCases { get; set; }
    public int DaySyncPendingCases { get; set; }
}

// ── 交辦單查詢 API（/api/work-orders GET）────────────────────────────────────

/// <summary>清單請求（查詢字串綁定）</summary>
public class WorkOrderListRequest
{
    public long? HandlerId { get; set; }
    public string? Source { get; set; }
    public int? EventId { get; set; }

    /// <summary><c>active</c>／<c>escalated</c>／<c>overdue</c>／<c>unreplied</c>／<c>closed</c>／<c>all</c></summary>
    public string Status { get; set; } = "active";

    /// <summary>true＝只列處理人已停用的單</summary>
    public bool HandlerInactive { get; set; }

    /// <summary>處理人所屬的使用者群組（回饋第 47 輪定案 48：與負載看板同義）；null＝不限</summary>
    public long? GroupId { get; set; }

    /// <summary><c>created_desc</c>／<c>members_desc</c>／<c>unreplied_oldest</c></summary>
    public string Sort { get; set; } = "created_desc";

    public int Page { get; set; } = 1;

    /// <summary>預設 20，上限 100</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>
    /// 暫停單（問題目前靜音中）篩選：<c>include</c>／<c>exclude</c>／<c>only</c>；
    /// 空白依入口預設（處理人清單 exclude、總覽 include）
    /// </summary>
    public string Paused { get; set; } = string.Empty;

    /// <summary>true＝只列「靜音到期、近 7 日內恢復」問題的進行中單（見 <see cref="WorkOrderRowDto.ResumedFromMuteAt"/>）</summary>
    public bool ResumedFromMute { get; set; }
}

/// <summary>清單篩選用的使用者群組選項（回饋第 47 輪定案 48）</summary>
public class HandlerGroupOptionDto
{
    public long GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public bool DispatchPool { get; set; }
}

public class WorkOrderCountsDto
{
    public int Total { get; set; }
    public int Active { get; set; }
    public int Closed { get; set; }
    public int InProgress { get; set; }
    public int Observing { get; set; }
    public int Open { get; set; }
    public int Escalated { get; set; }
    public int Overdue { get; set; }
    public int DaySyncPending { get; set; }
}

public class WorkOrderRowDto
{
    public long WorkOrderId { get; set; }
    public string? Source { get; set; }
    public int? EventId { get; set; }
    public string IssueLabel { get; set; } = string.Empty;
    public string? PlainExplanation { get; set; }
    public long HandlerId { get; set; }

    /// <summary>顯示名稱(帳號)；使用者已刪除為「（已刪除）」</summary>
    public string HandlerName { get; set; } = string.Empty;

    public bool HandlerActive { get; set; }
    public bool HandlerPaused { get; set; }
    public string Origin { get; set; } = string.Empty;
    public string ScopeKind { get; set; } = string.Empty;
    public bool AutoAttach { get; set; }
    public WorkOrderCountsDto Counts { get; set; } = new();
    public DateTime? DueDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAppendedAt { get; set; }
    public DateTime? LastReplyAt { get; set; }

    /// <summary>進行中且從未回覆時＝今天與建立日的日數差；其餘 null</summary>
    public int? UnrepliedDays { get; set; }

    public DateTime? ClosedAt { get; set; }
    public string? ClosedReason { get; set; }

    /// <summary>進行中且問題目前靜音中（不落盤，每次查詢推導）；問題欄為 null 的單恆為 false</summary>
    public bool Paused { get; set; }

    /// <summary>暫停時＝今天所在靜音區間的迄日（yyyy-MM-dd）；否則 null</summary>
    public string? MutedUntil { get; set; }

    /// <summary>
    /// 進行中、問題不在目前靜音中，且最近一個已結束區間的迄日落在 [今天−7, 今天−1] 時＝迄日＋1 天（yyyy-MM-dd）；否則 null
    /// </summary>
    public string? ResumedFromMuteAt { get; set; }
}

public class WorkOrderListDto
{
    public List<WorkOrderRowDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class WorkOrderScopeGroupDto
{
    public long GroupId { get; set; }

    /// <summary>主機群組已刪除為「（已刪除）」</summary>
    public string GroupName { get; set; } = string.Empty;
}

public class WorkOrderDetailDto : WorkOrderRowDto
{
    public string? Note { get; set; }
    public string CreatedByAccount { get; set; } = string.Empty;
    public List<WorkOrderScopeGroupDto> ScopeGroups { get; set; } = new();
    public bool ViewerIsHandler { get; set; }
    public bool ViewerCanAssign { get; set; }
}

public class WorkOrderMemberDto
{
    public string CaseId { get; set; } = string.Empty;

    /// <summary>主機已不存在時 null</summary>
    public long? HostId { get; set; }

    public string HostName { get; set; } = string.Empty;
    public string IssueKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public bool Overdue { get; set; }
    public DateTime FirstLinkedDate { get; set; }
    public DateTime LastLinkedDate { get; set; }
    public bool DaySyncPending { get; set; }
    public bool Cancelled { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public class WorkOrderMemberPageDto
{
    public List<WorkOrderMemberDto> Items { get; set; } = new();
    public int Total { get; set; }

    /// <summary>因檢視者可見範圍而沒列出的成員數（沒過濾時為 0）</summary>
    public int HiddenMemberCount { get; set; }

    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class WorkOrderEventDto
{
    public string Action { get; set; } = string.Empty;
    public string ActionText { get; set; } = string.Empty;

    /// <summary>有操作者時為顯示名稱(帳號)，系統動作為「系統」</summary>
    public string ActorName { get; set; } = string.Empty;

    public int MemberDelta { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ── 負載看板／待派清單／立即派工（/api/work-orders/load-board、gaps、auto-dispatch）───────

public class LoadBoardDto
{
    public List<LoadBoardRowDto> Rows { get; set; } = new();
}

public class LoadBoardRowDto
{
    public long UserId { get; set; }

    /// <summary>顯示名稱(帳號)；使用者已刪除為「（已刪除）」</summary>
    public string Name { get; set; } = string.Empty;

    public bool Active { get; set; }
    public bool Paused { get; set; }
    public bool InPool { get; set; }
    public int ActiveWorkOrders { get; set; }
    public int ActiveMembers { get; set; }
    public int UnrepliedWorkOrders { get; set; }
    public int OverdueMembers { get; set; }
    public int ClosedLast7Days { get; set; }

    /// <summary>沒有進行中單為 null</summary>
    public DateTime? OldestActiveCreatedAt { get; set; }
}

public class GapsDto
{
    public List<GapRowDto> Rows { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }

    /// <summary>出現點超過上限：不做試跑，請縮小期間</summary>
    public bool TooLarge { get; set; }

    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

/// <summary>待派清單的一列＝一個問題（Source, EventId）</summary>
public class GapRowDto
{
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string IssueLabel { get; set; } = string.Empty;
    public string? PlainExplanation { get; set; }

    /// <summary>缺口主機數</summary>
    public int GapHosts { get; set; }

    /// <summary>閘門排除：原因（gate_suppressed／gate_noise／gate_severity／gate_dismissed／muted）→ 台數</summary>
    public Dictionary<string, int> Excluded { get; set; } = new();

    public List<GapSuggestionDto> Suggested { get; set; } = new();

    public List<GapUnassignableDto> Unassignable { get; set; } = new();

    /// <summary>no_visibility 主機所屬的主機群組名稱（去重、最多 10 個）</summary>
    public List<string> UnseenHostGroups { get; set; } = new();
}

public class GapSuggestionDto
{
    public long HandlerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Hosts { get; set; }

    /// <summary>該人此問題目前沒有進行中單（立即派工會建新單）</summary>
    public bool WillCreate { get; set; }
}

public class GapUnassignableDto
{
    /// <summary>no_pool／all_paused／no_visibility／disabled</summary>
    public string Reason { get; set; } = string.Empty;

    public int Hosts { get; set; }
}

public class AutoDispatchRequest
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

public class AutoDispatchResultDto
{
    public int CreatedOrders { get; set; }
    public int MergedOrders { get; set; }
    public int AssignedMembers { get; set; }
    public List<AutoDispatchHandlerDto> PerHandler { get; set; } = new();
    public List<GapUnassignableDto> Unassigned { get; set; } = new();
    public int DaySyncPendingCases { get; set; }

    /// <summary>建單擲例外的組（其餘組照常處理；全部失敗時仍回結果、不擲）</summary>
    public List<AutoDispatchFailedGroupDto> FailedGroups { get; set; } = new();
}

public class AutoDispatchFailedGroupDto
{
    public long HandlerId { get; set; }
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public int Hosts { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class AutoDispatchHandlerDto
{
    public long HandlerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Hosts { get; set; }
}

// ── 處理人端：回覆／我的交辦清單摘要（/api/work-orders/{id}/reply、/api/handlers）──────

/// <summary>單張交辦單回覆</summary>
public class WorkOrderReplyRequest
{
    /// <summary>null 或空＝全部進行中成員</summary>
    public List<string>? CaseIds { get; set; }

    /// <summary>open＝清除（調回未處理）</summary>
    public string Status { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "說明不可超過 1000 字")]
    public string? Note { get; set; }
    public DateTime? DueDate { get; set; }
}

/// <summary>處理人修改單的期限（PUT api/work-orders/{id}/due-date）；null＝清除期限</summary>
public class WorkOrderDueDateRequest
{
    public DateTime? DueDate { get; set; }
}

public class WorkOrderDueDateResultDto
{
    public long WorkOrderId { get; set; }
    public DateTime? DueDate { get; set; }
}

public class WorkOrderReplyResultDto
{
    public long WorkOrderId { get; set; }
    public int Cases { get; set; }
    public bool WorkOrderClosed { get; set; }
    public int DaySyncPendingCases { get; set; }
}

/// <summary>多張交辦單一次回覆（每張全部進行中成員）</summary>
public class WorkOrderReplyManyRequest
{
    public List<long> WorkOrderIds { get; set; } = new();
    public string Status { get; set; } = string.Empty;
    [StringLength(1000, ErrorMessage = "說明不可超過 1000 字")]
    public string? Note { get; set; }
    public DateTime? DueDate { get; set; }
}

public class WorkOrderReplyManyResultDto
{
    public int WorkOrders { get; set; }
    public int Cases { get; set; }
    public int ClosedWorkOrders { get; set; }
    public int DaySyncPendingCases { get; set; }

    /// <summary>已寫入成功的單（依送出順序）</summary>
    public List<long> Succeeded { get; set; } = new();

    /// <summary>寫入中途失敗的那張；全部成功為 null</summary>
    public long? FailedWorkOrderId { get; set; }

    public string? FailureMessage { get; set; }

    /// <summary>失敗那張之後、沒有處理到的單</summary>
    public List<long> NotProcessed { get; set; } = new();
}

/// <summary>處理人的進行中摘要（我的交辦清單頁首與側欄徽章共用）</summary>
public class HandlerSummaryDto
{
    public int ActiveWorkOrders { get; set; }
    public int ActiveMembers { get; set; }
    public int OverdueMembers { get; set; }
    public int UnrepliedWorkOrders { get; set; }

    /// <summary>進行中且暫停（問題目前靜音中）的單數；上面四個數字都不含暫停單</summary>
    public int PausedWorkOrders { get; set; }

    /// <summary>檢視者自己的可見主機數（處理人工作頁空狀態分流用；側欄徽章不填，恆為 0）</summary>
    public int VisibleHostCount { get; set; }

    /// <summary>處理人工作頁標頭（側欄徽章不填）</summary>
    public string DisplayName { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public bool Active { get; set; }
}
