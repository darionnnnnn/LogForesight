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
/// 驗證邏輯在 IdentityService、token 簽發在 IJwtTokenService、稽核在 IAuditService。
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IIdentityService _identity;
    private readonly IJwtTokenService _tokens;
    private readonly IAuthenticationProvider _provider;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly WebAppSettings _settings;

    public AuthController(
        IIdentityService identity,
        IJwtTokenService tokens,
        IAuthenticationProvider provider,
        ICurrentUser currentUser,
        IAuditService audit,
        WebAppSettings settings)
    {
        _identity = identity;
        _tokens = tokens;
        _provider = provider;
        _currentUser = currentUser;
        _audit = audit;
        _settings = settings;
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
        var outcome = _identity.Login(request.Account, request.Password);

        if (!outcome.Success || outcome.Identity == null)
        {
            return Unauthorized(ApiResponse<CurrentUserDto>.Fail(
                ApiErrorCodes.Forbidden, outcome.ErrorMessage ?? "登入失敗。"));
        }

        var token = _tokens.CreateToken(outcome.Identity);
        var expires = _tokens.ExpiresAt();

        // HttpOnly：前端 JS 讀不到 token（XSS 也偷不走）
        // SameSite=Strict：跨站請求不帶上這張 Cookie，這是 CSRF 的第一層防線
        // Secure：只走 HTTPS。內網也必須是 HTTPS，否則 Cookie 在網路上是明文
        Response.Cookies.Append(_settings.Jwt.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = expires,
            Path = "/"
        });

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

        Response.Cookies.Delete(_settings.Jwt.CookieName);
        return ApiResponse.Ok();
    }

    /// <summary>目前登入者。前端用來渲染側欄選單與功能鈕的顯示範圍</summary>
    [HttpGet("me")]
    public ApiResponse<CurrentUserDto> Me() =>
        ApiResponse<CurrentUserDto>.Ok(new CurrentUserDto
        {
            UserId = _currentUser.UserId,
            Account = _currentUser.Account,
            DisplayName = _currentUser.DisplayName,
            IsServerAdmin = _currentUser.IsServerAdmin,
            Capabilities = _currentUser.Capabilities.Select(c => c.ToString()).ToList(),
            NeedsAdminSetup = _currentUser.IsServerAdmin && _identity.HasNoAdmins()
        });

    private CurrentUserDto ToDto(TokenIdentity identity) => new()
    {
        UserId = identity.UserId,
        Account = identity.Account,
        DisplayName = identity.DisplayName,
        IsServerAdmin = identity.IsServerAdmin,
        Capabilities = identity.Capabilities.Select(c => c.ToString()).ToList(),
        NeedsAdminSetup = identity.IsServerAdmin && _identity.HasNoAdmins()
    };
}
