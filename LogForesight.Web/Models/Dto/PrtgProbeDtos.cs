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

    public int DaysDone { get; set; }
    public int DaysTotal { get; set; }
    public DateTime? CurrentDate { get; set; }
    public int SensorsDone { get; set; }
    public int SensorsTotal { get; set; }
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

/// <summary>主機 PRTG 監控對應資訊</summary>
public class HostPrtgMappingDto
{
    public DateTime? MapDate { get; set; }
    public List<HostPrtgDeviceDto> Devices { get; set; } = new();
}

/// <summary>主機對應的 PRTG 裝置資訊</summary>
public class HostPrtgDeviceDto
{
    public long DeviceObjid { get; set; }
    public string? Ip { get; set; }
    public string? MapStatus { get; set; }
    public string? Note { get; set; }
    public List<HostPrtgSensorDto> Sensors { get; set; } = new();
}

/// <summary>主機對應 PRTG 裝置的感測器資訊</summary>
public class HostPrtgSensorDto
{
    public long Objid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SensorType { get; set; } = string.Empty;
    public string? Category { get; set; }
    public bool Paused { get; set; }
}


