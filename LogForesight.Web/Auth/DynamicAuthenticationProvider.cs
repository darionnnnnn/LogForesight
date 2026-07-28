using System.Runtime.Versioning;
using LogForesight.Web.Auth.Ldap;
using LogForesight.Web.Services;

namespace LogForesight.Web.Auth;

/// <summary>
/// 動態驗證 Provider（docs/WEB-FEEDBACK-PLAN.md #9）：取代 DI 裡依 appsettings 二選一
/// （Stub／Ldap）的固定註冊。每次驗證時讀 DB 的「系統管理 > 設定」：
/// <c>AdAuthEnabled</c> 且 <c>AdServers</c> 非空 → 用 DB 設定建 <see cref="LdapService"/> 驗證
/// （設定頁存檔即生效，不必重啟站台）；否則委派給 appsettings 決定的原 provider（Stub 或 Ldap）。
///
/// 這正是「測試模式（Stub）開啟 AD 後也走 AD 驗證」的實現方式——DB 開關可以蓋過 appsettings
/// 的 Auth:Provider，但反過來不行：<c>WebAppSettings.Validate</c> 禁止正式環境用 Stub 的欄杆
/// 維持不變（DB 開關是使用者可隨時關掉的東西，不能取代部署層的強制）。
///
/// **鎖死風險與逃生門**：管理者若在設定頁填錯 AD 伺服器，會把一般使用者全部擋在門外；
/// <c>serverAdmin</c> 本地救援帳號不經任何 Provider（<see cref="IdentityService"/> 既有的驗證順序：
/// serverAdmin 優先比對），永遠進得來——設定頁 hint 需明講這一點。
///
/// 失敗原因（帳號鎖定／密碼過期等）只進診斷 log，不對一般使用者顯示（定案 2026-07-27）：
/// <see cref="LdapCredentialVerifier"/> 已一律回「帳號或密碼錯誤」。
/// </summary>
[SupportedOSPlatform("windows")]
public class DynamicAuthenticationProvider : IAuthenticationProvider
{
    private readonly ISystemSettingsStore _settings;
    private readonly IAuthenticationProvider _fallback;
    private readonly SettingsBoundClient<AdSnapshot, LdapService> _adClient;

    public DynamicAuthenticationProvider(ISystemSettingsStore settings, IAuthenticationProvider fallback)
    {
        _settings = settings;
        _fallback = fallback;

        _adClient = new SettingsBoundClient<AdSnapshot, LdapService>(snapshot =>
            snapshot.Servers.Count == 0
                ? null
                : new LdapService(new LdapOptions
                {
                    Servers = snapshot.Servers.ToArray(),
                    SearchBase = string.IsNullOrWhiteSpace(snapshot.SearchBase) ? null : snapshot.SearchBase,
                    SearchFilter = string.IsNullOrWhiteSpace(snapshot.SearchFilter)
                        ? "(sAMAccountName={0})"
                        : snapshot.SearchFilter
                }));
    }

    /// <summary>顯示名稱依目前生效的驗證方式而定，供 /api/auth/options 與診斷 log 使用</summary>
    public string Name => IsAdEnabled(out _) ? "Ldap（AD 動態設定）" : _fallback.Name;

    public bool RequiresPassword => IsAdEnabled(out _) ? true : _fallback.RequiresPassword;

    public CredentialCheckResult Verify(string account, string? password)
    {
        if (!IsAdEnabled(out var db)) return _fallback.Verify(account, password);

        var ldap = _adClient.Get(new AdSnapshot(db.AdServers, db.AdSearchBase, db.AdSearchFilter));
        // 理論上 IsAdEnabled 已確認 AdServers 非空，這裡是防禦性 fallback，不會實際發生
        if (ldap == null) return _fallback.Verify(account, password);

        return LdapCredentialVerifier.Verify(ldap, account, password);
    }

    private bool IsAdEnabled(out SystemSettings db)
    {
        db = _settings.Get();
        return db.AdAuthEnabled && db.AdServers.Count > 0;
    }

    /// <summary>SettingsBoundClient 的快照鍵：伺服器清單＋SearchBase／Filter 任一變動都要重建</summary>
    private sealed record AdSnapshot(List<string> Servers, string SearchBase, string SearchFilter)
    {
        public bool Equals(AdSnapshot? other) =>
            other != null &&
            Servers.SequenceEqual(other.Servers) &&
            SearchBase == other.SearchBase &&
            SearchFilter == other.SearchFilter;

        public override int GetHashCode() => HashCode.Combine(Servers.Count, SearchBase, SearchFilter);
    }
}
