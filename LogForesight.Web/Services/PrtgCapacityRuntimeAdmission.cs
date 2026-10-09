using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>Rebuilds current source/scope evidence before a runtime lane may consume Table quota.</summary>
internal static class PrtgCapacityRuntimeAdmission
{
    public const string BoundedSingleBatchRecoveryReason = "bounded-single-batch-recovery-only";

    public static bool TryGetCurrent(StorageBackend backend, IHostStore hosts, SystemSettings settings,
        PrtgSnapshotTargetSelection snapshotSelection, out PrtgCapacityAdmissionPlan? plan, out string reason,
        bool allowBoundedSingleBatchRecovery = false)
    {
        plan = null;
        reason = "capacity-admission-plan-missing";
        if (!settings.PrtgEnabled) { reason = "prtg-disabled"; return false; }
        var now = DateTimeOffset.UtcNow;
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (policy.SensorIds.Count > 15_000 || !policy.SensorIds.Any(id => id > 0) || !policy.Ready(settings.PrtgUrl))
        { reason = "profile-source-or-scope-not-ready"; return false; }

        PrtgProfileTransportCapacityPilot.Contract contract;
        try
        {
            contract = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(
                settings, policy, hosts.CapturePrtgSnapshot());
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
        { reason = "profile-contract-unavailable"; return false; }

        var planStore = new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
        var stored = planStore.ReadCurrent(now);
        var priorContractMatches = stored is not null && stored.SourceFingerprint == contract.SourceFingerprint &&
            stored.SnapshotScopeFingerprint == snapshotSelection.ScopeFingerprint &&
            stored.ProfileScopeFingerprint == contract.ScopeFingerprint &&
            stored.StrategyFingerprint == contract.StrategyFingerprint &&
            stored.SnapshotRequestShapeFingerprint == snapshotSelection.RequestShapeFingerprint &&
            stored.RequestShapeFingerprint == contract.RequestShapeFingerprint &&
            stored.RuntimeVersionFingerprint == contract.VersionFingerprint &&
            stored.PolicyRevision == policy.Revision;

        var snapshotCapacityStore = new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
        var snapshotEvidence = snapshotCapacityStore.Read();
        var snapshot = PrtgSnapshotCapacityEvaluator.Evaluate(snapshotSelection.SensorObjids.Count,
            PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy), snapshotSelection.ScopeFingerprint,
            snapshotSelection.EndpointFingerprint, snapshotSelection.RequestShapeFingerprint,
            snapshotEvidence, now);
        var profile = PrtgProfileTransportCapacityEvaluator.Evaluate(policy.SensorIds.Distinct().Count(),
            contract.SourceFingerprint, contract.ScopeFingerprint, contract.StrategyFingerprint,
            contract.RequestShapeFingerprint, contract.VersionFingerprint,
            new PrtgProfileTransportCapacityStore(backend.Blob(PrtgProfileTransportCapacityStore.BlobKey)).Read(), now,
            requiredFreshSuccessfulSamples: priorContractMatches ? 1 : PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples);
        var usage = PrtgRequestBudget.Shared.ReadUsage();
        var joint = priorContractMatches && stored is not null
            ? PrtgJointCapacityEvaluator.EvaluateAgainstReservedRates(snapshot, profile, usage,
                settings.PrtgTimeoutSeconds, stored.SnapshotTableRequestsPerSecond,
                stored.ProfileTableRequestsPerSecond)
            : PrtgJointCapacityEvaluator.Evaluate(snapshot, profile, usage, settings.PrtgTimeoutSeconds);
        var boundedRecovery = false;
        if (joint.Status != PrtgSnapshotCapacityStatus.CapacityQualified)
        {
            if (!allowBoundedSingleBatchRecovery || !priorContractMatches || stored is null ||
                snapshotSelection.SensorObjids.Count is < 1 or > PrtgSnapshotCapacityEvaluator.BatchSize ||
                snapshot.Reason != "insufficient_fresh_full_batch_samples")
            { reason = "current-capacity-evidence-" + joint.Reason; return false; }

            // Recovery is available only while there is fresh, real evidence for this exact
            // batch contract. An absent or stale blob cannot bootstrap a request.
            var requiredBatchCount = snapshotSelection.SensorObjids.Count;
            var recoveryProgress = snapshotEvidence.Where(sample =>
                    sample.ScopeFingerprint == snapshotSelection.ScopeFingerprint &&
                    sample.EndpointFingerprint == snapshotSelection.EndpointFingerprint &&
                    sample.RequestShapeFingerprint == snapshotSelection.RequestShapeFingerprint &&
                    sample.RequestedSensorCount == requiredBatchCount && sample.CompletedAtUtc <= now &&
                    now - sample.CompletedAtUtc <= PrtgSnapshotCapacityEvaluator.EvidenceFreshness)
                .OrderBy(sample => sample.CompletedAtUtc).ToArray();
            if (recoveryProgress.Length is < 1 or >= PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples ||
                recoveryProgress.Any(sample => sample.ElapsedMilliseconds < 0 ||
                    sample.Outcome is not ("success" or "failed" or "timeout")))
            { reason = "current-capacity-evidence-bounded-recovery-progress-missing-or-invalid"; return false; }

            var recovery = PrtgJointCapacityEvaluator.EvaluateBoundedSingleBatchRecovery(
                snapshotSelection.SensorObjids.Count, PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
                profile, usage, settings.PrtgTimeoutSeconds,
                stored.SnapshotTableRequestsPerSecond, stored.ProfileTableRequestsPerSecond);
            if (!recovery.Admitted)
            { reason = "current-capacity-evidence-" + recovery.Reason; return false; }
            if (!PrtgJointCapacityEvaluator.HasCurrentPlanFingerprint(stored))
            { reason = "current-source-scope-or-strategy-plan-mismatch"; return false; }
            boundedRecovery = true;
        }

        if (!priorContractMatches || stored is null ||
            stored.SourceFingerprint != contract.SourceFingerprint ||
            stored.SnapshotScopeFingerprint != snapshotSelection.ScopeFingerprint ||
            stored.ProfileScopeFingerprint != contract.ScopeFingerprint ||
            stored.StrategyFingerprint != contract.StrategyFingerprint ||
            stored.SnapshotRequestShapeFingerprint != snapshotSelection.RequestShapeFingerprint ||
            stored.RequestShapeFingerprint != contract.RequestShapeFingerprint ||
            stored.RuntimeVersionFingerprint != contract.VersionFingerprint ||
            stored.PolicyRevision != policy.Revision)
        { reason = "current-source-scope-or-strategy-plan-mismatch"; return false; }

        if (!boundedRecovery)
        {
            PrtgCapacityAdmissionPlan expected;
            try { expected = PrtgJointCapacityEvaluator.CreatePlan(joint, contract.SourceFingerprint,
                snapshotSelection.ScopeFingerprint, contract.ScopeFingerprint, contract.StrategyFingerprint,
                snapshotSelection.RequestShapeFingerprint, contract.RequestShapeFingerprint,
                contract.VersionFingerprint, now,
                settings.Revision, policy.Revision); }
            catch (InvalidOperationException) { reason = "joint-plan-not-qualified"; return false; }
            if (stored.Fingerprint != expected.Fingerprint)
            { reason = "current-source-scope-or-strategy-plan-mismatch"; return false; }
        }

        if (stored.SettingsRevision != settings.Revision)
        {
            // 全域設定的品牌／AI／郵件保存也會換版本。只有本輪已重驗的完整
            // PRTG 契約仍完全相同時，才 CAS 重綁；來源、範圍、政策或成本不符仍拒絕。
            bool StillCurrent()
            {
                var latest = new SystemSettingsStore(backend.Blob("system_settings")).Get();
                var latestPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                if (!latest.PrtgEnabled || latest.Revision != settings.Revision ||
                    latestPolicy.Revision != policy.Revision) return false;
                try
                {
                    var latestContract = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(
                        latest, latestPolicy, hosts.CapturePrtgSnapshot());
                    var latestSelection = PrtgSnapshotTargetResolver.Resolve(backend, hosts, latest,
                        new SentinelStore(backend.Blob("sentinels")).GetAll(), latestPolicy);
                    return latestContract == contract &&
                        latestSelection.ScopeFingerprint == snapshotSelection.ScopeFingerprint &&
                        latestSelection.RequestShapeFingerprint == snapshotSelection.RequestShapeFingerprint;
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException)
                { return false; }
            }
            if (!planStore.TryRebindSettingsRevision(stored, settings.Revision, DateTimeOffset.UtcNow,
                StillCurrent, out var rebound))
            { reason = "capacity-admission-settings-rebind-superseded"; return false; }
            stored = rebound!;
        }

        var admissionTime = DateTimeOffset.UtcNow;
        if (stored.LeaseUntilUtc <= admissionTime)
        { reason = "capacity-admission-plan-expired"; return false; }
        if (stored.LeaseUntilUtc <= admissionTime.AddHours(6))
        {
            if (!planStore.Renew(stored.Fingerprint, stored.Owner, stored.Version,
                admissionTime, TimeSpan.FromHours(24)))
            { reason = "capacity-admission-plan-renewal-superseded"; return false; }
            var renewed = planStore.ReadCurrent(DateTimeOffset.UtcNow);
            if (renewed is null || renewed.Fingerprint != stored.Fingerprint ||
                renewed.Owner != stored.Owner || renewed.Version != stored.Version)
            { reason = "capacity-admission-plan-renewal-superseded"; return false; }
            stored = renewed;
        }

        PrtgRequestBudget.Shared.SetAdmissionPlan(stored);
        plan = stored;
        reason = boundedRecovery ? BoundedSingleBatchRecoveryReason : "admitted";
        return true;
    }
}
