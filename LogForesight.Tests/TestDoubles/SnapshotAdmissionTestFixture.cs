using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;

namespace LogForesight.Tests;

/// <summary>
/// Installs bounded synthetic capacity evidence for tests that exercise an admitted snapshot
/// request. This is test transport evidence only; it does not create native PRTG observations,
/// trusted sampling profiles, or production qualification.
/// </summary>
internal static class SnapshotAdmissionTestFixture
{
    public static void Seed(StorageBackend backend, IHostStore hosts, ISystemSettingsStore settingsStore,
        long hostId, long deviceId, params (long SensorObjid, string SensorType)[] sensors)
    {
        if (sensors.Length == 0) throw new ArgumentException("At least one mirrored sensor is required.", nameof(sensors));
        var settings = settingsStore.Get();
        var now = DateTimeOffset.UtcNow;
        var generation = "snapshot-admission-test-generation";
        var prtg = backend.PrtgStore();
        var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.Revision = Guid.NewGuid().ToString("N");
            policy.CoreSystemId = "snapshot-admission-test";
            policy.SourceGeneration = generation;
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl);
            policy.ValidFrom = now;
            policy.HostIds = [hostId];
            policy.SensorIds = sensors.Select(sensor => sensor.SensorObjid).Distinct().Order().ToList();
            policy.SourceTimeZoneId = "UTC";
            policy.SourceCultureName = "en-US";
        });

        foreach (var sensor in sensors)
            prtg.BindObservedResource(sensor.SensorObjid, hostId, generation,
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(
                    deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    sensor.SensorType, "created", 0));

        var policy = policyStore.Get();
        var selection = PrtgSnapshotTargetResolver.Resolve(backend, hosts, settings, Array.Empty<Sentinel>(), policy);
        if (selection.SensorObjids.Count == 0)
            throw new InvalidOperationException("Synthetic snapshot fixture did not resolve an active mapped target.");

        var snapshotSamples = new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
        for (var index = 0; index < PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples; index++)
            snapshotSamples.Record(new PrtgSnapshotCapacitySample(selection.ScopeFingerprint,
                selection.EndpointFingerprint, selection.RequestShapeFingerprint, now.AddSeconds(index - 5),
                250, selection.CapacitySampleBatchSize, "success"));

        var profileIds = policy.SensorIds.Distinct().Order()
            .Take(PrtgProfileTransportCapacityPilot.MaximumSensorIds).ToArray();
        var contract = PrtgProfileTransportCapacityPilot.BuildContract(
            backend, hosts, settings, policy, profileIds);
        var profileSamples = new PrtgProfileTransportCapacityStore(
            backend.Blob(PrtgProfileTransportCapacityStore.BlobKey));
        for (var index = 0; index < PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples; index++)
            profileSamples.Record(new PrtgProfileTransportSample(contract.SourceFingerprint,
                contract.ScopeFingerprint, contract.StrategyFingerprint, contract.RequestShapeFingerprint,
                now.AddSeconds(index - 5), 1_000L * profileIds.Length, profileIds.Length,
                profileIds.Length * 2, profileIds.Length * 2, "success", null, contract.VersionFingerprint));

        var snapshotEstimate = PrtgSnapshotCapacityEvaluator.Evaluate(selection.SensorObjids.Count,
            PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy), selection.ScopeFingerprint,
            selection.EndpointFingerprint, selection.RequestShapeFingerprint, snapshotSamples.Read(), now);
        var profileEstimate = PrtgProfileTransportCapacityEvaluator.Evaluate(policy.SensorIds.Distinct().Count(),
            contract.SourceFingerprint, contract.ScopeFingerprint, contract.StrategyFingerprint,
            contract.RequestShapeFingerprint, contract.VersionFingerprint, profileSamples.Read(), now);
        var joint = PrtgJointCapacityEvaluator.Evaluate(snapshotEstimate, profileEstimate,
            PrtgRequestBudget.Shared.ReadUsage(), settings.PrtgTimeoutSeconds);
        var candidate = PrtgJointCapacityEvaluator.CreatePlan(joint, contract.SourceFingerprint,
            selection.ScopeFingerprint, contract.ScopeFingerprint, contract.StrategyFingerprint,
            selection.RequestShapeFingerprint, contract.RequestShapeFingerprint, contract.VersionFingerprint,
            now, settings.Revision, policy.Revision);

        var admissionStore = new PrtgCapacityAdmissionPlanStore(
            backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
        admissionStore.Publish(candidate, "snapshot-test-fixture", now, TimeSpan.FromHours(24), settings.Revision, () =>
        {
            var currentSettings = settingsStore.Get();
            var currentPolicy = policyStore.Get();
            if (!currentSettings.PrtgEnabled || currentSettings.Revision != settings.Revision ||
                currentPolicy.Revision != policy.Revision) return false;
            var currentContract = PrtgProfileTransportCapacityPilot.BuildContract(
                backend, hosts, currentSettings, currentPolicy, profileIds);
            var currentSelection = PrtgSnapshotTargetResolver.Resolve(
                backend, hosts, currentSettings, Array.Empty<Sentinel>(), currentPolicy);
            return currentContract == contract &&
                currentSelection.ScopeFingerprint == selection.ScopeFingerprint &&
                currentSelection.RequestShapeFingerprint == selection.RequestShapeFingerprint;
        });
    }
}
