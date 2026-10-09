using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

[ApiController, Route("api/prtg/monitoring/trusted-sampling"), Permission(Capability.Maintain)]
public sealed class PrtgTrustedSamplingProfilesController(StorageBackend backend, IHostStore hosts,
    IVisibilityService visibility, ICurrentUser user,
    PrtgTrustedSamplingProfileRefreshHostedService refresh) : ControllerBase
{
    private const int MaxSensors = 15000;
    private const int PageSize = 100;

    /// <summary>Read-only full-scope progress from the durable profile refresh worker.</summary>
    [HttpGet("refresh-progress")]
    public IActionResult RefreshProgress()
    {
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (policy.SensorIds.Count > MaxSensors)
            return Conflict(ApiResponse.Fail("scope_limit_exceeded", "sensor profile 範圍超過 15,000 筆；拒絕回傳不完整狀態。"));
        var hostSnapshot = hosts.CapturePrtgSnapshot();
        var visibleHosts = visibility.GetVisibleHostIds(hostSnapshot).ToHashSet();
        var canViewAll = user.Has(Capability.ViewAll);
        if ((visibleHosts.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visibleHosts.Contains))) return Forbid();
        return Ok(ApiResponse<PrtgTrustedSamplingProfileRefreshProgress>.Ok(refresh.ReadProgress()));
    }

    /// <summary>Bounded current worker outcomes and waiting reasons for at most 100 selected sensors.</summary>
    [HttpGet("refresh-sensors")]
    public IActionResult RefreshSensors([FromQuery] int offset = 0, [FromQuery] int limit = PageSize)
    {
        if (offset < 0 || offset > MaxSensors || limit is < 1 or > PageSize)
            return BadRequest(ApiResponse.Fail("validation_failed", "offset 或 limit 超出範圍。"));
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (policy.SensorIds.Count > MaxSensors)
            return Conflict(ApiResponse.Fail("scope_limit_exceeded", "sensor profile 範圍超過 15,000 筆；拒絕回傳不完整狀態。"));
        var hostSnapshot = hosts.CapturePrtgSnapshot();
        var visibleHosts = visibility.GetVisibleHostIds(hostSnapshot).ToHashSet();
        var canViewAll = user.Has(Capability.ViewAll);
        if ((visibleHosts.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visibleHosts.Contains))) return Forbid();
        try
        {
            return Ok(ApiResponse<PrtgTrustedSamplingProfileRefreshSensorPage>.Ok(
                refresh.ReadSensorProgressPage(offset, limit)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return BadRequest(ApiResponse.Fail("validation_failed", "offset 或 limit 超出範圍。"));
        }
    }

    /// <summary>Runs a bounded read-only source capability probe for up to five configured sensors.</summary>
    [HttpPost("probe")]
    public async Task<IActionResult> Probe([FromBody] PrtgTrustedSamplingProbeRequest request,
        CancellationToken ct)
    {
        if (request.SensorObjids == null || request.SensorObjids.Count is < 1 or > PrtgTrustedSamplingProbeService.MaxSensorIds ||
            request.SensorObjids.Any(id => id <= 0) || request.SensorObjids.Distinct().Count() != request.SensorObjids.Count)
            return BadRequest(ApiResponse.Fail("validation_failed", "每次最多探測 5 顆且 objid 必須唯一。"));
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var hostSnapshot = hosts.CapturePrtgSnapshot();
        var visibleHosts = visibility.GetVisibleHostIds(hostSnapshot).ToHashSet();
        var canViewAll = user.Has(Capability.ViewAll);
        if ((visibleHosts.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visibleHosts.Contains))) return Forbid();
        if (request.SensorObjids.Any(id => !policy.SensorIds.Contains(id)))
            return BadRequest(ApiResponse.Fail("sensor_outside_policy", "只能探測目前監控政策範圍內的 sensor。"));
        try
        {
            var rows = await new PrtgTrustedSamplingProbeService(backend).ProbeAsync(request.SensorObjids, ct);
            if (hosts.CapturePrtgSnapshot().Version != hostSnapshot.Version)
                return Conflict(ApiResponse.Fail("catalogue_changed", "主機可見範圍在探測期間已變更；請重新探測。"));
            return Ok(ApiResponse<IReadOnlyList<PrtgTrustedSamplingProbeRow>>.Ok(rows));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return StatusCode(504, ApiResponse.Fail("probe_deadline", "來源能力探測超過 30 秒期限。")); }
        catch (InvalidOperationException ex)
        { return Conflict(ApiResponse.Fail("probe_fence_or_scope_changed", ex.Message)); }
        catch (InvalidDataException ex)
        { return Conflict(ApiResponse.Fail("probe_source_shape_invalid", ex.Message)); }
        catch (PrtgClientException ex)
        { return Conflict(ApiResponse.Fail("probe_source_unavailable", ex.Message)); }
    }

    /// <summary>Off-mode transport-only capacity sample; performs two bounded GETs per sensor and never writes a trusted profile.</summary>
    [HttpPost("profile-capacity-pilot")]
    public async Task<IActionResult> RunProfileCapacityPilot(CancellationToken ct)
    {
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (policy.SensorIds.Count > 15_000)
            return Conflict(ApiResponse.Fail("scope_limit_exceeded", "sensor 範圍超過 15,000 筆；拒絕執行不完整試測。"));
        var ids = policy.SensorIds.Distinct().Order().Take(PrtgProfileTransportCapacityPilot.MaximumSensorIds).ToArray();
        if (ids.Length == 0) return Conflict(ApiResponse.Fail("empty_scope", "目前沒有選取 sensor 可供試測。"));
        var hostSnapshot = hosts.CapturePrtgSnapshot();
        var visibleHosts = visibility.GetVisibleHostIds(hostSnapshot).ToHashSet();
        var canViewAll = user.Has(Capability.ViewAll);
        if ((visibleHosts.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visibleHosts.Contains))) return Forbid();
        try
        {
            var result = await new PrtgProfileTransportCapacityPilot(backend, hosts: hosts).RunAsync(ids, ct);
            if (hosts.CapturePrtgSnapshot().Version != hostSnapshot.Version)
                return Conflict(ApiResponse.Fail("catalogue_changed", "主機可見範圍在測量期間已變更；本次樣本不適用。"));
            return Ok(ApiResponse<PrtgProfileTransportCapacityPilot.Result>.Ok(result));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return StatusCode(504, ApiResponse.Fail("profile_pilot_deadline", "profile transport pilot exceeded its 30-second deadline.")); }
        catch (InvalidOperationException ex)
        { return Conflict(ApiResponse.Fail("profile_pilot_contract_changed", ex.Message)); }
        catch (ArgumentOutOfRangeException)
        { return BadRequest(ApiResponse.Fail("validation_failed", "profile transport selection is invalid.")); }
        catch (InvalidDataException ex)
        { return Conflict(ApiResponse.Fail("profile_pilot_response_invalid", ex.Message)); }
        catch (PrtgClientException)
        { return Conflict(ApiResponse.Fail("profile_pilot_source_unavailable", "來源請求失敗；此結果不會形成成功容量樣本。")); }
    }

    /// <summary>Bounded, maintain-only profile and missing-authority view; this endpoint never creates trust.</summary>
    [HttpGet("profiles")]
    public IActionResult Get([FromQuery] int offset = 0, [FromQuery] int limit = PageSize)
    {
        if (offset < 0 || offset > MaxSensors || limit is < 1 or > PageSize)
            return BadRequest(ApiResponse.Fail("validation_failed", "offset 或 limit 超出範圍。"));
        var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var policy = policyStore.Get();
        if (policy.SensorIds.Count > MaxSensors)
            return Conflict(ApiResponse.Fail("scope_limit_exceeded", "sensor profile 範圍超過 15,000 筆；拒絕回傳不完整狀態。"));
        var hostSnapshot = hosts.CapturePrtgSnapshot();
        var visibleHosts = visibility.GetVisibleHostIds(hostSnapshot).ToHashSet();
        var canViewAll = user.Has(Capability.ViewAll);
        if ((visibleHosts.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visibleHosts.Contains))) return Forbid();

        var sensorIds = policy.SensorIds.Distinct().Order().ToArray();
        var ids = sensorIds.Skip(offset).Take(limit).ToArray();
        var store = backend.PrtgStore();
        var profiles = new PrtgTrustedSamplingProfileStore(backend).GetMany(ids);
        var identities = store.GetResourceIdentities(ids);
        var currentSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var strategyName = PrtgFetchStrategy.Normalize(currentSettings.PrtgFetchStrategy);
        var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
        var nowUtc = DateTime.UtcNow;
        var strategyContext = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey))
            .GetCurrent(policy, strategyName, strategyMinutes, nowUtc);
        var rows = ids.Select(id =>
        {
            profiles.TryGetValue(id, out var profile);
            identities.TryGetValue(id, out var identity);
            var result = profile == null
                ? new PrtgTrustedSamplingProfileResolution(null,
                    ["profile", "raw_timestamp_timezone_probe", "analysis_timezone", "primary_channel_id",
                     "quantity_unit_direction", "source_metadata_reference", "physical_sample_comparison"],
                    "source_authority_incomplete")
                : PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy, id,
                    profile.SensorType, strategyContext, nowUtc, nowUtc);
            return new PrtgTrustedSamplingProfileStatusDto(id,
                result.Ready ? "ready" : result.RejectionReason ?? "waiting",
                result.MissingFacts, profile?.PrimaryChannelCaption, profile?.Quantity.ToString(), profile?.Unit,
                profile?.ConfirmedScanInterval.TotalSeconds, profile?.SourceMetadataObservedAtUtc,
                identity?.Epoch, identity?.ChannelGeneration);
        }).ToArray();

        var latestPolicy = policyStore.Get();
        var latestHosts = hosts.CapturePrtgSnapshot();
        var latestProfiles = new PrtgTrustedSamplingProfileStore(backend).GetMany(ids);
        var latestIdentities = store.GetResourceIdentities(ids);
        var latestSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        if (policy.Revision != latestPolicy.Revision || hostSnapshot.Version != latestHosts.Version ||
            !visibleHosts.SetEquals(visibility.GetVisibleHostIds(latestHosts)) ||
            currentSettings.PrtgFetchStrategy != latestSettings.PrtgFetchStrategy ||
            ids.Any(id => profiles.GetValueOrDefault(id)?.MetadataDigest != latestProfiles.GetValueOrDefault(id)?.MetadataDigest ||
                identities.GetValueOrDefault(id)?.Epoch != latestIdentities.GetValueOrDefault(id)?.Epoch ||
                identities.GetValueOrDefault(id)?.ChannelGeneration != latestIdentities.GetValueOrDefault(id)?.ChannelGeneration))
            return Conflict(ApiResponse.Fail("catalogue_changed", "主機、授權或來源設定在讀取期間已變更；請重新載入。"));
        return Ok(ApiResponse<object>.Ok(new
        {
            Total = sensorIds.Length,
            Offset = offset,
            Limit = limit,
            Rows = rows,
            NextOffset = offset + rows.Length < sensorIds.Length ? offset + rows.Length : (int?)null
        }));
    }
}

public sealed record PrtgTrustedSamplingProbeRequest(IReadOnlyList<long> SensorObjids);

public sealed record PrtgTrustedSamplingProfileStatusDto(long SensorObjid, string Status,
    IReadOnlyList<string> MissingFacts, string? PrimaryChannelCaption, string? Quantity,
    string? Unit, double? ConfirmedScanSeconds, DateTimeOffset? SourceMetadataObservedAtUtc,
    long? CurrentIdentityEpoch, string? CurrentChannelGeneration);
