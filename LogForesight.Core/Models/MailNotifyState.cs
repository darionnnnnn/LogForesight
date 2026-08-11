namespace LogForesight.Core.Models;

/// <summary>
/// 郵件通知的寄送狀態（↔ webdata blob，key=mail_notify_state，回饋十五輪批次D）：
/// 記錄「每日／每週摘要今天寄過了嗎」與「這台主機這天的緊急通知寄過了嗎」，
/// 避免同一封摘要或同一個高風險日重複轟炸收件人。
/// </summary>
public class MailNotifyState
{
    /// <summary>最後一次寄出每日摘要的日期（yyyy-MM-dd）；null＝從未寄過</summary>
    public string? LastDailySentDate { get; set; }

    /// <summary>最後一次寄出週報的日期（yyyy-MM-dd）</summary>
    public string? LastWeeklySentDate { get; set; }

    /// <summary>已寄過緊急通知的「主機Id|日期」組合，避免同一個高風險日重複寄——
    /// MailNotificationService 定期清掉超過保留期的舊項目，不無限增長</summary>
    public HashSet<string> UrgentSentKeys { get; set; } = new();
}
