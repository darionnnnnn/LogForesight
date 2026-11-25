using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Filters;
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
/// 驗證邏輯在 IdentityService、登入 cookie 的簽發與撤銷在 ISessionSigner、稽核在 IAuditService。
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IdentityService _identity;
    private readonly ISessionSigner _session;
    private readonly IAuthenticationProvider _provider;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly WebAppSettings _settings;
    private readonly IUserDisplayNameService _userDisplayNames;
    private readonly LoginThrottle _throttle;
    private readonly DirectoryVerifyGate _directoryGate;

    public AuthController(
        IdentityService identity,
        ISessionSigner session,
        IAuthenticationProvider provider,
        ICurrentUser currentUser,
        IAuditService audit,
        WebAppSettings settings,
        IUserDisplayNameService userDisplayNames,
        LoginThrottle throttle,
        DirectoryVerifyGate? directoryGate = null)
    {
        _directoryGate = directoryGate ?? new DirectoryVerifyGate();
        _throttle = throttle;
        _identity = identity;
        _session = session;
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

    internal const long LoginRequestMaxBytes = 4096;

    [SkipDataVersionBump]
    [HttpPost("login")]
    [AllowAnonymous]
    // 匿名端點：帳號與密碼各上限 256 字，正常本文遠小於 4 KB；不設上限時任何人都能塞到 Kestrel 預設的 30 MB 再被反序列化
    [RequestSizeLimit(LoginRequestMaxBytes)]
    public async Task<ActionResult<ApiResponse<CurrentUserDto>>> Login([FromBody] LoginRequest request)
    {
        var now = DateTime.Now;
        var account = request.Account ?? string.Empty;

        // 本地救援帳號（serverAdmin）豁免 IP 維度，只看帳號維度：AD 掛掉時它是唯一入口，
        // 不能因為同一個 IP（例如共用跳板機、NAT 出口）上其他人的失敗而被連坐擋在門外。
        // 它的連續失敗鎖定只由登入節流（下方帳號維度）負責。
        var isServerAdmin = string.Equals(account.Trim(), _settings.Auth.ServerAdmin.Account.Trim(),
            StringComparison.OrdinalIgnoreCase);
        var ip = isServerAdmin ? null : HttpContext.Connection.RemoteIpAddress;

        // 節流鍵用正規化帳號：A\bob、B\bob、bob 視為同一帳號，不能靠網域前綴變體分散計數
        var throttleKey = AccountNames.Canonical(account);

        if (_throttle.IsBlocked(throttleKey, ip, now, out var retryAfter))
        {
            // 同一次暫停只在第一次被擋時寫稽核：暫停期間的連續嘗試不能再讓稽核表無上限成長
            if (_throttle.MarkBlockAudited(throttleKey, ip, now))
            {
                _audit.RecordAuth(AuditActions.LoginThrottled, account.Trim(), null,
                    $"登入嘗試過多，暫停登入（來源 IP：{HttpContext.Connection.RemoteIpAddress?.ToString() ?? "未知"}）",
                    AuditResult.Denied);
            }

            // 不透露是帳號還是 IP 被擋，也不透露帳號是否存在
            var minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
            return StatusCode(StatusCodes.Status429TooManyRequests, ApiResponse<CurrentUserDto>.Fail(
                ApiErrorCodes.RateLimited, $"登入嘗試次數過多，請於 {minutes} 分鐘後再試。"));
        }

        // 目錄服務驗證的並行上限（G15）：救援帳號不經 Provider、不受限；一般帳號名額與佇列都滿了回 429，
        // 這不是帳密問題，不計入失敗節流
        IDisposable? permit = null;
        if (!isServerAdmin)
        {
            permit = await _directoryGate.TryEnterAsync(HttpContext.RequestAborted);
            if (permit == null)
                return StatusCode(StatusCodes.Status429TooManyRequests, ApiResponse<CurrentUserDto>.Fail(
                    ApiErrorCodes.RateLimited, "目錄服務忙碌，請稍後再試。"));
        }

        LoginOutcome outcome;
        try { outcome = _identity.Login(account, request.Password); }
        finally { permit?.Dispose(); }

        if (!outcome.Success || outcome.Identity == null)
        {
            _throttle.RecordFailure(throttleKey, ip, now);
            return Unauthorized(ApiResponse<CurrentUserDto>.Fail(
                ApiErrorCodes.AuthFailed, outcome.ErrorMessage ?? "登入失敗。"));
        }

        _throttle.RecordSuccess(throttleKey);

        // HttpOnly：前端 JS 讀不到 cookie（XSS 也偷不走）
        // SameSite=Strict：跨站請求不帶上這張 Cookie，這是 CSRF 的第一層防線
        // Secure：連線是 HTTPS（或代理標示 HTTPS）時才掛；內網正式環境應走 HTTPS，否則 Cookie 在網路上是明文
        await _session.SignInAsync(HttpContext, outcome.Identity);

        return Ok(ApiResponse<CurrentUserDto>.Ok(ToDto(outcome.Identity)));
    }

    [SkipDataVersionBump]
    [HttpPost("logout")]
    public async Task<ApiResponse> Logout()
    {
        if (_currentUser.IsAuthenticated)
        {
            _audit.RecordAuth(AuditActions.Logout, _currentUser.Account,
                _currentUser.UserId > 0 ? _currentUser.UserId : null, "登出", AuditResult.Ok);
        }

        // 登出撤銷：只刪 cookie 的話，事先被複製走的 cookie 在剩餘效期內仍可重放；
        // 遞增使用者的工作階段版號（持久化），既有 cookie 一律失效，重啟也不復活
        await _session.SignOutAsync(HttpContext, _currentUser.UserId, _currentUser.IsServerAdmin);
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
            OwnsAnyHost = _identity.OwnsAnyHost(_currentUser.UserId),
            NeedsAdminSetup = _currentUser.IsServerAdmin && _identity.HasNoAdmins(),
            LandingPath = LandingPathFor(_currentUser.UserId, _currentUser.IsServerAdmin, _currentUser.Capabilities)
        });

    private CurrentUserDto ToDto(SessionIdentity identity) => new()
    {
        UserId = identity.UserId,
        Account = identity.Account,
        DisplayName = _userDisplayNames.Of(identity.DisplayName),
        IsServerAdmin = identity.IsServerAdmin,
        Capabilities = identity.Capabilities.Select(c => c.ToString()).ToList(),
        OwnsAnyHost = _identity.OwnsAnyHost(identity.UserId),
        NeedsAdminSetup = identity.IsServerAdmin && _identity.HasNoAdmins(),
        LandingPath = LandingPathFor(identity.UserId, identity.IsServerAdmin, identity.Capabilities)
    };

    /// <summary>
    /// 登入落地頁：只負責處理問題的人（有 Handle、沒有任何管理能力）直接落到自己的交辦工作頁——
    /// 儀表板不是他今天要做的事；有 DevMonitor、沒有 Handle 也沒有 Assign 的人（典型是開發維運）直接落到排程作業頁，
    /// 那是他每天要看的頁；其餘一律落到儀表板。救援帳號（serverAdmin）不適用以上兩條。
    /// </summary>
    internal static string LandingPathFor(long userId, bool isServerAdmin, IReadOnlySet<Capability> capabilities)
    {
        var handlerOnly = !isServerAdmin
            && capabilities.Contains(Capability.Handle)
            && !capabilities.Contains(Capability.Maintain)
            && !capabilities.Contains(Capability.Assign)
            && !capabilities.Contains(Capability.ViewAll);
        if (handlerOnly) return $"/handlers/{userId}";

        var monitorOnly = !isServerAdmin
            && capabilities.Contains(Capability.DevMonitor)
            && !capabilities.Contains(Capability.Handle)
            && !capabilities.Contains(Capability.Assign);
        return monitorOnly ? "/runs" : "/";
    }
}
