namespace LogForesight.Core.Models;

/// <summary>
/// 交辦單（↔ lf_work_orders）：一張單＝一個問題 × 一組主機 × 一位處理人。
/// 既有的 <see cref="IssueCase"/>（一台主機 × 一個問題）是交辦單的**成員**，以
/// <see cref="IssueCase.WorkOrderId"/> 指回單。
///
/// **交辦單不存自己的主狀態**：進行中與否由成員案件推導；<see cref="ClosedAt"/> 只是快取，
/// 讓「進行中的單」可以走索引查，而不必每次都彙總成員。
/// </summary>
public class WorkOrder
{
    public long WorkOrderId { get; set; }

    /// <summary>問題來源原字（顯示用）；null＝多問題單</summary>
    public string? SourceName { get; set; }

    /// <summary>問題事件 ID；null＝多問題單</summary>
    public int? EventId { get; set; }

    /// <summary>「Source EventId」反正規化存下來（同 <see cref="IssueCase.IssueLabel"/> 的理由）</summary>
    public string IssueLabel { get; set; } = string.Empty;

    public long HandlerId { get; set; }

    /// <summary>最近一次被改派走的處理人，可唯讀開啟此單；再次改派時覆寫（只記最近一位）</summary>
    public long? PreviousHandlerId { get; set; }

    /// <summary><see cref="WorkOrderOrigins"/> 值域</summary>
    public string Origin { get; set; } = WorkOrderOrigins.Manual;

    /// <summary><see cref="WorkOrderScopes"/> 值域</summary>
    public string ScopeKind { get; set; } = WorkOrderScopes.Hosts;

    /// <summary><see cref="ScopeKind"/>＝Groups 時的主機群組 id；其餘為空清單</summary>
    public List<long> ScopeGroupIds { get; set; } = new();

    /// <summary>續掛旗標：同問題的新主機是否自動掛進這張單</summary>
    public bool AutoAttach { get; set; }

    public string? Note { get; set; }

    public DateTime? DueDate { get; set; }

    /// <summary>建立者；系統建立為 null</summary>
    public long? CreatedById { get; set; }

    public string CreatedByAccount { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastAppendedAt { get; set; }

    public DateTime? LastReplyAt { get; set; }

    /// <summary>具 Assign 的使用者最近開啟此單詳情的時間（未讀回覆判定用）；null＝從未開啟</summary>
    public DateTime? AdminSeenAt { get; set; }

    /// <summary>
    /// 續掛已停止的原因（<see cref="ContinuationStopReasons"/> 短代碼）：夜間派工要把新主機續掛進這張單，
    /// 卻因處理人不符資格而被擋時寫入；null＝沒有被擋。由夜間派工以單句 UPDATE 寫入（<c>IWorkOrderStore.SetContinuationStops</c>），
    /// 整列 <c>Save</c> 不會寫回，也不動併發權杖。
    /// </summary>
    public string? ContinuationStoppedReason { get; set; }

    /// <summary>最近一次續掛被擋的夜間派工時間；<see cref="ContinuationStoppedReason"/> 為 null 時一律為 null</summary>
    public DateTime? ContinuationStoppedAt { get; set; }

    /// <summary>
    /// 未讀回覆（管理者尚未看過處理人的最新回覆）的唯一定義：進行中、有回覆，且最近回覆晚於「最近開啟時間（沒開過則取建立時間）」。
    /// SQL 端的同義條件在 <c>EfWorkOrderStore.UnreadReplyPredicate</c>，兩處互相對應、改一處必改另一處。
    /// </summary>
    public bool HasUnreadReply => ClosedAt == null && LastReplyAt.HasValue && LastReplyAt > (AdminSeenAt ?? CreatedAt);

    /// <summary>結案時間快取；null＝進行中</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary><see cref="WorkOrderCloseReasons"/> 值域</summary>
    public string? ClosedReason { get; set; }

    /// <summary>併發權杖</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>交辦單的異動事件（↔ lf_work_order_events，append-only）</summary>
public class WorkOrderEvent
{
    public long EventId { get; set; }

    public long WorkOrderId { get; set; }

    /// <summary><see cref="WorkOrderEventActions"/> 值域</summary>
    public string Action { get; set; } = string.Empty;

    public long? ActorId { get; set; }

    public string ActorAccount { get; set; } = string.Empty;

    /// <summary>本事件造成的成員數變化（可為負）</summary>
    public int MemberDelta { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>交辦單的建立來源</summary>
public static class WorkOrderOrigins
{
    public const string Manual = "manual";
    public const string OwnerRule = "owner_rule";
    public const string AutoDispatch = "auto_dispatch";
    public const string Backfill = "backfill";
    public const string DayAssign = "day_assign";
}

/// <summary>續掛被擋的原因代碼與顯示名稱（唯一一份對照）</summary>
public static class ContinuationStopReasons
{
    /// <summary>處理人已停用，或已不具處理能力（不在候選人快照內）</summary>
    public const string HandlerInactive = "handler_inactive";

    /// <summary>處理人暫停接單</summary>
    public const string HandlerPaused = "handler_paused";

    /// <summary>處理人看不到這台新主機</summary>
    public const string HostNotVisible = "host_not_visible";

    public static string? LabelOf(string? code) => code switch
    {
        HandlerInactive => "處理人已停用或失去處理能力",
        HandlerPaused => "處理人暫停接單",
        HostNotVisible => "處理人看不到新主機",
        _ => null
    };
}

/// <summary>交辦單的主機範圍種類</summary>
public static class WorkOrderScopes
{
    public const string All = "All";
    public const string Groups = "Groups";
    public const string Hosts = "Hosts";
}

/// <summary>交辦單的結案原因</summary>
public static class WorkOrderCloseReasons
{
    public const string AllClosed = "all_closed";
    public const string Moved = "moved";
    public const string Cancelled = "cancelled";
    public const string AdminClosed = "admin_closed";

    /// <summary>夜間派工收尾：本趟新建的單最後沒有任何成員（成員全因決策後已有進行中案件而略過）</summary>
    public const string EmptyDispatch = "empty_dispatch";
}

/// <summary>交辦單事件動作</summary>
public static class WorkOrderEventActions
{
    public const string Created = "created";
    public const string Appended = "appended";
    public const string MergedIn = "merged_in";
    public const string Reassigned = "reassigned";
    public const string SplitOut = "split_out";
    public const string SplitIn = "split_in";
    public const string Cancelled = "cancelled";
    public const string AdminClosed = "admin_closed";
    public const string Closed = "closed";
    public const string Replied = "replied";
    public const string DueDateChanged = "due_date_changed";

    /// <summary>系統事件：本趟有先前判定不處理、已滿 30 天（<see cref="DismissalRelease.Days"/>）而放行重新派工的主機（成員數記在 MemberDelta）</summary>
    public const string DismissalReleased = "dismissal_released";

    /// <summary>管理者把「無法處理」的成員退回給處理人（escalated → in_progress）</summary>
    public const string Returned = "returned";
}

/// <summary>單張交辦單的成員案件計數</summary>
public sealed class WorkOrderMemberCounts
{
    public int Total { get; init; }

    /// <summary>
    /// 進行中（closed_at IS NULL）。呼叫端傳入停用主機集合時，停用主機上的進行中成員不計入這裡，
    /// 也不計入下面的各狀態與逾期數，改計於 <see cref="ActiveOnDisabledHosts"/>。
    /// </summary>
    public int Active { get; init; }

    /// <summary>進行中、但主機已停用的成員數（呼叫端沒有傳停用主機集合時恆為 0）</summary>
    public int ActiveOnDisabledHosts { get; init; }

    public int Closed { get; init; }

    /// <summary>進行中且狀態為 escalated</summary>
    public int Escalated { get; init; }

    /// <summary>進行中且狀態為 in_progress</summary>
    public int InProgress { get; init; }

    /// <summary>進行中且狀態為 observing</summary>
    public int Observing { get; init; }

    /// <summary>進行中且狀態為 open</summary>
    public int Open { get; init; }

    /// <summary>進行中、期限早於今天、狀態為 in_progress 或 observing（<see cref="WorkOrderQueries.IsOverdue"/>）</summary>
    public int Overdue { get; init; }

    /// <summary>待背景逐日同步（day_sync_pending = 1，不分進行中與否）</summary>
    public int DaySyncPending { get; init; }
}

/// <summary>交辦單清單查詢條件（IWorkOrderStore.QueryOrders）</summary>
public sealed class WorkOrderQuery
{
    /// <summary>null＝不限處理人；空集合＝查無</summary>
    public IReadOnlyCollection<long>? HandlerIds { get; init; }

    /// <summary>null＝不限；不分大小寫</summary>
    public string? Source { get; init; }

    public int? EventId { get; init; }

    /// <summary><see cref="WorkOrderQueries.OrderStatuses"/> 值域</summary>
    public string Status { get; init; } = WorkOrderQueries.StatusActive;

    /// <summary><see cref="WorkOrderQueries.Sorts"/> 值域</summary>
    public string Sort { get; init; } = WorkOrderQueries.SortCreatedDesc;

    /// <summary>1 起</summary>
    public int Page { get; init; } = 1;

    /// <summary>1～100</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>
    /// 目前靜音中問題的組合鍵（<c>IssueExclusion.CompositeKey</c>）：進行中、問題欄非 null 且鍵在集合內的單＝暫停。
    /// null＝不判定（<see cref="PausedMode"/> 不生效）。
    /// </summary>
    public IReadOnlyCollection<string>? PausedKeys { get; init; }

    /// <summary><see cref="WorkOrderQueries.PausedModes"/> 值域：include 不篩、exclude 排除暫停單、only 只列暫停單</summary>
    public string PausedMode { get; init; } = WorkOrderQueries.PausedInclude;

    /// <summary>非 null 時只列「進行中、問題欄非 null 且組合鍵在集合內」的單；空集合＝查無</summary>
    public IReadOnlyCollection<string>? OnlyKeys { get; init; }

    /// <summary>true＝只列有未讀回覆的單（判定同 <see cref="WorkOrder.HasUnreadReply"/>）</summary>
    public bool UnreadOnly { get; init; }

    /// <summary>
    /// 已停用主機的 host_name_key（大寫）集合：「有上報成員」「有逾期成員」兩種狀態條件與「成員數」排序都不計入這些主機上的成員，
    /// 與清單列上的成員數字同一口徑。null＝不排除。
    /// </summary>
    public IReadOnlyCollection<string>? ExcludedHostKeys { get; init; }
}

/// <summary>交辦單清單查詢結果</summary>
public sealed class WorkOrderPage
{
    public List<WorkOrder> Items { get; init; } = new();

    public int Total { get; init; }
}

/// <summary>交辦單成員查詢條件（IIssueCaseStore.QueryMembers）</summary>
public sealed class WorkOrderMemberQuery
{
    public long WorkOrderId { get; init; }

    /// <summary><see cref="WorkOrderQueries.MemberStatuses"/> 值域</summary>
    public string Status { get; init; } = WorkOrderQueries.StatusAll;

    /// <summary>null＝不限；其餘以 host_name_key（大寫）比對</summary>
    public IReadOnlyCollection<string>? HostNameKeys { get; init; }

    /// <summary>1 起</summary>
    public int Page { get; init; } = 1;

    /// <summary>1～200</summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>交辦單查詢的值域與共用判準（EF 與替身共用同一份定義）</summary>
/// <summary>
/// 交辦單與案件的 <c>source_key</c> 正規化規則（唯一一份）：截到欄位上限再轉大寫。
/// 放在模型層而不是 SQL 實作裡，是因為讀取端（依問題視角的已交辦計數）也要用同一把鑰匙比對，
/// 不該為此相依到某個特定後端的類別。
/// </summary>
public static class WorkOrderIssueKey
{
    /// <summary>與 <c>lf_work_orders.source_key</c>／<c>lf_issue_cases.source_key</c> 欄位長度一致</summary>
    public const int SourceMaxLength = 255;

    public static string SourceKeyOf(string source) =>
        (source.Length <= SourceMaxLength ? source : source[..SourceMaxLength]).ToUpperInvariant();
}

public static class WorkOrderQueries
{
    public const string StatusActive = "active";
    public const string StatusEscalated = "escalated";
    public const string StatusOverdue = "overdue";
    public const string StatusUnreplied = "unreplied";
    public const string StatusClosed = "closed";
    public const string StatusAll = "all";

    /// <summary>
    /// 待我處理（只用在交辦單清單）：進行中且至少有一個「待處理成員」的單——待處理成員＝進行中、不在停用主機上、
    /// 不是上報中、也不在觀察期內（觀察至日期 &gt;= 今天）的成員；只剩觀察期內或上報中成員的單不列。
    /// 觀察到期（觀察至日期 &lt; 今天）的成員算待處理。判準與 <see cref="IsTodoMember"/> 同義。
    /// </summary>
    public const string StatusTodo = "todo";

    public const string SortCreatedDesc = "created_desc";
    public const string SortMembersDesc = "members_desc";
    public const string SortUnrepliedOldest = "unreplied_oldest";

    public static readonly string[] OrderStatuses =
        { StatusActive, StatusEscalated, StatusOverdue, StatusUnreplied, StatusClosed, StatusAll, StatusTodo };

    public static readonly string[] MemberStatuses =
        { StatusActive, StatusClosed, StatusEscalated, StatusOverdue, StatusAll };

    /// <summary>
    /// 處理優先序（P-1，H7）：有被退回的 → 有逾期的（<see cref="IsOverdue"/>，含觀察到期）→ 嚴重度高到低 →
    /// 期限近到遠（無期限排最後）→ 建立時間新到舊（同現行預設）→ 單號降冪。
    /// </summary>
    public const string SortPriority = "priority";

    /// <summary>期限最近：進行中成員的最早期限（無期限排最後），同值依建立時間新到舊、單號降冪</summary>
    public const string SortDueSoon = "dueSoon";

    /// <summary>嚴重度：高到低（無法推導排最後），同值依建立時間新到舊、單號降冪</summary>
    public const string SortSeverity = "severity";

    /// <summary>資料庫端可直接排序的三個鍵（store 的 <c>QueryOrders</c> 只認這些）</summary>
    public static readonly string[] Sorts = { SortCreatedDesc, SortMembersDesc, SortUnrepliedOldest };

    /// <summary>
    /// 要讀取時推導（退回、逾期、嚴重度、最早期限）才排得出來的鍵：由 Web 服務對「符合篩選的全體」在記憶體排序後再取頁，
    /// 不交給 store 的 <c>QueryOrders</c>（它只認 <see cref="Sorts"/>）。
    /// </summary>
    public static readonly string[] DerivedSorts = { SortPriority, SortDueSoon, SortSeverity };

    /// <summary>清單端點接受的全部排序鍵：<see cref="Sorts"/>＋<see cref="DerivedSorts"/></summary>
    public static readonly string[] AllSorts = Sorts.Concat(DerivedSorts).ToArray();

    // ── 清單標記篩選（我的交辦「今天需要你處理」；與狀態篩選取交集，含 todo）──────────────────

    /// <summary>被退回：管理者退回後處理人尚未回覆的單（判定見 Web 服務 <c>ReturnedMembers</c>）</summary>
    public const string FlagReturned = "returned";

    /// <summary>今天新增：有成員建立日是今天的單</summary>
    public const string FlagNewToday = "newToday";

    /// <summary>復發：有進行中成員是復發（<c>IssueRecurrence</c>）的單</summary>
    public const string FlagRecurrence = "recurrence";

    /// <summary>觀察到期：有觀察中且觀察至早於今天的進行中成員的單</summary>
    public const string FlagObservationExpired = "observationExpired";

    /// <summary>3 天內到期：有處理中成員期限落在今天～今天＋<see cref="DueSoonDays"/> 的單</summary>
    public const string FlagDueSoon = "dueSoon";

    public static readonly string[] Flags = { FlagReturned, FlagNewToday, FlagRecurrence, FlagObservationExpired, FlagDueSoon };

    /// <summary>「即將到期」的天數：期限落在 [今天, 今天＋3]（含兩端）</summary>
    public const int DueSoonDays = 3;

    /// <summary>
    /// 即將到期的成員（唯一一份判定）：進行中、處理中（預計完成日才有意義；觀察中的日期是「觀察至」，另有觀察到期標記），
    /// 期限落在今天到今天＋<see cref="DueSoonDays"/> 天之間（含兩端）。已逾期（期限早於今天）不算——逾期有自己的標記。
    /// </summary>
    public static bool IsDueSoon(IssueCase c, DateTime today) =>
        c.ClosedAt == null && c.Status == IssueHandlingStatuses.InProgress && c.DueDate != null
        && c.DueDate.Value.Date >= today.Date && c.DueDate.Value.Date <= today.Date.AddDays(DueSoonDays);

    public const string PausedInclude = "include";
    public const string PausedExclude = "exclude";
    public const string PausedOnly = "only";

    public static readonly string[] PausedModes = { PausedInclude, PausedExclude, PausedOnly };

    public const int MaxOrderPageSize = 100;
    public const int MaxMemberPageSize = 200;

    /// <summary>
    /// 「已停用主機」的 host_name_key 集合（唯一一份）：徽章、處理人摘要、負載看板、交辦總覽與派工負載的台數都用它排除成員。
    /// 同名鍵若還有啟用中的主機就不算停用。
    /// </summary>
    public static IReadOnlyCollection<string> DisabledHostKeys(IEnumerable<WebHost> hosts)
    {
        var all = hosts.ToList();
        var active = all.Where(h => h.Active).Select(h => LogForesight.Core.Persistence.Sql.HostNameKey.Of(h.HostName)).ToHashSet();
        return all.Where(h => !h.Active)
            .Select(h => LogForesight.Core.Persistence.Sql.HostNameKey.Of(h.HostName))
            .Where(k => !active.Contains(k))
            .Distinct()
            .ToList();
    }

    /// <summary>成員逾期：進行中、期限非空且早於今天、狀態為 in_progress 或 observing</summary>
    public static bool IsOverdue(IssueCase c, DateTime today) =>
        c.ClosedAt == null && c.DueDate != null && c.DueDate.Value < today.Date
        && (c.Status == IssueHandlingStatuses.InProgress || c.Status == IssueHandlingStatuses.Observing);

    /// <summary>
    /// 待處理成員（我的交辦「待我處理」的最小單位）：進行中、不是上報中、不在觀察期內。呼叫端另行排除停用主機。
    /// 進行中成員恰好分成三類互斥：待處理（含觀察到期）、觀察期內（<see cref="IssueHandlingStatuses.IsObservationActive"/>）、上報中，
    /// 三者相加＝進行中台數；SQL 端（<c>EfWorkOrderStore</c> 的處理人摘要與清單）與替身都以這個定義為準。
    /// </summary>
    public static bool IsTodoMember(IssueCase c, DateTime today) =>
        c.ClosedAt == null && c.Status != IssueHandlingStatuses.Escalated
        && !IssueHandlingStatuses.IsObservationActive(c.Status, c.DueDate, today);

    /// <summary>清單條件檢查（store 入口共用；使用者輸入的友善訊息由 Web 層先擋）</summary>
    public static void Validate(WorkOrderQuery q)
    {
        if (!OrderStatuses.Contains(q.Status)) throw new ArgumentException($"不支援的交辦單狀態篩選「{q.Status}」。", nameof(q));
        if (!Sorts.Contains(q.Sort)) throw new ArgumentException($"不支援的交辦單排序「{q.Sort}」。", nameof(q));
        if (!PausedModes.Contains(q.PausedMode)) throw new ArgumentException($"不支援的暫停篩選「{q.PausedMode}」。", nameof(q));
        if (q.Page < 1 || q.PageSize < 1 || q.PageSize > MaxOrderPageSize)
            throw new ArgumentException("交辦單清單的頁碼或每頁筆數超出範圍。", nameof(q));
    }

    /// <summary>成員條件檢查（store 入口共用）</summary>
    public static void Validate(WorkOrderMemberQuery q)
    {
        if (!MemberStatuses.Contains(q.Status)) throw new ArgumentException($"不支援的成員狀態篩選「{q.Status}」。", nameof(q));
        if (q.Page < 1 || q.PageSize < 1 || q.PageSize > MaxMemberPageSize)
            throw new ArgumentException("成員清單的頁碼或每頁筆數超出範圍。", nameof(q));
    }
}

/// <summary>單一處理人的進行中摘要（IWorkOrderStore.HandlerSummary）</summary>
public sealed class WorkOrderHandlerSummary
{
    /// <summary>進行中的單數</summary>
    public int ActiveWorkOrders { get; init; }

    /// <summary>其進行中單底下仍進行中的案件數</summary>
    public int ActiveMembers { get; init; }

    /// <summary>其進行中單底下逾期的成員數（判準同 <see cref="WorkOrderQueries.IsOverdue"/>）</summary>
    public int OverdueMembers { get; init; }

    /// <summary>進行中且從未回覆（last_reply_at IS NULL）的單數</summary>
    public int UnrepliedWorkOrders { get; init; }

    /// <summary>進行中且暫停（問題目前靜音中）的單數；上面四個數字都不含暫停單</summary>
    public int PausedWorkOrders { get; init; }

    /// <summary>
    /// 進行中成員裡「觀察期內」的台數（觀察中且觀察至 &gt;= 今天）；與 <see cref="ActiveMembers"/> 同口徑
    /// （非暫停單、不含停用主機上的成員）。判準同 <see cref="IssueHandlingStatuses.IsObservationActive"/>。
    /// </summary>
    public int ObservingMembers { get; init; }

    /// <summary>進行中成員裡上報中的台數；口徑同 <see cref="ActiveMembers"/></summary>
    public int EscalatedMembers { get; init; }

    /// <summary>
    /// 進行中成員裡「待處理」的台數（判準 <see cref="WorkOrderQueries.IsTodoMember"/>：非上報中、非觀察期內，觀察到期算在這裡）；
    /// 口徑同 <see cref="ActiveMembers"/>。與 <see cref="ObservingMembers"/>、<see cref="EscalatedMembers"/> 互斥，三者相加＝<see cref="ActiveMembers"/>。
    /// </summary>
    public int TodoMembers { get; init; }
}

/// <summary>處理人負載看板的一列（列有進行中交辦單、或近 <see cref="ClosedWindowDays"/> 日有結案單的處理人）</summary>
public sealed class HandlerLoad
{
    /// <summary>「近期結案」的回看天數：結案時間 &gt;= 今天−7</summary>
    public const int ClosedWindowDays = 7;

    public long HandlerId { get; init; }

    public int ActiveWorkOrders { get; init; }

    /// <summary>其進行中單底下仍進行中的案件數</summary>
    public int ActiveMembers { get; init; }

    /// <summary>進行中且從未回覆（last_reply_at IS NULL）的單數</summary>
    public int UnrepliedWorkOrders { get; init; }

    /// <summary>其進行中單底下逾期的成員數（判準同 <see cref="WorkOrderQueries.IsOverdue"/>）</summary>
    public int OverdueMembers { get; init; }

    /// <summary>結案時間 &gt;= 今天−7 的單數（不分結案原因）</summary>
    public int ClosedLast7Days { get; init; }

    /// <summary>最早一張進行中單的建立時間；沒有進行中單為 null</summary>
    public DateTime? OldestActiveCreatedAt { get; init; }
}
