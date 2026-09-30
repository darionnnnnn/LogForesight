namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 獨立保存 PRTG 判定快照；不依賴日誌日紀錄。影子版本尚不進正式問題／派工／通知。
/// SnapshotId 是內容去重鍵，不是事故或來源身分；未知世代保持 null。
/// </summary>
public sealed class PrtgObservationRow
{
    public string SnapshotId { get; set; } = string.Empty;
    /// <summary>同日同來源提示／主機／資源／語意規則的暫定判定鍵；不代表可信事故鍵。</summary>
    public string DecisionKey { get; set; } = string.Empty;
    /// <summary>只有目前有效版本填入 DecisionKey；資料庫唯一索引防止多個有效版本。</summary>
    public string? ActiveKey { get; set; }
    public long HostId { get; set; }
    public DateTime RecordDate { get; set; }
    public long DeviceObjid { get; set; }
    public long? SensorObjid { get; set; }
    public string RuleCode { get; set; } = string.Empty;
    public string EventKey { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int SeverityRank { get; set; }
    public bool ElevatesDayRisk { get; set; }
    public bool Suppressed { get; set; }
    public string? SourceGeneration { get; set; }
    /// <summary>來源位址的遮蔽摘要，供辨別明確不同的端點；不證明同一 Core。</summary>
    public string SourceHint { get; set; } = string.Empty;
    public string? ResourceGeneration { get; set; }
    public string QualityReason { get; set; } = string.Empty;
    public int FormatVersion { get; set; }
    public string ContentJson { get; set; } = string.Empty;
    public DateTime RecordedAtUtc { get; set; }
}
