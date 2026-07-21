namespace LogForesight;

/// <summary>
/// 權限異動的結構化紀錄（↔ lf_permission_changes）。
///
/// **雙軌寫入**（docs/WEB-SPEC.md §2.1 Phase 3）：批次的既有 console 告警與
/// export txt 報告照舊，另外把每一筆異動明細寫成結構化資料供 Web 的待辦頁使用。
/// 沒有這一軌，權限異動待辦頁在 JSONL 前期就沒有任何資料可顯示。
///
/// <see cref="ChangeId"/> 用 GUID 而不是遞增數字：批次與 Web 分別寫入不同檔案
/// （異動由批次寫、確認狀態由 Web 寫），沒有共用的序號來源。
/// </summary>
public class PermissionChangeRecord
{
    public string ChangeId { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public DateTime DetectedAt { get; set; }

    /// <summary>資料夾路徑或群組名稱</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>成員新增/成員移除/擁有者變更/權限新增/權限移除/無法存取</summary>
    public string ChangeType { get; set; } = string.Empty;

    public string Before { get; set; } = string.Empty;

    public string After { get; set; } = string.Empty;

    /// <summary>批次產生的告警文字（與 console 顯示的同一行）</summary>
    public string AlertText { get; set; } = string.Empty;
}

/// <summary>
/// 權限異動的人工確認狀態（↔ lf_permission_changes 的 confirm_* 欄位）。
///
/// 獨立於 <see cref="PermissionChangeRecord"/> 之外的原因是 JSONL 後端的
/// 「單一寫入者」規則：異動由批次寫、確認由 Web 寫，各寫各的檔案才不需要跨程序交易。
/// SQL 後端會合併成同一張表的欄位。
/// </summary>
public class PermissionChangeConfirmation
{
    public string ChangeId { get; set; } = string.Empty;

    /// <summary>pending | authorized | suspicious</summary>
    public string Status { get; set; } = PermissionConfirmStatuses.Pending;

    public long? ConfirmedBy { get; set; }

    public string ConfirmedByAccount { get; set; } = string.Empty;

    public DateTime? ConfirmedAt { get; set; }

    public string? Note { get; set; }
}

public static class PermissionConfirmStatuses
{
    public const string Pending = "pending";
    public const string Authorized = "authorized";
    public const string Suspicious = "suspicious";

    public static bool IsValid(string status) =>
        status is Pending or Authorized or Suspicious;
}
