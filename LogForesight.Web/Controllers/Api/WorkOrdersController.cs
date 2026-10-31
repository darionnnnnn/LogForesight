using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 交辦單命令 API：依篩選建單（預覽／落盤）、追加、改派、拆單、取消。能力＝<c>Assign</c>。
/// 查詢端點（清單、詳情、看板）不在這裡。
/// </summary>
[ApiController]
[Route("api/work-orders")]
[Permission(Capability.Assign)]
public class WorkOrdersController : ControllerBase
{
    private readonly WorkOrderCommandService _service;
    private readonly WorkOrderCandidateService _candidates;

    public WorkOrdersController(WorkOrderCommandService service, WorkOrderCandidateService candidates)
    {
        _service = service;
        _candidates = candidates;
    }

    /// <summary>手動派工人選（交辦、改派／拆出、日層級指派共用）；能力沿用類別層 Assign</summary>
    [SkipDataVersionBump]
    [HttpPost("candidates")]
    [Permission(Capability.Assign)]
    public ApiResponse<List<WorkOrderCandidateDto>> Candidates([FromBody] WorkOrderCandidatesRequest request) =>
        ApiResponse<List<WorkOrderCandidateDto>>.Ok(_candidates.Get(request));

    [SkipDataVersionBump]
    [HttpPost("preview")]
    public ApiResponse<WorkOrderPreviewDto> Preview([FromBody] CreateWorkOrderRequest request) =>
        ApiResponse<WorkOrderPreviewDto>.Ok(_service.Preview(request));

    [HttpPost]
    public ApiResponse<CreateWorkOrderResultDto> Create([FromBody] CreateWorkOrderRequest request) =>
        ApiResponse<CreateWorkOrderResultDto>.Ok(_service.Create(request));

    [HttpPost("{id:long}/append")]
    public ApiResponse<AppendWorkOrderResultDto> Append(long id, [FromBody] AppendWorkOrderRequest request) =>
        ApiResponse<AppendWorkOrderResultDto>.Ok(_service.Append(id, request));

    [HttpPost("{id:long}/reassign")]
    public ApiResponse<WorkOrderMoveResultDto> Reassign(long id, [FromBody] ReassignWorkOrderRequest request) =>
        ApiResponse<WorkOrderMoveResultDto>.Ok(_service.Reassign(id, request));

    [HttpPost("{id:long}/split")]
    public ApiResponse<WorkOrderMoveResultDto> Split(long id, [FromBody] SplitWorkOrderRequest request) =>
        ApiResponse<WorkOrderMoveResultDto>.Ok(_service.Split(id, request));

    [HttpPost("{id:long}/return")]
    public ApiResponse<WorkOrderReturnResultDto> Return(long id, [FromBody] ReturnWorkOrderRequest request) =>
        ApiResponse<WorkOrderReturnResultDto>.Ok(_service.Return(id, request));

    [HttpPost("{id:long}/cancel")]
    public ApiResponse<WorkOrderCloseResultDto> Cancel(long id, [FromBody] CancelWorkOrderRequest request) =>
        ApiResponse<WorkOrderCloseResultDto>.Ok(_service.Cancel(id, request));
}
