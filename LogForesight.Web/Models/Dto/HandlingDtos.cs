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
    /// 由問題標記推導出的日狀態（docs/archive/HISTORY.md #6）——與 Status 不同：
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
    /// <summary>處理人帳號（§9：唯讀顯示時前端以 formatUserName 組「顯示名稱(帳號)」）</summary>
    public string? HandlerAccount { get; set; }

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
    /// 本日問題中屬進行中案件的數量（docs/archive/FEEDBACK-4-PLAN.md §2）：「N 項屬進行中案件」提示，
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

    /// <summary>本次指派改派掉的他人案件數（docs/archive/FEEDBACK-10-PLAN.md §9，只有帶 reassign 才會 &gt; 0）</summary>
    public int CasesReassigned { get; set; }

    /// <summary>
    /// true＝被指派的人沒有這台主機的一般檢視權（docs/archive/FEEDBACK-10-PLAN.md §7）。
    /// 指派**仍然成立**——對方以案件處理人身分取得該問題的範圍限定檢視權；
    /// 這個旗標是講給執行指派的人聽的，讓他知道對方看得到的只有被交辦的問題。
    /// </summary>
    public bool AssigneeHasNoHostAccess { get; set; }

    /// <summary>
    /// true＝被指派的人沒有 Handle 能力（回饋十三輪，體檢 H1 殘餘：
    /// IssueHandlingCommandService 的批次指派已有同款提示，日層級指派原本沒有）。
    /// **不擋、只提示**——與批次指派同一個決策：把工作知會給主管是合理用法，
    /// 只是執行指派的人該知道對方按下「回覆處理狀態」會被後端擋下來。
    /// </summary>
    public bool AssigneeCannotHandle { get; set; }
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

    /// <summary>
    /// 確認要改派已由他人案件處理中的問題（docs/archive/FEEDBACK-10-PLAN.md §9）。
    /// false（預設）時，若當日有問題已被別人的進行中案件涵蓋，後端**不動它們**並回 conflict，
    /// 由前端問過使用者「要不要改派」再帶 true 重送——不確認就靜默搶走別人手上的工作，
    /// 是原本這個機制刻意避免的事。
    /// </summary>
    public bool Reassign { get; set; }
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
    /// 0＝這批問題目前都沒有進行中案件（docs/archive/FEEDBACK-4-PLAN.md §2）</summary>
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
    /// 0＝目前沒有進行中案件（docs/archive/FEEDBACK-4-PLAN.md §2）</summary>
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

    /// <summary>問題層級操作才有值（「Source EventId」）——docs/archive/HISTORY.md #6</summary>
    public string? IssueLabel { get; set; }

    public string ActorAccount { get; set; } = string.Empty;

    /// <summary>顯示格式統一「顯示名稱(帳號)」的素材（docs/archive/FEEDBACK-8-PLAN.md #6）；
    /// 查無對應使用者（帳號已刪除等）時為 null，前端退回只顯示帳號</summary>
    public string? ActorDisplayName { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class HandlingTodoDto
{
    public int OpenCount { get; set; }
    public int InProgressCount { get; set; }
    public int OverdueCount { get; set; }

    /// <summary>
    /// 已處理的風險日數——docs/archive/HISTORY.md #6、docs/archive/HISTORY.md #12
    /// 報表「處理進度」用。對外三態（<see cref="HandlingStatuses.ExternalOf"/>）：
    /// 日層級 fallback 若為 wont_fix／false_positive／known_noise 也算已處理——這三態是
    /// 「這天已有結論」，對外部檢視（report/dashboard 只看還有沒有事要做）而言等同已處理，
    /// 不再像舊版那樣落在 Open/InProgress/Resolved 三個桶都數不到、讓「未完成」把已結案的
    /// 日子誤算進去。**與問題層級（詳情頁）「已處理」計數是不同的哲學**：詳情頁的三段計數器
    /// （見 record-detail.js renderProgress）刻意把 wont_fix/誤報/已知雜訊排除在「已處理」外，
    /// 那是「這件事的處理方式」的細節，只在單筆問題層級才有意義。</summary>
    public int ResolvedCount { get; set; }

    /// <summary>母體：期間內高＋中風險日總數（分母）——docs/archive/HISTORY.md #6 報表「處理進度」用</summary>
    public int TotalCount { get; set; }
}

/// <summary>
/// 待辦的問題口徑（回饋十九輪批次D2）：儀表板 KPI 卡的主要數字，答的是「有幾個不同的問題
/// 要處理」而不是「有幾個主機日還沒結案」——後者仍看 <see cref="HandlingTodoDto"/>
/// （供「未處理風險日 M」副標與報表「處理進度」圖表用，兩者角色不同、不互相取代）。
/// </summary>
public class IssueTodoDto
{
    /// <summary>未處理問題數（依 (Source, EventId) 去重）——KPI 卡的大數字</summary>
    public int OpenIssueCount { get; set; }

    public int InProgressIssueCount { get; set; }

    /// <summary>處理中但預計完成日已過的問題數，與批次H 郵件的逾期區塊同一個口徑</summary>
    public int OverdueIssueCount { get; set; }

    /// <summary>受未處理問題影響的相異主機數——KPI 卡副標「影響 N 台」</summary>
    public int AffectedHostCount { get; set; }
}

// ── 問題案件跨主機批次指派（docs/archive/FEEDBACK-4-PLAN.md §4）────────────────────────

/// <summary>批次指派 modal 開啟時的受影響主機預覽——已有進行中案件的主機標出既有處理人，
/// 讓使用者在送出前就知道哪些主機會被跳過（2.1，不搶走）</summary>
public class IssueCasePreviewHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;

    /// <summary>null＝目前沒有進行中案件，可以指派</summary>
    public string? ExistingHandlerName { get; set; }

    /// <summary>既有處理人的帳號與 Id（docs/archive/FEEDBACK-10-PLAN.md §6／§9）：
    /// 顯示要「顯示名稱(帳號)」，勾「改派」時前端也要判斷是不是換了人</summary>
    public string? ExistingHandlerAccount { get; set; }
    public long? ExistingHandlerId { get; set; }
}

/// <summary>
/// 批次指派 modal 的受影響主機預覽（體檢 M10）。與統一標記同一個理由改成有上限的結構：
/// DCOM 10016 這種問題在 6000 台環境幾乎每台都有，直接回全部會把 modal 塞爆。
/// </summary>
public class IssueCaseAssignPreviewDto
{
    /// <summary>本次顯示的主機（可能被截斷）</summary>
    public List<IssueCasePreviewHostDto> Hosts { get; set; } = new();

    /// <summary>受影響主機總數（截斷前）</summary>
    public int TotalHostCount { get; set; }

    /// <summary>true＝ <see cref="Hosts"/> 少於 <see cref="TotalHostCount"/>，畫面必須說明</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// 單台主機的指派對象（docs/archive/FEEDBACK-10-PLAN.md §12）：群組分攤時每台主機各有處理人，
/// 單人指派時全部都是同一個人。API 因此只有一種形狀，不必為「指派一個人」與
/// 「指派一群人」各開一支端點。
/// </summary>
public class IssueCaseAssignmentDto
{
    public long HostId { get; set; }
    public long HandlerId { get; set; }
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

    /// <summary>單人指派的處理人。<see cref="Assignments"/> 有值時忽略此欄位</summary>
    public long HandlerId { get; set; }

    /// <summary>
    /// 逐台指派明細（§12 群組分攤）。空＝全部指派給 <see cref="HandlerId"/>。
    /// 只有列在 <see cref="HostIds"/> 裡的主機會被套用——預覽表可以逐列改人，
    /// 但不能靠這個欄位偷偷加進沒勾選的主機。
    /// </summary>
    public List<IssueCaseAssignmentDto> Assignments { get; set; } = new();

    /// <summary>
    /// 明確要改派（換掉既有處理人）的主機（docs/archive/FEEDBACK-10-PLAN.md §9）。
    /// 不在此清單、卻已有他人進行中案件的主機維持既有語意——**保留原處理人、列入略過清單**，
    /// 不會被靜默搶走。預設空＝完全維持改版前的行為。
    /// </summary>
    public List<long> ReassignHostIds { get; set; } = new();

    [StringLength(1000, ErrorMessage = "說明長度不可超過 1000 字元")]
    public string? Note { get; set; }

    public DateTime? DueDate { get; set; }

    /// <summary>受影響主機認定的日期範圍（Q6：範圍認定與回溯深度是兩件事——
    /// 建案後的回溯關聯仍走全部留存歷史，這裡只決定「查哪些主機」）</summary>
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

/// <summary>
/// 跨主機一次回覆同一個問題的處理狀態（docs/archive/FEEDBACK-10-PLAN.md §11）。
/// 對象是**目前使用者名下**、這個問題的全部進行中案件——同一個硬體問題被指派到十台主機時，
/// 處理人不必進十次詳情頁標十次一樣的狀態。
/// </summary>
public class BulkIssueStatusRequest
{
    [Required]
    public string Source { get; set; } = string.Empty;

    public int EventId { get; set; }

    /// <summary>值域同問題層級狀態（含 observing）；空字串＝清除標記（調回未處理）</summary>
    public string Status { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "處理說明長度不可超過 1000 字元")]
    public string? Note { get; set; }

    /// <summary>處理中的預計完成日／觀察中的觀察至日期，其餘狀態忽略</summary>
    public DateTime? DueDate { get; set; }
}

/// <summary>
/// 統一標記（docs/archive/FEEDBACK-11-PLAN.md §6）：admin 直接把一個問題在**尚未有人接手**的主機上
/// 標成結論。與 <see cref="BulkIssueStatusRequest"/>（處理人回覆自己名下的案件）刻意分開：
/// 對象不同（無案件的主機 vs 自己的案件）、值域不同（只收結案四態）、原因必填。
/// </summary>
public class BulkCloseIssueRequest
{
    [Required]
    public string Source { get; set; } = string.Empty;

    public int EventId { get; set; }

    /// <summary>期間（依問題視角目前的篩選區間）。null＝不設邊界，但前端一律帶值</summary>
    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    /// <summary>只收結案四態（resolved／wont_fix／false_positive／known_noise）</summary>
    [Required]
    public string Status { get; set; } = string.Empty;

    /// <summary>原因**必填**——這是代全體下結論的操作，理由是紀錄的一部分</summary>
    [Required(ErrorMessage = "請填寫原因")]
    [StringLength(1000, ErrorMessage = "原因長度不可超過 1000 字元")]
    public string Note { get; set; } = string.Empty;

    /// <summary>
    /// 之後新出現的主機日是否自動套用這個結論（回饋十九輪批次F，§2 決策一）：勾選時同時把
    /// (Source,EventId) 的問題檔案設成機房結論，見 IssueOwnerAdminService.SetConclusion——
    /// 這一次的統一標記只處理**既有**日子，AutoApply 才是「以後也這樣」的開關。
    /// </summary>
    public bool AutoApply { get; set; }
}

/// <summary>統一標記的預覽／結果共用的逐主機列</summary>
public class IssueBulkCloseHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;

    /// <summary>將被（或已被）標記的日數</summary>
    public int DayCount { get; set; }

    /// <summary>其中原本標著「處理中／觀察中」、會被本次結論覆蓋的日數（定案 6-3）</summary>
    public int OverwriteDayCount { get; set; }

    /// <summary>已有結論、本次不動的日數</summary>
    public int AlreadyClosedDayCount { get; set; }

    /// <summary>非 null＝這台主機整台略過，值是可直接顯示的原因</summary>
    public string? SkipReason { get; set; }
}

public class IssueBulkClosePreviewDto
{
    /// <summary>期間（回顯給 modal，讓操作者看得到「本次只處理這段」）</summary>
    public string? From { get; set; }
    public string? To { get; set; }

    /// <summary>本次顯示的主機（可能被 <see cref="Truncated"/> 截斷）</summary>
    public List<IssueBulkCloseHostDto> Hosts { get; set; } = new();

    /// <summary>
    /// 受影響主機總數（截斷前，體檢 M10）。**統一標記尤其需要這個數字**：
    /// 它按下去是跨主機跨日的寫入，使用者需要的是「我到底影響了多少」的正確感知，
    /// 而不是一份捲不完、還可能把瀏覽器塞爆的清單。
    /// </summary>
    public int TotalHostCount { get; set; }

    /// <summary>將被標記的總日數（截斷前）——比主機數更能反映實際寫入規模</summary>
    public int TotalDayCount { get; set; }

    /// <summary>true＝<see cref="Hosts"/> 少於 <see cref="TotalHostCount"/>，畫面必須說明</summary>
    public bool Truncated { get; set; }

    /// <summary>整台被略過的主機數（截斷前）</summary>
    public int SkippedHostCount { get; set; }
}

public class BulkCloseIssueResultDto
{
    /// <summary>實際被標記的主機數</summary>
    public int UpdatedHostCount { get; set; }

    /// <summary>實際被標記的主機日數</summary>
    public int UpdatedDayCount { get; set; }

    /// <summary>整台略過的主機（含原因；同預覽，逐台清單受上限截斷）</summary>
    public List<IssueBulkCloseHostDto> Skipped { get; set; } = new();

    /// <summary>略過的主機總數（截斷前）</summary>
    public int SkippedHostCount { get; set; }

    /// <summary>
    /// 這次操作的稽核追溯連結參數（體檢 X6）：批次寫入沒有復原機制，
    /// 但**至少要查得到影響了哪些**。前端據此組成「檢視這次影響的清單」連結。
    /// </summary>
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
}

public class BulkIssueStatusResultDto
{
    /// <summary>實際套用的案件數（＝主機數，同主機同問題只有一個進行中案件）</summary>
    public int UpdatedCaseCount { get; set; }

    /// <summary>連同案件涵蓋的其他日子一起更新的天數合計（含觸發日）</summary>
    public int UpdatedDayCount { get; set; }

    /// <summary>套用到的主機名稱（前端回報用）</summary>
    public List<string> HostNames { get; set; } = new();
}

public class BulkAssignSkippedDto
{
    public string HostName { get; set; } = string.Empty;
    public string ExistingHandlerName { get; set; } = string.Empty;
}

/// <summary>
/// 群組指派的候選處理人（docs/archive/FEEDBACK-10-PLAN.md §12）：群組內啟用中的成員，
/// 帶上目前手上的進行中案件數——「依現有負載分攤」需要這個數字，
/// 畫面上也直接顯示，讓 admin 看得出為什麼某個人被分到比較少台。
/// </summary>
public class HandlerCandidateDto
{
    public long UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;

    /// <summary>目前名下的進行中案件數（跨主機、跨問題）</summary>
    public int OpenCaseCount { get; set; }
}

/// <summary>改派結果的一列（§9）：誰換成誰，供前端組回報訊息</summary>
public class BulkAssignReassignedDto
{
    public string HostName { get; set; } = string.Empty;
    public string PreviousHandlerName { get; set; } = string.Empty;
}

public class BulkAssignIssueCaseResultDto
{
    public int Created { get; set; }
    public List<BulkAssignSkippedDto> Skipped { get; set; } = new();

    /// <summary>實際改派掉的主機（§9）</summary>
    public List<BulkAssignReassignedDto> Reassigned { get; set; } = new();

    /// <summary>
    /// 被指派者看不到的主機（docs/archive/FEEDBACK-10-PLAN.md §7）：指派本身仍然成立
    /// （對方會以案件處理人的身分取得該問題的範圍限定檢視權），但**執行指派的人要知道**
    /// 對方不是這台主機的一般授權對象，看到的只有被指派的那個問題。
    /// </summary>
    public List<AssigneeNoAccessDto> AssigneeNoAccess { get; set; } = new();

    /// <summary>
    /// 被指派者**沒有處理能力**（體檢 H1）：指派前的檢查過去只做了「他看得到這台主機嗎」，
    /// 沒做「他動得了嗎」。交辦出去的工作進了對方清單、對方做不了任何事，指派的人也不知情。
    /// </summary>
    public List<AssigneeCannotHandleDto> AssigneeCannotHandle { get; set; } = new();
}

/// <summary>「這位處理人看不到這台主機」的提示素材（§7）</summary>
public class AssigneeNoAccessDto
{
    public string HostName { get; set; } = string.Empty;
    public string HandlerName { get; set; } = string.Empty;
}

/// <summary>
/// 被指派者**沒有處理能力**（體檢 H1）。
///
/// 與 <see cref="AssigneeNoAccessDto"/> 是兩件不同的事，刻意分開回報：
///   - NoAccess＝「他看不到這台主機」——指派仍成立，他會取得案件範圍的檢視權；
///   - CannotHandle＝「他動不了」——工作進了對方清單、對方**做不了任何事**，
///     而指派的人完全不知情。
///
/// **不擋**：把工作知會給主管是合理用法。問題不在能不能指派，
/// 而在於指派的人不知道對方動不了——所以要講。
/// 一個處理人只回報一次（不逐台重複），清單會很長時對決策沒有幫助。
/// </summary>
public class AssigneeCannotHandleDto
{
    public string HandlerName { get; set; } = string.Empty;

    /// <summary>指派給他的主機數（讓操作者判斷影響多大）</summary>
    public int HostCount { get; set; }
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
    /// <summary>確認者顯示名稱（§9：前端以 formatUserName 組「顯示名稱(帳號)」）</summary>
    public string? ConfirmedByDisplayName { get; set; }
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

    /// <summary>顯示格式統一「顯示名稱(帳號)」的素材（docs/archive/FEEDBACK-8-PLAN.md #6）；
    /// 查無對應使用者（登入失敗打錯帳號、外部帳號等）時為 null，前端退回只顯示帳號</summary>
    public string? AccountDisplayName { get; set; }

    public string Action { get; set; } = string.Empty;
    public string ActionText { get; set; } = string.Empty;
    public string? TargetKind { get; set; }
    public string? TargetId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? DetailJson { get; set; }
    public string? IpAddress { get; set; }
    public string Result { get; set; } = string.Empty;
}

// ── 處理人員工作頁（docs/archive/FEEDBACK-4-PLAN.md §6）─────────────────────────────

public class HandlerWorkloadDto
{
    public long UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>顯示格式統一「顯示名稱(帳號)」的素材（docs/archive/FEEDBACK-8-PLAN.md #6）——
    /// 格式化本身是前端的事，這裡只補齊素材</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>被查看的使用者已停用時仍顯示交辦紀錄（歷史事實不因停用消失），前端後綴「（已停用）」</summary>
    public bool Active { get; set; }

    public int OpenCaseCount { get; set; }
    public int UnresolvedDayCount { get; set; }
    public int OverdueCount { get; set; }

    /// <summary>名下進行中案件（資料以檢視者的可見範圍過濾）</summary>
    public List<HandlerCaseItemDto> Cases { get; set; } = new();

    /// <summary>名下被指派的風險日——預設只列推導後未結案，includeResolvedDays=true 時另含近 30 天已結案</summary>
    public List<HandlerDayItemDto> Days { get; set; } = new();
}

public class HandlerCaseItemDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string IssueLabel { get; set; } = string.Empty;

    /// <summary>
    /// 問題簽章的來源與 Event ID（docs/archive/FEEDBACK-11-PLAN.md §7）：工作頁「依問題」視角用這兩個
    /// 欄位分組，與問題查詢 by-issue 視角同一把分組鍵；也是「回覆處理狀態」端點的參數。
    /// 由 <c>IssueCase.IssueKey</c> 解析而來（見 <c>IssueSignatureKey.TryParse</c>），非新儲存欄位。
    /// </summary>
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }

    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string? DueDate { get; set; }
    public bool IsOverdue { get; set; }
    public string FirstLinkedDate { get; set; } = string.Empty;
    public string LastLinkedDate { get; set; } = string.Empty;
}

public class HandlerDayItemDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;

    /// <summary>由問題標記推導出的日狀態（同詳情頁 DerivedStatus）——日層級快照指派後恆為
    /// in_progress，必須看推導值才知道「現在真正的狀態」</summary>
    public string DerivedStatus { get; set; } = string.Empty;
    public string DerivedStatusText { get; set; } = string.Empty;
    public string? DueDate { get; set; }
    public bool IsOverdue { get; set; }
}
