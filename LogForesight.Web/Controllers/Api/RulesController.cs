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

    /// <summary>磁碟趨勢草稿唯讀預覽；支援 Enabled=false，最多 730 個已完成日、每頁最多 100 筆日期×sensor 結果。</summary>
    [HttpPost("disk-trend-preview")]
    public ApiResponse<DiskTrendRulePreviewDto> PreviewDiskTrend([FromBody] DiskTrendRulePreviewRequest request) =>
        ApiResponse<DiskTrendRulePreviewDto>.Ok(_service.PreviewDiskTrend(request, HttpContext.RequestAborted));

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

    /// <summary>Group／Site 抑制送出前的影響面預覽（回饋十四輪 C1）：會影響幾台主機、
    /// 過去這條規則在這些主機上命中過幾次。Host 範圍不適用（見 <see cref="RuleAdminService.PreviewSuppression"/>）。</summary>
    [HttpGet("{ruleId}/suppression-preview")]
    public ApiResponse<SuppressionPreviewDto> PreviewSuppression(
        string ruleId, [FromQuery] string scope, [FromQuery] long? hostGroupId = null) =>
        ApiResponse<SuppressionPreviewDto>.Ok(_service.PreviewSuppression(ruleId, scope, hostGroupId));

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

    /// <summary>
    /// 統一的抑制建立入口（回饋十五輪 A-6）：TargetType 由 body 指定（Rule／Signature／
    /// Correlation／Volume），供詳情頁的簽章/關聯/總量抑制出口使用（批次 C）——這幾種目標
    /// 不隸屬任何單一規則，不適合掛在 {ruleId} 路由下，因此用絕對路徑跳出 RulesController
    /// 的 api/rules 前綴，但 Service/Store 仍是同一份，邏輯與既有規則抑制路徑完全共用。
    /// </summary>
    [HttpPost("/api/suppressions")]
    public ApiResponse AddTargetedSuppression([FromBody] AddSuppressionRequest request)
    {
        _service.AddSuppression(request);
        return ApiResponse.Ok();
    }

    /// <summary>統一的抑制解除入口，語意同 <see cref="AddTargetedSuppression"/>。</summary>
    [HttpDelete("/api/suppressions")]
    public ApiResponse RemoveTargetedSuppression(
        [FromQuery] string targetType, [FromQuery] string? ruleId = null, [FromQuery] string? signatureKey = null,
        [FromQuery] string? correlationPatternId = null, [FromQuery] string? volumeKind = null,
        [FromQuery] string scope = SuppressionScopes.Host, [FromQuery] string? host = null, [FromQuery] long? hostGroupId = null)
    {
        _service.RemoveSuppression(targetType, ruleId, signatureKey, correlationPatternId, volumeKind, scope, host, hostGroupId);
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
