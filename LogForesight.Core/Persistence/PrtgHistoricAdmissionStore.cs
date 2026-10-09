using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

/// <summary>
/// Durable rolling-window admission shared by every PRTG client using this LF database.
/// Sent rows are the only retained history and are trimmed to the five-token/60-second bound.
/// </summary>
public sealed class PrtgHistoricAdmissionStore
{
    public const string BlobKey = "prtg_historic_admission_v1";
    public const int MaximumJsonBytes = 16 * 1024;
    public const int WindowCapacity = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private readonly EfJsonBlobStore blob;
    private readonly Func<DateTimeOffset> transactionClock;

    public PrtgHistoricAdmissionStore(StorageBackend backend)
        : this(backend, () => DateTimeOffset.UtcNow) { }

    internal PrtgHistoricAdmissionStore(StorageBackend backend, Func<DateTimeOffset> transactionClock)
    {
        blob = backend.Blob(BlobKey, serializeSqlServerWriters: true);
        this.transactionClock = transactionClock ?? throw new ArgumentNullException(nameof(transactionClock));
    }

    public sealed class State
    {
        public List<SentToken> Sent { get; set; } = [];
        public List<PendingToken> Pending { get; set; } = [];
        public ActiveQualification? ActiveQualification { get; set; }
        public PrtgQualificationJobStateStore.Job? Job { get; set; }
    }

    public sealed class PendingToken
    {
        public string Id { get; set; } = "";
        public bool Qualification { get; set; }
        public string JobId { get; set; } = "";
        public string Owner { get; set; } = "";
        public long LeaseVersion { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
    }

    public sealed class SentToken
    {
        public DateTimeOffset SentAtUtc { get; set; }
        public bool Qualification { get; set; }
    }

    public sealed class ActiveQualification
    {
        public string JobId { get; set; } = "";
        public string Owner { get; set; } = "";
        public long LeaseVersion { get; set; }
        public DateTimeOffset DeadlineUtc { get; set; }
        public DateTimeOffset LeaseUntilUtc { get; set; }
        public int QualificationLimit { get; set; } = 1;
        public int OtherLimit { get; set; } = 4;
    }

    public AdmissionDecision ReadDecision(DateTimeOffset nowUtc, bool qualification)
    {
        EnsureUtc(nowUtc);
        var state = Read();
        var sent = Current(state, nowUtc);
        state.Pending.RemoveAll(row => row.ExpiresUtc <= nowUtc);
        var active = state.ActiveQualification is { } job && job.DeadlineUtc > nowUtc;
        var capacity = !active ? WindowCapacity : qualification
            ? Math.Clamp(state.ActiveQualification!.QualificationLimit, 0, WindowCapacity)
            : Math.Clamp(state.ActiveQualification!.OtherLimit, 0, WindowCapacity);
        var pending = state.Pending;
        var inLane = active ? sent.Count(row => row.Qualification == qualification) + pending.Count(row => row.Qualification == qualification)
            : sent.Count + pending.Count;
        var eligible = inLane < capacity && sent.Count + pending.Count < WindowCapacity;
        var next = eligible ? TimeSpan.Zero : NextLaneAvailability(state, nowUtc, qualification, active);
        return new AdmissionDecision(eligible, next < TimeSpan.Zero ? TimeSpan.Zero : next,
            sent.Count + pending.Count, active, state.ActiveQualification?.JobId);
    }

    /// <summary>Atomically records a send at the request admission point; SQL errors propagate closed.</summary>
    public bool TryRecordSend(DateTimeOffset nowUtc, bool qualification, out AdmissionDecision decision)
    {
        EnsureUtc(nowUtc);
        var result = blob.MutateWithContext((_, raw) =>
        {
            var transactionNowUtc = TransactionNowUtc();
            var state = Parse(raw);
            Prune(state, transactionNowUtc);
            state.Pending.RemoveAll(row => row.ExpiresUtc <= transactionNowUtc);
            if (state.ActiveQualification is { } expired && expired.DeadlineUtc <= transactionNowUtc)
            {
                state.Pending.RemoveAll(row => row.JobId == expired.JobId);
                state.ActiveQualification = null;
            }
            var active = state.ActiveQualification is { } job && job.DeadlineUtc > transactionNowUtc;
            var laneLimit = !active ? WindowCapacity : qualification
                ? Math.Clamp(state.ActiveQualification!.QualificationLimit, 0, WindowCapacity)
                : Math.Clamp(state.ActiveQualification!.OtherLimit, 0, WindowCapacity);
            var laneCount = active
                ? state.Sent.Count(row => row.Qualification == qualification) + state.Pending.Count(row => row.Qualification == qualification)
                : state.Sent.Count + state.Pending.Count;
            var allowed = laneCount < laneLimit && state.Sent.Count + state.Pending.Count < WindowCapacity;
            var wait = allowed ? TimeSpan.Zero : NextLaneAvailability(state, transactionNowUtc, qualification, active);
            if (allowed) state.Sent.Add(new SentToken { SentAtUtc = transactionNowUtc, Qualification = qualification });
            var updated = Serialize(state);
            return (updated, (Allowed: allowed, Wait: wait < TimeSpan.Zero ? TimeSpan.Zero : wait,
                Count: state.Sent.Count + state.Pending.Count, Active: active, JobId: state.ActiveQualification?.JobId));
        }, MaximumJsonBytes, System.Data.IsolationLevel.Serializable);
        decision = new AdmissionDecision(result.Allowed, result.Wait, result.Count, result.Active, result.JobId);
        return result.Allowed;
    }

    public bool TryReserveQualification(string jobId, string owner, long leaseVersion,
        DateTimeOffset nowUtc, out string ticketId, out AdmissionDecision decision)
    {
        EnsureUtc(nowUtc);
        var id = Guid.NewGuid().ToString("N");
        var result = blob.MutateWithContext((_, raw) =>
        {
            var transactionNowUtc = TransactionNowUtc();
            var state = Parse(raw);
            Prune(state, transactionNowUtc);
            state.Pending.RemoveAll(row => row.ExpiresUtc <= transactionNowUtc);
            var job = state.ActiveQualification;
            if (job is null || job.JobId != jobId || job.Owner != owner || job.LeaseVersion != leaseVersion ||
                job.DeadlineUtc <= transactionNowUtc || job.LeaseUntilUtc <= transactionNowUtc)
                throw new InvalidOperationException("qualification-historic-reservation-job-fence-changed");
            var qual = state.Sent.Count(row => row.Qualification) + state.Pending.Count(row => row.Qualification);
            var total = state.Sent.Count + state.Pending.Count;
            var allowed = qual < job.QualificationLimit && total < WindowCapacity && state.Pending.Count < 4;
            var wait = allowed ? TimeSpan.Zero : NextLaneAvailability(state, transactionNowUtc, qualification: true, active: true);
            if (allowed) state.Pending.Add(new PendingToken
            {
                Id = id, Qualification = true, JobId = jobId, Owner = owner,
                LeaseVersion = leaseVersion,
                ExpiresUtc = Min(Min(transactionNowUtc + TimeSpan.FromSeconds(90), job.DeadlineUtc), job.LeaseUntilUtc)
            });
            return (Serialize(state), (Allowed: allowed, Wait: wait < TimeSpan.Zero ? TimeSpan.Zero : wait,
                Count: total + (allowed ? 1 : 0), Active: true, JobId: jobId));
        }, MaximumJsonBytes, System.Data.IsolationLevel.Serializable);
        ticketId = result.Allowed ? id : "";
        decision = new AdmissionDecision(result.Allowed, result.Wait, result.Count, result.Active, result.JobId);
        return result.Allowed;
    }

    public bool TryReserveOther(DateTimeOffset nowUtc, out string ticketId, out AdmissionDecision decision)
    {
        EnsureUtc(nowUtc);
        var id = Guid.NewGuid().ToString("N");
        var result = blob.MutateWithContext((_, raw) =>
        {
            var transactionNowUtc = TransactionNowUtc();
            var state = Parse(raw);
            Prune(state, transactionNowUtc);
            state.Pending.RemoveAll(row => row.ExpiresUtc <= transactionNowUtc);
            if (state.ActiveQualification is { } expired && expired.DeadlineUtc <= transactionNowUtc)
            {
                state.Pending.RemoveAll(row => row.JobId == expired.JobId);
                state.ActiveQualification = null;
            }
            var active = state.ActiveQualification is { } job && job.DeadlineUtc > transactionNowUtc;
            var total = state.Sent.Count + state.Pending.Count;
            var other = state.Sent.Count(row => !row.Qualification) + state.Pending.Count(row => !row.Qualification);
            var allowed = total < WindowCapacity && (!active || other < state.ActiveQualification!.OtherLimit) &&
                state.Pending.Count < WindowCapacity;
            if (allowed) state.Pending.Add(new PendingToken
            {
                Id = id, Qualification = false, ExpiresUtc = transactionNowUtc + TimeSpan.FromSeconds(90)
            });
            var wait = allowed ? TimeSpan.Zero : NextLaneAvailability(state, transactionNowUtc, qualification: false, active);
            return (Serialize(state), (Allowed: allowed, Wait: wait,
                Count: total + (allowed ? 1 : 0), Active: active, JobId: state.ActiveQualification?.JobId));
        }, MaximumJsonBytes, System.Data.IsolationLevel.Serializable);
        ticketId = result.Allowed ? id : "";
        decision = new AdmissionDecision(result.Allowed, result.Wait, result.Count, result.Active, result.JobId);
        return result.Allowed;
    }

    public void ConsumeReservation(string ticketId, DateTimeOffset nowUtc)
    {
        EnsureUtc(nowUtc);
        blob.MutateWithContext((_, raw) =>
        {
            var transactionNowUtc = TransactionNowUtc();
            var state = Parse(raw);
            Prune(state, transactionNowUtc);
            var pending = state.Pending.SingleOrDefault(row => row.Id == ticketId)
                ?? throw new InvalidOperationException("qualification-historic-reservation-missing");
            if (pending.ExpiresUtc <= transactionNowUtc || pending.Qualification &&
                (state.ActiveQualification is not { } job || job.JobId != pending.JobId || job.Owner != pending.Owner ||
                 job.LeaseVersion != pending.LeaseVersion || job.DeadlineUtc <= transactionNowUtc ||
                 job.LeaseUntilUtc <= transactionNowUtc))
                throw new InvalidOperationException("qualification-historic-reservation-expired");
            state.Pending.Remove(pending);
            state.Sent.Add(new SentToken { SentAtUtc = transactionNowUtc, Qualification = pending.Qualification });
            return (Serialize(state), true);
        }, MaximumJsonBytes, System.Data.IsolationLevel.Serializable);
    }

    public void ReleaseReservation(string ticketId)
    {
        if (string.IsNullOrWhiteSpace(ticketId)) return;
        blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            state.Pending.RemoveAll(row => row.Id == ticketId);
            return (Serialize(state), true);
        }, MaximumJsonBytes, System.Data.IsolationLevel.Serializable);
    }

    /// <summary>Installed atomically with job start/resume; no arbitrary quota values accepted.</summary>
    public void SetActiveQualification(string jobId, string owner, long leaseVersion,
        DateTimeOffset deadlineUtc, DateTimeOffset leaseUntilUtc, int qualificationLimit, int otherLimit)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64 || string.IsNullOrWhiteSpace(owner) ||
            owner.Length > 64 || leaseVersion <= 0 || deadlineUtc.Offset != TimeSpan.Zero ||
            leaseUntilUtc.Offset != TimeSpan.Zero || leaseUntilUtc > deadlineUtc ||
            qualificationLimit != 1 || otherLimit != 4)
            throw new ArgumentException("Historic qualification quota must be the bounded 1/4 split.");
        blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            state.ActiveQualification = new ActiveQualification
            {
                JobId = jobId, Owner = owner, LeaseVersion = leaseVersion, DeadlineUtc = deadlineUtc,
                LeaseUntilUtc = leaseUntilUtc,
                QualificationLimit = qualificationLimit, OtherLimit = otherLimit
            };
            return (Serialize(state), true);
        }, MaximumJsonBytes, System.Data.IsolationLevel.Serializable);
    }

    public void ClearActiveQualification(string jobId, string owner, long leaseVersion)
    {
        blob.MutateWithContext((_, raw) =>
        {
            var state = Parse(raw);
            var active = state.ActiveQualification;
            if (active?.JobId == jobId && active.Owner == owner && active.LeaseVersion == leaseVersion)
            {
                state.ActiveQualification = null;
                state.Pending.RemoveAll(row => row.JobId == jobId);
            }
            return (Serialize(state), true);
        }, MaximumJsonBytes, System.Data.IsolationLevel.Serializable);
    }

    public State Read()
    {
        var (json, _, length) = blob.ReadBoundedWithVersion(MaximumJsonBytes);
        if (length > MaximumJsonBytes || json is not null && Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("historic-admission-state-cap-exceeded");
        var state = Parse(json);
        ValidateLedger(state, DateTimeOffset.UtcNow);
        return state;
    }

    private static List<SentToken> Current(State state, DateTimeOffset now)
    {
        ValidateLedger(state, now);
        // Keep slightly-future tokens conservatively. They can be another process's send
        // observed during a backward UTC clock step and remain blocking for a full window.
        return state.Sent.Where(row => now < row.SentAtUtc || now - row.SentAtUtc < Window)
            .OrderBy(row => row.SentAtUtc).ToList();
    }
    private static void Prune(State state, DateTimeOffset now) => state.Sent = Current(state, now);
    private static void ValidateLedger(State state, DateTimeOffset now)
    {
        if (state.Sent.Count > WindowCapacity || state.Pending.Count > WindowCapacity ||
            state.Sent.Count + state.Pending.Count > WindowCapacity ||
            state.Sent.Any(row => row.SentAtUtc.Offset != TimeSpan.Zero || row.SentAtUtc > now + Window) ||
            state.Pending.Any(row => string.IsNullOrWhiteSpace(row.Id) || row.ExpiresUtc.Offset != TimeSpan.Zero ||
                row.ExpiresUtc > now + TimeSpan.FromMinutes(2)) ||
            state.Pending.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != state.Pending.Count ||
            state.ActiveQualification is { } active && (string.IsNullOrWhiteSpace(active.JobId) ||
                string.IsNullOrWhiteSpace(active.Owner) || active.LeaseVersion <= 0 ||
                active.DeadlineUtc.Offset != TimeSpan.Zero || active.LeaseUntilUtc.Offset != TimeSpan.Zero ||
                active.LeaseUntilUtc > active.DeadlineUtc || active.QualificationLimit != 1 || active.OtherLimit != 4))
            throw new InvalidDataException("historic-admission-ledger-invalid-or-over-limit");
    }
    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Historic admission timestamps must be UTC.", nameof(value));
    }
    private DateTimeOffset TransactionNowUtc()
    {
        var now = transactionClock();
        EnsureUtc(now);
        return now;
    }
    private static TimeSpan NextLaneAvailability(State state, DateTimeOffset now,
        bool qualification, bool active)
    {
        var total = state.Sent.Count + state.Pending.Count;
        var laneSent = state.Sent.Where(row => !active || row.Qualification == qualification)
            .OrderBy(row => row.SentAtUtc).ToArray();
        var lanePending = state.Pending.Where(row => !active || row.Qualification == qualification).ToArray();
        var laneLimit = active ? qualification
            ? Math.Clamp(state.ActiveQualification!.QualificationLimit, 0, WindowCapacity)
            : Math.Clamp(state.ActiveQualification!.OtherLimit, 0, WindowCapacity)
            : WindowCapacity;
        var fullLane = laneSent.Length + lanePending.Length >= laneLimit;
        var fullTotal = total >= WindowCapacity;
        var times = (fullLane ? laneSent.Select(row => row.SentAtUtc + Window).Concat(lanePending.Select(row => row.ExpiresUtc)) :
            fullTotal ? state.Sent.Select(row => row.SentAtUtc + Window).Concat(state.Pending.Select(row => row.ExpiresUtc)) :
            Enumerable.Empty<DateTimeOffset>()).ToArray();
        if (times.Length == 0) return Window;
        var delay = times.Min() - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(250);
    }
    private static State Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new State();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
            RejectDuplicateProperties(document.RootElement);
            return JsonSerializer.Deserialize<State>(json) ?? throw new InvalidDataException("historic-admission-state-invalid");
        }
        catch (JsonException ex) { throw new InvalidDataException("historic-admission-state-invalid", ex); }
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("historic-admission-duplicate-json-property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
    private static string Serialize(State state)
    {
        if (state.Sent.Count > WindowCapacity || state.Pending.Count > WindowCapacity)
            throw new InvalidDataException("historic-admission-ledger-bound-exceeded");
        var json = JsonSerializer.Serialize(state);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("historic-admission-state-cap-exceeded");
        return json;
    }
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}

public sealed record AdmissionDecision(bool Allowed, TimeSpan Wait, int SentInWindow,
    bool QualificationActive, string? JobId);

public interface IPrtgHistoricRequestCoordinator
{
    Task WaitAndRecordSendAsync(bool qualification, CancellationToken cancellationToken, string? reservationId = null);
    Task<PrtgHistoricReservation> ReserveOtherAsync(CancellationToken cancellationToken);
    Task<PrtgHistoricReservation> ReserveQualificationAsync(string jobId, string owner, long leaseVersion,
        CancellationToken cancellationToken);
    void ReleaseReservation(string reservationId);
}

public sealed class PrtgHistoricReservation(IPrtgHistoricRequestCoordinator coordinator, string id) : IDisposable
{
    private int disposed;
    public string Id { get; } = id;
    internal void TransferOwnership() => Interlocked.Exchange(ref disposed, 1);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) coordinator.ReleaseReservation(Id);
    }
}

/// <summary>SQL-backed coordinator. It intentionally has no local fallback when storage is unavailable.</summary>
public sealed class SqlPrtgHistoricRequestCoordinator(StorageBackend backend) : IPrtgHistoricRequestCoordinator
{
    private readonly PrtgHistoricAdmissionStore store = new(backend);
    public static readonly TimeSpan DefaultMaximumAdmissionWait = TimeSpan.FromMinutes(2);
    private readonly TimeSpan maximumAdmissionWait = DefaultMaximumAdmissionWait;

    internal SqlPrtgHistoricRequestCoordinator(StorageBackend backend, TimeSpan maximumAdmissionWait)
        : this(backend)
    {
        if (maximumAdmissionWait <= TimeSpan.Zero || maximumAdmissionWait > DefaultMaximumAdmissionWait)
            throw new ArgumentOutOfRangeException(nameof(maximumAdmissionWait));
        this.maximumAdmissionWait = maximumAdmissionWait;
    }

    public async Task WaitAndRecordSendAsync(bool qualification, CancellationToken cancellationToken, string? reservationId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reservationId is not null)
        {
            store.ConsumeReservation(reservationId, DateTimeOffset.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allowed = store.TryRecordSend(DateTimeOffset.UtcNow, qualification, out var decision);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfAdmissionWaitExpired(wait);
            if (allowed) return;
            await DelayWithinAdmissionDeadline(wait, decision.Wait, cancellationToken);
        }
    }

    public async Task<PrtgHistoricReservation> ReserveQualificationAsync(string jobId, string owner,
        long leaseVersion, CancellationToken cancellationToken)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (store.TryReserveQualification(jobId, owner, leaseVersion, DateTimeOffset.UtcNow,
                    out var id, out var decision))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfAdmissionWaitExpired(wait);
                    return new PrtgHistoricReservation(this, id);
                }
                catch
                {
                    store.ReleaseReservation(id);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfAdmissionWaitExpired(wait);
            await DelayWithinAdmissionDeadline(wait, decision.Wait, cancellationToken);
        }
    }

    public async Task<PrtgHistoricReservation> ReserveOtherAsync(CancellationToken cancellationToken)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (store.TryReserveOther(DateTimeOffset.UtcNow, out var id, out var decision))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfAdmissionWaitExpired(wait);
                    return new PrtgHistoricReservation(this, id);
                }
                catch
                {
                    store.ReleaseReservation(id);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfAdmissionWaitExpired(wait);
            await DelayWithinAdmissionDeadline(wait, decision.Wait, cancellationToken);
        }
    }

    public void ReleaseReservation(string reservationId) => store.ReleaseReservation(reservationId);

    private void ThrowIfAdmissionWaitExpired(System.Diagnostics.Stopwatch wait)
    {
        if (wait.Elapsed >= maximumAdmissionWait)
            throw new TimeoutException("prtg-historic-admission-wait-timeout");
    }

    private async Task DelayWithinAdmissionDeadline(System.Diagnostics.Stopwatch wait, TimeSpan suggestedDelay,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfAdmissionWaitExpired(wait);
        var remaining = maximumAdmissionWait - wait.Elapsed;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("prtg-historic-admission-wait-timeout");
        var minimumPoll = TimeSpan.FromMilliseconds(250);
        var delay = suggestedDelay > minimumPoll ? suggestedDelay : minimumPoll;
        if (delay > remaining) delay = remaining;
        await Task.Delay(delay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfAdmissionWaitExpired(wait);
    }
}