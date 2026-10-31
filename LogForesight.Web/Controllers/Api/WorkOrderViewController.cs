using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

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

    /// <summary>
    /// 標記管理者已看過這張單（未讀回覆判定）。回一般 ApiResponse（前端的 api.js 要有 JSON 本體才算成功）；不具 Assign 時不寫入、同樣回成功。
    /// 放在這個沒有類別層能力標註的 controller 是因為處理人與 ViewAll 使用者開詳情也會呼叫，不該因此得到 403。
    /// </summary>
    [SkipDataVersionBump]
    [HttpPost("{id:long}/seen")]
    public ApiResponse<bool> Seen(long id)
    {
        _service.MarkSeen(id);
        return ApiResponse<bool>.Ok(true);
    }

    [HttpGet("{id:long}/members")]
    public ApiResponse<WorkOrderMemberPageDto> Members(long id, [FromQuery] string status = "all",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        ApiResponse<WorkOrderMemberPageDto>.Ok(_service.Members(id, status, page, pageSize));

    [HttpGet("{id:long}/timeline")]
    public ApiResponse<List<WorkOrderEventDto>> Timeline(long id) =>
        ApiResponse<List<WorkOrderEventDto>>.Ok(_service.Timeline(id));
}
