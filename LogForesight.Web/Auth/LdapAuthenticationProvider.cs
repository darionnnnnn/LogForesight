using System.Runtime.Versioning;
using LogForesight.Web.Auth.Ldap;
using LogForesight.Web.Configuration;

namespace LogForesight.Web.Auth;

/// <summary>
/// 正式環境的 AD 驗證（appsettings 的 Auth:Provider=Ldap；docs/WEB-SPEC.md §6.2）：
/// 以使用者輸入的帳密向網域 bind，驗證通過即代表本人。
///
/// 2026-07-27 改寫（docs/HISTORY.md #9）：底層改用 <see cref="Ldap.LdapService"/>
/// （直接 DirectoryEntry bind），取代原本的 PrincipalContext——appsettings 的
/// Auth:Ldap:Domain 對應成 LdapService 的單一伺服器（LDAP:// + 網域 FQDN，Windows 會透過
/// DNS SRV 紀錄自動解析到某台網域控制站，行為與原本的 PrincipalContext(ContextType.Domain) 等價）。
/// 改寫理由：兩套 AD 驗證程式碼並存遲早漂移，且 LdapService 額外提供子錯誤碼細分與
/// GetUser（供 #8 AD 登入自動補顯示名稱／Email 使用），單靠 PrincipalContext 做不到。
/// 驗證流程本身（空密碼擋、bind、查使用者屬性、例外處理）與 <see cref="DynamicAuthenticationProvider"/>
/// 共用 <see cref="LdapCredentialVerifier"/>，不各寫一份。
///
/// **登入失敗的鎖定交由 AD 帳戶鎖定原則**，這裡不另建鎖定機制——
/// ValidateCredentials 失敗本來就會計入網域的失敗次數，達門檻由 AD 自動鎖定。
/// 一套鎖定原則、一個事實來源；Web 端再做一套只會出現「AD 說沒鎖、Web 說鎖了」的矛盾。
/// （serverAdmin 是例外：它是本地帳號、不受 AD 原則保護，所以自帶鎖定，見 ServerAdminAuthenticator。）
/// </summary>
[SupportedOSPlatform("windows")]
public class LdapAuthenticationProvider : IAuthenticationProvider
{
    private readonly LdapService _ldap;

    public LdapAuthenticationProvider(WebAppSettings settings)
    {
        var domain = settings.Auth.Ldap.Domain;
        if (string.IsNullOrWhiteSpace(domain))
            throw new InvalidOperationException("Auth:Provider=Ldap 需要設定 Auth:Ldap:Domain（AD 網域名稱）。");

        _ldap = new LdapService(new LdapOptions { Servers = new[] { domain } });
    }

    public string Name => "Ldap";

    public bool RequiresPassword => true;

    public CredentialCheckResult Verify(string account, string? password) =>
        LdapCredentialVerifier.Verify(_ldap, account, password);
}
