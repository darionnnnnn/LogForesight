using System.Globalization;
using System.Security.Claims;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Web.Auth;

/// <summary>登入成功後要寫進登入 cookie 的身分資訊</summary>
/// <param name="PermissionVersion">
/// 解析 <paramref name="Capabilities"/> 之前讀到的權限版號。必須先讀版號、後算能力：反過來的話，兩步之間若有人改了權限，
/// cookie 會蓋上新版號卻帶舊能力，之後版號相符就不再更新，舊能力一直用到下次異動。null＝建立主體時才讀（沒有能力解析的路徑）。
/// </param>
public record SessionIdentity(
    long UserId,
    string Account,
    string DisplayName,
    IReadOnlySet<Capability> Capabilities,
    bool IsServerAdmin,
    long? PermissionVersion = null);

/// <summary>
/// 登入 cookie 主體內的 claim 名稱（單一定義點）。
///
/// 自訂短名稱、不使用 <c>ClaimTypes</c> 的 WS-Federation 長 URI：寫什麼名字就讀什麼名字，
/// 不需要記憶「寫進去」與「讀出來」的對照（早期實測遇過 account 讀出來是空字串）。
/// </summary>
public static class AuthClaims
{
    public const string UserId = "sub";
    public const string Account = "account";
    public const string DisplayName = "name";
    public const string Capability = "cap";
    public const string ServerAdmin = "srvadm";

    /// <summary>簽發當下的權限版本號（<see cref="PermissionVersionStamp"/>）</summary>
    public const string PermissionVersion = "pv";

    /// <summary>簽發當下的工作階段版號（<see cref="SessionVersionStore"/>）</summary>
    public const string SessionVersion = "sv";

    /// <summary>這次登入的起點（Unix 秒）。滑動延長會重發 cookie，所以絕對上限不能看 cookie 的簽發時間，要看這個</summary>
    public const string SessionStartedAt = "sst";
}

/// <summary>
/// 建立登入 cookie 的主體。Claims 只放**能力**不放主機授權範圍：能力異動靠權限版本號
/// （claim <c>pv</c>）在下一個請求由 <see cref="CookieSessionEvents"/> 就地更新；
/// 主機授權範圍必須即時——調部門後不該還看得到前部門的主機，所以範圍每次請求由 IVisibilityService 重新解析。
/// </summary>
public sealed class SessionPrincipalFactory
{
    private readonly PermissionVersionStamp _permissionVersion;

    public SessionPrincipalFactory(PermissionVersionStamp permissionVersion)
    {
        _permissionVersion = permissionVersion;
    }

    public ClaimsPrincipal Create(SessionIdentity identity, long sessionVersion, DateTimeOffset sessionStartedAt)
    {
        var claims = new List<Claim>
        {
            new(AuthClaims.UserId, identity.UserId.ToString(CultureInfo.InvariantCulture)),
            new(AuthClaims.Account, identity.Account),
            new(AuthClaims.DisplayName, identity.DisplayName),
            new(AuthClaims.PermissionVersion,
                (identity.PermissionVersion ?? _permissionVersion.Current).ToString(CultureInfo.InvariantCulture)),
            new(AuthClaims.SessionVersion, sessionVersion.ToString(CultureInfo.InvariantCulture)),
            new(AuthClaims.SessionStartedAt, sessionStartedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
        };

        foreach (var capability in identity.Capabilities)
            claims.Add(new Claim(AuthClaims.Capability, capability.ToString()));

        if (identity.IsServerAdmin)
            claims.Add(new Claim(AuthClaims.ServerAdmin, "1"));

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims, SessionCookie.Scheme, AuthClaims.DisplayName, null));
    }
}

/// <summary>
/// 工作階段版號：使用者層級的登入失效手段。一般使用者的有效版號由 <see cref="IUserStore.GetSessionVersion"/> 取得
/// （<see cref="WebUser.SessionVersion"/> 加上獨立 blob 的登出增量，持久化、重啟不失效）；serverAdmin 不在 lf_users，版號存在 blob（同樣持久化）。
/// 登出時遞增；停用由使用者儲存在「啟用轉停用」時遞增。cookie 內版號與目前值不同＝失效。
/// </summary>
public sealed class SessionVersionStore
{
    public const string ServerAdminBlobKey = "server_admin_session_version";

    private readonly IUserStore _users;
    private readonly EfJsonBlobStore _serverAdminBlob;
    private readonly object _lock = new();
    private long? _serverAdminCached;

    public SessionVersionStore(IUserStore users, EfJsonBlobStore serverAdminBlob)
    {
        _users = users;
        _serverAdminBlob = serverAdminBlob;
    }

    /// <summary>目前版號。一般使用者呼叫端通常已查過使用者，直接傳入避免重複查找</summary>
    public long Current(bool isServerAdmin, WebUser? user = null)
    {
        if (!isServerAdmin) return user == null ? 0 : _users.GetSessionVersion(user);
        lock (_lock)
        {
            _serverAdminCached ??= Parse(_serverAdminBlob.Read());
            return _serverAdminCached.Value;
        }
    }

    /// <summary>登入當下要寫進 cookie 的版號（會查使用者）</summary>
    public long CurrentFor(long userId, bool isServerAdmin) =>
        Current(isServerAdmin, isServerAdmin ? null : _users.Get(userId));

    /// <summary>登出：遞增並回傳新值（該使用者所有裝置上既有的 cookie 一併失效）</summary>
    public long Bump(long userId, bool isServerAdmin)
    {
        if (!isServerAdmin) return _users.BumpSessionVersion(userId);
        lock (_lock)
        {
            var next = _serverAdminBlob.Mutate(content =>
            {
                var value = Parse(content) + 1;
                return (value.ToString(CultureInfo.InvariantCulture), value);
            });
            _serverAdminCached = next;
            return next;
        }
    }

    private static long Parse(string? content) =>
        long.TryParse(content, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
