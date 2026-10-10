using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>Durable, fence-bound request to derive a full trusted profile from a recorded raw proof.</summary>
public sealed record PrtgTrustedSamplingProofRefreshRequest(long SensorObjid, long BindingRevision,
    string BindingFingerprint, string QualificationProofReference, long IdentityEpoch, string ResourceGeneration,
    string SourceGeneration, string ChannelGeneration, string AuthorityContextFingerprint,
    string SettingsRevision, string PolicyRevision, DateTimeOffset QueuedAtUtc, DateTimeOffset NotBeforeUtc)
{
    public const string BlobKeyPrefix = "prtg_trusted_profile_refresh_proof_";
    public const int MaximumPending = 15_000;

    /// <summary>
    /// Keep the durable queue bounded without loading request bodies: at capacity evict one
    /// malformed-key or no-longer-selected sensor key inside the caller's transaction.
    /// </summary>
    internal static void MakeRoomForNewRequest(LfDbContext ctx, long sensorObjid,
        IReadOnlyCollection<long> currentSelectedSensorIds)
    {
        if (sensorObjid <= 0 || currentSelectedSensorIds.Count > MaximumPending ||
            currentSelectedSensorIds.Any(id => id <= 0) || !currentSelectedSensorIds.Contains(sensorObjid))
            throw new InvalidDataException("profile-refresh-proof-notice-scope-invalid");
        var pending = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey.StartsWith(BlobKeyPrefix));
        var countAtMostLimit = pending.Select(row => row.BlobKey).Take(MaximumPending + 1).Count();
        if (countAtMostLimit < MaximumPending) return;
        if (countAtMostLimit > MaximumPending)
            throw new InvalidDataException("profile-refresh-proof-notice-count-corrupt");
        var keys = pending.OrderBy(row => row.BlobKey).Select(row => row.BlobKey)
            .Take(MaximumPending + 1).ToArray();
        var currentScope = currentSelectedSensorIds.ToHashSet();
        var victim = keys.FirstOrDefault(key =>
        {
            var suffix = key.AsSpan(BlobKeyPrefix.Length);
            return !long.TryParse(suffix, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) || !currentScope.Contains(id);
        });
        if (victim is null)
            throw new InvalidDataException("profile-refresh-proof-notice-count-corrupt");
        if (ctx.Blobs.Where(row => row.BlobKey == victim && row.BlobKey.StartsWith(BlobKeyPrefix))
                .ExecuteDelete() != 1)
            throw new InvalidOperationException("profile-refresh-proof-notice-eviction-raced");
    }
}
