using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

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
