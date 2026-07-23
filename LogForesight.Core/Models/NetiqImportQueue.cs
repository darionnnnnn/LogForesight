namespace LogForesight;

/// <summary>
/// NetIQ 匯入排程佇列（↔ webdata\netiq_import_queue.json，docs/SCALE-2000-PLAN.md §5.3 D-3）。
///
/// Web 掃描/勾選後不再直接落盤主機異動——「套用」改成「排入匯入」，寫一筆這裡；
/// 實際的主機異動由批次每次執行開頭處理（或手動 <c>--apply-netiq-imports</c>）。
/// 這樣兩千台量級下主機停用/啟用集中在批次時段一次落盤，不會跟白天 Web 操作或
/// 正在跑的批次互踩（見 <see cref="NetiqImportApplier"/>）。
///
/// <see cref="RequestedByAccount"/> 保留排入當下的操作人——即使實際落盤延後到批次執行，
/// 稽核紀錄仍能歸戶到真正提出這次匯入的人，不會因為「誰執行了批次」而失真。
/// </summary>
public class NetiqImportQueueEntry
{
    public string QueueId { get; set; } = Guid.NewGuid().ToString("N");

    public string ServerName { get; set; } = string.Empty;

    /// <summary>使用者勾選要匯入的 IP（＝HostName，與既有 NetiqImportRequest 同一套鍵）</summary>
    public List<string> SelectedIps { get; set; } = new();

    public string RequestedByAccount { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; }

    /// <summary>pending | applied | failed | cancelled</summary>
    public string Status { get; set; } = NetiqImportQueueStatuses.Pending;

    public DateTime? AppliedAt { get; set; }

    public int Added { get; set; }
    public int Updated { get; set; }
    public int Revived { get; set; }

    /// <summary>Status=failed 時的原因，畫面直接顯示</summary>
    public string? FailureReason { get; set; }
}

public static class NetiqImportQueueStatuses
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
