namespace LogForesight;

/// <summary>
/// 分析紀錄的儲存前整形規則：純函數，所有儲存後端（現行 JSONL、未來 DB）都呼叫同一份，
/// 確保「無風險日精簡策略」只有一份定義——規則長在單一實作裡的話，DB 實作屆時得複製一份，
/// 兩份規則遲早漂移不同步（docs/DB-PLAN.md「txt ↔ DB 一致性保證」機制 #4：精簡策略單點化）。
/// </summary>
public static class RecordStorageShaper
{
    /// <summary>
    /// 無風險（低）日的精簡策略：全部簽章的次數/嚴重度/趨勢數字/發生時段完整保留
    /// （這些正是 TrendAnalyzer 計算 14 日平均與「首次出現」判定所需的基準，不可省略），
    /// 只省略體積最大的範例訊息與帳號/IP 彙總——這兩者在無風險日對基準判斷沒有價值，
    /// 需要時原始內容仍在 Sentinel／本機 Event Log 裡查得到。
    /// 風險「中」以上的日子維持完整紀錄不精簡（報告與後續調查需要範例訊息佐證）。
    /// </summary>
    public static DailyAnalysisRecord ForStorage(DailyAnalysisRecord record)
    {
        if (record.RiskLevel != "低" || record.TopIssues.Count == 0)
        {
            return record;
        }

        return new DailyAnalysisRecord
        {
            Date = record.Date,
            Host = record.Host,
            ErrorCount = record.ErrorCount,
            WarningCount = record.WarningCount,
            AuditEventCount = record.AuditEventCount,
            TrendAlerts = record.TrendAlerts,
            CorrelationAlerts = record.CorrelationAlerts,
            RiskLevel = record.RiskLevel,
            Summary = record.Summary,
            TrendAssessment = record.TrendAssessment,
            Recommendations = record.Recommendations,
            AiAnalyzed = record.AiAnalyzed,
            ScreenedTailCount = record.ScreenedTailCount,
            ScreeningNotes = record.ScreeningNotes,
            ReportFile = record.ReportFile,
            DataIncomplete = record.DataIncomplete,
            SecurityLogAvailable = record.SecurityLogAvailable,
            UncoveredChecks = record.UncoveredChecks,
            WeeklyCheckup = record.WeeklyCheckup,
            DeepDives = record.DeepDives,       // 低風險日恆為空清單（該日從不觸發深析），原樣帶過即可
            TopIssues = record.TopIssues.Select(i => new LogIssueSignature
            {
                LogName = i.LogName,
                Source = i.Source,
                EventId = i.EventId,
                EntryType = i.EntryType,
                Count = i.Count,
                FirstSeen = i.FirstSeen,
                LastSeen = i.LastSeen,
                SampleMessages = new List<string>(),       // 精簡：體積大戶，無風險日的基準用不到
                DistinctMessageCount = i.DistinctMessageCount,
                KeyDetails = null,                          // 精簡：同上
                Category = i.Category,
                Severity = i.Severity,
                KnownIssue = i.KnownIssue,
                Trend = i.Trend,
                PreviousDayCount = i.PreviousDayCount,
                HistoryDailyAverage = i.HistoryDailyAverage,
                DaysSeenInHistory = i.DaysSeenInHistory
            }).ToList()
        };
    }
}
