using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 處理人員工作頁（docs/FEEDBACK-4-PLAN.md §6）：查看某個人目前被交辦哪些項目
/// （進行中案件＋被指派的風險日）。沒有 [Permission] 標註是刻意的——全登入角色可查看任何人，
/// 資料以檢視者的可見範圍過濾（走 HandlingService 內部的 IVisibilityService），與全站查詢頁
/// 同一套授權模型，不新增能力。
/// </summary>
[ApiController]
[Route("api/handlers")]
public class HandlersController : ControllerBase
{
    private readonly HandlingService _service;

    public HandlersController(HandlingService service)
    {
        _service = service;
    }

    [HttpGet("{userId:long}/workload")]
    public ApiResponse<HandlerWorkloadDto> Workload(long userId, [FromQuery] bool includeResolvedDays = false) =>
        ApiResponse<HandlerWorkloadDto>.Ok(_service.GetHandlerWorkload(userId, includeResolvedDays));
}
