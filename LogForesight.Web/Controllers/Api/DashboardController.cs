using LogForesight.Web.Configuration;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using static LogForesight.Web.Controllers.Api.QueryStringParsing;

namespace LogForesight.Web.Controllers.Api;

/// <summary>儀表板（docs/WEB-SPEC.md §9.1）。一次回傳全部區塊，避免首頁發五個請求</summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    /// <summary>days 未指定時的預設期間（§12：原 appsettings 的 Ui:DashboardDefaultDays，
    /// 前端另有 localStorage 期間記憶，這個值只是 API 的 fallback，改為常數）</summary>
    public const int DefaultDays = 7;

    private readonly DashboardService _dashboard;

    public DashboardController(DashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    [HttpGet("summary")]
    public ApiResponse<DashboardDto> Summary([FromQuery] int? days) =>
        // Clamp 同 host-detail 慣例（1..90）：未夾住時任意整數（含負數／超大值）會直接進
        // DateTime.Today.AddDays(-days+1) 算出離譜的查詢區間
        ApiResponse<DashboardDto>.Ok(_dashboard.GetSummary(Math.Clamp(days ?? DefaultDays, 1, 90)));
}

/// <summary>
/// 執行中告示（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3）：**任何登入者**都讀得到，
/// 刻意不掛 <c>[Permission]</c>——分析與站台跑在同一個行程，變慢的是所有人的畫面，
/// 只讓維運看得到原因等於沒有配套。回傳只有「在不在跑、跑到哪」，不含排程設定、
/// 觸發來源與上次成敗（那些在 <c>/api/admin/schedule/status</c>，維運視角）。
///
/// 路由刻意不放在 <c>api/admin/</c> 底下：那個前綴在本專案是「需要管理權限」的訊號，
/// 一般使用者讀得到的東西擺進去，只會讓下一個人誤判這支的權限範圍。
/// </summary>
[ApiController]
[Route("api/run-activity")]
public class RunActivityController : ControllerBase
{
    private readonly SchedulerRunState _runState;

    public RunActivityController(SchedulerRunState runState)
    {
        _runState = runState;
    }

    [HttpGet]
    public ApiResponse<RunActivityDto> Get()
    {
        // 單一告示只能顯示一條進度，主／子軌的取捨（子進度優先）由 SchedulerRunState.LatestActivity
        // 單點決定（回饋十四輪 UI-6 體檢）——這支與健康診斷共用同一個選擇邏輯，不各自記得。
        var (phase, done, total) = _runState.LatestActivity();

        return ApiResponse<RunActivityDto>.Ok(new RunActivityDto
        {
            IsRunning = _runState.IsRunning,
            Done = done,
            Total = total,
            // 本機路徑是逐日回補（天），NetIQ 機房路徑是逐台主機（台）——「主機日」這個單位
            // 一般使用者沒有概念。netiq-ai／netiq-backpressure 這條 AI 背景消化軌的分母是
            // **排入 AI 佇列的件數**（回饋二十七輪作業 B3：背壓期間改報 AI 消化進度，
            // 不再沿用主機日數字），用「件」而非「台」，避免使用者以為又要重新查一次
            // （docs/archive/FEEDBACK-12-PLAN.md §3.7）。
            UnitText = phase switch
            {
                "local" => "天",
                "netiq" => "台",
                "netiq-ai" or "netiq-backpressure" => "件",
                _ => null
            }
        });
    }
}

/// <summary>主機詳情／時間軸（§9.4）</summary>
[ApiController]
[Route("api/host-detail")]
public class HostDetailController : ControllerBase
{
    private readonly RecordDetailQueryService _service;

    public HostDetailController(RecordDetailQueryService service)
    {
        _service = service;
    }

    [HttpGet("{hostId:long}")]
    public ApiResponse<HostDetailDto> Get(long hostId, [FromQuery] int days = 30) =>
        ApiResponse<HostDetailDto>.Ok(_service.GetHostDetail(hostId, Math.Clamp(days, 7, 90)));

    /// <summary>某個問題（Source+EventId）的逐日發生明細（docs/archive/FEEDBACK-4-PLAN.md §3）：
    /// 重點問題彙總表某一列展開時查詢，days 與外層時間軸期間一致</summary>
    [HttpGet("{hostId:long}/issues")]
    public ApiResponse<HostIssueOccurrenceDto> Issues(
        long hostId, [FromQuery] string source, [FromQuery] int eventId, [FromQuery] int days = 30) =>
        ApiResponse<HostIssueOccurrenceDto>.Ok(_service.GetHostIssueOccurrences(hostId, source, eventId, Math.Clamp(days, 7, 90)));
}

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
