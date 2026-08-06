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
    private readonly DayHandlingCommandService _day;
    private readonly IssueHandlingCommandService _issue;
    private readonly HandlingHistoryQueryService _history;

    public HandlingController(
        DayHandlingCommandService day, IssueHandlingCommandService issue, HandlingHistoryQueryService history)
    {
        _day = day;
        _issue = issue;
        _history = history;
    }

    [HttpGet]
    public ApiResponse<HandlingDto> Get(long hostId, string date) =>
        ApiResponse<HandlingDto>.Ok(_day.Get(hostId, QueryStringParsing.ParseRequiredDate(date)));

    [HttpPut]
    [Permission(Capability.Handle)]
    public ApiResponse<HandlingDto> Update(long hostId, string date, [FromBody] UpdateHandlingRequest request) =>
        ApiResponse<HandlingDto>.Ok(_day.Update(hostId, QueryStringParsing.ParseRequiredDate(date), request));

    /// <summary>設定單一問題的處理狀態（低風險預設不處理／已知雜訊自動判讀的快速動作用）。與日層級更新同為 Handle 能力</summary>
    [HttpPut("issues")]
    [Permission(Capability.Handle)]
    public ApiResponse<IssueStatusResultDto> SetIssueStatus(long hostId, string date, [FromBody] SetIssueStatusRequest request) =>
        ApiResponse<IssueStatusResultDto>.Ok(_issue.SetIssueStatus(hostId, QueryStringParsing.ParseRequiredDate(date), request));

    /// <summary>批次設定多個問題的處理狀態（勾選多個問題後在右側處理狀態區塊一次套用）</summary>
    [HttpPut("issues/batch")]
    [Permission(Capability.Handle)]
    public ApiResponse<BatchIssueStatusResultDto> SetIssueStatusBatch(long hostId, string date, [FromBody] BatchSetIssueStatusRequest request) =>
        ApiResponse<BatchIssueStatusResultDto>.Ok(_issue.SetIssueStatusBatch(hostId, QueryStringParsing.ParseRequiredDate(date), request));

    [HttpPut("assign")]
    [Permission(Capability.Assign)]
    public ApiResponse<HandlingDto> Assign(long hostId, string date, [FromBody] AssignHandlerRequest request) =>
        ApiResponse<HandlingDto>.Ok(
            _day.Assign(hostId, QueryStringParsing.ParseRequiredDate(date), request.HandlerId, request.Reassign));

    [HttpGet("logs")]
    public ApiResponse<List<HandlingLogDto>> GetLogs(long hostId, string date) =>
        ApiResponse<List<HandlingLogDto>>.Ok(_history.GetLogs(hostId, QueryStringParsing.ParseRequiredDate(date)));
}

/// <summary>
/// 問題案件跨主機批次指派（docs/archive/FEEDBACK-4-PLAN.md §4）：問題查詢「依問題」視角的指派入口，
/// 全部端點都是 <c>Assign</c> 能力——同單日詳情的指派，只有 admin 能做。
/// </summary>
[ApiController]
[Route("api/handling/issue-cases")]
[Permission(Capability.Assign)]
public class IssueCasesController : ControllerBase
{
    private readonly IssueHandlingCommandService _service;

    public IssueCasesController(IssueHandlingCommandService service)
    {
        _service = service;
    }

    /// <summary>批次指派 modal 開啟時的受影響主機預覽（逐台清單有上限，總數另外誠實回報——體檢 M10）</summary>
    [HttpGet("preview")]
    public ApiResponse<IssueCaseAssignPreviewDto> Preview(
        [FromQuery] string source, [FromQuery] int eventId,
        [FromQuery] string? from, [FromQuery] string? to) =>
        ApiResponse<IssueCaseAssignPreviewDto>.Ok(
            _service.PreviewIssueCaseAssign(source, eventId, QueryStringParsing.ParseDate(from), QueryStringParsing.ParseDate(to)));

    [HttpPost("bulk-assign")]
    public ApiResponse<BulkAssignIssueCaseResultDto> BulkAssign([FromBody] BulkAssignIssueCaseRequest request) =>
        ApiResponse<BulkAssignIssueCaseResultDto>.Ok(_service.BulkAssignIssueCase(request));

    /// <summary>群組指派時的候選處理人＋各自現有負載（docs/archive/FEEDBACK-10-PLAN.md §12）</summary>
    [HttpGet("handler-candidates")]
    public ApiResponse<List<HandlerCandidateDto>> HandlerCandidates([FromQuery] long groupId) =>
        ApiResponse<List<HandlerCandidateDto>>.Ok(_service.GetHandlerCandidates(groupId));
}

/// <summary>
/// 跨主機一次回覆同一個問題的處理狀態（docs/archive/FEEDBACK-10-PLAN.md §11）。
///
/// **刻意與 <see cref="IssueCasesController"/> 分開**，雖然路由前綴相同：那個類別掛的是
/// 類別層 <c>Assign</c>，而 <c>[Permission]</c> 是 <c>AllowMultiple</c>——類別與方法上的
/// 標註是「都要滿足」而不是「就近覆寫」。這支端點是**處理人回覆自己手上的案件**
/// （user 角色有 Handle 沒有 Assign），寫在那個類別裡會被類別層的 Assign 擋掉。
///
/// 對象限定「自己名下的進行中案件」由服務層強制，不靠端點能力區分。
/// </summary>
[ApiController]
[Route("api/handling/issue-cases")]
[Permission(Capability.Handle)]
public class IssueCaseStatusController : ControllerBase
{
    private readonly IssueHandlingCommandService _service;

    public IssueCaseStatusController(IssueHandlingCommandService service)
    {
        _service = service;
    }

    [HttpPost("bulk-status")]
    public ApiResponse<BulkIssueStatusResultDto> BulkStatus([FromBody] BulkIssueStatusRequest request) =>
        ApiResponse<BulkIssueStatusResultDto>.Ok(_service.BulkSetIssueStatusByHandler(request));
}

/// <summary>
/// 統一標記（docs/archive/FEEDBACK-11-PLAN.md §6）：把一個問題在**尚未有人接手**的主機上一次標成結論。
///
/// 能力＝<c>Assign</c> **且** <c>Handle</c>（兩個 <c>[Permission]</c> 標註疊加＝都要滿足，
/// 同一個標註內的多個能力才是「任一」）。實務上只有 admin 兩者兼具，與需求「admin 使用者」
/// 一致，不必為此開新能力。刻意獨立成一個 controller 而不是掛在上面兩個類別裡：
/// 那兩個類別各自有自己的類別層能力，混進去會把對象搞混。
/// </summary>
[ApiController]
[Route("api/handling/issue-cases")]
[Permission(Capability.Assign)]
[Permission(Capability.Handle)]
public class IssueBulkCloseController : ControllerBase
{
    private readonly IssueHandlingCommandService _service;

    public IssueBulkCloseController(IssueHandlingCommandService service)
    {
        _service = service;
    }

    /// <summary>統一標記 modal 開啟時的預覽：逐主機的將標天數／覆蓋天數／略過原因</summary>
    [HttpGet("close-preview")]
    public ApiResponse<IssueBulkClosePreviewDto> ClosePreview(
        [FromQuery] string source, [FromQuery] int eventId,
        [FromQuery] string? from, [FromQuery] string? to) =>
        ApiResponse<IssueBulkClosePreviewDto>.Ok(
            _service.PreviewBulkClose(source, eventId, QueryStringParsing.ParseDate(from), QueryStringParsing.ParseDate(to)));

    [HttpPost("bulk-close")]
    public ApiResponse<BulkCloseIssueResultDto> BulkClose([FromBody] BulkCloseIssueRequest request) =>
        ApiResponse<BulkCloseIssueResultDto>.Ok(_service.BulkCloseIssue(request));
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
