using LogForesight.Core.Persistence;

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

    /// <summary>狀態變更逐裝置查詢已完成台數（欄位名沿用舊稱）</summary>
    public int StateChangesRead { get; set; }
    /// <summary>狀態變更要查詢的總台數（0＝尚未開始）</summary>
    public int StateChangesTotal { get; set; }
    /// <summary>是否正在查狀態變更（逐日數值開始前）</summary>
    public bool ReadingStateChanges { get; set; }
    /// <summary>最近一趟是否被使用者停止</summary>
    public bool Cancelled { get; set; }
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
    public DateTime? SnapshotLastAt { get; set; }
    public int SnapshotSensors { get; set; }
    public int SnapshotIntervalMinutes { get; set; }
    public int SnapshotConsecutiveFailures { get; set; }
    public bool SnapshotBackingOff { get; set; }
    public string? SnapshotSkipReason { get; set; }
    /// <summary>各類資料最後一次成功擷取的紀錄（鏡像頁「擷取紀錄」表）</summary>
    public List<PrtgFreshnessDto> Freshness { get; set; } = new();
}

/// <summary>
/// PRTG 單一類別資料的擷取新鮮度（鏡像頁與系統健康頁共用）。
/// 資料時間推導的「最後同步」在連續擷取 0 筆時不會變、看起來正常；這裡是擷取本身的紀錄。
/// </summary>
public class PrtgFreshnessDto
{
    /// <summary>devices｜sensors｜state_changes｜snapshot｜values</summary>
    public string Category { get; set; } = string.Empty;
    public DateTime LastSuccessAt { get; set; }
    public int LastCount { get; set; }
    /// <summary>連續取得 0 筆的次數（畫面「連續 N 次取得 0 筆」的 N）</summary>
    public int ZeroStreak { get; set; }
    public bool Suspicious { get; set; }

    private static readonly string[] CategoryOrder =
    {
        PrtgFreshnessStore.Devices, PrtgFreshnessStore.Sensors, PrtgFreshnessStore.StateChanges,
        PrtgFreshnessStore.Snapshot, PrtgFreshnessStore.Values
    };

    /// <summary>依固定類別順序列出已有紀錄的類別（從未成功擷取過的類別不列）</summary>
    public static List<PrtgFreshnessDto> FromStore(PrtgFreshnessStore store)
    {
        var all = store.GetAll();
        return CategoryOrder
            .Where(all.ContainsKey)
            .Select(c => new PrtgFreshnessDto
            {
                Category = c,
                LastSuccessAt = all[c].LastSuccessAt,
                LastCount = all[c].LastCount,
                ZeroStreak = all[c].ZeroStreak,
                Suspicious = PrtgFreshnessStore.IsSuspicious(all[c])
            })
            .ToList();
    }
}

/// <summary>監看範圍外資料清除的預覽（PRTG 維護頁「鏡像狀態」頁籤）</summary>
public class PrtgScopePurgePreviewDto
{
    /// <summary>false＝範圍不可信或資料存放區未啟用，<see cref="ErrorMessage"/> 說明原因，不可確認清除</summary>
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>目前的監看裝置數</summary>
    public int MonitoredDevices { get; set; }
    /// <summary>將刪除的數值列數</summary>
    public int Values { get; set; }
    /// <summary>將刪除的狀態變更列數</summary>
    public int StateChanges { get; set; }
    /// <summary>受影響裝置數（感測器鏡像對得到裝置的部分）</summary>
    public int AffectedDevices { get; set; }
    /// <summary>受影響裝置前 20 台的名稱（外部字串，前端一律 textContent）</summary>
    public List<string> TopDeviceNames { get; set; } = new();
    /// <summary>感測器鏡像已沒有、對不到裝置的 sensor 數（它們的列同樣會被刪）</summary>
    public int UnknownSensors { get; set; }
    /// <summary>最近一次自動清除被擋下的原因（縮小保護／無基準）；成功清除後為 null</summary>
    public string? BlockedReason { get; set; }
    public DateTime? BlockedAt { get; set; }
    /// <summary>基準（上次成功清除）時間；null＝尚無基準</summary>
    public DateTime? BaselineAt { get; set; }
    public int BaselineDeviceCount { get; set; }
}

/// <summary>監看範圍外資料清除的結果</summary>
public class PrtgScopePurgeResultDto
{
    public int Values { get; set; }
    public int StateChanges { get; set; }
    public int MonitoredDevices { get; set; }
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
    /// <summary>PRTG 裝置名稱；鏡像表對不到時為 null</summary>
    public string? Name { get; set; }
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
    /// <summary>PRTG sensor 狀態字串（例如 Up、Down、Down (Acknowledged)）</summary>
    public string? Status { get; set; }
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

/// <summary>
/// 取數範圍的規模估算（docs/PRTG-SPEC.md §3a）：讓管理者在把範圍放寬之前
/// 先看得到「這樣一晚要抓幾個 sensor」。放寬到全部主機在大型環境跑不完，
/// 而跑不完的症狀是隔天早上資料不全，不是當下報錯。
/// </summary>
public class PrtgValueFetchScopeEstimateDto
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>估算的範圍模式（回傳查詢帶的值，方便前端對照）</summary>
    public string Scope { get; set; } = "";

    /// <summary>該模式涵蓋的主機數（以最新一日的 ok 對應計算）</summary>
    public int Hosts { get; set; }

    /// <summary>對應到的 device 數</summary>
    public int Devices { get; set; }

    /// <summary>套用 sensor type 白名單後的 sensor 數＝一晚要抓的量</summary>
    public int Sensors { get; set; }

    /// <summary>白名單是否為空（空＝不限 type，與 all-mapped 疊加就是全量）</summary>
    public bool WhitelistEmpty { get; set; }

    /// <summary>估算量超過建議上限時的提醒文字；未超過為 null</summary>
    public string? Warning { get; set; }

    /// <summary>快照目標感測器數量（不受取數範圍影響）</summary>
    public int SnapshotTargets { get; set; }

    /// <summary>快照每天產生的列數（Targets * 24）</summary>
    public long SnapshotRowsPerDay { get; set; }

    /// <summary>快照保留天數（PRTG 保留天數與全站保留天數之較小者）</summary>
    public int SnapshotRetentionDays { get; set; }

    /// <summary>快照在保留期內累積的總列數</summary>
    public long SnapshotRowsAtRetention { get; set; }

    /// <summary>快照規模警示（白名單為空或累積列數超標時提示）</summary>
    public string? SnapshotWarning { get; set; }
}

/// <summary>「同步結構與對應」的狀態（docs/PRTG-SPEC.md §5a）。Last* 全為 null 代表從未執行過。</summary>
public class PrtgStructureSyncStatusDto
{
    public bool IsRunning { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? LatestMessage { get; set; }
    public IReadOnlyList<string> Output { get; set; } = Array.Empty<string>();

    public string? ProgressPhase { get; set; }
    public int ProgressDone { get; set; }
    public int ProgressTotal { get; set; }

    public DateTime? LastCompletedAt { get; set; }
    public bool? LastSuccess { get; set; }
    public string? LastErrorMessage { get; set; }
    public double? LastElapsedSeconds { get; set; }
    public int? LastDevices { get; set; }
    public int? LastSensors { get; set; }
    public DateTime? LastMapDate { get; set; }
    public int? LastMapOk { get; set; }
    public int? LastMapManual { get; set; }
    public int? LastMapConflict { get; set; }
    public int? LastMapUnmatched { get; set; }
    public int? LastMapSkipped { get; set; }
    public string? LastSource { get; set; }
}

/// <summary>啟動「同步結構與對應」的回應</summary>
public class StartPrtgStructureSyncResultDto
{
    public bool Started { get; set; }
    public string? Error { get; set; }
}
