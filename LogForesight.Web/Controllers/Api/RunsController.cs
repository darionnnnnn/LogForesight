using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
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

    /// <summary>診斷匯出同一時間只允許一個（組裝要讀整段期間的診斷行並逐行遮罩）；不與校準共用閘門，兩者互不相關</summary>
    private static readonly SemaphoreSlim ExportAdmission = new(1, 1);

    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly RunMonitorService _service;
    private readonly ISystemSettingsStore _settingsStore;
    private readonly IRunExportService _export;
    private readonly IAuditService _audit;

    public RunsController(RunMonitorService service, ISystemSettingsStore settingsStore,
        IRunExportService export, IAuditService audit)
    {
        _service = service;
        _settingsStore = settingsStore;
        _export = export;
        _audit = audit;
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
    public ApiResponse<RunErrorSummaryDto> Errors([FromQuery] int days = 14, [FromQuery] string minLevel = RunMonitorService.MinLevelError) =>
        ApiResponse<RunErrorSummaryDto>.Ok(_service.GetErrorSummary(Math.Clamp(days, 1, MaxRunDays()), minLevel));

    /// <summary>
    /// 匯出給 AI 分析的診斷資料（第 63 輪批次 C）：一律去識別化、同類訊息收斂、沒有開關。
    /// 涵蓋全站資料，須可檢視全部主機（serverAdmin 回 403）；同一時間只允許一個匯出（503 capture_busy）。
    /// 去識別化失敗（字典來源讀不到、結果掃描有殘留）回 500 export_deidentification_failed，不給任何檔案。
    /// </summary>
    [RequireViewAll(ExportForbiddenMessage)]
    [HttpGet("export")]
    public IActionResult Export([FromQuery] int days = DefaultRunSummaryDays)
    {
        if (!ExportAdmission.Wait(0))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse.Fail(ApiErrorCodes.CaptureBusy, "另一個診斷資料匯出正在執行，請稍後重試。"));

        try
        {
            var clamped = Math.Clamp(days, 1, MaxRunDays());
            var cancellationToken = ControllerContext?.HttpContext?.RequestAborted ?? CancellationToken.None;

            // 取消（用戶端斷線）時例外直接穿出，由 ApiExceptionFilter 轉成 499；不寫稽核，也不留檔案
            var result = _export.Build(clamped, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            RecordAudit(result);
            return new FileContentResult(result.Bytes, "application/json")
            {
                FileDownloadName = $"run-diagnostics-{DateTime.Today:yyyyMMdd}-{clamped}d.json"
            };
        }
        finally
        {
            ExportAdmission.Release();
        }
    }

    private const string ExportForbiddenMessage = "診斷資料涵蓋全站執行紀錄，須由可檢視全部主機的管理者匯出。";

    /// <summary>稽核只記天數、組數、趟數、位元組數與各類代號數，不記任何內容；寫入失敗不中斷（WEB-SPEC §11-4）</summary>
    private void RecordAudit(RunExportResult result)
    {
        try
        {
            _audit.Record(
                action: AuditActions.RunDiagnosticsExport,
                summary: $"匯出排程診斷資料（近 {result.Days} 天）",
                targetKind: "run_diagnostics",
                targetId: DateTime.Today.ToString("yyyyMMdd"),
                detail: new
                {
                    Days = result.Days,
                    Runs = result.RunCount,
                    SignalGroups = result.SignalGroupCount,
                    AbnormalRuns = result.AbnormalRunCount,
                    AiCases = result.AiCaseCount,
                    Bytes = result.Bytes.Length,
                    Tokens = result.TokenCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .ToDictionary(kv => kv.Key, kv => kv.Value)
                });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "診斷匯出的稽核寫入失敗（不影響匯出結果）");
        }
    }

    /// <summary>執行紀錄分頁（回饋十七輪批次F-3）：每一筆 BatchRun 的扁平清單</summary>
    [HttpGet("list")]
    public ApiResponse<List<RunListItemDto>> List([FromQuery] int days = 14) =>
        ApiResponse<List<RunListItemDto>>.Ok(_service.GetRunList(Math.Clamp(days, 1, MaxRunDays())));
}
