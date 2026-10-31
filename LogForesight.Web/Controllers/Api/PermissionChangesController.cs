using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

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
    [Permission(Capability.ConfirmPermission)]
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
    [Permission(Capability.ConfirmPermission)]
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
    [Permission(Capability.ConfirmPermission)]
    public ApiResponse<List<PermissionCategoryOptionDto>> GetCategories() =>
        ApiResponse<List<PermissionCategoryOptionDto>>.Ok(
            PermissionCategory.GetAllLabels()
                .Select(kv => new PermissionCategoryOptionDto { Key = kv.Key, Label = kv.Value })
                .ToList());

    /// <summary>「全選符合目前篩選的權限異動」ID 清單（僅限待確認項目）</summary>
    [HttpGet("ids")]
    [Permission(Capability.ConfirmPermission)]
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
