namespace LogForesight.Web.Auth.Ldap;

/// <summary>
/// 從 LDAP 取得的使用者資訊（docs/WEB-FEEDBACK-PLAN.md #8：AD 登入自動補顯示名稱與 Email）。
/// </summary>
public sealed record LdapUserInfo(
    string UserId,
    string DisplayName,
    string? Email,
    string DistinguishedName,
    bool IsLocked);
