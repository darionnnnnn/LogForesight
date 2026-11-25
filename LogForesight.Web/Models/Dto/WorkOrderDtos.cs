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

    /// <summary>
    /// 管理者在預覽勾選「重新交辦」的主機：這些主機已結案（處理人回覆已處理／不處理）或被「不再自動派工」擋住的成員，
    /// 照常納入；沒列在這裡的，預覽與建立都會略過這類成員。不再打擾的成員納入時等於恢復（舊案件的擋派旗標改為 false）。
    /// </summary>
    public List<long>? ReopenHostIds { get; set; }

    /// <summary>勾「立即通知」：處理人的通知不等摘要時刻，下一次輪詢就寄。</summary>
    public bool NotifyNow { get; set; }

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

    /// <summary>因「最近一次出現當天已被處理人結案」而略過的主機數（未勾重新交辦）</summary>
    public int ClosedSkippedHosts { get; set; }

    /// <summary>因「不再自動派工」而略過的主機數（未勾重新交辦）</summary>
    public int DismissedSkippedHosts { get; set; }

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

    /// <summary>
    /// 建議期限（預填「預計完成日」用，可清除）：依這個問題在會寫入的主機上的最高嚴重度，自今天（站台時鐘）起算——
    /// 高 3 天、中 7 天（<c>DefaultDueDays</c>）；低或推導不到嚴重度為 null。建立時仍以請求的 <c>DueDate</c> 為準（清掉就是沒有）。
    /// </summary>
    public DateTime? SuggestedDueDate { get; set; }
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

    /// <summary>
    /// 因結案類處理而略過的原因：<c>closed</c>（已結案）／<c>dismissed</c>（不再自動派工）；null＝不是這類略過。
    /// 這類主機一個成員都不會寫入，要寫入請把主機 id 放進請求的 <see cref="CreateWorkOrderRequest.ReopenHostIds"/>。
    /// </summary>
    public string? ClosedReason { get; set; }

    /// <summary>結案狀態（<c>resolved</c>／<c>wont_fix</c>／<c>false_positive</c>／<c>known_noise</c>）；取不到為 null</summary>
    public string? ClosedStatus { get; set; }

    /// <summary>結案者帳號；取不到為 null</summary>
    public string? ClosedByAccount { get; set; }

    /// <summary>結案時間；取不到為 null</summary>
    public DateTime? ClosedAt { get; set; }
}

public class CreateWorkOrderResultDto
{
    public List<WorkOrderCreatedDto> Orders { get; set; } = new();
    public List<WorkOrderSkippedDto> Skipped { get; set; } = new();

    /// <summary>問題簽章過長、未建立案件的成員（其餘成員照常建立）；每筆附使用者看得懂的說明</summary>
    public List<WorkOrderOverlongDto> OverlongSkipped { get; set; } = new();

    public int NoiseExcludedHosts { get; set; }

    /// <summary>因已結案而略過的主機數（與預覽同一份計畫；另含協調器保底再擋下的）</summary>
    public int ClosedSkippedHosts { get; set; }

    /// <summary>因不再自動派工而略過的主機數</summary>
    public int DismissedSkippedHosts { get; set; }

    /// <summary>已結案而略過的主機名稱（依名稱排序、不分大小寫去重，只列前 50 筆；總數見 <see cref="ClosedSkippedHosts"/>）</summary>
    public List<string> ClosedSkippedHostNames { get; set; } = new();

    /// <summary>不再自動派工而略過的主機名稱（同上，只列前 50 筆；總數見 <see cref="DismissedSkippedHosts"/>）</summary>
    public List<string> DismissedSkippedHostNames { get; set; } = new();

    public int DaySyncPendingCases { get; set; }
    public List<AssigneeNoAccessDto> AssigneeNoAccess { get; set; } = new();
    public int AssigneeNoAccessTotal { get; set; }
    public List<AssigneeCannotHandleDto> AssigneeCannotHandle { get; set; } = new();

    /// <summary>有處理人沒有設定 email（不會收到通知）。</summary>
    public bool HandlerHasNoEmail { get; set; }
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

public class WorkOrderOverlongDto
{
    public string HostName { get; set; } = string.Empty;

    /// <summary>中文說明（不含例外原文）</summary>
    public string Message { get; set; } = string.Empty;
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

    /// <summary>問題簽章過長、未建立案件的成員</summary>
    public List<WorkOrderOverlongDto> OverlongSkipped { get; set; } = new();

    public int NoiseExcludedHosts { get; set; }

    /// <summary>不在目前使用者可見範圍、被忽略的主機數</summary>
    public int IgnoredHosts { get; set; }

    public int DaySyncPendingCases { get; set; }
}

public class ReassignWorkOrderRequest
{
    public long HandlerId { get; set; }

    /// <summary>勾「立即通知」：新處理人的通知不等摘要時刻。</summary>
    public bool NotifyNow { get; set; }
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

    /// <summary>新處理人沒有設定 email（不會收到通知）。</summary>
    public bool HandlerHasNoEmail { get; set; }
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

    /// <summary>結案狀態是不處理類時，是否讓這台主機的這個問題之後不再自動派工；結案狀態為「已處理」時忽略</summary>
    public bool BlockRedispatch { get; set; }
}

/// <summary>退回處理人：說明必填（長度與空白由服務層驗證）</summary>
public class ReturnWorkOrderRequest
{
    public string? Note { get; set; }
}

/// <summary>退回處理人的結果</summary>
public class WorkOrderReturnResultDto
{
    public long WorkOrderId { get; set; }
    public int ReturnedCases { get; set; }
    public int DaySyncPendingCases { get; set; }
}

/// <summary>未讀回覆的單數（GET /api/work-orders/unread-count）</summary>
public class WorkOrderUnreadCountDto
{
    public int Count { get; set; }

    /// <summary>未讀單中，含至少一個上報中進行中成員的單數（<see cref="Count"/> 的子集）</summary>
    public int Escalated { get; set; }
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

    /// <summary>
    /// <c>active</c>／<c>escalated</c>／<c>overdue</c>／<c>unreplied</c>／<c>closed</c>／<c>all</c>／<c>todo</c>（待我處理：
    /// 進行中且至少有一個待處理成員，只剩觀察期內或上報中成員的單不列；判準見 <c>WorkOrderQueries.StatusTodo</c>）
    /// </summary>
    public string Status { get; set; } = "active";

    /// <summary>true＝只列處理人已停用或已失去處理能力的單（與續掛停止原因 <c>handler_inactive</c> 同義）</summary>
    public bool HandlerInactive { get; set; }

    /// <summary>處理人所屬的使用者群組（回饋第 47 輪定案 48：與負載看板同義）；null＝不限</summary>
    public long? GroupId { get; set; }

    /// <summary>
    /// <c>created_desc</c>／<c>members_desc</c>／<c>unreplied_oldest</c>，以及讀取時推導的三個：
    /// <c>priority</c>（處理優先序：被退回 → 逾期 → 嚴重度高到低 → 期限近到遠、無期限最後 → 建立時間新到舊）、
    /// <c>dueSoon</c>（期限最近）、<c>severity</c>（嚴重度高到低）。三個推導鍵對「符合篩選的全體」排序後才分頁，不是只排當頁。
    /// </summary>
    public string Sort { get; set; } = "created_desc";

    /// <summary>
    /// 標記篩選（我的交辦「今天需要你處理」各數字的明細）：<c>returned</c>（被退回）／<c>newToday</c>（今天新增）／
    /// <c>recurrence</c>（復發）／<c>observationExpired</c>（觀察到期）／<c>dueSoon</c>（3 天內到期）；空白＝不篩。
    /// **組合規則**：與 <see cref="Status"/>（含 <c>todo</c> 待我處理）、暫停、處理人等其他條件一律取交集（AND），
    /// 不互相覆蓋——例如 <c>status=todo&amp;flag=returned</c> ＝待我處理且被退回的單。標記只看進行中、非停用主機的成員，
    /// 所以已結案的單不會被任何標記選中。
    /// </summary>
    public string? Flag { get; set; }

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

    /// <summary>true＝只列有未讀回覆的單（判定見 <c>WorkOrder.HasUnreadReply</c>）</summary>
    public bool UnreadOnly { get; set; }
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

    /// <summary>進行中、但主機已停用的成員數；不計入 <see cref="Active"/> 與各狀態數</summary>
    public int ActiveOnDisabledHosts { get; set; }
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

    /// <summary>進行中且夜間派工曾因處理人不符資格而擋下續掛（<c>WorkOrder.ContinuationStoppedReason</c> 非 null）</summary>
    public bool ContinuationStopped { get; set; }

    /// <summary>續掛已停止的原因代碼：handler_inactive／handler_paused／host_not_visible；沒有被擋為 null</summary>
    public string? ContinuationStoppedReason { get; set; }

    /// <summary>原因的顯示名稱（<c>ContinuationStopReasons.LabelOf</c>）</summary>
    public string? ContinuationStoppedLabel { get; set; }

    /// <summary>最近一次續掛被擋的夜間派工時間</summary>
    public DateTime? ContinuationStoppedAt { get; set; }

    public WorkOrderCountsDto Counts { get; set; } = new();

    /// <summary>
    /// 進行中的不重複主機數（成員以主機名鍵去重，不含停用主機上的成員）。<see cref="WorkOrderCountsDto.Active"/> 數的是成員（案件）；
    /// 同一台主機在同一張單內有新舊案件時，成員數會大於主機數。
    /// </summary>
    public int ActiveHosts { get; set; }

    /// <summary>歷來不重複主機數（全部成員，含已結案與停用主機上的成員）</summary>
    public int TotalHosts { get; set; }

    /// <summary>進行中成員裡是「復發」的不重複主機數（判定見 <c>IssueRecurrence</c>）；不含停用主機</summary>
    public int RecurrenceHosts { get; set; }

    /// <summary>
    /// 進行中成員裡帶「先前判定不處理」（同主機同問題上一件是處理人判定不處理、已滿 30 天仍在發生才重新派工）的不重複主機數；不含停用主機。
    /// 判定與成員的 <see cref="WorkOrderMemberDto.PriorDismissal"/> 同一個函式（<c>DismissalRelease.PriorDismissalOf</c>）。
    /// </summary>
    public int PriorDismissalHosts { get; set; }

    /// <summary>
    /// 這張單的問題之「不再打擾」滿 30 天會自動解除（目前規則嚴重度高或重大）：回覆彈窗選到不處理類時據此換後果提示，
    /// 前端不自己判斷嚴重度。多問題單（沒有單一問題）為 false。
    /// </summary>
    public bool DismissalReleases { get; set; }

    /// <summary>觀察期內（觀察中且觀察至 &gt;= 今天）的進行中不重複主機數；不含停用主機</summary>
    public int ObservingHosts { get; set; }

    /// <summary>觀察到期（觀察中且觀察至 &lt; 今天）的進行中不重複主機數；不含停用主機</summary>
    public int ObservationExpiredHosts { get; set; }

    /// <summary>
    /// 待處理成員（進行中、非上報中、非觀察期內；觀察到期算待處理，判準同 <c>WorkOrderQueries.IsTodoMember</c>）的不重複主機數；
    /// 不含停用主機。清單狀態篩選為 <c>todo</c>（待我處理）時以這個數字當台數。
    /// </summary>
    public int TodoHosts { get; set; }

    /// <summary>
    /// 今天新增：進行中成員裡建立時間（日期）是今天（站台時鐘）的不重複主機數；不含停用主機。
    /// 昨天 23:59 建立的不算。
    /// </summary>
    public int NewTodayHosts { get; set; }

    /// <summary>
    /// 被退回的成員數：這張單最近一筆退回事件之後處理人尚未回覆時，＝仍是處理中、說明是該次退回說明的進行中成員數
    /// （夜間續掛新增的成員不算）；沒有被退回、或退回後處理人已回覆為 0。以成員（案件）計，不是主機。
    /// </summary>
    public int ReturnedMembers { get; set; }

    /// <summary>
    /// 顯示用的嚴重度（<c>High</c>／<c>Medium</c>／<c>Low</c>）：取單內**進行中**成員最近一次出現的最高嚴重度
    /// （舊資料的 Critical 視為 High）；單內沒有進行中成員時取全部成員的最後已知值；都推導不到為 null（畫面顯示「—」）。
    /// </summary>
    public string? Severity { get; set; }

    /// <summary>重大旗標：用來決定 <see cref="Severity"/> 的那些最近一次出現中，有任何一個命中重大旗標（<c>elevates_day_risk</c>）</summary>
    public bool Critical { get; set; }

    public DateTime? DueDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAppendedAt { get; set; }
    public DateTime? LastReplyAt { get; set; }

    /// <summary>進行中且從未回覆時＝今天與建立日的日數差；其餘 null</summary>
    public int? UnrepliedDays { get; set; }

    /// <summary>管理者尚未看過處理人的最新回覆（<c>WorkOrder.HasUnreadReply</c>）</summary>
    public bool UnreadReply { get; set; }

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

    /// <summary>可回覆：檢視者是目前處理人本人、具 Handle 能力、且單尚未結案，三者皆成立（後端單點決定）</summary>
    public bool ViewerCanReply { get; set; }

    /// <summary>檢視者是最近一次被改派走的原處理人（只能唯讀檢視）</summary>
    public bool ViewerIsPreviousHandler { get; set; }
    public bool ViewerCanAssign { get; set; }

    /// <summary>
    /// 進行中的成員裡，處理人目前看不到的台數（讀取時以處理人的可見範圍計算、不存欄位；規則同夜間派工的可見主機，不含案件授與，
    /// 不含已停用主機）。0＝處理人都看得到；單已結案恆為 0。
    /// </summary>
    public int HandlerUnseenMemberCount { get; set; }

    /// <summary>
    /// 觀察到期且觀察期間「確定沒有再發生」（<see cref="WorkOrderMemberDto.ObservedRecurDays"/> 為 0）的進行中成員案件編號。
    /// 觀察期間沒有任何分析紀錄（無資料，K 為 null）的成員不列。畫面「把到期且沒有再發生的 N 台結案」一鍵動作以既有回覆端點送出這些案件
    /// （狀態已處理、說明「觀察期滿未再發生」）；不含停用主機上的成員。
    /// </summary>
    public List<string> ObservationExpiredQuietCaseIds { get; set; } = new();
}

public class WorkOrderMemberDto
{
    public LogForesight.Core.Models.PrtgCaseEvidence? PrtgEvidence { get; set; }
    public string? PrtgEvidenceParentStatus { get; set; }
    public string? PrtgEvidenceRecordPath { get; set; }
    public string CaseId { get; set; } = string.Empty;

    /// <summary>主機已不存在時 null</summary>
    public long? HostId { get; set; }

    public string HostName { get; set; } = string.Empty;

    /// <summary>這台主機已停用：進行中時不計入處理人的台數（徽章、負載看板、總覽）；重新啟用後恢復計入</summary>
    public bool HostDisabled { get; set; }
    public string IssueKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 處理人回覆時寫的說明（上報時就是上報原因）；沒有說明為 null。
    /// 成員清單已依檢視者的可見範圍過濾，說明不會洩漏到看不到的主機。
    /// </summary>
    public string? Note { get; set; }
    public DateTime? DueDate { get; set; }
    public bool Overdue { get; set; }
    public DateTime FirstLinkedDate { get; set; }
    public DateTime LastLinkedDate { get; set; }
    public bool DaySyncPending { get; set; }
    public bool Cancelled { get; set; }
    public DateTime? ClosedAt { get; set; }

    /// <summary>同一主機與 PRTG sensor 的另一張進行中 Warning／磁碟趨勢交辦單；僅同處理人案件會回傳</summary>
    public long? RelatedWorkOrderId { get; set; }

    /// <summary>同主機同 sensor 最近 30 日已落盤的磁碟趨勢 finding</summary>
    public WorkOrderDiskTrendEvidenceDto? DiskTrend { get; set; }

    /// <summary>
    /// 是復發：同主機同問題（Source＋EventId）的上一件案件以「已處理」結案，且結案日落在本件建立日往前 30 天內
    /// （日曆日差，第 30 天算、第 31 天不算）。上一件是不處理類結案不算；沒有上一件不算。判定在 <c>IssueRecurrence</c>。
    /// </summary>
    public bool IsRecurrence { get; set; }

    /// <summary>復發次數 N：建立於本件之前、以「已處理」結案、結案日在上述 30 天內的同主機同問題案件數</summary>
    public int RecurrenceCount { get; set; }

    /// <summary>上一件案件最後的回覆（結案日、狀態、說明）；沒有上一件為 null。與是否復發無關，由畫面決定怎麼用</summary>
    public WorkOrderLastReplyDto? LastReply { get; set; }

    /// <summary>
    /// 觀察期間再發生日數 K：觀察開始日（不含）到今天（含）之間，這台主機這個問題有出現紀錄的日數。
    /// null＝不是觀察中的進行中成員，或這段期間這台主機沒有任何分析紀錄（無資料，不等於沒有再發生）。
    /// </summary>
    public int? ObservedRecurDays { get; set; }

    /// <summary>
    /// 先前判定不處理：同主機同問題的上一件未取消案件是處理人判定不處理（不處理、誤報、已知雜訊）、高嚴重度或重大，
    /// 且距結案已滿 30 天仍在發生才重新派工的這件。沒有為 null。判定在 <c>DismissalRelease.PriorDismissalOf</c>（與派工閘門共用同一個解除條件）。
    /// </summary>
    public PriorDismissalDto? PriorDismissal { get; set; }

    /// <summary>同主機同問題的第幾次（從 1 起算＝建立在本件之前的未取消案件數＋1，含其他交辦單的案件）；「全部」篩選下同主機的新舊案件對照用</summary>
    public int Occurrence { get; set; } = 1;
}

/// <summary>成員的「先前判定不處理」資訊（畫面：先前判定不處理（MM-DD，某人），已滿 30 天仍在發生）</summary>
public class PriorDismissalDto
{
    /// <summary>上一件的結案時間</summary>
    public DateTime ClosedAt { get; set; }

    /// <summary>判定人帳號；讀不到為 null</summary>
    public string? Account { get; set; }

    /// <summary>上一件的結案狀態：wont_fix／false_positive／known_noise</summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>上一件案件最後的回覆摘要</summary>
public class WorkOrderLastReplyDto
{
    /// <summary>回覆（結案）時間</summary>
    public DateTime Date { get; set; }

    /// <summary>案件結案時的狀態（<c>IssueHandlingStatuses</c> 代碼）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>回覆說明（案件上最近一次的說明）；沒有為 null</summary>
    public string? Note { get; set; }
}

public class WorkOrderDiskTrendEvidenceDto
{
    public DateTime RecordDate { get; set; }
    public string SensorId { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public class WorkOrderMemberPageDto
{
    public List<WorkOrderMemberDto> Items { get; set; } = new();
    public int Total { get; set; }

    /// <summary>因檢視者可見範圍而沒列出的成員數（沒過濾時為 0；帶主機名篩選時一律 0，避免探測不可見主機）</summary>
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

    /// <summary>全部待派問題中有「無法派出」主機的問題數（不分頁；儀表板待派工卡只呼叫一次試跑就拿得到）</summary>
    public int UnassignableTotal { get; set; }

    public int Page { get; set; }

    /// <summary>出現點超過上限：不做試跑，請縮小期間</summary>
    public bool TooLarge { get; set; }

    /// <summary>可派的「問題 × 主機」總數（單位：問題×主機；不分頁，各問題的建議處理人台數相加）；超過上限時為 0</summary>
    public int DispatchableIssueHosts { get; set; }

    /// <summary>有可派問題的主機名稱（去重、不分大小寫）。只供伺服器內部彙整（報表處理成效），不輸出到 API</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<string> DispatchableHostNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>超過上限時尚未派出的出現點數（以下界判定時為「至少」）；未超過時為 0</summary>
    public long PendingAtLeast { get; set; }

    /// <summary>試跑的出現點上限，讓超限訊息能寫出數量與上限</summary>
    public int MaxOccurrences { get; set; }

    public DateTime From { get; set; }
    public DateTime To { get; set; }

    /// <summary>第一段「可以派」：有建議處理人的問題數與（問題×主機）數；超過上限時為 0</summary>
    public GapSectionDto Dispatchable { get; set; } = new();

    /// <summary>第二段「派不出去」：沒有可接單人選（<c>no_pool／all_paused／no_visibility</c>）的問題數與（問題×主機）數</summary>
    public GapSectionDto UnassignableSection { get; set; } = new();

    /// <summary>第三段「不會派」：閘門排除（嚴重度未達、不再打擾、抑制、已知雜訊、靜音）的問題數與（問題×主機）數</summary>
    public GapSectionDto NotDispatched { get; set; } = new();

    /// <summary>第二段依原因分組（每組一次說明）；原因依序排序</summary>
    public List<GapUnassignableReasonDto> UnassignableReasons { get; set; } = new();

    /// <summary>
    /// 「沒有人看得到」（<c>no_visibility</c>）的主機所屬群組，每個群組只出現一次（不隨問題重複），
    /// 台數多的在前。沒有群組的主機不在內。
    /// </summary>
    public List<GapUnseenHostGroupDto> UnseenHostGroups { get; set; } = new();

    /// <summary>可授權的派工池使用者群組（啟用中且為派工池）；授權沿用 <c>PUT api/admin/access/{userGroupId}</c></summary>
    public List<GapPoolGroupDto> PoolGroups { get; set; } = new();

    /// <summary>目前使用者能否直接授權（具 Maintain）；否則畫面只給連到群組與授權的連結</summary>
    public bool CanGrantAccess { get; set; }
}

/// <summary>
/// 待派頁一段的統計。<see cref="Issues"/>＝該段有幾個問題（相異來源＋事件編號）；<see cref="Hosts"/>＝該段「問題×主機」數
/// （同一台主機在兩個問題上各算一次）。一個問題在不同主機上可落在不同段，各段各算一次；該段沒有任何項目時兩者皆為 0。
/// </summary>
public class GapSectionDto
{
    public int Issues { get; set; }
    public int Hosts { get; set; }
}

public class GapUnassignableReasonDto
{
    /// <summary>no_pool／all_paused／no_visibility</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>有這個原因的問題數</summary>
    public int Issues { get; set; }

    /// <summary>（問題×主機）數</summary>
    public int Hosts { get; set; }
}

public class GapUnseenHostGroupDto
{
    public long GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;

    /// <summary>此群組內沒有人看得到的相異主機數</summary>
    public int Hosts { get; set; }

    /// <summary>牽涉到此群組的問題數</summary>
    public int Issues { get; set; }
}

public class GapPoolGroupDto
{
    public long UserGroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
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

/// <summary>待派頁「不再自動派工」清單：被「不再打擾」閘門擋住的（主機, 問題），依問題分組、依問題組分頁</summary>
public class DismissedListDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }

    /// <summary>問題組總數（分頁依據）</summary>
    public int Total { get; set; }

    /// <summary>被擋住的主機總台數（去重）</summary>
    public int TotalHosts { get; set; }

    public List<DismissedGroupDto> Groups { get; set; } = new();
}

/// <summary>不再自動派工的輕量計數（與清單同一個查詢）</summary>
public class DismissedCountDto
{
    /// <summary>被擋住的問題數（相異的來源＋事件編號）；沒有任何被擋住時為 0</summary>
    public int Issues { get; set; }

    /// <summary>被擋住的主機台數（相異主機）；沒有任何被擋住時為 0</summary>
    public int Hosts { get; set; }
}

public class DismissedGroupDto
{
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string IssueLabel { get; set; } = string.Empty;
    public string? PlainExplanation { get; set; }

    /// <summary>此問題被擋住的台數</summary>
    public int Hosts { get; set; }

    public List<DismissedRowDto> Rows { get; set; } = new();
}

public class DismissedRowDto
{
    public string HostName { get; set; } = string.Empty;

    /// <summary>單筆恢復用：問題簽章鍵（不透明字串，原樣回送）</summary>
    public string IssueKey { get; set; } = string.Empty;

    /// <summary>先前結案狀態：wont_fix／false_positive／known_noise</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>結案的人（顯示名稱）；讀不到處理歷程時為 null</summary>
    public string? ClosedBy { get; set; }

    public DateTime? ClosedAt { get; set; }

    /// <summary>
    /// 距自動恢復派工還有幾天（高嚴重度或重大、處理人回覆產生、生效日期之後結案才有；第 30 天＝1）。
    /// null＝永久（中低嚴重度、管理者明確勾選之後不再自動派工、上線前判定），畫面顯示「永久」。
    /// </summary>
    public int? ReleaseInDays { get; set; }

    /// <summary>上線前判定：結案早於 30 天解除功能的生效日期，不自動解除，由管理者逐筆或整個問題恢復</summary>
    public bool PreLaunch { get; set; }
}

/// <summary>恢復單一（主機, 問題）</summary>
public class RestoreDismissedRequest
{
    [Required]
    public string HostName { get; set; } = string.Empty;

    [Required]
    public string IssueKey { get; set; } = string.Empty;
}

/// <summary>恢復整個問題</summary>
public class RestoreDismissedIssueRequest
{
    [Required]
    public string Source { get; set; } = string.Empty;

    public int EventId { get; set; }
}

public class DismissedRestoreResultDto
{
    /// <summary>恢復的主機（案件）數</summary>
    public int Restored { get; set; }
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
    /// <summary>no_pool／all_paused／no_visibility</summary>
    public string Reason { get; set; } = string.Empty;

    public int Hosts { get; set; }
}

public class AutoDispatchRequest
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>單題派工：問題來源；與 <see cref="EventId"/> 一起給或都不給（不帶＝全頁，現行行為）</summary>
    public string? Source { get; set; }

    /// <summary>單題派工：問題事件編號</summary>
    public int? EventId { get; set; }
}

/// <summary>
/// 立即派工預覽（<c>POST api/work-orders/auto-dispatch/preview</c>，不落盤）：與落盤共用同一個試跑與分組。
/// 單位見 <c>WorkOrderBoardService.PreviewAutoDispatch</c>；沒有任何可派項目時 <see cref="NothingToDispatch"/> 為 true、各數字為 0。
/// </summary>
public class AutoDispatchPreviewDto
{
    /// <summary>將建立的新單張數</summary>
    public int CreateOrders { get; set; }

    /// <summary>將併入既有單的張數（只掛進既有單、不新建）</summary>
    public int MergeOrders { get; set; }

    /// <summary>掛入的總台數（各人台數加總）</summary>
    public int Hosts { get; set; }

    /// <summary>每人將掛入的台數</summary>
    public List<AutoDispatchHandlerDto> PerHandler { get; set; } = new();

    /// <summary>範圍內派不出去的（原因 → 台數）</summary>
    public List<GapUnassignableDto> Unassigned { get; set; } = new();

    /// <summary>沒有任何可派的項目</summary>
    public bool NothingToDispatch { get; set; }

    /// <summary>給畫面顯示的訊息；有可派項目時為 null</summary>
    public string? Message { get; set; }
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

    /// <summary>
    /// 這次沒有任何新成員被派出（<see cref="AssignedMembers"/> 為 0）且沒有失敗的組：多半是別人剛派完。
    /// <see cref="MergedOrders"/> 只計真的併入新成員的單，所以這時三個數字都是 0。
    /// </summary>
    public bool NothingToDispatch { get; set; }

    /// <summary>給畫面顯示的訊息（<see cref="NothingToDispatch"/> 時為「沒有需要派的項目（可能剛由其他人派出）」，否則為 null）</summary>
    public string? Message { get; set; }
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

    /// <summary>
    /// 進行中台數（<see cref="ActiveMembers"/>）的三個互斥分法，三者相加恆等於 <see cref="ActiveMembers"/>：
    /// 待我處理（進行中、非上報中、非觀察期內；觀察到期算在這裡）、觀察期內（觀察中且觀察至 &gt;= 今天）、上報中。
    /// 口徑同 <see cref="ActiveMembers"/>（非暫停單、不含停用主機）；數的是成員（主機×問題）。
    /// </summary>
    public int TodoHosts { get; set; }

    /// <summary>觀察期內的台數（見 <see cref="TodoHosts"/>）</summary>
    public int ObservingHosts { get; set; }

    /// <summary>上報中的台數（見 <see cref="TodoHosts"/>）</summary>
    public int EscalatedHosts { get; set; }

    // ── 今天需要你處理（只有 HandlerSummary 端點填，側欄徽章不填、恆為 0）─────────────────────────
    // 口徑同 ActiveMembers：進行中交辦單（不含暫停單）底下的進行中成員、不含停用主機。
    // 「以主機計」的三個數字是各單的不重複主機數加總，等於清單各列同名欄位相加；同一台主機出現在兩張單算兩次。

    /// <summary>今天新增（以主機計）：成員建立日是今天；等於清單各列 <see cref="WorkOrderRowDto.NewTodayHosts"/> 相加</summary>
    public int NewTodayHosts { get; set; }

    /// <summary>復發（以主機計）：進行中成員是復發（<c>IssueRecurrence</c>）；等於各列 <see cref="WorkOrderRowDto.RecurrenceHosts"/> 相加</summary>
    public int RecurrenceHosts { get; set; }

    /// <summary>被退回（以成員計）：等於各列 <see cref="WorkOrderRowDto.ReturnedMembers"/> 相加</summary>
    public int ReturnedMembers { get; set; }

    /// <summary>觀察到期（以主機計）：觀察中且觀察至早於今天；等於各列 <see cref="WorkOrderRowDto.ObservationExpiredHosts"/> 相加</summary>
    public int ObservationExpiredHosts { get; set; }

    /// <summary>
    /// 3 天內到期（以成員計）：處理中成員的期限在今天～今天＋3 天（含兩端）；判定見 <c>WorkOrderQueries.IsDueSoon</c>。
    /// 已逾期的不算（逾期是 <see cref="OverdueMembers"/>）。
    /// </summary>
    public int DueSoonMembers { get; set; }

    /// <summary>這位處理人目前暫停接單（自動派工排除他）；處理人工作頁頁頭標記用，側欄徽章不填</summary>
    public bool HandlerPaused { get; set; }

    /// <summary>檢視者自己的可見主機數（處理人工作頁空狀態分流用；側欄徽章不填，恆為 0）</summary>
    public int VisibleHostCount { get; set; }

    /// <summary>處理人工作頁標頭（側欄徽章不填）</summary>
    public string DisplayName { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public bool Active { get; set; }
}

/// <summary>手動派工人選請求：該操作涉及的主機，以及（有的話）要判斷「最近處理過」的問題</summary>
public class WorkOrderCandidatesRequest
{
    public List<long> HostIds { get; set; } = new();
    public string? Source { get; set; }
    public int? EventId { get; set; }
}

/// <summary>手動派工人選：啟用且具 Handle 者，附上能否看到這些主機、是否暫停、近期是否處理過同問題與目前負載</summary>
public class WorkOrderCandidateDto
{
    public long UserId { get; set; }
    public string Account { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int VisibleHostCount { get; set; }
    public int HiddenHostCount { get; set; }
    public bool Paused { get; set; }
    public bool InPool { get; set; }
    public bool RecentlyResolvedSameIssue { get; set; }
    public int ActiveLoad { get; set; }

    /// <summary>使用者是否設定了 email（沒有就收不到交辦通知）</summary>
    public bool HasEmail { get; set; }
}
