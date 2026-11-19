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

    // 以下三個計數與 Detail 只有 AdminReminder 用：寫入當下算好，信件照這個內容出，總覽顯示的是同一個彙整函式的當下結果
    public int ReminderEscalated { get; set; }       // 上報逾期未處理的成員數
    public int ReminderUnassignable { get; set; }    // 本趟派不出去的問題數
    public int ReminderNoRecipient { get; set; }     // 上報時沒有任何收件人的單數
    public string? Detail { get; set; }              // 三類的明細文字（寫入前截到 DetailMaxLength）

    /// <summary>管理者提醒的結構化明細（成員鍵＋顯示文字）；信件依（類別, 鍵）去重。只有文字的舊項目為 null，組信時退回逐行處理。</summary>
    public List<ReminderDetailItem>? ReminderItems { get; set; }

    /// <summary>結構化明細項目數上限（超過時只留前面的項目，類別總數仍在每個項目的 Total）</summary>
    public const int ReminderItemsMaxCount = 60;

    /// <summary>明細欄寫入佇列的長度上限；超過時截斷、不擲例外</summary>
    public const int DetailMaxLength = 4000;

    public static string? ClampDetail(string? detail) =>
        detail is { Length: > DetailMaxLength } ? detail[..DetailMaxLength] : detail;

    /// <summary>原因欄寫入佇列的長度上限（與取消、退回、靜音原因的輸入上限一致）；超過時截斷、不擲例外。</summary>
    public const int ReasonMaxLength = 500;

    public static string? ClampReason(string? reason) =>
        reason is { Length: > ReasonMaxLength } ? reason[..ReasonMaxLength] : reason;
}

/// <summary>管理者提醒的一個明細項目。Key：上報＝案件識別；沒有收件人＝單號；派不出去＝問題標籤＋原因。</summary>
public sealed class ReminderDetailItem
{
    public const string CategoryEscalated = "escalated";
    public const string CategoryUnassignable = "unassignable";
    public const string CategoryNoRecipient = "no_recipient";

    public const int KeyMaxLength = 200;
    public const int TextMaxLength = 300;
    public const int HeadingMaxLength = 100;
    public const int UnitMaxLength = 20;

    public string Category { get; set; } = "";
    public string Key { get; set; } = "";
    public string Text { get; set; } = "";           // 顯示的一行（不含縮排）
    public int Total { get; set; }                   // 入列當下該類的總數（明細只列前面幾行時用來寫「…等共 N」）
    public string Heading { get; set; } = "";        // 該類段落標題，例如「【上報後逾 3 天沒人處理】」
    public string Unit { get; set; } = "";           // 計數單位，例如「個成員」

    public ReminderDetailItem Clamped() => new()
    {
        Category = Category, Total = Total,
        Key = TextTruncation.SafeCut(Key, KeyMaxLength), Text = TextTruncation.SafeCut(Text, TextMaxLength),
        Heading = TextTruncation.SafeCut(Heading, HeadingMaxLength), Unit = TextTruncation.SafeCut(Unit, UnitMaxLength)
    };
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

    /// <summary>
    /// 管理者提醒（夜間派工結束後彙整）：上報逾 N 天未處理的成員、本趟派不出去的問題、上報時沒有任何收件人的單。
    /// 收件人是管理者；不屬於任何一張單（<see cref="WorkOrderOutboxItem.WorkOrderId"/> 為 0），內容放在 <see cref="WorkOrderOutboxItem.Detail"/>。
    /// </summary>
    public const string AdminReminder = "admin_reminder";
}

public sealed class WorkOrderOutbox
{
    /// key＝{Kind}|{WorkOrderId}|{RecipientUserId}|{EnqueuedAt.Ticks}
    public Dictionary<string, WorkOrderOutboxItem> Items { get; set; } = new(StringComparer.Ordinal);
}
