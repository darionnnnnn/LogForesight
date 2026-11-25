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

    /// <summary>立即派工；請求帶 <c>source</c>＋<c>eventId</c> 時只派這個問題（單題派工），不帶＝全頁</summary>
    [HttpPost("auto-dispatch")]
    public ApiResponse<AutoDispatchResultDto> AutoDispatch([FromBody] AutoDispatchRequest request) =>
        ApiResponse<AutoDispatchResultDto>.Ok(_service.RunAutoDispatch(request.From, request.To, request.Source, request.EventId));

    /// <summary>派工預覽（不落盤，給確認框用）：與落盤共用同一個試跑，回將建立／併入幾張單、每人幾台</summary>
    [SkipDataVersionBump]
    [HttpPost("auto-dispatch/preview")]
    public ApiResponse<AutoDispatchPreviewDto> AutoDispatchPreview([FromBody] AutoDispatchRequest request) =>
        ApiResponse<AutoDispatchPreviewDto>.Ok(_service.PreviewAutoDispatch(request.From, request.To, request.Source, request.EventId));
}
