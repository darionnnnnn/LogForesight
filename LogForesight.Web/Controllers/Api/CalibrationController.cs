using System.Text.Encodings.Web;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// 校準數值匯出 API（「系統管理 > 校準數值匯出」頁）。整個 Controller 需要 Maintain 能力
/// </summary>
[ApiController]
[Route("api/admin/calibration")]
[Permission(Capability.Maintain)]
public class CalibrationController : ControllerBase
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly CalibrationService _calibrationService;
    private readonly IAuditService _audit;

    public CalibrationController(CalibrationService calibrationService, IAuditService audit)
    {
        _calibrationService = calibrationService ?? throw new ArgumentNullException(nameof(calibrationService));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>
    /// 取得四項校準指標的判定摘要。這是重查詢，由前端「重新計算」按鈕觸發，
    /// 因此一律強制重算——使用者按下按鈕就是要看當下的數字，回快取等於按鈕沒作用。
    /// 匯出端則沿用快取（同一次匯出不必把整組查詢再跑一遍）。
    /// </summary>
    [HttpGet("status")]
    public ApiResponse<CalibrationStatusDto> GetStatus()
    {
        var summary = _calibrationService.AssessStatus(anchor: null, forceRefresh: true);
        return ApiResponse<CalibrationStatusDto>.Ok(CalibrationStatusDto.From(summary));
    }

    /// <summary>
    /// 匯出校準數值封裝包為 JSON 檔案
    /// </summary>
    [HttpGet("export")]
    public IActionResult Export([FromQuery(Name = "override")] bool isOverride = false)
    {
        // 強制重算：判定快取只以日期為鍵，設定（白名單、保留天數）剛改過時舊摘要會與
        // 即時算出的資料集口徑不一致、稽核也會記到舊狀態。匯出是低頻操作，重算一次可接受；
        // 隨後 BuildExportPackage 內的判定直接命中這次更新的快取，不會跑第二遍。
        var summary = _calibrationService.AssessStatus(anchor: null, forceRefresh: true);
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

        var package = _calibrationService.BuildExportPackage();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(package, ExportJsonOptions);
        var fileName = $"calibration-{DateTime.Today:yyyyMMdd}.json";

        _audit.Record(
            action: AuditActions.CalibrationExport,
            summary: $"匯出校準數值（PRTG值型基線：{summary.PrtgValueBaseline.StatusText}、PRTG規則門檻：{summary.PrtgRuleThresholds.StatusText}、觸發式取數量級：{summary.TriggeredFetchMagnitude.StatusText}、殘留判定門檻：{summary.ResidualCredentialThresholds.StatusText}{(isOverride ? "，覆寫匯出" : "")}）",
            targetKind: "calibration_data",
            targetId: DateTime.Today.ToString("yyyyMMdd"),
            detail: new
            {
                PrtgValueBaseline = summary.PrtgValueBaseline.Status.ToString(),
                PrtgRuleThresholds = summary.PrtgRuleThresholds.Status.ToString(),
                TriggeredFetchMagnitude = summary.TriggeredFetchMagnitude.Status.ToString(),
                ResidualCredentialThresholds = summary.ResidualCredentialThresholds.Status.ToString(),
                Override = isOverride
            });

        return File(bytes, "application/json", fileName);
    }
}
