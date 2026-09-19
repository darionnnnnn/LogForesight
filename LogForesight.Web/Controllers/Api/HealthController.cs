using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 健康檢查（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）。
///
/// <c>GET api/health</c> **匿名可讀**——這是刻意的：資料庫掛掉時最需要這支端點，
/// 而那時多半也登不進去。回傳內容因此收斂到「活著沒有＋版本」，不含任何可用來
/// 探查系統的細節（見 <see cref="HealthDto"/> 的說明）。
///
/// <c>GET api/health/detail</c> 需 <c>Maintain</c>，給維運人員診斷用。
/// </summary>
[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly HealthService _health;
    private readonly ScheduleFreshnessService _freshness;
    private readonly ScheduleOptionsStore _optionsStore;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IUserGroupStore _groups;
    private readonly IUserStore _users;
    private readonly IUserDisplayNameService _userDisplayNames;
    private readonly LoginThrottle _throttle;

    public HealthController(
        HealthService health,
        ScheduleFreshnessService freshness,
        ScheduleOptionsStore optionsStore,
        IAuditService audit,
        ICurrentUser currentUser,
        IUserGroupStore groups,
        IUserStore users,
        IUserDisplayNameService userDisplayNames,
        LoginThrottle throttle)
    {
        _throttle = throttle;
        _health = health;
        _freshness = freshness;
        _optionsStore = optionsStore;
        _audit = audit;
        _currentUser = currentUser;
        _groups = groups;
        _users = users;
        _userDisplayNames = userDisplayNames;
    }

    /// <summary>
    /// 存活檢查。**資料庫不可達時回 503 而不是 200＋down**——監控系統與負載平衡器
    /// 判讀的是 HTTP 狀態碼，用 200 包一個 down 的 body 等於在說「我很好」。
    ///
    /// <c>[AllowAnonymous]</c> 是必要的：全站 FallbackPolicy 預設要求已登入
    /// （見 ServiceCollectionExtensions 的「安全的預設值」），不明確標註的話這支端點
    /// 會回 401——而資料庫掛掉時最需要它，那時多半也登不進去。
    /// 回傳內容因此收斂到不含任何內部細節（見 <see cref="HealthDto"/>）。
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public ActionResult<ApiResponse<HealthDto>> Live()
    {
        var dto = _health.GetLiveness();
        var response = ApiResponse<HealthDto>.Ok(dto);
        return dto.Status == HealthStatuses.Down
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, response)
            : Ok(response);
    }

    [HttpGet("detail")]
    [Permission(Capability.Maintain)]
    public ApiResponse<HealthDetailDto> Detail() => ApiResponse<HealthDetailDto>.Ok(_health.GetDetail());

    /// <summary>
    /// 排程資料新鮮度（任務 A-3）：任何已登入使用者可讀。
    /// 只在 Stale && !Acked 且檢視者沒有 Maintain 時填入 admin 群組成員的顯示名稱，其餘為空清單。
    /// </summary>
    [HttpGet("freshness")]
    public ApiResponse<ScheduleFreshnessDto> Freshness()
    {
        var freshness = _freshness.GetScheduleFreshness(DateTime.Now);
        if (freshness.Stale && !freshness.Acked && !_currentUser.Has(Capability.Maintain))
        {
            freshness.AdminContacts = AdminMembersResolver.GetAdminMembers(_groups, _users)
                .Select(u =>
                {
                    var name = _userDisplayNames.Of(u.DisplayName);
                    return string.IsNullOrWhiteSpace(name) ? u.Account : name;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return ApiResponse<ScheduleFreshnessDto>.Ok(freshness);
    }

    /// <summary>
    /// 確認並靜音資料過期提醒（任務 A-3）：需 Maintain 權限。
    /// </summary>
    [HttpPost("freshness-ack")]
    [Permission(Capability.Maintain)]
    public ApiResponse FreshnessAck([FromBody] FreshnessAckRequest request)
    {
        var today = DateTime.Today;
        if (request.Until.Date <= today)
            throw DomainException.Validation("靜音日期必須晚於今天。");

        if (request.Until.Date > today.AddDays(30))
            throw DomainException.Validation("靜音日期不可超過今天起算 30 天。");

        _optionsStore.Update(o =>
        {
            o.FreshnessAckUntil = request.Until.Date;
        });

        _audit.Record(
            action: AuditActions.HealthFreshnessAck,
            summary: $"確認資料過期提醒，靜音至 {request.Until:yyyy-MM-dd}",
            targetKind: "schedule",
            detail: new { until = request.Until.ToString("yyyy-MM-dd") });

        return ApiResponse.Ok();
    }

    /// <summary>目前被登入節流暫停的帳號與 IP（需 Maintain）</summary>
    [HttpGet("login-throttle")]
    [Permission(Capability.Maintain)]
    public ApiResponse<IReadOnlyList<LoginThrottleEntry>> LoginThrottleList() =>
        ApiResponse<IReadOnlyList<LoginThrottleEntry>>.Ok(_throttle.GetBlocked(DateTime.Now));

    /// <summary>手動解除登入暫停（需 Maintain），key 為帳號或 IP</summary>
    [HttpDelete("login-throttle/{key}")]
    [Permission(Capability.Maintain)]
    public ApiResponse LoginThrottleClear(string key)
    {
        if (!_throttle.Clear(key))
            throw DomainException.NotFound("找不到這個被暫停的帳號或 IP（可能已自動解除）。");

        _audit.Record(
            action: AuditActions.LoginThrottleCleared,
            summary: $"解除登入暫停：{key}",
            targetKind: "login_throttle",
            targetId: key);

        return ApiResponse.Ok();
    }
}
