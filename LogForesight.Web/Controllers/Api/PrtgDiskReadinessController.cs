using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

[ApiController]
[Route("api/prtg/disk-readiness")]
[Permission(Capability.Maintain)]
public sealed class PrtgDiskReadinessController : ControllerBase
{
    private readonly PrtgDiskReadinessQueryService _query;
    public PrtgDiskReadinessController(EfPrtgStore store, IHostStore hosts, ISystemSettingsStore settings,
        PrtgDiskSemanticEvidenceStore evidence, PrtgDiskVerificationResultStore verificationResults) =>
        _query = new PrtgDiskReadinessQueryService(store, hosts, settings, evidence, verificationResults);

    [HttpGet]
    public ApiResponse<PrtgDiskReadinessPage> Get([FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        ApiResponse<PrtgDiskReadinessPage>.Ok(_query.Get(page, pageSize));
}
