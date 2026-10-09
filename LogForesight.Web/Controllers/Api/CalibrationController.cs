using System.Text.Encodings.Web;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 校準數值匯出 API（「系統管理 > 校準數值匯出」頁）。整個 Controller 需要 Maintain 能力
/// </summary>
[ApiController]
[Route("api/admin/calibration")]
[Permission(Capability.Maintain)]
public class CalibrationController : ControllerBase
{
    private const long MaximumExportBytes = 64L * 1024 * 1024;
    private static readonly SemaphoreSlim RequestAdmission = new(1, 1);
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly CalibrationService _calibrationService;
    private readonly IAuditService _audit;
    private readonly long _maximumExportBytes;

    public CalibrationController(CalibrationService calibrationService, IAuditService audit,
        long maximumExportBytes = MaximumExportBytes)
    {
        _calibrationService = calibrationService ?? throw new ArgumentNullException(nameof(calibrationService));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        if (maximumExportBytes is <= 0 or > MaximumExportBytes) throw new ArgumentOutOfRangeException(nameof(maximumExportBytes));
        _maximumExportBytes = maximumExportBytes;
    }

    /// <summary>
    /// 取得四項校準指標的判定摘要。這是重查詢，由前端「重新計算」按鈕觸發，
    /// 因此一律強制重算——使用者按下按鈕就是要看當下的數字，回快取等於按鈕沒作用。
    /// 匯出端則沿用快取（同一次匯出不必把整組查詢再跑一遍）。
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        if (!RequestAdmission.Wait(0))
            return CapacityResponse(StatusCodes.Status503ServiceUnavailable, "capture_busy", "另一個校準資料擷取或匯出正在執行，請稍後重試。");
        var admissionTransferred = false;
        try
        {
            var result = _calibrationService.WithAssessment(forceRefresh: true, (summary, budget) =>
            {
                var budgetLifetime = budget.Retain();
                IDisposable? outputReservation = null;
                try
                {
                    outputReservation = budget.ReserveTransient(128L * 1024 * 1024, "bounded status JSON response buffer");
                    var response = new CalibrationBudgetedOkResult(ApiResponse<CalibrationStatusDto>.Ok(
                        CalibrationStatusDto.From(summary)), () => RequestAdmission.Release(), budgetLifetime, outputReservation);
                    var httpContext = ControllerContext?.HttpContext;
                    if (httpContext != null)
                        httpContext.Response.OnCompleted(() => { response.Dispose(); return Task.CompletedTask; });
                    outputReservation = null;
                    budgetLifetime = null!;
                    admissionTransferred = true;
                    return response;
                }
                finally
                {
                    outputReservation?.Dispose();
                    budgetLifetime?.Dispose();
                }
            }, ControllerContext?.HttpContext?.RequestAborted ?? CancellationToken.None);
            return result;
        }
        catch (CalibrationCapacityException exception)
        {
            return CapacityResponse(exception.Retryable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status413PayloadTooLarge,
                exception.Retryable ? "capture_busy" : "capacity_exceeded", exception.Message);
        }
        finally
        {
            if (!admissionTransferred) RequestAdmission.Release();
        }
    }

    /// <summary>
    /// 匯出校準數值封裝包為 JSON 檔案
    /// </summary>
    [HttpGet("export")]
    public IActionResult Export(
        [FromQuery(Name = "override")] bool isOverride = false,
        [FromQuery(Name = "detail")] string? detail = null)
    {
        if (!RequestAdmission.Wait(0))
            return CapacityResponse(StatusCodes.Status503ServiceUnavailable, "capture_busy", "另一個校準資料擷取或匯出正在執行，請稍後重試。");
        BoundedMemoryStream? buffer = null;
        IDisposable? budgetLifetime = null;
        IDisposable? outputBudgetReservation = null;
        var admissionTransferred = false;
        try
        {
        var summaryOnly = string.Equals(detail, "summary", StringComparison.OrdinalIgnoreCase);

        // 封包組裝會以同一有界 SQL snapshot 產生摘要與明細，資格檢查也直接採用該摘要。
        return _calibrationService.WithExportPackage(summaryOnly, (package, budget) =>
        {
        var cancellationToken = ControllerContext?.HttpContext?.RequestAborted ?? CancellationToken.None;
        cancellationToken.ThrowIfCancellationRequested();
        // 匯出資格及稽核必須對應實際回傳封包；強制評估之後設定或證據仍可能變更。
        var summary = package.Summary;
        var ineligible = new List<string>();

        if (summary.PrtgValueBaseline.Status is not (CalibrationStatus.Available or CalibrationStatus.Sufficient))
            ineligible.Add($"{summary.PrtgValueBaseline.ItemName}（{summary.PrtgValueBaseline.StatusText}）");
        if (summary.PrtgRuleThresholds.Status is not (CalibrationStatus.Available or CalibrationStatus.Sufficient))
            ineligible.Add($"{summary.PrtgRuleThresholds.ItemName}（{summary.PrtgRuleThresholds.StatusText}）");
        if (summary.TriggeredFetchMagnitude.Status is not (CalibrationStatus.Available or CalibrationStatus.Sufficient))
            ineligible.Add($"{summary.TriggeredFetchMagnitude.ItemName}（{summary.TriggeredFetchMagnitude.StatusText}）");
        if (summary.ResidualCredentialThresholds.Status is not (CalibrationStatus.Available or CalibrationStatus.Sufficient))
            ineligible.Add($"{summary.ResidualCredentialThresholds.ItemName}（{summary.ResidualCredentialThresholds.StatusText}）");

        if (ineligible.Count > 0 && !isOverride)
        {
            throw DomainException.Validation(
                $"校準資料累積量未達標（{string.Join("、", ineligible)}），需全數達到「可用」以上才允許匯出；若確認要強制匯出請勾選「仍要匯出」。");
        }

        budgetLifetime = budget.Retain();
        outputBudgetReservation = budget.ReserveTransient(128L * 1024 * 1024, "bounded JSON response buffer");
        buffer = new BoundedMemoryStream(_maximumExportBytes);
        JsonSerializer.Serialize(buffer, package, ExportJsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        buffer.Position = 0;
        var fileName = summaryOnly
            ? $"calibration-{DateTime.Today:yyyyMMdd}-summary.json"
            : $"calibration-{DateTime.Today:yyyyMMdd}.json";

        _audit.Record(
            action: AuditActions.CalibrationExport,
            summary: $"匯出校準數值（PRTG值型基線：{summary.PrtgValueBaseline.StatusText}、PRTG規則門檻：{summary.PrtgRuleThresholds.StatusText}、數值取得量級：{summary.TriggeredFetchMagnitude.StatusText}、殘留判定門檻：{summary.ResidualCredentialThresholds.StatusText}{(isOverride ? "，覆寫匯出" : "")}）",
            targetKind: "calibration_data",
            targetId: DateTime.Today.ToString("yyyyMMdd"),
            detail: new
            {
                PrtgValueBaseline = summary.PrtgValueBaseline.Status.ToString(),
                PrtgRuleThresholds = summary.PrtgRuleThresholds.Status.ToString(),
                TriggeredFetchMagnitude = summary.TriggeredFetchMagnitude.Status.ToString(),
                ResidualCredentialThresholds = summary.ResidualCredentialThresholds.Status.ToString(),
                Override = isOverride,
                Detail = summaryOnly ? "summary" : "full"
            });

        var result = new CalibrationFileStreamResult(buffer!, "application/json", fileName,
            () => RequestAdmission.Release(), budgetLifetime!, outputBudgetReservation!);
        if (ControllerContext?.HttpContext is { } httpContext)
            httpContext.Response.OnCompleted(() => { result.Dispose(); return Task.CompletedTask; });
        buffer = null;
        budgetLifetime = null;
        outputBudgetReservation = null;
        admissionTransferred = true;
        return result;
        }, ControllerContext?.HttpContext?.RequestAborted ?? CancellationToken.None);
        }
        catch (CalibrationCapacityException exception)
        {
            buffer?.Dispose();
            return CapacityResponse(exception.Retryable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status413PayloadTooLarge,
                exception.Retryable ? "capture_busy" : "capacity_exceeded", exception.Message);
        }
        catch
        {
            buffer?.Dispose();
            throw;
        }
        finally
        {
            outputBudgetReservation?.Dispose();
            budgetLifetime?.Dispose();
            if (!admissionTransferred) RequestAdmission.Release();
        }
    }

    private ObjectResult CapacityResponse(int statusCode, string code, string message) =>
        StatusCode(statusCode, ApiResponse.Fail(code, message));

    private sealed class CalibrationBudgetedOkResult(object value, Action releaseAdmission,
        IDisposable budgetLifetime, IDisposable outputReservation) : OkObjectResult(value), IDisposable
    {
        private Action? _releaseAdmission = releaseAdmission;
        private IDisposable? _budgetLifetime = budgetLifetime;
        private IDisposable? _outputReservation = outputReservation;

        public override async Task ExecuteResultAsync(ActionContext context)
        {
            try
            {
                var httpContext = context.HttpContext;
                var cancellationToken = httpContext.RequestAborted;
                using var buffer = new BoundedMemoryStream(MaximumExportBytes);
                var options = httpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
                try
                {
                    JsonSerializer.Serialize(buffer, Value, Value?.GetType() ?? typeof(object), options);
                }
                catch (CalibrationCapacityException exception)
                {
                    buffer.ResetBuffer();
                    httpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    JsonSerializer.Serialize(buffer, ApiResponse.Fail("capacity_exceeded", exception.Message), options);
                }
                cancellationToken.ThrowIfCancellationRequested();
                buffer.Position = 0;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                httpContext.Response.ContentLength = buffer.Length;
                await buffer.CopyToAsync(httpContext.Response.Body, 64 * 1024, cancellationToken).ConfigureAwait(false);
            }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _outputReservation, null)?.Dispose();
            Interlocked.Exchange(ref _budgetLifetime, null)?.Dispose();
            Interlocked.Exchange(ref _releaseAdmission, null)?.Invoke();
        }
    }

    private sealed class CalibrationFileStreamResult : FileStreamResult, IDisposable
    {
        public CalibrationFileStreamResult(Stream stream, string contentType, string fileDownloadName,
            Action releaseAdmission, IDisposable budgetLifetime, IDisposable outputBudgetReservation)
            : base(stream, contentType)
        {
            FileDownloadName = fileDownloadName;
            _releaseAdmission = releaseAdmission;
            _budgetLifetime = budgetLifetime;
            _outputBudgetReservation = outputBudgetReservation;
        }

        private Action? _releaseAdmission;
        private IDisposable? _budgetLifetime;
        private IDisposable? _outputBudgetReservation;

        public override async Task ExecuteResultAsync(ActionContext context)
        {
            try
            {
                await base.ExecuteResultAsync(context).ConfigureAwait(false);
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            FileStream.Dispose();
            Interlocked.Exchange(ref _outputBudgetReservation, null)?.Dispose();
            Interlocked.Exchange(ref _budgetLifetime, null)?.Dispose();
            Interlocked.Exchange(ref _releaseAdmission, null)?.Invoke();
        }
    }

    private sealed class BoundedMemoryStream(long maximumBytes) : MemoryStream
    {
        private long _written;

        public void ResetBuffer()
        {
            SetLength(0);
            Position = 0;
            _written = 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            base.Write(buffer, offset, count);
            _written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
            _written += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            base.WriteByte(value);
            _written++;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCapacity(buffer.Length);
            base.Write(buffer.Span);
            _written += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCapacity(count);
            base.Write(buffer, offset, count);
            _written += count;
            return Task.CompletedTask;
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || _written > maximumBytes - count)
                throw new CalibrationCapacityException($"校準 JSON 超過 {maximumBytes / (1024 * 1024)} MiB 匯出安全上限。使用摘要模式取得同一完整範圍的統計；需要逐時明細請用診斷資料搬運匯出。完整匯出不會截斷資料。");
        }
    }
}
