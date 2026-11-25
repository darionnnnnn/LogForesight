using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 主機詳情「這台的交辦」。沒有 [Permission] 標註是刻意的，理由同 <see cref="HostDetailController"/>：
/// 授權是資料範圍過濾（主機可見性＋案件授與的問題白名單），逐請求在 <see cref="WorkOrderQueryService.HostOrders"/> 內判斷，
/// 類別層能力表達不了「只經案件授與看得到這台、只能看被交辦的問題」。獨立成 controller 是為了不改動
/// <see cref="HostDetailController"/> 既有的建構子（PRTG 區塊的測試直接組裝它）。
/// </summary>
[ApiController]
[Route("api/host-detail")]
public class HostOrdersController : ControllerBase
{
    private readonly WorkOrderQueryService _service;

    public HostOrdersController(WorkOrderQueryService service)
    {
        _service = service;
    }

    [HttpGet("{hostId:long}/orders")]
    public ApiResponse<HostWorkOrdersDto> Get(long hostId) =>
        ApiResponse<HostWorkOrdersDto>.Ok(_service.HostOrders(hostId));
}
