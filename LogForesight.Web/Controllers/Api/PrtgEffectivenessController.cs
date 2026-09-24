using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

[ApiController]
[Route("api/prtg/effectiveness")]
[Permission(Capability.Maintain)]
public sealed class PrtgEffectivenessController : ControllerBase
{
    private readonly PrtgEffectivenessService _service;

    // StorageBackend is the application's registered owner of the provider-specific context factory.
    public PrtgEffectivenessController(StorageBackend backend, IIssueOwnerStore issueOwners) =>
        _service = new PrtgEffectivenessService(backend.CreateContext, issueOwners);

    [HttpGet]
    public ActionResult<ApiResponse<PrtgEffectivenessSummary>> Get([FromQuery] DateTime? from = null,
        [FromQuery] DateTime? through = null)
    {
        try { return ApiResponse<PrtgEffectivenessSummary>.Ok(_service.Get(from, through)); }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<PrtgEffectivenessSummary>.Fail(ApiErrorCodes.ValidationFailed, ex.Message));
        }
    }
}
