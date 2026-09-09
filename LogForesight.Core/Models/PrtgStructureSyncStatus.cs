namespace LogForesight.Core.Models;

/// <summary>
/// 「同步結構與對應」最近一次執行的結果（docs/PRTG-SPEC.md §5a）。
/// 持久化在 blob，站台重啟後畫面仍看得到上次同步是什麼時候、對應成果如何。
/// 從未執行過時整個物件為 null（畫面顯示「尚未同步」）——
/// 這與「執行過但零筆」是不同的意思，不可用零值代表未執行。
/// </summary>
public class PrtgStructureSyncStatus
{
    /// <summary>最近一次執行結束的時間（成功或失敗都記）。</summary>
    public DateTime CompletedAt { get; set; }

    /// <summary>最近一次是否成功。</summary>
    public bool Success { get; set; }

    /// <summary>失敗時的訊息；成功為 null。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>耗時秒數。</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>同步到的裝置數。</summary>
    public int Devices { get; set; }

    /// <summary>同步到的感測器數。</summary>
    public int Sensors { get; set; }

    /// <summary>對應的目標日期。</summary>
    public DateTime MapDate { get; set; }

    public int MapOk { get; set; }
    public int MapManual { get; set; }
    public int MapConflict { get; set; }
    public int MapUnmatched { get; set; }
    public int MapSkippedNoIp { get; set; }
    public int MapSkippedExcluded { get; set; }
    public int MapSkippedManualSibling { get; set; }
}
