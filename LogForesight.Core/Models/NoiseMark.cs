namespace LogForesight;

/// <summary>
/// 「已知雜訊」記憶（↔ blob key=noise_marks，docs/SCALE-2000-PLAN.md §5.1 D-1 #3）。
///
/// 使用者在風險日詳情把某個問題標「已知雜訊」時寫入一筆；之後同主機同簽章的
/// 新問題（不同日期、尚無明確標記）會**自動顯示**「已知雜訊（自動）」，不必每天重標一次。
/// 與規則抑制是兩條不同的治理路徑：有 <see cref="LogIssueSignature.RuleId"/> 時走抑制
/// （治本：之後不再進報告）；沒有規則命中（Other 類別）就只能靠這份記憶（治標：報告仍會出現，
/// 但畫面自動判讀成雜訊，不必使用者每天重新判斷）。
///
/// 鍵＝主機＋問題簽章（<see cref="IssueSignatureKey"/>），不含日期——這是「記憶」的意義所在。
/// </summary>
public class NoiseMark
{
    public string HostName { get; set; } = string.Empty;

    public string IssueKey { get; set; } = string.Empty;

    public string MarkedByAccount { get; set; } = string.Empty;

    public DateTime MarkedAt { get; set; }

    public string? Note { get; set; }
}
