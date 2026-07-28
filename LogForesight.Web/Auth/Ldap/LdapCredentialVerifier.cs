namespace LogForesight.Web.Auth.Ldap;

/// <summary>
/// 「用一個已建好的 LdapService 驗證帳密」的共用邏輯（docs/WEB-FEEDBACK-PLAN.md #9）。
/// <see cref="LdapAuthenticationProvider"/>（appsettings 固定設定）與
/// <see cref="DynamicAuthenticationProvider"/>（DB 動態設定）都需要同一套流程：
/// 空密碼先擋、去除網域前綴、bind、成功後順手查使用者屬性（#8）、統一的例外處理——
/// 兩個 Provider 各寫一份的話，這幾個安全細節（空密碼擋、不洩漏失敗原因給前端）遲早漂移。
/// </summary>
internal static class LdapCredentialVerifier
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public static CredentialCheckResult Verify(LdapService ldap, string account, string? password)
    {
        // AD 不接受空密碼驗證：某些 AD 設定下空密碼的 bind 會「成功」（匿名 bind），
        // 那會變成任何人輸入帳號就能登入，必須自己先擋掉。
        if (string.IsNullOrEmpty(password))
            return CredentialCheckResult.Fail("密碼為空");

        // 去掉可能的網域前綴（DOMAIN\user）：LdapService 要的是 sAMAccountName
        var samAccountName = account.Contains('\\') ? account[(account.IndexOf('\\') + 1)..] : account;

        try
        {
            var status = ldap.Authenticate(samAccountName, password);
            if (status != LdapAuthStatus.Success)
            {
                // 細分只進診斷 log（前端一律「帳號或密碼錯誤」，不洩漏帳號狀態——定案 2026-07-27）
                Log.Info("AD 驗證未通過（帳號 {0}）：{1}", account, status);
                return CredentialCheckResult.Fail($"AD 驗證未通過（{status}）");
            }

            // 驗證通過後順手取得使用者屬性（同一次帳密、同一個連線內查詢，見 #8）——
            // 查詢失敗不影響登入本身，補資料是加值不是門檻
            LdapUserInfo? userInfo = null;
            try
            {
                userInfo = ldap.GetUser(samAccountName, password);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "AD 驗證成功但查詢使用者屬性失敗（帳號 {0}），略過自動補資料：{1}", account, ex.Message);
            }

            return new CredentialCheckResult(true, UserInfo: userInfo);
        }
        catch (Exception ex)
        {
            // 網域連不上與密碼錯誤是**不同**的情況，但對前端一律回「登入失敗」（不洩漏內部狀態）；
            // 診斷 log 記完整原因，否則 AD 掛掉時只會看到一片「帳密錯誤」，查不出真正的原因。
            Log.Error(ex, "AD 驗證發生例外（帳號 {0}）", account);
            return CredentialCheckResult.Fail($"AD 驗證發生例外：{ex.Message}");
        }
    }
}
