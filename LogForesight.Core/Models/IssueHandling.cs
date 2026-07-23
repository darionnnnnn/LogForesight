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

    /// <summary>resolved | wont_fix | false_positive | known_noise | open（只有真的被人動過才存；
    /// 大多數「未處理」仍以缺列表示，open 只用在需要明確蓋掉自動推導的少數情境——見 <see cref="IssueHandlingStatuses.Open"/>）</summary>
    public string Status { get; set; } = string.Empty;

    public long? ActorId { get; set; }

    public string ActorAccount { get; set; } = string.Empty;

    public string? Note { get; set; }

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
/// 問題層級的處理狀態集合。結案類（Closed）四種——問題層級不需要 in_progress
/// （「正在處理」是整個風險日的案件狀態，維持在日層級）。
///
/// <see cref="Open"/> 是唯一的非結案類、但仍需要**明確持久化**的狀態：
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
    public const string Open = "open";

    public static readonly string[] Closed =
    {
        Resolved, WontFix, FalsePositive, KnownNoise
    };

    public static readonly string[] All =
    {
        Resolved, WontFix, FalsePositive, KnownNoise, Open
    };

    /// <summary>是否為結案類狀態</summary>
    public static bool IsClosed(string status) => Closed.Contains(status);

    /// <summary>是否為合法的問題層級狀態（結案類 或 明確 open）</summary>
    public static bool IsValid(string status) => All.Contains(status);
}
