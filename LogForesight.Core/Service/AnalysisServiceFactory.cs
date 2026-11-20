using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>
/// <see cref="LogAnalysisService"/> 與 <see cref="RiskReportService"/> 的唯一組裝點。
/// 各建構點（本機分析、NetIQ 管線、AI 補寫、工作流恢復）共用這裡，深入分析的 token 上限
/// 一律由 <see cref="AiSettings.DeepDiveMaxTokens"/> 取得，不會有路徑漏接而退回預設值。
/// 守門：正式碼只有這個檔案能 <c>new LogAnalysisService(</c>／<c>new RiskReportService(</c>（AnalysisServiceFactoryGuardTests）。
/// 不涉及 AI 閘道：<see cref="IAiService"/> 由呼叫端依「每分片一個實例」的既有語意建立後傳入。
/// </summary>
public static class AnalysisServiceFactory
{
    /// <summary>建立風險報告服務；深入分析 MaxTokens 取自 <paramref name="ai"/>。</summary>
    public static RiskReportService CreateReportService(IAiService aiService, IReportSink reportSink, AiSettings ai,
        Func<IReadOnlyList<KnownIssueRule>>? ruleProvider = null, string? hostOs = null) =>
        new RiskReportService(aiService, reportSink, ai.DeepDiveMaxTokens, ruleProvider, hostOs);

    /// <summary>建立單一主機（或本機）的分析服務；<paramref name="reportService"/> 由呼叫端傳入（含共用的實例）。</summary>
    public static LogAnalysisService Create(EventLogService eventLogService, IAiService aiService,
        IAnalysisRecordStore historyService, ISuppressionStore suppressionStore, RiskReportService? reportService,
        string serverDescription = "", string? host = null, long hostId = 0, IRiskyEventStore? riskyEventStore = null,
        IReadOnlyCollection<long>? hostGroupIds = null, List<RuleSuppression>? suppressionSnapshot = null,
        AnalysisLogSource logSource = AnalysisLogSource.Local,
        Func<DateTime, Func<EventLogEntryData, bool>, EventLogScanResult>? localSecuritySupplementScanner = null,
        string hostOs = WebHost.OsWindows) =>
        new LogAnalysisService(eventLogService, aiService, historyService, suppressionStore, serverDescription, reportService,
            host, hostId, riskyEventStore, hostGroupIds, suppressionSnapshot, logSource,
            localSecuritySupplementScanner, hostOs);
}
