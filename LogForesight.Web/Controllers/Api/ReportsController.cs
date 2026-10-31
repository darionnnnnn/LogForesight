using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using static LogForesight.Web.Controllers.Api.QueryStringParsing;

namespace LogForesight.Web.Controllers.Api;

/// <summary>報表（§9.6）</summary>
[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly ReportService _reports;

    public ReportsController(ReportService reports)
    {
        _reports = reports;
    }

    [HttpGet("summary")]
    public ApiResponse<ReportSummaryDto> Summary(
        [FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? handlingScope, [FromQuery] string? compare = null)
    {
        // 未帶 to 時的預設終點錨在昨天（回饋十九輪批次C）：前端一律會帶明確日期
        // toDefault 為昨天；fromDefault 依實際解析出的 to 往前 29 天（報表 JS 的 setRange 語意）
        var toDefault = DateTime.Today.AddDays(-1);
        var (fromDate, toDate) = ParseDateRange(
            from, to,
            fromDefault: (ParseDate(to) ?? toDefault).AddDays(-29),
            toDefault: toDefault,
            throwOnReversed: true,
            maxDays: 366);

        return ApiResponse<ReportSummaryDto>.Ok(_reports.GetSummary(fromDate!.Value, toDate!.Value, handlingScope, compare));
    }
    // 跨主機同簽章查詢（原 GET signature）已於 §4 併入「問題查詢」（eventId＋source＋依問題視角
    // 為嚴格超集），端點與 ReportService.FindSignature／SignatureHitDto 一併移除。
}
