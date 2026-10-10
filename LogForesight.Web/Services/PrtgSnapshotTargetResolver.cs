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
    string RequestShapeFingerprint);

/// <summary>One target and evidence fingerprint implementation shared by snapshot runtime, estimate, and pilot.</summary>
public static class PrtgSnapshotTargetResolver
{
    public const string SnapshotColumns = PrtgSnapshotBatch100Capability.SnapshotColumns;
    public const string SnapshotRequestShapeVersion = PrtgSnapshotBatch100Capability.RequestShapeVersion;
    public const int SnapshotCountSentinel = 1;
    public const int MaximumRelativeUrlBytes = PrtgSnapshotBatch100Capability.MaximumRelativeUrlBytes;
    public const string RequestShape = "GET api/table.json?content=sensors&columns=" + SnapshotColumns +
        "&filter_objid=sorted&count=batch+1;batch={0};sentinel=1;url-cap=4096;depth-cap=32;response-cap=524288;version=" +
        SnapshotRequestShapeVersion;

    public static string BuildSnapshotRelativeUrl(IReadOnlyList<long> sensorObjids)
    {
        if (sensorObjids.Count is < 1 or > PrtgSnapshotCapacityEvaluator.BatchSize ||
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
        var ordered = SelectEffectiveTargets(mappedTargets, policy, settings, guardTargets);
        return CreateSelection(ordered, deviceIds.Length, backend, settings, policy);
    }

    public static long[] SelectEffectiveTargets(IEnumerable<long> mappedTargets, PrtgMonitoringPolicy policy,
        SystemSettings settings, IEnumerable<long> guardTargets)
    {
        var selected = mappedTargets.Distinct();
        if (policy.Ready(settings.PrtgUrl))
            selected = selected.Where(policy.SensorIds.Contains).Union(guardTargets);
        return selected.Distinct().Order().ToArray();
    }

    public static PrtgSnapshotTargetSelection CreateSelection(IReadOnlyList<long> ordered, int deviceCount,
        StorageBackend backend, SystemSettings settings, PrtgMonitoringPolicy policy)
    {
        var endpoint = CanonicalEndpointFingerprint(settings.PrtgUrl);
        var targetHash = PrtgSnapshotCapacityStore.Fingerprint(string.Join(",", ordered.Select(
            id => id.ToString(CultureInfo.InvariantCulture))));
        var secretMaterial = string.Join("|", settings.PrtgApiTokenEnc, settings.PrtgPasswordEnc,
            settings.PrtgPasshashEnc, settings.PrtgUsername, settings.PrtgAuthMode);
        var semanticSettings = string.Join("|", PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
            settings.PrtgTimeoutSeconds, settings.PrtgIgnoreSslErrors, targetHash,
            policy.SourceGeneration, policy.EndpointHint,
            PrtgSnapshotCapacityStore.Fingerprint(secretMaterial));
        var scope = PrtgSnapshotCapacityStore.Fingerprint(endpoint + "|" + semanticSettings);
        var batchSize = Math.Min(PrtgSnapshotCapacityEvaluator.BatchSize, ordered.Count);
        // Pilot、runtime、估算共用同一建置識別；升級後不接受舊版的成本樣本。
        var shape = PrtgSnapshotCapacityStore.Fingerprint(
            PrtgProfileTransportCapacityPilot.RuntimeVersionFingerprint() + "|" +
            string.Format(CultureInfo.InvariantCulture, RequestShape, batchSize));
        return new PrtgSnapshotTargetSelection(ordered, deviceCount, batchSize, scope, endpoint, shape);
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

    private sealed class SilentConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}
