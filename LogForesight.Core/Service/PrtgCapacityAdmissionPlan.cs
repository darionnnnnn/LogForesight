using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>One versioned, fail-closed admission contract for the shared PRTG Table budget.</summary>
public sealed record PrtgCapacityAdmissionPlan(
    string Fingerprint,
    string SourceFingerprint,
    string SnapshotScopeFingerprint,
    string ProfileScopeFingerprint,
    string StrategyFingerprint,
    string SnapshotRequestShapeFingerprint,
    string RequestShapeFingerprint,
    string RuntimeVersionFingerprint,
    string SettingsRevision,
    string PolicyRevision,
    double SnapshotTableRequestsPerSecond,
    double ProfileTableRequestsPerSecond,
    double GeneralResidualRequestsPerSecond,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LeaseUntilUtc,
    string Owner,
    long Version);

/// <summary>CAS publication of the one active admission contract. A stale owner cannot restore an older plan.</summary>
public sealed class PrtgCapacityAdmissionPlanStore(EfJsonBlobStore blob, TimeProvider? timeProvider = null)
{
    public const string BlobKey = "prtg_capacity_admission_plan_v1";
    private const int MaximumBytes = 4096;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public PrtgCapacityAdmissionPlan? ReadCurrent(DateTimeOffset nowUtc)
    {
        var (raw, _, length) = blob.ReadBoundedWithVersion(MaximumBytes);
        if (length > MaximumBytes || raw is not null && Encoding.UTF8.GetByteCount(raw) > MaximumBytes) return null;
        try
        {
            var plan = string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<PrtgCapacityAdmissionPlan>(raw);
            return plan is not null && plan.LeaseUntilUtc > nowUtc && IsValid(plan) ? plan : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        { return null; }
    }

    /// <summary>Reads a valid retained plan even when its runtime lease expired.</summary>
    public PrtgCapacityAdmissionPlan? ReadRetained()
    {
        var (raw, _, length) = blob.ReadBoundedWithVersion(MaximumBytes);
        if (length > MaximumBytes || raw is not null && Encoding.UTF8.GetByteCount(raw) > MaximumBytes) return null;
        return Parse(raw);
    }

    /// <summary>
    /// Renews an expired retained plan only through an exact-record CAS and a caller supplied
    /// current-source check. This is intended for one bounded profile calibration group, never
    /// for normal runtime work or for publishing new capacity evidence.
    /// </summary>
    public bool TryRenewRetainedForBoundedRecovery(PrtgCapacityAdmissionPlan expected,
        DateTimeOffset nowUtc, TimeSpan leaseDuration, Func<bool> sourceAndSettingsStillCurrent,
        out PrtgCapacityAdmissionPlan? renewed)
    {
        ArgumentNullException.ThrowIfNull(sourceAndSettingsStillCurrent);
        renewed = null;
        if (!IsValid(expected) || expected.Version == long.MaxValue || leaseDuration <= TimeSpan.Zero)
            return false;
        var currentTime = CurrentTime(nowUtc);
        if (expected.LeaseUntilUtc > currentTime) return false;
        var desired = expected with { Version = expected.Version + 1, LeaseUntilUtc = currentTime + leaseDuration };
        var accepted = blob.MutateWithContext<PrtgCapacityAdmissionPlan?>((_, raw) =>
        {
            var current = Parse(raw);
            if (current != expected || current.LeaseUntilUtc > CurrentTime(nowUtc) ||
                !sourceAndSettingsStillCurrent()) return (raw ?? "", null);
            return (Serialize(desired), desired);
        }, MaximumBytes, skipUnchangedContent: true);
        if (accepted is null) return false;
        try
        {
            if (!sourceAndSettingsStillCurrent() || accepted.LeaseUntilUtc <= CurrentTime(nowUtc))
            {
                Invalidate(accepted.Owner, accepted.Version);
                return false;
            }
        }
        catch
        {
            Invalidate(accepted.Owner, accepted.Version);
            throw;
        }
        renewed = accepted;
        return true;
    }

    public PrtgCapacityAdmissionPlan Publish(PrtgCapacityAdmissionPlan candidate, string owner,
        DateTimeOffset nowUtc, TimeSpan leaseDuration, string currentSettingsRevision,
        Func<bool> sourceAndSettingsStillCurrent)
    {
        if (!IsValid(candidate) || owner.Length is < 1 or > 128 || leaseDuration <= TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(currentSettingsRevision))
            throw new ArgumentException("Invalid capacity admission plan.");
        var published = blob.MutateWithContext<PrtgCapacityAdmissionPlan?>((_, raw) =>
        {
            if (!sourceAndSettingsStillCurrent()) return (raw ?? "", null);
            var old = Parse(raw);
            var version = (old?.Version ?? 0) + 1;
            var approvedAt = CurrentTime(nowUtc);
            var next = candidate with { SettingsRevision = currentSettingsRevision, Owner = owner, Version = version, CreatedAtUtc = approvedAt,
                LeaseUntilUtc = approvedAt + leaseDuration };
            return (Serialize(next), next);
        }, MaximumBytes, skipUnchangedContent: true);
        if (published is null || !sourceAndSettingsStillCurrent())
        {
            if (published is not null) Invalidate(published.Owner, published.Version);
            throw new InvalidOperationException("capacity-admission-plan-source-or-settings-superseded");
        }
        if (published.LeaseUntilUtc <= CurrentTime(nowUtc))
        {
            Invalidate(published.Owner, published.Version);
            throw new InvalidOperationException("capacity-admission-plan-expired");
        }
        return published!;
    }

    public bool Renew(string fingerprint, string owner, long version, DateTimeOffset nowUtc, TimeSpan leaseDuration)
    {
        return blob.MutateWithContext((_, raw) =>
        {
            var current = Parse(raw);
            var currentTime = CurrentTime(nowUtc);
            if (current is null || current.Fingerprint != fingerprint || current.Owner != owner ||
                current.Version != version || current.LeaseUntilUtc <= currentTime)
                return (raw ?? "", false);
            var next = current with { LeaseUntilUtc = currentTime + leaseDuration };
            return (Serialize(next), true);
        }, MaximumBytes, skipUnchangedContent: true);
    }

    /// <summary>只重綁無關設定的版本；呼叫端須重驗完整採集契約，不延長租約或重設量測／速率。</summary>
    public bool TryRebindSettingsRevision(PrtgCapacityAdmissionPlan expected, string settingsRevision,
        DateTimeOffset nowUtc, Func<bool> sourceAndSettingsStillCurrent, out PrtgCapacityAdmissionPlan? rebound)
    {
        ArgumentNullException.ThrowIfNull(sourceAndSettingsStillCurrent);
        if (!IsValid(expected) || string.IsNullOrWhiteSpace(settingsRevision) ||
            settingsRevision == expected.SettingsRevision || expected.Version == long.MaxValue)
            throw new ArgumentException("Invalid capacity settings revision rebind.");

        var desired = expected with { SettingsRevision = settingsRevision, Version = expected.Version + 1 };
        var accepted = blob.MutateWithContext<PrtgCapacityAdmissionPlan?>((_, raw) =>
        {
            var current = Parse(raw);
            if (current is null || current.LeaseUntilUtc <= CurrentTime(nowUtc) ||
                !sourceAndSettingsStillCurrent() || current.LeaseUntilUtc <= CurrentTime(nowUtc))
                return (raw ?? "", null);
            // 提交回覆遺失後可重試同一次重綁；不再次增加版本、不借用別人的新計畫。
            if (current == desired) return (raw ?? "", current);
            if (current != expected) return (raw ?? "", null);
            return (Serialize(desired), desired);
        }, MaximumBytes, skipUnchangedContent: true);
        if (accepted is null || !sourceAndSettingsStillCurrent() ||
            accepted.LeaseUntilUtc <= CurrentTime(nowUtc))
        {
            if (accepted is not null) Invalidate(accepted.Owner, accepted.Version);
            rebound = null;
            return false;
        }
        rebound = accepted;
        return true;
    }

    private DateTimeOffset CurrentTime(DateTimeOffset captured)
    {
        var current = _timeProvider.GetUtcNow();
        return current > captured ? current : captured;
    }

    public void Invalidate(string owner, long expectedVersion)
    {
        blob.MutateWithContext((_, raw) =>
        {
            var current = Parse(raw);
            if (current is not null && current.Owner == owner && current.Version == expectedVersion)
                return ("{}", true);
            return (raw ?? "", false);
        }, MaximumBytes);
    }

    private static bool IsValid(PrtgCapacityAdmissionPlan p) =>
        Hash(p.Fingerprint) && Hash(p.SourceFingerprint) && Hash(p.SnapshotScopeFingerprint) &&
        Hash(p.ProfileScopeFingerprint) && Hash(p.StrategyFingerprint) && Hash(p.SnapshotRequestShapeFingerprint) &&
        Hash(p.RequestShapeFingerprint) &&
        Hash(p.RuntimeVersionFingerprint) && !string.IsNullOrWhiteSpace(p.SettingsRevision) &&
        !string.IsNullOrWhiteSpace(p.PolicyRevision) && p.Owner is { Length: >= 1 and <= 128 } && p.Version >= 0 &&
        p.LeaseUntilUtc >= p.CreatedAtUtc &&
        double.IsFinite(p.SnapshotTableRequestsPerSecond) && p.SnapshotTableRequestsPerSecond > 0 &&
        double.IsFinite(p.ProfileTableRequestsPerSecond) && p.ProfileTableRequestsPerSecond > 0 &&
        double.IsFinite(p.GeneralResidualRequestsPerSecond) && p.GeneralResidualRequestsPerSecond > 0 &&
        p.SnapshotTableRequestsPerSecond + p.ProfileTableRequestsPerSecond + p.GeneralResidualRequestsPerSecond <=
            PrtgJointCapacityEvaluator.SharedTableRequestsPerSecond + 1e-9;
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static PrtgCapacityAdmissionPlan? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "{}") return null;
        if (Encoding.UTF8.GetByteCount(raw) > MaximumBytes) throw new InvalidDataException("capacity-admission-plan-cap-exceeded");
        try
        {
            var plan = JsonSerializer.Deserialize<PrtgCapacityAdmissionPlan>(raw);
            return plan is not null && IsValid(plan) ? plan : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        { return null; }
    }
    private static string Serialize(PrtgCapacityAdmissionPlan value)
    {
        var json = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("capacity-admission-plan-cap-exceeded");
        return json;
    }
}
