using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>執行監控（§9.10）。需 DevMonitor 或 Maintain 能力（dev / admin / serverAdmin，
/// docs/archive/FEEDBACK-6-PLAN.md §2——排程作業頁併入本頁後，serverAdmin 要能看見執行紀錄佐證排程活著）</summary>
[ApiController]
[Route("api/runs")]
[Permission(Capability.DevMonitor, Capability.Maintain)]
public class RunsController : ControllerBase
{
    /// <summary>days 未指定時的預設天數（§12：原 appsettings 的 Ui:RunMatrixDays，
    /// 前端期間鈕本來就明傳 days，這個值只是 API 的 fallback，改為常數）</summary>
    public const int DefaultRunSummaryDays = 14;

    /// <summary>summary 分頁的預設每頁天數（批次G）</summary>
    public const int DefaultRunSummaryPageSize = 30;

    private readonly RunMonitorService _service;
    private readonly ISystemSettingsStore _settingsStore;

    public RunsController(RunMonitorService service, ISystemSettingsStore settingsStore)
    {
        _service = service;
        _settingsStore = settingsStore;
    }

    /// <summary>
    /// 執行總表分頁（批次G）：加入 page／pageSize 支援，天數上限放寬為
    /// max(90, RunLogRetentionDays)，讓「全部」選項涵蓋完整保留區間。
    /// </summary>
    [HttpGet("summary")]
    public ApiResponse<RunDaySummaryPageDto> Summary(
        [FromQuery] int? days, [FromQuery] int? page, [FromQuery] int? pageSize)
    {
        var maxDays = MaxRunDays();
        var clampedDays = Math.Clamp(days ?? DefaultRunSummaryDays, 1, maxDays);
        var clampedPage = Math.Clamp(page ?? 1, 1, int.MaxValue);
        var clampedPageSize = Math.Clamp(pageSize ?? DefaultRunSummaryPageSize, 1, 90);

        var totalPages = Math.Max(1, (int)Math.Ceiling((double)clampedDays / clampedPageSize));
        clampedPage = Math.Clamp(clampedPage, 1, totalPages);

        var items = _service.GetDaySummaries(clampedDays, clampedPage, clampedPageSize);
        return ApiResponse<RunDaySummaryPageDto>.Ok(new RunDaySummaryPageDto
        {
            Items = items,
            TotalDays = clampedDays,
            TotalPages = totalPages,
            Page = clampedPage,
            PageSize = clampedPageSize,
            MaxDays = maxDays
        });
    }

    [HttpGet("day/{date}")]
    public ApiResponse<List<RunDayHostStatusDto>> DayDetail(string date)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            throw DomainException.Validation("日期格式必須為 yyyy-MM-dd。");

        return ApiResponse<List<RunDayHostStatusDto>>.Ok(_service.GetDayDetail(parsed));
    }

    [HttpGet("{runId:long}")]
    public ApiResponse<RunDetailDto> Detail(long runId) =>
        ApiResponse<RunDetailDto>.Ok(_service.GetDetail(runId));

    /// <summary>errors/list 與 summary 用同一個天數上限（體檢輪）：原本仍夾 90，
    /// 選「全部」（RunLogRetentionDays）時執行總表算 120 天、這兩頁籤靜默只給 90 天，口徑不一致</summary>
    private int MaxRunDays() => Math.Max(90, _settingsStore.Get().RunLogRetentionDays);

    [HttpGet("errors")]
    public ApiResponse<List<RunErrorGroupDto>> Errors([FromQuery] int days = 14) =>
        ApiResponse<List<RunErrorGroupDto>>.Ok(_service.GetErrorSummary(Math.Clamp(days, 1, MaxRunDays())));

    /// <summary>執行紀錄分頁（回饋十七輪批次F-3）：每一筆 BatchRun 的扁平清單</summary>
    [HttpGet("list")]
    public ApiResponse<List<RunListItemDto>> List([FromQuery] int days = 14) =>
        ApiResponse<List<RunListItemDto>>.Ok(_service.GetRunList(Math.Clamp(days, 1, MaxRunDays())));
}
