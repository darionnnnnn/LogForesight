using System.Diagnostics;
using System.Text.Json;
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
        long tableRequestsSent = 0;
        long requestAttempts = 0;
        var priorTableRequestSent = client.TableRequestSent;
        client.TableRequestSent = () =>
        {
            priorTableRequestSent?.Invoke();
            Interlocked.Increment(ref tableRequestsSent);
        };
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
        var modeFailure = false;
        var measuredMode = selection.PrimaryChannelCapabilityMode;
        var sampleBatches = SelectPilotBatches(ids, selection);
        try
        {
            foreach (var batch in sampleBatches)
            {
                if (deadline.IsCancellationRequested) { dto.Reason = "pilot_deadline_reached"; break; }
                if (!reservationStore.Renew(planFingerprint, owner, leaseVersion, DateTimeOffset.UtcNow.AddSeconds(90)))
                { dto.Reason = "capacity_plan_reservation_superseded"; scopeChanged = true; break; }
                var startedAt = Stopwatch.GetTimestamp();
                long sentAt = 0;
                var outcome = "failed";
                string? json = null;
                try
                {
                    var query = "api/table.json?content=sensors&columns=" + PrtgSnapshotTargetResolver.SnapshotColumns
                        + PrtgResourceGuardProbe.BuildObjidFilter(batch);
                    Interlocked.Increment(ref requestAttempts);
                    json = await client.GetBoundedJsonAsync(query, MaximumResponseBytes, deadline.Token,
                        () => Interlocked.CompareExchange(ref sentAt, Stopwatch.GetTimestamp(), 0));
                    PrtgSnapshotCapacityResponseValidator.Validate(json, batch);
                    outcome = "success";
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                { outcome = "timeout"; }
                catch { outcome = "failed"; }
                var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                if (outcome != "success" || json is null)
                {
                    var failedSelection = measuredMode == PrtgSnapshotTargetResolver.PrimaryChannelModeUnknown
                        ? selection : PrtgSnapshotTargetResolver.WithPrimaryChannelMode(selection, measuredMode);
                    if (sentAt != 0)
                        new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Record(
                            MakeSample(failedSelection, DateTimeOffset.UtcNow, batch.Length, outcome, elapsed));
                    break;
                }

                var candidatesInBatch = selection.PotentialQualifiedSensorObjids.Where(batch.Contains).ToArray();
                var fieldCount = CountPrimaryChannelFields(json, candidatesInBatch.Length > 0 ? candidatesInBatch : batch);
                var observedMode = fieldCount == (candidatesInBatch.Length > 0 ? candidatesInBatch.Length : batch.Length)
                    ? PrtgSnapshotTargetResolver.PrimaryChannelModeProvided
                    : fieldCount == 0 ? PrtgSnapshotTargetResolver.PrimaryChannelModeFallback
                    : PrtgSnapshotTargetResolver.PrimaryChannelModeUnknown;
                if (observedMode == PrtgSnapshotTargetResolver.PrimaryChannelModeUnknown ||
                    measuredMode != PrtgSnapshotTargetResolver.PrimaryChannelModeUnknown && measuredMode != observedMode)
                {
                    if (measuredMode == PrtgSnapshotTargetResolver.PrimaryChannelModeProvided)
                    {
                        var providedSelection = PrtgSnapshotTargetResolver.WithPrimaryChannelMode(selection,
                            PrtgSnapshotTargetResolver.PrimaryChannelModeProvided);
                        new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Record(
                            MakeSample(providedSelection, DateTimeOffset.UtcNow, batch.Length, "failed", elapsed));
                    }
                    modeFailure = true;
                    dto.Reason = "primarychannel_mode_mixed_or_changed";
                    break;
                }
                measuredMode = observedMode;
                var modeSelection = PrtgSnapshotTargetResolver.WithPrimaryChannelMode(selection, measuredMode);
                long fallbackGroupElapsed = 0;
                var fallbackGroupRequests = 0;
                var fallbackGroupSize = 0;
                if (measuredMode == PrtgSnapshotTargetResolver.PrimaryChannelModeFallback &&
                    selection.PotentialQualifiedSensorCount > 0)
                {
                    var fallbackCandidates = candidatesInBatch.Take(selection.PotentialQualifiedFallbackSampleSize).ToArray();
                    if (fallbackCandidates.Length != selection.PotentialQualifiedFallbackSampleSize)
                    {
                        dto.Reason = "no-full-batch-for-qualified-fallback-pilot";
                        break;
                    }
                    try
                    {
                        var fallback = await PrtgSnapshotPrimaryChannelFallback.ReadGroupAsync(client,
                            fallbackCandidates, deadline.Token, () => Interlocked.Increment(ref requestAttempts));
                        var currentBindings = new PrtgTrustedSamplingBindingStore(backend).GetMany(fallbackCandidates);
                        foreach (var pair in fallback.Brackets)
                        {
                            if (!currentBindings.TryGetValue(pair.Key, out var currentBinding) ||
                                !string.Equals(pair.Value.BeforeId, currentBinding.ChannelObjectId, StringComparison.Ordinal) ||
                                !string.Equals(pair.Value.AfterId, currentBinding.ChannelObjectId, StringComparison.Ordinal))
                                throw new InvalidDataException("primarychannel-fallback-binding-mismatch");
                        }
                        fallbackGroupElapsed = fallback.ElapsedMilliseconds;
                        fallbackGroupRequests = fallback.RequestsSent;
                        fallbackGroupSize = fallbackCandidates.Length;
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Record(
                            MakeSample(modeSelection, DateTimeOffset.UtcNow, batch.Length, "timeout", elapsed));
                        dto.Reason = "pilot_deadline_reached";
                        break;
                    }
                    catch (Exception ex) when (ex is PrtgClientException or InvalidDataException or JsonException)
                    {
                        new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Record(
                            MakeSample(modeSelection, DateTimeOffset.UtcNow, batch.Length, "failed", elapsed));
                        dto.Reason = "fallback_group_observation_incomplete";
                        break;
                    }
                }
                var currentSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
                var currentPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                var currentSelection = PrtgSnapshotTargetResolver.CreateSelection(ids,
                    selection.ActiveMappedDeviceCount, backend, currentSettings, currentPolicy);
                if (currentSelection.ScopeFingerprint != selection.ScopeFingerprint ||
                    currentSelection.EndpointFingerprint != selection.EndpointFingerprint ||
                    currentSelection.PotentialQualifiedSensorObjids.SequenceEqual(selection.PotentialQualifiedSensorObjids) is false ||
                    currentSelection.PotentialQualifiedContractFingerprint != selection.PotentialQualifiedContractFingerprint ||
                    !reservationStore.Renew(planFingerprint, owner, leaseVersion, DateTimeOffset.UtcNow.AddSeconds(90)))
                {
                    dto.Reason = "scope_source_or_capacity_plan_changed_during_pilot";
                    scopeChanged = true;
                    break;
                }
                var store = new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
                store.Record(MakeSample(modeSelection, DateTimeOffset.UtcNow, batch.Length, outcome, elapsed));
                if (fallbackGroupSize > 0)
                    store.Record(new PrtgSnapshotCapacitySample(selection.ScopeFingerprint,
                        selection.EndpointFingerprint, modeSelection.RequestShapeFingerprint, DateTimeOffset.UtcNow,
                        0, fallbackGroupSize, "success", measuredMode, fallbackGroupSize,
                        fallbackGroupRequests, fallbackGroupElapsed, "fallback-group"));
                if (measuredMode == PrtgSnapshotTargetResolver.PrimaryChannelModeFallback &&
                    selection.PotentialQualifiedSensorCount > 0) break;
                if (outcome != "success") break;
            }
        }
        finally
        {
            dto.RequestsSent = checked((int)Math.Min(int.MaxValue, Volatile.Read(ref tableRequestsSent)));
            dto.RequestsAttempted = checked((int)Math.Min(int.MaxValue, Volatile.Read(ref requestAttempts)));
            reservationStore.Release(planFingerprint, owner, leaseVersion);
        }

        var finalSelection = PrtgSnapshotTargetResolver.CreateSelection(ids, selection.ActiveMappedDeviceCount,
            backend, new SystemSettingsStore(backend.Blob("system_settings")).Get(),
            new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get());
        var evaluationSelection = !scopeChanged && !modeFailure &&
            finalSelection.PrimaryChannelCapabilityMode == PrtgSnapshotTargetResolver.PrimaryChannelModeUnknown &&
            measuredMode != PrtgSnapshotTargetResolver.PrimaryChannelModeUnknown
            ? PrtgSnapshotTargetResolver.WithPrimaryChannelMode(finalSelection, measuredMode)
            : finalSelection;
        dto.MaximumRequestsPerRun = MaximumRequestsPerRun(evaluationSelection);
        var evaluation = PrtgSnapshotCapacityEvaluator.Evaluate(ids.Count, strategy,
            evaluationSelection.ScopeFingerprint, evaluationSelection.EndpointFingerprint, evaluationSelection.RequestShapeFingerprint,
            new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Read(),
            DateTimeOffset.UtcNow, evaluationSelection.PrimaryChannelCapabilityMode,
            evaluationSelection.PotentialQualifiedSensorCount, evaluationSelection.PotentialQualifiedBatchCount,
            evaluationSelection.PotentialQualifiedFallbackSampleSize);
        dto.Status = ToStatus(evaluation.Status);
        dto.Reason = evaluation.Reason;
        dto.MatchingFullBatchSamples = evaluation.FreshMatchingFullBatchSamples;
        dto.PrimaryChannelCapabilityMode = evaluationSelection.PrimaryChannelCapabilityMode;
        dto.ExpectedTableRequests = evaluationSelection.ExpectedTableRequests;
        dto.PotentialQualifiedSensors = evaluationSelection.PotentialQualifiedSensorCount;
        dto.PotentialFallbackGroups = evaluationSelection.PotentialQualifiedBatchCount;
        dto.MatchingFallbackGroupSamples = evaluation.FreshFallbackGroupSamples;
        dto.RequiredFallbackGroupSamples = evaluationSelection.PotentialQualifiedSensorCount > 0 &&
            evaluationSelection.PrimaryChannelCapabilityMode == PrtgSnapshotTargetResolver.PrimaryChannelModeFallback
            ? PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples : 0;
        dto.P95FallbackGroupSeconds = evaluation.P95FallbackGroupSeconds;
        dto.P95BatchSeconds = evaluation.P95BatchSeconds;
        dto.EstimatedSeconds = evaluation.EstimatedSeconds;
        dto.CompletionWindowSeconds = evaluation.CompletionWindowSeconds;
        if (scopeChanged)
        {
            dto.Status = "capacity-unverified";
            dto.Reason = "scope_source_or_capacity_plan_changed_during_pilot";
            return dto;
        }
        if (modeFailure)
        {
            dto.Status = "capacity-unverified";
            dto.Reason = "primarychannel_mode_mixed_or_changed";
            return dto;
        }
        if (dto.RequestsAttempted < dto.MaximumRequestsPerRun &&
            dto.MatchingFullBatchSamples < PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples)
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

    private static IReadOnlyList<long[]> SelectPilotBatches(IReadOnlyList<long> ids,
        PrtgSnapshotTargetSelection selection)
    {
        // A fallback contract needs one complete, real bracket group per invocation so each
        // bounded pilot stays within the established 60-second deadline. Repeating the pilot
        // accrues the five required independent observations over multiple invocations.
        if (selection.PrimaryChannelCapabilityMode == PrtgSnapshotTargetResolver.PrimaryChannelModeFallback &&
            selection.PotentialQualifiedSensorCount > 0)
        {
            return ids.Chunk(PrtgSnapshotCapacityEvaluator.BatchSize)
                .Select(batch => batch.ToArray())
                .Where(batch => selection.PotentialQualifiedSensorObjids.Count(batch.Contains) >=
                    selection.PotentialQualifiedFallbackSampleSize)
                .Take(1).ToArray();
        }

        if (selection.PrimaryChannelCapabilityMode == PrtgSnapshotTargetResolver.PrimaryChannelModeUnknown &&
            selection.PotentialQualifiedSensorCount > 0)
        {
            var batches = ids.Chunk(PrtgSnapshotCapacityEvaluator.BatchSize).Select(batch => batch.ToArray()).ToArray();
            var best = batches.OrderByDescending(batch => selection.PotentialQualifiedSensorObjids.Count(batch.Contains))
                .ThenBy(batch => batch[0]).FirstOrDefault();
            if (best is null) return Array.Empty<long[]>();
            var rest = BuildPilotBatches(ids).Where(batch => !batch.SequenceEqual(best)).Take(MaximumRequests - 1);
            return new[] { best }.Concat(rest).ToArray();
        }

        // Unknown mode must first collect bounded live observations. Keep the established
        // five-request provided-mode pilot; if the source is actually fallback, the first
        // invocation records only one complete bounded fallback group and a retry accrues more.
        return BuildPilotBatches(ids);
    }

    internal static int CountPrimaryChannelFields(string json, IReadOnlyCollection<long> expectedIds)
    {
        using var document = JsonDocument.Parse(json,
            new JsonDocumentOptions { MaxDepth = PrtgSnapshotResponseContract.MaximumDepth });
        if (!document.RootElement.TryGetProperty("sensors", out var sensors) ||
            sensors.ValueKind != JsonValueKind.Array) return -1;
        var expected = expectedIds.ToHashSet();
        var seen = new HashSet<long>();
        var count = 0;
        foreach (var row in sensors.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("objid", out var idElement) ||
                !TryReadSnapshotSensorId(idElement, out var id)) return -1;
            if (!expected.Contains(id)) continue;
            if (!seen.Add(id)) return -1;
            if (row.EnumerateObject().Any(property =>
                property.Name.Equals(PrtgTrustedSnapshotParser.NativePrimaryChannelIdField, StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals(PrtgTrustedSnapshotParser.NativePrimaryChannelIdRawField, StringComparison.OrdinalIgnoreCase)))
                count++;
        }
        return seen.Count == expected.Count ? count : -1;
    }

    private static bool TryReadSnapshotSensorId(JsonElement element, out long id)
    {
        id = 0;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetInt64(out id);
        return element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    private static int MaximumRequestsPerRun(PrtgSnapshotTargetSelection selection) =>
        selection.PrimaryChannelCapabilityMode == PrtgSnapshotTargetResolver.PrimaryChannelModeFallback &&
        selection.PotentialQualifiedSensorCount > 0
            ? 2 + 2 * selection.PotentialQualifiedFallbackSampleSize
            : Math.Max(MaximumRequests, 2 + 2 * selection.PotentialQualifiedFallbackSampleSize);

    private static PrtgSnapshotCapacitySample MakeSample(PrtgSnapshotTargetSelection selection,
        DateTimeOffset completedAt, int requestedCount, string outcome, double elapsedMilliseconds) =>
        new(selection.ScopeFingerprint, selection.EndpointFingerprint, selection.RequestShapeFingerprint,
            completedAt, Math.Max(0, (long)Math.Ceiling(elapsedMilliseconds)), requestedCount, outcome,
            selection.PrimaryChannelCapabilityMode);

    private static double Window(string strategy) =>
        strategy == PrtgFetchStrategy.Aggressive ? 180 : 600;

    private static string ToStatus(PrtgSnapshotCapacityStatus status) => status switch
    {
        PrtgSnapshotCapacityStatus.CapacityQualified => "capacity-qualified",
        PrtgSnapshotCapacityStatus.CapacityExceeded => "capacity-exceeded",
        _ => "capacity-unverified"
    };
}
