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

    /// <summary>已摘要過的「主機Id|日期」組合（回饋十七輪批次A-2）：執行後摘要改窗口查詢
    /// （近 N 天，不含今天——分析永遠只產出到昨天，見 MissingDateFinder）取代原本查
    /// 「今天」的錯誤假設，改用去重狀態避免同一個達門檻主機日被重複摘要。
    /// 語意與 <see cref="UrgentSentKeys"/> 對稱，清理方式相同（保留期截止即移除）。</summary>
    public HashSet<string> SummarySentKeys { get; set; } = new();

    /// <summary>已連續失敗的收件人 email（小寫正規化）→ 連續失敗次數（回饋十七輪批次B-1）：
    /// 達門檻即從本輪 coverage 排除（壞地址不再綁架整批標記），寄送成功則歸零，
    /// 熔斷（本輪未嘗試）不計入。設定頁儲存郵件設定時整份清空。</summary>
    public Dictionary<string, int> RecipientFailureStreaks { get; set; } = new();
}
