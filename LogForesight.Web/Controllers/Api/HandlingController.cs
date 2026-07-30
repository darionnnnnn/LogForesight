using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 風險日的處理狀態與指派（docs/WEB-SPEC.md §9.3）。
///
/// 注意兩個端點的能力要求不同：狀態更新是 <c>Handle</c>（user 也有），
/// 指派是 <c>Assign</c>（只有 admin）。這是「admin 才能指派、user 可以維護處理狀態」的實作點。
/// </summary>
[ApiController]
[Route("api/records/{hostId:long}/{date}/handling")]
public class HandlingController : ControllerBase
{
    private readonly HandlingService _service;

    public HandlingController(HandlingService service)
    {
        _service = service;
    }

    [HttpGet]
    public ApiResponse<HandlingDto> Get(long hostId, string date) =>
        ApiResponse<HandlingDto>.Ok(_service.Get(hostId, QueryStringParsing.ParseRequiredDate(date)));

    [HttpPut]
    [Permission(Capability.Handle)]
    public ApiResponse<HandlingDto> Update(long hostId, string date, [FromBody] UpdateHandlingRequest request) =>
        ApiResponse<HandlingDto>.Ok(_service.Update(hostId, QueryStringParsing.ParseRequiredDate(date), request));

    /// <summary>設定單一問題的處理狀態（低風險預設不處理／已知雜訊自動判讀的快速動作用）。與日層級更新同為 Handle 能力</summary>
    [HttpPut("issues")]
    [Permission(Capability.Handle)]
    public ApiResponse<IssueStatusResultDto> SetIssueStatus(long hostId, string date, [FromBody] SetIssueStatusRequest request) =>
        ApiResponse<IssueStatusResultDto>.Ok(_service.SetIssueStatus(hostId, QueryStringParsing.ParseRequiredDate(date), request));

    /// <summary>批次設定多個問題的處理狀態（勾選多個問題後在右側處理狀態區塊一次套用）</summary>
    [HttpPut("issues/batch")]
    [Permission(Capability.Handle)]
    public ApiResponse<BatchIssueStatusResultDto> SetIssueStatusBatch(long hostId, string date, [FromBody] BatchSetIssueStatusRequest request) =>
        ApiResponse<BatchIssueStatusResultDto>.Ok(_service.SetIssueStatusBatch(hostId, QueryStringParsing.ParseRequiredDate(date), request));

    [HttpPut("assign")]
    [Permission(Capability.Assign)]
    public ApiResponse<HandlingDto> Assign(long hostId, string date, [FromBody] AssignHandlerRequest request) =>
        ApiResponse<HandlingDto>.Ok(_service.Assign(hostId, QueryStringParsing.ParseRequiredDate(date), request.HandlerId));

    [HttpGet("logs")]
    public ApiResponse<List<HandlingLogDto>> GetLogs(long hostId, string date) =>
        ApiResponse<List<HandlingLogDto>>.Ok(_service.GetLogs(hostId, QueryStringParsing.ParseRequiredDate(date)));
}

/// <summary>
/// 問題案件跨主機批次指派（docs/FEEDBACK-4-PLAN.md §4）：問題查詢「依問題」視角的指派入口，
/// 全部端點都是 <c>Assign</c> 能力——同單日詳情的指派，只有 admin 能做。
/// </summary>
[ApiController]
[Route("api/handling/issue-cases")]
[Permission(Capability.Assign)]
public class IssueCasesController : ControllerBase
{
    private readonly HandlingService _service;

    public IssueCasesController(HandlingService service)
    {
        _service = service;
    }

    /// <summary>批次指派 modal 開啟時的受影響主機預覽</summary>
    [HttpGet("preview")]
    public ApiResponse<List<IssueCasePreviewHostDto>> Preview(
        [FromQuery] string source, [FromQuery] int eventId,
        [FromQuery] string? from, [FromQuery] string? to) =>
        ApiResponse<List<IssueCasePreviewHostDto>>.Ok(
            _service.PreviewIssueCaseAssign(source, eventId, QueryStringParsing.ParseDate(from), QueryStringParsing.ParseDate(to)));

    [HttpPost("bulk-assign")]
    public ApiResponse<BulkAssignIssueCaseResultDto> BulkAssign([FromBody] BulkAssignIssueCaseRequest request) =>
        ApiResponse<BulkAssignIssueCaseResultDto>.Ok(_service.BulkAssignIssueCase(request));
}

/// <summary>權限異動待辦（§9.5）</summary>
[ApiController]
[Route("api/permission-changes")]
public class PermissionChangesController : ControllerBase
{
    private readonly PermissionChangeService _service;

    public PermissionChangesController(PermissionChangeService service)
    {
        _service = service;
    }

    [HttpGet]
    public ApiResponse<List<PermissionChangeDto>> Query(
        [FromQuery] string? status, [FromQuery] int maxCount = 200) =>
        ApiResponse<List<PermissionChangeDto>>.Ok(_service.Query(status, Math.Clamp(maxCount, 1, 1000)));

    [HttpPut("{changeId}/confirm")]
    [Permission(Capability.ConfirmPermission)]
    public ApiResponse<PermissionChangeDto> Confirm(
        string changeId, [FromBody] ConfirmPermissionChangeRequest request) =>
        ApiResponse<PermissionChangeDto>.Ok(_service.Confirm(changeId, request));
}
