namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 執行監控每日彙總（§5.4 D-4）：取代舊版「主機×日期」矩陣。
/// 兩千台規模下矩陣會炸出 2000×90 格 DOM，改成每天一列的計數＋失敗主機清單
/// （上限 10 台＋「其他 N 台」），完整清單透過 <see cref="RunDayDetailHostDto"/> 點日期取得。
/// </summary>
public class RunDaySummaryDto
{
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

    /// <summary>本機分析已停用（回饋十八輪批次D）：排程設定「分析本機主機」關閉時，本機主機的
    /// 空白日不算進「未執行」——那是設定行為不是漏跑，混計會讓人誤判排程壞了。</summary>
    public int LocalDisabledCount { get; set; }

    /// <summary>優雅停止（手動停止或執行窗口 End 到點）——不算失敗，見 <c>BatchRun.Stopped</c></summary>
    public int StoppedCount { get; set; }

    /// <summary>失敗（含異常中斷）的主機名，最多 10 台；其餘用 OtherFailedCount 表示</summary>
    public List<string> FailedHostNames { get; set; } = new();
    public int OtherFailedCount { get; set; }
}

/// <summary>單一日期的逐主機狀態（點日期下鑽時取得，同一天最多 TotalHosts 筆，不是整個矩陣）</summary>
public class RunDayHostStatusDto
{
    public string HostName { get; set; } = string.Empty;

    /// <summary>success | backfilled | warning | failed | stopped | running | stuck | none | local_disabled</summary>
    public string Status { get; set; } = string.Empty;

    public long? RunId { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int DaysAnalyzed { get; set; }
    public int WarnCount { get; set; }
    public int ErrorCount { get; set; }
    public int AiFailures { get; set; }

    /// <summary>當日執行次數（手動重跑會 > 1）</summary>
    public int RunCount { get; set; }
}

public class RunDetailDto
{
    public long RunId { get; set; }
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

    public List<RunLogDto> Logs { get; set; } = new();
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
    public int Count { get; set; }
    public List<string> AffectedHosts { get; set; } = new();
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public long LatestRunId { get; set; }
}

/// <summary>執行紀錄（回饋十七輪批次F-3）：每一筆 BatchRun 原始資料的扁平清單，不像
/// <see cref="RunDaySummaryDto"/>／<see cref="RunDayHostStatusDto"/> 是按日期/主機彙總——
/// 回答的是「每一次執行本身跑了多久、觸發方式、有沒有問題」，供「執行紀錄」分頁逐筆檢視。</summary>
public class RunListItemDto
{
    public long RunId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? DurationSeconds { get; set; }

    /// <summary>success | failed | stopped | running | stuck，與 RunDayHostStatusDto.Status
    /// 同一套語意（不含 backfilled／none——那是「沒有這筆 BatchRun」的狀態，這裡每筆都存在）</summary>
    public string Status { get; set; } = string.Empty;

    public string TriggerText { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public int DaysAnalyzed { get; set; }
    public int AiCalls { get; set; }
    public int AiFailures { get; set; }
    public int WarnCount { get; set; }
    public int ErrorCount { get; set; }
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
    /// 可用最大天數（RunLogRetentionDays 設定值）：「全部」按鈕的天數來源——
    /// 前端不得寫死，必須讀此欄位。
    /// </summary>
    public int MaxDays { get; set; }
}
