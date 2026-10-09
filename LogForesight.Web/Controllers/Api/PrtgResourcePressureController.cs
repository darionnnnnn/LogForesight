using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

[ApiController, Route("api/prtg/resource-pressure"), Permission(Capability.Maintain)]
public sealed class PrtgResourcePressureController(
    StorageBackend backend,
    ISystemSettingsStore settings,
    IVisibilityService visibility,
    ICurrentUser user) : ControllerBase
{
    [HttpPost("{hostId:long}/trial")]
    public IActionResult Trial(long hostId, [FromBody] PrtgResourceTrialRequest request)
    {
        visibility.EnsureVisible(hostId);
        if (!user.Has(Capability.Maintain) || visibility.IsCaseGrantOnly(hostId)) return Forbid();
        if (hostId <= 0 || request.SensorObjid <= 0)
            return BadRequest(ApiResponse.Fail("validation_failed", "主機或 sensor 識別碼無效。"));

        var now = DateTime.UtcNow;
        var result = new PrtgResourcePeriodConsumer(backend, settings)
            .EvaluateBatch([request.SensorObjid], now);
        var assessment = result.Assessments.FirstOrDefault(a => a.HostId == hostId &&
            a.SensorObjid == request.SensorObjid);
        if (assessment is null || assessment.Decision.Kind == PrtgResourceDecisionKind.Insufficient)
            return Conflict(ApiResponse.Fail("trial_evidence_unready", "目前沒有足夠的可信資源資料可完成試算。"));

        try
        {
            var profile = new PrtgTrustedSamplingProfileStore(backend).GetMany([assessment.SensorObjid])
                .GetValueOrDefault(assessment.SensorObjid);
            if (profile is null) return Conflict(ApiResponse.Fail("trial_evidence_unready", "目前無法讀取試算使用的來源時區。"));
            var zone = TimeZoneInfo.FindSystemTimeZoneById(profile.AnalysisTimeZoneId);
            var hourResults = new List<PrtgResourceTrialHour>(assessment.Decision.Window.Count);
            foreach (var hour in assessment.Decision.Window.OrderBy(hour => hour.WallPeriodStart))
            {
                var wallStart = DateTime.SpecifyKind(hour.WallPeriodStart, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(wallStart) || zone.IsAmbiguousTime(wallStart))
                    return Conflict(ApiResponse.Fail("trial_evidence_unready", "試算小時無法唯一對應 UTC 時間。"));
                var startUtc = TimeZoneInfo.ConvertTimeToUtc(wallStart, zone);
                hourResults.Add(new(startUtc, hour.AveragePercent, hour.CoveragePercent,
                    hour.GoodSlots, hour.RequiredSlots));
            }
            if (hourResults.Count != 2) return Conflict(ApiResponse.Fail("trial_evidence_unready", "試算未涵蓋兩個已完成 UTC 小時。"));
            var grant = new PrtgResourcePressureAuthorizationService(backend, settings)
                .IssueSuccessfulTrial(assessment, true, now);
            return Ok(ApiResponse<PrtgResourceTrialResponse>.Ok(new PrtgResourceTrialResponse(grant,
                assessment.Decision.Kind.ToString(), assessment.Decision.ReasonCode, hourResults,
                ThresholdPercent: 90, RecoveryPercent: 85, MinimumHourlyCoveragePercent: 75,
                EvidenceAsOfUtc: assessment.EvidenceAsOfUtc)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeZoneNotFoundException or
                                   InvalidTimeZoneException or ArgumentException)
        {
            return Conflict(ApiResponse.Fail("trial_evidence_unready", ex.Message));
        }
    }

    [HttpPut("{hostId:long}/mode")]
    public IActionResult SetMode(long hostId, [FromBody] PrtgResourceModeRequest request)
    {
        visibility.EnsureVisible(hostId);
        if (!user.Has(Capability.Maintain) || visibility.IsCaseGrantOnly(hostId)) return Forbid();
        if (hostId <= 0 || request.SensorObjid <= 0 || request.Enabled &&
            (string.IsNullOrWhiteSpace(request.TrialResultId) || request.TrialResultId.Length > 128))
            return BadRequest(ApiResponse.Fail("validation_failed", "主機、sensor 或試算識別碼無效。"));

        var now = DateTime.UtcNow;
        var authorization = new PrtgResourcePressureAuthorizationService(backend, settings);
        if (!request.Enabled)
        {
            var disabled = authorization.DisableFormalMode(hostId, request.SensorObjid, true, now);
            return disabled
                ? Ok(ApiResponse<object>.Ok(ModeSavedResponse(hostId, request.SensorObjid, request.Enabled)))
                : Conflict(ApiResponse.Fail("mode_not_enabled", "這個資源目前沒有正式模式。"));
        }

        var result = new PrtgResourcePeriodConsumer(backend, settings)
            .EvaluateBatch([request.SensorObjid], now);
        var assessment = result.Assessments.FirstOrDefault(a => a.HostId == hostId &&
            a.SensorObjid == request.SensorObjid);
        if (assessment is null)
            return Conflict(ApiResponse.Fail("trial_evidence_unready", "目前無法核實這個資源的來源身分。"));

        var changed = authorization.SetFormalMode(assessment, request.TrialResultId ?? "", true, true, now);
        return changed
            ? Ok(ApiResponse<object>.Ok(ModeSavedResponse(hostId, request.SensorObjid, request.Enabled)))
            : Conflict(ApiResponse.Fail("trial_expired_or_stale", "試算已過期，或資源來源、規則版本已變更；請重新試算。"));
    }

    [HttpGet("{hostId:long}/mode/replay")]
    public IActionResult ReplayStatus(long hostId, [FromQuery] long? sensorObjid = null)
    {
        visibility.EnsureVisible(hostId);
        if (sensorObjid is <= 0) return BadRequest(ApiResponse.Fail("validation_failed", "sensor 識別碼無效。"));
        return Ok(ApiResponse<object>.Ok(ModeSavedResponse(hostId, sensorObjid, null)));
    }

    private object ModeSavedResponse(long hostId, long? sensorObjid, bool? requestedEnabled)
    {
        var snapshot = new PrtgResourcePressureModeStore(backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(hostId))).ReadHostSnapshot(hostId);
        var progress = new PrtgResourcePressureModeReplayProgressStore(backend.Blob(
            PrtgResourcePressureModeReplayProgressStore.BlobKey(hostId)));
        var jobs = snapshot.ReplayJobs.Where(job => sensorObjid is null || job.SensorObjid == sensorObjid)
            .Select(job =>
            {
                var item = progress.Find(job);
                var status = item?.TransitionId == job.TransitionId ? item.Status.ToString() : "Pending";
                return new
                {
                    job.SensorObjid, job.Family, job.Enabled, job.RequestedAtUtc,
                    DeadlineArmed = job.DeadlineArmed || (item?.DueAtUtc is not null),
                    DueAtUtc = job.DeadlineArmed ? job.DueAtUtc : item?.DueAtUtc,
                    Status = status,
                    LastReason = item?.LastReason,
                    AfterRecordId = item?.AfterRecordId
                };
            }).ToArray();
        return new
        {
            ModeSaved = true,
            RequestedEnabled = requestedEnabled,
            ReplayPending = jobs.Any(job => job.Status is "Pending" or "Running" or "Waiting" or "Overdue"),
            ReplayJobs = jobs
        };
    }
}

public sealed record PrtgResourceTrialRequest(long SensorObjid);
public sealed record PrtgResourceModeRequest(long SensorObjid, string? TrialResultId, bool Enabled);
public sealed record PrtgResourceTrialResponse(PrtgResourceTrialGrant Grant, string Outcome,
    string ReasonCode, IReadOnlyList<PrtgResourceTrialHour> Hours, double ThresholdPercent,
    double RecoveryPercent, double MinimumHourlyCoveragePercent, DateTime EvidenceAsOfUtc);
public sealed record PrtgResourceTrialHour(DateTime StartUtc, double AveragePercent,
    double CoveragePercent, int GoodSlots, int RequiredSlots);
