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
}

/// <summary>處理人負載看板的一列（只列有進行中交辦單的處理人）</summary>
public sealed class HandlerLoad
{
    public long HandlerId { get; init; }

    public int ActiveWorkOrders { get; init; }

    /// <summary>其進行中單底下仍進行中的案件數</summary>
    public int ActiveMembers { get; init; }

    /// <summary>進行中且從未回覆（last_reply_at IS NULL）的單數</summary>
    public int UnrepliedWorkOrders { get; init; }

    public DateTime OldestActiveCreatedAt { get; init; }
}
