namespace LogForesight.Core.Models;

/// <summary>
/// 同步結果的一則警告：同步本身算完成，但發生了管理者該知道的保護生效或異常（docs/PRTG-SPEC.md §3c）。
/// <see cref="Code"/> 是機器可讀的種類，健康整體狀態據此判定；<see cref="Message"/> 是給人看的一句中文。
/// 清除與保護的規則不因警告改變——警告只出聲，不改任何清除條件。
/// </summary>
public sealed class PrtgSyncWarning
{
    /// <summary>警告文字的長度上限：文字含 PRTG 回傳的裝置名稱（外部字串，沒有長度保證），寫入執行紀錄與狀態前一律截斷</summary>
    public const int MaxMessageLength = 300;

    /// <summary>範圍內的感測器查詢全部成功，卻一顆感測器都沒回</summary>
    public const string SensorsAllEmpty = "sensors-all-empty";

    /// <summary>已對應主機的裝置本趟沒出現在 PRTG 回應中，已從鏡像清除</summary>
    public const string MappedDevicesPurged = "mapped-devices-purged";

    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>唯一的建立點：文字在這裡截斷到 <see cref="MaxMessageLength"/>。</summary>
    public static PrtgSyncWarning Create(string code, string message) => new()
    {
        Code = code,
        Message = Truncate(message, MaxMessageLength)
    };

    /// <summary>依長度截斷，超過時尾端補「…」（總長不超過 <paramref name="max"/>）。</summary>
    public static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = text.Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}

/// <summary>
/// 「同步結構與對應」最近一次執行的結果（docs/PRTG-SPEC.md §5a）。
/// 持久化在 blob，站台重啟後畫面仍看得到上次同步是什麼時候、對應成果如何。
/// 從未執行過時整個物件為 null（畫面顯示「尚未同步」）——
/// 這與「執行過但零筆」是不同的意思，不可用零值代表未執行。
/// </summary>
public class PrtgStructureSyncStatus
{
    public const string SourceManual = "manual";
    public const string SourceNightly = "nightly";

    /// <summary>同步來源（manual: 手動同步；nightly: 夜間取數）。</summary>
    public string Source { get; set; } = SourceManual;

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

    /// <summary>主機口徑（畫面只顯示這三個；上面的 device 計數保留給診斷，不呈現，docs/PRTG-SPEC.md §1）。null＝舊紀錄。</summary>
    public int? MapMappedHosts { get; set; }
    public int? MapUnmappedHosts { get; set; }
    public int? MapNameMismatch { get; set; }

    /// <summary>本趟同步的警告（成功也可能有）；沒有警告為空清單。健康整體狀態讀這份。</summary>
    public List<PrtgSyncWarning> Warnings { get; set; } = new();

    /// <summary>
    /// 把另一份結果的全部欄位抄進來（手動同步與夜間取數兩個寫入點共用）。
    /// 欄位清單只在這裡維護一份：各寫一份的話，加欄位時漏改一處不會有任何編譯或測試訊號。
    /// </summary>
    public void CopyFrom(PrtgStructureSyncStatus other)
    {
        Source = other.Source;
        CompletedAt = other.CompletedAt;
        Success = other.Success;
        ErrorMessage = other.ErrorMessage;
        ElapsedSeconds = other.ElapsedSeconds;
        Devices = other.Devices;
        Sensors = other.Sensors;
        MapDate = other.MapDate;
        MapOk = other.MapOk;
        MapManual = other.MapManual;
        MapConflict = other.MapConflict;
        MapUnmatched = other.MapUnmatched;
        MapSkippedNoIp = other.MapSkippedNoIp;
        MapSkippedExcluded = other.MapSkippedExcluded;
        MapSkippedManualSibling = other.MapSkippedManualSibling;
        MapMappedHosts = other.MapMappedHosts;
        MapUnmappedHosts = other.MapUnmappedHosts;
        MapNameMismatch = other.MapNameMismatch;
        Warnings = other.Warnings.Select(w => new PrtgSyncWarning { Code = w.Code, Message = w.Message }).ToList();
    }
}
