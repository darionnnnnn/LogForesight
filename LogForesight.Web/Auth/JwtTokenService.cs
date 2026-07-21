using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LogForesight.Web.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace LogForesight.Web.Auth;

/// <summary>登入成功後要寫進 token 的身分資訊</summary>
public record TokenIdentity(
    long UserId,
    string Account,
    string DisplayName,
    IReadOnlySet<Capability> Capabilities,
    bool IsServerAdmin);

public interface IJwtTokenService
{
    string CreateToken(TokenIdentity identity);

    /// <summary>供 Cookie 設定過期時間用</summary>
    DateTimeOffset ExpiresAt();
}

/// <summary>
/// JWT 簽發（docs/WEB-SPEC.md §6.1、§6.2）。
///
/// Claims 只放**能力**不放主機授權範圍：能力異動最遲在 token 過期時生效（可接受），
/// 但主機授權範圍必須即時——調部門後不該還看得到前部門的主機，
/// 所以範圍每次請求由 IVisibilityService 重新解析。
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    // 自訂 claim 名稱，不使用 ClaimTypes 的 WS-Federation 長 URI：
    // 那些 URI 會被 JwtBearer 的 claim 映射機制改寫，讀取端就得知道「寫進去的名字」與
    // 「讀出來的名字」不一樣——是個典型的隱形陷阱（實測遇過：account 讀出來是空字串）。
    // 搭配 MapInboundClaims = false，寫什麼名字就讀什麼名字。
    public const string AccountClaim = "account";
    public const string DisplayNameClaim = "name";
    public const string CapabilityClaim = "cap";
    public const string ServerAdminClaim = "srvadm";

    private readonly JwtSettings _jwt;

    public JwtTokenService(WebAppSettings settings)
    {
        _jwt = settings.Jwt;
    }

    public DateTimeOffset ExpiresAt() => DateTimeOffset.UtcNow.AddHours(_jwt.ExpireHours);

    public string CreateToken(TokenIdentity identity)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, identity.UserId.ToString()),
            new(AccountClaim, identity.Account),
            new(DisplayNameClaim, identity.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        foreach (var capability in identity.Capabilities)
            claims.Add(new Claim(CapabilityClaim, capability.ToString()));

        if (identity.IsServerAdmin)
            claims.Add(new Claim(ServerAdminClaim, "1"));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            expires: ExpiresAt().UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
