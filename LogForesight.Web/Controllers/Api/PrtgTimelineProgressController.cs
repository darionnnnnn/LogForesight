using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

[ApiController, Route("api/prtg/timeline-progress"), Permission(Capability.Maintain)]
public sealed class PrtgTimelineProgressController(
    StorageBackend backend,
    IHostStore hosts,
    IVisibilityService visibility,
    ICurrentUser currentUser) : ControllerBase
{
    private const int MaximumPageSize = 100;
    private static readonly string[] AuthorizationFenceBlobKeys =
        ["hosts", "users", "user_groups", "group_access", PermissionVersionStamp.BlobKey];

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken requestCancellation = default)
    {
        var cancellationToken = HttpContext?.RequestAborted ?? requestCancellation;
        cancellationToken.ThrowIfCancellationRequested();
        if (page < 1 || pageSize is < 1 or > MaximumPageSize)
            return BadRequest(ApiResponse.Fail("validation_failed", "頁碼需大於0，每頁最多100筆。"));

        var hostSnapshot = hosts.CapturePrtgSnapshot();
        var visible = visibility.GetVisibleHostIds(hostSnapshot);
        var authorizationFenceVersions = ReadAuthorizationFenceVersions();
        var policyBlob = backend.Blob(PrtgMonitoringPolicyStore.BlobKey);
        var policyVersion = policyBlob.ReadVersion();
        var policy = new PrtgMonitoringPolicyStore(policyBlob).Get();
        var settingsBlob = backend.Blob("system_settings");
        var settingsVersion = settingsBlob.ReadVersion();
        var settings = new SystemSettingsStore(settingsBlob).Get();
        var scopeRevision = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        if (!policy.Ready(settings.PrtgUrl) || policy.SensorIds.Count > PrtgSensorTimelineEvidence.MaximumBootstrapSensors)
            return Conflict(ApiResponse.Fail("scope_unavailable", "PRTG監控來源尚未確認或超出已計價感測器範圍。"));

        var owner = string.Equals(policy.ConfirmedBy, currentUser.UserId.ToString(), StringComparison.Ordinal);
        var fullVisibility = IsFullVisibility(hostSnapshot);
        if (!owner && !fullVisibility) return Forbid();
        if (!currentUser.Has(Capability.ViewAll) && policy.HostIds.Any(id => !visible.Contains(id) || visibility.IsCaseGrantOnly(id)))
            return Forbid();

        var selected = policy.SensorIds.Distinct().Order().ToArray();
        var totalPages = (selected.Length + pageSize - 1) / pageSize;
        var offset = ((long)page - 1) * pageSize;
        var pageIds = offset >= selected.Length ? [] : selected.Skip((int)offset).Take(pageSize).ToArray();
        var identityBefore = ReadIdentitySnapshot(pageIds);
        cancellationToken.ThrowIfCancellationRequested();
        var metadataResult = await new PrtgTimelineMetadataQuery(backend.CreateContext)
            .ReadAsync(pageIds, PrtgSensorTimelineStore.Prefix, cancellationToken).ConfigureAwait(false);
        var progress = new PrtgSensorTimelineProgressStore(backend.Blob(PrtgSensorTimelineProgressStore.BlobKey)).Get();

        var rows = pageIds.Select(sensorId =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!metadataResult.Rows.TryGetValue(sensorId, out var evidence))
                return new PrtgTimelineProgressRow(sensorId, null, "not-started", null, null,
                    PrtgSensorTimelineEvidence.SupportedBootstrapWindow.TotalHours, 0, null, null, null, "not-observed");

            if (evidence.Malformed)
                return new PrtgTimelineProgressRow(sensorId, null, "malformed", null, null,
                    PrtgSensorTimelineEvidence.SupportedBootstrapWindow.TotalHours, 0, null, null, null,
                    evidence.QualityReason ?? "metadata-malformed");

            var scopeMatches = evidence.HostId is > 0 && policy.HostIds.Contains(evidence.HostId.Value) &&
                evidence.SourceGeneration == policy.SourceGeneration &&
                evidence.EffectiveScopeFingerprint == policy.EffectiveSensorScope(sensorId, evidence.HostId.Value) &&
                !string.IsNullOrWhiteSpace(evidence.ResourceGeneration);
            if (!scopeMatches)
                return new PrtgTimelineProgressRow(sensorId, null, "source-scope-stale", null, null,
                    PrtgSensorTimelineEvidence.SupportedBootstrapWindow.TotalHours, 0,
                    null, null, null, "source-scope-stale");

            var identityMatches = identityBefore.TryGetValue(sensorId, out var identity) &&
                identity.Active && !identity.PendingReconciliation && identity.Epoch > 0 &&
                evidence.IdentityEpoch is > 0 &&
                identity.SourceGeneration == evidence.SourceGeneration && identity.Generation == evidence.ResourceGeneration &&
                identity.HostId == evidence.HostId && identity.Epoch == evidence.IdentityEpoch &&
                identity.ChannelGeneration == evidence.ChannelGeneration;
            if (!identityMatches)
                return new PrtgTimelineProgressRow(sensorId, evidence.HostId, "waiting-identity", null, null,
                    PrtgSensorTimelineEvidence.SupportedBootstrapWindow.TotalHours, 0, null, null, null,
                    "resource-identity-not-current");

            var status = evidence.BootstrapStatus is "complete" or "capacity-shortfall"
                ? evidence.BootstrapStatus : "capacity-unverified";
            return new PrtgTimelineProgressRow(sensorId, evidence.HostId, status, evidence.BootstrapStartedAt,
                null, PrtgSensorTimelineEvidence.SupportedBootstrapWindow.TotalHours,
                evidence.BootstrapPagesRead ?? 0, evidence.LastCompleteThrough, evidence.NextAttemptAt,
                evidence.PendingNextPage is > 0 ? evidence.PendingNextPage : null, evidence.QualityReason ?? "metadata-malformed",
                evidence.BootstrapCollectionCycleId == progress.BootstrapCycleId &&
                progress.BootstrapCycleAsOfUtc is { } asOf && evidence.LastCompleteThrough is { } through && through >= asOf &&
                evidence.PendingNextPage == 0);
        }).ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        if (policyBlob.ReadVersion() != policyVersion || settingsBlob.ReadVersion() != settingsVersion ||
            backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() != scopeRevision ||
            hosts.DataVersion != hostSnapshot.Version || !visibility.GetVisibleHostIds(hostSnapshot).Order().SequenceEqual(visible.Order()) ||
            !AuthorizationFenceVersionsMatch(authorizationFenceVersions) ||
            !owner && !IsFullVisibility(hostSnapshot) ||
            !policy.HostIds.All(id => currentUser.Has(Capability.ViewAll) ||
                visible.Contains(id) && !visibility.IsCaseGrantOnly(id)))
            return Conflict(ApiResponse.Fail("scope_changed", "PRTG範圍或主機授權於查詢期間改變；請重試。"));

        var identityAfter = ReadIdentitySnapshot(pageIds);
        if (pageIds.Any(id =>
            !identityBefore.TryGetValue(id, out var beforeIdentity) ||
            !identityAfter.TryGetValue(id, out var afterIdentity) ||
            beforeIdentity.Epoch != afterIdentity.Epoch || beforeIdentity.Generation != afterIdentity.Generation ||
            beforeIdentity.ChannelGeneration != afterIdentity.ChannelGeneration ||
            beforeIdentity.PendingReconciliation != afterIdentity.PendingReconciliation ||
            beforeIdentity.Active != afterIdentity.Active))
            return Conflict(ApiResponse.Fail("identity_changed", "PRTG 資源身分於查詢期間改變；請重新載入此頁。"));

        if (!metadataResult.StableVersions)
            return Conflict(ApiResponse.Fail("timeline_metadata_changed", "PRTG 感測器進度於查詢期間更新；請重新載入此頁。"));

        var revision = BuildRevision(policyVersion, settingsVersion, hostSnapshot.Version, scopeRevision,
            visible, page, pageSize, identityAfter, progress, authorizationFenceVersions);
        var response = new PrtgTimelineProgressPage(selected.Length, page, pageSize, totalPages,
            "capacity-unverified", PrtgSensorTimelineEvidence.SupportedBootstrapWindow.TotalHours, revision,
            progress.LastRoundStartedAt, progress.LastRoundCompletedAt, progress.LastRoundOutcome, rows,
            progress.BootstrapDeadlineHours, progress.BootstrapSettingsRevision, progress.BootstrapCycleId,
            progress.BootstrapCycleOutcome, progress.BootstrapCycleReason, progress.BootstrapCycleStartedAtUtc,
            progress.BootstrapCycleDeadlineAtUtc, progress.BootstrapCycleAsOfUtc,
            progress.BootstrapCycleSelectedSensors, progress.LastServedSensorId, "unknown", EstimateHours(selected.Length),
            rows.Count(row => row.InitialCollectionCaptured),
            progress.BootstrapCycleOutcome == "running" && progress.BootstrapCycleDeadlineAtUtc is { } deadline
                ? Math.Max(0, (long)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds)) : (long?)null,
            progress.PreviousBootstrapCycle);
        return Ok(ApiResponse<PrtgTimelineProgressPage>.Ok(response));
    }

    [HttpPut("configuration")]
    public IActionResult Configure([FromBody] PrtgTimelineBootstrapConfigurationRequest request)
    {
        if (request.DeadlineHours is < PrtgSensorTimelineProgress.MinimumBootstrapDeadlineHours or > PrtgSensorTimelineProgress.MaximumBootstrapDeadlineHours ||
            request.Page < 1 || request.PageSize is < 1 or > MaximumPageSize)
            return BadRequest(ApiResponse.Fail("validation_failed", "期限需為1至720小時，且頁面範圍有效。"));
        var current = CaptureAuthorizedScope(request.Page, request.PageSize, out var failure);
        if (failure is not null) return failure;
        var store = new PrtgSensorTimelineProgressStore(backend.Blob(PrtgSensorTimelineProgressStore.BlobKey));
        var progress = store.Get();
        var revision = BuildRevision(current!.PolicyVersion, current.SettingsVersion, current.HostVersion,
            current.ScopeRevision, current.Visible, request.Page, request.PageSize, current.Identity, progress,
            current.AuthorizationFenceVersions);
        if (!FixedRevisionEquals(revision, request.ExpectedRevision))
            return Conflict(ApiResponse.Fail("stale_revision", "PRTG來源、授權或資源身分已變更；重新載入後再保存。"));
        if (!StillAuthorized(current)) return Forbid();
        if (!store.TryUpdateExpected(progress.BootstrapCycleId, progress.BootstrapCycleOutcome,
            progress.BootstrapSettingsRevision, current.PolicyVersion, current.SettingsVersion,
            current.ScopeRevision, current.AuthorizationFenceVersions, state =>
            {
                state.BootstrapDeadlineHours = request.DeadlineHours;
                state.BootstrapSettingsRevision++;
            }))
            return Conflict(ApiResponse.Fail("stale_revision", "設定或週期已變更；重新載入後再保存。"));
        return Ok(ApiResponse.Ok());
    }

    [HttpPost("resume")]
    public IActionResult Resume([FromBody] PrtgTimelineBootstrapResumeRequest request)
    {
        if (request.Page < 1 || request.PageSize is < 1 or > MaximumPageSize)
            return BadRequest(ApiResponse.Fail("validation_failed", "頁面範圍無效。"));
        var current = CaptureAuthorizedScope(request.Page, request.PageSize, out var failure);
        if (failure is not null) return failure;
        var store = new PrtgSensorTimelineProgressStore(backend.Blob(PrtgSensorTimelineProgressStore.BlobKey));
        var progress = store.Get();
        var revision = BuildRevision(current!.PolicyVersion, current.SettingsVersion, current.HostVersion,
            current.ScopeRevision, current.Visible, request.Page, request.PageSize, current.Identity, progress,
            current.AuthorizationFenceVersions);
        if (!FixedRevisionEquals(revision, request.ExpectedRevision))
            return Conflict(ApiResponse.Fail("stale_revision", "PRTG來源、授權或資源身分已變更；重新載入後再續行。"));
        if (!StillAuthorized(current)) return Forbid();
        if (progress.BootstrapCycleOutcome != "deadline-exceeded" ||
            progress.BootstrapScopeFingerprint != PrtgSensorTimelineProgressStore.ScopeFingerprint(current.Policy, current.Settings))
            return Conflict(ApiResponse.Fail("resume_not_available", "僅可續行仍屬目前來源與範圍的逾期週期。"));
        var cycleId = progress.BootstrapCycleId;
        if (!store.TryUpdateExpected(cycleId, "deadline-exceeded", progress.BootstrapSettingsRevision,
            current.PolicyVersion, current.SettingsVersion, current.ScopeRevision, current.AuthorizationFenceVersions,
            state =>
            {
                state.PreviousBootstrapCycle = new(state.BootstrapCycleId, state.BootstrapSourceGeneration,
                    state.BootstrapScopeFingerprint, state.BootstrapCycleStartedAtUtc,
                    state.BootstrapCycleDeadlineAtUtc, "deadline-exceeded");
                state.BootstrapCycleDeadlineAtUtc = DateTimeOffset.UtcNow.AddHours(Math.Clamp(state.BootstrapDeadlineHours,
                    PrtgSensorTimelineProgress.MinimumBootstrapDeadlineHours,
                    PrtgSensorTimelineProgress.MaximumBootstrapDeadlineHours));
                state.BootstrapCycleOutcome = "running";
                state.BootstrapCycleReason = "explicitly-resumed-same-cycle-and-cursor";
            }))
            return Conflict(ApiResponse.Fail("stale_revision", "週期狀態已變更；重新載入後再續行。"));
        return Ok(ApiResponse.Ok());
    }

    private AuthorizedTimelineScope? CaptureAuthorizedScope(int page, int pageSize, out IActionResult? failure)
    {
        failure = null;
        var hostSnapshot = hosts.CapturePrtgSnapshot();
        var visible = visibility.GetVisibleHostIds(hostSnapshot);
        var authorizationFenceVersions = ReadAuthorizationFenceVersions();
        var policyBlob = backend.Blob(PrtgMonitoringPolicyStore.BlobKey);
        var policyVersion = policyBlob.ReadVersion();
        var policy = new PrtgMonitoringPolicyStore(policyBlob).Get();
        var settingsBlob = backend.Blob("system_settings");
        var settingsVersion = settingsBlob.ReadVersion();
        var settings = new SystemSettingsStore(settingsBlob).Get();
        var scopeRevision = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        if (!policy.Ready(settings.PrtgUrl) || policy.SensorIds.Count > PrtgSensorTimelineEvidence.MaximumBootstrapSensors)
        { failure = Conflict(ApiResponse.Fail("scope_unavailable", "PRTG監控來源尚未確認或超出已計價感測器範圍。")); return null; }
        var owner = string.Equals(policy.ConfirmedBy, currentUser.UserId.ToString(), StringComparison.Ordinal);
        if ((!owner && !IsFullVisibility(hostSnapshot)) ||
            !currentUser.Has(Capability.ViewAll) && policy.HostIds.Any(id => !visible.Contains(id) || visibility.IsCaseGrantOnly(id)))
        { failure = Forbid(); return null; }
        var selected = policy.SensorIds.Distinct().Order().ToArray();
        var offset = ((long)page - 1) * pageSize;
        var pageIds = offset >= selected.Length ? [] : selected.Skip((int)offset).Take(pageSize).ToArray();
        var identity = ReadIdentitySnapshot(pageIds);
        if (policyBlob.ReadVersion() != policyVersion || settingsBlob.ReadVersion() != settingsVersion ||
            backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() != scopeRevision ||
            hosts.DataVersion != hostSnapshot.Version || !visibility.GetVisibleHostIds(hostSnapshot).Order().SequenceEqual(visible.Order()) ||
            !AuthorizationFenceVersionsMatch(authorizationFenceVersions))
        { failure = Conflict(ApiResponse.Fail("scope_changed", "PRTG來源或授權已變更；重新載入後再試。")); return null; }
        return new AuthorizedTimelineScope(policy, settings, policyVersion, settingsVersion,
            hostSnapshot.Version, scopeRevision, visible, identity, authorizationFenceVersions);
    }

    private static string BuildRevision(long policyVersion, long settingsVersion, long hostVersion,
        long scopeRevision, IReadOnlySet<long> visible, int page, int pageSize,
        IReadOnlyDictionary<long, PrtgResourceIdentity> identities, PrtgSensorTimelineProgress progress,
        IReadOnlyDictionary<string, long> authorizationFenceVersions)
    {
        var material = string.Join("|", policyVersion, settingsVersion, hostVersion, scopeRevision, page, pageSize,
            progress.BootstrapCycleId, progress.BootstrapCycleOutcome, progress.BootstrapSettingsRevision,
            string.Join(";", authorizationFenceVersions.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")),
            string.Join(";", visible.Order()), string.Join(";", identities.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}:{pair.Value.Epoch}:{pair.Value.Generation}:{pair.Value.ChannelGeneration}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static bool FixedRevisionEquals(string expected, string? supplied) =>
        !string.IsNullOrWhiteSpace(supplied) && supplied.Length == expected.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));

    private IReadOnlyDictionary<string, long> ReadAuthorizationFenceVersions() =>
        AuthorizationFenceBlobKeys.ToDictionary(key => key, key => backend.Blob(key).ReadVersion(), StringComparer.Ordinal);

    private bool AuthorizationFenceVersionsMatch(IReadOnlyDictionary<string, long> expected) =>
        expected.All(pair => backend.Blob(pair.Key).ReadVersion() == pair.Value);

    private bool StillAuthorized(AuthorizedTimelineScope expected)
    {
        var snapshot = hosts.CapturePrtgSnapshot();
        var visible = visibility.GetVisibleHostIds(snapshot);
        var owner = string.Equals(expected.Policy.ConfirmedBy, currentUser.UserId.ToString(), StringComparison.Ordinal);
        return hosts.DataVersion == expected.HostVersion && visible.SetEquals(expected.Visible) &&
            (owner || IsFullVisibility(snapshot)) &&
            expected.Policy.HostIds.All(id => currentUser.Has(Capability.ViewAll) ||
                visible.Contains(id) && !visibility.IsCaseGrantOnly(id));
    }

    private static double EstimateHours(int sensorCount) =>
        (sensorCount * (double)PrtgSensorTimelineEvidence.MaximumBootstrapPagesPerSensor +
         Math.Ceiling(sensorCount / (double)PrtgResourceGuardProbe.MaxBatchSize) * 2) / 2d / 3600d / 0.75d;

    private sealed record AuthorizedTimelineScope(PrtgMonitoringPolicy Policy, SystemSettings Settings,
        long PolicyVersion, long SettingsVersion, long HostVersion, long ScopeRevision,
        IReadOnlySet<long> Visible, IReadOnlyDictionary<long, PrtgResourceIdentity> Identity,
        IReadOnlyDictionary<string, long> AuthorizationFenceVersions);

    private bool IsFullVisibility(PrtgHostSnapshot snapshot)
    {
        if (currentUser.Has(Capability.ViewAll)) return true;
        var visible = visibility.GetVisibleHostIds(snapshot);
        return visible.Count > 0 && snapshot.Hosts.Count > 0 && snapshot.Hosts.All(host =>
            visible.Contains(host.HostId) && !visibility.IsCaseGrantOnly(host.HostId));
    }

    private IReadOnlyDictionary<long, PrtgResourceIdentity> ReadIdentitySnapshot(IReadOnlyCollection<long> sensorIds)
    {
        var stored = backend.PrtgStore().GetResourceIdentities(sensorIds);
        return sensorIds.ToDictionary(id => id, id => stored.TryGetValue(id, out var identity)
            ? identity : new PrtgResourceIdentity { SensorId = id });
    }
}

public sealed record PrtgTimelineProgressPage(int SelectedSensors, int Page, int PageSize, int TotalPages,
    string CapacityStatus, double CalculatedBootstrapBaselineHours,
    string Revision,
    DateTimeOffset? LastRoundStartedAt, DateTimeOffset? LastRoundCompletedAt,
    string LastRoundOutcome, IReadOnlyList<PrtgTimelineProgressRow> Rows,
    int BootstrapDeadlineHours, int BootstrapSettingsRevision, string BootstrapCycleId,
    string BootstrapCycleOutcome, string BootstrapCycleReason, DateTimeOffset? BootstrapCycleStartedAtUtc,
    DateTimeOffset? BootstrapCycleDeadlineAtUtc, DateTimeOffset? BootstrapCycleAsOfUtc,
    int BootstrapCycleSelectedSensors, long LastServedSensorId, string BudgetTelemetryStatus,
    double EstimatedBootstrapBaselineHours, int BootstrapCycleCapturedOnPage,
    long? BootstrapCycleRemainingSeconds, PrtgTimelineBootstrapCycleSummary? PreviousBootstrapCycle);

public sealed record PrtgTimelineBootstrapConfigurationRequest(int DeadlineHours, string? ExpectedRevision,
    int Page = 1, int PageSize = 50);
public sealed record PrtgTimelineBootstrapResumeRequest(string? ExpectedRevision, int Page = 1, int PageSize = 50);

public sealed record PrtgTimelineProgressRow(long SensorId, long? HostId, string BootstrapStatus,
    DateTimeOffset? StartedAt, DateTimeOffset? DeadlineAt, double CalculatedBootstrapBaselineHours,
    int MessagesPagesRead, DateTimeOffset? LastCompleteThrough, DateTimeOffset? NextAttemptAt,
    int? ResumePage, string QualityReason, bool InitialCollectionCaptured = false);
