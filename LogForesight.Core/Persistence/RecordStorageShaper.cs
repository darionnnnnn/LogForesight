namespace LogForesight.Core.Persistence;

/// <summary>
/// 分析紀錄的儲存前整形規則：純函數，確保「無風險日精簡策略」只有一份定義
/// （docs/DB-SPEC.md 一致性機制 #4：精簡策略單點化）。
/// </summary>
internal static class RecordStorageShaper
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
        if (record.RiskLevel != RiskLevels.Low || record.TopIssues.Count == 0)
        {
            return record;
        }

        return new DailyAnalysisRecord
        {
            Date = record.Date,
            HostId = record.HostId,
            Host = record.Host,
            ErrorCount = record.ErrorCount,
            WarningCount = record.WarningCount,
            AuditEventCount = record.AuditEventCount,
            TrendAlerts = record.TrendAlerts,
            CorrelationAlerts = record.CorrelationAlerts,
            RiskLevel = record.RiskLevel,
            RiskBasis = record.RiskBasis,
            Headline = record.Headline,
            Summary = record.Summary,
            TrendAssessment = record.TrendAssessment,
            Action = record.Action,
            AiAnalyzed = record.AiAnalyzed,
            // AiPending（docs/archive/FEEDBACK-12-PLAN.md §3.5）：低風險日仍可能是「AI 排隊中」的暫代紀錄
            // （尾巴事件夠多時 needsAi 仍為 true），漏抄這個欄位會讓它在精簡後靜默重置為 false，
            // 排隊中的紀錄會被誤判成「AI 已定案不需要」——這是本檔逐欄手抄模式最容易踩的地雷
            // （同 FakeHostStore/FakeSentinelStore 曾漏欄的教訓）
            AiPending = record.AiPending,
            ScreenedTailCount = record.ScreenedTailCount,
            ScreeningNotes = record.ScreeningNotes,
            ReportFile = record.ReportFile,
            DataIncomplete = record.DataIncomplete,
            SecurityLogAvailable = record.SecurityLogAvailable,
            UncoveredChecks = record.UncoveredChecks,
            ChannelsRead = record.ChannelsRead,  // 趨勢基準的頻道覆蓋判斷（ChannelCoverage.WasRead）依賴它，
                                                 // 漏掉會讓低風險日被當成舊紀錄、新頻道的暖身永遠結束不了
            HiddenIssueCount = record.HiddenIssueCount,  // 批次寫入時恆為 0（只有 Web 的 RecordRepository
                                                          // 讀取當下會設定），複製只是維持欄位完整性
            WeeklyCheckup = record.WeeklyCheckup,
            DeepDives = record.DeepDives,       // 低風險日恆為空清單（該日從不觸發深析），原樣帶過即可
            TopIssues = record.TopIssues.Select(i => new LogIssueSignature
            {
                LogName = i.LogName,
                Source = i.Source,
                EventId = i.EventId,
                EntryType = i.EntryType,
                // Linux 完整簽章第五段（回饋十九輪批次B）：漏抄會讓低風險日的 Linux 規則命中
                // 精簡後遺失 EventKey，「同 program 不同規則」併回同一組——同 KnownIssue/RuleId
                // 那一類「新增欄位漏抄進精簡投影」的既有教訓
                EventKey = i.EventKey,
                Count = i.Count,
                FirstSeen = i.FirstSeen,
                LastSeen = i.LastSeen,
                SampleMessages = new List<string>(),       // 精簡：體積大戶，無風險日的基準用不到
                DistinctMessageCount = i.DistinctMessageCount,
                KeyDetails = null,                          // 精簡：同上
                // 登入失敗明細（A1）：跨日比對依賴此欄位，已分組封頂 50 組體積受控，完整保留不精簡
                LoginFailureDetails = i.LoginFailureDetails,
                Category = i.Category,
                Severity = i.Severity,
                // 低風險日仍可能帶「重大」旗標：被抑制的簽章不拉高風險（見 ComputeRuleBasedRisk）
                // 但旗標照算，趨勢層的升級同理——漏掉這個欄位，那些日子的重大標記與頻率報表
                // 依據會靜默消失（與下方 RuleId/Suppressed 同一個理由）
                ElevatesDayRisk = i.ElevatesDayRisk,
                KnownIssue = i.KnownIssue,
                RuleId = i.RuleId,
                Suppressed = i.Suppressed,
                Trend = i.Trend,
                PreviousDayCount = i.PreviousDayCount,
                HistoryDailyAverage = i.HistoryDailyAverage,
                DaysSeenInHistory = i.DaysSeenInHistory
            }).ToList()
        };
    }
}
