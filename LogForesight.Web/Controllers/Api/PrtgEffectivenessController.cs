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
    private readonly ICurrentUser _currentUser;

    // StorageBackend is the application's registered owner of the provider-specific context factory.
    public PrtgEffectivenessController(StorageBackend backend, IIssueOwnerStore issueOwners,
        ICurrentUser currentUser)
    {
        _service = new PrtgEffectivenessService(backend.CreateContext, issueOwners);
        _currentUser = currentUser;
    }

    [HttpGet]
    public ActionResult<ApiResponse<PrtgEffectivenessSummary>> Get([FromQuery] DateTime? from = null,
        [FromQuery] DateTime? through = null)
    {
        if (!_currentUser.HasViewAll())
            return Forbid(); // 此舊統計以全站資源為分母；受限角色使用已授權的試點證據包。
        try { return ApiResponse<PrtgEffectivenessSummary>.Ok(_service.Get(from, through)); }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<PrtgEffectivenessSummary>.Fail(ApiErrorCodes.ValidationFailed, ex.Message));
        }
    }
}
