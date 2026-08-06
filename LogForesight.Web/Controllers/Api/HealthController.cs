using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 健康檢查（docs/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）。
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

    public HealthController(HealthService health)
    {
        _health = health;
    }

    /// <summary>
    /// 存活檢查。**資料庫不可達時回 503 而不是 200＋down**——監控系統與負載平衡器
    /// 判讀的是 HTTP 狀態碼，用 200 包一個 down 的 body 等於在說「我很好」。
    /// </summary>
    [HttpGet]
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
}
