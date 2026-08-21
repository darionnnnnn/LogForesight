using System.Linq;

namespace LogForesight.Core.Models;

/// <summary>
/// 權限異動類別常數、顯示標籤對照與推導純函式。
/// </summary>
public static class PermissionCategory
{
    public const string GroupMember = "group_member";
    public const string FolderAcl = "folder_acl";
    /// <summary>非檔案物件（Token、登錄機碼…）的權限變更——4670 依物件類型與資料夾權限分流</summary>
    public const string ObjectAcl = "object_acl";
    public const string OwnerChange = "owner_change";
    public const string FolderAccess = "folder_access";
    public const string AuditPolicy = "audit_policy";
    /// <summary>不再產生的類別（某主機日的異動總計列）。既有資料仍有這種列，保留供其分類與篩選。</summary>
    public const string Summary = "summary";
    public const string Other = "other";

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GroupMember] = "群組成員異動",
        [FolderAcl] = "資料夾權限異動",
        [ObjectAcl] = "物件權限變更",
        [OwnerChange] = "擁有者變更",
        [FolderAccess] = "資料夾存取狀態",
        [AuditPolicy] = "稽核政策變更",
        [Summary] = "權限異動彙總",
        [Other] = "其他"
    };

    /// <summary>
    /// 特權群組關鍵字清單（集中定義，用於不分大小寫的包含比對）。
    /// </summary>
    public static readonly IReadOnlyList<string> PrivilegedTargetKeywords = new[]
    {
        "Administrators",
        "Domain Admins",
        "Enterprise Admins",
        "Schema Admins",
        "Account Operators",
        "Backup Operators",
        "本機 Administrators 群組"
    };

    /// <summary>檔案系統類的物件類型（4670 的「物件類型」欄）：這些才算資料夾權限異動</summary>
    private static readonly string[] FileObjectTypes = { "File", "Directory", "檔案", "目錄", "資料夾" };

    /// <summary>
    /// 依異動類型、EventId 與物件類型推導類別 key。
    /// 純函式：不依賴任何服務或狀態，對應不到時回傳 other，不拋例外。
    ///
    /// **4670 依物件類型分流**：官方語意是「物件的權限已變更」，物件可以是檔案／目錄，
    /// 也可以是 Token、登錄機碼這類與資料夾無關的核心物件。一律歸「資料夾權限異動」時，
    /// 畫面上會出現「TP-XXX$ 變更（未能解析路徑）的權限」這種對 Token 物件語意錯誤的句子。
    /// 物件類型缺漏時保留在 <see cref="FolderAcl"/>（既有資料無此欄，不改變它們的分類）。
    /// </summary>
    public static string Resolve(string? changeType, int? eventId = null, string? objectType = null)
    {
        if (changeType == "權限變更" && eventId == 4670 && !string.IsNullOrWhiteSpace(objectType))
        {
            return FileObjectTypes.Any(t => objectType.Contains(t, StringComparison.OrdinalIgnoreCase))
                ? FolderAcl
                : ObjectAcl;
        }

        return changeType switch
        {
            "成員新增" or "成員移除" => GroupMember,
            "權限新增（ACL 規則）" or "權限移除（ACL 規則）" or "權限變更" => FolderAcl,
            "擁有者變更" => OwnerChange,
            "無法存取" or "恢復可存取" => FolderAccess,
            "稽核政策變更" => AuditPolicy,
            "權限異動（彙總）" => Summary,
            _ => Other
        };
    }

    /// <summary>
    /// 取得所有類別 key 與顯示標籤對照表。
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetAllLabels() => Labels;

    /// <summary>
    /// 查詢類別 key 對應的顯示標籤。查不到對應時回傳「其他」標籤。
    /// </summary>
    public static string GetLabel(string? category)
    {
        if (category != null && Labels.TryGetValue(category, out var label))
        {
            return label;
        }
        return Labels[Other];
    }

    /// <summary>
    /// 判定是否為高風險（特權群組）異動。
    /// 同時滿足以下三條件才為 true：
    /// 1. 類別是 group_member
    /// 2. ChangeType 是「成員新增」
    /// 3. Target 命中特權群組關鍵字（不分大小寫的包含比對）
    /// </summary>
    public static bool IsPrivilegedTarget(string? target, string? changeType)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;

        // 只認「成員新增」：這個旗標的用途是提示提權（有人被加進特權群組）。
        // 不必再檢查類別——能推導成 group_member 的只有成員新增與成員移除，後者在這裡就被擋掉了。
        if (changeType != "成員新增") return false;

        for (var i = 0; i < PrivilegedTargetKeywords.Count; i++)
        {
            if (target.Contains(PrivilegedTargetKeywords[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
