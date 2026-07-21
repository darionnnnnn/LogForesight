namespace LogForesight;

/// <summary>操作的結果。denied（權限不足被擋下）刻意一併記錄——見 <see cref="AuditEntry"/></summary>
public enum AuditResult
{
    Ok,

    /// <summary>權限不足被擋下的嘗試</summary>
    Denied,

    /// <summary>嘗試執行但失敗（如登入密碼錯誤）</summary>
    Failed
}

/// <summary>
/// 操作稽核紀錄（↔ lf_audit_logs，docs/WEB-SPEC.md §10.1）。**append-only**：
/// 介面只有 Append 與 Query，沒有更新或刪除的路徑——稽核表不該有「順手修正」的可能。
///
/// 記錄範圍是**寫入類操作與身分事件**，不記查詢/瀏覽：每次點擊都記會讓這張表變成流量表，
/// 真正重要的行會被淹沒。
/// </summary>
public class AuditEntry
{
    public long AuditId { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>對應的使用者；登入失敗的未知帳號、系統自動行為為 null</summary>
    public long? UserId { get; set; }

    /// <summary>
    /// 帳號字串。**刻意冗餘保存**：使用者日後停用、改名或合併後，稽核行仍要能自己讀懂，
    /// 不能因為關聯資料變了就看不出當初是誰做的。系統自動行為固定存 "(system)"。
    /// </summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>動作代碼，見 <see cref="AuditActions"/></summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>對象類型：handling / rule / user / host / group / import / auth …</summary>
    public string? TargetKind { get; set; }

    /// <summary>對象識別（規則是字串鍵，所以統一用字串容納）</summary>
    public string? TargetId { get; set; }

    /// <summary>
    /// 人讀的一句話，**在寫入當下就組好**（「將 SRV-OO-WEB01 2026-07-19 的處理人由 OOO 改為 XXX」）。
    /// 不留到查詢時從 DetailJson 反推：稽核清單要能直接讀，這也延續專案「能預先算好的
    /// 不要留到查詢時算」的原則。
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>欄位級的異動前後對照（點開才看）</summary>
    public string? DetailJson { get; set; }

    public string? IpAddress { get; set; }

    public AuditResult Result { get; set; } = AuditResult.Ok;
}

/// <summary>
/// 稽核動作代碼（docs/WEB-SPEC.md §11-1 的六類）。用常數而不是字串字面量：
/// 拼錯字會讓篩選查不到那筆紀錄，而稽核最需要的就是「查得到」。
/// </summary>
public static class AuditActions
{
    // 身分
    public const string Login = "login";
    public const string Logout = "logout";
    public const string LoginFailed = "login_failed";
    public const string SessionExpired = "session_expired";

    // 處理流程
    public const string HandlingAssign = "handling_assign";
    public const string HandlingStatus = "handling_status";
    public const string HandlingNote = "handling_note";

    // 權限異動確認
    public const string PermConfirmAuthorized = "perm_confirm_authorized";
    public const string PermConfirmSuspicious = "perm_confirm_suspicious";

    // 規則
    public const string RuleCreate = "rule_create";
    public const string RuleUpdate = "rule_update";
    public const string RuleEnable = "rule_enable";
    public const string RuleDisable = "rule_disable";
    public const string RuleRestoreSeed = "rule_restore_seed";
    public const string RuleDelete = "rule_delete";
    public const string SuppressAdd = "suppress_add";
    public const string SuppressRemove = "suppress_remove";

    // 帳號/主機/群組
    public const string UserCreate = "user_create";
    public const string UserUpdate = "user_update";
    public const string HostUpdate = "host_update";
    public const string HostMerge = "host_merge";
    public const string HostUnmerge = "host_unmerge";
    public const string GroupCreate = "group_create";
    public const string GroupUpdate = "group_update";
    public const string GroupDelete = "group_delete";
    public const string AccessGrant = "access_grant";
    public const string AccessRevoke = "access_revoke";

    // 匯入
    public const string ImportApply = "import_apply";

    /// <summary>系統自動行為的帳號值（如負責人唯一時自動帶入處理人）</summary>
    public const string SystemAccount = "(system)";
}

/// <summary>稽核查詢條件（全部為選用，null = 不限）</summary>
public class AuditQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public long? UserId { get; set; }
    public List<string>? Actions { get; set; }
    public string? TargetKind { get; set; }
    public AuditResult? Result { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>分頁結果</summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}
