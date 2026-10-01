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
    private readonly IVisibilityService? _visibility;
    private readonly IHostStore? _hosts;

    // StorageBackend is the application's registered owner of the provider-specific context factory.
    public PrtgEffectivenessController(StorageBackend backend, IIssueOwnerStore issueOwners,
        IVisibilityService? visibility = null, IHostStore? hosts = null)
    {
        _service = new PrtgEffectivenessService(backend.CreateContext, issueOwners);
        _visibility = visibility; _hosts = hosts;
    }

    [HttpGet]
    public ActionResult<ApiResponse<PrtgEffectivenessSummary>> Get([FromQuery] DateTime? from = null,
        [FromQuery] DateTime? through = null)
    {
        if (_visibility != null && _hosts != null && _hosts.GetAll().Any(h => !_visibility.GetVisibleHostIds().Contains(h.HostId) || _visibility.IsCaseGrantOnly(h.HostId)))
            return Forbid(); // 此舊統計以全站資源為分母；受限角色使用已授權的試點證據包。
        try { return ApiResponse<PrtgEffectivenessSummary>.Ok(_service.Get(from, through)); }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<PrtgEffectivenessSummary>.Fail(ApiErrorCodes.ValidationFailed, ex.Message));
        }
    }
}
