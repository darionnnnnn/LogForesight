namespace LogForesight.Web.Models.Dto;

// 排程作業診斷匯出的檔案結構（schemaVersion 2：dailyHealth.date 改為資料日、新增 runDate）。欄位名是給 AI 讀的契約，語意見 docs/FEEDBACK-63-PLAN.md 批次 C。
//
// 兩條結構規則（結果掃描 C-10 依賴它們）：
// 1. 所有物件都是固定結構的類別，沒有「資料內容當屬性名稱」的字典——要表達「依某維度的計數」一律用
//    [{維度值, 計數}] 陣列，維度值放在字串「值」裡，才會被結果掃描檢查。
// 2. 日期、時間字串由程式格式化產生，屬於「程式產生的值」，由組裝服務登錄為放行的整串值。

/// <summary>整份匯出檔。<see cref="Guide"/> 是程式寫死的說明文字，結果掃描時略過（見 RunExportService）。</summary>
public sealed record RunExportDocument
{
    public RunExportMetaDto Meta { get; init; } = new();
    public IReadOnlyList<string>? Guide { get; init; }
    public RunExportContextDto Context { get; init; } = new();
    public IReadOnlyList<ExportDailyHealthDto> DailyHealth { get; init; } = Array.Empty<ExportDailyHealthDto>();
    public ExportRunStatsDto RunStats { get; init; } = new();
    public IReadOnlyList<ExportSignalGroupDto> SignalGroups { get; init; } = Array.Empty<ExportSignalGroupDto>();
    public IReadOnlyList<ExportStackDto> Stacks { get; init; } = Array.Empty<ExportStackDto>();
    public IReadOnlyList<ExportSlowSqlDto> SlowSql { get; init; } = Array.Empty<ExportSlowSqlDto>();
    public IReadOnlyList<ExportAbnormalRunDto> AbnormalRuns { get; init; } = Array.Empty<ExportAbnormalRunDto>();

    /// <summary>期間內有記憶體里程碑的趟次（由舊到新，最多 200 筆、超過取最新）</summary>
    public IReadOnlyList<ExportRunMemoryDto> RunMemory { get; init; } = Array.Empty<ExportRunMemoryDto>();
    public ExportAiFormatAnomaliesDto AiFormatAnomalies { get; init; } = new();
}

public sealed record ExportCountDto(string Name, int Count);

/// <summary>匯出當下的記憶體快照＋GC 設定（數值單位 bytes）。GC 設定三欄：isServerGc、gcConserveMemory、gcHeapHardLimitPercent。</summary>
public sealed record ExportMemoryDto
{
    public long? WorkingSetBytes { get; init; }
    public long? PeakWorkingSetBytes { get; init; }
    public long? ManagedHeapBytes { get; init; }
    public long? GcCommittedBytes { get; init; }
    public long? LohSizeBytes { get; init; }
    public long? HardLimitBytes { get; init; }
    public long? SqliteHeapHighwaterBytes { get; init; }
    public int? NlogRuleCount { get; init; }
    public bool IsServerGc { get; init; }
    public long? GcConserveMemory { get; init; }
    public long? GcHeapHardLimitPercent { get; init; }
    public int Gen2Collections { get; init; }
}

/// <summary>單趟的記憶體里程碑（時間為程式格式化的字串）</summary>
public sealed record ExportRunMemoryMarkDto
{
    public string At { get; init; } = string.Empty;
    public long? WorkingSetBytes { get; init; }
    public long? PeakWorkingSetBytes { get; init; }
    public long? ManagedHeapBytes { get; init; }
    public long? GcCommittedBytes { get; init; }
    public long? LohSizeBytes { get; init; }
    public long? HardLimitBytes { get; init; }
    public long? SqliteHeapHighwaterBytes { get; init; }
    public int? NlogRuleCount { get; init; }
}

/// <summary>逐趟記憶體（<see cref="RunExportDocument.RunMemory"/> 的一筆）</summary>
public sealed record ExportRunMemoryDto
{
    public long RunId { get; init; }
    public string JobType { get; init; } = string.Empty;
    public string StartedAt { get; init; } = string.Empty;
    public ExportRunMemoryMarkDto? Start { get; init; }
    public ExportRunMemoryMarkDto? End { get; init; }
}

// ===== meta =====

public sealed record RunExportMetaDto
{
    public int SchemaVersion { get; init; }
    public string ExportedAt { get; init; } = string.Empty;
    public string UtcOffset { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;
    public ExportPeriodDto Period { get; init; } = new();
    public int Days { get; init; }
    public int RetentionDays { get; init; }
    public bool HasData { get; init; }

    /// <summary>true：aiFormatAnomalies 含遮罩後的事件文字（案例的問題訊息），與一般「不匯出事件內容」的規則不同</summary>
    public bool IncludesEventText { get; init; }

    public ExportDeidentificationDto Deidentification { get; init; } = new();
    public ExportSignalTotalsDto SignalTotals { get; init; } = new();

    /// <summary>匯出當下的行程記憶體與 GC 設定（診斷「吃光記憶體」用）</summary>
    public ExportMemoryDto? Memory { get; init; }

    public IReadOnlyList<ExportOmittedDto> Omitted { get; init; } = Array.Empty<ExportOmittedDto>();
}

public sealed record ExportPeriodDto
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
}

public sealed record ExportDeidentificationDto
{
    public IReadOnlyList<ExportTokenCountDto> Tokens { get; init; } = Array.Empty<ExportTokenCountDto>();
    public string Notice { get; init; } = string.Empty;

    /// <summary>解不開、無法比對實際值的憑證名稱（固定清單，不含任何值）</summary>
    public IReadOnlyList<string> UndecryptableSecrets { get; init; } = Array.Empty<string>();

    /// <summary>已不是目前認證方式的舊密文且解不開的名稱（不含任何值）；目前沒有使用</summary>
    public IReadOnlyList<string> RetiredUndecryptableSecrets { get; init; } = Array.Empty<string>();
}

public sealed record ExportTokenCountDto(string Kind, int Count);

/// <summary>
/// 診斷行的全域計數（與是否被收進訊號組、組數上限無關）：C-4 不變式的對照基準。
/// <see cref="ErrorFatal"/> 就是 <c>/api/runs/errors</c>（minLevel=error）各組次數的總和。
/// </summary>
public sealed record ExportSignalTotalsDto
{
    public int ErrorFatal { get; init; }
    public int Warn { get; init; }
    public int OutputSignals { get; init; }
    public int SlowSql { get; init; }
    public int AiDiagnosticsLines { get; init; }
}

/// <summary>每一處因上限而省略的項目；<see cref="Occurrences"/>／<see cref="ErrorFatalOccurrences"/> 只用於訊號組</summary>
public sealed record ExportOmittedDto
{
    public string Item { get; init; } = string.Empty;
    public int Count { get; init; }
    public int Limit { get; init; }
    public int? Occurrences { get; init; }
    public int? ErrorFatalOccurrences { get; init; }
}

// ===== context =====

public sealed record RunExportContextDto
{
    public ExportScheduleContextDto Schedule { get; init; } = new();
    public ExportPrtgContextDto Prtg { get; init; } = new();
    public ExportAiContextDto Ai { get; init; } = new();
    public ExportHostsContextDto Hosts { get; init; } = new();
    public int RunLogRetentionDays { get; init; }
    public int RecordRetentionDays { get; init; }
}

public sealed record ExportWindowDto(string Start, string End);

public sealed record ExportScheduleContextDto
{
    public bool Enabled { get; init; }
    public IReadOnlyList<ExportWindowDto> Windows { get; init; } = Array.Empty<ExportWindowDto>();
    public IReadOnlyList<ExportWindowDto> AiWindows { get; init; } = Array.Empty<ExportWindowDto>();
    public int AiConcurrency { get; init; }
    public bool LocalAnalysisEnabled { get; init; }
    public bool AutoCatchUp { get; init; }
    public bool DebugDump { get; init; }
}

public sealed record ExportPrtgContextDto
{
    public bool Enabled { get; init; }
    public string FetchStrategy { get; init; } = string.Empty;
    public string ValueFetchScope { get; init; } = string.Empty;
}

public sealed record ExportAiContextDto
{
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; }
    public int RetryCount { get; init; }
    public int RetryDelaySeconds { get; init; }
    public int AiJsonRetryCount { get; init; }
    public int AiMaxTokens { get; init; }
    public int DeepDiveMaxTokens { get; init; }

    /// <summary>主分析回覆的標題欄位長度上限（超過視為內容檢查未通過）</summary>
    public int HeadlineMaxChars { get; init; }

    /// <summary>主分析回覆其餘敘事欄位的長度上限</summary>
    public int OtherFieldMaxChars { get; init; }
}

public sealed record ExportHostsContextDto
{
    public int Total { get; init; }
    public int Active { get; init; }
    public IReadOnlyList<ExportHostSourceDto> BySource { get; init; } = Array.Empty<ExportHostSourceDto>();
}

public sealed record ExportHostSourceDto(string Source, int Total, int Active);

// ===== dailyHealth =====

/// <summary>執行總表的一列（<see cref="Date"/> 是資料日，<see cref="RunDate"/> 是排程處理日＝隔天）。不含主機名單</summary>
public sealed record ExportDailyHealthDto
{
    public bool IsToday { get; init; }
    public bool InProgress { get; init; }

    /// <summary>資料日（這一列的主機狀態與 PRTG 都是這一天的資料）</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>排程處理日（資料日的隔天）；totalSeconds 是這一天開跑的趟</summary>
    public string RunDate { get; init; } = string.Empty;
    public int TotalHosts { get; init; }
    public int Success { get; init; }
    public int Backfilled { get; init; }
    public int Warning { get; init; }
    public int Failed { get; init; }
    public int Stuck { get; init; }
    public int Running { get; init; }
    public int NotRun { get; init; }
    public int RecheckCount { get; init; }
    public string? PendingSummary { get; init; }
    public int LocalDisabled { get; init; }
    public int Stopped { get; init; }
    public string? PrtgOutcome { get; init; }
    public int? PrtgFindings { get; init; }
    public int? PrtgTargetSensors { get; init; }
    public string? PrtgNote { get; init; }
    public int? TotalSeconds { get; init; }
}

// ===== runStats =====

public sealed record ExportRunStatsDto
{
    public int TotalRuns { get; init; }

    /// <summary>狀態為 failed／stuck 但沒有任何 Error 行的趟數（這些趟不會出現在訊號組）</summary>
    public int FailedOrStuckWithoutErrorRuns { get; init; }

    public IReadOnlyList<ExportJobStatDto> ByJob { get; init; } = Array.Empty<ExportJobStatDto>();
    public IReadOnlyList<ExportPhaseStatDto> Phases { get; init; } = Array.Empty<ExportPhaseStatDto>();
    public IReadOnlyList<ExportVersionStatDto> Versions { get; init; } = Array.Empty<ExportVersionStatDto>();
}

public sealed record ExportJobStatDto
{
    public string JobType { get; init; } = string.Empty;
    public string Trigger { get; init; } = string.Empty;
    public int Runs { get; init; }
    public int Success { get; init; }
    public int Warning { get; init; }
    public int Failed { get; init; }
    public int Stopped { get; init; }
    public int Running { get; init; }
    public int Stuck { get; init; }
    public int CatchUpRuns { get; init; }
    public int AiCalls { get; init; }
    public int AiFailures { get; init; }

    /// <summary>AI 呼叫數為 0 時為 null（沒有分母，不是 0）</summary>
    public double? AiFailureRate { get; init; }
}

public sealed record ExportPhaseStatDto
{
    public string Phase { get; init; } = string.Empty;
    public int Runs { get; init; }
    public int Completed { get; init; }
    public int NotSucceeded { get; init; }

    /// <summary>沒有任何完成的階段樣本時為 null</summary>
    public double? MedianSeconds { get; init; }
    public double? P95Seconds { get; init; }
    public double? MaxSeconds { get; init; }
}

public sealed record ExportVersionStatDto
{
    public string Version { get; init; } = string.Empty;
    public int Runs { get; init; }
    public string FirstSeen { get; init; } = string.Empty;
    public string LastSeen { get; init; } = string.Empty;
    public int WarnCount { get; init; }
    public int ErrorCount { get; init; }
}

// ===== signalGroups / stacks / slowSql =====

public sealed record ExportSignalGroupDto
{
    public string Code { get; init; } = string.Empty;

    /// <summary>Warn／Error／Fatal；source=output 的等級依行首符號推定（⚠ 為 Warn、✗ 為 Error）</summary>
    public string Level { get; init; } = string.Empty;

    /// <summary>log（NLog 的 Warn 以上）或 output（白話輸出行首為 ⚠／✗）</summary>
    public string Source { get; init; } = string.Empty;

    public string Logger { get; init; } = string.Empty;
    public IReadOnlyList<ExportStageCountDto> Stages { get; init; } = Array.Empty<ExportStageCountDto>();
    public int Count { get; init; }
    public int AffectedRuns { get; init; }

    /// <summary>組內 Error／Fatal 等級的次數（source=log）；同一正規化訊息同時以 Warn 與 Error 出現時，count 含兩者</summary>
    public int ErrorFatalCount { get; init; }
    public string FirstSeen { get; init; } = string.Empty;
    public string LastSeen { get; init; } = string.Empty;
    public IReadOnlyList<ExportDayCountDto> Daily { get; init; } = Array.Empty<ExportDayCountDto>();
    public IReadOnlyList<ExportVersionCountDto> Versions { get; init; } = Array.Empty<ExportVersionCountDto>();
    public IReadOnlyList<string> Samples { get; init; } = Array.Empty<string>();
    public string? StackId { get; init; }
    public IReadOnlyList<long> RecentRunIds { get; init; } = Array.Empty<long>();

    /// <summary>true：慢 SQL 監控的 Warn（不計入該趟的警告數，格式解析不到才會落在這裡）</summary>
    public bool ExcludedFromWarnCount { get; init; }
}

public sealed record ExportStageCountDto(string Stage, int Count);

public sealed record ExportDayCountDto(string Date, int Count);

public sealed record ExportVersionCountDto(string Version, int Count, int Runs);

public sealed record ExportStackDto(string Id, string Text);

public sealed record ExportSlowSqlDto
{
    public string Operation { get; init; } = string.Empty;
    public int Count { get; init; }
    public long MaxMs { get; init; }
    public double AvgMs { get; init; }
    public int AffectedRuns { get; init; }
    public string FirstSeen { get; init; } = string.Empty;
    public string LastSeen { get; init; } = string.Empty;
}

// ===== abnormalRuns =====

public sealed record ExportAbnormalRunDto
{
    public long RunId { get; init; }
    public string JobType { get; init; } = string.Empty;
    public string Trigger { get; init; } = string.Empty;
    public bool? CatchUp { get; init; }

    /// <summary>執行站台（跑這趟的主機），不是被分析的主機</summary>
    public string? Host { get; init; }

    public string AppVersion { get; init; } = string.Empty;
    public string Args { get; init; } = string.Empty;
    public string StartedAt { get; init; } = string.Empty;
    public string? FinishedAt { get; init; }

    /// <summary>異常中斷的結束時刻是站台重啟時刻，不給耗時（null）</summary>
    public int? DurationSeconds { get; init; }

    public string Status { get; init; } = string.Empty;
    public int? ExitCode { get; init; }
    public bool Interrupted { get; init; }
    public bool Stopped { get; init; }
    public int WarnCount { get; init; }
    public int ErrorCount { get; init; }
    public int AiFailures { get; init; }
    public int? LocalDaysAnalyzed { get; init; }
    public int? LocalDaysFailed { get; init; }
    public int? NetiqDaysAnalyzed { get; init; }
    public int? NetiqDaysFailed { get; init; }
    public int? NetiqHostsSkipped { get; init; }

    /// <summary>這趟結束時仍為空資料待複查的 NetIQ 主機日（本趟新記＋未滿複查間隔而延後）；舊紀錄為 null</summary>
    public int? NetiqEmptyPending { get; init; }
    public string? PrtgOutcome { get; init; }

    /// <summary>趟次層的 PRTG 結局原因（disabled 早退：未啟用／尚未設定認證資訊／憑證無法解密）；經 Text 遮罩，沒有原因為 null</summary>
    public string? PrtgOutcomeNote { get; init; }
    public IReadOnlyList<ExportUnsuccessfulPhaseDto> UnsuccessfulPhases { get; init; } = Array.Empty<ExportUnsuccessfulPhaseDto>();
    public IReadOnlyList<ExportPrtgDayDto> UnsuccessfulPrtgDays { get; init; } = Array.Empty<ExportPrtgDayDto>();
    public IReadOnlyList<string> SignalCodes { get; init; } = Array.Empty<string>();
}

public sealed record ExportUnsuccessfulPhaseDto(string Phase, string? Outcome, string? Note);

public sealed record ExportPrtgDayDto(string Date, string Outcome, string? Note, bool NeedsReevaluation, int? PendingDevices = null);

// ===== aiFormatAnomalies =====

public sealed record ExportAiFormatAnomaliesDto
{
    public int CaseCount { get; init; }

    /// <summary>true：案例數達到掃描上限，實際可能更多</summary>
    public bool CaseCountIsLowerBound { get; init; }

    /// <summary>期間內最早一筆詳細診斷的日期；null＝期間內沒有任何詳細診斷（無法與「功能尚未發生過失敗」區分）</summary>
    public string? DetailedDiagnosticsFirstSeen { get; init; }

    public ExportAiSummaryDto Summary { get; init; } = new();
    public IReadOnlyList<ExportAiCaseDto> Cases { get; init; } = Array.Empty<ExportAiCaseDto>();
    public ExportAiComparisonDto? Comparison { get; init; }
}

public sealed record ExportAiSummaryDto
{
    public IReadOnlyList<ExportCountDto> ByDate { get; init; } = Array.Empty<ExportCountDto>();
    public IReadOnlyList<ExportCountDto> ByOs { get; init; } = Array.Empty<ExportCountDto>();
    public IReadOnlyList<ExportCountDto> BySource { get; init; } = Array.Empty<ExportCountDto>();
    public IReadOnlyList<ExportCountDto> ByReplyShape { get; init; } = Array.Empty<ExportCountDto>();
    public int CutOffCount { get; init; }

    /// <summary>執行紀錄 Warn 歷史比對出的解析失敗／內容檢查失敗次數（依回覆型別）；比對不到就是 0</summary>
    public IReadOnlyList<ExportFailureHistoryDto> FailureHistory { get; init; } = Array.Empty<ExportFailureHistoryDto>();

    public ExportAiDiagnosticsSummaryDto Diagnostics { get; init; } = new();
}

public sealed record ExportFailureHistoryDto(string ResponseType, int ParseFailures, int ValidationFailures);

public sealed record ExportAiDiagnosticsSummaryDto
{
    public int Entries { get; init; }
    public IReadOnlyList<ExportCountDto> ByCategory { get; init; } = Array.Empty<ExportCountDto>();
    public IReadOnlyList<ExportCountDto> ByEndedBy { get; init; } = Array.Empty<ExportCountDto>();
    public IReadOnlyList<ExportCountDto> ByResponseType { get; init; } = Array.Empty<ExportCountDto>();
}

public sealed record ExportAiCaseDto
{
    public string? Host { get; init; }
    public string Date { get; init; } = string.Empty;
    public string? Os { get; init; }
    public string DataSource { get; init; } = string.Empty;
    public string Risk { get; init; } = string.Empty;

    /// <summary>從降級摘要的說明前綴解析（單次呼叫內的嘗試次數）；解析不到為 null</summary>
    public int? Attempts { get; init; }

    /// <summary>AI 排程對這個主機日降級的排程次數（歷史舊標題資料為 0）</summary>
    public int DegradedAttempts { get; init; }

    /// <summary>true：已達連續格式異常上限、不再補跑</summary>
    public bool Abandoned { get; init; }

    /// <summary>object／fence／array／text／blank；詳情已清除（摘要沒留下）為 null</summary>
    public string? ReplyShape { get; init; }

    public bool CutOff { get; init; }

    /// <summary>遮罩後的回覆前 600 字（說明前綴已剝除）</summary>
    public string? Reply { get; init; }

    public bool DetailPruned { get; init; }
    public ExportFeaturesDto? Features { get; init; }
    public IReadOnlyList<ExportDiagnosticDto> Diagnostics { get; init; } = Array.Empty<ExportDiagnosticDto>();

    /// <summary>只給最新的 N 個案例；其餘與詳情已清除者為 null</summary>
    public ExportAiInputDto? Input { get; init; }

    public ExportControlDto? Control { get; init; }
}

public sealed record ExportFeaturesDto
{
    public int IssueCount { get; init; }
    public int RuleHitCount { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public int AuditCount { get; init; }
    public int TrendAlertCount { get; init; }
    public int CorrelationAlertCount { get; init; }
    public bool DataIncomplete { get; init; }
    public int InputChars { get; init; }
    public int LongestItemChars { get; init; }
    public int QuoteCount { get; init; }
    public int BackslashCount { get; init; }
    public int OpenBraceCount { get; init; }
    public int OpenBracketCount { get; init; }
    public int LessThanCount { get; init; }
    public int ControlCharCount { get; init; }
    public int NonBmpCharCount { get; init; }
    public int PathLikeCount { get; init; }
    public int JsonLikeCount { get; init; }
    public int XmlLikeCount { get; init; }

    /// <summary>沒有任何文字（分母為 0）時為 null</summary>
    public double? NonAsciiRatio { get; init; }
}

public sealed record ExportDiagnosticDto
{
    public string LoggedAt { get; init; } = string.Empty;
    public long RunId { get; init; }
    public string ResponseType { get; init; } = string.Empty;
    public string? Model { get; init; }
    public int MaxTokens { get; init; }
    public int Attempts { get; init; }
    public string EndedBy { get; init; } = string.Empty;
    public IReadOnlyList<ExportAttemptDto> AttemptDetails { get; init; } = Array.Empty<ExportAttemptDto>();
}

public sealed record ExportAttemptDto
{
    public int Attempt { get; init; }
    public string Category { get; init; } = string.Empty;
    public string? FinishReason { get; init; }
    public int? PromptChars { get; init; }
    public int? SystemPromptChars { get; init; }
    public int? ResponseChars { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public string? ParseErrorPath { get; init; }
    public IReadOnlyList<ExportFieldLengthDto>? FieldLengths { get; init; }
    public string Head { get; init; } = string.Empty;
    public string? Tail { get; init; }

    /// <summary>空回覆的細分：array（只有 []）或 object（只有 {}）；其他為 null</summary>
    public string? EmptyShape { get; init; }

    /// <summary>清洗前原文字元數；與清洗後完全相同或舊診斷沒有這欄時為 null</summary>
    public int? RawChars { get; init; }

    /// <summary>清洗前原文的開頭（走文字遮罩）；缺值同 <see cref="RawChars"/></summary>
    public string? RawHead { get; init; }

    /// <summary>清洗前原文的結尾（走文字遮罩）；缺值同 <see cref="RawChars"/>，總長不超過 2,000 字時也為 null</summary>
    public string? RawTail { get; init; }

    /// <summary>伺服器另放的思考欄位（reasoning_content／reasoning）字元數；只有長度；沒有該欄位為 null</summary>
    public int? ReasoningChars { get; init; }
}

/// <summary>解析得出時的一個欄位：字串記長度，非字串記型別名</summary>
public sealed record ExportFieldLengthDto(string Field, int? Length, string? Kind);

public sealed record ExportAiInputDto
{
    public int IssueTotal { get; init; }
    public IReadOnlyList<ExportAiInputIssueDto> Issues { get; init; } = Array.Empty<ExportAiInputIssueDto>();
    public IReadOnlyList<string> TrendAlerts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CorrelationAlerts { get; init; } = Array.Empty<string>();
}

public sealed record ExportAiInputIssueDto(string Source, int EventId, int Count, string? Message);

public sealed record ExportControlDto
{
    public string Date { get; init; } = string.Empty;
    public int DaysApart { get; init; }
    public ExportFeaturesDto Features { get; init; } = new();
}

public sealed record ExportAiComparisonDto
{
    public int CaseSamples { get; init; }
    public int ControlSamples { get; init; }

    /// <summary>案例或對照任一組為 0 筆時為 null（不輸出 0）</summary>
    public IReadOnlyList<ExportFeatureMedianDto>? Features { get; init; }
}

public sealed record ExportFeatureMedianDto(string Feature, double? CaseMedian, double? ControlMedian);
