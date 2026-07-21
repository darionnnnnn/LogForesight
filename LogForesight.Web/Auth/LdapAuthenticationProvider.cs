using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using LogForesight.Web.Configuration;

namespace LogForesight.Web.Auth;

/// <summary>
/// 正式環境的 AD 驗證（docs/WEB-SPEC.md §6.2）：以使用者輸入的帳密向網域 bind，
/// 驗證通過即代表本人。
///
/// **登入失敗的鎖定交由 AD 帳戶鎖定原則**，這裡不另建鎖定機制——
/// ValidateCredentials 失敗本來就會計入網域的失敗次數，達門檻由 AD 自動鎖定。
/// 一套鎖定原則、一個事實來源；Web 端再做一套只會出現「AD 說沒鎖、Web 說鎖了」的矛盾。
/// （serverAdmin 是例外：它是本地帳號、不受 AD 原則保護，所以自帶鎖定，見 ServerAdminAuthenticator。）
/// </summary>
[SupportedOSPlatform("windows")]
public class LdapAuthenticationProvider : IAuthenticationProvider
{
    private readonly string _domain;
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public LdapAuthenticationProvider(WebAppSettings settings)
    {
        _domain = settings.Auth.Ldap.Domain;
        if (string.IsNullOrWhiteSpace(_domain))
            throw new InvalidOperationException("Auth:Provider=Ldap 需要設定 Auth:Ldap:Domain（AD 網域名稱）。");
    }

    public string Name => "Ldap";

    public bool RequiresPassword => true;

    public CredentialCheckResult Verify(string account, string? password)
    {
        // AD 不接受空密碼驗證：某些 AD 設定下空密碼的 bind 會「成功」（匿名 bind），
        // 那會變成任何人輸入帳號就能登入，必須自己先擋掉。
        if (string.IsNullOrEmpty(password))
            return CredentialCheckResult.Fail("密碼為空");

        try
        {
            using var context = new PrincipalContext(ContextType.Domain, _domain);

            // 去掉可能的網域前綴（DOMAIN\user）：ValidateCredentials 要的是 sAMAccountName
            var samAccountName = account.Contains('\\') ? account[(account.IndexOf('\\') + 1)..] : account;

            return context.ValidateCredentials(samAccountName, password)
                ? CredentialCheckResult.Ok
                : CredentialCheckResult.Fail("AD 驗證未通過");
        }
        catch (Exception ex)
        {
            // 網域連不上與密碼錯誤是**不同**的情況，但對前端一律回「登入失敗」（不洩漏內部狀態）；
            // 診斷 log 記完整原因，否則 AD 掛掉時只會看到一片「帳密錯誤」，查不出真正的原因。
            Log.Error(ex, "AD 驗證發生例外（帳號 {0}，網域 {1}）", account, _domain);
            return CredentialCheckResult.Fail($"AD 驗證發生例外：{ex.Message}");
        }
    }
}
