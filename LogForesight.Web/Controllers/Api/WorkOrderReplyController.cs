using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 處理人回覆交辦單。能力＝<c>Handle</c>；「只准該單處理人本人」由 <see cref="WorkOrderReplyService"/> 逐單強制。
///
/// **刻意不與 Assign 類的交辦單 controller 合併**：
/// <c>[Permission]</c> 是 AllowMultiple，類別與方法上的標註是「都要滿足」而不是「就近覆寫」——
/// 寫進掛類別層 <c>Assign</c> 的 controller，一般處理人（有 Handle 沒有 Assign）會被擋掉。
/// </summary>
[ApiController]
[Route("api/work-orders")]
[Permission(Capability.Handle)]
public class WorkOrderReplyController : ControllerBase
{
    private readonly WorkOrderReplyService _service;

    public WorkOrderReplyController(WorkOrderReplyService service)
    {
        _service = service;
    }

    [HttpPost("{id:long}/reply")]
    public ApiResponse<WorkOrderReplyResultDto> Reply(long id, [FromBody] WorkOrderReplyRequest request) =>
        ApiResponse<WorkOrderReplyResultDto>.Ok(_service.Reply(id, request));

    [HttpPost("reply-many")]
    public ApiResponse<WorkOrderReplyManyResultDto> ReplyMany([FromBody] WorkOrderReplyManyRequest request) =>
        ApiResponse<WorkOrderReplyManyResultDto>.Ok(_service.ReplyMany(request));
}
