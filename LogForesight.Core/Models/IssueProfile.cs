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
    /// 靜音區間清單（唯一事實來源）。分析側標記 Suppressed 只看紀錄日是否落在任一區間（含首尾），
    /// 重新分析舊日子永遠同答案；讀取側與派工另把「目前靜音中」（今天落在任一區間）也算靜音——
    /// 見 IssueExclusion.IsMuted 與 DispatchContext.IsMuted。舊 blob 缺欄＝空清單。
    /// </summary>
    public List<MuteInterval> Mutes { get; set; } = new();

    /// <summary>紀錄日 <paramref name="date"/> 是否落在任一靜音區間內（含首尾）</summary>
    public static bool IsMutedOn(IssueProfile p, DateTime date) =>
        p.Mutes.Any(m => MuteInterval.Covers(m.From, m.To, date));

    /// <summary>今天所在的靜音區間；沒有則回 null</summary>
    public static MuteInterval? CurrentMute(IssueProfile p, DateTime today) =>
        p.Mutes.FirstOrDefault(m => MuteInterval.Covers(m.From, m.To, today));

    /// <summary>
    /// (Source,EventId) 走來源正規化 comparer 是否命中——單一事實來源：
    /// 原本 IssueOwnerStore、MailNotificationService、RecordListQueryService、
    /// DayHandlingCommandService 各自重寫一份幾乎相同的比對邏輯（且兩種寫法混用：
    /// `string.Equals(..., OrdinalIgnoreCase)` 與 `.ToUpperInvariant()` 建字典鍵），
    /// 收斂到這裡之後只有一處要維護——切換比對規則（例如改成不允許不分大小寫）
    /// 只需要改這一個方法，不會有處改到、處漏改的風險。
    /// </summary>
    public static bool Matches(IssueProfile rule, string source, int eventId) =>
        rule.EventId == eventId && SourceKeyComparer.Instance.Equals(rule.SourceName, source);

    /// <summary>
    /// 批次查找用的來源正規化鍵，與 <see cref="Matches"/> 使用完全相同的規則。
    /// </summary>
    public static (string SourceUpper, int EventId) KeyOf(string source, int eventId) =>
        (WorkOrderIssueKey.SourceKeyOf(source), eventId);

    /// <summary>
    /// 批次建立 (Source,EventId) → 負責人清單 的查找字典（回饋十八輪批次F 的 N+1 教訓：
    /// 逐 record／逐群組各自呼叫 <c>IIssueOwnerStore.GetAll()</c> 是明顯的 N+1）。
    /// 呼叫端（郵件路由、依問題視角 badge）一次批次只建一次。
    /// GroupBy 防禦性取第一筆，避免損壞 blob 的重複鍵讓查詢頁 500。
    /// </summary>
    public static Dictionary<(string SourceUpper, int EventId), List<long>> IndexByKey(IEnumerable<IssueProfile> rules) =>
        rules.GroupBy(r => KeyOf(r.SourceName, r.EventId))
            .ToDictionary(g => g.Key, g => g.First().OwnerUserIds);
}

/// <summary>問題靜音區間（From／To 只取日期，含首尾）</summary>
public class MuteInterval
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    /// <summary>靜音原因（≤500 字）</summary>
    public string Reason { get; set; } = string.Empty;

    public long? ById { get; set; }

    public string ByAccount { get; set; } = string.Empty;

    /// <summary>設定（或最後一次延長）的時間</summary>
    public DateTime At { get; set; }

    /// <summary>
    /// 靜音日期判定的唯一一份規則：<paramref name="date"/> 的日期落在 [from, to]（皆取日期、含首尾）。
    /// 問題檔案 helper、合成抑制項目的比對、派工脈絡、報告反查一律呼叫這裡。
    /// </summary>
    public static bool Covers(DateTime from, DateTime to, DateTime date) =>
        date.Date >= from.Date && date.Date <= to.Date;
}
