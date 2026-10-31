using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>PRTG 數值資料就緒度唯讀 API。</summary>
[ApiController]
[Route("api/prtg/value-readiness")]
[Permission(Capability.Maintain)]
public sealed class PrtgValueReadinessController : ControllerBase
{
    private readonly StorageBackend _backend;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;

    public PrtgValueReadinessController(StorageBackend backend, IHostStore hosts, IVisibilityService visibility)
    {
        _backend = backend;
        _hosts = hosts;
        _visibility = visibility;
    }

    [HttpGet]
    public ActionResult<ApiResponse<PrtgValueReadinessDto>> Get([FromQuery] int page = 1, [FromQuery] string? reason = default)
    {
        PrtgValueReason? filterReason = null;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            if (!Enum.TryParse<PrtgValueReason>(reason.Trim(), ignoreCase: true, out var parsed))
            {
                return BadRequest(ApiResponse.Fail(ApiErrorCodes.InvalidReason, $"無效的就緒原因：{reason}。"));
            }
            filterReason = parsed;
        }

        var completedDay = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        var visibleHostIds = _visibility.GetVisibleHostIds();
        var allHosts = _hosts.GetAll();
        var evaluationHostIds = allHosts
            .Where(h => h.Active && h.MergedInto == null && visibleHostIds.Contains(h.HostId) &&
                !_visibility.IsCaseGrantOnly(h.HostId))
            .Select(h => h.HostId)
            .ToHashSet();

        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var policy = policyStore.Get();
        if (policy.HostIds is { Count: > 0 })
        {
            evaluationHostIds.IntersectWith(policy.HostIds);
        }

        var policySensorIds = (policy.SensorIds ?? []).ToHashSet();
        var settings = new SystemSettingsStore(_backend.Blob("system_settings")).Get();
        var semanticOverrides = PrtgSensorTypeCategoryMap.ParseSemanticOverrides(settings.PrtgSensorTypeCategoryOverrides);
        var assessment = new PrtgValueAssessment(_backend.PrtgStore(), semanticOverrides);
        var deviceToHost = assessment.DeviceToHostOn(completedDay, completedDay, evaluationHostIds);
        var ct = HttpContext?.RequestAborted ?? CancellationToken.None;

        var summary = PrtgValueReadiness.Compute(assessment, deviceToHost, evaluationHostIds, policySensorIds, completedDay, ct);

        var groups = Enum.GetValues<PrtgValueReason>()
            .Select(r => new PrtgValueReadinessGroupDto(r.ToString(), PrtgValueReasonText.Label(r), summary.Counts.GetValueOrDefault(r, 0)))
            .ToList();

        var filteredRows = filterReason.HasValue
            ? summary.Rows.Where(r => r.Reason == filterReason.Value).ToList()
            : summary.Rows;

        var totalRows = filteredRows.Count;
        var actualPage = page < 1 ? 1 : page;
        var pageSize = PrtgValueAssessment.PageSize;

        var hostNameMap = new Dictionary<long, string>();
        foreach (var h in allHosts)
        {
            if (!hostNameMap.ContainsKey(h.HostId))
            {
                hostNameMap[h.HostId] = string.IsNullOrWhiteSpace(h.HostName) ? $"#{h.HostId}" : h.HostName;
            }
        }

        var pagedRows = filteredRows
            .Skip((actualPage - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new PrtgValueReadinessRowDto(
                r.Sensor.HostId,
                hostNameMap.TryGetValue(r.Sensor.HostId, out var name) ? name : $"#{r.Sensor.HostId}",
                r.Sensor.SensorObjid,
                r.Sensor.SensorName,
                r.Sensor.Category ?? string.Empty,
                r.ValidDays,
                r.RequiredDays,
                r.LatestValidDay,
                r.Reason.ToString(),
                PrtgValueReasonText.Label(r.Reason)))
            .ToList();

        var dto = new PrtgValueReadinessDto(
            summary.CompletedDay,
            summary.Population,
            PrtgValueReadiness.WindowDays,
            groups,
            filterReason?.ToString(),
            actualPage,
            pageSize,
            totalRows,
            pagedRows);

        return ApiResponse<PrtgValueReadinessDto>.Ok(dto);
    }
}

public sealed record PrtgValueReadinessDto(
    DateOnly CompletedDay,
    int Population,
    int WindowDays,
    IReadOnlyList<PrtgValueReadinessGroupDto> Groups,
    string? Reason,
    int Page,
    int PageSize,
    int TotalRows,
    IReadOnlyList<PrtgValueReadinessRowDto> Rows);

public sealed record PrtgValueReadinessGroupDto(string Reason, string Label, int Count);

public sealed record PrtgValueReadinessRowDto(
    long HostId,
    string HostName,
    long SensorObjid,
    string SensorName,
    string Category,
    int ValidDays,
    int RequiredDays,
    DateOnly? LatestValidDay,
    string Reason,
    string ReasonLabel);
