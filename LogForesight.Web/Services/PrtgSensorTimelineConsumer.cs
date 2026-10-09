using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Core;
using LogForesight.Core.Models;
using NLog;

namespace LogForesight.Web.Services;

internal sealed record PrtgSensorTimelineTick(TimeSpan Delay, bool DidWork, string Outcome);

/// <summary>只沿確認政策中的sensor ID分頁讀取目前可服務工作；每頁最多100顆、每批最多50顆。</summary>
internal sealed class PrtgSensorTimelineConsumer(
    ISystemSettingsStore settingsStore,
    StorageBackend backend,
    Func<bool> hasPriorityWork,
    Func<SystemSettings, PrtgClient>? clientFactory = null,
    PrtgSamplingActivity? samplingActivity = null,
    TimeProvider? timeProvider = null)
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const int InventoryPageSize = 100;
    private const int MaximumInventoryPagesPerTick = 5;
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PriorityRecheck = TimeSpan.FromSeconds(5);
    private readonly Func<SystemSettings, PrtgClient> _clientFactory = clientFactory ?? (s => PrtgClientFactory.Create(s));
    private readonly PrtgSamplingActivity _sampling = samplingActivity ?? PrtgSamplingActivity.Shared;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    internal async Task<PrtgSensorTimelineTick> TickAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tickStartedAt = _time.GetUtcNow();
        var progressStore = new PrtgSensorTimelineProgressStore(backend.Blob(PrtgSensorTimelineProgressStore.BlobKey));
        if (hasPriorityWork()) return Complete("deferred-priority", PriorityRecheck, false);

        var settings = settingsStore.Get();
        var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var policy = policyStore.Get();
        if (!settings.PrtgEnabled || !PrtgClientFactory.HasUsableCredentials(settings) || !policy.Ready(settings.PrtgUrl))
            return Complete("monitoring-not-ready", TimeSpan.FromMinutes(1), false);

        var ids = policy.SensorIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length == 0 || ids.Length > PrtgSensorTimelineEvidence.MaximumBootstrapSensors)
        {
            var reason = ids.Length == 0 ? "no-selected-sensors" : "timeline-bootstrap-capacity-shortfall";
            LogOutcome(progressStore, reason, tickStartedAt);
            return new(IdlePoll, false, reason);
        }

        var progress = progressStore.Get();
        var scopeFingerprint = PrtgSensorTimelineProgressStore.ScopeFingerprint(policy, settings);
        progress = EnsureCycle(progressStore, progress, policy, ids, scopeFingerprint, tickStartedAt);
        var cycleId = progress.BootstrapCycleId;
        var cycleDeadline = progress.BootstrapCycleOutcome == "running"
            ? progress.BootstrapCycleDeadlineAtUtc : null;
        if (progress.BootstrapCycleOutcome == "running" && cycleDeadline is { } deadline && tickStartedAt >= deadline)
        {
            progress = progressStore.Update(state =>
            {
                if (state.BootstrapCycleId == cycleId && state.BootstrapCycleOutcome == "running")
                { state.BootstrapCycleOutcome = "deadline-exceeded"; state.BootstrapCycleReason = "bootstrap-deadline-exceeded"; }
            });
            return Complete("bootstrap-deadline-exceeded", IdlePoll, false);
        }
        if (progress.BootstrapCycleOutcome == "deadline-exceeded")
            return Complete("bootstrap-deadline-exceeded-awaiting-explicit-resume", IdlePoll, false);
        var rotated = ids.Where(id => id > progress.LastServedSensorId)
            .Concat(ids.Where(id => id <= progress.LastServedSensorId)).ToArray();
        DateTimeOffset? earliestEligible = null;
        var inspected = 0;
        var store = backend.PrtgStore();
        var mapFrom = DateTime.Today.AddDays(-30);
        var mapTo = DateTime.Today.AddDays(1);

        foreach (var idPage in rotated.Chunk(InventoryPageSize).Take(MaximumInventoryPagesPerTick))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hasPriorityWork()) return Complete("deferred-priority", PriorityRecheck, false);
            inspected += idPage.Length;

            // 所有鏡像查詢只接受當頁ID；缺列／重複列時整頁拒絕，絕不退回全站鏡像。
            var mappingRevision = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
            var sensorDetails = store.GetReadinessSensorDetailsByIds(idPage);
            if (sensorDetails.Count != idPage.Length || sensorDetails.Select(s => s.Objid).Distinct().Count() != idPage.Length)
                throw new InvalidOperationException("timeline-selected-sensor-page-incomplete");
            var deviceIds = sensorDetails.Select(s => s.DeviceObjid).Distinct().ToArray();
            var mapRows = store.GetReadinessMaps(deviceIds, mapFrom, mapTo);
            if (backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() != mappingRevision)
                return Complete("mapping-changed-during-selection", PriorityRecheck, false);
            var hostByDevice = LatestUniqueMappings(mapRows, policy.HostIds.ToHashSet());
            var work = sensorDetails.Where(sensor => hostByDevice.ContainsKey(sensor.DeviceObjid))
                .Select(sensor => new PrtgTimelineWorkItem(sensor.Objid, hostByDevice[sensor.DeviceObjid]))
                .ToArray();
            var now = _time.GetUtcNow();
            var due = PrtgSensorTimelineWorkSelector.Select(work, progress.LastServedSensorId, now,
                PrtgResourceGuardProbe.MaxBatchSize, ReadTimeline, out var nextEligible);
            if (nextEligible is { } eligible && (earliestEligible == null || eligible < earliestEligible))
                earliestEligible = eligible;

            if (due.Count == 0)
            {
                progress = AdvanceCursor(progressStore, idPage[^1], ids[^1]);
                continue;
            }

            if (cycleDeadline is { } batchDeadline && _time.GetUtcNow() >= batchDeadline)
            {
                MarkCycleExpired(progressStore, cycleId);
                return Complete("bootstrap-deadline-exceeded", IdlePoll, false);
            }
            if (hasPriorityWork() || !_sampling.TryBeginBackground(out var admission))
                return Complete("deferred-priority", PriorityRecheck, false);
            using var admissionLease = admission;

            progress = progressStore.Update(state =>
            {
                state.LastRoundStartedAt = tickStartedAt;
                state.LastRoundOutcome = "running";
                if (state.BootstrapCycleId == cycleId && state.BootstrapCycleOutcome == "not-started")
                    state.BootstrapCycleOutcome = "running";
                var previousCursor = state.LastServedSensorId;
                state.LastServedSensorId = due[^1].SensorId;
                if (ids.Length == 1 || previousCursor >= ids[^1] && state.LastServedSensorId < ids[^1])
                    state.BootstrapCycleSweepPassedEnd = true;
            });
            try
            {
                using var deadlineCts = cycleDeadline is { } dueAt
                    ? new CancellationTokenSource(Max(TimeSpan.Zero, dueAt - _time.GetUtcNow()), _time)
                    : null;
                using var cycleToken = deadlineCts is null
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
                using (var client = _clientFactory(settings))
                {
                    var evidence = await new PrtgSensorTimelineCollector(backend, client)
                        .CollectBatchAsync(due, policy, cycleToken.Token, admission, mappingRevision,
                            cycleId, progress.BootstrapCycleAsOfUtc);
                    var yielded = admission.WasPreempted || evidence.Any(e => e.QualityReason == "yielded-to-sampling");
                    foreach (var row in evidence.Where(e => e.BootstrapStatus == "capacity-shortfall"))
                        Log.Warn("PRTG timeline bootstrap容量未驗證 sensor={SensorId}; watermark={Watermark}; reason={Reason}",
                            row.SensorId, row.LastCompleteThrough, row.QualityReason);
                    if (cycleDeadline is { } expires && _time.GetUtcNow() >= expires)
                    {
                        MarkCycleExpired(progressStore, cycleId);
                        return Complete("bootstrap-deadline-exceeded", IdlePoll, true);
                    }
                    var outcome = await TryCompleteCycleAsync(progressStore, ids, policy, cycleId,
                        cycleToken.Token, cycleDeadline) ? "initial-collection-complete-readiness-separate" : "bounded-batch";
                    return Complete(yielded ? "yielded-to-sampling" : outcome,
                        yielded ? PriorityRecheck : TimeSpan.Zero, true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (cycleDeadline is { } expires && _time.GetUtcNow() >= expires)
            {
                MarkCycleExpired(progressStore, cycleId);
                return Complete("bootstrap-deadline-exceeded", IdlePoll, true);
            }
            catch (OperationCanceledException) when (admission.WasPreempted)
            {
                return Complete("yielded-to-sampling", PriorityRecheck, true);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PRTG timeline 背景批次失敗；保留已提交水位 firstSensor={SensorId}", due[0].SensorId);
                return Complete("batch-failed", PriorityRecheck, true);
            }
        }

        if (inspected < rotated.Length) return Complete("bounded-inventory-page", TimeSpan.Zero, false);
        if (await TryCompleteCycleAsync(progressStore, ids, policy, cycleId, cancellationToken, cycleDeadline))
            return Complete("initial-collection-complete-readiness-separate", IdlePoll, false);
        var delay = earliestEligible is { } next && next > _time.GetUtcNow()
            ? Min(IdlePoll, next - _time.GetUtcNow()) : IdlePoll;
        return Complete("idle-or-retry-wait", delay, false);

        PrtgSensorTimelineTick Complete(string outcome, TimeSpan delay, bool didWork)
        {
            LogOutcome(progressStore, outcome, tickStartedAt);
            return new(delay, didWork, outcome);
        }
    }

    private PrtgSensorTimelineProgress EnsureCycle(PrtgSensorTimelineProgressStore store,
        PrtgSensorTimelineProgress current, PrtgMonitoringPolicy policy, long[] selectedIds,
        string scopeFingerprint, DateTimeOffset now)
    {
        if (current.BootstrapCycleId.Length > 0 && current.BootstrapScopeFingerprint == scopeFingerprint)
            return current;
        return store.Update(state =>
        {
            if (state.BootstrapCycleId.Length > 0 && state.BootstrapScopeFingerprint == scopeFingerprint) return;
            if (state.BootstrapCycleId.Length > 0)
                state.PreviousBootstrapCycle = new(state.BootstrapCycleId, state.BootstrapSourceGeneration,
                    state.BootstrapScopeFingerprint, state.BootstrapCycleStartedAtUtc,
                    state.BootstrapCycleDeadlineAtUtc, state.BootstrapCycleOutcome);
            state.BootstrapCycleId = Guid.NewGuid().ToString("N");
            state.BootstrapSourceGeneration = policy.SourceGeneration;
            state.BootstrapScopeFingerprint = scopeFingerprint;
            state.BootstrapCycleStartedAtUtc = now;
            state.BootstrapCycleAsOfUtc = now;
            state.BootstrapCycleDeadlineAtUtc = now.AddHours(Math.Clamp(state.BootstrapDeadlineHours,
                PrtgSensorTimelineProgress.MinimumBootstrapDeadlineHours,
                PrtgSensorTimelineProgress.MaximumBootstrapDeadlineHours));
            state.BootstrapCycleSelectedSensors = selectedIds.Length;
            state.BootstrapCycleStartSensorId = state.LastServedSensorId;
            state.BootstrapCycleSweepEndSensorId = selectedIds.Where(id => id <= state.LastServedSensorId)
                .DefaultIfEmpty(selectedIds[^1]).Last();
            state.BootstrapCycleSweepPassedEnd = state.LastServedSensorId < selectedIds[0] ||
                state.LastServedSensorId >= selectedIds[^1];
            state.BootstrapCycleOutcome = "running";
            state.BootstrapCycleReason = "bootstrap-running";
        });
    }

    private PrtgSensorTimelineProgress AdvanceCursor(PrtgSensorTimelineProgressStore store, long cursor,
        long maximumSelectedId) =>
        store.Update(state =>
        {
            var previousCursor = state.LastServedSensorId;
            state.LastServedSensorId = cursor;
            if (previousCursor >= maximumSelectedId && cursor < maximumSelectedId)
                state.BootstrapCycleSweepPassedEnd = true;
        });

    private async Task<bool> TryCompleteCycleAsync(PrtgSensorTimelineProgressStore store, long[] ids,
        PrtgMonitoringPolicy policy, string cycleId, CancellationToken cancellationToken,
        DateTimeOffset? deadline)
    {
        var current = store.Get();
        if (current.BootstrapCycleId != cycleId || current.BootstrapCycleOutcome != "running" ||
            !current.BootstrapCycleSweepPassedEnd || current.LastServedSensorId < current.BootstrapCycleSweepEndSensorId)
            return false;
        if (deadline is { } end && _time.GetUtcNow() >= end)
        { MarkCycleExpired(store, cycleId); return false; }
        var sweep = await AllSelectedCapturedAsync(ids, policy, cycleId,
            current.BootstrapCycleAsOfUtc ?? _time.GetUtcNow(), cancellationToken);
        if (!sweep.Captured)
        {
            store.Update(state =>
            {
                if (state.BootstrapCycleId == cycleId && state.BootstrapCycleOutcome == "running")
                    state.BootstrapCycleSweepPassedEnd = false;
            });
            return false;
        }
        if (deadline is { } expires && _time.GetUtcNow() >= expires)
        { MarkCycleExpired(store, cycleId); return false; }
        return store.TryUpdateExpected(cycleId, "running", current.BootstrapSettingsRevision,
            sweep.FenceVersions.GetValueOrDefault(PrtgMonitoringPolicyStore.BlobKey),
            sweep.FenceVersions.GetValueOrDefault("system_settings"),
            sweep.FenceVersions.GetValueOrDefault(EfPrtgStore.ScopeRevisionBlobKey), sweep.FenceVersions,
            state =>
            {
                state.BootstrapCycleOutcome = "initial-collection-complete";
                state.BootstrapCycleReason = "all-selected-current-identities-captured";
            },
            state => state.BootstrapCycleId == cycleId && state.BootstrapCycleOutcome == "running" &&
                state.BootstrapScopeFingerprint == current.BootstrapScopeFingerprint &&
                state.BootstrapCycleSweepPassedEnd &&
                state.LastServedSensorId >= current.BootstrapCycleSweepEndSensorId);
    }

    private async Task<CycleSweepResult> AllSelectedCapturedAsync(long[] ids, PrtgMonitoringPolicy policy,
        string cycleId, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var store = backend.PrtgStore();
        var expectedVersions = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [PrtgMonitoringPolicyStore.BlobKey] = backend.Blob(PrtgMonitoringPolicyStore.BlobKey).ReadVersion(),
            ["system_settings"] = backend.Blob("system_settings").ReadVersion(),
            [EfPrtgStore.ScopeRevisionBlobKey] = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion(),
            [EfPrtgStore.HostMapDataRevisionBlobKey] = backend.Blob(EfPrtgStore.HostMapDataRevisionBlobKey).ReadVersion(),
            [EfPrtgStore.CatalogueDataRevisionBlobKey] = backend.Blob(EfPrtgStore.CatalogueDataRevisionBlobKey).ReadVersion()
        };
        var authorizationPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        if (!settings.PrtgEnabled || authorizationPolicy.SourceGeneration != policy.SourceGeneration ||
            !authorizationPolicy.SensorIds.Order().SequenceEqual(ids) ||
            authorizationPolicy.EndpointHint != EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl) ||
            PrtgSensorTimelineProgressStore.ScopeFingerprint(authorizationPolicy, settings) !=
                PrtgSensorTimelineProgressStore.ScopeFingerprint(policy, settings)) return CycleSweepResult.NotCaptured;
        foreach (var page in ids.Chunk(PrtgTimelineMetadataQuery.MaximumPageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sensorDetails = store.GetReadinessSensorDetailsByIds(page);
            if (sensorDetails.Count != page.Length || sensorDetails.Select(sensor => sensor.Objid).Distinct().Count() != page.Length)
                return CycleSweepResult.NotCaptured;
            var deviceIds = sensorDetails.Select(sensor => sensor.DeviceObjid).Distinct().ToArray();
            var mapRows = store.GetReadinessMaps(deviceIds, DateTime.Today.AddDays(-30), DateTime.Today.AddDays(1));
            var hostByDevice = LatestUniqueMappings(mapRows, policy.HostIds.ToHashSet());
            var metadata = await new PrtgTimelineMetadataQuery(backend.CreateContext)
                .ReadAsync(page, PrtgSensorTimelineStore.Prefix, cancellationToken).ConfigureAwait(false);
            if (!metadata.StableVersions || page.Any(id =>
                !metadata.Rows.TryGetValue(id, out var row) || row.Malformed ||
                row.BootstrapCollectionCycleId != cycleId || row.SourceGeneration != policy.SourceGeneration ||
                row.PendingNextPage != 0 || row.LastCompleteThrough is null || row.LastCompleteThrough < asOf ||
                row.HostId is null || !policy.HostIds.Contains(row.HostId.Value) ||
                row.EffectiveScopeFingerprint != policy.EffectiveSensorScope(id, row.HostId.Value) ||
                !sensorDetails.Any(sensor => sensor.Objid == id && hostByDevice.TryGetValue(sensor.DeviceObjid, out var hostId) && hostId == row.HostId)))
                return CycleSweepResult.NotCaptured;
            var identities = store.GetResourceIdentities(page);
            if (page.Any(id => !metadata.Rows.TryGetValue(id, out var row) ||
                !identities.TryGetValue(id, out var identity) || !identity.Active || identity.PendingReconciliation ||
                identity.Generation != row.ResourceGeneration || identity.Epoch != row.IdentityEpoch ||
                identity.ChannelGeneration != row.ChannelGeneration || identity.HostId != row.HostId ||
                identity.SourceGeneration != row.SourceGeneration)) return CycleSweepResult.NotCaptured;
            foreach (var id in page)
            {
                expectedVersions[PrtgSensorTimelineStore.Prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    metadata.Rows[id].Version;
                var identityKey = PrtgResourceIdentityStore.Prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                expectedVersions[identityKey] = backend.Blob(identityKey).ReadVersion();
            }
            var identitiesAfterVersionCapture = store.GetResourceIdentities(page);
            if (page.Any(id => !identitiesAfterVersionCapture.TryGetValue(id, out var after) ||
                !identities.TryGetValue(id, out var before) || !SameIdentity(before, after)))
                return CycleSweepResult.NotCaptured;
        }
        var currentPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var currentSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var currentProgress = new PrtgSensorTimelineProgressStore(backend.Blob(PrtgSensorTimelineProgressStore.BlobKey)).Get();
        if (currentProgress.BootstrapCycleId != cycleId || currentProgress.BootstrapCycleOutcome != "running" ||
            currentPolicy.SourceGeneration != policy.SourceGeneration || !currentSettings.PrtgEnabled ||
            currentPolicy.EndpointHint != EfPrtgObservationStore.SourceHintFor(currentSettings.PrtgUrl) ||
            !currentPolicy.SensorIds.Order().SequenceEqual(ids) ||
            PrtgSensorTimelineProgressStore.ScopeFingerprint(currentPolicy, currentSettings) !=
                PrtgSensorTimelineProgressStore.ScopeFingerprint(policy, settings))
            return CycleSweepResult.NotCaptured;
        return new(true, expectedVersions);
    }

    private sealed record CycleSweepResult(bool Captured, IReadOnlyDictionary<string, long> FenceVersions)
    {
        public static CycleSweepResult NotCaptured { get; } = new(false, new Dictionary<string, long>());
    }

    private static bool SameIdentity(PrtgResourceIdentity left, PrtgResourceIdentity right) =>
        left.SensorId == right.SensorId && left.Epoch == right.Epoch && left.Generation == right.Generation &&
        left.SourceGeneration == right.SourceGeneration && left.DeviceId == right.DeviceId && left.HostId == right.HostId &&
        left.ResourceFingerprint == right.ResourceFingerprint && left.InventoryFingerprint == right.InventoryFingerprint &&
        left.ChannelFingerprint == right.ChannelFingerprint && left.ChannelGeneration == right.ChannelGeneration &&
        left.Active == right.Active && left.PendingReconciliation == right.PendingReconciliation &&
        left.ChangedAtUtc == right.ChangedAtUtc;

    private void MarkCycleExpired(PrtgSensorTimelineProgressStore store, string cycleId) =>
        store.Update(state =>
        {
            if (state.BootstrapCycleId == cycleId && state.BootstrapCycleOutcome == "running")
            { state.BootstrapCycleOutcome = "deadline-exceeded"; state.BootstrapCycleReason = "bootstrap-deadline-exceeded"; }
        });

    private static Dictionary<long, long> LatestUniqueMappings(
        IReadOnlyCollection<PrtgHostMapRow> rows, IReadOnlySet<long> allowedHosts)
    {
        var result = new Dictionary<long, long>();
        foreach (var deviceRows in rows.GroupBy(row => row.DeviceObjid))
        {
            var latestDate = deviceRows.Max(row => row.MapDate.Date);
            var latest = deviceRows.Where(row => row.MapDate.Date == latestDate).ToArray();
            if (latest.Length == 1 && latest[0].MapStatus == PrtgMapStatus.Ok && latest[0].HostId is { } hostId &&
                allowedHosts.Contains(hostId))
                result[deviceRows.Key] = hostId;
        }
        return result;
    }

    private PrtgSensorTimelineEvidence ReadTimeline(long sensorId) =>
        new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + sensorId)).Get();

    private void LogOutcome(PrtgSensorTimelineProgressStore progressStore, string outcome, DateTimeOffset tickStartedAt) =>
        progressStore.Update(state =>
        {
            state.LastRoundStartedAt = tickStartedAt;
            state.LastRoundCompletedAt = _time.GetUtcNow();
            state.LastRoundOutcome = outcome;
        });

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;
}
