using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// 缺漏日計算：本機與 NetIQ 路徑都要「回望 N 天，挑出還沒有分析紀錄的日期」，
/// 邏輯完全相同，只有回望天數的決定方式不同（本機視是否為首次執行決定天數，
/// NetIQ 固定用管理者設定的回補窗口）。
/// </summary>
internal static class MissingDateFinder
{
    public static List<DateTime> Find(IAnalysisRecordReader store, int lookbackDays) =>
        Enumerable.Range(1, lookbackDays)
            .Select(offset => DateTime.Today.AddDays(-offset))
            .Where(date => !store.HasRecord(date))
            .OrderBy(date => date)
            .ToList();
}

/// <summary>
/// 單一主機日分析完成後的共通後續處理：問題案件掛接、風險 log 暫存寫入、AI 呼叫計數——
/// 本機路徑（<see cref="AnalysisOrchestrator"/>）與 NetIQ 機房路徑（<see cref="NetiqPipelineService"/>）
/// 逐日/逐主機分析完成後都要做同一組動作，任一步失敗都不讓這個主機日的分析結果作廢
/// （只記警告，下次執行冪等補跑）。<paramref name="logContext"/> 供 NetIQ 路徑帶入
/// Sentinel／IP 供除錯定位，本機路徑固定傳空字串。
/// </summary>
internal static class HostDayPostProcessor
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static void AttachCase(
        IssueCaseCoordinator caseCoordinator, string hostName, DateTime date,
        List<LogIssueSignature> topIssues, string logContext = "")
    {
        try
        {
            var attach = caseCoordinator.AttachNewDay(hostName, date, topIssues, DateTime.Now);
            if (attach.AttachedCount > 0)
                Log.Info("{Context}{Date:yyyy-MM-dd} 案件掛接：掛入 {Count} 個問題", logContext, date, attach.AttachedCount);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "{Context}{Date:yyyy-MM-dd} 案件掛接失敗（不影響分析結果，下次執行冪等補掛）", logContext, date);
        }
    }

    public static void ReplaceRiskyEvents(
        IRiskyEventStore? riskyEventStore, int retentionDays, DateTime date,
        List<LogIssueSignature> topIssues, List<EventLogEntryData> logs, long hostId, string logContext = "")
    {
        if (riskyEventStore == null || !RiskyEventSelector.WithinRetention(date, retentionDays, DateTime.Today)) return;

        try
        {
            var riskyEvents = RiskyEventSelector.Select(topIssues, logs, hostId, date);
            riskyEventStore.ReplaceDay(hostId, date, riskyEvents);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "{Context}{Date:yyyy-MM-dd} 風險 log 暫存寫入失敗（不影響分析結果）", logContext, date);
        }
    }

    /// <summary>
    /// AiAnalyzed=false 有兩種意義：低風險日「刻意不呼叫」（正常）與呼叫失敗的降級（異常）。
    /// useAi=false（AI 未設定）時本來就不會呼叫，不該被記成失敗，只有後者該計入執行監控頁的
    /// AI 呼叫統計。
    /// </summary>
    public static void RecordAiCallIfApplicable(BatchRunRecorder runRecorder, bool useAi, DailyAnalysisRecord record)
    {
        if (useAi && (record.AiAnalyzed || record.RiskLevel != RiskLevels.Low))
        {
            runRecorder.RecordAiCall(record.AiAnalyzed);
        }
    }
}
