using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>首次啟動精靈 API（回饋十八輪批次H）。整個 Controller 需要 Maintain 能力——
/// 精靈本身就是給 admin/serverAdmin 做初始設定用的。</summary>
[ApiController]
[Route("api/admin/setup")]
[Permission(Capability.Maintain)]
public class SetupController : ControllerBase
{
    private readonly SetupReadinessService _service;
    private readonly IAuditService _audit;

    public SetupController(SetupReadinessService service, IAuditService audit)
    {
        _service = service;
        _audit = audit;
    }

    [HttpGet("status")]
    public ApiResponse<SetupStatusDto> Status() => ApiResponse<SetupStatusDto>.Ok(_service.GetStatus());

    [HttpPost("skip/{stepId}")]
    public ApiResponse<SetupStatusDto> Skip(string stepId, [FromBody] SetSkippedRequest request)
    {
        _service.SetSkipped(stepId, request.Skipped);
        return ApiResponse<SetupStatusDto>.Ok(_service.GetStatus());
    }

    [HttpPost("hidden")]
    public ApiResponse<SetupStatusDto> SetHidden([FromBody] SetHiddenRequest request)
    {
        _service.SetHidden(request.Hidden);
        _audit.Record(
            action: AuditActions.SettingsUpdate,
            summary: request.Hidden ? "隱藏教學文件裡的啟動精靈入口" : "重新顯示教學文件裡的啟動精靈入口",
            targetKind: "setup_wizard");
        return ApiResponse<SetupStatusDto>.Ok(_service.GetStatus());
    }
}
