namespace LogForesight;

/// <summary>
/// 單一問題（事件簽章）在某個風險日的處理狀態（↔ 未來 lf_issue_handling）。
///
/// 為什麼要問題層級：一個風險日常同時有多個問題，有的要修、有的是誤報、有的是已知雜訊，
/// 硬塞進「一天一個狀態」會逼使用者用自由文字描述「哪項怎樣」，既查不了也統計不了。
/// 日層級的狀態改由問題層級**推導**（見 <see cref="IssueHandlingStatuses.IsClosed"/>），
/// 處理人／期限／說明仍維持日層級（那是案件層概念，不隨單一問題改變）。
///
/// 鍵＝主機＋日期＋問題簽章（<see cref="IssueSignatureKey"/>）。只存「結案類」狀態；
/// 沒有紀錄＝未處理（open），與風險日「從未處理過即 open」同一套「缺列即未處理」語意。
/// </summary>
public class IssueHandling
{
    public string HostName { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    /// <summary>問題簽章的穩定鍵（見 <see cref="IssueSignatureKey.For"/>）</summary>
    public string IssueKey { get; set; } = string.Empty;

    /// <summary>resolved | wont_fix | false_positive | known_noise | in_progress | open（只有真的被人動過才存；
    /// 大多數「未處理」仍以缺列表示，open 只用在需要明確蓋掉自動推導的少數情境——見 <see cref="IssueHandlingStatuses.Open"/>）</summary>
    public string Status { get; set; } = string.Empty;

    public long? ActorId { get; set; }

    public string ActorAccount { get; set; } = string.Empty;

    public string? Note { get; set; }

    /// <summary>
    /// 預計完成日——Status=in_progress 時才有意義，畫面上僅該狀態顯示此欄位。
    /// Status=observing（觀察中，docs/FEEDBACK-8-PLAN.md #4）時**沿用同一欄位**當「觀察至」日期，
    /// 不另開一欄——語意是同一件事的推廣（「這個狀態何時需要重新處理」），不是硬湊；
    /// 到期後仍是這個日期，只是解讀從「該完成了」變成「該回頭看了」，見
    /// <see cref="IssueHandlingStatuses.IsObservationExpired"/>。
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// 出處案件（docs/FEEDBACK-4-PLAN.md §0，2026-07-30 起）：由 <c>IssueCaseCoordinator</c>
    /// 展開寫入的列帶值，使用者在個別日子手動標記的列維持 null。
    /// 這是案件同步的邊界——同步只覆蓋 CaseId 屬於本案件與未標記的日子，
    /// 使用者手動標的列（無論狀態）一律不受案件同步影響。舊資料反序列化為 null，零遷移。
    /// </summary>
    public string? CaseId { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 問題簽章的穩定鍵。以聚合鍵的四個欄位組成——與 <see cref="LogIssueSignature"/> 的相同性
/// 判定（LogName＋Source＋EventId＋EntryType）一致，換言之同一個問題跨日、跨查詢都是同一個鍵。
/// </summary>
public static class IssueSignatureKey
{
    public static string For(string logName, string source, int eventId, System.Diagnostics.EventLogEntryType entryType) =>
        $"{logName}|{source}|{eventId}|{(int)entryType}";

    public static string For(LogIssueSignature signature) =>
        For(signature.LogName, signature.Source, signature.EventId, signature.EntryType);
}

/// <summary>
/// 問題層級的處理狀態集合。結案類（Closed）四種＋兩個未結案但需要明確持久化的狀態
/// （in_progress／observing）。
///
/// <see cref="InProgress"/>（2026-07-27 起，風險日詳情批次套用改版）：問題層級現在也能標
/// 「處理中」並帶 <see cref="IssueHandling.DueDate"/>——批次勾選多個問題、在右側處理狀態
/// 區塊填一次即可套用，不用再逐項各自填。非結案類，但一旦有任一問題被標成 in_progress，
/// 當日狀態即推導為 in_progress（見 DayHandlingDerivation）。
///
/// <see cref="Observing"/>（docs/FEEDBACK-8-PLAN.md #4）：處理人判斷「先看幾天再說」——
/// 觀察期間這個問題不再進入待辦／告警（跟 in_progress 一樣不算 open），但處理中的人隨時
/// 查得到、確認得了。到期語意**讀取時推導**，不跑背景作業（同「缺列即未處理」的哲學）：
/// 到期＝視同 in_progress 且逾期，自然回到既有的待辦／逾期通道現身，不必另建通知機制
/// （見 <see cref="IsObservationActive"/>／<see cref="IsObservationExpired"/>）。
///
/// <see cref="Open"/> 是唯一另一個非結案類、但仍需要**明確持久化**的狀態：
/// 用在使用者要蓋掉畫面自動推導的預設值時（低風險預設不處理／已知雜訊記憶自動判讀），
/// 「調回未處理」若只是清除標記，缺列語意會讓畫面重新套用同一個自動推導、
/// 使用者的操作等於沒發生。所以這裡持久化一筆 open，明確蓋掉自動推導。
/// </summary>
public static class IssueHandlingStatuses
{
    public const string Resolved = "resolved";
    public const string WontFix = "wont_fix";
    public const string FalsePositive = "false_positive";
    public const string KnownNoise = "known_noise";
    public const string InProgress = "in_progress";
    public const string Observing = "observing";
    public const string Open = "open";

    public static readonly string[] Closed =
    {
        Resolved, WontFix, FalsePositive, KnownNoise
    };

    public static readonly string[] All =
    {
        Resolved, WontFix, FalsePositive, KnownNoise, InProgress, Observing, Open
    };

    /// <summary>是否為結案類狀態</summary>
    public static bool IsClosed(string status) => Closed.Contains(status);

    /// <summary>是否為合法的問題層級狀態（結案類、明確 open、或 in_progress/observing）</summary>
    public static bool IsValid(string status) => All.Contains(status);

    /// <summary>觀察中且尚未到期——<see cref="IssueHandling.DueDate"/> 在此狀態下代表「觀察至」</summary>
    public static bool IsObservationActive(string status, DateTime? dueDate, DateTime today) =>
        status == Observing && dueDate.HasValue && today.Date <= dueDate.Value.Date;

    /// <summary>觀察期滿、問題仍在——回到「處理中逾期」的既有通道現身，不是新狀態</summary>
    public static bool IsObservationExpired(string status, DateTime? dueDate, DateTime today) =>
        status == Observing && dueDate.HasValue && dueDate.Value.Date < today.Date;
}
