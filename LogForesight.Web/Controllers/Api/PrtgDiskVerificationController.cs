using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

[ApiController]
[Route("api/prtg/disk-verification")]
[Permission(Capability.Maintain)]
public sealed class PrtgDiskVerificationController : ControllerBase
{
    private readonly PrtgDiskVerificationService _service;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _user;
    public PrtgDiskVerificationController(PrtgDiskVerificationService service, IAuditService audit, ICurrentUser user)
    { _service = service; _audit = audit; _user = user; }

    [HttpGet]
    public ApiResponse<PrtgDiskVerificationStatus> Status([FromQuery] long? sensorObjid = null) =>
        ApiResponse<PrtgDiskVerificationStatus>.Ok(_service.GetStatus(sensorObjid));

    [HttpPost("start")]
    public IActionResult Start([FromBody] PrtgDiskVerificationStart request)
    {
        if (!_service.TryStart(request, out var error))
            return Conflict(ApiResponse.Fail("conflict", error ?? "語意驗證無法啟動。"));
        _audit.Record("prtg_disk_semantic_probe", "啟動單一磁碟感測器語意驗證。", "prtg_sensor", request.SensorObjid.ToString());
        return Ok(ApiResponse.Ok());
    }

    [HttpPost("cancel")]
    public IActionResult Cancel()
    {
        var cancelled = _service.Cancel();
        _audit.Record("prtg_disk_semantic_cancel", cancelled ? "停止磁碟感測器語意驗證。" : "沒有進行中的磁碟感測器語意驗證。",
            "prtg_sensor", result: cancelled ? AuditResult.Ok : AuditResult.Failed);
        return Ok(ApiResponse<bool>.Ok(cancelled));
    }

    [HttpGet("{sensorObjid:long}/evidence")]
    public ApiResponse<object> Evidence(long sensorObjid) => ApiResponse<object>.Ok(new
    {
        Verification = _service.GetStatus(sensorObjid).Results.FirstOrDefault(),
        Semantic = _service.CheckEvidence(sensorObjid)
    });

    [HttpGet("{sensorObjid:long}/rule-trial")]
    public ApiResponse<PrtgDiskRuleTrial> RuleTrial(long sensorObjid) =>
        ApiResponse<PrtgDiskRuleTrial>.Ok(_service.AssessRuleTrial(sensorObjid));

    [HttpPost("confirm")]
    public IActionResult Confirm([FromBody] PrtgDiskManualConfirmation request)
    {
        try
        {
            var evidence = _service.ConfirmManually(request, _user.UserId);
            _audit.Record("prtg_disk_semantic_manual_confirm", "人工確認磁碟感測器頻道與量測語意。",
                "prtg_sensor", request.SensorObjid.ToString(), new { Reason = request.Reason.Trim(), evidence.MainChannelIdentifier, evidence.Unit, evidence.Direction });
            return Ok(ApiResponse<PrtgDiskSemanticEvidence>.Ok(evidence));
        }
        catch (ArgumentException ex) { return BadRequest(ApiResponse.Fail("validation_failed", ex.Message)); }
        catch (InvalidOperationException ex) { return Conflict(ApiResponse.Fail("validation_failed", ex.Message)); }
    }
}
