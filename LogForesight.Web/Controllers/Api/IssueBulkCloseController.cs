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
/// 能力＝<c>Handle</c>，範圍依是否另具 <c>Assign</c> 決定（O1）：
/// Assign＋Handle（admin）不限主機；只具 Handle（主機負責人）限自己負責的主機，其餘列入略過
/// （「不是你負責的主機」，過濾在 <see cref="IssueHandlingCommandService"/>、不信任前端），
/// 且不能勾「之後新出現的主機日也自動套用」（全站的決定，仍需 Assign）。
/// 只具 Assign 不具 Handle 者被類別層標註擋下。刻意獨立成一個 controller 而不是掛進上面的逐日處理類別：
/// 那個類別有自己的類別層能力，混進去會把對象搞混。
/// </summary>
[ApiController]
[Route("api/handling/issue-cases")]
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
