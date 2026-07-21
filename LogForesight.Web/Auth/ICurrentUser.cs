using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace LogForesight.Web.Auth;

/// <summary>
/// 目前登入者（docs/WEB-SPEC.md §4.2）。
///
/// 存在的理由：Service 層**不准讀 HttpContext**——Service 是業務規則，
/// 依賴 HTTP 管線就無法用單元測試驗證（授權範圍過濾這種必測的規則尤其不能）。
/// 注入這個介面即可，測試時給假實作。
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    long UserId { get; }

    string Account { get; }

    string DisplayName { get; }

    IReadOnlySet<Capability> Capabilities { get; }

    /// <summary>是否為 serverAdmin 本地救援帳號（它不存在於 lf_users，UserId 為 0）</summary>
    bool IsServerAdmin { get; }

    bool Has(Capability capability);
}

/// <summary>自 JWT Claims 解析目前登入者。</summary>
public class HttpContextCurrentUser : ICurrentUser
{
    private readonly ClaimsPrincipal? _principal;
    private readonly Lazy<HashSet<Capability>> _capabilities;

    public HttpContextCurrentUser(IHttpContextAccessor accessor)
    {
        _principal = accessor.HttpContext?.User;
        _capabilities = new Lazy<HashSet<Capability>>(ParseCapabilities);
    }

    public bool IsAuthenticated => _principal?.Identity?.IsAuthenticated == true;

    public long UserId =>
        long.TryParse(_principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var id) ? id : 0;

    public string Account => _principal?.FindFirst(JwtTokenService.AccountClaim)?.Value ?? string.Empty;

    public string DisplayName => _principal?.FindFirst(JwtTokenService.DisplayNameClaim)?.Value ?? Account;

    public bool IsServerAdmin => _principal?.FindFirst(JwtTokenService.ServerAdminClaim)?.Value == "1";

    public IReadOnlySet<Capability> Capabilities => _capabilities.Value;

    public bool Has(Capability capability) => Capabilities.Contains(capability);

    private HashSet<Capability> ParseCapabilities()
    {
        var result = new HashSet<Capability>();
        if (_principal == null) return result;

        foreach (var claim in _principal.FindAll(JwtTokenService.CapabilityClaim))
        {
            if (Enum.TryParse<Capability>(claim.Value, out var capability))
                result.Add(capability);
        }
        return result;
    }
}
