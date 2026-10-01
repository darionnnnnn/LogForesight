using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Web.Controllers.Api;

[ApiController]
[Route("api/prtg/risk-review")]
[Permission(Capability.Maintain)]
public sealed class PrtgRiskReviewController(StorageBackend backend, IVisibilityService visibility,
    DataVersionStamp dataVersion) : ControllerBase
{
    [HttpGet]
    public ActionResult<ApiResponse<PrtgRiskReviewCounts>> Get() =>
        ApiResponse<PrtgRiskReviewCounts>.Ok(new PrtgRiskProjectionStore(backend.CreateContext)
            .Counts(visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToArray()));

    [HttpGet("pending")]
    public IActionResult Pending([FromQuery] int page = 1)
    {
        if (page < 1 || page > 100000) return BadRequest();
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToArray();
        using var db = backend.CreateContext();
        var rows = db.DailyRecords.AsNoTracking().Where(r => visible.Contains(r.HostId) &&
            (r.RiskReviewStatus == "pending" || r.RiskReviewStatus == "unavailable"));
        return Ok(ApiResponse<object>.Ok(new { Total = rows.Count(), Page = page, PageSize = 50,
            Items = rows.OrderByDescending(r => r.RecordDate).ThenBy(r => r.RecordId).Skip((page - 1) * 50).Take(50)
                .Select(r => new { r.HostId, r.HostName, r.RecordDate, r.RiskLevel, r.RiskReviewStatus, r.DetailPruned }).ToArray() }));
    }

    [HttpPost("retry")]
    public ActionResult<ApiResponse<PrtgRiskReviewCounts>> Retry()
    {
        var hosts = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToArray();
        var store = new PrtgRiskProjectionStore(backend.CreateContext);
        if (store.RunBatch(hosts) > 0) dataVersion.Bump();
        return ApiResponse<PrtgRiskReviewCounts>.Ok(store.Counts(hosts));
    }
}
