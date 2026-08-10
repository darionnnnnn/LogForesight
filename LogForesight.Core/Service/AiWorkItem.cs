namespace LogForesight.Core.Service;

/// <summary>
/// 一個主機日「需要 AI 補完」時，統計段（<see cref="LogAnalysisService.BuildStatisticalRecordAsync"/>）
/// 留給 AI 段（<see cref="LogAnalysisService.CompleteAiAsync"/>）的完整輸入
/// （docs/FEEDBACK-12-PLAN.md §3.2/§3.3）。統計段已經把 issues/trendAlerts/correlations/ruleRisk
/// 都算好，AI 段不重算，只需要這些既有結果＋原始 events（深析報告用——原始 log 不落地，
/// 只能隨件攜帶）。
///
/// **歷史（history）刻意不放進來**：AI 段執行時才向 <see cref="IAnalysisRecordStore"/> 重讀，
/// 讓 <see cref="AiFollowupQueue{T}"/> 的 FIFO 保序保證的「前一天已定案」語意在讀取當下自然成立，
/// 不因兩階段化而讓隔日 prompt 引用前一天 AI 摘要的既有語意降級。
///
/// **<see cref="Logs"/> 已窄化**（回饋十四輪 A2）：建構時（<c>BuildStatisticalRecordAsync</c>
/// 內部）就套用 <see cref="RiskyEventSelector.SelectSourceEvents"/>，不是原始 events 全量——
/// 每主機日至多 500 筆、單筆訊息至多 2000 字。這個不變量在型別建構當下就成立，不依賴唯一呼叫端
/// 事後記得補窄化（見 <see cref="RiskyEventSelector"/> 文件的記憶體風險說明）。
/// </summary>
internal sealed record AiWorkItem(
    DateTime TargetDate,
    List<LogIssueSignature> Issues,
    List<string> TrendAlerts,
    List<CorrelationFinding> Correlations,
    string RuleRisk,
    string? RiskBasis,
    List<string> UncoveredChecks,
    bool DataIncomplete,
    int ErrorCount,
    int WarningCount,
    int AuditCount,
    int HistoryDays,
    List<EventLogEntryData> Logs,
    List<RuleSuppression> ActiveSuppressions);
