using System.Globalization;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>儀表板（docs/WEB-SPEC.md §9.1）。一次回傳全部區塊，避免首頁發五個請求</summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboard;
    private readonly WebAppSettings _settings;

    public DashboardController(IDashboardService dashboard, WebAppSettings settings)
    {
        _dashboard = dashboard;
        _settings = settings;
    }

    [HttpGet("summary")]
    public ApiResponse<DashboardDto> Summary([FromQuery] int? days) =>
        ApiResponse<DashboardDto>.Ok(
            _dashboard.GetSummary(days ?? _settings.Ui.DashboardDefaultDays));
}

/// <summary>主機詳情／時間軸（§9.4）</summary>
[ApiController]
[Route("api/host-detail")]
public class HostDetailController : ControllerBase
{
    private readonly IRecordQueryService _service;

    public HostDetailController(IRecordQueryService service)
    {
        _service = service;
    }

    [HttpGet("{hostId:long}")]
    public ApiResponse<HostDetailDto> Get(long hostId, [FromQuery] int days = 30) =>
        ApiResponse<HostDetailDto>.Ok(_service.GetHostDetail(hostId, Math.Clamp(days, 7, 90)));
}

/// <summary>報表（§9.6）</summary>
[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reports;

    public ReportsController(IReportService reports)
    {
        _reports = reports;
    }

    [HttpGet("summary")]
    public ApiResponse<ReportSummaryDto> Summary([FromQuery] string? from, [FromQuery] string? to)
    {
        var toDate = ParseDate(to) ?? DateTime.Today;
        var fromDate = ParseDate(from) ?? toDate.AddDays(-29);

        return ApiResponse<ReportSummaryDto>.Ok(_reports.GetSummary(fromDate, toDate));
    }

    [HttpGet("signature")]
    public ApiResponse<List<SignatureHitDto>> Signature([FromQuery] int eventId, [FromQuery] string? source)
    {
        if (eventId <= 0)
            throw DomainException.Validation("請輸入要查詢的 Event ID。");

        return ApiResponse<List<SignatureHitDto>>.Ok(_reports.FindSignature(eventId, source));
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
}
