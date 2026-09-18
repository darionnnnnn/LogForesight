using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>交辦單清單：能力＝<c>Assign</c> 或 <c>ViewAll</c>（同一標註內多個能力＝任一）</summary>
[ApiController]
[Route("api/work-orders")]
[Permission(Capability.Assign, Capability.ViewAll)]
public class WorkOrderListController : ControllerBase
{
    private readonly WorkOrderQueryService _service;

    public WorkOrderListController(WorkOrderQueryService service)
    {
        _service = service;
    }

    [HttpGet("")]
    public ApiResponse<WorkOrderListDto> List([FromQuery] WorkOrderListRequest request) =>
        ApiResponse<WorkOrderListDto>.Ok(_service.List(request));

    /// <summary>清單篩選用的使用者群組選項（定案 48）：只回啟用中群組的 id、名稱、是否派工池。
    /// 群組名稱對 Assign／ViewAll 使用者本來就可見（處理人頁、使用者名稱旁的群組標示），不是新的洩漏面。</summary>
    [HttpGet("handler-groups")]
    public ApiResponse<List<HandlerGroupOptionDto>> HandlerGroups() =>
        ApiResponse<List<HandlerGroupOptionDto>>.Ok(_service.ListHandlerGroups());
}

/// <summary>
/// 單張交辦單的詳情／成員／時間軸。沒有能力標註（Permission 屬性）是刻意的：處理人本人不具 Assign／ViewAll
/// 也要看得到自己的單，類別層能力表達不了「是不是這張單的處理人」——授權逐單在
/// <see cref="WorkOrderQueryService"/> 內判斷（不存在 404、無權 403），此處仍需登入。
/// </summary>
[ApiController]
[Route("api/work-orders")]
public class WorkOrderViewController : ControllerBase
{
    private readonly WorkOrderQueryService _service;

    public WorkOrderViewController(WorkOrderQueryService service)
    {
        _service = service;
    }

    [HttpGet("{id:long}")]
    public ApiResponse<WorkOrderDetailDto> Get(long id) =>
        ApiResponse<WorkOrderDetailDto>.Ok(_service.Get(id));

    [HttpGet("{id:long}/members")]
    public ApiResponse<WorkOrderMemberPageDto> Members(long id, [FromQuery] string status = "all",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        ApiResponse<WorkOrderMemberPageDto>.Ok(_service.Members(id, status, page, pageSize));

    [HttpGet("{id:long}/timeline")]
    public ApiResponse<List<WorkOrderEventDto>> Timeline(long id) =>
        ApiResponse<List<WorkOrderEventDto>>.Ok(_service.Timeline(id));
}
