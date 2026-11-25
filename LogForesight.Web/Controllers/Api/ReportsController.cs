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

    /// <summary>報表三個端點共用的期間解析：to 預設昨天（前端一律帶明確日期）、from 預設 to 往前 29 天、最長 366 天、起訖顛倒擲錯</summary>
    private static (DateTime From, DateTime To) ParseReportRange(string? from, string? to)
    {
        var toDefault = DateTime.Today.AddDays(-1);
        var (fromDate, toDate) = ParseDateRange(
            from, to,
            fromDefault: (ParseOptionalDate(to) ?? toDefault).AddDays(-29),
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
        var (fromDate, toDate) = ParseReportRange(from, to);
        return ApiResponse<ReportSummaryDto>.Ok(_reports.GetSummary(fromDate, toDate, handlingScope, compare));
    }
    /// <summary>
    /// 下載報表某張表的完整 CSV（F20-4）：trend（逐日趨勢）、hosts（主機排行全部）、issues（問題排行全部）。
    /// 走與報表總覽同一段查詢，受使用者可見範圍限制（授權同 summary：登入即可，Service 層依可見範圍過濾）；
    /// 逐列串流寫入回應，列數超過 <see cref="ReportCsv.MaxRows"/> 在寫出前就回 400 並要求縮小期間。
    /// 檔名與內容規則見 <see cref="ReportCsv"/>（UTF-8 BOM、公式注入防護）。
    /// </summary>
    [HttpGet("export/{table}")]
    public async Task ExportCsv(
        string table, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] string? handlingScope, [FromQuery] string? compare = null, CancellationToken ct = default)
    {
        var (fromDate, toDate) = ParseReportRange(from, to);
        var data = _reports.GetCsvTable(table, fromDate, toDate, handlingScope, compare);   // 上限檢查在這裡擲出，尚未開始寫回應
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"report-{table}-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.csv\"";
        Response.Headers.CacheControl = "no-store";
        await ReportCsv.WriteAsync(Response.Body, data, ct);
    }

    // 跨主機同簽章查詢（原 GET signature）已於 §4 併入「問題查詢」（eventId＋source＋依問題視角
    // 為嚴格超集），端點與 ReportService.FindSignature／SignatureHitDto 一併移除。
}
