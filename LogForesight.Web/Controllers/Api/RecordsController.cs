using System.Globalization;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

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
    private readonly IRecordQueryService _service;

    public RecordsController(IRecordQueryService service)
    {
        _service = service;
    }

    [HttpGet]
    public ApiResponse<PagedResult<RecordListItemDto>> Search(
        [FromQuery] string? hostIds,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? riskLevels,
        [FromQuery] string? categories,
        [FromQuery] string? severity,
        [FromQuery] int? eventId,
        [FromQuery] string? source,
        [FromQuery] string? statuses,
        [FromQuery] bool? overdue,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var request = new RecordSearchRequest
        {
            HostIds = ParseLongs(hostIds),
            From = ParseDate(from),
            To = ParseDate(to),
            RiskLevels = ParseStrings(riskLevels),
            Categories = ParseStrings(categories),
            Severity = severity,
            EventId = eventId,
            Source = source,
            Statuses = ParseStrings(statuses),
            Overdue = overdue,
            Page = page,
            PageSize = pageSize
        };

        return ApiResponse<PagedResult<RecordListItemDto>>.Ok(_service.Search(request));
    }

    /// <summary>依主機彙總（日期合併）。篩選參數與 <see cref="Search"/> 同義，只是換視角</summary>
    [HttpGet("by-host")]
    public ApiResponse<PagedResult<RecordHostGroupDto>> ByHost(
        [FromQuery] string? hostIds,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? riskLevels,
        [FromQuery] string? categories,
        [FromQuery] string? severity,
        [FromQuery] int? eventId,
        [FromQuery] string? source,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50) =>
        ApiResponse<PagedResult<RecordHostGroupDto>>.Ok(
            _service.SearchByHost(BuildRequest(hostIds, from, to, riskLevels, categories, severity, eventId, source, page, pageSize)));

    /// <summary>依日期彙總（主機合併）</summary>
    [HttpGet("by-date")]
    public ApiResponse<PagedResult<RecordDateGroupDto>> ByDate(
        [FromQuery] string? hostIds,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? riskLevels,
        [FromQuery] string? categories,
        [FromQuery] string? severity,
        [FromQuery] int? eventId,
        [FromQuery] string? source,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50) =>
        ApiResponse<PagedResult<RecordDateGroupDto>>.Ok(
            _service.SearchByDate(BuildRequest(hostIds, from, to, riskLevels, categories, severity, eventId, source, page, pageSize)));

    private static RecordSearchRequest BuildRequest(
        string? hostIds, string? from, string? to, string? riskLevels, string? categories,
        string? severity, int? eventId, string? source, int page, int pageSize) =>
        new()
        {
            HostIds = ParseLongs(hostIds),
            From = ParseDate(from),
            To = ParseDate(to),
            RiskLevels = ParseStrings(riskLevels),
            Categories = ParseStrings(categories),
            Severity = severity,
            EventId = eventId,
            Source = source,
            Page = page,
            PageSize = pageSize
        };

    [HttpGet("{hostId:long}/{date}")]
    public ApiResponse<RecordDetailDto> GetDetail(long hostId, string date) =>
        ApiResponse<RecordDetailDto>.Ok(_service.GetDetail(hostId, RequireDate(date)));

    /// <summary>報告全文（純文字，前端以等寬字型原樣呈現）</summary>
    [HttpGet("{hostId:long}/{date}/report")]
    public ApiResponse<string?> GetReport(long hostId, string date) =>
        ApiResponse<string?>.Ok(_service.GetReport(hostId, RequireDate(date)));

    private static List<long>? ParseLongs(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => long.TryParse(s, out var value) ? value : (long?)null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

    private static List<string>? ParseStrings(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;

    private static DateTime RequireDate(string value) =>
        ParseDate(value) ?? throw DomainException.Validation("日期格式必須為 yyyy-MM-dd。");
}
