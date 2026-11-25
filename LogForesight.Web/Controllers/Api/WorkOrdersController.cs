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
    private readonly WorkOrderBulkReassignService _bulkReassign;

    public WorkOrdersController(WorkOrderCommandService service, WorkOrderCandidateService candidates, WorkOrderBulkReassignService bulkReassign)
    {
        _service = service;
        _candidates = candidates;
        _bulkReassign = bulkReassign;
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

    /// <summary>批次改派預覽（只讀）：每張是新單或併入哪張、以授與補上的台數、目標是否暫停或沒有 email；分類與套用同一份</summary>
    [SkipDataVersionBump]
    [HttpPost("bulk-reassign/preview")]
    public ApiResponse<BulkReassignPreviewDto> BulkReassignPreview([FromBody] BulkReassignRequest request) =>
        ApiResponse<BulkReassignPreviewDto>.Ok(_bulkReassign.Preview(request));

    /// <summary>批次改派套用：逐張沿用單張改派，單張失敗不影響其他張，逐張回報；一次上限 100 張</summary>
    [HttpPost("bulk-reassign")]
    public ApiResponse<BulkReassignResultDto> BulkReassign([FromBody] BulkReassignRequest request) =>
        ApiResponse<BulkReassignResultDto>.Ok(_bulkReassign.Apply(request));

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
