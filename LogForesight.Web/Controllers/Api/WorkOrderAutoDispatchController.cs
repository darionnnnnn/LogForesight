using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>立即派工：真的建單，是業務動作，能力＝<c>Assign</c>（<c>Maintain</c> 含不能看業務資料的救援帳號，不能建單）</summary>
[ApiController]
[Route("api/work-orders")]
[Permission(Capability.Assign)]
public class WorkOrderAutoDispatchController : ControllerBase
{
    private readonly WorkOrderBoardService _service;

    public WorkOrderAutoDispatchController(WorkOrderBoardService service) => _service = service;

    [HttpPost("auto-dispatch")]
    public ApiResponse<AutoDispatchResultDto> AutoDispatch([FromBody] AutoDispatchRequest request) =>
        ApiResponse<AutoDispatchResultDto>.Ok(_service.RunAutoDispatch(request.From, request.To));
}
