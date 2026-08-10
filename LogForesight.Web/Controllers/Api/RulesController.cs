using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>規則維護（docs/WEB-SPEC.md §9.7）</summary>
[ApiController]
[Route("api/rules")]
[Permission(Capability.Maintain)]
public class RulesController : ControllerBase
{
    private readonly RuleAdminService _service;

    public RulesController(RuleAdminService service)
    {
        _service = service;
    }

    [HttpGet]
    public ApiResponse<List<RuleDto>> GetRules() =>
        ApiResponse<List<RuleDto>>.Ok(_service.GetRules());

    /// <summary>儲存前的即時驗證（不寫入）</summary>
    [HttpPost("validate")]
    public ApiResponse<RuleValidationDto> Validate([FromBody] SaveRuleRequest request) =>
        ApiResponse<RuleValidationDto>.Ok(_service.ValidateRule(request));

    [HttpPost]
    public ApiResponse<RuleDto> Save([FromBody] SaveRuleRequest request) =>
        ApiResponse<RuleDto>.Ok(_service.SaveRule(request));

    [HttpPut("{ruleId}/enabled")]
    public ApiResponse SetEnabled(string ruleId, [FromBody] SetRuleEnabledRequest request)
    {
        _service.SetEnabled(ruleId, request.Enabled);
        return ApiResponse.Ok();
    }

    [HttpDelete("{ruleId}")]
    public ApiResponse Delete(string ruleId)
    {
        _service.DeleteRule(ruleId);
        return ApiResponse.Ok();
    }

    /// <summary>回復預設的前後對照（套用前先讓人看清楚會變成什麼）</summary>
    [HttpGet("{ruleId}/restore-preview")]
    public ApiResponse<RuleRestorePreviewDto> PreviewRestore(string ruleId) =>
        ApiResponse<RuleRestorePreviewDto>.Ok(_service.PreviewRestore(ruleId));

    [HttpPost("{ruleId}/restore")]
    public ApiResponse<RuleDto> Restore(string ruleId) =>
        ApiResponse<RuleDto>.Ok(_service.RestoreSeed(ruleId));

    [HttpGet("suppressions")]
    public ApiResponse<List<RuleSuppressionDto>> GetSuppressions() =>
        ApiResponse<List<RuleSuppressionDto>>.Ok(_service.GetSuppressions());

    [HttpPost("{ruleId}/suppressions")]
    public ApiResponse AddSuppression(string ruleId, [FromBody] AddSuppressionRequest request)
    {
        _service.AddSuppression(ruleId, request);
        return ApiResponse.Ok();
    }

    /// <summary>範圍改用 query string 傳遞（回饋十三輪 F）：Host/Group/Site 三種形狀的目標不同，
    /// 不再適合塞進單一 path segment（Group／Site 沒有「host」可放）。</summary>
    [HttpDelete("{ruleId}/suppressions")]
    public ApiResponse RemoveSuppression(
        string ruleId, [FromQuery] string scope = SuppressionScopes.Host,
        [FromQuery] string? host = null, [FromQuery] long? hostGroupId = null)
    {
        _service.RemoveSuppression(ruleId, scope, host, hostGroupId);
        return ApiResponse.Ok();
    }

    /// <summary>內建規則升級（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.9，承接 --import-rules）</summary>
    [HttpGet("import-status")]
    public ApiResponse<RuleImportStatusDto> GetImportStatus() =>
        ApiResponse<RuleImportStatusDto>.Ok(_service.GetImportStatus());

    [HttpGet("import-preview")]
    public ApiResponse<RuleImportPreviewDto> PreviewImport([FromQuery] bool overwriteBuiltin = false) =>
        ApiResponse<RuleImportPreviewDto>.Ok(_service.PreviewImport(overwriteBuiltin));

    [HttpPost("import-apply")]
    public ApiResponse<RuleImportApplyResultDto> ApplyImport([FromBody] ApplyRuleImportRequest request) =>
        ApiResponse<RuleImportApplyResultDto>.Ok(_service.ApplyImport(request.OverwriteBuiltin));
}

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

    private readonly RunMonitorService _service;

    public RunsController(RunMonitorService service)
    {
        _service = service;
    }

    [HttpGet("summary")]
    public ApiResponse<List<RunDaySummaryDto>> Summary([FromQuery] int? days) =>
        ApiResponse<List<RunDaySummaryDto>>.Ok(
            _service.GetDaySummaries(Math.Clamp(days ?? DefaultRunSummaryDays, 1, 90)));

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

    [HttpGet("errors")]
    public ApiResponse<List<RunErrorGroupDto>> Errors([FromQuery] int days = 14) =>
        ApiResponse<List<RunErrorGroupDto>>.Ok(_service.GetErrorSummary(Math.Clamp(days, 1, 90)));
}
