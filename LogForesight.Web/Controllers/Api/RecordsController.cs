using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using static LogForesight.Web.Controllers.Api.QueryStringParsing;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 問題查詢與風險日詳情（docs/WEB-SPEC.md §9.2、§9.3）。
///
/// 資源識別採 <c>{hostId}/{date}</c> 複合鍵（§7.2）：JSONL 後端的紀錄天然以
/// （主機,日期）為鍵、沒有代理數字 id，SQL 的 record_id 只是內部主鍵不對外。
/// 兩後端因此共用同一套路由。
///
/// 沒有 [Permission] 標註是刻意的——所有查詢都經 IVisibilityService 過濾，
/// 使用者只會拿到授權範圍內的資料（三層授權的第 3 層）。
/// </summary>
[ApiController]
[Route("api/records")]
public class RecordsController : ControllerBase
{
    private readonly RecordListQueryService _list;
    private readonly RecordDetailQueryService _detail;

    public RecordsController(RecordListQueryService list, RecordDetailQueryService detail)
    {
        _list = list;
        _detail = detail;
    }

    [HttpGet]
    public ApiResponse<PagedResult<RecordListItemDto>> Search(
        [FromQuery] string? hostIds,
        [FromQuery] string? groupIds,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? riskLevels,
        [FromQuery] string? categories,
        [FromQuery] string? severity,
        [FromQuery] int? eventId,
        [FromQuery] string? source,
        [FromQuery] string? statuses,
        [FromQuery] bool? overdue,
        [FromQuery] string? sort,
        [FromQuery] string dir = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var request = new RecordSearchRequest
        {
            HostIds = ParseLongs(hostIds),
            GroupIds = ParseLongs(groupIds),
            From = ParseDate(from),
            To = ParseDate(to),
            RiskLevels = ParseStrings(riskLevels),
            Categories = ParseStrings(categories),
            Severity = severity,
            EventId = eventId,
            Source = source,
            Statuses = ParseStrings(statuses),
            Overdue = overdue,
            SortKey = sort,
            Ascending = dir == "asc",
            Page = page,
            PageSize = pageSize
        };

        return ApiResponse<PagedResult<RecordListItemDto>>.Ok(_list.Search(request));
    }

    /// <summary>依主機彙總（日期合併）。篩選參數與 <see cref="Search"/> 同義，只是換視角</summary>
    [HttpGet("by-host")]
    public ApiResponse<PagedResult<RecordHostGroupDto>> ByHost(
        [FromQuery] string? hostIds,
        [FromQuery] string? groupIds,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? riskLevels,
        [FromQuery] string? categories,
        [FromQuery] string? severity,
        [FromQuery] int? eventId,
        [FromQuery] string? source,
        [FromQuery] string? sort,
        [FromQuery] string dir = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50) =>
        ApiResponse<PagedResult<RecordHostGroupDto>>.Ok(
            _list.SearchByHost(BuildRequest(hostIds, groupIds, from, to, riskLevels, categories, severity, eventId, source, sort, dir, page, pageSize)));

    /// <summary>依日期彙總（主機合併）</summary>
    [HttpGet("by-date")]
    public ApiResponse<PagedResult<RecordDateGroupDto>> ByDate(
        [FromQuery] string? hostIds,
        [FromQuery] string? groupIds,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? riskLevels,
        [FromQuery] string? categories,
        [FromQuery] string? severity,
        [FromQuery] int? eventId,
        [FromQuery] string? source,
        [FromQuery] string? sort,
        [FromQuery] string dir = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50) =>
        ApiResponse<PagedResult<RecordDateGroupDto>>.Ok(
            _list.SearchByDate(BuildRequest(hostIds, groupIds, from, to, riskLevels, categories, severity, eventId, source, sort, dir, page, pageSize)));

    /// <summary>依問題彙總（主機與日期都合併，docs/archive/FEEDBACK-4-PLAN.md §4）</summary>
    [HttpGet("by-issue")]
    public ApiResponse<PagedResult<IssueGroupDto>> ByIssue(
        [FromQuery] string? hostIds,
        [FromQuery] string? groupIds,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? riskLevels,
        [FromQuery] string? categories,
        [FromQuery] string? severity,
        [FromQuery] int? eventId,
        [FromQuery] string? source,
        [FromQuery] string? sort,
        [FromQuery] string dir = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50) =>
        ApiResponse<PagedResult<IssueGroupDto>>.Ok(
            _list.SearchByIssue(BuildRequest(hostIds, groupIds, from, to, riskLevels, categories, severity, eventId, source, sort, dir, page, pageSize)));

    private static RecordSearchRequest BuildRequest(
        string? hostIds, string? groupIds, string? from, string? to, string? riskLevels, string? categories,
        string? severity, int? eventId, string? source, string? sort, string dir, int page, int pageSize) =>
        new()
        {
            HostIds = ParseLongs(hostIds),
            GroupIds = ParseLongs(groupIds),
            From = ParseDate(from),
            To = ParseDate(to),
            RiskLevels = ParseStrings(riskLevels),
            Categories = ParseStrings(categories),
            Severity = severity,
            EventId = eventId,
            Source = source,
            SortKey = sort,
            Ascending = dir == "asc",
            Page = page,
            PageSize = pageSize
        };

    [HttpGet("{hostId:long}/{date}")]
    public ApiResponse<RecordDetailDto> GetDetail(long hostId, string date) =>
        ApiResponse<RecordDetailDto>.Ok(_detail.GetDetail(hostId, ParseRequiredDate(date)));

    /// <summary>報告全文（純文字，前端以等寬字型原樣呈現）</summary>
    [HttpGet("{hostId:long}/{date}/report")]
    public ApiResponse<string?> GetReport(long hostId, string date) =>
        ApiResponse<string?>.Ok(_detail.GetReport(hostId, ParseRequiredDate(date)));

    /// <summary>
    /// 單一問題簽章的先前處理歷史（docs/archive/FEEDBACK-5-PLAN.md §4）。issueKey 含 <c>|</c>，
    /// 走 query string 不進路由（路由樣板對 <c>|</c> 的處理因 routing 設定而異，query string 天生無此問題）。
    /// </summary>
    [HttpGet("{hostId:long}/{date}/handling/issue-history")]
    public ApiResponse<IssueHistoryDto> GetIssueHistory(long hostId, string date, [FromQuery] string issueKey) =>
        ApiResponse<IssueHistoryDto>.Ok(_detail.GetIssueHistory(hostId, ParseRequiredDate(date), issueKey));
}
