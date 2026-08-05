namespace LogForesight.Web.Auth;

/// <summary>
/// AD 驗證尚未於「系統管理 > 設定」頁設定時的 fallback（§12，回饋第九輪）。
///
/// §12 起 **AD 驗證的唯一事實來源是設定頁**（<c>SystemSettings.AdAuthEnabled</c>／<c>AdServers</c>，
/// 由 <see cref="DynamicAuthenticationProvider"/> 路由）；appsettings 的 <c>Auth:Ldap:Domain</c>
/// 與依賴它的 <c>LdapAuthenticationProvider</c> 已退役——兩套 AD 設定並存遲早漂移，
/// 且設定頁那套可即時調整、附連線測試，檔案那套要改設定檔重啟站台。
///
/// 這個 provider 的職責只有一件事：在 AD 尚未設定時，把「登不進去的原因」講清楚。
/// **serverAdmin 不受影響**——它不經任何 Provider（見 <see cref="Services.IdentityService.Login"/>），
/// 這正是本地救援帳號存在的理由：全新部署、或 AD 設定壞掉時，仍有人進得來把設定補上。
/// </summary>
public class UnconfiguredAdAuthenticationProvider : IAuthenticationProvider
{
    public string Name => "Ad";

    /// <summary>照常要求密碼：登入頁行為不因 AD 未設定而改變（也不對外洩漏設定狀態）</summary>
    public bool RequiresPassword => true;

    public CredentialCheckResult Verify(string account, string? password) =>
        CredentialCheckResult.Fail(
            "AD 驗證尚未設定。請以本機救援帳號（serverAdmin）登入後，至「系統管理 > 設定」頁啟用 AD 驗證並填入伺服器位址。");
}
