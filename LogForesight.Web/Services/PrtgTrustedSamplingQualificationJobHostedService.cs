using System.Diagnostics;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LogForesight.Web.Services;

/// <summary>Durable, fair, single-sensor raw-proof qualification worker.</summary>
public sealed class PrtgTrustedSamplingQualificationJobHostedService : BackgroundService
{
    // A single raw qualification may wait through a 90-second durable admission reservation,
    // then spend up to 30 seconds on the bounded native probe.
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan SliceDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(2);
    private readonly StorageBackend backend;
    private readonly ILogger<PrtgTrustedSamplingQualificationJobHostedService> logger;
    private readonly Func<long, PrtgQualificationWriteFence, string, CancellationToken,
        Task<IReadOnlyList<PrtgTrustedSamplingProbeRow>>> probeFactory;
    private readonly Func<SystemSettings, PrtgMonitoringPolicy,
        (bool Allowed, string? Fingerprint, string Reason)>? tableAdmissionOverride;
    private readonly PrtgTrustedSamplingProfileRefreshHostedService? profileRefresh;
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly PrtgQualificationJobStateStore store;

    public PrtgTrustedSamplingQualificationJobHostedService(StorageBackend backend,
        ILogger<PrtgTrustedSamplingQualificationJobHostedService> logger)
        : this(backend, logger, CreateNativeProbeFactory(backend), null, null) { }

    public PrtgTrustedSamplingQualificationJobHostedService(StorageBackend backend,
        ILogger<PrtgTrustedSamplingQualificationJobHostedService> logger,
        PrtgTrustedSamplingProfileRefreshHostedService profileRefresh)
        : this(backend, logger, CreateNativeProbeFactory(backend), null, profileRefresh) { }

    internal PrtgTrustedSamplingQualificationJobHostedService(StorageBackend backend,
        ILogger<PrtgTrustedSamplingQualificationJobHostedService> logger,
        Func<long, PrtgQualificationWriteFence, string, CancellationToken,
            Task<IReadOnlyList<PrtgTrustedSamplingProbeRow>>> probeFactory,
        Func<SystemSettings, PrtgMonitoringPolicy,
            (bool Allowed, string? Fingerprint, string Reason)>? tableAdmissionOverride = null,
        PrtgTrustedSamplingProfileRefreshHostedService? profileRefresh = null)
    {
        this.backend = backend;
        this.logger = logger;
        this.probeFactory = probeFactory ?? throw new ArgumentNullException(nameof(probeFactory));
        this.tableAdmissionOverride = tableAdmissionOverride;
        this.profileRefresh = profileRefresh;
        store = new PrtgQualificationJobStateStore(backend);
    }

    private static Func<long, PrtgQualificationWriteFence, string, CancellationToken,
        Task<IReadOnlyList<PrtgTrustedSamplingProbeRow>>> CreateNativeProbeFactory(StorageBackend backend) =>
        (sensorObjid, fence, admissionPlanFingerprint, ct) =>
            new PrtgTrustedSamplingProbeService(backend).ProbeAsync([sensorObjid], ct,
                allowQualification: true, requestPurpose: PrtgRequestPurpose.ProfileRefresh,
                allowUnrelatedPolicyRevisionChanges: false,
                admissionPlanFingerprint: admissionPlanFingerprint,
                expectedBindingRevision: fence.BindingRevision,
                expectedBindingFingerprint: fence.BindingFingerprint,
                qualificationOnly: true, qualificationFence: fence);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOneSliceAsync(stoppingToken))
                {
                    await Task.Delay(PollDelay, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Durable PRTG raw qualification slice stopped safely: {Reason}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal async Task<bool> RunOneSliceAsync(CancellationToken stoppingToken)
    {
        store.ExpireIfDeadlinePassed(DateTimeOffset.UtcNow);
        var current = store.ReadCurrent();
        if (current is null || current.Status is not ("initializing" or "running" or "waiting-capacity")) return false;
        if (!store.TryAcquireLease(owner, DateTimeOffset.UtcNow, LeaseDuration, out var leased) || leased is null)
            return false;
        await RunSliceAsync(leased, stoppingToken);
        return true;
    }

    private async Task RunSliceAsync(PrtgQualificationJobStateStore.Job job, CancellationToken stoppingToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            if (!HasCurrentAuthority(job, out var staleReason))
            {
                store.SetStatus(job.JobId, owner, job.Version, DateTimeOffset.UtcNow,
                    "failed-stale", staleReason, releaseQuota: true);
                return;
            }
            if (job.Status == "initializing")
            {
                bool initialized;
                try { initialized = InitializePages(job, started, stoppingToken); }
                catch (InvalidOperationException ex) when (ex.Message == "qualification-job-capacity-contract-changed")
                {
                    store.SetStatus(job.JobId, owner, job.Version, DateTimeOffset.UtcNow,
                        "failed-stale", "qualification-capacity-contract-changed", releaseQuota: true);
                    return;
                }
                if (!initialized)
                {
                    store.ReleaseLease(job.JobId, owner, job.Version, DateTimeOffset.UtcNow);
                    return;
                }
                job = store.ReadCurrent() ?? throw new InvalidOperationException("qualification-job-missing");
                if (job.Status != "running") return;
            }

            string? tableAdmissionFingerprint = null;
            while (started.Elapsed < SliceDuration && !stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                store.Renew(job.JobId, owner, job.Version, now, LeaseDuration);
                var next = FindNextRunnable(job, now);
                if (next is null)
                {
                    if (HasRunnableFuture(job, now, out var nextAttempt) && nextAttempt.HasValue)
                    {
                        store.ReleaseLease(job.JobId, owner, job.Version, DateTimeOffset.UtcNow);
                        var delay = nextAttempt.Value - DateTimeOffset.UtcNow;
                        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(250);
                        if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
                        await Task.Delay(delay, stoppingToken);
                        return;
                    }
                    store.SetStatus(job.JobId, owner, job.Version, DateTimeOffset.UtcNow,
                        job.Waiting > 0 || job.Failed > 0 ? "completed-with-waiting" : "completed",
                        job.Waiting > 0 || job.Failed > 0 ? "job-finished-with-unqualified-or-unbound-sensors" :
                            "all-bound-sensors-qualified", releaseQuota: true);
                    return;
                }

                var (pageIndex, sensor, pageVersion) = next.Value;
                store.SetCursor(job.JobId, owner, job.Version, now,
                    (pageIndex * PrtgQualificationJobStateStore.PageSize +
                     store.ReadPage(job.JobId, pageIndex).Sensors.FindIndex(row => row.SensorObjid == sensor.SensorObjid)) %
                    Math.Max(1, job.Selected));
                var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
                var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                var context = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy,
                    PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
                    PrtgFetchStrategy.Profile(PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy)).SnapshotIntervalMinutes);
                var fence = new PrtgQualificationWriteFence(job.JobId, owner, job.Version,
                    job.ScopeFingerprint, job.SourceGeneration, job.SettingsRevision, job.PolicyRevision,
                    job.AuthorityContextFingerprint, pageIndex, pageVersion, sensor.SensorObjid,
                    sensor.BindingRevision, sensor.BindingFingerprint);
                if (settings.Revision != job.SettingsRevision || policy.Revision != job.PolicyRevision ||
                    policy.SourceGeneration != job.SourceGeneration || context != job.AuthorityContextFingerprint)
                {
                    store.SetStatus(job.JobId, owner, job.Version, DateTimeOffset.UtcNow,
                        "failed-stale", "source-context-changed", releaseQuota: true);
                    return;
                }

                if (!TryGetRuntimeTableAdmission(settings, policy,
                    out tableAdmissionFingerprint, out var capacityReason))
                {
                    store.SetStatus(job.JobId, owner, job.Version, DateTimeOffset.UtcNow,
                        "waiting-capacity", capacityReason, releaseQuota: false);
                    store.ReleaseLease(job.JobId, owner, job.Version, DateTimeOffset.UtcNow);
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    return;
                }
                if (job.Status == "waiting-capacity")
                    store.SetStatus(job.JobId, owner, job.Version, DateTimeOffset.UtcNow,
                        "running", "runtime-table-admission-restored", releaseQuota: false);

                try
                {
                    var rows = await ProbeWithHeartbeatAsync(job, sensor, fence,
                        tableAdmissionFingerprint!, stoppingToken);
                    if (rows.Count == 1 && rows[0].Status == "qualified" && !rows[0].QualificationRecorded)
                    {
                        logger.LogWarning("Qualification proof was not committed under the active job fence for {SensorObjid}",
                            sensor.SensorObjid);
                        store.SetStatus(job.JobId, owner, job.Version, DateTimeOffset.UtcNow,
                            "failed-stale", "qualification-proof-not-recorded-for-job", releaseQuota: true);
                        return;
                    }
                    if (rows.Count == 1 && rows[0].Status == "qualified")
                        // Requeueing current proof is idempotent and recovers a process restart
                        // after the atomic proof commit but before this worker observed its result.
                        _ = profileRefresh?.QueueCurrentQualificationProofRefresh(rows[0].SensorObjid);
                    if (rows.Count != 1 || rows[0].Status != "qualified")
                    {
                        var reason = rows.Count == 1 && rows[0].MissingAuthorityFields.Count > 0
                            ? rows[0].MissingAuthorityFields[0] : "source-proof-not-qualified";
                        store.RecordAttemptResult(fence, DateTimeOffset.UtcNow, reason);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (QualificationJobFenceCanceledException) { return; }
                catch (InvalidOperationException ex) when (
                    ex.Message.StartsWith("resource-identity-not-current:", StringComparison.Ordinal) ||
                    ex.Message == "source-policy-not-ready-or-outside-pilot")
                {
                    logger.LogWarning("Qualification source authority became invalid for {SensorObjid}: {Reason}",
                        sensor.SensorObjid, ex.Message);
                    store.SetStatus(job.JobId, owner, job.Version, DateTimeOffset.UtcNow,
                        "failed-stale", "source-authority-invalidated", releaseQuota: true);
                    return;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("fence-changed", StringComparison.Ordinal) ||
                    ex.Message.Contains("revision-changed", StringComparison.Ordinal) ||
                    ex.Message.Contains("lease-lost", StringComparison.Ordinal))
                {
                    logger.LogWarning("Qualification fence changed for {SensorObjid}: {Reason}",
                        sensor.SensorObjid, ex.Message);
                    store.SetStatus(job.JobId, owner, job.Version, DateTimeOffset.UtcNow,
                        "failed-stale", "binding-or-job-fence-changed", releaseQuota: true);
                    return;
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("prtg-capacity-admission-", StringComparison.Ordinal))
                {
                    store.SetStatus(job.JobId, owner, job.Version, DateTimeOffset.UtcNow,
                        "waiting-capacity", "runtime-table-admission-changed", releaseQuota: false);
                    store.ReleaseLease(job.JobId, owner, job.Version, DateTimeOffset.UtcNow);
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    return;
                }
                catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or TaskCanceledException or TimeoutException or
                    PrtgClientException or JsonException)
                {
                    store.RecordAttemptResult(fence, DateTimeOffset.UtcNow,
                        "bounded-source-probe-unavailable");
                }
                job = store.ReadCurrent() ?? throw new InvalidOperationException("qualification-job-missing");
                if (job.Owner != owner) return;
            }
            store.ReleaseLease(job.JobId, owner, job.Version, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<IReadOnlyList<PrtgTrustedSamplingProbeRow>> ProbeWithHeartbeatAsync(
        PrtgQualificationJobStateStore.Job job, PrtgQualificationJobStateStore.Sensor sensor,
        PrtgQualificationWriteFence fence, string admissionPlanFingerprint, CancellationToken stoppingToken)
    {
        var remaining = job.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new OperationCanceledException("qualification-job-deadline-reached");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var nextHeartbeatUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        var probe = probeFactory(sensor.SensorObjid, fence, admissionPlanFingerprint, linked.Token);
        try
        {
            while (!probe.IsCompleted)
            {
                var poll = Task.Delay(TimeSpan.FromSeconds(2), linked.Token);
                if (await Task.WhenAny(probe, poll) == probe) break;
                await poll;
                var current = store.ReadCurrent();
                if (current is null || current.JobId != job.JobId || current.Owner != owner ||
                    current.Version != job.Version || current.CancelRequested ||
                    current.Status is not ("running" or "waiting-capacity") || current.DeadlineUtc <= DateTimeOffset.UtcNow)
                {
                    linked.Cancel();
                    throw new QualificationJobFenceCanceledException();
                }
                if (DateTimeOffset.UtcNow >= nextHeartbeatUtc)
                {
                    store.Renew(job.JobId, owner, job.Version, DateTimeOffset.UtcNow, LeaseDuration);
                    nextHeartbeatUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                }
            }
            return await probe;
        }
        catch
        {
            linked.Cancel();
            try { await probe; } catch { /* Preserve the original fence, deadline, or shutdown failure. */ }
            throw;
        }
    }

    private sealed class QualificationJobFenceCanceledException : OperationCanceledException { }

    private bool TryGetCurrentTableAdmission(SystemSettings settings, PrtgMonitoringPolicy policy,
        out string? fingerprint, out string reason)
    {
        fingerprint = null;
        reason = "qualification-runtime-table-admission-unavailable";
        try
        {
            var hostStore = new HostStore(backend.Blob("hosts"));
            var snapshotSelection = PrtgSnapshotTargetResolver.Resolve(backend, hostStore, settings,
                new SentinelStore(backend.Blob("sentinels")).GetAll(), policy);
            if (!PrtgCapacityRuntimeAdmission.TryGetCurrent(backend, hostStore, settings, snapshotSelection,
                out var plan, out reason)) return false;
            if (plan is null || plan.SourceFingerprint.Length == 0 || plan.ProfileScopeFingerprint.Length == 0 ||
                plan.PolicyRevision != policy.Revision || plan.SettingsRevision != settings.Revision)
            {
                reason = "qualification-runtime-table-admission-plan-stale";
                return false;
            }
            fingerprint = plan.Fingerprint;
            return !string.IsNullOrWhiteSpace(fingerprint);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            reason = "qualification-runtime-table-admission-" + ex.GetType().Name;
            return false;
        }
    }

    private bool TryGetRuntimeTableAdmission(SystemSettings settings, PrtgMonitoringPolicy policy,
        out string? fingerprint, out string reason)
    {
        if (tableAdmissionOverride is null)
            return TryGetCurrentTableAdmission(settings, policy, out fingerprint, out reason);
        var result = tableAdmissionOverride(settings, policy);
        fingerprint = result.Fingerprint;
        reason = result.Reason;
        return result.Allowed && !string.IsNullOrWhiteSpace(fingerprint);
    }

    private bool InitializePages(PrtgQualificationJobStateStore.Job job, Stopwatch slice, CancellationToken ct)
    {
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
        var context = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy, strategyName, strategyMinutes);
        var ids = policy.SensorIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length != job.Selected || ids.Length > PrtgQualificationJobStateStore.MaximumSensors)
            throw new InvalidOperationException("qualification-job-scope-changed");
        for (var pageIndex = job.InitializationCursor; pageIndex < job.PageCount; pageIndex++)
        {
            ct.ThrowIfCancellationRequested();
            if (slice.Elapsed >= SliceDuration) return false;
            store.Renew(job.JobId, owner, job.Version, DateTimeOffset.UtcNow, LeaseDuration);
            var start = pageIndex * PrtgQualificationJobStateStore.PageSize;
            var pageIds = ids.Skip(start).Take(PrtgQualificationJobStateStore.PageSize).ToArray();
            var bindings = new PrtgTrustedSamplingBindingStore(backend).GetMany(pageIds);
            var identities = backend.PrtgStore().GetResourceIdentities(pageIds);
            var sensors = pageIds.Select(id =>
            {
                bindings.TryGetValue(id, out var binding);
                var qualified = binding is not null && !string.IsNullOrWhiteSpace(binding.QualificationProofReference) &&
                    identities.TryGetValue(id, out var identity) && identity.Active && !identity.PendingReconciliation &&
                    binding.Matches(id, context, policy.SourceGeneration, identity.Generation, identity.Epoch, identity.ChannelGeneration) &&
                    binding.RawTimestampTimeZoneId == policy.RawTimestampTimeZoneId &&
                    binding.AnalysisTimeZoneId == policy.AnalysisTimeZoneId &&
                    binding.TimeBasisEvidenceReference == policy.TimeBasisEvidenceReference;
                return new PrtgQualificationJobStateStore.Sensor
                {
                    SensorObjid = id, BindingRevision = binding?.BindingRevision ?? 0,
                    BindingFingerprint = binding?.BindingFingerprint ?? "",
                    HasQualificationProof = binding is not null &&
                        !string.IsNullOrWhiteSpace(binding.QualificationProofReference),
                    Status = qualified ? "qualified" : binding is not null &&
                        !string.IsNullOrWhiteSpace(binding.QualificationProofReference) ? "failed" : "waiting",
                    Reason = binding is null ? "binding-missing" : qualified ? "proof-and-authority-current" :
                        string.IsNullOrWhiteSpace(binding.QualificationProofReference) ? "qualification-required" : "existing-proof-authority-stale"
                };
            }).ToArray();
            store.InitializePage(job.JobId, owner, job.Version, DateTimeOffset.UtcNow, pageIndex, sensors);
            job = store.ReadCurrent() ?? throw new InvalidOperationException("qualification-job-missing");
        }
        return job.InitializedPages == job.PageCount;
    }

    private (int PageIndex, PrtgQualificationJobStateStore.Sensor Sensor, long PageVersion)? FindNextRunnable(
        PrtgQualificationJobStateStore.Job job, DateTimeOffset now)
    {
        foreach (var segment in CircularPageSegments(job.Cursor, job.Selected,
            PrtgQualificationJobStateStore.PageSize))
        {
            var (page, version) = store.ReadPageWithVersion(job.JobId, segment.PageIndex);
            for (var local = segment.StartInclusive; local < segment.EndExclusive && local < page.Sensors.Count; local++)
            {
                var sensor = page.Sensors[local];
                if (PrtgQualificationJobStateStore.IsRunnableNow(sensor, job, now))
                    return (segment.PageIndex, sensor, version);
            }
        }
        return null;
    }

    internal static IReadOnlyList<(int PageIndex, int StartInclusive, int EndExclusive)> CircularPageSegments(
        int cursor, int selected, int pageSize)
    {
        if (selected is < 1 or > PrtgQualificationJobStateStore.MaximumSensors || pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(selected));
        var start = Math.Clamp(cursor, 0, selected - 1);
        var pageCount = (selected + pageSize - 1) / pageSize;
        var startingPage = start / pageSize;
        var startInPage = start % pageSize;
        var segments = new List<(int PageIndex, int StartInclusive, int EndExclusive)>(pageCount + 1);
        for (var pageOffset = 0; pageOffset < pageCount; pageOffset++)
        {
            var pageIndex = (startingPage + pageOffset) % pageCount;
            var pageLength = Math.Min(pageSize, selected - pageIndex * pageSize);
            var first = pageIndex == startingPage ? startInPage : 0;
            segments.Add((pageIndex, first, pageLength));
        }
        // Complete the global circular order only after visiting every later page. This
        // prevents a retry at the beginning of the current page from leapfrogging sensors
        // on subsequent pages on every cursor rotation.
        if (startInPage > 0) segments.Add((startingPage, 0, startInPage));
        return segments;
    }

    private bool HasRunnableFuture(PrtgQualificationJobStateStore.Job job, DateTimeOffset now,
        out DateTimeOffset? next)
    {
        next = null;
        for (var pageIndex = 0; pageIndex < job.PageCount; pageIndex++)
        {
            var page = store.ReadPage(job.JobId, pageIndex);
            var times = page.Sensors.Where(sensor => sensor.Status == "waiting" && sensor.BindingRevision > 0 &&
                    sensor.WaveAttempts < job.MaximumAttempts && sensor.NextAttemptUtc > now)
                .Select(sensor => sensor.NextAttemptUtc!.Value).ToArray();
            if (times.Length > 0) next = next is null || times.Min() < next ? times.Min() : next;
        }
        return next.HasValue;
    }

    private bool HasCurrentAuthority(PrtgQualificationJobStateStore.Job job, out string reason)
    {
        reason = "source-context-changed";
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (!settings.PrtgEnabled || settings.Revision != job.SettingsRevision ||
            policy.Revision != job.PolicyRevision || policy.SourceGeneration != job.SourceGeneration ||
            !policy.Ready(settings.PrtgUrl) || policy.SensorIds.Count != job.Selected ||
            policy.SensorIds.Count > PrtgQualificationJobStateStore.MaximumSensors)
            return false;
        try
        {
            var contract = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(settings, policy,
                new HostStore(backend.Blob("hosts")).CapturePrtgSnapshot());
            var strategy = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy,
                PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
                PrtgFetchStrategy.Profile(PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy)).SnapshotIntervalMinutes);
            if (contract.ScopeFingerprint != job.ScopeFingerprint || strategy != job.AuthorityContextFingerprint)
                return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        { return false; }
        reason = "";
        return true;
    }
}
