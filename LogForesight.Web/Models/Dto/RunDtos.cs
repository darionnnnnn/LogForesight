namespace LogForesight.Web.Models.Dto;

/// <summary>執行監控總表：列＝主機、欄＝日期</summary>
public class RunMatrixDto
{
    public List<string> Dates { get; set; } = new();
    public List<RunMatrixRowDto> Rows { get; set; } = new();
}

public class RunMatrixRowDto
{
    public string HostName { get; set; } = string.Empty;
    public List<RunCellDto> Cells { get; set; } = new();
}

public class RunCellDto
{
    public string Date { get; set; } = string.Empty;

    /// <summary>success | warning | failed | running | stuck | none</summary>
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
    public string AppVersion { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public int DaysAnalyzed { get; set; }
    public int AiCalls { get; set; }
    public int AiFailures { get; set; }
    public int WarnCount { get; set; }
    public int ErrorCount { get; set; }
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
