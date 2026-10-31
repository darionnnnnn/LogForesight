using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

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
