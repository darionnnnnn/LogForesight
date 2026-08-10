namespace LogForesight.Core.Models;

/// <summary>
/// 告警抑制的生效範圍（回饋十三輪 F，體檢批 3 #13）：主機×規則的抑制粒度在 2000 台環境下
/// 維護成本爆炸——同一條規則在同類主機（如所有 IIS 前端）上都是雜訊卻要逐台設定，
/// 使用者最後會改成直接停用整條規則，反而失去分類與知識庫。Group／Site 讓抑制可以
/// 一次套用到一個主機群組或全站，同時仍保留既有的 Host（單台）粒度。
/// </summary>
public static class SuppressionScopes
{
    public const string Host = "Host";
    public const string Group = "Group";
    public const string Site = "Site";

    public static readonly string[] All = { Host, Group, Site };

    public static bool IsValid(string scope) => All.Contains(scope);
}

/// <summary>
/// 告警抑制項目：維護者判斷某規則在某個範圍（單台主機／主機群組／全站）內的告警不需要繼續吵，
/// 關閉通知。語意邊界（見 docs/RULES-SPEC.md）：抑制只影響「要不要吵」（console/報告的告警呈現、
/// 風險等級是否被此問題拉高），**不影響偵測與紀錄**——事件照常聚合、規則照常命中、
/// 照常寫入歷史，這樣頻率報表才有資料，體檢也才能提醒「暫時關掉的東西後來還在發生」。
/// </summary>
public class RuleSuppression
{
    /// <summary>要抑制的規則 Id（KnownIssueRule.Id）</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>
    /// 生效範圍（回饋十三輪 F）：<see cref="SuppressionScopes.Host"/>（預設，既有資料未帶這個欄位時
    /// 反序列化到此值，語意與改版前逐位相同——零遷移）／<see cref="SuppressionScopes.Group"/>／
    /// <see cref="SuppressionScopes.Site"/>。
    /// </summary>
    public string Scope { get; set; } = SuppressionScopes.Host;

    /// <summary>生效主機（不分大小寫比對 Environment.MachineName）。只有 Scope=Host 時有意義。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>生效的主機群組 Id（對應 WebHost.GroupIds 的其中一個）。只有 Scope=Group 時有意義。</summary>
    public long? HostGroupId { get; set; }

    /// <summary>抑制原因，管理頁與體檢報告會顯示這段文字，方便日後回頭確認「當初為什麼關掉」</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>操作者（Environment.UserName），供稽核用途</summary>
    public string SuppressedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>null = 永久抑制；有值時到期後自動失效（見 docs/RULES-SPEC.md 陷阱 4 的「暫時關掉不能變永久盲區」）</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>為未來「同規則同範圍下，只關閉部分比對範圍」的抑制粒度卡位，此版本必須為 null。</summary>
    public string? MatchFilter { get; set; }
}
