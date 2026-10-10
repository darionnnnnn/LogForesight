using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace LogForesight.Web.Controllers.Api;

[ApiController, Route("api/prtg/monitoring/trusted-sampling"), Permission(Capability.Maintain)]
public sealed class PrtgTrustedSamplingProfilesController(StorageBackend backend, IHostStore hosts,
    IVisibilityService visibility, ICurrentUser user,
    PrtgTrustedSamplingProfileRefreshHostedService refresh) : ControllerBase
{
    private const int MaxSensors = 15000;
    private const int PageSize = 100;

    internal Func<PrtgHostSnapshot>? HostSnapshotProviderOverride { get; set; }
    internal Func<long, PrtgTrustedSamplingBindingQualificationRequest, CancellationToken,
        Task<IReadOnlyList<PrtgTrustedSamplingProbeRow>>>? QualificationProbeOverride { get; set; }
    internal Func<IReadOnlyList<long>, CancellationToken,
        Task<IReadOnlyList<PrtgTrustedSamplingProbeRow>>>? ProbeOverride { get; set; }

    private PrtgHostSnapshot CaptureHostSnapshot() =>
        HostSnapshotProviderOverride?.Invoke() ?? hosts.CapturePrtgSnapshot();

    [HttpGet("bindings/{sensorObjid:long}")]
    public IActionResult GetBinding(long sensorObjid)
    {
        if (sensorObjid <= 0) return BadRequest(ApiResponse.Fail("validation_failed", "sensor objid 無效。"));
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (!policy.SensorIds.Contains(sensorObjid))
            return BadRequest(ApiResponse.Fail("sensor_outside_policy", "只能查看目前監控政策範圍內的 sensor。"));
        var hostSnapshot = CaptureHostSnapshot();
        if (!CanManageScope(policy, hostSnapshot)) return Forbid();
        var binding = new PrtgTrustedSamplingBindingStore(backend).Get(sensorObjid);
        var identity = backend.PrtgStore().GetResourceIdentity(sensorObjid);
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
        var currentAuthorityContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            policy, strategyName, strategyMinutes);
        if (CaptureHostSnapshot().Version != hostSnapshot.Version)
            return Conflict(ApiResponse.Fail("catalogue_changed", "主機可見範圍在讀取期間已變更；請重新載入。"));
        var authorityContextCurrent = settings.PrtgEnabled && policy.Ready(settings.PrtgUrl) &&
            binding is not null && identity.Active && !identity.PendingReconciliation &&
            binding.AuthorityContextFingerprint == currentAuthorityContext && binding.Matches(sensorObjid,
                currentAuthorityContext, identity.SourceGeneration, identity.Generation,
                identity.Epoch, identity.ChannelGeneration) && identity.ChannelFingerprint == binding.BindingFingerprint;
        var status = binding is null || string.IsNullOrWhiteSpace(binding.QualificationProofReference) ||
            !authorityContextCurrent ? "waiting" : "qualified";
        var missing = binding is null ? new[] { "management:binding_missing" } :
            !authorityContextCurrent ? new[] { "management:authority_context_changed" } :
            string.IsNullOrWhiteSpace(binding.QualificationProofReference)
                ? new[] { "qualification:raw_channel_id_time_proof_required" } : Array.Empty<string>();
        return Ok(ApiResponse<PrtgTrustedSamplingBindingEditDto>.Ok(new(
            binding is null ? null : PrtgTrustedSamplingBindingStatusDto.From(binding, status, missing),
            settings.Revision, policy.Revision, identity.Epoch, identity.ChannelGeneration,
            binding?.BindingRevision ?? 0)));
    }

    [HttpPost("bindings/{sensorObjid:long}/qualify")]
    public async Task<IActionResult> QualifyBinding(long sensorObjid,
        [FromBody] PrtgTrustedSamplingBindingQualificationRequest request, CancellationToken ct)
    {
        if (sensorObjid <= 0 || request is null || request.ExpectedBindingRevision <= 0 ||
            !Hex64(request.ExpectedBindingFingerprint))
            return BadRequest(ApiResponse.Fail("validation_failed", "binding qualification revision 或 fingerprint 無效。"));
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var hostSnapshot = CaptureHostSnapshot();
        if (!policy.SensorIds.Contains(sensorObjid))
            return BadRequest(ApiResponse.Fail("sensor_outside_policy", "只能核驗目前監控政策範圍內的 sensor。"));
        if (!CanManageScope(policy, hostSnapshot)) return Forbid();
        try
        {
            var rows = QualificationProbeOverride is { } probeOverride
                ? await probeOverride(sensorObjid, request, ct)
                : await new PrtgTrustedSamplingProbeService(backend).ProbeAsync([sensorObjid], ct,
                    allowQualification: true, expectedBindingRevision: request.ExpectedBindingRevision,
                    expectedBindingFingerprint: request.ExpectedBindingFingerprint);
            var row = rows.Single();
            if (row.QualificationRecorded || row.ProfileRecorded)
                refresh.WakeForPendingProofRefresh();
            var latestHosts = CaptureHostSnapshot();
            if (latestHosts.Version != hostSnapshot.Version || !CanManageScope(policy, latestHosts))
            {
                if (row.ProfileRecorded || row.QualificationRecorded)
                    return Ok(ApiResponse<PrtgTrustedSamplingCommitReceipt>.Ok(new(true, true, [sensorObjid])));
                return latestHosts.Version != hostSnapshot.Version
                    ? Conflict(ApiResponse.Fail("catalogue_changed", "主機可見範圍在核驗期間已變更；請重新載入。"))
                    : Forbid();
            }
            var binding = new PrtgTrustedSamplingBindingStore(backend).Get(sensorObjid);
            var status = binding is not null && !string.IsNullOrWhiteSpace(binding.QualificationProofReference)
                ? row.ProfileRecorded ? "ready" : "waiting" : "waiting";
            var missing = row.MissingAuthorityFields;
            return Ok(ApiResponse<PrtgTrustedSamplingBindingQualificationResult>.Ok(new(sensorObjid,
                status, missing, binding is null ? null : PrtgTrustedSamplingBindingStatusDto.From(binding,
                    status, missing), row)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return StatusCode(504, ApiResponse.Fail("probe_deadline", "來源能力核驗超過 30 秒期限。")); }
        catch (InvalidOperationException ex)
        { return Conflict(ApiResponse.Fail("binding_fence_changed", ex.Message)); }
        catch (JsonException)
        { return Conflict(ApiResponse.Fail("qualification_source_shape_invalid", "來源回應格式無法辨識；未採用核驗結果。")); }
        catch (InvalidDataException ex)
        { return Conflict(ApiResponse.Fail("qualification_source_shape_invalid", ex.Message)); }
        catch (PrtgClientException ex)
        { return Conflict(ApiResponse.Fail("qualification_source_unavailable", ex.Message)); }
    }

    /// <summary>Read-only full-scope progress from the durable profile refresh worker.</summary>
    [HttpGet("refresh-progress")]
    public IActionResult RefreshProgress()
    {
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (policy.SensorIds.Count > MaxSensors)
            return Conflict(ApiResponse.Fail("scope_limit_exceeded", "sensor profile 範圍超過 15,000 筆；拒絕回傳不完整狀態。"));
        var hostSnapshot = CaptureHostSnapshot();
        var visibleHosts = visibility.GetVisibleHostIds(hostSnapshot).ToHashSet();
        var canViewAll = user.Has(Capability.ViewAll);
        if ((visibleHosts.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visibleHosts.Contains))) return Forbid();
        return Ok(ApiResponse<PrtgTrustedSamplingProfileRefreshProgress>.Ok(refresh.ReadProgress()));
    }

    /// <summary>Save one explicit semantic binding. The save invalidates prior profile authority and remains waiting.</summary>
    [HttpPut("bindings/{sensorObjid:long}")]
    public IActionResult SaveBinding(long sensorObjid, [FromBody] PrtgTrustedSamplingBindingRequest request)
    {
        if (!ValidBindingRequest(sensorObjid, request))
            return BadRequest(ApiResponse.Fail("validation_failed", "sampling binding 欄位無效或超出界限。"));
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var hostSnapshot = CaptureHostSnapshot();
        var visibleHosts = visibility.GetVisibleHostIds(hostSnapshot).ToHashSet();
        var canViewAll = user.Has(Capability.ViewAll);
        if ((visibleHosts.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visibleHosts.Contains))) return Forbid();
        if (!policy.SensorIds.Contains(sensorObjid))
            return BadRequest(ApiResponse.Fail("sensor_outside_policy", "只能設定目前監控政策範圍內的 sensor。"));
        try
        {
            var saved = new PrtgTrustedSamplingBindingStore(backend).Save(new(sensorObjid,
                request.ExpectedSettingsRevision, request.ExpectedPolicyRevision, request.ExpectedIdentityEpoch,
                request.ExpectedChannelGeneration, request.ExpectedBindingRevision, request.ChannelObjectId,
                request.ExpectedCaption, request.Quantity, request.Unit, request.Scale, request.Direction,
                request.IntervalRawUnit, request.RawTimestampTimeZoneId, request.AnalysisTimeZoneId,
                request.TimeBasisEvidenceReference));
            var latestHosts = CaptureHostSnapshot();
            if (latestHosts.Version != hostSnapshot.Version || !CanManageScope(policy, latestHosts))
                return Ok(ApiResponse<PrtgTrustedSamplingCommitReceipt>.Ok(new(true, true, [sensorObjid])));
            var status = string.IsNullOrWhiteSpace(saved.QualificationProofReference) ? "waiting" : "qualified";
            var missing = status == "waiting" ? new[] { "qualification_required" } : Array.Empty<string>();
            return Ok(ApiResponse<PrtgTrustedSamplingBindingStatusDto>.Ok(
                PrtgTrustedSamplingBindingStatusDto.From(saved, status, missing)));
        }
        catch (InvalidOperationException ex)
        { return Conflict(ApiResponse.Fail("binding_fence_changed", ex.Message)); }
        catch (InvalidDataException ex)
        { return BadRequest(ApiResponse.Fail("validation_failed", ex.Message)); }
        catch (ArgumentException)
        { return BadRequest(ApiResponse.Fail("validation_failed", "sampling binding 欄位無效或超出界限。")); }
    }

    /// <summary>Apply an explicit per-sensor binding batch. Every row carries its own exact channel ID and CAS fences.</summary>
    [HttpPost("bindings/batch")]
    public IActionResult SaveBindingsBatch([FromBody] PrtgTrustedSamplingBindingBatchRequest request)
    {
        if (request?.Rows is null || request.Rows.Count is < 1 or > 100 ||
            request.Rows.Any(row => row is null || !ValidBindingRequest(row.SensorObjid, row)) ||
            request.Rows.Select(row => row.SensorObjid).Distinct().Count() != request.Rows.Count)
            return BadRequest(ApiResponse.Fail("validation_failed", "每批需 1 至 100 筆唯一 sensor binding，且每筆都要明確指定 channel 與 revision fence。"));
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var hostSnapshot = CaptureHostSnapshot();
        var visibleHosts = visibility.GetVisibleHostIds(hostSnapshot).ToHashSet();
        var canViewAll = user.Has(Capability.ViewAll);
        if ((visibleHosts.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visibleHosts.Contains))) return Forbid();
        if (request.Rows.Any(row => !policy.SensorIds.Contains(row.SensorObjid)))
            return BadRequest(ApiResponse.Fail("sensor_outside_policy", "只能設定目前監控政策範圍內的 sensor。"));

        var store = new PrtgTrustedSamplingBindingStore(backend);
        var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var results = new List<PrtgTrustedSamplingBindingBatchResult>(request.Rows.Count);
        var committedSensorObjids = new List<long>(request.Rows.Count);
        foreach (var row in request.Rows.OrderBy(item => item.SensorObjid))
        {
            var beforeRowHosts = CaptureHostSnapshot();
            var currentPolicy = policyStore.Get();
            if (beforeRowHosts.Version != hostSnapshot.Version || !CanManageScope(currentPolicy, beforeRowHosts) ||
                currentPolicy.Revision != policy.Revision || !currentPolicy.SensorIds.Contains(row.SensorObjid))
            {
                if (committedSensorObjids.Count > 0)
                    return Ok(ApiResponse<PrtgTrustedSamplingCommitReceipt>.Ok(
                        new(true, true, committedSensorObjids.ToArray())));
                if (!CanManageScope(currentPolicy, beforeRowHosts)) return Forbid();
                return Conflict(ApiResponse.Fail("catalogue_changed", "主機、授權或政策在批次保存前已變更；請重新載入。"));
            }
            try
            {
                var saved = store.Save(new(row.SensorObjid, row.ExpectedSettingsRevision,
                    row.ExpectedPolicyRevision, row.ExpectedIdentityEpoch, row.ExpectedChannelGeneration,
                    row.ExpectedBindingRevision, row.ChannelObjectId, row.ExpectedCaption, row.Quantity,
                    row.Unit, row.Scale, row.Direction, row.IntervalRawUnit, row.RawTimestampTimeZoneId,
                    row.AnalysisTimeZoneId, row.TimeBasisEvidenceReference));
                var status = string.IsNullOrWhiteSpace(saved.QualificationProofReference) ? "waiting" : "qualified";
                var missing = status == "waiting" ? new[] { "qualification_required" } : Array.Empty<string>();
                results.Add(new(row.SensorObjid, status, missing, saved.BindingRevision,
                    saved.BindingFingerprint, null));
                committedSensorObjids.Add(row.SensorObjid);
                var afterRowHosts = CaptureHostSnapshot();
                var afterRowPolicy = policyStore.Get();
                if (afterRowHosts.Version != hostSnapshot.Version || !CanManageScope(afterRowPolicy, afterRowHosts) ||
                    afterRowPolicy.Revision != policy.Revision)
                    return Ok(ApiResponse<PrtgTrustedSamplingCommitReceipt>.Ok(
                        new(true, true, committedSensorObjids.ToArray())));
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
            { results.Add(new(row.SensorObjid, "rejected", ["binding_save_failed"], null, null, ex.Message)); }
        }
        var finalHosts = CaptureHostSnapshot();
        var finalPolicy = policyStore.Get();
        if (finalHosts.Version != hostSnapshot.Version || !CanManageScope(finalPolicy, finalHosts) ||
            finalPolicy.Revision != policy.Revision)
        {
            if (committedSensorObjids.Count > 0)
                return Ok(ApiResponse<PrtgTrustedSamplingCommitReceipt>.Ok(
                    new(true, true, committedSensorObjids.ToArray())));
            if (!CanManageScope(finalPolicy, finalHosts)) return Forbid();
            return Conflict(ApiResponse.Fail("catalogue_changed", "主機、授權或政策在批次設定期間已變更；請重新載入並核對每筆結果。"));
        }
        return Ok(ApiResponse<IReadOnlyList<PrtgTrustedSamplingBindingBatchResult>>.Ok(results));
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
        var hostSnapshot = CaptureHostSnapshot();
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
        var hostSnapshot = CaptureHostSnapshot();
        var visibleHosts = visibility.GetVisibleHostIds(hostSnapshot).ToHashSet();
        var canViewAll = user.Has(Capability.ViewAll);
        if ((visibleHosts.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visibleHosts.Contains))) return Forbid();
        if (request.SensorObjids.Any(id => !policy.SensorIds.Contains(id)))
            return BadRequest(ApiResponse.Fail("sensor_outside_policy", "只能探測目前監控政策範圍內的 sensor。"));
        try
        {
            var rows = ProbeOverride is { } probeOverride
                ? await probeOverride(request.SensorObjids, ct)
                : await new PrtgTrustedSamplingProbeService(backend).ProbeAsync(request.SensorObjids, ct);
            var latestHosts = CaptureHostSnapshot();
            if (latestHosts.Version != hostSnapshot.Version || !CanManageScope(policy, latestHosts))
            {
                var committed = rows.Where(row => row.ProfileRecorded).Select(row => row.SensorObjid).ToArray();
                if (committed.Length > 0)
                    return Ok(ApiResponse<PrtgTrustedSamplingCommitReceipt>.Ok(new(true, true, committed)));
                return latestHosts.Version != hostSnapshot.Version
                    ? Conflict(ApiResponse.Fail("catalogue_changed", "主機可見範圍在探測期間已變更；請重新探測。"))
                    : Forbid();
            }
            return Ok(ApiResponse<IReadOnlyList<PrtgTrustedSamplingProbeRow>>.Ok(rows));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return StatusCode(504, ApiResponse.Fail("probe_deadline", "來源能力探測超過 30 秒期限。")); }
        catch (InvalidOperationException ex)
        { return Conflict(ApiResponse.Fail("probe_fence_or_scope_changed", ex.Message)); }
        catch (JsonException)
        { return Conflict(ApiResponse.Fail("probe_source_shape_invalid", "來源回應格式無法辨識；未採用探測結果。")); }
        catch (InvalidDataException ex)
        { return Conflict(ApiResponse.Fail("probe_source_shape_invalid", ex.Message)); }
        catch (PrtgClientException ex)
        { return Conflict(ApiResponse.Fail("probe_source_unavailable", ex.Message)); }
    }

    /// <summary>Off-mode transport-only capacity sample; performs four Table-budget GETs per sensor and never writes a trusted profile.</summary>
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
        var bindings = new PrtgTrustedSamplingBindingStore(backend).GetMany(ids);
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
            bindings.TryGetValue(id, out var binding);
            var result = profile == null
                ? new PrtgTrustedSamplingProfileResolution(null,
                    binding is null ? ["management:binding_missing"] :
                    string.IsNullOrWhiteSpace(binding.QualificationProofReference)
                        ? ["qualification:raw_channel_id_time_proof_required"] :
                    ["profile", "source_metadata_reference", "physical_sample_comparison"],
                    "source_authority_incomplete")
                : PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy, id,
                    profile.SensorType, strategyContext, nowUtc, nowUtc);
            return new PrtgTrustedSamplingProfileStatusDto(id,
                result.Ready ? "ready" : result.RejectionReason ?? "waiting",
                result.MissingFacts, profile?.PrimaryChannelCaption, profile?.Quantity.ToString(), profile?.Unit,
                profile?.ConfirmedScanInterval.TotalSeconds, profile?.SourceMetadataObservedAtUtc,
                identity?.Epoch, identity?.ChannelGeneration, binding?.BindingRevision,
                binding?.ChannelObjectId, binding?.ExpectedCaption, binding?.QualificationProofReference,
                binding?.BindingFingerprint, binding is null ? null : string.IsNullOrWhiteSpace(binding.QualificationProofReference)
                    ? "waiting" : "qualified", binding?.Quantity.ToString(), binding?.Scale,
                binding?.Direction, binding?.IntervalRawUnit, binding?.RawTimestampTimeZoneId,
                binding?.AnalysisTimeZoneId, binding?.TimeBasisEvidenceReference,
                binding?.QualificationObservedAtUtc, binding?.QualificationSourceVersion);
        }).ToArray();

        var latestPolicy = policyStore.Get();
        var latestHosts = CaptureHostSnapshot();
        var latestProfiles = new PrtgTrustedSamplingProfileStore(backend).GetMany(ids);
        var latestBindings = new PrtgTrustedSamplingBindingStore(backend).GetMany(ids);
        var latestIdentities = store.GetResourceIdentities(ids);
        var latestSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        if (policy.Revision != latestPolicy.Revision || hostSnapshot.Version != latestHosts.Version ||
            !visibleHosts.SetEquals(visibility.GetVisibleHostIds(latestHosts)) ||
            currentSettings.Revision != latestSettings.Revision ||
            currentSettings.PrtgEnabled != latestSettings.PrtgEnabled ||
            currentSettings.PrtgFetchStrategy != latestSettings.PrtgFetchStrategy ||
            ids.Any(id => profiles.GetValueOrDefault(id)?.MetadataDigest != latestProfiles.GetValueOrDefault(id)?.MetadataDigest ||
                bindings.GetValueOrDefault(id)?.BindingFingerprint != latestBindings.GetValueOrDefault(id)?.BindingFingerprint ||
                identities.GetValueOrDefault(id)?.Epoch != latestIdentities.GetValueOrDefault(id)?.Epoch ||
                identities.GetValueOrDefault(id)?.ChannelGeneration != latestIdentities.GetValueOrDefault(id)?.ChannelGeneration))
            return Conflict(ApiResponse.Fail("catalogue_changed", "主機、授權或來源設定在讀取期間已變更；請重新載入。"));
        return Ok(ApiResponse<object>.Ok(new
        {
            SettingsRevision = currentSettings.Revision,
            PolicyRevision = policy.Revision,
            SourceGeneration = policy.SourceGeneration,
            AuthorityContextFingerprint = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
                policy, strategyName, strategyMinutes),
            PrtgEnabled = currentSettings.PrtgEnabled,
            Total = sensorIds.Length,
            Offset = offset,
            Limit = limit,
            Rows = rows,
            NextOffset = offset + rows.Length < sensorIds.Length ? offset + rows.Length : (int?)null
        }));
    }

    private static bool ValidBindingRequest(long sensorObjid, PrtgTrustedSamplingBindingRequest? request) =>
        sensorObjid > 0 && request is not null && request.SensorObjid == sensorObjid && request.ExpectedIdentityEpoch > 0 &&
        request.ExpectedBindingRevision >= 0 && SafeOpaque(request.ExpectedSettingsRevision) &&
        SafeOpaque(request.ExpectedPolicyRevision) && request.ExpectedChannelGeneration is not null &&
        (request.ExpectedChannelGeneration.Length == 0 || SafeOpaque(request.ExpectedChannelGeneration)) &&
        ValidChannelObjectId(request.ChannelObjectId) && !string.IsNullOrWhiteSpace(request.ExpectedCaption) &&
        request.ExpectedCaption.Length <= 256 && request.ExpectedCaption.All(ch => !char.IsControl(ch)) &&
        Enum.IsDefined(request.Quantity) && request.Quantity != PrtgTrustedQuantitySemantic.Unknown &&
        request.Unit == "%" && double.IsFinite(request.Scale) && request.Scale > 0 &&
        request.Direction is "direct" or "inverse" or "absolute" &&
        request.IntervalRawUnit is "seconds" or "minutes" && SafeZone(request.RawTimestampTimeZoneId) &&
        SafeZone(request.AnalysisTimeZoneId) && SafeOpaque(request.TimeBasisEvidenceReference);

    private static bool ValidChannelObjectId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 20 && value.All(char.IsAsciiDigit) &&
        long.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) && id >= 0;

    private static bool SafeOpaque(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(ch => !char.IsControl(ch)) && !value.Contains("://", StringComparison.Ordinal) &&
        !value.Contains('/') && !value.Contains('\\') && !value.Contains('?') && !value.Contains('#') && !value.Contains('@');

    private static bool SafeZone(string? zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId) || zoneId.Length > 128) return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zoneId); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private bool CanManageScope(PrtgMonitoringPolicy policy, PrtgHostSnapshot? captured = null)
    {
        var hostSnapshot = captured ?? CaptureHostSnapshot();
        var visibleHosts = visibility.GetVisibleHostIds(hostSnapshot).ToHashSet();
        var canViewAll = user.Has(Capability.ViewAll);
        return !((visibleHosts.Count == 0 && !canViewAll) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!canViewAll && !policy.HostIds.All(visibleHosts.Contains)));
    }

    private static bool Hex64(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public sealed record PrtgTrustedSamplingProbeRequest(IReadOnlyList<long> SensorObjids);

public sealed record PrtgTrustedSamplingProfileStatusDto(long SensorObjid, string Status,
    IReadOnlyList<string> MissingFacts, string? PrimaryChannelCaption, string? Quantity,
    string? Unit, double? ConfirmedScanSeconds, DateTimeOffset? SourceMetadataObservedAtUtc,
    long? CurrentIdentityEpoch, string? CurrentChannelGeneration,
    long? BindingRevision, string? BoundChannelObjectId, string? BoundChannelCaption,
    string? QualificationProofReference, string? BindingFingerprint, string? BindingStatus,
    string? BindingQuantity, double? BindingScale, string? BindingDirection,
    string? BindingIntervalRawUnit, string? BindingRawTimestampTimeZoneId,
    string? BindingAnalysisTimeZoneId, string? TimeBasisEvidenceReference,
    DateTimeOffset? QualificationObservedAtUtc, string? QualificationSourceVersion);

public sealed record PrtgTrustedSamplingBindingRequest(
    long SensorObjid, string ExpectedSettingsRevision, string ExpectedPolicyRevision, long ExpectedIdentityEpoch,
    string ExpectedChannelGeneration, long ExpectedBindingRevision, string ChannelObjectId,
    string ExpectedCaption, PrtgTrustedQuantitySemantic Quantity, string Unit, double Scale,
    string Direction, string IntervalRawUnit, string RawTimestampTimeZoneId,
    string AnalysisTimeZoneId, string TimeBasisEvidenceReference);

public sealed record PrtgTrustedSamplingBindingBatchRequest(IReadOnlyList<PrtgTrustedSamplingBindingRequest> Rows);
public sealed record PrtgTrustedSamplingBindingBatchResult(long SensorObjid, string Status,
    IReadOnlyList<string> MissingFacts, long? BindingRevision, string? BindingFingerprint, string? RejectionReason);

public sealed record PrtgTrustedSamplingBindingStatusDto(long SensorObjid, long BindingRevision,
    string BindingFingerprint, string SemanticFingerprint, string Status,
    IReadOnlyList<string> MissingFacts, string ChannelObjectId, string ExpectedCaption,
    string Quantity, string Unit, double Scale, string Direction, string IntervalRawUnit,
    string RawTimestampTimeZoneId, string AnalysisTimeZoneId,
    string TimeBasisEvidenceReference, string AuthorityContextFingerprint, string QualificationProofReference,
    long IdentityEpoch, string ChannelGeneration,
    DateTimeOffset? QualificationObservedAtUtc, string QualificationSourceVersion)
{
    public static PrtgTrustedSamplingBindingStatusDto From(PrtgTrustedSamplingBinding binding,
        string status, IReadOnlyList<string> missing) => new(binding.SensorObjid, binding.BindingRevision,
        binding.BindingFingerprint, binding.SemanticFingerprint, status, missing,
        binding.ChannelObjectId, binding.ExpectedCaption, binding.Quantity.ToString(), binding.Unit,
        binding.Scale, binding.Direction, binding.IntervalRawUnit, binding.RawTimestampTimeZoneId,
        binding.AnalysisTimeZoneId, binding.TimeBasisEvidenceReference, binding.AuthorityContextFingerprint,
        binding.QualificationProofReference, binding.IdentityEpoch,
        binding.ChannelGeneration, binding.QualificationObservedAtUtc, binding.QualificationSourceVersion);
}

public sealed record PrtgTrustedSamplingBindingEditDto(PrtgTrustedSamplingBindingStatusDto? Binding,
    string ExpectedSettingsRevision, string ExpectedPolicyRevision, long ExpectedIdentityEpoch,
    string ExpectedChannelGeneration, long ExpectedBindingRevision);
public sealed record PrtgTrustedSamplingBindingQualificationRequest(long ExpectedBindingRevision,
    string ExpectedBindingFingerprint);
public sealed record PrtgTrustedSamplingBindingQualificationResult(long SensorObjid, string Status,
    IReadOnlyList<string> MissingFacts, PrtgTrustedSamplingBindingStatusDto? Binding,
    PrtgTrustedSamplingProbeRow Probe);

public sealed record PrtgTrustedSamplingCommitReceipt(bool Committed, bool ReloadRequired,
    IReadOnlyList<long> CommittedSensorObjids);
