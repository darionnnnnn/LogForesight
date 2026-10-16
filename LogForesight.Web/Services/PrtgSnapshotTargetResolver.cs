using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

public sealed record PrtgSnapshotTargetSelection(
    IReadOnlyList<long> SensorObjids,
    int ActiveMappedDeviceCount,
    int CapacitySampleBatchSize,
    string ScopeFingerprint,
    string EndpointFingerprint,
    string RequestShapeFingerprint,
    IReadOnlyList<long> PotentialQualifiedSensorObjids,
    int PotentialQualifiedSensorCount,
    int PotentialQualifiedBatchCount,
    int PotentialQualifiedFallbackSampleSize,
    string PotentialQualifiedContractFingerprint,
    string PrimaryChannelCapabilityMode)
{
    public long ExpectedTableRequests => PrimaryChannelCapabilityMode switch
    {
        PrtgSnapshotTargetResolver.PrimaryChannelModeProvided =>
            (SensorObjids.Count + PrtgSnapshotCapacityEvaluator.BatchSize - 1) / PrtgSnapshotCapacityEvaluator.BatchSize,
        _ => (SensorObjids.Count + PrtgSnapshotCapacityEvaluator.BatchSize - 1) / PrtgSnapshotCapacityEvaluator.BatchSize +
             2L * PotentialQualifiedSensorCount + PotentialQualifiedBatchCount
    };
}

/// <summary>One target and evidence fingerprint implementation shared by snapshot runtime, estimate, and pilot.</summary>
public static class PrtgSnapshotTargetResolver
{
    /// <summary>每個 filter_objid 快照請求最多帶的 objid 數。</summary>
    public const int BatchSize = 100;
    public const string PrimaryChannelModeProvided = "provided";
    public const string PrimaryChannelModeFallback = "fallback";
    public const string PrimaryChannelModeUnknown = "unknown";
    public const int PrimaryChannelFallbackGroupSize = 5;
    public const string SnapshotColumns = PrtgSnapshotBatch100Capability.SnapshotColumns;
    public const string SnapshotRequestShapeVersion = PrtgSnapshotBatch100Capability.RequestShapeVersion;
    public const int SnapshotCountSentinel = 1;
    public const int MaximumRelativeUrlBytes = PrtgSnapshotBatch100Capability.MaximumRelativeUrlBytes;
    public const string RequestShape = "GET api/table.json?content=sensors&columns=" + SnapshotColumns +
        "&filter_objid=sorted&count=batch+1;batch={0};sentinel=1;url-cap=4096;depth-cap=32;response-cap=524288;version=" +
        SnapshotRequestShapeVersion;

    public static string BuildSnapshotRelativeUrl(IReadOnlyList<long> sensorObjids)
    {
        if (sensorObjids.Count is < 1 or > BatchSize ||
            sensorObjids.Any(id => id <= 0) ||
            sensorObjids.Distinct().Count() != sensorObjids.Count ||
            !sensorObjids.SequenceEqual(sensorObjids.Order()))
            throw new ArgumentException("Snapshot request must use a non-empty, sorted, distinct batch of at most 100 IDs.", nameof(sensorObjids));

        var url = "api/table.json?content=sensors&columns=" + SnapshotColumns +
            PrtgResourceGuardProbe.BuildObjidFilter(sensorObjids) +
            "&count=" + (sensorObjids.Count + SnapshotCountSentinel).ToString(CultureInfo.InvariantCulture);
        if (Encoding.UTF8.GetByteCount(url) > MaximumRelativeUrlBytes)
            throw new InvalidDataException("Snapshot relative URL exceeds the 4096-byte bound.");
        return url;
    }

    public static PrtgSnapshotTargetSelection Resolve(StorageBackend backend, IHostStore hosts,
        SystemSettings settings, IReadOnlyCollection<Sentinel> sentinels,
        PrtgMonitoringPolicy? policyOverride = null)
    {
        var store = backend.PrtgStore();
        var (_, maps) = store.GetLatestHostMapWithDate(30, DateTime.Today);
        var activeHostIds = hosts.GetAll().Where(h => h.Active && !h.MergedInto.HasValue)
            .Select(h => h.HostId).ToHashSet();
        var okMaps = maps.Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue &&
            activeHostIds.Contains(m.HostId.Value)).ToArray();
        var deviceIds = okMaps.Select(m => m.DeviceObjid).Distinct().ToArray();
        var mappedTargets = store.GetValueFetchTargets(settings.PrtgSensorTypeWhitelist, deviceIds);
        var policy = policyOverride ?? new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var guardTargets = policy.Ready(settings.PrtgUrl) && settings.PrtgResourceGuardEnabled
            ? PrtgResourceGuardTargets.Resolve(new PrtgMirrorGuardSource(store), settings, sentinels.ToArray(),
                new SilentConsole(), new PrtgAddressResolver()).SensorObjids
            : Array.Empty<long>();
        var ordered = SelectEffectiveTargets(mappedTargets, policy, guardTargets);
        return CreateSelection(ordered, deviceIds.Length, backend, settings, policy);
    }

    /// <summary>白名單目標（監看政策的 sensor 清單非空時再縮圈）∪ 資源守門目標，依 objid 排序。</summary>
    public static long[] SelectEffectiveTargets(IEnumerable<long> mappedTargets, PrtgMonitoringPolicy policy,
        IEnumerable<long> guardTargets)
    {
        var selected = mappedTargets.Distinct();
        if (policy.SensorIds.Count > 0)
            selected = selected.Where(policy.SensorIds.Contains);
        return selected.Union(guardTargets).Distinct().Order().ToArray();
    }

    public static PrtgSnapshotTargetSelection CreateSelection(IReadOnlyList<long> ordered, int deviceCount,
        StorageBackend backend, SystemSettings settings, PrtgMonitoringPolicy policy)
    {
        var endpoint = CanonicalEndpointFingerprint(settings.PrtgUrl);
        var targetHash = PrtgSnapshotCapacityStore.Fingerprint(string.Join(",", ordered.Select(
            id => id.ToString(CultureInfo.InvariantCulture))));
        var (potentialQualifiedSensorObjids, qualifiedContractFingerprint) =
            FindPotentialQualifiedSensors(backend, ordered, policy, settings);
        var qualifiedHash = qualifiedContractFingerprint;
        var qualifiedIds = potentialQualifiedSensorObjids.ToHashSet();
        var qualifiedPerTargetBatch = ordered.Chunk(PrtgSnapshotCapacityEvaluator.BatchSize)
            .Select(batch => batch.Count(qualifiedIds.Contains)).Where(count => count > 0).ToArray();
        var qualifiedBatchCount = qualifiedPerTargetBatch.Sum(count =>
            (count + PrimaryChannelFallbackGroupSize - 1) / PrimaryChannelFallbackGroupSize);
        var fallbackSampleSize = qualifiedPerTargetBatch
            .Select(count => Math.Min(count, PrimaryChannelFallbackGroupSize)).DefaultIfEmpty(0).Max();
        var secretMaterial = string.Join("|", settings.PrtgApiTokenEnc, settings.PrtgPasswordEnc,
            settings.PrtgPasshashEnc, settings.PrtgUsername, settings.PrtgAuthMode);
        var semanticSettings = string.Join("|", PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
            settings.PrtgTimeoutSeconds, settings.PrtgIgnoreSslErrors, targetHash, qualifiedHash,
            potentialQualifiedSensorObjids.Length, qualifiedBatchCount, fallbackSampleSize,
            policy.SourceGeneration, policy.EndpointHint,
            PrtgSnapshotCapacityStore.Fingerprint(secretMaterial));
        var scope = PrtgSnapshotCapacityStore.Fingerprint(endpoint + "|" + semanticSettings);
        var batchSize = Math.Min(PrtgSnapshotCapacityEvaluator.BatchSize, ordered.Count);
        var mode = InferPrimaryChannelMode(backend, scope, endpoint, batchSize, qualifiedHash,
            potentialQualifiedSensorObjids.Length, qualifiedBatchCount, fallbackSampleSize, DateTimeOffset.UtcNow);
        var shape = BuildRequestShapeFingerprint(batchSize, qualifiedHash, potentialQualifiedSensorObjids.Length,
            qualifiedBatchCount, fallbackSampleSize, mode);
        return new PrtgSnapshotTargetSelection(ordered, deviceCount, batchSize, scope, endpoint, shape,
            potentialQualifiedSensorObjids, potentialQualifiedSensorObjids.Length, qualifiedBatchCount,
            fallbackSampleSize, qualifiedContractFingerprint, mode);
    }

    public static PrtgSnapshotTargetSelection WithPrimaryChannelMode(PrtgSnapshotTargetSelection selection,
        string mode)
    {
        if (mode is not (PrimaryChannelModeProvided or PrimaryChannelModeFallback or PrimaryChannelModeUnknown))
            throw new ArgumentOutOfRangeException(nameof(mode));
        var shape = BuildRequestShapeFingerprint(selection.CapacitySampleBatchSize, selection.PotentialQualifiedContractFingerprint,
            selection.PotentialQualifiedSensorCount, selection.PotentialQualifiedBatchCount,
            selection.PotentialQualifiedFallbackSampleSize, mode);
        return selection with { PrimaryChannelCapabilityMode = mode, RequestShapeFingerprint = shape };
    }

    public static string CanonicalEndpointFingerprint(string? configuredUrl)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return PrtgSnapshotCapacityStore.Fingerprint("invalid-endpoint");
        // Never persist URL query, path credentials, or user-info.
        var endpoint = string.Join("|", uri.Scheme.ToLowerInvariant(), uri.Host.ToLowerInvariant(),
            uri.IsDefaultPort ? "" : uri.Port.ToString(CultureInfo.InvariantCulture),
            uri.AbsolutePath.TrimEnd('/'));
        return PrtgSnapshotCapacityStore.Fingerprint(endpoint);
    }

    private static string BuildRequestShapeFingerprint(int batchSize, string qualifiedHash,
        int qualifiedCount, int fallbackGroupCount, int fallbackSampleSize, string mode) => PrtgSnapshotCapacityStore.Fingerprint(
        PrtgProfileTransportCapacityPilot.RuntimeVersionFingerprint() + "|" +
        string.Format(CultureInfo.InvariantCulture, RequestShape, batchSize) +
        $"|primarychannel-mode={mode}|qualified-candidates={qualifiedCount}|qualified-id-hash={qualifiedHash}|" +
        $"fallback-group-size={PrimaryChannelFallbackGroupSize}|fallback-groups={fallbackGroupCount}|sample-group-size={fallbackSampleSize}|" +
        "fallback-shape=initial-bulk+property-before+bounded-group-bulk+property-after");

    private static string InferPrimaryChannelMode(StorageBackend backend, string scope, string endpoint,
        int batchSize, string qualifiedHash, int qualifiedCount, int fallbackGroupCount,
        int fallbackSampleSize, DateTimeOffset nowUtc)
    {
        var samples = new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Read();
        var matchingModes = new List<string>();
        foreach (var mode in new[] { PrimaryChannelModeProvided, PrimaryChannelModeFallback })
        {
            var shape = BuildRequestShapeFingerprint(batchSize, qualifiedHash, qualifiedCount,
                fallbackGroupCount, fallbackSampleSize, mode);
            var valid = samples.Where(sample => sample.EvidenceKind == "snapshot-batch" &&
                    sample.ScopeFingerprint == scope && sample.EndpointFingerprint == endpoint &&
                    sample.RequestShapeFingerprint == shape && sample.RequestedSensorCount == batchSize &&
                    sample.PrimaryChannelCapabilityMode == mode && sample.Outcome == "success" &&
                    sample.CompletedAtUtc <= nowUtc && nowUtc - sample.CompletedAtUtc <= PrtgSnapshotCapacityEvaluator.EvidenceFreshness &&
                    (mode != PrimaryChannelModeFallback || qualifiedCount == 0 || fallbackSampleSize > 0))
                .OrderByDescending(sample => sample.CompletedAtUtc)
                .Take(PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples).ToArray();
            var fallbackGroups = samples.Count(sample => sample.EvidenceKind == "fallback-group" &&
                sample.ScopeFingerprint == scope && sample.EndpointFingerprint == endpoint &&
                sample.RequestShapeFingerprint == shape && sample.PrimaryChannelCapabilityMode == mode &&
                sample.FallbackGroupSensorCount == fallbackSampleSize && sample.FallbackGroupRequestCount ==
                2 * sample.FallbackGroupSensorCount + 1 && sample.FallbackGroupElapsedMilliseconds > 0 &&
                sample.Outcome == "success" && sample.CompletedAtUtc <= nowUtc &&
                nowUtc - sample.CompletedAtUtc <= PrtgSnapshotCapacityEvaluator.EvidenceFreshness);
            if (valid.Length == PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples &&
                (mode != PrimaryChannelModeFallback || qualifiedCount == 0 ||
                 fallbackGroups >= PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples))
                matchingModes.Add(mode);
        }
        return matchingModes.Count == 1 ? matchingModes[0] : PrimaryChannelModeUnknown;
    }

    private static (long[] SensorObjids, string ContractFingerprint) FindPotentialQualifiedSensors(
        StorageBackend backend, IReadOnlyList<long> ordered,
        PrtgMonitoringPolicy policy, SystemSettings settings)
    {
        var emptyFingerprint = PrtgSnapshotCapacityStore.Fingerprint("no-qualified-binding-contract");
        if (!policy.Ready(settings.PrtgUrl) || ordered.Count == 0) return ([], emptyFingerprint);
        var selected = ordered.Where(policy.SensorIds.Contains).Distinct().Order().ToArray();
        if (selected.Length == 0) return ([], emptyFingerprint);
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
        var authorityContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            policy, strategyName, strategyMinutes);
        var bindingsStore = new PrtgTrustedSamplingBindingStore(backend);
        var result = new List<long>(selected.Length);
        var contractRows = new List<string>(selected.Length);
        for (var offset = 0; offset < selected.Length; offset += 100)
        {
            var page = selected.Skip(offset).Take(100).ToArray();
            var bindings = bindingsStore.GetMany(page);
            var identities = backend.PrtgStore().GetResourceIdentities(page);
            foreach (var id in page)
            {
                if (!identities.TryGetValue(id, out var identity) ||
                    !bindings.TryGetValue(id, out var binding)) continue;
                if (!identity.Active || identity.PendingReconciliation || identity.Epoch <= 0 ||
                    identity.SourceGeneration != policy.SourceGeneration || !policy.HostIds.Contains(identity.HostId) ||
                    binding.SourceGeneration != identity.SourceGeneration || binding.ResourceGeneration != identity.Generation ||
                    binding.IdentityEpoch != identity.Epoch || binding.ChannelGeneration != identity.ChannelGeneration ||
                    identity.ChannelFingerprint != binding.BindingFingerprint ||
                    binding.AuthorityContextFingerprint != authorityContext ||
                    binding.TimeBasisEvidenceReference != policy.TimeBasisEvidenceReference ||
                    binding.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
                    binding.AnalysisTimeZoneId != policy.AnalysisTimeZoneId || !binding.IsValid()) continue;
                result.Add(id);
                // Qualification rotates the channel fingerprint and proof hash, but it does not
                // change the requested trusted semantics. Keep the cost contract stable across
                // first-time proof/profile creation while fencing semantic edits and revisions.
                contractRows.Add(string.Join("|", id.ToString(CultureInfo.InvariantCulture),
                    binding.BindingRevision.ToString(CultureInfo.InvariantCulture), binding.SemanticFingerprint,
                    binding.AuthorityContextFingerprint, binding.SourceGeneration, binding.ResourceGeneration,
                    binding.IdentityEpoch.ToString(CultureInfo.InvariantCulture)));
            }
        }
        var orderedResults = result.Order().ToArray();
        var contractFingerprint = contractRows.Count == 0 ? emptyFingerprint :
            PrtgSnapshotCapacityStore.Fingerprint(string.Join("\n", contractRows.Order(StringComparer.Ordinal)));
        return (orderedResults, contractFingerprint);
    }

    private sealed class SilentConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}
