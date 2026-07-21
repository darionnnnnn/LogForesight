using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 目前登入者可見的主機——各查詢頁主篩選列的主機選單來源。
///
/// **沒有 [Permission] 標註是刻意的**：任何已登入者都可以問「我看得到哪些主機」，
/// 答案本身就是依授權過濾的結果（IVisibilityService，三層授權的第 3 層）。
/// 這正是那一層存在的意義——不靠能力標註，靠資料範圍過濾。
/// </summary>
[ApiController]
[Route("api/hosts")]
public class HostsController : ControllerBase
{
    private readonly IVisibilityService _visibility;

    public HostsController(IVisibilityService visibility)
    {
        _visibility = visibility;
    }

    [HttpGet]
    public ApiResponse<List<VisibleHostDto>> GetVisibleHosts()
    {
        var hosts = _visibility.GetVisibleHosts()
            .Where(h => h.Active)
            .Select(h => new VisibleHostDto
            {
                HostId = h.HostId,
                HostName = h.HostName,
                RoleDesc = h.RoleDesc,
                LastReportAt = h.LastReportAt
            })
            .ToList();

        return ApiResponse<List<VisibleHostDto>>.Ok(hosts);
    }
}
