namespace LogForesight.Core.Models;

/// <summary>
/// 問題檔案（↔ webdata blob，key=issue_owners 沿用，回饋十八輪批次F 建立、
/// 回饋十九輪批次F 擴欄承載機房結論）：以 (Source, EventId) 為鍵、跨主機生效——
/// 與「依問題」視角的分組鍵（Web 端 RecordListQueryService 等消費端的
/// GroupBy (Source, EventId)）完全一致。**不用 RuleId**：IssueCase／IssueHandling／
/// lf_top_issues 都沒有 RuleId 欄位，引入第二套鍵只會製造對不上的縫隙。
///
/// 類別自 IssueOwnerRule 改名而來——blob key／既有六個消費端的比對規則一字不改，
/// 純粹是「這個檔案現在不只記負責人，也記機房對這個問題的結論」的正名。
///
/// 負責人語意比照主機負責人（<see cref="WebHost.OwnerUserIds"/>）但作用在問題層級、
/// 優先於主機負責人：
///   1. 自動帶入處理人：問題負責人恰一人時優先於主機負責人（見 Web 端 DayHandlingCommandService）。
///   2. 郵件通知路由：主機日內有問題命中規則時通知問題負責人，取代主機負責人
///      （見 Web 端 MailNotificationService.ResolvePerRecipient）。
///   3. 授權路徑（第四條）：問題負責人自動可見「保留期內出現過其負責問題的主機」
///      （見 Web 端 HostVisibilityResolver.GetIssueOwnedHostIds），並隱含 User 角色。
///
/// 機房結論語意（回饋十九輪批次F，§2 決策一）：這個問題「機房層級」已經有定論
/// （例如「這是已知的雜訊來源，不用再管」），<see cref="AutoApply"/>＝true 時之後
/// 新出現的主機日自動套用這個結論（見 Web 端 IssueCaseCoordinator.AttachNewDay），
/// 使用者標記／進行中案件的優先序仍高於這個 fleet 結論（不搶既有的人工判斷）。
/// </summary>
public class IssueProfile
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
    /// 機房結論（回饋十九輪批次F）：<see cref="IssueHandlingStatuses.Closed"/> 四態之一，
    /// null＝這個問題目前沒有機房層級的結論。刻意限定結案類——`escalated`／`in_progress`／
    /// `observing`／`open` 都是「還沒定案」的狀態，不該被自動套用成新一天的預設值
    /// （那會讓「機房結論」變成幫使用者做了本來該由他判斷的事）。
    /// </summary>
    public string? ConclusionStatus { get; set; }

    /// <summary>結論原因，有 <see cref="ConclusionStatus"/> 時必填——寫入 IssueHandling.Note
    /// 前綴「〔機房結論〕」，讓自動套用的那一天看得出「為什麼」不是空白結案。</summary>
    public string ConclusionNote { get; set; } = string.Empty;

    public long? ConcludedById { get; set; }

    public string? ConcludedByAccount { get; set; }

    public DateTime? ConcludedAt { get; set; }

    /// <summary>
    /// 之後新出現的主機日是否自動套用這個結論（見 IssueCaseCoordinator.AttachNewDay）。
    /// 與 <see cref="ConclusionStatus"/> 分開存放而不是「有結論就自動套用」：使用者可能
    /// 只想記錄「這個問題現在是這樣定論的」，但不想讓它悄悄自動蓋掉之後每一天的畫面——
    /// 兩件事（有沒有結論／要不要自動套用）本質上是獨立的兩個決定。
    /// </summary>
    public bool AutoApply { get; set; }

    /// <summary>
    /// (Source,EventId) 不分大小寫比對是否命中——單一事實來源（回饋十八輪體檢輪修正）：
    /// 原本 IssueOwnerStore、MailNotificationService、RecordListQueryService、
    /// DayHandlingCommandService 各自重寫一份幾乎相同的比對邏輯（且兩種寫法混用：
    /// `string.Equals(..., OrdinalIgnoreCase)` 與 `.ToUpperInvariant()` 建字典鍵），
    /// 收斂到這裡之後只有一處要維護——切換比對規則（例如改成不允許不分大小寫）
    /// 只需要改這一個方法，不會有處改到、處漏改的風險。
    /// </summary>
    public static bool Matches(IssueProfile rule, string source, int eventId) =>
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
    /// GroupBy 防禦性取第一筆（同 PlanBulkClose 對重複鍵的既有慣例）：Upsert 以 OrdinalIgnoreCase
    /// 去重、KeyOf 以 ToUpperInvariant 折疊，兩者對極端 Unicode（如土耳其無點 i）並非嚴格等價，
    /// 直接 ToDictionary 撞鍵會讓整頁炸掉——寧可靜默取第一筆也不讓查詢頁 500。
    /// </summary>
    public static Dictionary<(string SourceUpper, int EventId), List<long>> IndexByKey(IEnumerable<IssueProfile> rules) =>
        rules.GroupBy(r => KeyOf(r.SourceName, r.EventId))
            .ToDictionary(g => g.Key, g => g.First().OwnerUserIds);
}
