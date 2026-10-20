using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 處理人員工作頁（docs/archive/FEEDBACK-4-PLAN.md §6）：查看某個人目前被交辦哪些項目
/// （進行中交辦單＋被指派的風險日）。沒有類別層 [Permission] 標註是刻意的——授權是「本人，或具 Assign／ViewAll」
/// （WEB-SPEC §9.4a），三支逐人端點都走 <see cref="WorkOrderQueryService.AuthorizeHandlerView"/> 同一道判定，
/// 資料再以檢視者的可見範圍過濾（HandlingHistoryQueryService 內部的 IVisibilityService），不新增能力。
/// 徽章（me/badge）只看目前使用者。
/// </summary>
[ApiController]
[Route("api/handlers")]
public class HandlersController : ControllerBase
{
    private readonly HandlingHistoryQueryService _service;
    private readonly WorkOrderQueryService _workOrders;

    public HandlersController(HandlingHistoryQueryService service, WorkOrderQueryService workOrders)
    {
        _service = service;
        _workOrders = workOrders;
    }

    [HttpGet("{userId:long}/workload")]
    public ApiResponse<HandlerWorkloadDto> Workload(long userId, [FromQuery] bool includeResolvedDays = false)
    {
        _workOrders.AuthorizeHandlerView(userId);
        return ApiResponse<HandlerWorkloadDto>.Ok(_service.GetHandlerWorkload(userId, includeResolvedDays));
    }

    [HttpGet("{userId:long}/work-orders")]
    public ApiResponse<WorkOrderListDto> WorkOrders(long userId, [FromQuery] WorkOrderListRequest request) =>
        ApiResponse<WorkOrderListDto>.Ok(_workOrders.ListForHandler(userId, request));

    [HttpGet("{userId:long}/work-orders/summary")]
    public ApiResponse<HandlerSummaryDto> WorkOrderSummary(long userId) =>
        ApiResponse<HandlerSummaryDto>.Ok(_workOrders.HandlerSummary(userId));

    [HttpGet("me/badge")]
    public ApiResponse<HandlerSummaryDto> MyBadge() =>
        ApiResponse<HandlerSummaryDto>.Ok(_workOrders.MyBadge());
}
