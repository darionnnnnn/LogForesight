using System.Data;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

/// <summary>Bounded durable queue for read-only channel-list discovery. No method writes a profile or proof.</summary>
public sealed class PrtgChannelDiscoveryJobStore(StorageBackend backend)
{
    public const string BlobKey = "prtg_channel_discovery_jobs_v1";
    public const int MaximumJobs = 32;
    public const int MaximumActiveJobs = 16;
    public const int MaximumSerializedBytes = 4 * 1024 * 1024;
    private readonly EfJsonBlobStore blob = backend.Blob(BlobKey, serializeSqlServerWriters: true);

    public sealed class Channel
    {
        public long ChannelObjectId { get; set; }
        public string Caption { get; set; } = "";
        public string Unit { get; set; } = "";
        public double? RawValue { get; set; }
    }

    public sealed class Job
    {
        public string JobId { get; set; } = "";
        public long RequesterUserId { get; set; }
        public bool RequesterWasServerAdmin { get; set; }
        public long SensorObjid { get; set; }
        public string Status { get; set; } = "queued";
        public string Reason { get; set; } = "queued";
        public string SettingsRevision { get; set; } = "";
        public string PolicyRevision { get; set; } = "";
        public string SourceGeneration { get; set; } = "";
        public long HostSnapshotVersion { get; set; }
        public string ResourceGeneration { get; set; } = "";
        public long IdentityEpoch { get; set; }
        public string ChannelGeneration { get; set; } = "";
        public string IdentityChannelFingerprint { get; set; } = "";
        public long BindingRevision { get; set; }
        public string BindingFingerprint { get; set; } = "";
        public string AdmissionPlanFingerprint { get; set; } = "";
        public DateTimeOffset QueuedAtUtc { get; set; }
        public DateTimeOffset DeadlineUtc { get; set; }
        public DateTimeOffset? NextAttemptUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public bool ChannelsTruncated { get; set; }
        public List<Channel> Channels { get; set; } = [];
        public string LeaseOwner { get; set; } = "";
        public long LeaseVersion { get; set; }
        public DateTimeOffset? LeaseUntilUtc { get; set; }
    }

    private sealed class State
    {
        public List<Job> Jobs { get; set; } = [];
        public string LaneOwner { get; set; } = "";
        public long LaneVersion { get; set; }
        public DateTimeOffset? LaneUntilUtc { get; set; }
    }

    public Job Queue(Job job, DateTimeOffset nowUtc)
    {
        Validate(job);
        Job? queued = null;
        blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            Expire(state, nowUtc);
            var matching = state.Jobs.FirstOrDefault(row => IsActive(row) &&
                row.RequesterUserId == job.RequesterUserId && row.SensorObjid == job.SensorObjid &&
                row.SettingsRevision == job.SettingsRevision && row.PolicyRevision == job.PolicyRevision &&
                row.SourceGeneration == job.SourceGeneration && row.HostSnapshotVersion == job.HostSnapshotVersion &&
                row.ResourceGeneration == job.ResourceGeneration && row.IdentityEpoch == job.IdentityEpoch &&
                row.ChannelGeneration == job.ChannelGeneration && row.BindingRevision == job.BindingRevision &&
                row.BindingFingerprint == job.BindingFingerprint &&
                row.IdentityChannelFingerprint == job.IdentityChannelFingerprint);
            if (matching is not null)
            {
                queued = Copy(matching);
                return (Serialize(state), false);
            }
            state.Jobs.RemoveAll(row => !IsActive(row) && row.CompletedAtUtc < nowUtc.AddHours(-24));
            if (state.Jobs.Count(row => IsActive(row)) >= MaximumActiveJobs)
                throw new InvalidOperationException("channel-discovery-active-capacity-full");
            state.Jobs.RemoveAll(row => !IsActive(row) && state.Jobs.Count >= MaximumJobs);
            if (state.Jobs.Count >= MaximumJobs)
                throw new InvalidOperationException("channel-discovery-retention-capacity-full");
            queued = Copy(job);
            state.Jobs.Add(queued);
            return (Serialize(state), true);
        }, MaximumSerializedBytes, IsolationLevel.Serializable);
        return Copy(queued!);
    }

    public Job? Read(string jobId)
    {
        if (!SafeId(jobId)) return null;
        var state = ReadState();
        var job = state.Jobs.SingleOrDefault(row => row.JobId == jobId);
        return job is null ? null : Copy(job);
    }

    public Job? TryAcquire(string owner, DateTimeOffset nowUtc, TimeSpan leaseDuration)
    {
        Job? acquired = null;
        blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            Expire(state, nowUtc);
            if (state.LaneUntilUtc > nowUtc) return (Serialize(state), false);
            var job = state.Jobs.Where(row => IsActive(row) && row.DeadlineUtc > nowUtc &&
                    (!row.NextAttemptUtc.HasValue || row.NextAttemptUtc.Value <= nowUtc) &&
                    (row.Status != "running" || row.LeaseUntilUtc <= nowUtc))
                .OrderBy(row => row.QueuedAtUtc).ThenBy(row => row.JobId, StringComparer.Ordinal).FirstOrDefault();
            if (job is null) return (Serialize(state), false);
            state.LaneVersion = checked(state.LaneVersion + 1);
            state.LaneOwner = owner;
            state.LaneUntilUtc = Min(nowUtc + leaseDuration, job.DeadlineUtc);
            job.Status = "running";
            job.LeaseOwner = owner;
            job.LeaseVersion = state.LaneVersion;
            job.LeaseUntilUtc = state.LaneUntilUtc;
            job.NextAttemptUtc = null;
            job.Reason = "running";
            acquired = Copy(job);
            return (Serialize(state), true);
        }, MaximumSerializedBytes, IsolationLevel.Serializable);
        return acquired;
    }

    public bool Renew(Job lease, DateTimeOffset nowUtc, TimeSpan leaseDuration)
    {
        var renewed = false;
        blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            var job = state.Jobs.SingleOrDefault(row => row.JobId == lease.JobId &&
                row.LeaseOwner == lease.LeaseOwner && row.LeaseVersion == lease.LeaseVersion);
            if (job is null || state.LaneOwner != lease.LeaseOwner || state.LaneVersion != lease.LeaseVersion ||
                state.LaneUntilUtc <= nowUtc || job.DeadlineUtc <= nowUtc)
                return (Serialize(state), false);
            state.LaneUntilUtc = Min(nowUtc + leaseDuration, job.DeadlineUtc);
            job.LeaseUntilUtc = state.LaneUntilUtc;
            renewed = true;
            return (Serialize(state), true);
        }, MaximumSerializedBytes, IsolationLevel.Serializable);
        return renewed;
    }

    public bool Defer(Job lease, DateTimeOffset nowUtc, string reason, TimeSpan retryAfter)
    {
        return MutateLeased(lease, state =>
        {
            var job = FindLease(state, lease);
            job.Status = "waiting-capacity";
            job.Reason = SafeReason(reason);
            job.NextAttemptUtc = Min(nowUtc + retryAfter, job.DeadlineUtc);
            ReleaseLane(state, job);
            return true;
        });
    }

    public bool Complete(Job lease, DateTimeOffset nowUtc, IReadOnlyList<Channel> channels, bool truncated)
    {
        if (channels.Count > 100) throw new ArgumentOutOfRangeException(nameof(channels));
        return MutateLeased(lease, state =>
        {
            var job = FindLease(state, lease);
            job.Status = "completed";
            job.Reason = truncated ? "completed-truncated-at-source-cap" : "channel-list-read-only";
            job.Channels = channels.Select(Copy).ToList();
            job.ChannelsTruncated = truncated;
            job.CompletedAtUtc = nowUtc;
            job.NextAttemptUtc = null;
            ReleaseLane(state, job);
            return true;
        });
    }

    public bool Fail(Job lease, DateTimeOffset nowUtc, string status, string reason)
    {
        if (status is not ("failed" or "failed-stale" or "expired")) throw new ArgumentOutOfRangeException(nameof(status));
        return MutateLeased(lease, state =>
        {
            var job = FindLease(state, lease);
            job.Status = status;
            job.Reason = SafeReason(reason);
            job.CompletedAtUtc = nowUtc;
            job.NextAttemptUtc = null;
            job.Channels.Clear();
            ReleaseLane(state, job);
            return true;
        });
    }

    public void Expire(DateTimeOffset nowUtc)
    {
        blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            Expire(state, nowUtc);
            if (state.LaneUntilUtc <= nowUtc) { state.LaneOwner = ""; state.LaneUntilUtc = null; }
            return (Serialize(state), true);
        }, MaximumSerializedBytes, IsolationLevel.Serializable);
    }

    private bool MutateLeased(Job lease, Func<State, bool> mutation)
    {
        var updated = false;
        blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            if (state.LaneOwner != lease.LeaseOwner || state.LaneVersion != lease.LeaseVersion ||
                state.LaneUntilUtc <= DateTimeOffset.UtcNow) return (Serialize(state), false);
            updated = mutation(state);
            return (Serialize(state), updated);
        }, MaximumSerializedBytes, IsolationLevel.Serializable);
        return updated;
    }

    private static Job FindLease(State state, Job lease) => state.Jobs.SingleOrDefault(row =>
        row.JobId == lease.JobId && row.LeaseOwner == lease.LeaseOwner && row.LeaseVersion == lease.LeaseVersion)
        ?? throw new InvalidOperationException("channel-discovery-job-lease-lost");

    private static void ReleaseLane(State state, Job job)
    {
        state.LaneOwner = "";
        state.LaneUntilUtc = null;
        job.LeaseOwner = "";
        job.LeaseUntilUtc = null;
    }

    private State ReadState()
    {
        var (json, _, length) = blob.ReadBoundedWithVersion(MaximumSerializedBytes);
        if (length > MaximumSerializedBytes || json is not null && Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes)
            throw new InvalidDataException("channel-discovery-state-cap-exceeded");
        return Parse(json);
    }

    private static State Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        if (Encoding.UTF8.GetByteCount(raw) > MaximumSerializedBytes) throw new InvalidDataException("channel-discovery-state-cap-exceeded");
        var value = JsonSerializer.Deserialize<State>(raw) ?? throw new InvalidDataException("channel-discovery-state-invalid");
        if (value.Jobs.Count > MaximumJobs || value.Jobs.Any(job => !SafeId(job.JobId) || job.Channels.Count > 100))
            throw new InvalidDataException("channel-discovery-state-shape-invalid");
        return value;
    }

    private static string Serialize(State value)
    {
        var json = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(json) > MaximumSerializedBytes) throw new InvalidDataException("channel-discovery-state-cap-exceeded");
        return json;
    }

    private static void Expire(State state, DateTimeOffset nowUtc)
    {
        foreach (var job in state.Jobs.Where(row => IsActive(row) && row.DeadlineUtc <= nowUtc))
        {
            job.Status = "expired";
            job.Reason = "deadline-reached";
            job.CompletedAtUtc = nowUtc;
            job.Channels.Clear();
        }
        if (state.LaneUntilUtc <= nowUtc) { state.LaneOwner = ""; state.LaneUntilUtc = null; }
    }

    private static bool IsActive(Job row) => row.Status is "queued" or "running" or "waiting-capacity";
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
    private static bool SafeId(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);
    private static string SafeReason(string value) => value.Length <= 128 ? value : value[..128];
    private static Job Copy(Job job) => JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
    private static Channel Copy(Channel channel) => new() { ChannelObjectId = channel.ChannelObjectId,
        Caption = channel.Caption, Unit = channel.Unit, RawValue = channel.RawValue };
    private static void Validate(Job job)
    {
        if (!SafeId(job.JobId) || job.RequesterUserId < 0 || job.SensorObjid <= 0 ||
            job.Status != "queued" || job.QueuedAtUtc.Offset != TimeSpan.Zero || job.DeadlineUtc <= job.QueuedAtUtc ||
            job.DeadlineUtc - job.QueuedAtUtc > TimeSpan.FromMinutes(5) || job.SettingsRevision.Length is < 1 or > 128 ||
            job.PolicyRevision.Length is < 1 or > 128 || job.SourceGeneration.Length is < 1 or > 128 ||
            job.HostSnapshotVersion < 0 || job.ResourceGeneration.Length is < 1 or > 128 || job.IdentityEpoch <= 0 ||
            job.ChannelGeneration.Length > 128 || job.BindingRevision < 0 ||
            job.BindingFingerprint.Length is > 128 || job.IdentityChannelFingerprint.Length > 128 ||
            job.AdmissionPlanFingerprint.Length is not (0 or 64))
            throw new ArgumentException("channel-discovery-job-invalid", nameof(job));
    }
}
