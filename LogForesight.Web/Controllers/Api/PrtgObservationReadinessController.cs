using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>管理者查看獨立判定遷移前的重複／未知數；不進行正式讀取切換。</summary>
[ApiController]
[Route("api/prtg/observation-readiness")]
[Permission(Capability.Maintain)]
public sealed class PrtgObservationReadinessController : ControllerBase
{
    private readonly EfPrtgObservationStore _observations;
    private readonly IVisibilityService _visibility;

    public PrtgObservationReadinessController(StorageBackend backend, IVisibilityService visibility)
    {
        _observations = backend.PrtgObservationStore();
        _visibility = visibility;
    }

    [HttpGet]
    public ActionResult<ApiResponse<PrtgObservationCutoverPreview>> Get(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var end = (to ?? DateTime.Today).Date;
        var start = (from ?? end.AddDays(-29)).Date;
        try
        {
            return ApiResponse<PrtgObservationCutoverPreview>.Ok(
                _observations.Preview(start, end, _visibility.GetVisibleHostIds().ToArray()));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<PrtgObservationCutoverPreview>.Fail(ApiErrorCodes.ValidationFailed, ex.Message));
        }
    }
}
