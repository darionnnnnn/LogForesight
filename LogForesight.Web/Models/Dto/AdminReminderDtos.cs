using System.Text.Json.Serialization;

namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 管理者提醒的彙整結果（<c>GET api/work-orders/admin-reminders</c>）：三類的筆數與明細，
/// 交辦總覽的提醒區與管理者摘要信共用同一份（<c>AdminReminderService.Compute</c>）。
/// 明細每類最多 50 筆，筆數是全部。
/// </summary>
public sealed class AdminReminderDto
{
    /// <summary>設定的提醒間隔天數（0＝不寄提醒信；總覽的提醒區不受影響）</summary>
    public int ReminderDays { get; set; }

    /// <summary>判定「上報逾期」用的天數：設定的間隔天數，0（不提醒）時用出廠預設</summary>
    public int ThresholdDays { get; set; }

    /// <summary>上報後超過間隔天數仍沒人處理的成員數</summary>
    public int EscalatedCount { get; set; }

    /// <summary>最近一趟夜間派工派不出去的問題數</summary>
    public int UnassignableCount { get; set; }

    /// <summary>上報時沒有任何收件人、且仍有上報成員的單數</summary>
    public int NoRecipientCount { get; set; }

    /// <summary>三類任一不是 0 就顯示提醒區</summary>
    public bool Any => EscalatedCount + UnassignableCount + NoRecipientCount > 0;

    /// <summary>派不出去清單的彙整時間；null＝尚未跑過夜間派工</summary>
    public DateTime? UnassignableAt { get; set; }

    public List<AdminReminderEscalatedDto> Escalated { get; set; } = new();
    public List<AdminReminderUnassignableDto> Unassignable { get; set; } = new();
    public List<AdminReminderNoRecipientDto> NoRecipient { get; set; } = new();

    // ── 以下只給彙整函式與入列用，不序列化 ──
    [JsonIgnore] public int MailEscalatedCount { get; set; }
    [JsonIgnore] public int MailNoRecipientCount { get; set; }
    [JsonIgnore] public bool UnassignableDueForMail { get; set; }
    [JsonIgnore] public List<string> MailEscalatedKeys { get; set; } = new();
    [JsonIgnore] public List<string> MailNoRecipientKeys { get; set; } = new();
    [JsonIgnore] public List<AdminReminderEscalatedDto> EscalatedAll { get; set; } = new();
    [JsonIgnore] public List<AdminReminderNoRecipientDto> NoRecipientAll { get; set; } = new();
}

/// <summary>上報逾期的一個成員（主機＋問題）</summary>
public sealed class AdminReminderEscalatedDto
{
    [JsonIgnore] public string CaseId { get; set; } = "";
    public long WorkOrderId { get; set; }
    public string IssueLabel { get; set; } = "";
    public string HostName { get; set; } = "";

    /// <summary>上報後已過幾天</summary>
    public int Days { get; set; }

    /// <summary>這次夜間彙整要不要提醒（距上次提醒已滿間隔天數）</summary>
    [JsonIgnore] public bool DueForMail { get; set; }
}

/// <summary>派不出去的問題；<see cref="Reason"/> 是 no_pool／all_paused／no_visibility</summary>
public sealed class AdminReminderUnassignableDto
{
    public string IssueLabel { get; set; } = "";
    public string Reason { get; set; } = "";
    public int Hosts { get; set; }
}

/// <summary>上報時沒有任何收件人的單</summary>
public sealed class AdminReminderNoRecipientDto
{
    public long WorkOrderId { get; set; }
    public string IssueLabel { get; set; } = "";

    [JsonIgnore] public bool DueForMail { get; set; }
}
