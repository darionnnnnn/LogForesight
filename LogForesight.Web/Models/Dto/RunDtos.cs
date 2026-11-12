namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 執行監控每日彙總（§5.4 D-4）：取代舊版「主機×日期」矩陣。
/// 兩千台規模下矩陣會炸出 2000×90 格 DOM，改成每天一列的計數＋失敗主機清單
/// （上限 10 台＋「其他 N 台」），完整清單透過 <see cref="RunDayDetailHostDto"/> 點日期取得。
/// </summary>
public class RunDaySummaryDto
{
    /// <summary>今天的附加列（不佔 days）：未執行數代表尚未處理，不是漏跑</summary>
    public bool IsToday { get; set; }

    /// <summary>今天的取數趟還在跑</summary>
    public bool InProgress { get; set; }

    public string Date { get; set; } = string.Empty;

    public int TotalHosts { get; set; }
    public int SuccessCount { get; set; }

    /// <summary>已回補（§3）：當天無 BatchRun，但事後回補的分析紀錄補上了這天。與 SuccessCount
    /// 分開——「當天真的有跑」與「後來補的資料」是不同狀態，見 RunMonitorService.LocalStatus</summary>
    public int BackfilledCount { get; set; }

    public int WarningCount { get; set; }
    public int FailedCount { get; set; }
    public int StuckCount { get; set; }
    public int RunningCount { get; set; }
    public int NotRunCount { get; set; }

    /// <summary>NetIQ 回空資料、等複查一次的主機數：不是漏跑，不計入 <see cref="NotRunCount"/></summary>
    public int RecheckCount { get; set; }

    /// <summary>今天列限定：有待複查時，同一窗口內預計的複查時刻（HH:mm，<see cref="LogForesight.Core.Service.ScheduleCalculator.NextEmptyRecheckAt"/>）；排不進窗口或其他列為 null</summary>
    public string? RecheckAt { get; set; }

    /// <summary>今天列限定：最後一趟取數結束時尚有的待補事項（或「最後一趟異常中斷」）；其他列與無待補時為 null</summary>
    public string? PendingSummary { get; set; }

    /// <summary>本機分析已停用（回饋十八輪批次D）：排程設定「分析本機主機」關閉時，本機主機的
    /// 空白日不算進「未執行」——那是設定行為不是漏跑，混計會讓人誤判排程壞了。</summary>
    public int LocalDisabledCount { get; set; }

    /// <summary>優雅停止（手動停止或執行窗口 End 到點）——不算失敗，見 <c>BatchRun.Stopped</c></summary>
    public int StoppedCount { get; set; }

    /// <summary>當天開跑的執行時間（null＝當天沒有任何趟）</summary>
    public RunDayTimingDto? Timing { get; set; }

    /// <summary>PRTG 擷取成果狀態（取該日取數類最後一筆，null＝舊紀錄或當日未執行 PRTG）</summary>
    public string? PrtgOutcome { get; set; }

    /// <summary>該日 PRTG finding 數（與 <see cref="PrtgOutcome"/> 同一筆逐日統計；舊紀錄為 null）</summary>
    public int? PrtgFindings { get; set; }

    /// <summary>該日 PRTG 觸發式取數目標 sensor 數（與 <see cref="PrtgOutcome"/> 同一筆逐日統計；舊紀錄為 null）</summary>
    public int? PrtgTargetSensors { get; set; }

    /// <summary>該日 PRTG 結局原因（與 <see cref="PrtgOutcome"/> 同一筆逐日統計；舊紀錄或無原因為 null）</summary>
    public string? PrtgNote { get; set; }

    /// <summary>失敗（含異常中斷）的主機名，最多 10 台；其餘用 OtherFailedCount 表示</summary>
    public List<string> FailedHostNames { get; set; } = new();
    public int OtherFailedCount { get; set; }
}

/// <summary>單一日期的逐主機狀態（點日期下鑽時取得，同一天最多 TotalHosts 筆，不是整個矩陣）</summary>
public class RunDayHostStatusDto
{
    public string HostName { get; set; } = string.Empty;

    /// <summary>success | backfilled | warning | failed | stopped | running | stuck | none | local_disabled | recheck（空資料待複查）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>狀態的補充說明（等複查、已放棄自動重查）；null＝沒有</summary>
    public string? StatusNote { get; set; }

    /// <summary>這一格的狀態來自升級前沒有階段資料的紀錄（以整趟狀態代表）</summary>
    public bool Legacy { get; set; }

    public long? RunId { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int DaysAnalyzed { get; set; }
    public int WarnCount { get; set; }
    public int ErrorCount { get; set; }
    public int AiFailures { get; set; }

    /// <summary>Persisted host-day workflow snapshot; null means no workflow sidecar has been reconstructed yet.</summary>
    public long? DecisionVersion { get; set; }
    public string? ParentWorkflowState { get; set; }
    public string? PrtgWorkflowState { get; set; }
    public string? PrtgReadinessState { get; set; }
    public string? WorkflowRecoveryState { get; set; }
    public string? WorkflowRecoveryReason { get; set; }
    public string? AiWorkflowState { get; set; }
    public string? CaseWorkflowState { get; set; }
    public string? MailWorkflowState { get; set; }
    public bool? WorkflowComplete { get; set; }
    /// <summary>complete | partial | pending; distinguishes terminal degraded/failed legs from clean success.</summary>
    public string? WorkflowOutcome { get; set; }

    /// <summary>當日執行次數（手動重跑會 > 1）</summary>
    public int RunCount { get; set; }
}

public class RunPhaseDto
{
    public string Key { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Outcome { get; set; }
    public string? Note { get; set; }

    public static List<RunPhaseDto>? From(List<RunPhaseRecord>? phases) => phases?.Select(p => new RunPhaseDto
    {
        Key = p.Key,
        StartedAt = p.StartedAt,
        FinishedAt = p.FinishedAt,
        Outcome = p.Outcome,
        Note = p.Note
    }).ToList();
}

/// <summary>某一路當天開跑的區間與耗時（同日多趟取聯集）</summary>
public class RunBranchTimingDto
{
    public string Key { get; set; } = string.Empty;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int Seconds { get; set; }

    /// <summary>這一路底下的子階段（目前只有 PRTG：結構同步、觸發式取數、補數值）；沒有則為空</summary>
    public List<RunBranchTimingDto> SubPhases { get; set; } = new();
}

/// <summary>執行總表的當天執行時間：取數趟的起訖與總耗時，加上本機／NetIQ／PRTG／AI 各路明細</summary>
public class RunDayTimingDto
{
    public DateTime? FirstStartedAt { get; set; }
    public DateTime? LastFinishedAt { get; set; }
    /// <summary>取數趟與跟隨取數 AI 趟的聯集秒數（異常中斷不計）；沒有可計算的趟為 null</summary>
    public int? TotalSeconds { get; set; }

    /// <summary>非跟隨的 AI 趟（排程補跑、手動）趟數；不進起訖與總計</summary>
    public int OtherAiRuns { get; set; }

    /// <summary>非跟隨 AI 趟的聯集秒數（OtherAiRuns 為 0 時為 null）</summary>
    public int? OtherAiSeconds { get; set; }

    /// <summary>已完成（非異常中斷）的取數趟數；用來判斷「全部沒有階段資料」</summary>
    public int FetchRuns { get; set; }

    /// <summary>已完成（非異常中斷）的取數趟中，沒有 Phases 的趟數（升級前紀錄）</summary>
    public int FetchRunsWithoutPhases { get; set; }

    public int InterruptedCount { get; set; }
    public DateTime? LastInterruptedActivityAt { get; set; }
    public List<RunBranchTimingDto> Branches { get; set; } = new();
}

/// <summary>記憶體里程碑（數值以 bytes 傳，前端換算）</summary>
public class RunMemoryMarkDto
{
    public DateTime At { get; set; }
    public long? WorkingSetBytes { get; set; }
    public long? PeakWorkingSetBytes { get; set; }
    public long? ManagedHeapBytes { get; set; }
    public long? GcCommittedBytes { get; set; }
    public long? LohSizeBytes { get; set; }
    public long? HardLimitBytes { get; set; }
    public long? SqliteHeapHighwaterBytes { get; set; }
    public int? NlogRuleCount { get; set; }

    public static RunMemoryMarkDto? From(RunMemoryMark? mark) => mark == null ? null : new RunMemoryMarkDto
    {
        At = mark.At,
        WorkingSetBytes = mark.WorkingSetBytes,
        PeakWorkingSetBytes = mark.PeakWorkingSetBytes,
        ManagedHeapBytes = mark.ManagedHeapBytes,
        GcCommittedBytes = mark.GcCommittedBytes,
        LohSizeBytes = mark.LohSizeBytes,
        HardLimitBytes = mark.HardLimitBytes,
        SqliteHeapHighwaterBytes = mark.SqliteHeapHighwaterBytes,
        NlogRuleCount = mark.NlogRuleCount
    };
}

public class RunDetailDto
{
    public long RunId { get; set; }

    /// <summary>與列表同一份判定（BatchRunStatus.Compute），詳情不另算一次</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>異常中斷：FinishedAt 是站台重啟時刻，不代表這趟跑到那時候</summary>
    public bool Interrupted { get; set; }

    public List<RunPhaseDto>? Phases { get; set; }

    /// <summary>本趟開始時的記憶體里程碑（bytes；null＝舊紀錄或讀不到）</summary>
    public RunMemoryMarkDto? MemoryStart { get; set; }

    /// <summary>本趟結束時的記憶體里程碑（bytes；null＝舊紀錄、異常中斷或讀不到）</summary>
    public RunMemoryMarkDto? MemoryEnd { get; set; }

    public string HostName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? DurationSeconds { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>優雅停止（手動停止或窗口 End 到點）——詳情頁狀態顯示「已停止」而非以 exit code 判定</summary>
    public bool Stopped { get; set; }

    public string AppVersion { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public int DaysAnalyzed { get; set; }
    public int AiCalls { get; set; }
    public int AiFailures { get; set; }
    public int WarnCount { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>「誰跑的」（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4）：排程／手動（含帳號）／工作排程器
    /// （console，含舊紀錄沒有 Trigger 欄位的情況——那正是升級前唯一的觸發來源）</summary>
    public string TriggerText { get; set; } = string.Empty;

    /// <summary>本機分析成功天數（null＝舊紀錄或本機未產出）</summary>
    public int? LocalDaysAnalyzed { get; set; }

    /// <summary>本機分析失敗天數（null＝舊紀錄或本機未產出）</summary>
    public int? LocalDaysFailed { get; set; }

    /// <summary>NetIQ 機房分析成功主機日數（null＝舊紀錄或 NetIQ 未產出）</summary>
    public int? NetiqDaysAnalyzed { get; set; }

    /// <summary>NetIQ 機房分析失敗主機日數（null＝舊紀錄或 NetIQ 未產出）</summary>
    public int? NetiqDaysFailed { get; set; }

    /// <summary>NetIQ 機房分析已完成跳過的主機日數（null＝舊紀錄或 NetIQ 未產出）</summary>
    public int? NetiqHostsSkipped { get; set; }

    /// <summary>取數範圍（full | local | netiq-hosts，null＝舊紀錄或 AI 趟）</summary>
    public string? Scope { get; set; }

    /// <summary>PRTG 擷取成果狀態（disabled | success | partial | failed，null＝舊紀錄或未執行）</summary>
    public string? PrtgOutcome { get; set; }

    /// <summary>PRTG 結局原因（disabled 早退時：未啟用／尚未設定認證資訊／憑證無法解密；舊紀錄或無原因為 null）</summary>
    public string? PrtgOutcomeNote { get; set; }

    /// <summary>PRTG 觸發式取數目標感測器數（null＝舊紀錄或 PRTG 未產出）</summary>
    public int? PrtgSensorsFetched { get; set; }

    /// <summary>這趟的取數策略（conservative | aggressive，null＝舊紀錄）；保守策略夜間不做觸發式取數，畫面不顯示 sensor 數</summary>
    public string? PrtgStrategy { get; set; }

    /// <summary>PRTG 觸發式取數失敗感測器數（null＝舊紀錄或 PRTG 未產出）</summary>
    public int? PrtgSensorsFailed { get; set; }

    /// <summary>PRTG 觸發式取數問題主機數（null＝舊紀錄或 PRTG 未產出）</summary>
    public int? PrtgTriggeredHosts { get; set; }

    /// <summary>PRTG 逐日統計（null＝舊紀錄或 PRTG 未產出）</summary>
    public List<PrtgDayStatDto>? PrtgDays { get; set; }

    public List<RunLogDto> Logs { get; set; } = new();
}

/// <summary>執行詳情「PRTG 逐日」表的一列</summary>
public class PrtgDayStatDto
{
    public DateTime Date { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public int Findings { get; set; }
    public int AttributedHosts { get; set; }
    public bool MapAvailable { get; set; }
    public int TriggerHosts { get; set; }
    public int TargetSensors { get; set; }
    public int FailedSensors { get; set; }
    public string? Note { get; set; }
}

public class RunLogDto
{
    public DateTime LoggedAt { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Logger { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? ExceptionText { get; set; }
}

/// <summary>異常彙總的一組：同一個錯誤在哪些主機出現過幾次</summary>
public class RunErrorGroupDto
{
    public string Message { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    /// <summary>出現次數（診斷行數）</summary>
    public int Count { get; set; }
    /// <summary>影響趟數：組內去重後的 RunId 數（與 <see cref="Count"/> 的「次數」不同）</summary>
    public int AffectedRuns { get; set; }
    /// <summary>true＝Warn 且來自慢 SQL 效能監控（不計入該趟的警告數，只供排查）</summary>
    public bool ExcludedFromWarnCount { get; set; }
    public List<string> AffectedHosts { get; set; } = new();
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public long LatestRunId { get; set; }
}

/// <summary>異常彙總的回應：訊息分組，加上沒有任何 Error 行卻失敗或中斷的趟數</summary>
public class RunErrorSummaryDto
{
    public List<RunErrorGroupDto> Groups { get; set; } = new();

    /// <summary>狀態為 failed 或 stuck 且 ErrorCount 為 0 的趟數（這些趟不會出現在 <see cref="Groups"/>）</summary>
    public int FailedWithoutErrorRuns { get; set; }
}

/// <summary>執行紀錄（回饋十七輪批次F-3）：每一筆 BatchRun 原始資料的扁平清單，不像
/// <see cref="RunDaySummaryDto"/>／<see cref="RunDayHostStatusDto"/> 是按日期/主機彙總——
/// 回答的是「每一次執行本身跑了多久、觸發方式、有沒有問題」，供「執行紀錄」分頁逐筆檢視。</summary>
public class RunListItemDto
{
    /// <summary>這一趟結束時還留著的失敗與待補（單位統一）；沒有或 AI 趟為 null</summary>
    public string? ResumeSummary { get; set; }

    public long RunId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? DurationSeconds { get; set; }

    public bool Interrupted { get; set; }

    public List<RunPhaseDto>? Phases { get; set; }

    /// <summary>success | warning | failed | stopped | running | stuck，與 RunDayHostStatusDto.Status
    /// 同一套語意（不含 backfilled／none——那是「沒有這筆 BatchRun」的狀態，這裡每筆都存在）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>作業類型顯示文字（批次D）：「取數分析」（含舊紀錄）｜「AI 分析」</summary>
    public string JobTypeText { get; set; } = string.Empty;

    public string TriggerText { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public int DaysAnalyzed { get; set; }
    public int AiCalls { get; set; }
    public int AiFailures { get; set; }
    public int WarnCount { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>本機分析成功天數（null＝舊紀錄或本機未產出）</summary>
    public int? LocalDaysAnalyzed { get; set; }

    /// <summary>本機分析失敗天數（null＝舊紀錄或本機未產出）</summary>
    public int? LocalDaysFailed { get; set; }

    /// <summary>NetIQ 機房分析成功主機日數（null＝舊紀錄或 NetIQ 未產出）</summary>
    public int? NetiqDaysAnalyzed { get; set; }

    /// <summary>NetIQ 機房分析失敗主機日數（null＝舊紀錄或 NetIQ 未產出）</summary>
    public int? NetiqDaysFailed { get; set; }

    /// <summary>NetIQ 機房分析已完成跳過的主機日數（null＝舊紀錄或 NetIQ 未產出）</summary>
    public int? NetiqHostsSkipped { get; set; }

    /// <summary>取數範圍（full | local | netiq-hosts，null＝舊紀錄或 AI 趟）</summary>
    public string? Scope { get; set; }

    /// <summary>PRTG 擷取成果狀態（disabled | success | partial | failed，null＝舊紀錄或未執行）</summary>
    public string? PrtgOutcome { get; set; }

    /// <summary>PRTG 結局原因（disabled 早退時：未啟用／尚未設定認證資訊／憑證無法解密；舊紀錄或無原因為 null）</summary>
    public string? PrtgOutcomeNote { get; set; }

    /// <summary>PRTG 觸發式取數目標感測器數（null＝舊紀錄或 PRTG 未產出）</summary>
    public int? PrtgSensorsFetched { get; set; }

    /// <summary>這趟的取數策略（conservative | aggressive，null＝舊紀錄）；保守策略夜間不做觸發式取數，畫面不顯示 sensor 數</summary>
    public string? PrtgStrategy { get; set; }

    /// <summary>PRTG 觸發式取數失敗感測器數（null＝舊紀錄或 PRTG 未產出）</summary>
    public int? PrtgSensorsFailed { get; set; }

    /// <summary>PRTG 觸發式取數問題主機數（null＝舊紀錄或 PRTG 未產出）</summary>
    public int? PrtgTriggeredHosts { get; set; }
}

/// <summary>
/// 執行總表分頁回應（批次G）：含當頁的每日彙總清單與分頁資訊。
/// 分頁以「最新的一頁為第 1 頁」為準，每頁內部維持既有的舊→新排序，
/// 前端顯示時再 reverse 成新→舊。
/// </summary>
public class RunDaySummaryPageDto
{
    /// <summary>當頁的每日彙總列表（舊→新排序，前端 reverse 顯示）</summary>
    public List<RunDaySummaryDto> Items { get; set; } = new();

    /// <summary>總天數（Clamp 後的實際值，≤ MaxDays）</summary>
    public int TotalDays { get; set; }

    /// <summary>總頁數（= ceil(TotalDays / PageSize)）</summary>
    public int TotalPages { get; set; }

    /// <summary>目前頁碼（1 起算）</summary>
    public int Page { get; set; }

    /// <summary>每頁天數（Clamp 後的實際值）</summary>
    public int PageSize { get; set; }

    /// <summary>
    /// 可用最大天數（max(90, RunLogRetentionDays)）：「全部」按鈕的天數來源——
    /// 前端不得寫死，必須讀此欄位。
    /// </summary>
    public int MaxDays { get; set; }
}
