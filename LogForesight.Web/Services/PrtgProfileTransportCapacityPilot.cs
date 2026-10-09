using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

public sealed class PrtgProfileTransportCapacityPilot(StorageBackend backend,
    Func<SystemSettings, PrtgClient>? clientFactory = null, IHostStore? hosts = null)
{
    public const int MaximumSensorIds = 5;
    public const int MaximumRequests = MaximumSensorIds * 2;
    public const int MaximumResponseBytes = 512 * 1024;
    public static readonly TimeSpan PilotDeadline = TimeSpan.FromSeconds(30);
    public const string RequestShape = "table.json sensors then channels; one exact sensor id; max 100 channels; 512 KiB each; 2 sequential GETs per sensor";

    public sealed record Result(
        string Status, string Reason, int TargetSensorCount, int RequestsAttempted, int RequestsSent,
        long ElapsedMilliseconds, DateTimeOffset CompletedAtUtc, int MatchingFreshSuccessfulSamples,
        double? P95SensorSeconds, double? EstimatedSeconds, double CompletionWindowSeconds,
        string SourceFingerprint, string ScopeFingerprint, string StrategyFingerprint,
        string RequestShapeFingerprint, string VersionFingerprint);
    public sealed record Contract(string SourceFingerprint, string ScopeFingerprint,
        string StrategyFingerprint, string RequestShapeFingerprint, string VersionFingerprint);

    public static Contract BuildContract(StorageBackend backend, IHostStore? hosts, SystemSettings settings,
        PrtgMonitoringPolicy policy, IReadOnlyCollection<long> ids)
    {
        var identities = backend.PrtgStore().GetResourceIdentities(ids);
        if (ids.Any(id => !identities.TryGetValue(id, out var identity) || !identity.Active ||
            identity.PendingReconciliation || identity.SourceGeneration != policy.SourceGeneration ||
            !policy.HostIds.Contains(identity.HostId)))
            throw new InvalidOperationException("profile-capacity-resource-identity-not-current");

        return BuildTransportContextContract(settings, policy, hosts?.CapturePrtgSnapshot());
    }

    /// <summary>
    /// Builds the transport context for the complete policy scope. This does not authorize
    /// representative HTTP probe IDs; callers that will issue per-resource requests must use
    /// <see cref="BuildContract"/>
    /// so every requested identity is checked against current persisted authority first.
    /// </summary>
    internal static Contract BuildTransportContextContract(SystemSettings settings, PrtgMonitoringPolicy policy,
        PrtgHostSnapshot? hostSnapshot)
    {
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
        var source = PrtgProfileTransportCapacityStore.Fingerprint(string.Join("\n",
            settings.PrtgUrl.TrimEnd('/'), settings.PrtgAuthMode, settings.PrtgApiTokenEnc,
            settings.PrtgUsername, settings.PrtgPasswordEnc, settings.PrtgPasshashEnc,
            settings.PrtgTimeoutSeconds, settings.PrtgIgnoreSslErrors));
        IEnumerable<string> selectedHostState = hostSnapshot is null ? Array.Empty<string>() :
            hostSnapshot.Hosts.Where(host => policy.HostIds.Contains(host.HostId))
                .OrderBy(host => host.HostId).Select(host => string.Join(":", host.HostId, host.Active, host.MergedInto));
        // Transport cost depends on the complete selected set and current selected-host state,
        // not which representative five IDs happened to be due. This context fingerprint is
        // separate from the strict per-ID authorization required by native probe requests.
        var scope = PrtgProfileTransportCapacityStore.Fingerprint(string.Join("\n",
            policy.SourceGeneration, policy.EndpointHint, string.Join(",", policy.HostIds.Order()),
            string.Join(",", policy.SensorIds.Where(id => id > 0).Distinct().Order()),
            string.Join(";", selectedHostState)));
        var strategyFingerprint = PrtgProfileTransportCapacityStore.Fingerprint(
            $"profile-transport-strategy:{strategyName}:{strategyMinutes}");
        return new(source, scope, strategyFingerprint,
            PrtgProfileTransportCapacityStore.Fingerprint(RequestShape), RuntimeVersionFingerprint());
    }

    public static string RuntimeVersionFingerprint()
    {
        var assembly = typeof(PrtgProfileTransportCapacityPilot).Assembly;
        var informational = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
        var assemblyVersion = assembly.GetName().Version?.ToString() ?? "unknown";
        var moduleRevision = assembly.ManifestModule.ModuleVersionId.ToString("N");
        return PrtgProfileTransportCapacityStore.Fingerprint(string.Join("|", informational, assemblyVersion, moduleRevision));
    }

    public async Task<Result> RunAsync(IReadOnlyCollection<long> requestedIds, CancellationToken ct)
    {
        if (requestedIds.Count is < 1 or > MaximumSensorIds || requestedIds.Any(x => x <= 0) ||
            requestedIds.Distinct().Count() != requestedIds.Count)
            throw new ArgumentOutOfRangeException(nameof(requestedIds));

        var settingsStore = new SystemSettingsStore(backend.Blob("system_settings"));
        var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var settings = settingsStore.Get();
        var policy = policyStore.Get();
        if (settings.PrtgEnabled)
            throw new InvalidOperationException("profile-capacity-pilot-requires-prtg-disabled");
        if (!policy.Ready(settings.PrtgUrl) || requestedIds.Any(id => !policy.SensorIds.Contains(id)))
            throw new InvalidOperationException("profile-capacity-source-or-scope-not-ready");
        if (requestedIds.Count != Math.Min(policy.SensorIds.Distinct().Count(), MaximumSensorIds))
            throw new InvalidOperationException("profile-capacity-pilot-requires-full-bounded-sample");

        var ids = requestedIds.Order().ToArray();
        if (!ids.SequenceEqual(policy.SensorIds.Distinct().Order().Take(MaximumSensorIds)))
            throw new InvalidOperationException("profile-capacity-pilot-selection-not-canonical");
        var contract = BuildContract(backend, hosts, settings, policy, ids);
        var sampleContract = new PrtgProfileTransportSample(contract.SourceFingerprint, contract.ScopeFingerprint,
            contract.StrategyFingerprint, contract.RequestShapeFingerprint, DateTimeOffset.UtcNow,
            0, ids.Length, 0, 0, "failed", null, contract.VersionFingerprint);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(PilotDeadline);
        var planFingerprint = PrtgProfileTransportCapacityStore.Fingerprint(string.Join("\n",
            contract.SourceFingerprint, contract.ScopeFingerprint, contract.StrategyFingerprint,
            contract.RequestShapeFingerprint, contract.VersionFingerprint));
        var reservationStore = new PrtgCapacityReservationStore(backend.Blob(PrtgCapacityReservationStore.BlobKey));
        var owner = Guid.NewGuid().ToString("N");
        if (!reservationStore.TryAcquire(planFingerprint, owner, DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(45), out var leaseVersion))
            throw new InvalidOperationException("profile-capacity-pilot-already-running-for-current-plan");
        var attempted = 0;
        var sent = 0;
        var failure = (string?)null;
        var outcome = "success";
        var timer = Stopwatch.StartNew();
        PrtgClient? client = null;
        try
        {
            client = clientFactory?.Invoke(settings) ?? PrtgClientFactory.Create(settings);
            client.RequestPurpose = PrtgRequestPurpose.CapacityPilot;
            foreach (var id in ids)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!reservationStore.Renew(planFingerprint, owner, leaseVersion, DateTimeOffset.UtcNow.AddSeconds(45)))
                    throw new InvalidOperationException("profile-capacity-plan-reservation-lost");
                attempted++;
                var sensor = await client.GetBoundedJsonAsync(
                    $"api/table.json?content=sensors&id={id}&columns=objid,parentid,type,status,lastvalue,lastcheck,interval,cumsince&count=2",
                    MaximumResponseBytes, deadline.Token, () => Interlocked.Increment(ref sent));
                ValidateSensorShape(sensor, id);
                EnsureCurrent(settingsStore, policyStore, policy, contract, ids);

                attempted++;
                var channels = await client.GetBoundedJsonAsync(
                    $"api/table.json?content=channels&id={id}&columns=objid,name,lastvalue_raw,unit&usecaption=1&count=100",
                    MaximumResponseBytes, deadline.Token, () => Interlocked.Increment(ref sent));
                ValidateChannelShape(channels);
                EnsureCurrent(settingsStore, policyStore, policy, contract, ids);
                if (!reservationStore.Renew(planFingerprint, owner, leaseVersion, DateTimeOffset.UtcNow.AddSeconds(45)))
                    throw new InvalidOperationException("profile-capacity-plan-reservation-lost");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = "timeout";
            failure = "pilot_deadline_or_request_timeout";
        }
        catch (InvalidDataException)
        {
            outcome = "failed";
            failure = "response_shape_invalid";
        }
        catch (InvalidOperationException)
        {
            outcome = "failed";
            failure = "source_settings_scope_or_strategy_changed";
        }
        catch (Exception ex) when (ex is PrtgClientException or HttpRequestException)
        {
            outcome = "failed";
            failure = ex is PrtgClientException ? "source_auth_or_request_failed" : "source_request_failed";
        }
        catch (Exception)
        {
            outcome = "failed";
            failure = "pilot_internal_or_storage_failure";
        }
        finally
        {
            timer.Stop();
            client?.Dispose();
            reservationStore.Release(planFingerprint, owner, leaseVersion);
        }
        ct.ThrowIfCancellationRequested();

        var completed = DateTimeOffset.UtcNow;
        var sample = sampleContract with
        {
            CompletedAtUtc = completed,
            ElapsedMilliseconds = (long)Math.Ceiling(Math.Max(0, timer.Elapsed.TotalMilliseconds)),
            RequestsAttempted = attempted,
            RequestsSent = sent,
            Outcome = outcome,
            FailureCode = failure
        };
        new PrtgProfileTransportCapacityStore(backend.Blob(PrtgProfileTransportCapacityStore.BlobKey)).Record(sample);
        var selection = new PrtgProfileTransportCapacityStore(backend.Blob(PrtgProfileTransportCapacityStore.BlobKey)).Read();
        var estimate = PrtgProfileTransportCapacityEvaluator.Evaluate(policy.SensorIds.Distinct().Count(),
            contract.SourceFingerprint, contract.ScopeFingerprint, contract.StrategyFingerprint,
            contract.RequestShapeFingerprint, contract.VersionFingerprint, selection, completed);
        var status = outcome != "success" ? "capacity-unverified" : estimate.Status switch
        {
            PrtgSnapshotCapacityStatus.CapacityQualified => "capacity-qualified",
            PrtgSnapshotCapacityStatus.CapacityExceeded => "capacity-exceeded",
            _ => "capacity-unverified"
        };
        return new(status, outcome == "success" ? estimate.Reason : failure ?? "pilot-failed",
            policy.SensorIds.Distinct().Count(), attempted, sent, sample.ElapsedMilliseconds, completed,
            estimate.FreshSuccessfulSamples, estimate.P95SensorSeconds, estimate.EstimatedSeconds,
            estimate.CompletionWindowSeconds, contract.SourceFingerprint, contract.ScopeFingerprint,
            contract.StrategyFingerprint, contract.RequestShapeFingerprint, contract.VersionFingerprint);
    }

    private static void ValidateSensorShape(string json, long expectedId)
    {
        using var doc = Parse(json);
        if (!doc.RootElement.TryGetProperty("sensors", out var rows) || rows.ValueKind != JsonValueKind.Array ||
            rows.GetArrayLength() != 1 || !rows[0].TryGetProperty("objid", out var id) ||
            !id.TryGetInt64(out var actual) || actual != expectedId)
            throw new InvalidDataException("profile_capacity_sensor_shape_invalid");
    }

    private static void ValidateChannelShape(string json)
    {
        using var doc = Parse(json);
        if (!doc.RootElement.TryGetProperty("channels", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 100)
            throw new InvalidDataException("profile_capacity_channel_shape_invalid");
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("objid", out var id) || !id.TryGetInt64(out _))
                throw new InvalidDataException("profile_capacity_channel_row_invalid");
    }

    private static JsonDocument Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes) throw new InvalidDataException("profile_capacity_response_cap_exceeded");
        try { return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 }); }
        catch (JsonException ex) { throw new InvalidDataException("profile_capacity_json_invalid", ex); }
    }

    private void EnsureCurrent(SystemSettingsStore settingsStore, PrtgMonitoringPolicyStore policyStore,
        PrtgMonitoringPolicy originalPolicy, Contract original, long[] ids)
    {
        var latestSettings = settingsStore.Get();
        var latestPolicy = policyStore.Get();
        var latest = BuildContract(backend, hosts, latestSettings, latestPolicy, ids);
        if (latest.SourceFingerprint != original.SourceFingerprint || latest.ScopeFingerprint != original.ScopeFingerprint ||
            latest.StrategyFingerprint != original.StrategyFingerprint ||
            latest.RequestShapeFingerprint != original.RequestShapeFingerprint ||
            latest.VersionFingerprint != original.VersionFingerprint ||
            ids.Any(id => !latestPolicy.SensorIds.Contains(id)) || !latestPolicy.Ready(latestSettings.PrtgUrl) ||
            originalPolicy.SourceGeneration != latestPolicy.SourceGeneration)
            throw new InvalidOperationException("profile-capacity-contract-changed");
    }

}
