using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 處理人員工作頁（docs/archive/FEEDBACK-4-PLAN.md §6）：查看某個人目前被交辦哪些項目
/// （進行中案件＋被指派的風險日）。沒有 [Permission] 標註是刻意的——全登入角色可查看任何人，
/// 資料以檢視者的可見範圍過濾（走 HandlingHistoryQueryService 內部的 IVisibilityService），
/// 與全站查詢頁同一套授權模型，不新增能力。
///
/// 交辦清單與摘要（work-orders、work-orders/summary）授權在 <see cref="WorkOrderQueryService"/> 內：
/// 本人，或具 Assign／ViewAll；徽章（me/badge）只看目前使用者。
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
    public ApiResponse<HandlerWorkloadDto> Workload(long userId, [FromQuery] bool includeResolvedDays = false) =>
        ApiResponse<HandlerWorkloadDto>.Ok(_service.GetHandlerWorkload(userId, includeResolvedDays));

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
