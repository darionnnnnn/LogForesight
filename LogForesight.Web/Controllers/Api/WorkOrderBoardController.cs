using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 負載看板與待派清單：能力＝<c>Assign</c> 或 <c>ViewAll</c>（同一標註內多個能力＝任一）。
///
/// 路由不會被 <see cref="WorkOrderViewController"/> 的 <c>{id:long}</c> 吃掉：那邊的參數帶 long 約束，
/// 「load-board」「gaps」不是整數、約束不成立；且字面段的路由優先序本來就高於參數段。
/// </summary>
[ApiController]
[Route("api/work-orders")]
[Permission(Capability.Assign, Capability.ViewAll)]
public class WorkOrderBoardController : ControllerBase
{
    private readonly WorkOrderBoardService _service;

    public WorkOrderBoardController(WorkOrderBoardService service)
    {
        _service = service;
    }

    [HttpGet("load-board")]
    public ApiResponse<LoadBoardDto> LoadBoard([FromQuery] long? groupId) =>
        ApiResponse<LoadBoardDto>.Ok(_service.GetLoadBoard(groupId));

    [HttpGet("gaps")]
    public ApiResponse<GapsDto> Gaps([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int page = 1) =>
        ApiResponse<GapsDto>.Ok(_service.GetGaps(from, to, page));
}

/// <summary>立即派工：真的建單，能力＝<c>Maintain</c></summary>
[ApiController]
[Route("api/work-orders")]
[Permission(Capability.Maintain)]
public class WorkOrderAutoDispatchController : ControllerBase
{
    private readonly WorkOrderBoardService _service;

    public WorkOrderAutoDispatchController(WorkOrderBoardService service)
    {
        _service = service;
    }

    [HttpPost("auto-dispatch")]
    public ApiResponse<AutoDispatchResultDto> AutoDispatch([FromBody] AutoDispatchRequest request) =>
        ApiResponse<AutoDispatchResultDto>.Ok(_service.RunAutoDispatch(request.From, request.To));
}
