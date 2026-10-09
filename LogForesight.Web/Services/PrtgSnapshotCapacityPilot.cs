using System.Diagnostics;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

public sealed class PrtgSnapshotCapacityPilot(StorageBackend backend, Func<SystemSettings, PrtgClient>? clientFactory = null)
{
    public const int MaximumRequests = 5;
    public static readonly TimeSpan PilotDeadline = TimeSpan.FromSeconds(60);
    public const int MaximumResponseBytes = 512 * 1024;

    public async Task<PrtgSnapshotCapacityPilotDto> RunAsync(IHostStore hosts, SystemSettings settings,
        IReadOnlyCollection<Sentinel> sentinels, CancellationToken cancellationToken)
    {
        var selection = PrtgSnapshotTargetResolver.Resolve(backend, hosts, settings, sentinels);
        var ids = selection.SensorObjids;
        var strategy = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var requests = BuildPilotBatches(ids);
        var dto = new PrtgSnapshotCapacityPilotDto
        {
            TargetCount = ids.Count,
            CapacitySampleBatchSize = selection.CapacitySampleBatchSize,
            UniqueSensorsSampled = requests.SelectMany(x => x).Distinct().Count(),
            RequestShape = $"table.json sensors; columns={PrtgSnapshotTargetResolver.SnapshotColumns}; sorted {selection.CapacitySampleBatchSize}-ID filter; response cap 512 KiB",
            Coverage = ids.Count >= MaximumRequests * PrtgSnapshotCapacityEvaluator.BatchSize
                ? "five_disjoint_full_batches_spread_across_scope"
                : ids.Count >= PrtgSnapshotCapacityEvaluator.BatchSize
                    ? "repeated_or_spread_distinct_full_batches_with_scope_coverage_limited_by_target_count"
                    : "repeated_exact_selected_scope_requests"
        };

        if (ids.Count == 0)
        {
            dto.Status = "capacity-qualified";
            dto.Reason = "empty_scope";
            dto.CompletionWindowSeconds = Window(strategy);
            return dto;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PilotDeadline);
        using var client = clientFactory?.Invoke(settings) ?? PrtgClientFactory.Create(settings);
        client.RequestPurpose = PrtgRequestPurpose.CapacityPilot;
        var planFingerprint = PrtgSnapshotCapacityStore.Fingerprint(string.Join("\n",
            selection.ScopeFingerprint, selection.EndpointFingerprint, strategy,
            selection.RequestShapeFingerprint, typeof(PrtgSnapshotCapacityPilot).Assembly.GetName().Version?.ToString() ?? "unknown"));
        var reservationStore = new PrtgCapacityReservationStore(backend.Blob(PrtgCapacityReservationStore.BlobKey));
        var owner = Guid.NewGuid().ToString("N");
        if (!reservationStore.TryAcquire(planFingerprint, owner, DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(90), out var leaseVersion))
        {
            dto.Status = "capacity-unverified";
            dto.Reason = "capacity_plan_already_running";
            return dto;
        }
        var scopeChanged = false;
        try
        {
            foreach (var batch in requests)
            {
                if (deadline.IsCancellationRequested) { dto.Reason = "pilot_deadline_reached"; break; }
                if (!reservationStore.Renew(planFingerprint, owner, leaseVersion, DateTimeOffset.UtcNow.AddSeconds(90)))
                { dto.Reason = "capacity_plan_reservation_superseded"; scopeChanged = true; break; }
                dto.RequestsAttempted++;
                var startedAt = Stopwatch.GetTimestamp();
                long sentAt = 0;
                var outcome = "failed";
                try
                {
                    var query = "api/table.json?content=sensors&columns=" + PrtgSnapshotTargetResolver.SnapshotColumns
                        + PrtgResourceGuardProbe.BuildObjidFilter(batch);
                    var json = await client.GetBoundedJsonAsync(query, MaximumResponseBytes, deadline.Token,
                        () => Interlocked.CompareExchange(ref sentAt, Stopwatch.GetTimestamp(), 0));
                    PrtgSnapshotCapacityResponseValidator.Validate(json, batch);
                    outcome = "success";
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                { outcome = "timeout"; }
                catch { outcome = "failed"; }
                if (sentAt != 0) dto.RequestsSent++;
                var currentSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
                var currentPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                var currentSelection = PrtgSnapshotTargetResolver.CreateSelection(ids,
                    selection.ActiveMappedDeviceCount, backend, currentSettings, currentPolicy);
                if (currentSelection.ScopeFingerprint != selection.ScopeFingerprint ||
                    currentSelection.EndpointFingerprint != selection.EndpointFingerprint ||
                    currentSelection.RequestShapeFingerprint != selection.RequestShapeFingerprint ||
                    !reservationStore.Renew(planFingerprint, owner, leaseVersion, DateTimeOffset.UtcNow.AddSeconds(90)))
                {
                    dto.Reason = "scope_source_or_capacity_plan_changed_during_pilot";
                    scopeChanged = true;
                    break;
                }
                var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Record(
                    new PrtgSnapshotCapacitySample(selection.ScopeFingerprint, selection.EndpointFingerprint,
                        selection.RequestShapeFingerprint, DateTimeOffset.UtcNow,
                        (long)Math.Ceiling(Math.Max(0, elapsed)), batch.Distinct().Count(), outcome));
                if (outcome != "success") break;
            }
        }
        finally { reservationStore.Release(planFingerprint, owner, leaseVersion); }

        var evaluation = PrtgSnapshotCapacityEvaluator.Evaluate(ids.Count, strategy,
            selection.ScopeFingerprint, selection.EndpointFingerprint, selection.RequestShapeFingerprint,
            new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Read(),
            DateTimeOffset.UtcNow);
        dto.Status = ToStatus(evaluation.Status);
        dto.Reason = evaluation.Reason;
        dto.MatchingFullBatchSamples = evaluation.FreshMatchingFullBatchSamples;
        dto.P95BatchSeconds = evaluation.P95BatchSeconds;
        dto.EstimatedSeconds = evaluation.EstimatedSeconds;
        dto.CompletionWindowSeconds = evaluation.CompletionWindowSeconds;
        if (scopeChanged)
        {
            dto.Status = "capacity-unverified";
            dto.Reason = "scope_source_or_capacity_plan_changed_during_pilot";
            return dto;
        }
        if (dto.RequestsSent < MaximumRequests && dto.MatchingFullBatchSamples < PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples)
        {
            dto.Status = "capacity-unverified";
            dto.Reason = "pilot_incomplete_or_budget_wait_exceeded";
        }
        return dto;
    }

    public static List<long[]> BuildPilotBatches(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return [];
        var sampleSize = Math.Min(ids.Count, PrtgSnapshotCapacityEvaluator.BatchSize);
        var chunks = ids.Chunk(PrtgSnapshotCapacityEvaluator.BatchSize)
            .Where(x => x.Length == sampleSize).Select(x => x.ToArray()).ToArray();
        var batches = new List<long[]>(MaximumRequests);
        for (var i = 0; i < MaximumRequests; i++)
        {
            var index = chunks.Length == 0 ? 0 : chunks.Length >= MaximumRequests
                ? (int)Math.Round(i * (chunks.Length - 1) / (double)(MaximumRequests - 1))
                : i % chunks.Length;
            batches.Add(chunks[index]);
        }
        return batches;
    }

    private static double Window(string strategy) =>
        strategy == PrtgFetchStrategy.Aggressive ? 180 : 600;

    private static string ToStatus(PrtgSnapshotCapacityStatus status) => status switch
    {
        PrtgSnapshotCapacityStatus.CapacityQualified => "capacity-qualified",
        PrtgSnapshotCapacityStatus.CapacityExceeded => "capacity-exceeded",
        _ => "capacity-unverified"
    };
}
