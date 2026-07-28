namespace LogForesight.Web.Auth.Ldap;

/// <summary>
/// LDAP 帳密驗證結果（docs/HISTORY.md #9）。
/// AD 對多種情況都回傳同一個錯誤碼 (0x52E)，需再解析伺服器端子錯誤碼才能區分。
///
/// 這些細分只進診斷 log 與稽核 detail，**不對一般使用者顯示**（定案 2026-07-27）——
/// 前端一律「帳號或密碼錯誤」，不洩漏帳號狀態，只有「測試連線」鈕（管理者對自己測試）例外。
/// </summary>
public enum LdapAuthStatus
{
    /// <summary>驗證成功。</summary>
    Success,

    /// <summary>帳號或密碼錯誤。</summary>
    InvalidCredentials,

    /// <summary>找不到此帳號。</summary>
    UserNotFound,

    /// <summary>帳號已停用。</summary>
    AccountDisabled,

    /// <summary>帳號已被鎖定。</summary>
    AccountLocked,

    /// <summary>帳號已到期。</summary>
    AccountExpired,

    /// <summary>密碼已過期。</summary>
    PasswordExpired,

    /// <summary>需於下次登入時變更密碼。</summary>
    MustChangePassword,

    /// <summary>不允許於此時間 / 此工作站登入。</summary>
    LogonNotAllowed,

    /// <summary>無法連線至任何 LDAP 伺服器。</summary>
    ServerUnavailable,

    /// <summary>其他未分類的錯誤。</summary>
    Unknown
}
