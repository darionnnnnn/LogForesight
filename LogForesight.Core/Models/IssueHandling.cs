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

    /// <summary>resolved | wont_fix | false_positive | known_noise（只存結案類；open 以缺列表示）</summary>
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
/// 問題層級的處理狀態集合。刻意只有「結案類」——問題層級不需要 in_progress
/// （「正在處理」是整個風險日的案件狀態，維持在日層級）。
/// </summary>
public static class IssueHandlingStatuses
{
    public const string Resolved = "resolved";
    public const string WontFix = "wont_fix";
    public const string FalsePositive = "false_positive";
    public const string KnownNoise = "known_noise";

    public static readonly string[] Closed =
    {
        Resolved, WontFix, FalsePositive, KnownNoise
    };

    /// <summary>是否為合法的問題層級狀態（皆為結案類）</summary>
    public static bool IsClosed(string status) => Closed.Contains(status);
}
