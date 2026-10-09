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
public sealed class PrtgCapacityAdmissionPlanStore(EfJsonBlobStore blob)
{
    public const string BlobKey = "prtg_capacity_admission_plan_v1";
    private const int MaximumBytes = 4096;

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

    public PrtgCapacityAdmissionPlan Publish(PrtgCapacityAdmissionPlan candidate, string owner,
        DateTimeOffset nowUtc, TimeSpan leaseDuration, string currentSettingsRevision,
        Func<bool> sourceAndSettingsStillCurrent)
    {
        if (!IsValid(candidate) || owner.Length is < 1 or > 128 || leaseDuration <= TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(currentSettingsRevision))
            throw new ArgumentException("Invalid capacity admission plan.");
        PrtgCapacityAdmissionPlan? published = null;
        blob.MutateWithContext((_, raw) =>
        {
            if (!sourceAndSettingsStillCurrent()) return (raw ?? "", false);
            var old = Parse(raw);
            var version = (old?.Version ?? 0) + 1;
            published = candidate with { SettingsRevision = currentSettingsRevision, Owner = owner, Version = version, CreatedAtUtc = nowUtc,
                LeaseUntilUtc = nowUtc + leaseDuration };
            return (Serialize(published), true);
        }, MaximumBytes);
        if (published is null || !sourceAndSettingsStillCurrent())
        {
            if (published is not null) Invalidate(published.Owner, published.Version);
            throw new InvalidOperationException("capacity-admission-plan-source-or-settings-superseded");
        }
        return published!;
    }

    public bool Renew(string fingerprint, string owner, long version, DateTimeOffset nowUtc, TimeSpan leaseDuration)
    {
        var renewed = false;
        blob.MutateWithContext((_, raw) =>
        {
            var current = Parse(raw);
            if (current is null || current.Fingerprint != fingerprint || current.Owner != owner ||
                current.Version != version || current.LeaseUntilUtc <= nowUtc)
                return (raw ?? "", false);
            var next = current with { LeaseUntilUtc = nowUtc + leaseDuration };
            renewed = true;
            return (Serialize(next), true);
        }, MaximumBytes);
        return renewed;
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
