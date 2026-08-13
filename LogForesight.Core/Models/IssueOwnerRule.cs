namespace LogForesight.Core.Models;

/// <summary>
/// 問題負責人規則（↔ webdata blob，key=issue_owners，回饋十八輪批次F）：以 (Source, EventId)
/// 為鍵、跨主機生效——與「依問題」視角的分組鍵（Web 端 RecordListQueryService 等消費端的
/// GroupBy (Source, EventId)）完全一致。**不用 RuleId**：IssueCase／IssueHandling／
/// lf_top_issues 都沒有 RuleId 欄位，引入第二套鍵只會製造對不上的縫隙。
///
/// 語意比照主機負責人（<see cref="WebHost.OwnerUserIds"/>）但作用在問題層級、優先於主機負責人：
///   1. 自動帶入處理人：問題負責人恰一人時優先於主機負責人（見 Web 端 DayHandlingCommandService）。
///   2. 郵件通知路由：主機日內有問題命中規則時通知問題負責人，取代主機負責人
///      （見 Web 端 MailNotificationService.ResolvePerRecipient）。
///   3. 授權路徑（第四條）：問題負責人自動可見「保留期內出現過其負責問題的主機」
///      （見 Web 端 HostVisibilityResolver.GetIssueOwnedHostIds），並隱含 User 角色。
/// </summary>
public class IssueOwnerRule
{
    /// <summary>比對不分大小寫（見 IssueOwnerStore.Matches），OrdinalIgnoreCase——與
    /// MatchesSignature（IssueHandlingCommandService）等既有 Source 比對慣例一致。</summary>
    public string SourceName { get; set; } = string.Empty;

    public int EventId { get; set; }

    /// <summary>負責人（可多人，與主機負責人對稱）。</summary>
    public List<long> OwnerUserIds { get; set; } = new();

    public string? Note { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? UpdatedByAccount { get; set; }

    /// <summary>
    /// (Source,EventId) 不分大小寫比對是否命中——單一事實來源（回饋十八輪體檢輪修正）：
    /// 原本 IssueOwnerStore、MailNotificationService、RecordListQueryService、
    /// DayHandlingCommandService 各自重寫一份幾乎相同的比對邏輯（且兩種寫法混用：
    /// `string.Equals(..., OrdinalIgnoreCase)` 與 `.ToUpperInvariant()` 建字典鍵），
    /// 收斂到這裡之後只有一處要維護——切換比對規則（例如改成不允許不分大小寫）
    /// 只需要改這一個方法，不會有處改到、處漏改的風險。
    /// </summary>
    public static bool Matches(IssueOwnerRule rule, string source, int eventId) =>
        rule.EventId == eventId && string.Equals(rule.SourceName, source, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 批次查找用的正規化鍵（ToUpperInvariant）：與 <see cref="Matches"/> 用的
    /// OrdinalIgnoreCase 比對是同一條規則的兩種等價寫法（序數大小寫折疊 vs 序數大寫化後相等比較，
    /// 對本應用網域的 Source 字面值——Windows/Linux 事件來源名稱——兩者結果一致）。
    /// 供需要以字典一次查找取代逐筆比對的批次場景使用（見 <see cref="IndexByKey"/>）。
    /// </summary>
    public static (string SourceUpper, int EventId) KeyOf(string source, int eventId) => (source.ToUpperInvariant(), eventId);

    /// <summary>
    /// 批次建立 (Source,EventId) → 負責人清單 的查找字典（回饋十八輪批次F 的 N+1 教訓：
    /// 逐 record／逐群組各自呼叫 <c>IIssueOwnerStore.GetAll()</c> 是明顯的 N+1）。
    /// 呼叫端（郵件路由、依問題視角 badge）一次批次只建一次。
    /// </summary>
    public static Dictionary<(string SourceUpper, int EventId), List<long>> IndexByKey(IEnumerable<IssueOwnerRule> rules) =>
        rules.ToDictionary(r => KeyOf(r.SourceName, r.EventId), r => r.OwnerUserIds);
}
