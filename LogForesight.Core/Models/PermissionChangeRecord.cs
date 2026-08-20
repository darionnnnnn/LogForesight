namespace LogForesight.Core.Models;

/// <summary>
/// 權限異動的結構化紀錄（↔ lf_permission_changes，異動與確認狀態同一列，
/// 見 docs/DB-SPEC.md）。分析端寫異動、Web 端以條件式原子更新寫確認狀態。
///
/// <see cref="ChangeId"/> 用 GUID 而不是資料表的自增主鍵：它是對外（API 路由、
/// 稽核 targetId、批次請求）的識別，不能隨資料表重建而改變。
/// </summary>
public class PermissionChangeRecord
{
    public string ChangeId { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public DateTime DetectedAt { get; set; }

    /// <summary>資料夾路徑或群組名稱</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// 異動類型（共 10 個相異值）。
    /// NetIQ 事件來源：成員新增、成員移除、權限變更、稽核政策變更、權限異動（彙總）。
    /// 本機監控來源：成員新增、成員移除、無法存取、恢復可存取、擁有者變更、權限新增（ACL 規則）、權限移除（ACL 規則）。
    /// </summary>
    public string ChangeType { get; set; } = string.Empty;

    public string Before { get; set; } = string.Empty;

    public string After { get; set; } = string.Empty;

    /// <summary>批次產生的告警文字（與 console 顯示的同一行）</summary>
    public string AlertText { get; set; } = string.Empty;

    /// <summary>異動來源（本機監控／NetIQ 事件）</summary>
    public string Source { get; set; } = PermissionChangeSources.Local;

    /// <summary>原始事件 ID（NetIQ 事件填入，本機監控為 null）</summary>
    public int? EventId { get; set; }

    /// <summary>類別 key（永遠非空，預設為 other）</summary>
    public string Category { get; set; } = PermissionCategory.Other;

    /// <summary>是否為高風險（特權群組）異動，預設 false</summary>
    public bool IsPrivilegedTarget { get; set; }

    /// <summary>操作者帳號（由後續作業負責填入）</summary>
    public string? InitiatorAccount { get; set; }

    /// <summary>被異動的目標帳號／成員（由後續作業負責填入）</summary>
    public string? TargetAccount { get; set; }

    /// <summary>去重鍵（主機, 事件時間, EventId, 告警文字）——寫入端與快照端共用同一個定義</summary>
    public static string DedupeKey(string hostName, DateTime detectedAt, int eventId, string alertText) =>
        $"{hostName.ToUpperInvariant()}|{detectedAt.Ticks}|{eventId}|{alertText}";

    public string DedupeKey() => DedupeKey(HostName, DetectedAt, EventId ?? 0, AlertText);
}

public static class PermissionChangeSources
{
    public const string Local = "本機監控";
    public const string Netiq = "NetIQ 事件";
}

/// <summary>
/// 權限異動的人工確認狀態（↔ lf_permission_changes 的 confirm_* 欄位）。
///
/// 獨立於 <see cref="PermissionChangeRecord"/> 之外的原因是「單一寫入者」規則：
/// 異動由批次寫、確認由 Web 寫，各寫各的儲存 key 才不需要跨程序交易
/// （見 <see cref="PermissionChangeStore"/>）。
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
