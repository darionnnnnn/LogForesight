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

    public WorkOrdersController(WorkOrderCommandService service)
    {
        _service = service;
    }

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

    [HttpPost("{id:long}/cancel")]
    public ApiResponse<WorkOrderCloseResultDto> Cancel(long id, [FromBody] CancelWorkOrderRequest request) =>
        ApiResponse<WorkOrderCloseResultDto>.Ok(_service.Cancel(id, request));
}

/// <summary>
/// 代為結案：把一張交辦單的進行中成員全部標成結案狀態。
///
/// 能力＝<c>Assign</c> **且** <c>Handle</c>（兩個 <c>[Permission]</c> 標註疊加＝都要滿足，
/// 同一個標註內的多個能力才是「任一」）——它同時是「管理交辦單」與「替處理人標處理結論」，
/// 與 <see cref="IssueBulkCloseController"/> 同一個理由。刻意獨立成一個 controller 而不是掛在
/// <see cref="WorkOrdersController"/> 裡：那個類別的類別層能力只有 Assign，混進去會把對象搞混。
/// </summary>
[ApiController]
[Route("api/work-orders")]
[Permission(Capability.Assign)]
[Permission(Capability.Handle)]
public class WorkOrderAdminCloseController : ControllerBase
{
    private readonly WorkOrderCommandService _service;

    public WorkOrderAdminCloseController(WorkOrderCommandService service)
    {
        _service = service;
    }

    [HttpPost("{id:long}/admin-close")]
    public ApiResponse<WorkOrderCloseResultDto> AdminClose(long id, [FromBody] AdminCloseWorkOrderRequest request) =>
        ApiResponse<WorkOrderCloseResultDto>.Ok(_service.AdminClose(id, request));
}
