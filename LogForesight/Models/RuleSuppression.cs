namespace LogForesight;

/// <summary>
/// 主機級的告警抑制項目：維護者判斷某規則在某主機上的告警不需要繼續吵，關閉通知。
/// 語意邊界（見 docs/RULES-PLAN.md）：抑制只影響「要不要吵」（console/報告的告警呈現、
/// 風險等級是否被此問題拉高），**不影響偵測與紀錄**——事件照常聚合、規則照常命中、
/// 照常寫入歷史，這樣頻率報表才有資料，體檢也才能提醒「暫時關掉的東西後來還在發生」。
/// </summary>
public class RuleSuppression
{
    /// <summary>要抑制的規則 Id（KnownIssueRule.Id）</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>生效主機（不分大小寫比對 Environment.MachineName）</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>抑制原因，管理頁與體檢報告會顯示這段文字，方便日後回頭確認「當初為什麼關掉」</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>操作者（Environment.UserName），供稽核用途</summary>
    public string SuppressedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>null = 永久抑制；有值時到期後自動失效（見 docs/RULES-PLAN.md 陷阱 4 的「暫時關掉不能變永久盲區」）</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>為未來「同規則同主機下，只關閉部分比對範圍」的抑制粒度卡位，此版本必須為 null。</summary>
    public string? MatchFilter { get; set; }
}
