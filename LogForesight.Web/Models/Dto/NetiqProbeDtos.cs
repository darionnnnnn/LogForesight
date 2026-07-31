namespace LogForesight.Web.Models.Dto;

/// <summary>NetIQ 診斷（probe，docs/WEB-SCHEDULER-PLAN.md §1.4.11）狀態，供輪詢用</summary>
public class NetiqProbeStatusDto
{
    public bool IsRunning { get; set; }
    public long? SentinelId { get; set; }
    public string? SentinelName { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool? Success { get; set; }
    public string? LatestMessage { get; set; }

    /// <summary>累積至今的完整診斷文字——執行中也持續更新，可讓前端做「即時 tail」的效果</summary>
    public string Output { get; set; } = string.Empty;
}

public class StartNetiqProbeRequest
{
    public long SentinelId { get; set; }

    /// <summary>一台已知的 Windows 主機 IP，用於核對主機歸屬鍵／頻道覆蓋／dt 邊界。選填</summary>
    public string? SampleIp { get; set; }

    /// <summary>一台已知的 Linux 主機 IP，用於核對 Linux 事件欄位形狀。選填</summary>
    public string? SampleLinuxIp { get; set; }
}

public class StartNetiqProbeResultDto
{
    public bool Started { get; set; }
}
