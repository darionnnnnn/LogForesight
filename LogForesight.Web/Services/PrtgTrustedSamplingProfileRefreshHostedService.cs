using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LogForesight.Web.Services;

public sealed record PrtgTrustedSamplingProfileRefreshSensorRow(
    long SensorObjid, bool Eligible, string Status, string Reason,
    DateTimeOffset? LastOutcomeAtUtc, DateTimeOffset? NextAttemptAtUtc);

public sealed record PrtgTrustedSamplingProfileRefreshSensorPage(
    int Total, int Offset, int Limit, int? NextOffset,
    IReadOnlyList<PrtgTrustedSamplingProfileRefreshSensorRow> Rows);

public sealed record PrtgTrustedSamplingProfileRefreshProgress(
    DateTimeOffset? StartedAtUtc, DateTimeOffset? UpdatedAtUtc, DateTimeOffset? CompletedAtUtc,
    int Cursor, int SelectedSensors, int EligibleSensors, int Qualified, int Unavailable, int Failed,
    int Waiting, int RawRequestCount, double ObservedRequestSeconds,
    string LastOutcome, string? LastWaitingReason, bool Running, DateTimeOffset? LeaseUntilUtc,
    DateTimeOffset? NextSweepAtUtc);

/// <summary>Durable, fair profile refresh. Its DB lease is renewed while bounded source calls run.</summary>
public sealed class PrtgTrustedSamplingProfileRefreshHostedService(
    StorageBackend backend, IHostStore hosts, ILogger<PrtgTrustedSamplingProfileRefreshHostedService> logger)
    : BackgroundService
{
    public const string ProgressBlobKey = "prtg_trusted_profile_refresh_v1";
    private static readonly TimeSpan ProbeCooldown = TimeSpan.FromHours(1);
    private static readonly TimeSpan FreshRefreshAfter = TimeSpan.FromHours(23);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SliceDuration = TimeSpan.FromSeconds(
        PrtgJointCapacityEvaluator.ProfileRefreshSliceSeconds);
    private static readonly TimeSpan IdleRescanDelay = TimeSpan.FromHours(1);
    private const int MaxSelectedSensors = 15_000;
    private const int PageSize = 100;
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly PrtgTrustedSamplingProfileRefreshStateStore stateStore = new(backend);
    private readonly SemaphoreSlim proofRefreshWake = new(0, 1);
    private long leaseVersion;

    private sealed class SensorState
    {
        public bool Eligible { get; set; }
        public string ContractFingerprint { get; set; } = "";
        public DateTimeOffset? NextAttemptAtUtc { get; set; }
        public string Status { get; set; } = "waiting";
        public string Reason { get; set; } = "not-probed";
        public DateTimeOffset? LastOutcomeAtUtc { get; set; }
    }

    private sealed class State
    {
        public string ScopeFingerprint { get; set; } = "";
        public int Cursor { get; set; }
        public int SelectedSensors { get; set; }
        public int EligibleSensors { get; set; }
        public int Qualified { get; set; }
        public int Unavailable { get; set; }
        public int Failed { get; set; }
        public int Waiting { get; set; }
        public int RawRequestCount { get; set; }
        public double ObservedRequestSeconds { get; set; }
        public string LastOutcome { get; set; } = "not-started";
        public string? LastWaitingReason { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public DateTimeOffset? NextSweepAtUtc { get; set; }
        public string LeaseOwner { get; set; } = "";
        public long LeaseVersion { get; set; }
        public DateTimeOffset? LeaseUntilUtc { get; set; }
        public List<string> PreviousScopeFingerprints { get; set; } = [];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunSliceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "PRTG trusted profile refresh slice failed"); }
            await proofRefreshWake.WaitAsync(TimeSpan.FromSeconds(
                PrtgJointCapacityEvaluator.ProfileRefreshInterSliceDelaySeconds), stoppingToken);
        }
    }

    /// <summary>Persist and wake a bounded refresh request after a raw proof is committed.</summary>
    public PrtgTrustedSamplingProofRefreshRequest? QueueCurrentQualificationProofRefresh(
        long sensorObjid)
    {
        if (sensorObjid <= 0) throw new ArgumentOutOfRangeException(nameof(sensorObjid));
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (!settings.PrtgEnabled || !policy.Ready(settings.PrtgUrl) || !policy.SensorIds.Contains(sensorObjid)) return null;
        var binding = new PrtgTrustedSamplingBindingStore(backend).Get(sensorObjid);
        var identity = backend.PrtgStore().GetResourceIdentity(sensorObjid);
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var context = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy, strategyName,
            PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes);
        if (binding is null || string.IsNullOrWhiteSpace(binding.QualificationProofReference) ||
            !identity.Active || identity.PendingReconciliation || identity.SourceGeneration != policy.SourceGeneration ||
            !policy.HostIds.Contains(identity.HostId) || identity.ChannelFingerprint != binding.BindingFingerprint ||
            !binding.Matches(sensorObjid, context, policy.SourceGeneration, identity.Generation,
                identity.Epoch, identity.ChannelGeneration) ||
            binding.TimeBasisEvidenceReference != policy.TimeBasisEvidenceReference ||
            binding.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
            binding.AnalysisTimeZoneId != policy.AnalysisTimeZoneId)
            return null;
        var now = DateTimeOffset.UtcNow;
        var notice = new PrtgTrustedSamplingProofRefreshRequest(sensorObjid,
            binding.BindingRevision, binding.BindingFingerprint, binding.QualificationProofReference,
            identity.Epoch, identity.Generation, identity.SourceGeneration, identity.ChannelGeneration,
            context, settings.Revision, policy.Revision, now, now);
        new PrtgTrustedSamplingProfileRefreshStateStore(backend).QueueProofRefresh(notice);
        WakeForPendingProofRefresh();
        return notice;
    }

    public void WakeForPendingProofRefresh()
    {
        try { proofRefreshWake.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task RunSliceAsync(CancellationToken ct)
    {
        if (!TryAcquireLease(DateTimeOffset.UtcNow, out var version)) return;
        leaseVersion = version;
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = RenewLeaseLoopAsync(version, workCts);
        var deadline = DateTime.UtcNow + SliceDuration;
        var purgedOutOfScopeProofNotices = false;
        try
        {
            stateStore.PurgeExpiredPages(DateTimeOffset.UtcNow.AddDays(-14));
            while (DateTime.UtcNow < deadline && !workCts.IsCancellationRequested)
            {
                var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
                var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
                var policy = policyStore.Get();
                var ids = policy.SensorIds.Where(id => id > 0).Distinct().Order().Take(MaxSelectedSensors + 1).ToArray();
                if (policy.SensorIds.Count > MaxSelectedSensors || ids.Length > MaxSelectedSensors ||
                    !settings.PrtgEnabled || !policy.Ready(settings.PrtgUrl))
                {
                    Update(s => { s.LastOutcome = policy.SensorIds.Count > MaxSelectedSensors || ids.Length > MaxSelectedSensors
                            ? "scope-limit-exceeded" : "source-policy-not-ready";
                        s.LastWaitingReason = s.LastOutcome; s.UpdatedAtUtc = DateTimeOffset.UtcNow; });
                    return;
                }
                if (!purgedOutOfScopeProofNotices)
                {
                    stateStore.PurgeOutOfScopeProofRefreshNotices(ids);
                    purgedOutOfScopeProofNotices = true;
                }

                var catalog = PrtgResourceCurrentRuleCatalog.Load(backend);
                var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
                var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
                var strategy = new PrtgTrustedSamplingStrategyStateStore(backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey))
                    .GetCurrent(policy, strategyName, strategyMinutes, DateTime.UtcNow);
                var scopeFingerprint = ScopeFingerprint(policy, settings, strategy, catalog, ids);
                var state = PrepareScope(scopeFingerprint, ids.Length);
                if (state.Cursor == 0 && state.NextSweepAtUtc > DateTimeOffset.UtcNow)
                {
                    var proofRefreshDue = stateStore.EarliestPendingProofRefresh(ids);
                    if (proofRefreshDue is null || proofRefreshDue > DateTimeOffset.UtcNow) return;
                }
                if (state.Cursor >= ids.Length)
                {
                    CompleteSweep(ids);
                    return;
                }

                var scopeSnapshot = hosts.CapturePrtgSnapshot();
                var activeHosts = scopeSnapshot.Hosts.Where(h => h.Active && h.MergedInto is null)
                    .Select(h => h.HostId).ToHashSet();
                var pageStart = state.Cursor;
                var pageIds = ids.Skip(pageStart).Take(PageSize).ToArray();
                if (pageIds.Length == 0) { CompleteSweep(ids); return; }
                var pageIndex = pageStart / PageSize;
                var previousPage = ReadPageWithMigration(state, scopeFingerprint, pageIndex);
                var priorById = previousPage.Sensors.ToDictionary(row => row.SensorObjid);
                var metadata = backend.PrtgStore().GetResourcePressureSensorMetadata(pageIds)
                    .ToDictionary(row => row.SensorObjid);
                var identities = backend.PrtgStore().GetResourceIdentities(pageIds);
                var profiles = new PrtgTrustedSamplingProfileStore(backend).GetMany(pageIds);
                var bindings = new PrtgTrustedSamplingBindingStore(backend).GetMany(pageIds);
                var proofRefreshNotices = stateStore.ReadProofRefreshNotices(pageIds);
                var candidates = new List<long>();
                var pageStates = new List<(long Id, SensorState State)>();
                var now = DateTimeOffset.UtcNow;
                var authorityContextFingerprint = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
                    policy, strategyName, strategyMinutes);

                foreach (var id in pageIds)
                {
                    metadata.TryGetValue(id, out var sensor);
                    identities.TryGetValue(id, out var identity);
                    profiles.TryGetValue(id, out var profile);
                    bindings.TryGetValue(id, out var binding);
                    proofRefreshNotices.TryGetValue(id, out var proofNotice);
                    var proofNoticeCurrent = proofNotice is not null && IsCurrentProofRefreshNotice(
                        proofNotice, binding, identity, settings, policy);
                    if (proofNotice is not null && !proofNoticeCurrent)
                    {
                        _ = stateStore.CompleteProofRefresh(proofNotice);
                        proofNotice = null;
                    }
                    var family = FamilyForCategory(sensor?.Category);
                    var ruleFingerprint = family switch
                    {
                        PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory => catalog.For(family!.Value)?.Fingerprint,
                        PrtgResourceFamily.Disk when catalog.For(family!.Value) != null || catalog.DiskTrendEnabled => catalog.AdmissionFingerprintFor(family.Value),
                        _ => null
                    };
                    var eligible = sensor is not null && family.HasValue && ruleFingerprint is not null &&
                        !sensor.Paused && !sensor.DevicePaused && identity is { Active: true, PendingReconciliation: false } &&
                        identity.SourceGeneration == policy.SourceGeneration && identity.DeviceId == sensor.DeviceObjid &&
                        policy.HostIds.Contains(identity.HostId) && activeHosts.Contains(identity.HostId);
                    var contractFingerprint = SensorContractFingerprint(sensor, identity, ruleFingerprint,
                        strategy.StrategyFingerprint, strategy.EffectiveFromHourUtc, policy.SourceGeneration,
                        identity is not null && policy.HostIds.Contains(identity.HostId),
                        identity is not null && activeHosts.Contains(identity.HostId));
                    priorById.TryGetValue(id, out var previous);
                    var changed = previous is null || previous.ContractFingerprint != contractFingerprint;
                    var next = new SensorState
                    {
                        Eligible = eligible,
                        ContractFingerprint = contractFingerprint,
                        LastOutcomeAtUtc = previous?.LastOutcomeAtUtc,
                        NextAttemptAtUtc = changed ? null : previous?.NextAttemptAtUtc,
                        Status = previous?.Status ?? "waiting",
                        Reason = previous?.Reason ?? "not-probed"
                    };
                    if (!eligible)
                    {
                        if (proofNoticeCurrent && proofNotice is not null)
                            _ = stateStore.CompleteProofRefresh(proofNotice);
                        next.Status = "unavailable";
                        next.Reason = sensor is null ? "sensor-missing" : family is null ? "unsupported-category" :
                            ruleFingerprint is null ? "no-enabled-current-rule" : sensor.Paused || sensor.DevicePaused
                                ? "paused" : "identity-or-host-not-current";
                        next.NextAttemptAtUtc = now + IdleRescanDelay;
                        next.LastOutcomeAtUtc = now;
                    }
                    else
                    {
                        if (IsRawQualificationPending(binding))
                        {
                            // Qualification owns the bounded raw-evidence cadence. Do not issue
                            // profile transport requests until its durable proof is committed.
                            next.Status = "waiting";
                            next.Reason = "raw-qualification-pending";
                            next.NextAttemptAtUtc = null;
                            next.LastOutcomeAtUtc = now;
                            pageStates.Add((id, next));
                            continue;
                        }
                        if (!HasCurrentRawProof(id, binding, identity, authorityContextFingerprint, settings, policy))
                        {
                            next.Status = "waiting";
                            next.Reason = "raw-qualification-proof-not-current";
                            next.NextAttemptAtUtc = null;
                            next.LastOutcomeAtUtc = now;
                            pageStates.Add((id, next));
                            continue;
                        }
                        var resolution = profile is null ? null : PrtgTrustedSamplingProfileResolver.Resolve(profile,
                            identity, policy, id, sensor!.SensorType, strategy, now.UtcDateTime, now.UtcDateTime);
                        if (resolution?.Ready == true && profile!.SourceMetadataObservedAtUtc > now - FreshRefreshAfter &&
                            !ShouldHonorCooldown(changed, next.NextAttemptAtUtc, proofNoticeCurrent,
                                proofNotice?.NotBeforeUtc, now))
                        {
                            next.Status = "qualified";
                            next.Reason = "current-profile-fresh";
                            next.NextAttemptAtUtc = profile.SourceMetadataObservedAtUtc + FreshRefreshAfter;
                            next.LastOutcomeAtUtc ??= profile.SourceMetadataObservedAtUtc;
                            if (proofNoticeCurrent && proofNotice is not null)
                                _ = stateStore.CompleteProofRefresh(proofNotice);
                        }
                        else if (ShouldHonorCooldown(changed, next.NextAttemptAtUtc,
                            proofNoticeCurrent, proofNotice?.NotBeforeUtc, now))
                        {
                            // Current per-sensor contract is unchanged; preserve a finite cooldown.
                        }
                        else
                        {
                            next.Status = "waiting";
                            next.Reason = profile is null ? "profile-missing" : resolution?.RejectionReason ?? "profile-stale";
                            next.NextAttemptAtUtc = null;
                            candidates.Add(id);
                        }
                    }
                    pageStates.Add((id, next));
                }
                    SavePage(scopeFingerprint, pageIndex, pageStates);

                // A due sensor advances its fixed policy-order group into the next 23-hour window.
                // Only already-fresh, eligible neighbors with a current raw proof may join; pending
                // proof, failed cooldown, unavailable, and stale neighbors remain outside the request.
                var dueSet = candidates.ToHashSet();
                var freshProvenNeighbors = pageStates.Where(item => item.State.Eligible &&
                        item.State.Status == "qualified" && item.State.Reason == "current-profile-fresh")
                    .Where(item => identities.TryGetValue(item.Id, out var identity) &&
                        bindings.TryGetValue(item.Id, out var binding) &&
                        HasCurrentRawProof(item.Id, binding, identity, authorityContextFingerprint, settings, policy) &&
                        (!proofRefreshNotices.TryGetValue(item.Id, out var notice) ||
                         !IsCurrentProofRefreshNotice(notice, binding, identity, settings, policy) ||
                         notice.NotBeforeUtc <= now))
                    .Select(item => item.Id).ToHashSet();
                var refreshGroups = BuildRefreshGroups(pageIds, dueSet, freshProvenNeighbors);

                // Read-only eligibility evaluation and durable progress are safe without transport
                // admission. Require the joint snapshot/profile plan only when this page actually
                // contains source requests, so unavailable/unsupported sensors can still complete
                // a sweep and an already completed scope can honor its next-sweep deadline.
                PrtgCapacityAdmissionPlan? admission = null;
                var admissionReason = "admitted";
                if (candidates.Count > 0)
                {
                    var snapshotSelection = PrtgSnapshotTargetResolver.Resolve(backend, hosts, settings,
                        new SentinelStore(backend.Blob("sentinels")).GetAll());
                    if (!PrtgCapacityRuntimeAdmission.TryGetCurrent(backend, hosts, settings, snapshotSelection,
                        out admission, out admissionReason,
                        boundedProfileRefreshRecoveryGroupSize: refreshGroups.Count == 0
                            ? null : refreshGroups.Max(group => group.Length)))
                    {
                        Update(s => { s.LastOutcome = "waiting-capacity-admission";
                            s.LastWaitingReason = admissionReason; s.UpdatedAtUtc = DateTimeOffset.UtcNow; });
                        return;
                    }
                }

                var admittedGroups = admissionReason == PrtgCapacityRuntimeAdmission.BoundedProfileRefreshRecoveryReason
                    ? refreshGroups.Take(1) : refreshGroups;
                foreach (var group in admittedGroups)
                {
                    workCts.Token.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow >= deadline) break;
                    if (!RenewLease(version)) { workCts.Cancel(); throw new OperationCanceledException("profile refresh lease lost"); }
                    var requestCount = 0;
                    var requestAttempted = 0;
                    long admissionWaitTicks = 0;
                    var elapsed = Stopwatch.StartNew();
                    TimeSpan WorkElapsed() => TimeSpan.FromTicks(Math.Max(0,
                        elapsed.Elapsed.Ticks - Interlocked.Read(ref admissionWaitTicks)));
                    try
                    {
                        var rows = await new PrtgTrustedSamplingProbeService(backend).ProbeAsync(group, workCts.Token,
                            () => Interlocked.Increment(ref requestCount), profile =>
                                new PrtgTrustedSamplingProfileStore(backend)
                                    .RecordProbeResultUnderLease(profile, owner, version), owner, version,
                            allowUnrelatedPolicyRevisionChanges: true,
                            requestPurpose: PrtgRequestPurpose.ProfileRefresh,
                            admissionPlanFingerprint: admission!.Fingerprint,
                            onRequestAttempted: () => Interlocked.Increment(ref requestAttempted),
                            onTableBudgetAdmissionWait: duration => Interlocked.Add(ref admissionWaitTicks, duration.Ticks));
                        elapsed.Stop();
                        if (!RecordRuntimeTransportSample(group, ids, settings, policy, admission!, requestAttempted,
                            requestCount, elapsed.Elapsed, "success", null, WorkElapsed()))
                        {
                            DeferProofRefreshNotices(group, proofRefreshNotices, DateTimeOffset.UtcNow);
                            SaveSensorOutcomes(scopeFingerprint, pageIndex, pageStates, group.Select(id => (id, new SensorState
                            {
                                Eligible = true,
                                ContractFingerprint = pageStates.First(item => item.Id == id).State.ContractFingerprint,
                                Status = "failed", Reason = "request-shape-mismatch", LastOutcomeAtUtc = DateTimeOffset.UtcNow,
                                NextAttemptAtUtc = DateTimeOffset.UtcNow + ProbeCooldown
                            })), requestCount, elapsed.Elapsed.TotalSeconds, "failed", "request-shape-mismatch");
                            continue;
                        }
                        var expectedProfiles = group.Where(profiles.ContainsKey)
                            .ToDictionary(id => id, id => profiles[id]);
                        RecordProbeRows(rows, scopeFingerprint, pageIndex, pageStates, requestCount,
                            elapsed.Elapsed.TotalSeconds, expectedProfiles, owner, version);
                        UpdateProofRefreshNotices(rows, proofRefreshNotices, DateTimeOffset.UtcNow);
                    }
                    catch (OperationCanceledException ex) when (!ct.IsCancellationRequested &&
                        (workCts.IsCancellationRequested || ex.Message.Contains("profile-refresh-lease-lost", StringComparison.Ordinal)))
                    {
                        workCts.Cancel();
                        throw;
                    }
                    catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                    {
                        elapsed.Stop();
                        _ = RecordRuntimeTransportSample(group, ids, settings, policy, admission!, requestAttempted,
                            requestCount, elapsed.Elapsed, "timeout", SafeReason(ex), WorkElapsed());
                        DeferProofRefreshNotices(group, proofRefreshNotices, DateTimeOffset.UtcNow);
                        SaveSensorOutcomes(scopeFingerprint, pageIndex, pageStates, group.Select(id => (id, new SensorState
                        {
                            Eligible = true,
                            ContractFingerprint = pageStates.First(item => item.Id == id).State.ContractFingerprint,
                            Status = "failed", Reason = SafeReason(ex), LastOutcomeAtUtc = DateTimeOffset.UtcNow,
                            NextAttemptAtUtc = DateTimeOffset.UtcNow + ProbeCooldown
                        })), requestCount, elapsed.Elapsed.TotalSeconds, "failed", SafeReason(ex));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or PrtgClientException)
                    {
                        elapsed.Stop();
                        _ = RecordRuntimeTransportSample(group, ids, settings, policy, admission!, requestAttempted,
                            requestCount, elapsed.Elapsed, "failed", SafeReason(ex), WorkElapsed());
                        DeferProofRefreshNotices(group, proofRefreshNotices, DateTimeOffset.UtcNow);
                        SaveSensorOutcomes(scopeFingerprint, pageIndex, pageStates, group.Select(id => (id, new SensorState
                        {
                            Eligible = true,
                            ContractFingerprint = pageStates.First(item => item.Id == id).State.ContractFingerprint,
                            Status = ex is InvalidOperationException ? "unavailable" : "failed",
                            Reason = SafeReason(ex),
                            LastOutcomeAtUtc = DateTimeOffset.UtcNow,
                            NextAttemptAtUtc = DateTimeOffset.UtcNow + ProbeCooldown
                        })), requestCount, elapsed.Elapsed.TotalSeconds,
                            ex is InvalidOperationException ? "unavailable" : "failed", SafeReason(ex));
                    }
                }

                if (workCts.IsCancellationRequested) throw new OperationCanceledException(workCts.Token);
                if (DateTime.UtcNow >= deadline)
                {
                    // Keep the page cursor until all groups are handled. Successful profiles and cooldowns
                    // make completed groups cheap to skip when this same page is resumed.
                    Update(s => { s.LastOutcome = "slice-time-limit"; s.UpdatedAtUtc = DateTimeOffset.UtcNow; });
                    return;
                }
                Update(s => { s.Cursor = pageStart + pageIds.Length; s.UpdatedAtUtc = DateTimeOffset.UtcNow; });
                var latestSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
                var latestPolicy = policyStore.Get();
                var latestStrategyName = PrtgFetchStrategy.Normalize(latestSettings.PrtgFetchStrategy);
                var latestStrategyMinutes = PrtgFetchStrategy.Profile(latestStrategyName).SnapshotIntervalMinutes;
                var latestStrategy = new PrtgTrustedSamplingStrategyStateStore(backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey))
                    .GetCurrent(latestPolicy, latestStrategyName, latestStrategyMinutes, DateTime.UtcNow);
                var latestIds = latestPolicy.SensorIds.Count > MaxSelectedSensors ? Array.Empty<long>() :
                    latestPolicy.SensorIds.Where(id => id > 0).Distinct().Order().Take(MaxSelectedSensors + 1).ToArray();
                var latestScopeFingerprint = latestIds.Length > MaxSelectedSensors ? "scope-limit-exceeded" :
                    ScopeFingerprint(latestPolicy, latestSettings, latestStrategy,
                        PrtgResourceCurrentRuleCatalog.Load(backend), latestIds);
                if (scopeSnapshot.Version != hosts.CapturePrtgSnapshot().Version || scopeFingerprint != latestScopeFingerprint)
                {
                    Update(s => { s.Cursor = 0; s.NextSweepAtUtc = DateTimeOffset.UtcNow; s.LastOutcome = "scope-or-rule-fence-changed";
                        s.LastWaitingReason = "selection-host-source-or-rule-changed"; s.UpdatedAtUtc = DateTimeOffset.UtcNow; });
                    return;
                }
                if (pageStart + pageIds.Length >= ids.Length) { CompleteSweep(ids); return; }
            }
        }
        finally
        {
            workCts.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) { }
            ReleaseLease(version);
        }
    }

    private bool RecordRuntimeTransportSample(IReadOnlyCollection<long> group, IReadOnlyList<long> selectedIds,
        SystemSettings settings, PrtgMonitoringPolicy policy, PrtgCapacityAdmissionPlan admission,
        int attempted, int sent, TimeSpan elapsed, string outcome, string? failureCode,
        TimeSpan? nonAdmissionElapsed = null)
    {
        var selected = selectedIds.ToHashSet();
        var sampledIds = group.Distinct().Order().ToArray();
        if (sampledIds.Length is < 1 or > PrtgProfileTransportCapacityPilot.MaximumSensorIds ||
            group.Count != sampledIds.Length || sampledIds.Any(id => !selected.Contains(id))) return false;
        try
        {
            var contract = PrtgProfileTransportCapacityPilot.BuildContract(backend, hosts, settings, policy, sampledIds);
            if (contract.ScopeFingerprint != admission.ProfileScopeFingerprint ||
                contract.SourceFingerprint != admission.SourceFingerprint ||
                contract.StrategyFingerprint != admission.StrategyFingerprint) return false;
            var validSuccess = outcome == "success";
            var expectedRequests = PrtgProfileTransportCapacityEvaluator.ExpectedRequests(sampledIds.Length);
            if (outcome == "success" && (attempted != expectedRequests || sent != expectedRequests))
            {
                validSuccess = false;
                outcome = "failed";
                failureCode = "request-shape-mismatch";
            }
            var store = new PrtgProfileTransportCapacityStore(backend.Blob(PrtgProfileTransportCapacityStore.BlobKey));
            store.Record(new PrtgProfileTransportSample(contract.SourceFingerprint, contract.ScopeFingerprint,
                contract.StrategyFingerprint, contract.RequestShapeFingerprint, DateTimeOffset.UtcNow,
                Math.Max(0, (long)Math.Ceiling(elapsed.TotalMilliseconds)), sampledIds.Length,
                attempted, sent, outcome, failureCode,
                contract.VersionFingerprint, nonAdmissionElapsed is { } work
                    ? Math.Max(0, (long)Math.Ceiling(work.TotalMilliseconds)) : null));
            return validSuccess;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            logger.LogWarning(ex, "Could not record bounded runtime profile transport evidence");
            return false;
        }
    }

    public PrtgTrustedSamplingProfileRefreshProgress ReadProgress()
    {
        var state = ReadState();
        var ids = CurrentSelectedIds();
        var scopeCurrent = ids.Length <= MaxSelectedSensors && CurrentScopeFingerprint(ids) == state.ScopeFingerprint;
        return new(state.StartedAtUtc, state.UpdatedAtUtc, state.CompletedAtUtc, state.Cursor,
            ids.Length, scopeCurrent ? state.EligibleSensors : 0, scopeCurrent ? state.Qualified : 0,
            scopeCurrent ? state.Unavailable : 0, scopeCurrent ? state.Failed : 0, scopeCurrent ? state.Waiting : 0,
            scopeCurrent ? state.RawRequestCount : 0, scopeCurrent ? state.ObservedRequestSeconds : 0,
            scopeCurrent ? state.LastOutcome : "scope-rescan-pending", scopeCurrent ? state.LastWaitingReason : "selection-or-contract-changed",
            state.LeaseUntilUtc > DateTimeOffset.UtcNow, state.LeaseUntilUtc, state.NextSweepAtUtc);
    }

    public PrtgTrustedSamplingProfileRefreshSensorPage ReadSensorProgressPage(int offset, int limit)
    {
        if (offset < 0 || offset > MaxSelectedSensors || limit is < 1 or > PageSize)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var state = ReadState();
        var ids = CurrentSelectedIds();
        var currentScope = ids.Length <= MaxSelectedSensors ? CurrentScopeFingerprint(ids) : "scope-limit-exceeded";
        var selected = ids.Skip(offset).Take(limit).ToArray();
        var rows = new List<PrtgTrustedSamplingProfileRefreshSensorRow>(selected.Length);
        foreach (var group in selected.Select((id, localOffset) => (id, absoluteOffset: offset + localOffset))
                     .GroupBy(item => item.absoluteOffset / PageSize))
        {
            var page = currentScope == state.ScopeFingerprint
                ? stateStore.ReadPage(currentScope, group.Key)
                : new PrtgTrustedSamplingProfileRefreshStateStore.SensorPage();
            var byId = page.Sensors.ToDictionary(row => row.SensorObjid);
            foreach (var item in group)
            {
                var sensor = byId.GetValueOrDefault(item.id);
                rows.Add(sensor is null
                    ? new(item.id, false, "waiting", "not-yet-visited", null, null)
                    : new(sensor.SensorObjid, sensor.Eligible, sensor.Status, sensor.Reason,
                        sensor.LastOutcomeAtUtc, sensor.NextAttemptAtUtc));
            }
        }
        var next = offset + selected.Length < ids.Length ? offset + selected.Length : (int?)null;
        return new(ids.Length, offset, limit, next, rows);
    }

    private async Task RenewLeaseLoopAsync(long version, CancellationTokenSource workCts)
    {
        try
        {
            while (!workCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), workCts.Token);
                if (!RenewLease(version)) { workCts.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (workCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PRTG trusted profile refresh lease renewal failed");
            workCts.Cancel();
        }
    }

    private void RecordProbeRows(IReadOnlyList<PrtgTrustedSamplingProbeRow> rows, string scope, int pageIndex,
        List<(long Id, SensorState State)> pageStates, int requestCount, double elapsedSeconds,
        IReadOnlyDictionary<long, PrtgTrustedSamplingProfile> expectedProfiles,
        string leaseOwner, long leaseVersion)
    {
        // A returned waiting row is a completed source observation. Unlike timeout, transport
        // exceptions, caller cancellation, or lease loss, it can establish that the prior source
        // authority facts are no longer present. Revoke only the captured profile via the SQL
        // digest/identity/binding/lease CAS; API probes never enter this hosted-worker path.
        RevokeCompletedWaitingProfiles(backend, rows, expectedProfiles, leaseOwner, leaseVersion);

        var profiles = new PrtgTrustedSamplingProfileStore(backend).GetMany(rows.Select(row => row.SensorObjid));
        var nextRows = rows.Select(row =>
        {
            profiles.TryGetValue(row.SensorObjid, out var profile);
            var status = row.ProfileRecorded ? "qualified" : "waiting";
            var reason = row.ProfileRecorded ? "profile-refreshed" : string.Join(",", row.MissingAuthorityFields.Take(6));
            var cooldown = row.ProfileRecorded && profile is not null
                ? profile.SourceMetadataObservedAtUtc + FreshRefreshAfter
                : DateTimeOffset.UtcNow + ProbeCooldown;
            var prior = pageStates.First(item => item.Id == row.SensorObjid).State;
            return (row.SensorObjid, new SensorState
            {
                Eligible = true, ContractFingerprint = prior.ContractFingerprint, Status = status,
                Reason = reason, LastOutcomeAtUtc = row.ProbedAtUtc, NextAttemptAtUtc = cooldown
            });
        }).ToArray();
        SaveSensorOutcomes(scope, pageIndex, pageStates, nextRows, requestCount, elapsedSeconds,
            nextRows.All(row => row.Item2.Status == "qualified") ? "qualified" : "waiting-for-source-authority",
            nextRows.FirstOrDefault(row => row.Item2.Status != "qualified").Item2?.Reason);
    }

    /// <summary>Shared invocation seam for completed scheduled refresh rows only.</summary>
    internal static void RevokeCompletedWaitingProfiles(StorageBackend backend,
        IReadOnlyList<PrtgTrustedSamplingProbeRow> rows,
        IReadOnlyDictionary<long, PrtgTrustedSamplingProfile> expectedProfiles,
        string leaseOwner, long leaseVersion)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(expectedProfiles);
        foreach (var row in rows)
        {
            if (!expectedProfiles.TryGetValue(row.SensorObjid, out var expectedProfile)) continue;
            _ = RevokeCompletedWaitingProfile(backend, row, expectedProfile, leaseOwner, leaseVersion);
        }
    }

    internal static bool RevokeCompletedWaitingProfile(StorageBackend backend,
        PrtgTrustedSamplingProbeRow? row, PrtgTrustedSamplingProfile expectedProfile,
        string leaseOwner, long leaseVersion)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(expectedProfile);
        if (row is null || row.ProfileRecorded || row.Status != "waiting" ||
            row.MissingAuthorityFields.Count == 0 || row.SensorObjid != expectedProfile.SensorObjid)
            return false;
        return new PrtgTrustedSamplingProfileStore(backend).RevokeAfterObservedRefreshFailure(
            expectedProfile, leaseOwner, leaseVersion);
    }

    private State PrepareScope(string fingerprint, int count) => Update(s =>
    {
        if (s.ScopeFingerprint != fingerprint)
        {
            if (!string.IsNullOrEmpty(s.ScopeFingerprint))
            {
                s.PreviousScopeFingerprints.Remove(fingerprint);
                s.PreviousScopeFingerprints.Insert(0, s.ScopeFingerprint);
                s.PreviousScopeFingerprints = s.PreviousScopeFingerprints.Distinct().Take(4).ToList();
            }
            s.ScopeFingerprint = fingerprint; s.Cursor = 0; s.SelectedSensors = count;
            s.EligibleSensors = s.Qualified = s.Unavailable = s.Failed = s.Waiting = 0;
            s.RawRequestCount = 0; s.ObservedRequestSeconds = 0;
            s.StartedAtUtc = DateTimeOffset.UtcNow; s.CompletedAtUtc = null; s.NextSweepAtUtc = null;
        }
        return s;
    });

    private void CompleteSweep(IReadOnlyCollection<long> currentSelectedSensorIds)
    {
        var current = ReadState();
        var earliest = stateStore.EarliestNextAttempt(current.ScopeFingerprint, (current.SelectedSensors + PageSize - 1) / PageSize);
        var proofRefreshDue = stateStore.EarliestPendingProofRefresh(currentSelectedSensorIds);
        if (proofRefreshDue.HasValue && (earliest is null || proofRefreshDue < earliest)) earliest = proofRefreshDue;
        Update(s =>
        {
            var now = DateTimeOffset.UtcNow;
            var due = earliest ?? now + IdleRescanDelay;
            s.Cursor = 0; s.CompletedAtUtc = now; s.NextSweepAtUtc = due > now ? due : now + TimeSpan.FromMinutes(1);
            s.PreviousScopeFingerprints.Clear();
            s.LastOutcome = "full-scope-traversal-complete"; s.UpdatedAtUtc = now;
        });
    }

    internal static bool IsCurrentProofRefreshNotice(
        PrtgTrustedSamplingProofRefreshRequest notice,
        PrtgTrustedSamplingBinding? binding, PrtgResourceIdentity? identity, SystemSettings settings,
        PrtgMonitoringPolicy policy)
    {
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var authorityContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy,
            strategyName, PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes);
        return binding is not null && identity is { Active: true, PendingReconciliation: false } &&
            settings.PrtgEnabled && policy.Ready(settings.PrtgUrl) && policy.SensorIds.Contains(notice.SensorObjid) &&
            policy.HostIds.Contains(identity.HostId) &&
            settings.Revision == notice.SettingsRevision && policy.Revision == notice.PolicyRevision &&
            authorityContext == notice.AuthorityContextFingerprint &&
            identity.SensorId == notice.SensorObjid && identity.Epoch == notice.IdentityEpoch &&
            identity.Generation == notice.ResourceGeneration && identity.SourceGeneration == notice.SourceGeneration &&
            identity.SourceGeneration == policy.SourceGeneration && identity.ChannelGeneration == notice.ChannelGeneration &&
            identity.ChannelFingerprint == notice.BindingFingerprint &&
            binding.SensorObjid == notice.SensorObjid && binding.BindingRevision == notice.BindingRevision &&
            binding.BindingFingerprint == notice.BindingFingerprint &&
            binding.QualificationProofReference == notice.QualificationProofReference &&
            binding.TimeBasisEvidenceReference == policy.TimeBasisEvidenceReference &&
            binding.RawTimestampTimeZoneId == policy.RawTimestampTimeZoneId &&
            binding.AnalysisTimeZoneId == policy.AnalysisTimeZoneId &&
            binding.Matches(notice.SensorObjid, authorityContext, policy.SourceGeneration,
                identity.Generation, identity.Epoch, identity.ChannelGeneration);
    }

    internal static bool ShouldHonorCooldown(bool contractChanged, DateTimeOffset? nextAttemptAtUtc,
        bool proofNoticeCurrent, DateTimeOffset? proofNoticeNotBeforeUtc, DateTimeOffset nowUtc) =>
        !contractChanged && nextAttemptAtUtc > nowUtc &&
        !(proofNoticeCurrent && proofNoticeNotBeforeUtc <= nowUtc);

    internal static bool IsRawQualificationPending(PrtgTrustedSamplingBinding? binding) =>
        binding is null || string.IsNullOrWhiteSpace(binding.QualificationProofReference);

    internal static IReadOnlyList<long[]> BuildRefreshGroups(IReadOnlyList<long> pageIds,
        IReadOnlyCollection<long> dueCandidateIds, IReadOnlyCollection<long> freshProvenEligibleIds)
    {
        if (pageIds.Count > PageSize || pageIds.Any(id => id <= 0) || pageIds.Distinct().Count() != pageIds.Count)
            throw new ArgumentException("Profile refresh page IDs must be unique positive IDs within one bounded page.", nameof(pageIds));
        var pageSet = pageIds.ToHashSet();
        var due = dueCandidateIds.ToHashSet();
        var fresh = freshProvenEligibleIds.ToHashSet();
        if (due.Any(id => !pageSet.Contains(id)) || fresh.Any(id => !pageSet.Contains(id)))
            throw new ArgumentException("Refresh candidates and neighbors must belong to the current page.");

        return pageIds.Chunk(PrtgTrustedSamplingProbeService.MaxSensorIds)
            .Where(group => group.Any(due.Contains))
            .Select(group => group.Where(id => due.Contains(id) || fresh.Contains(id)).ToArray())
            .Where(group => group.Length > 0)
            .ToArray();
    }

    internal static bool HasCurrentRawProof(long sensorObjid, PrtgTrustedSamplingBinding? binding,
        PrtgResourceIdentity? identity, string authorityContextFingerprint, SystemSettings settings,
        PrtgMonitoringPolicy policy) => binding is not null &&
        !string.IsNullOrWhiteSpace(binding.QualificationProofReference) &&
        identity is { Active: true, PendingReconciliation: false } && settings.PrtgEnabled &&
        policy.Ready(settings.PrtgUrl) && policy.SensorIds.Contains(sensorObjid) &&
        policy.HostIds.Contains(identity.HostId) && identity.SensorId == sensorObjid &&
        identity.SourceGeneration == policy.SourceGeneration && identity.ChannelFingerprint == binding.BindingFingerprint &&
        binding.SensorObjid == sensorObjid &&
        binding.TimeBasisEvidenceReference == policy.TimeBasisEvidenceReference &&
        binding.RawTimestampTimeZoneId == policy.RawTimestampTimeZoneId &&
        binding.AnalysisTimeZoneId == policy.AnalysisTimeZoneId &&
        binding.Matches(sensorObjid, authorityContextFingerprint, policy.SourceGeneration,
            identity.Generation, identity.Epoch, identity.ChannelGeneration);

    private void UpdateProofRefreshNotices(IReadOnlyList<PrtgTrustedSamplingProbeRow> rows,
        IReadOnlyDictionary<long, PrtgTrustedSamplingProofRefreshRequest> notices,
        DateTimeOffset now)
    {
        foreach (var row in rows)
        {
            if (!notices.TryGetValue(row.SensorObjid, out var notice)) continue;
            if (row.ProfileRecorded) _ = stateStore.CompleteProofRefresh(notice);
            else _ = stateStore.DeferProofRefresh(notice, now + ProbeCooldown);
        }
    }

    private void DeferProofRefreshNotices(IEnumerable<long> sensorIds,
        IReadOnlyDictionary<long, PrtgTrustedSamplingProofRefreshRequest> notices,
        DateTimeOffset now)
    {
        foreach (var sensorId in sensorIds.Distinct())
            if (notices.TryGetValue(sensorId, out var notice))
                _ = stateStore.DeferProofRefresh(notice, now + ProbeCooldown);
    }

    private static PrtgResourceFamily? FamilyForCategory(string? category) => category?.ToLowerInvariant() switch
    {
        PrtgSensorCategories.Cpu => PrtgResourceFamily.Cpu,
        PrtgSensorCategories.Memory => PrtgResourceFamily.Memory,
        PrtgSensorCategories.Disk => PrtgResourceFamily.Disk,
        _ => null
    };

    private static string ScopeFingerprint(PrtgMonitoringPolicy policy, SystemSettings settings,
        PrtgTrustedSamplingStrategyContext strategy, PrtgResourceCurrentRuleCatalog catalog, IEnumerable<long> ids)
    {
        var rules = string.Join("|", catalog.For(PrtgResourceFamily.Cpu)?.Fingerprint ?? "disabled",
            catalog.For(PrtgResourceFamily.Memory)?.Fingerprint ?? "disabled",
            catalog.AdmissionFingerprintFor(PrtgResourceFamily.Disk));
        var authority = string.Join("|", policy.SourceGeneration, policy.EndpointHint, policy.SourceTimeZoneId,
            policy.SourceCultureName, policy.RawTimestampTimeZoneId, policy.AnalysisTimeZoneId,
            policy.TimeBasisEvidenceReference, strategy.StrategyFingerprint,
            strategy.EffectiveFromHourUtc.ToString("O", CultureInfo.InvariantCulture), rules,
            settings.PrtgUrl, settings.PrtgFetchStrategy, string.Join(",", ids));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authority)));
    }

    private static string SensorContractFingerprint(PrtgResourcePressureSensorMetadata? sensor,
        PrtgResourceIdentity? identity, string? ruleFingerprint, string strategyFingerprint,
        DateTime strategyEffectiveFromHourUtc, string sourceGeneration,
        bool hostSelected, bool hostActive)
    {
        var material = string.Join("|", sourceGeneration, sensor?.Category, sensor?.SensorType,
            sensor?.DeviceObjid, sensor?.Paused, sensor?.DevicePaused, identity?.HostId, identity?.DeviceId,
            identity?.SourceGeneration, identity?.Generation, identity?.Epoch, identity?.ChannelGeneration,
            identity?.Active, identity?.PendingReconciliation, hostSelected, hostActive, ruleFingerprint,
            strategyFingerprint, strategyEffectiveFromHourUtc.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private long[] CurrentSelectedIds()
    {
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        return policy.SensorIds.Where(id => id > 0).Distinct().Order().Take(MaxSelectedSensors + 1).ToArray();
    }

    private string CurrentScopeFingerprint(long[] ids)
    {
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey))
            .GetCurrent(policy, strategyName, PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes, DateTime.UtcNow);
        return ScopeFingerprint(policy, settings, strategy, PrtgResourceCurrentRuleCatalog.Load(backend), ids);
    }

    private State ReadState()
    {
        var overview = stateStore.ReadOverview();
        return new State
        {
            ScopeFingerprint = overview.ScopeFingerprint, PreviousScopeFingerprints = overview.PreviousScopeFingerprints,
            Cursor = overview.Cursor, SelectedSensors = overview.SelectedSensors, EligibleSensors = overview.EligibleSensors,
            Qualified = overview.Qualified, Unavailable = overview.Unavailable, Failed = overview.Failed, Waiting = overview.Waiting,
            RawRequestCount = overview.RawRequestCount, ObservedRequestSeconds = overview.ObservedRequestSeconds,
            LastOutcome = overview.LastOutcome, LastWaitingReason = overview.LastWaitingReason,
            StartedAtUtc = overview.StartedAtUtc, UpdatedAtUtc = overview.UpdatedAtUtc,
            CompletedAtUtc = overview.CompletedAtUtc, NextSweepAtUtc = overview.NextSweepAtUtc,
            LeaseOwner = overview.LeaseOwner, LeaseVersion = overview.LeaseVersion, LeaseUntilUtc = overview.LeaseUntilUtc
        };
    }

    private T Update<T>(Func<State, T> mutate) => stateStore.MutateOverview(owner, leaseVersion, overview =>
    {
        var state = new State
        {
            ScopeFingerprint = overview.ScopeFingerprint, PreviousScopeFingerprints = overview.PreviousScopeFingerprints,
            Cursor = overview.Cursor, SelectedSensors = overview.SelectedSensors, EligibleSensors = overview.EligibleSensors,
            Qualified = overview.Qualified, Unavailable = overview.Unavailable, Failed = overview.Failed, Waiting = overview.Waiting,
            RawRequestCount = overview.RawRequestCount, ObservedRequestSeconds = overview.ObservedRequestSeconds,
            LastOutcome = overview.LastOutcome, LastWaitingReason = overview.LastWaitingReason,
            StartedAtUtc = overview.StartedAtUtc, UpdatedAtUtc = overview.UpdatedAtUtc,
            CompletedAtUtc = overview.CompletedAtUtc, NextSweepAtUtc = overview.NextSweepAtUtc,
            LeaseOwner = overview.LeaseOwner, LeaseVersion = overview.LeaseVersion, LeaseUntilUtc = overview.LeaseUntilUtc
        };
        var result = mutate(state);
        overview.ScopeFingerprint = state.ScopeFingerprint; overview.PreviousScopeFingerprints = state.PreviousScopeFingerprints;
        overview.Cursor = state.Cursor; overview.SelectedSensors = state.SelectedSensors; overview.EligibleSensors = state.EligibleSensors;
        overview.Qualified = state.Qualified; overview.Unavailable = state.Unavailable; overview.Failed = state.Failed; overview.Waiting = state.Waiting;
        overview.RawRequestCount = state.RawRequestCount; overview.ObservedRequestSeconds = state.ObservedRequestSeconds;
        overview.LastOutcome = state.LastOutcome; overview.LastWaitingReason = state.LastWaitingReason;
        overview.StartedAtUtc = state.StartedAtUtc; overview.UpdatedAtUtc = DateTimeOffset.UtcNow;
        overview.CompletedAtUtc = state.CompletedAtUtc; overview.NextSweepAtUtc = state.NextSweepAtUtc;
        return result;
    });

    private void Update(Action<State> mutate) => Update(s => { mutate(s); return true; });

    private PrtgTrustedSamplingProfileRefreshStateStore.SensorPage ReadPageWithMigration(State state, string scope, int pageIndex)
    {
        var page = stateStore.ReadPage(scope, pageIndex);
        if (page.Sensors.Count != 0) return page;
        var ids = CurrentSelectedIds().Skip(pageIndex * PageSize).Take(PageSize).ToHashSet();
        foreach (var oldScope in state.PreviousScopeFingerprints)
        {
            var old = stateStore.ReadPage(oldScope, pageIndex);
            page.Sensors.AddRange(old.Sensors.Where(row => ids.Contains(row.SensorObjid)));
            if (page.Sensors.Count != 0) break;
        }
        return page;
    }

    private void SavePage(string scope, int pageIndex, IEnumerable<(long Id, SensorState State)> rows)
    {
        var mapped = rows.Select(item => new PrtgTrustedSamplingProfileRefreshStateStore.SensorState
        {
            SensorObjid = item.Id, Eligible = item.State.Eligible, ContractFingerprint = item.State.ContractFingerprint,
            NextAttemptAtUtc = item.State.NextAttemptAtUtc, Status = item.State.Status, Reason = item.State.Reason,
            LastOutcomeAtUtc = item.State.LastOutcomeAtUtc
        }).ToArray();
        stateStore.SavePage(scope, pageIndex, mapped, owner, leaseVersion);
    }

    private void SaveSensorOutcomes(string scope, int pageIndex, List<(long Id, SensorState State)> pageStates,
        IEnumerable<(long Id, SensorState State)> outcomes, int requestCount, double requestSeconds,
        string outcome, string? reason)
    {
        var map = pageStates.ToDictionary(row => row.Id, row => row.State);
        foreach (var (id, state) in outcomes) map[id] = state;
        var mapped = map.Select(pair => new PrtgTrustedSamplingProfileRefreshStateStore.SensorState
        {
            SensorObjid = pair.Key, Eligible = pair.Value.Eligible, ContractFingerprint = pair.Value.ContractFingerprint,
            NextAttemptAtUtc = pair.Value.NextAttemptAtUtc, Status = pair.Value.Status, Reason = pair.Value.Reason,
            LastOutcomeAtUtc = pair.Value.LastOutcomeAtUtc
        }).ToArray();
        stateStore.SavePage(scope, pageIndex, mapped, owner, leaseVersion, requestCount, requestSeconds, outcome, reason);
        pageStates.Clear();
        pageStates.AddRange(map.Select(pair => (pair.Key, pair.Value)));
    }

    private bool TryAcquireLease(DateTimeOffset now, out long acquiredVersion)
    {
        return stateStore.TryAcquire(owner, now, LeaseDuration, out acquiredVersion);
    }

    private bool RenewLease(long version) => stateStore.Renew(owner, version, DateTimeOffset.UtcNow + LeaseDuration);

    private void ReleaseLease(long version) => stateStore.Release(owner, version);

    private static string SafeReason(Exception ex) => ex switch
    {
        PrtgClientException => "source-unavailable",
        InvalidDataException => "source-shape-or-authority-invalid",
        OperationCanceledException => "probe-deadline-exceeded",
        _ => "probe-fence-or-scope-changed"
    };
}
