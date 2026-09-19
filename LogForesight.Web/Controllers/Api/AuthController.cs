using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 登入/登出/取得目前身分（docs/WEB-SPEC.md §9.0）。
///
/// Controller 的職責僅止於「HTTP ↔ DTO 轉換與呼叫 Service」——
/// 驗證邏輯在 IdentityService、token 簽發在 JwtTokenService、稽核在 IAuditService。
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IdentityService _identity;
    private readonly JwtTokenService _tokens;
    private readonly IAuthenticationProvider _provider;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly WebAppSettings _settings;
    private readonly IUserDisplayNameService _userDisplayNames;
    private readonly LoginThrottle _throttle;

    public AuthController(
        IdentityService identity,
        JwtTokenService tokens,
        IAuthenticationProvider provider,
        ICurrentUser currentUser,
        IAuditService audit,
        WebAppSettings settings,
        IUserDisplayNameService userDisplayNames,
        LoginThrottle throttle)
    {
        _throttle = throttle;
        _identity = identity;
        _tokens = tokens;
        _provider = provider;
        _currentUser = currentUser;
        _audit = audit;
        _settings = settings;
        _userDisplayNames = userDisplayNames;
    }

    /// <summary>登入頁初始化：是否需要密碼欄</summary>
    [HttpGet("options")]
    [AllowAnonymous]
    public ApiResponse<LoginOptionsDto> Options() =>
        ApiResponse<LoginOptionsDto>.Ok(new LoginOptionsDto
        {
            Provider = _provider.Name,
            RequiresPassword = _provider.RequiresPassword
        });

    [HttpPost("login")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<CurrentUserDto>> Login([FromBody] LoginRequest request)
    {
        var now = DateTime.Now;
        var account = request.Account ?? string.Empty;

        // 本地救援帳號（serverAdmin）豁免 IP 維度，只看帳號維度：AD 掛掉時它是唯一入口，
        // 不能因為同一個 IP（例如共用跳板機、NAT 出口）上其他人的失敗而被連坐擋在門外。
        // 它自己的連續失敗鎖定仍由 ServerAdminAuthenticator 負責，兩道並存。
        var isServerAdmin = string.Equals(account.Trim(), _settings.Auth.ServerAdmin.Account.Trim(),
            StringComparison.OrdinalIgnoreCase);
        var ip = isServerAdmin ? null : HttpContext.Connection.RemoteIpAddress;

        if (_throttle.IsBlocked(account, ip, now, out var retryAfter))
        {
            // 同一次暫停只在第一次被擋時寫稽核：暫停期間的連續嘗試不能再讓稽核表無上限成長
            if (_throttle.MarkBlockAudited(account, ip, now))
            {
                _audit.RecordAuth(AuditActions.LoginThrottled, account.Trim(), null,
                    $"登入嘗試過多，暫停登入（來源 IP：{HttpContext.Connection.RemoteIpAddress?.ToString() ?? "未知"}）",
                    AuditResult.Denied);
            }

            // 不透露是帳號還是 IP 被擋，也不透露帳號是否存在
            var minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
            return StatusCode(StatusCodes.Status429TooManyRequests, ApiResponse<CurrentUserDto>.Fail(
                ApiErrorCodes.Forbidden, $"登入嘗試次數過多，請於 {minutes} 分鐘後再試。"));
        }

        var outcome = _identity.Login(account, request.Password);

        if (!outcome.Success || outcome.Identity == null)
        {
            _throttle.RecordFailure(account, ip, now);
            return Unauthorized(ApiResponse<CurrentUserDto>.Fail(
                ApiErrorCodes.Forbidden, outcome.ErrorMessage ?? "登入失敗。"));
        }

        _throttle.RecordSuccess(account);

        var token = _tokens.CreateToken(outcome.Identity);
        var expires = _tokens.ExpiresAt();

        // HttpOnly：前端 JS 讀不到 token（XSS 也偷不走）
        // SameSite=Strict：跨站請求不帶上這張 Cookie，這是 CSRF 的第一層防線
        // Secure：只走 HTTPS。內網也必須是 HTTPS，否則 Cookie 在網路上是明文
        AuthCookie.Append(Response, Request, _settings.Jwt.CookieName, token, expires);

        return Ok(ApiResponse<CurrentUserDto>.Ok(ToDto(outcome.Identity)));
    }

    [HttpPost("logout")]
    public ApiResponse Logout()
    {
        if (_currentUser.IsAuthenticated)
        {
            _audit.RecordAuth(AuditActions.Logout, _currentUser.Account,
                _currentUser.UserId > 0 ? _currentUser.UserId : null, "登出", AuditResult.Ok);
        }

        AuthCookie.Delete(Response, Request, _settings.Jwt.CookieName);
        return ApiResponse.Ok();
    }

    /// <summary>目前登入者。前端用來渲染側欄選單與功能鈕的顯示範圍</summary>
    [HttpGet("me")]
    public ApiResponse<CurrentUserDto> Me() =>
        ApiResponse<CurrentUserDto>.Ok(new CurrentUserDto
        {
            UserId = _currentUser.UserId,
            Account = _currentUser.Account,
            DisplayName = _userDisplayNames.Of(_currentUser.DisplayName),
            IsServerAdmin = _currentUser.IsServerAdmin,
            Capabilities = _currentUser.Capabilities.Select(c => c.ToString()).ToList(),
            NeedsAdminSetup = _currentUser.IsServerAdmin && _identity.HasNoAdmins()
        });

    private CurrentUserDto ToDto(TokenIdentity identity) => new()
    {
        UserId = identity.UserId,
        Account = identity.Account,
        DisplayName = _userDisplayNames.Of(identity.DisplayName),
        IsServerAdmin = identity.IsServerAdmin,
        Capabilities = identity.Capabilities.Select(c => c.ToString()).ToList(),
        NeedsAdminSetup = identity.IsServerAdmin && _identity.HasNoAdmins()
    };
}
