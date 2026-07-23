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
    private readonly IRuleAdminService _service;

    public RulesController(IRuleAdminService service)
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

    [HttpDelete("{ruleId}/suppressions/{host}")]
    public ApiResponse RemoveSuppression(string ruleId, string host)
    {
        _service.RemoveSuppression(ruleId, host);
        return ApiResponse.Ok();
    }
}

/// <summary>執行監控（§9.10）。需 DevMonitor 能力（dev / admin）</summary>
[ApiController]
[Route("api/runs")]
[Permission(Capability.DevMonitor)]
public class RunsController : ControllerBase
{
    private readonly IRunMonitorService _service;
    private readonly WebAppSettings _settings;

    public RunsController(IRunMonitorService service, WebAppSettings settings)
    {
        _service = service;
        _settings = settings;
    }

    [HttpGet("summary")]
    public ApiResponse<List<RunDaySummaryDto>> Summary([FromQuery] int? days) =>
        ApiResponse<List<RunDaySummaryDto>>.Ok(
            _service.GetDaySummaries(Math.Clamp(days ?? _settings.Ui.RunMatrixDays, 1, 90)));

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
