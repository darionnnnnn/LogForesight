using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

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
