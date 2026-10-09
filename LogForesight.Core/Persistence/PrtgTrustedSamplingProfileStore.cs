using LogForesight.Core.Service;

namespace LogForesight.Core.Persistence;

/// <summary>Per-resource 8 KiB profile records, fetched with bounded SQL batches.</summary>
public sealed class PrtgTrustedSamplingProfileStore(StorageBackend backend)
{
    public IReadOnlyDictionary<long, PrtgTrustedSamplingProfile> GetMany(IEnumerable<long> sensorIds) =>
        backend.PrtgStore().GetTrustedSamplingProfiles(sensorIds);

    /// <summary>
    /// Persist only a profile created from a typed source probe result. The SQL writer rechecks the
    /// current resource identity in the same transaction, so a stale probe cannot authorize a new epoch.
    /// </summary>
    internal void RecordProbeResult(PrtgTrustedSamplingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        backend.PrtgStore().RecordTrustedSamplingProfile(profile);
    }

    /// <summary>Atomically checks a durable worker lease while publishing a typed source profile.</summary>
    public bool RecordProbeResultUnderLease(PrtgTrustedSamplingProfile profile,
        string leaseOwner, long leaseVersion)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        return backend.PrtgStore().RecordTrustedSamplingProfile(profile, leaseOwner, leaseVersion);
    }

    /// <summary>
    /// Revoke only the exact prior profile after a completed scheduled refresh observed missing
    /// source authority. Callers must not use this for diagnostic/API probes or transport failures.
    /// </summary>
    public bool RevokeAfterObservedRefreshFailure(PrtgTrustedSamplingProfile expectedProfile,
        string leaseOwner, long leaseVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedProfile);
        expectedProfile.Validate();
        return backend.PrtgStore().RevokeTrustedSamplingProfileAfterObservedRefreshFailure(
            expectedProfile, leaseOwner, leaseVersion);
    }
}
