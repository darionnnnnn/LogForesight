namespace LogForesight.Core.Models;

/// 交辦通知待寄項目：只存識別與這次操作才知道的資訊；單的內容（問題、主機、期限）寄送時才讀當下的單。
public sealed class WorkOrderOutboxItem
{
    public string Kind { get; set; } = "";          // WorkOrderOutboxKinds 之一
    public long WorkOrderId { get; set; }
    public long RecipientUserId { get; set; }
    public DateTime EnqueuedAt { get; set; }
    public string ActorAccount { get; set; } = "";
    public int MemberDelta { get; set; }             // 本次新增台數（Appended／夜間續掛用；其他為 0）
    public int RecurrenceMembers { get; set; }       // 復發台數（夜間自動派工延續性偏好）
    public string? Reason { get; set; }              // 取消／移交原因
    public bool Immediate { get; set; }              // 立即通知（C-4；本段一律 false）
    public bool NoEmail { get; set; }                // 收件人沒有 email（寄送段標記）
}

public static class WorkOrderOutboxKinds
{
    public const string Created = "created";
    public const string Appended = "appended";
    public const string Transferred = "transferred";
    public const string Cancelled = "cancelled";

    /// <summary>管理者把「無法處理」的成員退回給處理人（原因欄放退回說明）</summary>
    public const string Returned = "returned";
}

public sealed class WorkOrderOutbox
{
    /// key＝{Kind}|{WorkOrderId}|{RecipientUserId}|{EnqueuedAt.Ticks}
    public Dictionary<string, WorkOrderOutboxItem> Items { get; set; } = new(StringComparer.Ordinal);
}
