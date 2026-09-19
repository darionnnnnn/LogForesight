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
    private readonly IHttpContextAccessor _accessor;
    private ClaimsPrincipal? _parsedFrom;
    private HashSet<Capability> _capabilities = new();

    public HttpContextCurrentUser(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    // 每次都從 HttpContext.User 讀，不在建構時抓快照：ActiveUserMiddleware 在權限版本不符時會
    // 以重算後的 claims 替換 context.User，而本實例（Scoped）在那之前就已被 middleware 解析出來——
    // 抓快照的話，同一請求後段的授權判斷會讀到舊能力。
    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public long UserId =>
        long.TryParse(Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var id) ? id : 0;

    public string Account => Principal?.FindFirst(JwtTokenService.AccountClaim)?.Value ?? string.Empty;

    public string DisplayName => Principal?.FindFirst(JwtTokenService.DisplayNameClaim)?.Value ?? Account;

    public bool IsServerAdmin => Principal?.FindFirst(JwtTokenService.ServerAdminClaim)?.Value == "1";

    public IReadOnlySet<Capability> Capabilities
    {
        get
        {
            // 以 principal 參照當快取鍵：principal 被替換就重新解析
            var principal = Principal;
            if (!ReferenceEquals(principal, _parsedFrom))
            {
                _capabilities = ParseCapabilities(principal);
                _parsedFrom = principal;
            }
            return _capabilities;
        }
    }

    public bool Has(Capability capability) => Capabilities.Contains(capability);

    private static HashSet<Capability> ParseCapabilities(ClaimsPrincipal? principal)
    {
        var result = new HashSet<Capability>();
        if (principal == null) return result;

        foreach (var claim in principal.FindAll(JwtTokenService.CapabilityClaim))
        {
            if (Enum.TryParse<Capability>(claim.Value, out var capability))
                result.Add(capability);
        }
        return result;
    }
}
