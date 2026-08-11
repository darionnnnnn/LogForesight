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
/// 抑制目標型別（回饋十五輪 A）：四型共用同一份 <see cref="RuleSuppression"/> 清單與同一套
/// Scope／到期語意，差別只在「比對什麼」——這是 A1 的核心修正：規則命中的告警本來就能抑制，
/// 但 <see cref="LogIssueSignature.RuleId"/> 為 null（Other 類未命中規則）的簽章、總量突增
/// 告警、跨 log 關聯告警過去完全沒有抑制掛載點，見 docs/RULES-SPEC.md。
/// </summary>
public static class SuppressionTargetTypes
{
    /// <summary>預設，舊資料反序列化到此值，語意與改版前逐位相同——零遷移</summary>
    public const string Rule = "Rule";

    /// <summary>未命中規則的問題簽章，鍵見 <see cref="IssueSignatureKey.For"/></summary>
    public const string Signature = "Signature";

    /// <summary>跨 log 關聯模式，鍵見 CorrelationPatternIds（LogForesight.Core.Analysis）</summary>
    public const string Correlation = "Correlation";

    /// <summary>整體錯誤量／安全稽核事件量突增，見 <see cref="RuleSuppression.VolumeKind"/></summary>
    public const string Volume = "Volume";

    public static readonly string[] All = { Rule, Signature, Correlation, Volume };

    public static bool IsValid(string targetType) => All.Contains(targetType);
}

/// <summary>TargetType=Volume 抑制比對的總量類別（回饋十五輪 A），對應 TrendAnalyzer 的兩種
/// 突增告警：整體錯誤量／安全稽核事件量。</summary>
public static class VolumeKinds
{
    public const string Error = "error";
    public const string Audit = "audit";

    public static readonly string[] All = { Error, Audit };

    public static bool IsValid(string volumeKind) => All.Contains(volumeKind);
}

/// <summary>
/// 告警抑制項目：維護者判斷某規則在某個範圍（單台主機／主機群組／全站）內的告警不需要繼續吵，
/// 關閉通知。語意邊界（見 docs/RULES-SPEC.md）：抑制只影響「要不要吵」（console/報告的告警呈現、
/// 風險等級是否被此問題拉高），**不影響偵測與紀錄**——事件照常聚合、規則照常命中、
/// 照常寫入歷史，這樣頻率報表才有資料，體檢也才能提醒「暫時關掉的東西後來還在發生」。
/// </summary>
public class RuleSuppression
{
    /// <summary>要抑制的規則 Id（KnownIssueRule.Id）。只有 <see cref="TargetType"/>=Rule 時必填，
    /// 其餘型別為空字串（比對改看 <see cref="SignatureKey"/>／<see cref="CorrelationPatternId"/>／
    /// <see cref="VolumeKind"/>）。</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>
    /// 抑制目標型別（回饋十五輪 A，見 <see cref="SuppressionTargetTypes"/>）：預設 Rule，
    /// 舊資料反序列化到此值，語意與改版前逐位相同——零遷移。
    /// </summary>
    public string TargetType { get; set; } = SuppressionTargetTypes.Rule;

    /// <summary>TargetType=Signature 時的簽章鍵（<see cref="IssueSignatureKey.For"/>）；其餘型別為 null</summary>
    public string? SignatureKey { get; set; }

    /// <summary>TargetType=Correlation 時的關聯模式 Id（見 CorrelationPatternIds）；其餘型別為 null</summary>
    public string? CorrelationPatternId { get; set; }

    /// <summary>TargetType=Volume 時的總量類別：<see cref="VolumeKinds.Error"/>（整體錯誤量）｜
    /// <see cref="VolumeKinds.Audit"/>（安全稽核事件量）；其餘型別為 null</summary>
    public string? VolumeKind { get; set; }

    /// <summary>
    /// 非規則目標的人話標籤（建立時擷取，如「Application / MyApp EventId 1000」），管理頁與
    /// 徽章直接顯示，不必從 SignatureKey/PatternId 反推。TargetType=Rule 時為 null
    /// （規則列表本身有 RuleId 可查，不需要另存標籤）。
    /// </summary>
    public string? TargetLabel { get; set; }

    /// <summary>
    /// 建立時記錄的平台（WebHost.OsWindows／OsLinux），供非規則目標的抑制清單頁篩選——這些目標
    /// 無法像 Rule 一樣從 KnownIssueRule 反查平台。TargetType=Rule 時為 null（沿用既有反查邏輯）。
    /// </summary>
    public string? Platform { get; set; }

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
