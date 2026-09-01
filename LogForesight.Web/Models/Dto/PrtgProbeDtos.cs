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

/// <summary>PRTG 歷史回填狀態，供前端輪詢用</summary>
public class PrtgBackfillStatusDto
{
    public bool IsRunning { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool? Success { get; set; }
    public string? LatestMessage { get; set; }
    public IReadOnlyList<string> Output { get; set; } = Array.Empty<string>();
}

/// <summary>啟動 PRTG 歷史回填回應</summary>
public class StartPrtgBackfillResultDto
{
    public bool Started { get; set; }
    public string? Error { get; set; }
}

/// <summary>PRTG 主機對應項目簡要資訊</summary>
public record PrtgHostMapItemDto(long DeviceObjid, string? Ip, string? HostName, string? Note);

/// <summary>PRTG 鏡像與主機對應狀態資訊</summary>
public class PrtgMirrorStatusDto
{
    public int DeviceCount { get; set; }
    public int SensorCount { get; set; }
    public DateTime? LastDeviceSync { get; set; }
    public DateTime? LastSensorSync { get; set; }
    public DateTime? LastValueAt { get; set; }
    public DateTime? LastStateChangeAt { get; set; }
    public DateTime? MapDate { get; set; }
    public int MapOk { get; set; }
    public int MapConflict { get; set; }
    public int MapUnmatched { get; set; }
    public int WhitelistSensorCount { get; set; }
    public int OnMappedDeviceCount { get; set; }
    public IReadOnlyList<PrtgHostMapItemDto> Conflicts { get; set; } = Array.Empty<PrtgHostMapItemDto>();
    public IReadOnlyList<PrtgHostMapItemDto> Unmatched { get; set; } = Array.Empty<PrtgHostMapItemDto>();
}

/// <summary>設定 PRTG 人工主機對應請求</summary>
public class SetPrtgManualMapRequest
{
    public long DeviceObjid { get; set; }
    public long HostId { get; set; }
    public string? Note { get; set; }
}

/// <summary>PRTG 人工主機對應項目</summary>
public class PrtgManualMapDto
{
    public long DeviceObjid { get; set; }
    public long HostId { get; set; }
    public string? HostName { get; set; }
    public string? Note { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

