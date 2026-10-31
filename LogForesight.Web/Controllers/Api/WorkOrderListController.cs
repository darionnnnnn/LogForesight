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

    /// <summary>有未讀回覆的單數（側欄「交辦總覽」徽章用）；判定與清單 UnreadOnly 篩選同一份</summary>
    [HttpGet("unread-count")]
    public ApiResponse<WorkOrderUnreadCountDto> UnreadCount() =>
        ApiResponse<WorkOrderUnreadCountDto>.Ok(_service.UnreadCount());

    /// <summary>清單篩選用的使用者群組選項（回饋第 47 輪定案 48）：只回啟用中群組的 id、名稱、是否派工池。
    /// 群組名稱對 Assign／ViewAll 使用者本來就可見（處理人頁、使用者名稱旁的群組標示），不是新的洩漏面。</summary>
    [HttpGet("handler-groups")]
    public ApiResponse<List<HandlerGroupOptionDto>> HandlerGroups() =>
        ApiResponse<List<HandlerGroupOptionDto>>.Ok(_service.ListHandlerGroups());
}
