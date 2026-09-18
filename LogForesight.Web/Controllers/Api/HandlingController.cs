using LogForesight.Core.Models;
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
/// 統一標記（docs/archive/FEEDBACK-11-PLAN.md §6）：把一個問題在**尚未有人接手**的主機上一次標成結論。
///
/// 能力＝<c>Assign</c> **且** <c>Handle</c>（兩個 <c>[Permission]</c> 標註疊加＝都要滿足，
/// 同一個標註內的多個能力才是「任一」）。實務上只有 admin 兩者兼具，與需求「admin 使用者」
/// 一致，不必為此開新能力。刻意獨立成一個 controller 而不是掛進上面的逐日處理類別：
/// 那個類別有自己的類別層能力，混進去會把對象搞混。
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
        [FromQuery] string? from, [FromQuery] string? to)
    {
        var (parsedFrom, parsedTo) = QueryStringParsing.ParseDateRange(from, to);
        return ApiResponse<IssueBulkClosePreviewDto>.Ok(
            _service.PreviewBulkClose(source, eventId, parsedFrom, parsedTo));
    }

    [HttpPost("bulk-close")]
    public ApiResponse<BulkCloseIssueResultDto> BulkClose([FromBody] BulkCloseIssueRequest request) =>
        ApiResponse<BulkCloseIssueResultDto>.Ok(_service.BulkCloseIssue(request));
}

/// <summary>權限異動檢核（§9.5）</summary>
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
    public ApiResponse<PagedResult<PermissionChangeDto>> Query(
        [FromQuery] string? q,
        [FromQuery] string? subnet,
        [FromQuery] string? category,
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string sort = "detectedAt",
        [FromQuery] string dir = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var (parsedFrom, parsedTo) = QueryStringParsing.ParseDateRange(from, to);
        var request = new PermissionChangeQueryRequest
        {
            Keyword = q,
            Subnet = subnet,
            Categories = QueryStringParsing.ParseStrings(category),
            Status = status,
            Source = source,
            From = parsedFrom,
            To = parsedTo?.AddDays(1).AddSeconds(-1),
            Sort = sort,
            Ascending = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase),
            Page = page,
            PageSize = pageSize
        };
        return ApiResponse<PagedResult<PermissionChangeDto>>.Ok(_service.Query(request));
    }

    /// <summary>
    /// 某主機某日的權限異動報告全文（逐項「請確認」的完整敘事）。查無回 null，不是錯誤。
    /// </summary>
    [HttpGet("report")]
    public ApiResponse<ReportViewDto?> GetReport([FromQuery] string host, [FromQuery] string date)
    {
        if (string.IsNullOrWhiteSpace(host)) throw DomainException.Validation("請指定主機。");
        return ApiResponse<ReportViewDto?>.Ok(_service.GetReport(host, QueryStringParsing.ParseRequiredDate(date)));
    }

    /// <summary>
    /// 可用的異動類別清單（key 與中文標籤）。
    /// 前端的類別篩選必須靠它才列得全——只從當頁資料收集的話，
    /// 沒有出現在當頁的類別就選不到，等於篩不到那一類。
    /// </summary>
    [HttpGet("categories")]
    public ApiResponse<List<PermissionCategoryOptionDto>> GetCategories() =>
        ApiResponse<List<PermissionCategoryOptionDto>>.Ok(
            PermissionCategory.GetAllLabels()
                .Select(kv => new PermissionCategoryOptionDto { Key = kv.Key, Label = kv.Value })
                .ToList());

    /// <summary>「全選符合目前篩選的權限異動」ID 清單（僅限待確認項目）</summary>
    [HttpGet("ids")]
    public ApiResponse<PermissionChangeIdListDto> GetMatchingChangeIds(
        [FromQuery] string? q,
        [FromQuery] string? subnet,
        [FromQuery] string? category,
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string sort = "detectedAt",
        [FromQuery] string dir = "desc")
    {
        var (parsedFrom, parsedTo) = QueryStringParsing.ParseDateRange(from, to);
        var request = new PermissionChangeQueryRequest
        {
            Keyword = q,
            Subnet = subnet,
            Categories = QueryStringParsing.ParseStrings(category),
            Status = status,
            Source = source,
            From = parsedFrom,
            To = parsedTo?.AddDays(1).AddSeconds(-1),
            Sort = sort,
            Ascending = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase)
        };
        return ApiResponse<PermissionChangeIdListDto>.Ok(_service.MatchingChangeIds(request));
    }

    [HttpPut("{changeId}/confirm")]
    [Permission(Capability.ConfirmPermission)]
    public ApiResponse<PermissionChangeDto> Confirm(
        string changeId, [FromBody] ConfirmPermissionChangeRequest request) =>
        ApiResponse<PermissionChangeDto>.Ok(_service.Confirm(changeId, request));

    /// <summary>批次確認權限異動（授權操作或標記可疑）</summary>
    [HttpPost("confirm/batch")]
    [Permission(Capability.ConfirmPermission)]
    public ApiResponse<BatchConfirmPermissionChangesResultDto> ConfirmBatch(
        [FromBody] BatchConfirmPermissionChangesRequest request) =>
        ApiResponse<BatchConfirmPermissionChangesResultDto>.Ok(_service.ConfirmBatch(request));
}
