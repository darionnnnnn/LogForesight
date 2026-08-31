namespace LogForesight.Web.Models.Dto;

/// <summary>PRTG 探測（probe）狀態，供前端輪詢用</summary>
public class PrtgProbeStatusDto
{
    public bool IsRunning { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool? Success { get; set; }
    public string? LatestMessage { get; set; }
    public IReadOnlyList<string> Output { get; set; } = Array.Empty<string>();
}

/// <summary>啟動 PRTG 探測回應</summary>
public class StartPrtgProbeResultDto
{
    public bool Started { get; set; }
    public string? Error { get; set; }
}
