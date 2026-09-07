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

    /// <summary>已完成的天數（不含正在處理中的那一天）；正在處理哪一天看 <see cref="CurrentDate"/></summary>
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
    public int IpExcludeCount { get; set; }
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
    public string? RemapWarning { get; set; }
    public int SameIpSkippedCount { get; set; }
}

/// <summary>刪除類操作的回應：是否真的刪到，以及重算今日對應的警告（null＝重算正常）。
/// 刪除本身已經成功，重算失敗只是「畫面上的衝突清單要等下次夜間批次才會更新」，
/// 因此不擲例外，改由這個欄位讓畫面說明清楚。</summary>
public class PrtgDeleteResultDto
{
    public bool Deleted { get; set; }
    public string? RemapWarning { get; set; }
}

/// <summary>PRTG 主機對應衝突清單分頁回應</summary>
public class PrtgHostMapPageDto
{
    public DateTime? MapDate { get; set; }
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<PrtgConflictItemDto> Items { get; set; } = new();
}

/// <summary>PRTG 主機對應衝突項目</summary>
public class PrtgConflictItemDto
{
    public long DeviceObjid { get; set; }
    public string? DeviceName { get; set; }
    public string? GroupPath { get; set; }
    public string? Ip { get; set; }
    public string? HostName { get; set; }
    public string? Note { get; set; }
    public string ConflictKind { get; set; } = string.Empty;
    public List<PrtgConflictDeviceDto> SameIpDevices { get; set; } = new();
    public List<PrtgCandidateHostDto> CandidateHosts { get; set; } = new();
}

public class PrtgConflictDeviceDto
{
    public long Objid { get; set; }
    public string? Name { get; set; }
    public string? GroupPath { get; set; }
}

public class PrtgCandidateHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
}

/// <summary>PRTG IP 排除清單項目</summary>
public class PrtgIpExcludeDto
{
    public string Ip { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? RemapWarning { get; set; }
}

/// <summary>設定 PRTG IP 排除請求</summary>
public class SetPrtgIpExcludeRequest
{
    public string Ip { get; set; } = string.Empty;
    public string? Note { get; set; }
}

/// <summary>主機 PRTG 監控對應資訊</summary>
public class HostPrtgMappingDto
{
    public DateTime? MapDate { get; set; }
    public List<HostPrtgDeviceDto> Devices { get; set; } = new();
    public bool IpExcluded { get; set; }
    public string? ExcludedIp { get; set; }
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

/// <summary>PRTG 資源守門受監看感測器預覽項目</summary>
public class PrtgResourceGuardSensorPreviewDto
{
    public long Objid { get; set; }
    public string? Device { get; set; }
    public string? Sensor { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Status { get; set; }
    public double? Percentage { get; set; }
    public string? UnmeasurableReason { get; set; }
}

/// <summary>PRTG 資源守門受監看感測器預覽結果</summary>
public class PrtgResourceGuardPreviewResultDto
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>清單來源：override（覆寫清單）或 auto（自動偵測）</summary>
    public string Source { get; set; } = "auto";
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
    public IReadOnlyList<PrtgResourceGuardSensorPreviewDto> Sensors { get; set; } = Array.Empty<PrtgResourceGuardSensorPreviewDto>();
}
