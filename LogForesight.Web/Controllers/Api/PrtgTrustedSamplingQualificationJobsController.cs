using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

[ApiController, Route("api/prtg/monitoring/trusted-sampling/qualification-jobs"), Permission(Capability.Maintain)]
public sealed class PrtgTrustedSamplingQualificationJobsController(StorageBackend backend,
    IHostStore hosts, IVisibilityService visibility, ICurrentUser user) : ControllerBase
{
    private readonly PrtgQualificationJobStateStore jobs = new(backend);
    private readonly PrtgQualificationCapacityPilotStore pilots = new(backend);

    [HttpGet("contract")]
    public IActionResult Contract()
    {
        if (!TryCapture(out var settings, out var policy, out var contract, out var failure))
            return Conflict(ApiResponse.Fail("source_not_ready", failure));
        if (!CanManageScope(policy!)) return Forbid();
        var pilot = pilots.Read();
        var pilotCurrent = pilot is not null && PrtgQualificationCapacityEvaluator.ValidPilot(pilot, contract!.SourceFingerprint,
            contract.ScopeFingerprint, PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(),
            contract.VersionFingerprint, DateTimeOffset.UtcNow);
        return Ok(ApiResponse<QualificationJobContract>.Ok(new(settings!.Revision, policy!.Revision,
            contract.ScopeFingerprint, policy.SensorIds.Count, pilotCurrent,
            pilotCurrent ? "capacity-pilot-current" : "qualification-capacity-pilot-required")));
    }

    [HttpPost("pilot")]
    public async Task<IActionResult> Pilot([FromBody] QualificationPilotRequest request, CancellationToken ct)
    {
        if (request?.SensorObjids is null || request.SensorObjids.Count is < 1 or > PrtgQualificationCapacityEvaluator.MaximumPilotSensors ||
            request.SensorObjids.Any(id => id <= 0) || request.SensorObjids.Distinct().Count() != request.SensorObjids.Count)
            return BadRequest(ApiResponse.Fail("validation_failed", "資格容量 pilot 一次需提供 1 至 5 個不同的 sensor ID。"));
        if (!TryCapture(out var settings, out var policy, out var contract, out var failure))
            return Conflict(ApiResponse.Fail("source_not_ready", failure));
        if (!CanManageScope(policy!)) return Forbid();
        if (policy!.SensorIds.Count > PrtgQualificationJobStateStore.MaximumSensors ||
            request.SensorObjids.Any(id => !policy.SensorIds.Contains(id)))
            return BadRequest(ApiResponse.Fail("sensor_outside_policy", "pilot sensor 必須位於目前完整政策範圍內。"));

        var requestCount = 0;
        var elapsed = 0d;
        var maxSensorElapsed = 0d;
        var versions = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<PrtgTrustedSamplingProbeRow>(request.SensorObjids.Count);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        foreach (var id in request.SensorObjids.Order())
        {
            var probe = await new PrtgTrustedSamplingProbeService(backend).ProbeAsync([id], deadline.Token,
                requestPurpose: PrtgRequestPurpose.CapacityPilot,
                onRequestAttempted: () => requestCount++,
                qualificationPilotOnly: true,
                onSensorElapsed: value =>
                {
                    elapsed += value.TotalSeconds;
                    maxSensorElapsed = Math.Max(maxSensorElapsed, value.TotalSeconds);
                },
                onHistoricSourceVersion: value => versions.Add(value));
            rows.AddRange(probe);
        }
        if (!TryCapture(out var afterSettings, out var afterPolicy, out var afterContract, out failure))
        {
            var currentPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
            if (!user.Has(Capability.Maintain) || !CanManageScope(currentPolicy)) return Forbid();
            return Conflict(ApiResponse.Fail("source_changed", failure));
        }
        if (!user.Has(Capability.Maintain) || !CanManageScope(afterPolicy!)) return Forbid();
        if (requestCount != rows.Count * 5 || rows.Count != request.SensorObjids.Count ||
            rows.Any(row => row.Status != "pilot-qualified") || versions.Count != 1)
            return Ok(ApiResponse<QualificationPilotResult>.Ok(new("waiting-capacity",
                "qualification-pilot-shape-or-raw-proof-unavailable", null, rows)));
        if (afterSettings!.Revision != settings!.Revision || afterPolicy!.Revision != policy!.Revision ||
            afterContract != contract)
            return Conflict(ApiResponse.Fail("source_changed", "來源或完整範圍在容量探測期間已變更；本次證據未保存。"));

        var now = DateTimeOffset.UtcNow;
        var pilot = new PrtgQualificationCapacityPilot(contract!.SourceFingerprint, contract.ScopeFingerprint,
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), versions.Single(),
            PrtgProfileTransportCapacityPilot.RuntimeVersionFingerprint(), now,
            rows.Count, rows.Count, rows.Count, rows.Count, rows.Count, rows.Count,
            elapsed, maxSensorElapsed, RawIdentityAndTimeValidated: true);
        pilots.Save(pilot);
        return Ok(ApiResponse<QualificationPilotResult>.Ok(new("qualified",
            "bounded-native-qualification-pilot-recorded", pilot, rows)));
    }

    [HttpPost("start")]
    public IActionResult Start([FromBody] QualificationJobStartRequest request)
    {
        if (request is null || request.DurationHours is < 1 or > 720 || request.MaximumAttempts is < 1 or > 3 ||
            string.IsNullOrWhiteSpace(request.ExpectedSettingsRevision) ||
            string.IsNullOrWhiteSpace(request.ExpectedPolicyRevision) ||
            string.IsNullOrWhiteSpace(request.ExpectedScopeFingerprint))
            return BadRequest(ApiResponse.Fail("validation_failed", "durationHours 必須為 1..720，並提供目前設定、政策與範圍 revision。"));
        return StartCore(request, expectedCurrentJobId: null, expectedCurrentVersion: null);
    }

    [HttpPost("{jobId}/resume")]
    public IActionResult Resume(string jobId, [FromBody] QualificationJobResumeRequest request)
    {
        if (request is null) return BadRequest(ApiResponse.Fail("validation_failed", "續跑要求缺少版本或期限。"));
        if (request.DurationHours is < 1 or > 720 || request.MaximumAttempts is < 1 or > 3)
            return BadRequest(ApiResponse.Fail("validation_failed", "期限必須為 1..720 小時、每顆 sensor 最多探測 1..3 次。"));
        jobs.ExpireIfDeadlinePassed(DateTimeOffset.UtcNow);
        var current = jobs.ReadCurrent();
        if (current is null || current.JobId != jobId || current.Version != request.ExpectedVersion ||
            current.Status is "initializing" or "running" or "waiting-capacity")
            return Conflict(ApiResponse.Fail("stale_job", "只能明確續跑目前已結束或已逾期的資格作業。"));
        if (current.Status == "failed-stale")
            return Conflict(ApiResponse.Fail("source_context_changed", "來源、設定或範圍已變更；請明確開始新作業，舊作業證據會保留至新作業取代。"));
        if (!TryCapture(out var capturedSettings, out var capturedPolicy, out var contract, out var failure))
            return Conflict(ApiResponse.Fail("source_not_ready", failure));
        if (!CanManageScope(capturedPolicy!)) return Forbid();
        var authorityContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(capturedPolicy!,
            PrtgFetchStrategy.Normalize(capturedSettings!.PrtgFetchStrategy),
            PrtgFetchStrategy.Profile(PrtgFetchStrategy.Normalize(capturedSettings.PrtgFetchStrategy)).SnapshotIntervalMinutes);
        if (current.SourceGeneration != capturedPolicy!.SourceGeneration ||
            current.SettingsRevision != capturedSettings.Revision || current.PolicyRevision != capturedPolicy.Revision ||
            current.ScopeFingerprint != contract!.ScopeFingerprint ||
            current.AuthorityContextFingerprint != authorityContext)
            return Conflict(ApiResponse.Fail("source_context_changed", "來源、設定、作用範圍或採樣語意已變更；請明確開始新作業。"));

        var plan = new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey))
            .ReadCurrent(DateTimeOffset.UtcNow);
        if (plan is null || plan.SourceFingerprint != contract.SourceFingerprint ||
            plan.ProfileScopeFingerprint != contract.ScopeFingerprint ||
            plan.StrategyFingerprint != contract.StrategyFingerprint ||
            plan.RequestShapeFingerprint != contract.RequestShapeFingerprint ||
            plan.RuntimeVersionFingerprint != contract.VersionFingerprint || plan.PolicyRevision != capturedPolicy.Revision)
            return Ok(ApiResponse<PrtgQualificationStartResult>.Ok(new("waiting-capacity",
                "shared-table-plan-missing-or-stale", null, current)));

        var eligible = 0;
        for (var pageIndex = 0; pageIndex < current.InitializedPages; pageIndex++)
            eligible += jobs.ReadPage(jobId, pageIndex).Sensors.Count(sensor =>
                sensor.BindingRevision > 0 && !sensor.HasQualificationProof);
        var scopedSensorIds = capturedPolicy!.SensorIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (scopedSensorIds.Length != current.Selected || current.InitializedPages < 0 ||
            current.InitializedPages > current.PageCount || current.InitializationCursor != current.InitializedPages)
            return Conflict(ApiResponse.Fail("stale_job_or_scope", "作業範圍或初始化游標不一致，請重新載入或開始新作業。"));
        var bindingStore = new PrtgTrustedSamplingBindingStore(backend);
        for (var pageIndex = current.InitializedPages; pageIndex < current.PageCount; pageIndex++)
        {
            var pageIds = scopedSensorIds.Skip(pageIndex * PrtgQualificationJobStateStore.PageSize)
                .Take(PrtgQualificationJobStateStore.PageSize).ToArray();
            var bindings = bindingStore.GetMany(pageIds);
            eligible += pageIds.Count(id => bindings.TryGetValue(id, out var binding) &&
                string.IsNullOrWhiteSpace(binding.QualificationProofReference));
        }
        var pilot = pilots.Read();
        var capacity = PrtgQualificationCapacityEvaluator.Evaluate(eligible, request.DurationHours,
            request.MaximumAttempts, pilot, contract.SourceFingerprint, contract.ScopeFingerprint,
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), DateTimeOffset.UtcNow,
            plan.ProfileTableRequestsPerSecond);
        if (!capacity.Admitted)
            return Ok(ApiResponse<PrtgQualificationStartResult>.Ok(new(capacity.Status, capacity.Reason, capacity, current)));

        if (!TryCapture(out var latestSettings, out var latestPolicy, out var latestContract, out failure))
            return Conflict(ApiResponse.Fail("source_changed", failure));
        if (!user.Has(Capability.Maintain) || !CanManageScope(latestPolicy!)) return Forbid();
        if (latestSettings!.Revision != capturedSettings!.Revision || latestPolicy!.Revision != capturedPolicy!.Revision ||
            latestContract != contract)
            return Conflict(ApiResponse.Fail("stale_job_or_scope", "來源、範圍或權限在續跑准入期間已變更。"));

        var owner = "http-resume-" + Guid.NewGuid().ToString("N");
        PrtgQualificationJobStateStore.Job resumed;
        try
        {
            resumed = jobs.ResumeExisting(jobId, request.ExpectedVersion, owner, contract.ScopeFingerprint,
                capturedPolicy.SourceGeneration, capturedSettings.Revision, capturedPolicy.Revision,
                authorityContext, eligible, request.DurationHours, request.MaximumAttempts, DateTimeOffset.UtcNow,
                PrtgTrustedSamplingQualificationJobHostedService.LeaseDuration);
        }
        catch (InvalidOperationException)
        {
            return Conflict(ApiResponse.Fail("stale_job_or_binding", "作業、來源、範圍或 binding 在續跑准入時已變更，請重新載入目前狀態。"));
        }
        jobs.ReleaseLease(jobId, owner, resumed.Version, DateTimeOffset.UtcNow);
        return Accepted(ApiResponse<PrtgQualificationStartResult>.Ok(new("running",
            "existing-job-resumed-with-preserved-pages-and-attempt-watermark", capacity, jobs.ReadCurrent())));
    }

    [HttpGet("current")]
    public IActionResult Current()
    {
        jobs.ExpireIfDeadlinePassed(DateTimeOffset.UtcNow);
        if (!TryCurrentScope(out var policy, out var scopeFingerprint))
            return Conflict(ApiResponse.Fail("current_scope_unavailable", "目前完整政策範圍無法核對，作業資訊暫不提供。"));
        if (!CanManageScope(policy)) return Forbid();
        var current = jobs.ReadCurrent();
        if (current is not null && current.ScopeFingerprint != scopeFingerprint)
            return Conflict(ApiResponse.Fail("stale_job_scope", "作業建立時的完整範圍已變更；舊作業資料不提供。請明確開始新作業。"));
        return Ok(ApiResponse<PrtgQualificationJobStateStore.Job?>.Ok(current));
    }

    [HttpGet("{jobId}/page")]
    public IActionResult Page(string jobId, [FromQuery] int offset = 0, [FromQuery] int limit = 100)
    {
        var job = jobs.ReadCurrent();
        if (job is null || job.JobId != jobId) return NotFound(ApiResponse.Fail("job_missing", "資格作業不存在。"));
        if (!TryCurrentScope(out var policy, out var scopeFingerprint))
            return Conflict(ApiResponse.Fail("current_scope_unavailable", "目前完整政策範圍無法核對，作業資料暫不提供。"));
        if (!CanManageScope(policy)) return Forbid();
        if (job.ScopeFingerprint != scopeFingerprint)
            return Conflict(ApiResponse.Fail("stale_job_scope", "作業建立時的完整範圍已變更；sensor 分頁不提供。請明確開始新作業。"));
        if (offset < 0 || offset > job.Selected || limit is < 1 or > PrtgQualificationJobStateStore.PageSize)
            return BadRequest(ApiResponse.Fail("validation_failed", "分頁 offset/limit 無效。"));
        var rows = new List<PrtgQualificationJobStateStore.Sensor>(limit);
        for (var index = offset; index < Math.Min(job.Selected, offset + limit);)
        {
            var pageIndex = index / PrtgQualificationJobStateStore.PageSize;
            if (pageIndex >= job.InitializedPages) break;
            var page = jobs.ReadPage(jobId, pageIndex);
            var local = index % PrtgQualificationJobStateStore.PageSize;
            var take = Math.Min(limit - rows.Count, page.Sensors.Count - local);
            if (take <= 0)
            {
                if (!TryCurrentScope(out var incompletePolicy, out var incompleteScope))
                    return Conflict(ApiResponse.Fail("current_scope_unavailable", "目前完整政策範圍無法核對。"));
                if (!user.Has(Capability.Maintain) || !CanManageScope(incompletePolicy)) return Forbid();
                if (incompleteScope != job.ScopeFingerprint)
                    return Conflict(ApiResponse.Fail("stale_job_scope", "作業範圍已變更；sensor 分頁不提供。"));
                return Conflict(ApiResponse.Fail("job_page_incomplete", "作業分頁不完整；請稍後重新載入。"));
            }
            rows.AddRange(page.Sensors.Skip(local).Take(take));
            index += take;
        }
        var next = rows.Count > 0 && offset + rows.Count < job.Selected ? offset + rows.Count : (int?)null;
        if (!TryCurrentScope(out var currentPolicy, out var currentScope))
            return Conflict(ApiResponse.Fail("current_scope_unavailable", "目前完整政策範圍無法核對，sensor 分頁不提供。"));
        if (!user.Has(Capability.Maintain) || !CanManageScope(currentPolicy)) return Forbid();
        if (currentScope != job.ScopeFingerprint)
            return Conflict(ApiResponse.Fail("stale_job_scope", "作業範圍已變更；sensor 分頁不提供。"));
        if (jobs.ReadCurrent() is not { } latest || latest.JobId != jobId || latest.Version != job.Version)
            return Conflict(ApiResponse.Fail("stale_job", "作業狀態在讀取期間已變更，請重新載入。"));
        return Ok(ApiResponse<QualificationJobPage>.Ok(new(job.Status, job.Selected, offset, limit, next, rows)));
    }

    [HttpPost("{jobId}/cancel")]
    public IActionResult Cancel(string jobId, [FromBody] QualificationJobCancelRequest request)
    {
        if (request is null) return BadRequest(ApiResponse.Fail("validation_failed", "取消要求缺少 expectedVersion。"));
        var current = jobs.ReadCurrent();
        if (current is null || current.JobId != jobId) return NotFound(ApiResponse.Fail("job_missing", "資格作業不存在。"));
        if (!TryCurrentScope(out var policy, out var scopeFingerprint))
            return Conflict(ApiResponse.Fail("current_scope_unavailable", "目前完整政策範圍無法核對；作業未取消。"));
        if (!CanManageScope(policy)) return Forbid();
        if (current.ScopeFingerprint != scopeFingerprint)
            return Conflict(ApiResponse.Fail("stale_job_scope", "作業建立時的完整範圍已變更；舊作業無法由目前範圍取消。請明確開始新作業。"));
        if (request.ExpectedWave is <= 0)
            return BadRequest(ApiResponse.Fail("validation_failed", "取消作業的輪次必須大於零。"));
        try
        {
            jobs.Cancel(jobId, user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.ExpectedVersion, DateTimeOffset.UtcNow, request.ExpectedWave);
        }
        catch (InvalidOperationException)
        {
            return Conflict(ApiResponse.Fail("stale_job", "作業已結束或已明確續跑，請重新載入後再操作。"));
        }
        return Ok(ApiResponse<PrtgQualificationJobStateStore.Job?>.Ok(jobs.ReadCurrent()));
    }

    private IActionResult StartCore(QualificationJobStartRequest request, string? expectedCurrentJobId,
        long? expectedCurrentVersion)
    {
        jobs.ExpireIfDeadlinePassed(DateTimeOffset.UtcNow);
        if (!TryCapture(out var settings, out var policy, out var contract, out var failure))
            return Conflict(ApiResponse.Fail("source_not_ready", failure));
        if (!CanManageScope(policy!)) return Forbid();
        if (settings!.Revision != request.ExpectedSettingsRevision || policy!.Revision != request.ExpectedPolicyRevision ||
            contract!.ScopeFingerprint != request.ExpectedScopeFingerprint)
            return Conflict(ApiResponse.Fail("catalogue_or_source_changed", "設定、政策或完整範圍已變更，請重新載入。"));
        var ids = policy.SensorIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length is < 1 or > PrtgQualificationJobStateStore.MaximumSensors || ids.Length != policy.SensorIds.Count)
            return BadRequest(ApiResponse.Fail("scope_too_large_or_invalid", "政策 sensor 範圍必須為 1..15000 個唯一 ID。"));
        if (jobs.ReadCurrent() is { Status: "initializing" or "running" or "waiting-capacity" })
            return Conflict(ApiResponse.Fail("qualification_job_active", "目前仍有資格作業執行中；請等待全域 worker 關閉舊作業後再開始新作業。"));
        if (expectedCurrentJobId is not null)
        {
            var current = jobs.ReadCurrent();
            if (current is null || current.JobId != expectedCurrentJobId || current.Version != expectedCurrentVersion ||
                current.Status is "initializing" or "running" or "waiting-capacity")
                return Conflict(ApiResponse.Fail("stale_job", "原作業狀態已變更；請重新載入後明確續跑。"));
        }
        var plan = new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey))
            .ReadCurrent(DateTimeOffset.UtcNow);
        if (plan is null || plan.SourceFingerprint != contract.SourceFingerprint ||
            plan.ProfileScopeFingerprint != contract.ScopeFingerprint ||
            plan.StrategyFingerprint != contract.StrategyFingerprint ||
            plan.RequestShapeFingerprint != contract.RequestShapeFingerprint ||
            plan.RuntimeVersionFingerprint != contract.VersionFingerprint || plan.PolicyRevision != policy.Revision)
            return Ok(ApiResponse<PrtgQualificationStartResult>.Ok(new("waiting-capacity",
                "shared-table-plan-missing-or-stale", null, null)));

        var eligible = 0;
        for (var start = 0; start < ids.Length; start += PrtgQualificationJobStateStore.PageSize)
        {
            var page = ids.Skip(start).Take(PrtgQualificationJobStateStore.PageSize).ToArray();
            var bindings = new PrtgTrustedSamplingBindingStore(backend).GetMany(page);
            eligible += bindings.Values.Count(binding => string.IsNullOrWhiteSpace(binding.QualificationProofReference));
        }
        var pilot = pilots.Read();
        var capacity = PrtgQualificationCapacityEvaluator.Evaluate(eligible, request.DurationHours, request.MaximumAttempts,
            pilot, contract.SourceFingerprint, contract.ScopeFingerprint,
            PrtgQualificationCapacityEvaluator.RequestShapeFingerprint(), DateTimeOffset.UtcNow,
            plan.ProfileTableRequestsPerSecond);
        if (!capacity.Admitted)
            return Ok(ApiResponse<PrtgQualificationStartResult>.Ok(new(capacity.Status, capacity.Reason, capacity, null)));

        if (!TryCapture(out var latestSettings, out var latestPolicy, out var latestContract, out failure))
            return Conflict(ApiResponse.Fail("source_changed", failure));
        if (!user.Has(Capability.Maintain) || !CanManageScope(latestPolicy!)) return Forbid();
        if (latestSettings!.Revision != settings!.Revision || latestPolicy!.Revision != policy!.Revision ||
            latestContract != contract)
            return Conflict(ApiResponse.Fail("stale_job_or_scope", "來源、範圍或權限在作業准入期間已變更。"));

        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid().ToString("N");
        var owner = "http-init-" + Guid.NewGuid().ToString("N");
        var authorityContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy,
            PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
            PrtgFetchStrategy.Profile(PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy)).SnapshotIntervalMinutes);
        PrtgQualificationJobStateStore.Job started;
        try
        {
            started = jobs.Start(jobId, owner, contract.ScopeFingerprint, policy.SourceGeneration,
                settings.Revision, policy.Revision, authorityContext, ids.Length, eligible,
                request.DurationHours, now, request.MaximumAttempts);
        }
        catch (InvalidOperationException)
        {
            return Conflict(ApiResponse.Fail("stale_job_or_scope", "來源或資格作業在准入期間已變更，請重新載入。"));
        }
        jobs.ReleaseLease(jobId, owner, started.Version, DateTimeOffset.UtcNow);
        return Accepted(ApiResponse<PrtgQualificationStartResult>.Ok(new("initializing", "durable-job-accepted", capacity, jobs.ReadCurrent())));
    }

    private bool TryCapture(out SystemSettings? settings, out PrtgMonitoringPolicy? policy,
        out PrtgProfileTransportCapacityPilot.Contract? contract, out string failure)
    {
        settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        contract = null;
        failure = "source-policy-not-ready";
        if (!settings.PrtgEnabled || !policy.Ready(settings.PrtgUrl) || policy.SensorIds.Count is < 1 or > 15000) return false;
        try
        {
            contract = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(settings, policy, hosts.CapturePrtgSnapshot());
            failure = "";
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        { failure = "qualification-source-contract-unavailable"; return false; }
    }

    private bool TryCurrentScope(out PrtgMonitoringPolicy policy, out string scopeFingerprint)
    {
        policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        scopeFingerprint = "";
        try
        {
            scopeFingerprint = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(
                settings, policy, hosts.CapturePrtgSnapshot()).ScopeFingerprint;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        { return false; }
    }

    private bool CanManageScope(PrtgMonitoringPolicy policy)
    {
        var snapshot = hosts.CapturePrtgSnapshot();
        var visible = visibility.GetVisibleHostIds(snapshot).ToHashSet();
        var all = user.Has(Capability.ViewAll);
        return !((visible.Count == 0 && !all) || policy.HostIds.Any(visibility.IsCaseGrantOnly) ||
            (!all && !policy.HostIds.All(visible.Contains)));
    }
}

public sealed record QualificationPilotRequest(IReadOnlyList<long> SensorObjids);
public sealed record QualificationPilotResult(string Status, string Reason,
    PrtgQualificationCapacityPilot? Pilot, IReadOnlyList<PrtgTrustedSamplingProbeRow> Rows);
public sealed record QualificationJobStartRequest(int DurationHours, string ExpectedSettingsRevision,
    string ExpectedPolicyRevision, string ExpectedScopeFingerprint, int MaximumAttempts = 1);
public sealed record QualificationJobResumeRequest(long ExpectedVersion, int DurationHours, int MaximumAttempts = 1);
public sealed record QualificationJobCancelRequest(long ExpectedVersion, int? ExpectedWave = null);
public sealed record QualificationJobContract(string SettingsRevision, string PolicyRevision,
    string ScopeFingerprint, int SelectedSensors, bool CapacityPilotCurrent, string Status);
public sealed record QualificationJobPage(string Status, int Total, int Offset, int Limit, int? NextOffset,
    IReadOnlyList<PrtgQualificationJobStateStore.Sensor> Rows);
public sealed record PrtgQualificationStartResult(string Status, string Reason,
    PrtgQualificationCapacityDecision? Capacity, PrtgQualificationJobStateStore.Job? Job);
