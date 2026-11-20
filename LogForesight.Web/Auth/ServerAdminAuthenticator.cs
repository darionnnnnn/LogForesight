using LogForesight.Web.Configuration;

namespace LogForesight.Web.Auth;

/// <summary>serverAdmin 登入嘗試的結果</summary>
public enum ServerAdminLoginResult
{
    /// <summary>此帳號不是 serverAdmin，應交給一般流程處理</summary>
    NotServerAdmin,

    Success,

    WrongPassword
}

/// <summary>
/// serverAdmin 本地救援帳號的驗證（docs/WEB-SPEC.md §6.2）。
///
/// 防暴力破解由登入端點的 <see cref="LoginThrottle"/> 負責（依帳號與來源 IP 計失敗，所有帳號一致、可在畫面上手動解除）。
/// 這裡不另設第二套鎖定：兩套疊加時，匿名者只要輸錯幾次就能把 AD 停擺時唯一的救援入口鎖死 15 分鐘，
/// 而那時沒有其他管理者能登入解除。
/// </summary>
public class ServerAdminAuthenticator
{
    private readonly ServerAdminSettings _settings;

    public ServerAdminAuthenticator(WebAppSettings settings)
    {
        _settings = settings.Auth.ServerAdmin;
    }

    public string Account => _settings.Account;

    public bool IsServerAdmin(string account) =>
        !string.IsNullOrWhiteSpace(_settings.Account) &&
        string.Equals(account, _settings.Account, StringComparison.OrdinalIgnoreCase);

    /// <param name="requiresPassword">
    /// 目前的驗證方式是否驗密碼（＝<see cref="IAuthenticationProvider.RequiresPassword"/>）。
    /// Stub（測試）模式為 false：此時救援帳號比照一般帳號免密碼登入，讓「測試模式一律免密碼」
    /// 對所有帳號一致。正式環境強制 Ldap（true），且 <see cref="WebAppSettings.Validate"/> 會擋下
    /// 帶 Stub 上正式環境的部署，所以這條免密碼捷徑到不了正式環境。
    /// </param>
    public ServerAdminLoginResult TryLogin(string account, string? password, bool requiresPassword = true)
    {
        if (!IsServerAdmin(account)) return ServerAdminLoginResult.NotServerAdmin;

        // Stub 模式不驗密碼：直接視為成功
        if (!requiresPassword) return ServerAdminLoginResult.Success;

        return !string.IsNullOrEmpty(password) && PasswordHasher.Verify(password, _settings.PasswordHash)
            ? ServerAdminLoginResult.Success
            : ServerAdminLoginResult.WrongPassword;
    }
}
