using System.Globalization;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>操作紀錄查閱（docs/WEB-SPEC.md §9.11）。需 ViewAudit 能力（admin / serverAdmin）</summary>
[ApiController]
[Route("api/audit")]
[Permission(Capability.ViewAudit)]
public class AuditController : ControllerBase
{
    private readonly IAuditQueryService _service;

    public AuditController(IAuditQueryService service)
    {
        _service = service;
    }

    [HttpGet]
    public ApiResponse<PagedResult<AuditEntryDto>> Query(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] long? userId,
        [FromQuery] string? actions,
        [FromQuery] string? targetKind,
        [FromQuery] string? result,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var query = new AuditQuery
        {
            From = ParseDate(from),
            // 結束日期含當天：使用者選「到 7/21」的意思是包含 7/21 全天，
            // 不做這個處理的話當天的紀錄會全部查不到
            To = ParseDate(to)?.AddDays(1).AddSeconds(-1),
            UserId = userId,
            TargetKind = targetKind,
            Page = page,
            PageSize = pageSize
        };

        if (!string.IsNullOrWhiteSpace(actions))
        {
            query.Actions = actions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(result) &&
            Enum.TryParse<AuditResult>(result, ignoreCase: true, out var auditResult))
        {
            query.Result = auditResult;
        }

        return ApiResponse<PagedResult<AuditEntryDto>>.Ok(_service.Query(query));
    }

    [HttpGet("actions")]
    public ApiResponse<Dictionary<string, string>> Actions() =>
        ApiResponse<Dictionary<string, string>>.Ok(_service.GetActionNames());

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
}
