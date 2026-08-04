using System.DirectoryServices;

namespace LogForesight.Web.Auth.Ldap;

/// <summary>
/// LDAP 連線與查詢設定（docs/archive/HISTORY.md #9）。Servers／SearchBase／SearchFilter
/// 對應「系統管理 > 設定」頁的 AdServers／AdSearchBase／AdSearchFilter；其餘欄位（使用者屬性
/// 對應、bind 驗證方式）目前固定用 AD 慣例值，未來若要支援 OpenLDAP 等其他目錄再開放到 UI。
/// </summary>
public sealed class LdapOptions
{
    /// <summary>一個以上的 LDAP 伺服器位址 (IP 或 URL)，依序嘗試第一個可連線者。</summary>
    public string[] Servers { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 查詢基準 DN (Base DN)，例如 "DC=company,DC=com" 或 "OU=Users,DC=company,DC=com"。
    /// 留空則使用目錄預設根目錄。
    /// </summary>
    public string? SearchBase { get; set; }

    /// <summary>
    /// 查詢使用者的過濾器樣板，{0} 會被代入登入帳號。
    /// AD:"(sAMAccountName={0})"；OpenLDAP:"(uid={0})"；UPN:"(userPrincipalName={0})"。
    /// </summary>
    public string SearchFilter { get; set; } = "(sAMAccountName={0})";

    /// <summary>對應 <see cref="LdapUserInfo.UserId"/> 的屬性名稱。</summary>
    public string UserIdAttribute { get; set; } = "sAMAccountName";

    /// <summary>顯示名稱屬性，依序取第一個有值者 (AD 常見 displayName、name；OpenLDAP 常見 cn)。</summary>
    public string[] DisplayNameAttributes { get; set; } = { "displayName", "name" };

    /// <summary>Email 屬性名稱。</summary>
    public string EmailAttribute { get; set; } = "mail";

    /// <summary>bind 驗證方式，預設 Secure。</summary>
    public AuthenticationTypes AuthenticationType { get; set; } = AuthenticationTypes.Secure;
}
