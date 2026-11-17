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
    public string? Reason { get; set; }              // 取消／移交／結案／靜音原因（寫入前截到 ReasonMaxLength）
    public string? Status { get; set; }              // 代為結案的結案狀態（IssueHandlingStatuses 代碼；其他種類為 null）
    public bool BlocksRedispatch { get; set; }       // 代為結案時是否勾選「之後不再自動派工」
    public DateTime? Until { get; set; }             // 靜音迄日（MutePaused；靜音造成的代為結案也帶）
    public bool Immediate { get; set; }              // 立即通知：交辦／改派勾「立即通知處理人」時為 true，輪詢時不等摘要時刻單獨寄出
    public bool NoEmail { get; set; }                // 收件人沒有 email（寄送段標記）

    /// <summary>原因欄寫入佇列的長度上限（與取消、退回、靜音原因的輸入上限一致）；超過時截斷、不擲例外。</summary>
    public const int ReasonMaxLength = 500;

    public static string? ClampReason(string? reason) =>
        reason is { Length: > ReasonMaxLength } ? reason[..ReasonMaxLength] : reason;
}

public static class WorkOrderOutboxKinds
{
    public const string Created = "created";
    public const string Appended = "appended";
    public const string Transferred = "transferred";
    public const string Cancelled = "cancelled";

    /// <summary>管理者把「無法處理」的成員退回給處理人（原因欄放退回說明）</summary>
    public const string Returned = "returned";

    /// <summary>管理者代為結案：通知該單處理人（含結案狀態、原因、問題再出現會不會重新派工）</summary>
    public const string AdminClosed = "admin_closed";

    /// <summary>靜音造成的暫停：通知處理人靜音迄日與到期自動恢復（靜音連同代為結案時只發 AdminClosed）</summary>
    public const string MutePaused = "mute_paused";

    /// <summary>管理者提前解除靜音：通知處理人已恢復（靜音自然到期不通知）</summary>
    public const string MuteResumed = "mute_resumed";
}

public sealed class WorkOrderOutbox
{
    /// key＝{Kind}|{WorkOrderId}|{RecipientUserId}|{EnqueuedAt.Ticks}
    public Dictionary<string, WorkOrderOutboxItem> Items { get; set; } = new(StringComparer.Ordinal);
}
