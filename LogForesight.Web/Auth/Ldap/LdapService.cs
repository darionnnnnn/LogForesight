using System.DirectoryServices;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace LogForesight.Web.Auth.Ldap;

/// <summary>
/// LDAP 共用服務（docs/WEB-FEEDBACK-PLAN.md #9）：以使用者自己的帳號密碼進行驗證，並可取得其資訊。
/// 移植自使用者提供的參考實作，調整為專案慣例（NLog、命名空間）；邏輯不變。
///
/// **不需要儲存任何 AD 服務帳號密碼**——bind 一律用登入者自己的帳密，這也是「查得到自己的
/// displayName/mail」（見 GetUser）之所以可行、卻不必保管服務帳號的原因：查詢時用的就是
/// 剛驗證過的同一個帳密，同一次連線內執行。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LdapService
{
    // LDAP bind 失敗的 Win32 錯誤碼 (ERROR_LOGON_FAILURE)。
    private const int ErrorLogonFailure = unchecked((int)0x8007052E);

    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly LdapOptions _options;
    private readonly IReadOnlyList<string> _servers;

    /// <param name="options">LDAP 連線與查詢設定。</param>
    public LdapService(LdapOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.Servers == null || options.Servers.Length == 0)
            throw new ArgumentException("至少需提供一個 LDAP 伺服器位址。", nameof(options));

        _servers = options.Servers.Where(s => !string.IsNullOrWhiteSpace(s)).Select(NormalizeServer).ToList();
        if (_servers.Count == 0)
            throw new ArgumentException("至少需提供一個有效的 LDAP 伺服器位址。", nameof(options));
    }

    /// <summary>
    /// 驗證帳號密碼並回傳詳細結果。可藉此分辨「密碼真的錯」與
    /// 「密碼正確但帳號有其他狀態(過期/需變更/停用/鎖定)」。
    /// </summary>
    public LdapAuthStatus Authenticate(string userId, string password) =>
        Authenticate(userId, password, out _);

    /// <summary>
    /// 以使用者自己的帳密驗證並取得其資訊。
    /// 驗證未成功或查無資料時回傳 null。
    /// </summary>
    public LdapUserInfo? GetUser(string userId, string password) =>
        Query(userId, password, BuildUserInfo);

    /// <summary>
    /// 以使用者自己的帳密驗證後，對其 <see cref="DirectoryEntry"/> 執行自訂投影，
    /// 讓呼叫端能自由擴充所需欄位/邏輯。驗證未成功或查無資料時回傳 default。
    /// </summary>
    public TResult? Query<TResult>(string userId, string password, Func<DirectoryEntry, TResult> projector)
    {
        if (projector == null) throw new ArgumentNullException(nameof(projector));

        var status = Authenticate(userId, password, out var root);
        using (root)
        {
            if (status != LdapAuthStatus.Success || root == null) return default;

            using var searcher = new DirectorySearcher(root, BuildFilter(userId));
            var result = searcher.FindOne();
            if (result == null) return default;

            using var entry = result.GetDirectoryEntry();
            return projector(entry);
        }
    }

    private LdapUserInfo BuildUserInfo(DirectoryEntry entry)
    {
        var name = string.Empty;
        foreach (var attr in _options.DisplayNameAttributes)
        {
            name = GetString(entry, attr);
            if (!string.IsNullOrEmpty(name)) break;
        }

        var dn = GetString(entry, "distinguishedName");
        if (string.IsNullOrEmpty(dn)) dn = entry.Path;

        return new LdapUserInfo(
            GetString(entry, _options.UserIdAttribute),
            name,
            GetString(entry, _options.EmailAttribute),
            dn,
            IsLockedOut(entry));
    }

    /// <summary>
    /// 依序嘗試各伺服器，使用指定帳密進行 bind。
    /// </summary>
    /// <param name="entry">驗證成功時輸出已連線的 <see cref="DirectoryEntry"/>，呼叫端需負責 Dispose。</param>
    private LdapAuthStatus Authenticate(string userId, string password, out DirectoryEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrEmpty(password))
            return LdapAuthStatus.InvalidCredentials;

        var lastStatus = LdapAuthStatus.ServerUnavailable;

        foreach (var server in _servers)
        {
            var candidate = new DirectoryEntry(BuildPath(server), userId, password, _options.AuthenticationType);
            try
            {
                // 存取 NativeObject 會觸發實際的 LDAP bind，藉此驗證帳密。
                _ = candidate.NativeObject;
                entry = candidate;
                return LdapAuthStatus.Success;
            }
            catch (DirectoryServicesCOMException ex) when (ex.ErrorCode == ErrorLogonFailure)
            {
                // bind 失敗：可能是帳密錯，也可能密碼正確但帳號有其他狀態。
                candidate.Dispose();
                // 帳密相同，換伺服器結果也一樣，直接回傳解析後的原因。
                return MapBindFailure(ex);
            }
            catch (DirectoryServicesCOMException ex)
            {
                // 連線失敗，改試下一台伺服器。
                Log.Debug(ex, "LDAP 伺服器 {0} 連線失敗，改試下一台。", server);
                candidate.Dispose();
                lastStatus = LdapAuthStatus.ServerUnavailable;
            }
        }

        return lastStatus;
    }

    // AD 於 bind 失敗訊息中夾帶的子錯誤碼，例如 "... data 52e, ..."。
    private static readonly Regex AdSubCodeRegex =
        new(@"data\s+([0-9a-fA-F]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 解析 AD bind 失敗訊息中的子錯誤碼，對應到更精確的狀態。
    /// 參考：https://ldapwiki.com/wiki/Common%20Active%20Directory%20Bind%20Errors
    /// </summary>
    private static LdapAuthStatus MapBindFailure(DirectoryServicesCOMException ex)
    {
        var match = AdSubCodeRegex.Match(ex.Message ?? string.Empty);
        if (!match.Success) return LdapAuthStatus.InvalidCredentials;

        return match.Groups[1].Value.ToLowerInvariant() switch
        {
            "525" => LdapAuthStatus.UserNotFound,
            "52e" => LdapAuthStatus.InvalidCredentials,
            "530" => LdapAuthStatus.LogonNotAllowed,       // 不允許此時間登入
            "531" => LdapAuthStatus.LogonNotAllowed,       // 不允許此工作站登入
            "532" => LdapAuthStatus.PasswordExpired,       // 密碼過期
            "533" => LdapAuthStatus.AccountDisabled,       // 帳號停用
            "701" => LdapAuthStatus.AccountExpired,        // 帳號到期
            "773" => LdapAuthStatus.MustChangePassword,    // 需變更密碼
            "775" => LdapAuthStatus.AccountLocked,         // 帳號鎖定
            _ => LdapAuthStatus.InvalidCredentials
        };
    }

    /// <summary>將查詢過濾器樣板代入 (已跳脫) 帳號。</summary>
    private string BuildFilter(string userId) =>
        string.Format(_options.SearchFilter, EscapeFilter(userId));

    /// <summary>組合連線路徑，若有指定 SearchBase 則附加於伺服器之後。</summary>
    private string BuildPath(string server) =>
        string.IsNullOrWhiteSpace(_options.SearchBase)
            ? server
            : $"{server}/{_options.SearchBase}";

    /// <summary>將輸入標準化為 LDAP URL，允許傳入純 IP/主機名稱或完整 LDAP URL。</summary>
    private static string NormalizeServer(string server)
    {
        server = server.Trim();
        return server.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase)
            ? server
            : $"LDAP://{server}";
    }

    /// <summary>跳脫 LDAP 過濾器特殊字元 (RFC 4515)，避免 LDAP injection。</summary>
    private static string EscapeFilter(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                case '/': sb.Append("\\2f"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static bool IsLockedOut(DirectoryEntry entry)
    {
        var value = entry.Properties["lockoutTime"].Value;
        if (value == null) return false;

        // lockoutTime 為 IADsLargeInteger COM 物件，透過反射讀取 HighPart / LowPart，
        // 避免對 COM 物件直接呼叫 Convert 造成 E_NOINTERFACE。
        var type = value.GetType();
        if (type.IsCOMObject)
        {
            var high = (int)type.InvokeMember("HighPart", BindingFlags.GetProperty, null, value, null)!;
            var low = (int)type.InvokeMember("LowPart", BindingFlags.GetProperty, null, value, null)!;
            var ticks = ((long)high << 32) | (uint)low;
            return ticks != 0;
        }

        return Convert.ToInt64(value) != 0;
    }

    private static string GetString(DirectoryEntry entry, string propertyName) =>
        entry.Properties[propertyName].Value as string ?? string.Empty;
}
