using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;

namespace LogForesight.Tests;

/// <summary>
/// 舊版 V1/V2 匯入檔格式，僅供驗證舊檔被正確拒絕／辨識。
/// 正式碼已不再讀寫這個型別（匯出走串流寫入器、舊單次匯入入口回 410）。
/// </summary>
public sealed class PrtgDataPackage
{
    public int FormatVersion { get; set; } = PrtgDataTransfer.CurrentFormatVersion;
    public DateTime ExportedAt { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<PrtgDeviceRow> Devices { get; set; } = new();
    public List<PrtgSensorRow> Sensors { get; set; } = new();
    public List<PrtgStateChangeRow> StateChanges { get; set; } = new();
    public List<PrtgValueRow> Values { get; set; } = new();
    public List<PrtgHostMapRow> HostMaps { get; set; } = new();
    public List<PrtgManualMapRow> ManualMaps { get; set; } = new();
    public string Purpose { get; set; } = "diagnostic-only";
    public PrtgMonitoringPolicy? SourcePolicy { get; set; }
}
