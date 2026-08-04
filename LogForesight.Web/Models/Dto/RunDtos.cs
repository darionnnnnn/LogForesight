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
    public int WarningCount { get; set; }
    public int FailedCount { get; set; }
    public int StuckCount { get; set; }
    public int RunningCount { get; set; }
    public int NotRunCount { get; set; }

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

    /// <summary>success | warning | failed | stopped | running | stuck | none</summary>
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

    /// <summary>「誰跑的」（docs/WEB-SCHEDULER-PLAN.md §1.4.4）：排程／手動（含帳號）／工作排程器
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
