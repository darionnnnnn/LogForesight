namespace LogForesight.Core.Models;

/// <summary>案件逐日同步的模式值域（<see cref="CaseDayIntent.Mode"/>）</summary>
public static class CaseDayModes
{
    /// <summary>指派：把案件狀態展開到全部合格日，觸發日記「案件指派」</summary>
    public const string Assign = "assign";

    /// <summary>同步：案件狀態變更展開到全部合格日，觸發日記使用者按下的標記動作</summary>
    public const string Sync = "sync";

    /// <summary>取消：只把本案件擁有、尚未結案的日子調回 open</summary>
    public const string Cancel = "cancel";
}

/// <summary>
/// 案件逐日同步的「要寫成什麼」：就地寫入時直接展開；列數超過門檻時存在案件上
/// （<see cref="IssueCase.DaySyncIntent"/>），由背景服務分批展開。
/// 同一個意圖重跑不重複寫——逐日列的 UpdatedAt 等於 <see cref="OccurredAt"/> 且內容相同即跳過。
/// </summary>
public class CaseDayIntent
{
    /// <summary><see cref="CaseDayModes"/> 值域</summary>
    public string Mode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? Note { get; set; }

    public DateTime? DueDate { get; set; }

    /// <summary>同步模式專用：調回未處理（目標狀態 open、備註清空）</summary>
    public bool Clearing { get; set; }

    public long? ActorId { get; set; }

    public string ActorAccount { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    /// <summary>使用者實際操作的那一天；有值時該日歷程記使用者動作、其餘日記案件同步</summary>
    public DateTime? TriggerDate { get; set; }
}
