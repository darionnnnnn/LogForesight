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
        ApiResponse<DashboardDto>.Ok(_dashboard.GetSummary(days ?? DefaultDays));
}

/// <summary>
/// 執行中告示（docs/SCALE-FIX-PLAN-2026-08-06.md S-3）：**任何登入者**都讀得到，
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
        // 子進度優先（回饋十四輪 UI-6）：SchedulerRunState 把 netiq-ai／netiq-backpressure
        // 拆進獨立的 SubProgress* 欄位後（不再與主進度共用、互相覆蓋），這支一般使用者看的
        // 單一告示要自己決定「兩條軌只能顯示一條時，顯示哪一條」——選子進度：它代表較晚、
        // 較貼近「現在到底卡在哪」的階段（搜尋主線常常已經跑完，只剩 AI 佇列在後面消化），
        // 沿用改版前「後回報的蓋掉先回報的」單一欄位年代，AI 佇列進入消化階段後畫面自然
        // 「被它接手」的既有體驗——只是現在用明確的優先序表達，不再依賴欄位互相覆蓋的巧合。
        var hasSubProgress = _runState.SubProgressPhase != null;
        var phase = hasSubProgress ? _runState.SubProgressPhase : _runState.ProgressPhase;
        var done = hasSubProgress ? _runState.SubProgressDone : _runState.ProgressDone;
        var total = hasSubProgress ? _runState.SubProgressTotal : _runState.ProgressTotal;

        return ApiResponse<RunActivityDto>.Ok(new RunActivityDto
        {
            IsRunning = _runState.IsRunning,
            Done = done,
            Total = total,
            // 分母都是「主機日」，但一般使用者對這個單位沒有概念——本機路徑是逐日回補（天），
            // NetIQ 機房路徑是逐台主機（台）；netiq-ai／netiq-backpressure 是排隊的主機日數，
            // 用「件」而非「台」，避免使用者以為又要重新查一次（docs/FEEDBACK-12-PLAN.md §3.7）。
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
        [FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? handlingScope)
    {
        var toDate = ParseDate(to) ?? DateTime.Today;
        var fromDate = ParseDate(from) ?? toDate.AddDays(-29);

        return ApiResponse<ReportSummaryDto>.Ok(_reports.GetSummary(fromDate, toDate, handlingScope));
    }
    // 跨主機同簽章查詢（原 GET signature）已於 §4 併入「問題查詢」（eventId＋source＋依問題視角
    // 為嚴格超集），端點與 ReportService.FindSignature／SignatureHitDto 一併移除。
}
