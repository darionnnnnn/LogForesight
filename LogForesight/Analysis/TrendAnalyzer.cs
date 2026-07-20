namespace LogForesight;

public enum IssueTrend
{
    Unknown,    // 尚無歷史可比對
    New,        // 歷史中從未出現過
    Rising,     // 頻率明顯上升（今日 >= 歷史平均 2 倍且達最低次數門檻）
    Recurring,  // 歷史中重複出現，頻率相近
    Declining   // 頻率明顯下降
}

/// <summary>
/// 程式端的確定性頻率比對：拿當日各事件簽章的發生次數，對照前一日與近期歷史的平均值，
/// 標記趨勢並在頻率上升時自動升級嚴重度。趨勢偵測不依賴 AI——數字比較程式做得又快又準，
/// AI 只負責解讀「為什麼會上升、代表什麼」。
/// </summary>
public static class TrendAnalyzer
{
    /// <summary>今日次數需達此值才可能被判為 Rising，避免 1 次變 2 次這種雜訊觸發告警</summary>
    private const int RisingMinCount = 5;

    /// <summary>今日次數達歷史平均的幾倍視為頻率上升</summary>
    private const double RisingFactor = 2.0;

    /// <summary>
    /// 為當日事件簽章標記趨勢，回傳程式比對出的頻率異常說明（給 prompt 與 console 告警用）
    /// </summary>
    public static List<string> Apply(List<LogIssueSignature> issues, List<DailyAnalysisRecord> history,
        DateTime targetDate, int todayErrorCount, int todayAuditCount)
    {
        var alerts = new List<string>();

        if (history.Count == 0)
        {
            foreach (var sig in issues)
            {
                sig.Trend = IssueTrend.Unknown;
            }
            return alerts;
        }

        // DataIncomplete 的日子（事件來源保留歷史不足以涵蓋整天）一律排除在基準計算外，
        // 否則不完整的一天會墊低/墊高平均值，讓之後的正常量被誤判為頻率異常（或反過來把真異常蓋掉）
        var reliableHistory = history.Where(h => !h.DataIncomplete).ToList();

        // 安全稽核事件量（AuditEventCount）幾乎全來自 Security log，該來源本次或歷史上無權限讀取時
        // 這個數字是假的零，不能拿來當基準
        var reliableAuditHistory = reliableHistory.Where(h => h.SecurityLogAvailable != false).ToList();

        var prevRecord = history.FirstOrDefault(h => h.Date.Date == targetDate.Date.AddDays(-1));

        foreach (var sig in issues)
        {
            bool isSecuritySignature = sig.LogName.Equals("Security", StringComparison.OrdinalIgnoreCase);

            // Security 簽章額外排除「當天 Security log 讀取失敗」的歷史日，避免假性零把平均墊低，
            // 造成權限恢復後的正常量被誤判成「首次出現」或「頻率上升」
            var relevantHistory = isSecuritySignature
                ? reliableHistory.Where(h => h.SecurityLogAvailable != false).ToList()
                : reliableHistory;

            var pastCounts = relevantHistory
                .Select(h => h.TopIssues.FirstOrDefault(i => SameIssue(i, sig)))
                .Where(m => m != null)
                .Select(m => m!.Count)
                .ToList();

            sig.DaysSeenInHistory = pastCounts.Count;
            sig.HistoryDailyAverage = pastCounts.Count > 0 ? Math.Round(pastCounts.Average(), 1) : null;
            sig.PreviousDayCount = prevRecord == null
                ? null
                : prevRecord.TopIssues.FirstOrDefault(i => SameIssue(i, sig))?.Count ?? 0;

            if (pastCounts.Count == 0)
            {
                sig.Trend = IssueTrend.New;
                if (sig.Severity >= IssueSeverity.High)
                {
                    alerts.Add($"首次出現：{sig.Source} EventId {sig.EventId}（{sig.Severity}）今日 x{sig.Count}，近 {relevantHistory.Count} 日可靠歷史中從未發生");
                }
            }
            else if (sig.Count >= RisingMinCount && sig.Count >= sig.HistoryDailyAverage * RisingFactor)
            {
                sig.Trend = IssueTrend.Rising;
                sig.Severity = Escalate(sig.Severity);
                var prevText = sig.PreviousDayCount != null ? $"、昨日 x{sig.PreviousDayCount}" : "";
                alerts.Add($"頻率上升：{sig.Source} EventId {sig.EventId} 今日 x{sig.Count}，近 {relevantHistory.Count} 日可靠歷史平均 x{sig.HistoryDailyAverage}{prevText}");
            }
            else if (sig.HistoryDailyAverage >= RisingMinCount && sig.Count * RisingFactor <= sig.HistoryDailyAverage)
            {
                sig.Trend = IssueTrend.Declining;
            }
            else
            {
                sig.Trend = IssueTrend.Recurring;
            }
        }

        // 整體錯誤量突增：個別事件都不顯眼、但總量暴增，也是異常訊號（例如大量不同來源同時出錯）
        // DataIncomplete 的日子排除在平均值外，避免不完整的一天墊低基準
        if (reliableHistory.Count > 0)
        {
            var avgErrors = reliableHistory.Average(h => (double)h.ErrorCount);
            if (todayErrorCount >= 10 && todayErrorCount >= avgErrors * RisingFactor)
            {
                alerts.Add($"整體錯誤量突增：今日 {todayErrorCount} 筆，近 {reliableHistory.Count} 日可靠歷史平均 {avgErrors:0.#} 筆");
            }
        }

        // 安全稽核事件總量突增：稽核事件（如 4625 登入失敗）不計入錯誤數，需獨立比對總量；
        // 額外排除 Security log 無權限的歷史日（假性零會把平均墊低）
        if (reliableAuditHistory.Count > 0)
        {
            var avgAudit = reliableAuditHistory.Average(h => (double)h.AuditEventCount);
            if (todayAuditCount >= 10 && todayAuditCount >= avgAudit * RisingFactor)
            {
                alerts.Add($"安全稽核事件量突增：今日 {todayAuditCount} 筆，近 {reliableAuditHistory.Count} 日可靠歷史平均 {avgAudit:0.#} 筆，需留意入侵嘗試");
            }
        }

        return alerts;
    }

    private static bool SameIssue(LogIssueSignature a, LogIssueSignature b) =>
        a.LogName == b.LogName && a.Source == b.Source && a.EventId == b.EventId && a.EntryType == b.EntryType;

    private static IssueSeverity Escalate(IssueSeverity s) =>
        s == IssueSeverity.Critical ? IssueSeverity.Critical : s + 1;
}
