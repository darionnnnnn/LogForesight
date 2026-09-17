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
}

/// <summary>單張交辦單的成員案件計數</summary>
public sealed class WorkOrderMemberCounts
{
    public int Total { get; init; }

    /// <summary>進行中（closed_at IS NULL）</summary>
    public int Active { get; init; }

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
public static class WorkOrderQueries
{
    public const string StatusActive = "active";
    public const string StatusEscalated = "escalated";
    public const string StatusOverdue = "overdue";
    public const string StatusUnreplied = "unreplied";
    public const string StatusClosed = "closed";
    public const string StatusAll = "all";

    public const string SortCreatedDesc = "created_desc";
    public const string SortMembersDesc = "members_desc";
    public const string SortUnrepliedOldest = "unreplied_oldest";

    public static readonly string[] OrderStatuses =
        { StatusActive, StatusEscalated, StatusOverdue, StatusUnreplied, StatusClosed, StatusAll };

    public static readonly string[] MemberStatuses =
        { StatusActive, StatusClosed, StatusEscalated, StatusOverdue, StatusAll };

    public static readonly string[] Sorts = { SortCreatedDesc, SortMembersDesc, SortUnrepliedOldest };

    public const string PausedInclude = "include";
    public const string PausedExclude = "exclude";
    public const string PausedOnly = "only";

    public static readonly string[] PausedModes = { PausedInclude, PausedExclude, PausedOnly };

    public const int MaxOrderPageSize = 100;
    public const int MaxMemberPageSize = 200;

    /// <summary>成員逾期：進行中、期限非空且早於今天、狀態為 in_progress 或 observing</summary>
    public static bool IsOverdue(IssueCase c, DateTime today) =>
        c.ClosedAt == null && c.DueDate != null && c.DueDate.Value < today.Date
        && (c.Status == IssueHandlingStatuses.InProgress || c.Status == IssueHandlingStatuses.Observing);

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
