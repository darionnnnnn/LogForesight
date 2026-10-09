using LogForesight.Core.Persistence;
using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>Rechecks the current source profile before replay or notification of a disk resource issue.</summary>
public static class PrtgResourceProfileQualification
{
    public static bool IsCpuOrMemoryPressure(LogIssueSignature issue) =>
        issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure ||
        issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceMemoryPressure;
    public static bool IsCurrentDiskProfile(StorageBackend backend, long hostId, long sensorObjid, DateTime nowUtc,
        string? expectedSourceGeneration = null, string? expectedResourceGeneration = null,
        string? expectedChannelGeneration = null) =>
        TryCurrentProfile(backend, hostId, sensorObjid, nowUtc, PrtgResourceFamily.Disk,
            expectedSourceGeneration, expectedResourceGeneration, expectedChannelGeneration, out _);

    /// <summary>Late cases/mail must still have the exact enabled rule, profile, and active formal grant.</summary>
    public static bool IsCurrentFormalPressure(StorageBackend backend, long hostId, long sensorObjid,
        LogIssueSignature issue, DateTime nowUtc)
    {
        var family = issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure ? PrtgResourceFamily.Cpu :
            issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceMemoryPressure ? PrtgResourceFamily.Memory :
                (PrtgResourceFamily?)null;
        if (!family.HasValue || string.IsNullOrWhiteSpace(issue.PrtgChannelGeneration) ||
            !TryCurrentProfile(backend, hostId, sensorObjid, nowUtc, family.Value,
                issue.PrtgSourceGeneration, issue.PrtgResourceGeneration, issue.PrtgChannelGeneration, out var profile) ||
            profile is null) return false;
        var catalog = PrtgResourceCurrentRuleCatalog.Load(backend);
        var rule = catalog.For(family.Value);
        var grant = new PrtgResourcePressureModeStore(backend.Blob(PrtgResourcePressureModeStore.BlobKey(hostId)))
            .Find(hostId, sensorObjid, family.Value);
        if (rule is null || rule.Rule.Id != issue.RuleId || issue.PrtgRuleAdmissionFingerprint !=
                catalog.AdmissionFingerprintFor(family.Value) ||
            grant is not { FormalEnabled: true, MaintainAuthorized: true } ||
            grant.RuleId != rule.Rule.Id || grant.RuleFingerprint != rule.Fingerprint ||
            grant.SourceGeneration != profile.SourceGeneration || grant.ResourceGeneration != profile.ResourceGeneration ||
            grant.ChannelGeneration != profile.ChannelGeneration ||
            grant.ResourceEpoch != profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            grant.SemanticVersion != profile.SemanticVersion || grant.StrategyVersion != profile.StrategyFingerprint)
            return false;
        var context = new PrtgResourceCurrentContext(profile.SensorObjid, profile.SourceGeneration,
            profile.ResourceGeneration, profile.ChannelGeneration,
            profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), profile.SemanticVersion,
            profile.StrategyFingerprint, profile.StrategyMinutes, profile.StrategyEffectiveFromHourUtc,
            profile.ConfirmedScanInterval, profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId,
            PrtgResourcePeriodConsumer.StableProfileFingerprint(profile));
        return grant.ProfileFingerprint == PrtgResourcePressureEvaluator.GetProfileFingerprint(family.Value, context);
    }

    private static bool TryCurrentProfile(StorageBackend backend, long hostId, long sensorObjid, DateTime nowUtc,
        PrtgResourceFamily family, string? expectedSourceGeneration, string? expectedResourceGeneration,
        string? expectedChannelGeneration, out PrtgTrustedSamplingProfile? currentProfile)
    {
        currentProfile = null;
        ArgumentNullException.ThrowIfNull(backend);
        if (hostId <= 0 || sensorObjid <= 0 || nowUtc.Kind != DateTimeKind.Utc) return false;
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        if (!settings.PrtgEnabled) return false;
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (!policy.Ready(settings.PrtgUrl) || !policy.HostIds.Contains(hostId) ||
            !policy.SensorIds.Contains(sensorObjid)) return false;
        var store = backend.PrtgStore();
        if (!store.GetResourceIdentities([sensorObjid]).TryGetValue(sensorObjid, out var identity) ||
            !identity.Active || identity.PendingReconciliation || identity.HostId != hostId ||
            expectedSourceGeneration is not null && identity.SourceGeneration != expectedSourceGeneration ||
            expectedResourceGeneration is not null && identity.Generation != expectedResourceGeneration ||
            expectedChannelGeneration is not null && identity.ChannelGeneration != expectedChannelGeneration) return false;
        var metadata = store.GetResourcePressureSensorMetadata([sensorObjid])
            .FirstOrDefault(item => item.SensorObjid == sensorObjid);
        if (metadata is null || metadata.Paused || metadata.DevicePaused ||
            metadata.DeviceObjid != identity.DeviceId ||
            !string.Equals(metadata.Category, family.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
        var profile = new PrtgTrustedSamplingProfileStore(backend).GetMany([sensorObjid])
            .GetValueOrDefault(sensorObjid);
        if (profile is null || !(family switch
            {
                PrtgResourceFamily.Cpu => profile.Quantity == PrtgTrustedQuantitySemantic.CpuLoadPercent,
                PrtgResourceFamily.Memory => profile.Quantity is PrtgTrustedQuantitySemantic.MemoryUsedPercent or
                    PrtgTrustedQuantitySemantic.MemoryAvailablePercent,
                // DiskUsedPercent is a configured quantity, but no formal risk rule consumes it yet.
                _ => profile.Quantity == PrtgTrustedQuantitySemantic.DiskFreePercent
            }) ||
            profile.Unit != "%" || !double.IsFinite(profile.Scale) || profile.Scale <= 0) return false;
        var strategyProfile = PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(policy,
            PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
            strategyProfile.SnapshotIntervalMinutes, nowUtc);
        if (!strategy.Ready) return false;
        if (!PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy, sensorObjid,
                metadata.SensorType, strategy, nowUtc, nowUtc, nowUtc).Ready) return false;
        currentProfile = profile;
        return true;
    }
}
