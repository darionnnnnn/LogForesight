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
    private readonly ReportEffectivenessService _effectiveness;
    private readonly ReportCategoryTrendService _categoryTrend;

    public ReportsController(
        ReportService reports, ReportEffectivenessService effectiveness, ReportCategoryTrendService categoryTrend)
    {
        _reports = reports;
        _effectiveness = effectiveness;
        _categoryTrend = categoryTrend;
    }

    /// <summary>期間解析與上限同 summary：to 預設昨天、from 預設 to 往前 29 天、最長 366 天</summary>
    private static (DateTime From, DateTime To) ParseReportRange(string? from, string? to)
    {
        var toDefault = DateTime.Today.AddDays(-1);
        var (fromDate, toDate) = ParseDateRange(
            from, to,
            fromDefault: (ParseDate(to) ?? toDefault).AddDays(-29),
            toDefault: toDefault,
            throwOnReversed: true,
            maxDays: 366);
        return (fromDate!.Value, toDate!.Value);
    }

    /// <summary>處理成效：風險日處理三態分桶、未結案帳齡、已處理後再發生率（授權同 summary：登入即可，Service 層依可見範圍過濾）</summary>
    [HttpGet("effectiveness")]
    public ApiResponse<ReportEffectivenessDto> Effectiveness([FromQuery] string? from, [FromQuery] string? to)
    {
        var (f, t) = ParseReportRange(from, to);
        return ApiResponse<ReportEffectivenessDto>.Ok(_effectiveness.Get(f, t));
    }

    /// <summary>類型趨勢：類別 × 時間桶的問題日熱度表</summary>
    [HttpGet("category-trend")]
    public ApiResponse<ReportCategoryTrendDto> CategoryTrend([FromQuery] string? from, [FromQuery] string? to)
    {
        var (f, t) = ParseReportRange(from, to);
        return ApiResponse<ReportCategoryTrendDto>.Ok(_categoryTrend.Get(f, t));
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
