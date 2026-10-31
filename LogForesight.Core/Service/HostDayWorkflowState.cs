using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Service;

public enum WorkflowLegState { Waiting, Running, Succeeded, Failed, Disabled, Degraded, Deferred, Overdue }

/// <summary>Durable workflow summary for one host and analysis date. Payload is deliberately metadata only.</summary>
public sealed class HostDayWorkflowState
{
    public long HostId { get; set; }
    public string HostName { get; set; } = "";
    public DateTime Date { get; set; }
    public string? ParentRunId { get; set; }
    public long? ParentRecordId { get; set; }
    public WorkflowLegState Parent { get; set; } = WorkflowLegState.Waiting;
    public DateTime? ParentCompletedAt { get; set; }
    public int? ParentEventCount { get; set; }
    public long DecisionVersion { get; set; }
    public string? DecisionFingerprint { get; set; }
    public string? PrtgFingerprint { get; set; }
    public WorkflowLegState Prtg { get; set; } = WorkflowLegState.Waiting;
    public string? PrtgFailure { get; set; }
    public DateTime? PrtgAttemptAtUtc { get; set; }
    public long PrtgEvidenceVersion { get; set; }
    public DateTime? PrtgEvidenceReadyAt { get; set; }
    public DateTime? SupplementDueAt { get; set; }
    /// <summary>Scope of a whole Daily-qualified evidence pass, persisted before formal manifest attachment.</summary>
    public long? RecoveryWaitingRecordId { get; set; }
    public long? RecoveryWaitingWriteRevision { get; set; }
    public string? RecoveryWaitingReason { get; set; }
    public int? RecoveryWaitingReportedCharacters { get; set; }
    public DateTime? RecoveryWaitingCapturedAtUtc { get; set; }
    /// <summary>標記恢復等待前的 PRTG 腿；恢復只負責解除等待，不重判 PRTG，解除時還原這個狀態。</summary>
    public WorkflowLegState? PrtgBeforeRecoveryWaiting { get; set; }
    public WorkflowLegState Ai { get; set; } = WorkflowLegState.Waiting;
    public long? AiInputVersion { get; set; }
    public long? AiResultVersion { get; set; }
    public string? AiFailure { get; set; }
    public string[] CaseIntents { get; set; } = [];
    public string[] CaseDeliveredIntents { get; set; } = [];
    public string[] CaseFailedIntents { get; set; } = [];
    public string[] CaseSkippedIntents { get; set; } = [];
    /// <summary>Bounded, durable linearization claims for automatic formal CPU/memory case starts.</summary>
    public Dictionary<string, string> CaseDispositionReasons { get; set; } = new(StringComparer.Ordinal);
    public bool CaseTrackingIncomplete { get; set; }
    public bool CasePlanClosed { get; set; }
    public WorkflowLegState CaseState { get; set; } = WorkflowLegState.Waiting;
    public string[] MailIntents { get; set; } = [];
    public string[] MailRequiredLanes { get; set; } = [];
    public Dictionary<string, string[]> MailExpectedPartsByIntent { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> MailDeliveredPartsByIntent { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> MailFailedPartsByIntent { get; set; } = new(StringComparer.Ordinal);
    public string[] MailDeliveredParts { get; set; } = [];
    public string[] MailFailedParts { get; set; } = [];
    public bool MailTrackingIncomplete { get; set; }
    public bool MailPlanClosed { get; set; }
    public string? MailPlanReason { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsComplete => Parent == WorkflowLegState.Succeeded &&
        Prtg is WorkflowLegState.Succeeded or WorkflowLegState.Disabled or WorkflowLegState.Degraded &&
        Ai is WorkflowLegState.Succeeded or WorkflowLegState.Disabled or WorkflowLegState.Failed &&
        CaseIsComplete &&
        MailIsComplete && !IsRecoveryWaiting;

    public bool IsRecoveryWaiting => RecoveryWaitingRecordId.HasValue &&
        RecoveryWaitingWriteRevision.HasValue && !string.IsNullOrWhiteSpace(RecoveryWaitingReason);

    /// <summary>PRTG 腿的就緒狀態：該主機日的 PRTG 評估完成即就緒（資料不足的 sensor 原因另見數值就緒度，不阻擋主機日）。</summary>
    public string PrtgReadinessState => IsRecoveryWaiting ? "recovery-waiting" : Prtg == WorkflowLegState.Disabled ? "not-required" :
        Prtg is WorkflowLegState.Succeeded or WorkflowLegState.Degraded ? "ready" : "waiting";

    public bool CaseIsComplete => CasePlanClosed && !CaseTrackingIncomplete && CaseState != WorkflowLegState.Failed &&
        (CaseIntents.Length == 0 || CaseIntents.All(intent =>
            (CaseDeliveredIntents.Contains(intent, StringComparer.Ordinal) || CaseSkippedIntents.Contains(intent, StringComparer.Ordinal)) &&
            !CaseFailedIntents.Contains(intent, StringComparer.Ordinal)));

    public string WorkflowOutcome => Parent == WorkflowLegState.Failed ? "failed" : IsRecoveryWaiting ? "pending" :
        (Prtg is WorkflowLegState.Degraded or WorkflowLegState.Failed || Ai == WorkflowLegState.Failed ||
            CaseState is WorkflowLegState.Failed or WorkflowLegState.Degraded || CaseTrackingIncomplete || MailTrackingIncomplete)
            ? "partial" : IsComplete ? "complete" : "pending";

    public bool MailIsComplete => MailPlanClosed && (MailRequiredLanes.Length == 0 || !MailTrackingIncomplete &&
        MailRequiredLanes.All(lane =>
            MailIntents.Where(intent => intent.StartsWith(lane + ":", StringComparison.Ordinal)).Any() &&
            MailIntents.Where(intent => intent.StartsWith(lane + ":", StringComparison.Ordinal)).All(intent =>
                MailExpectedPartsByIntent.TryGetValue(intent, out var expected) && expected.Length > 0 &&
                MailDeliveredPartsByIntent.TryGetValue(intent, out var delivered) &&
                expected.All(value => delivered.Contains(value, StringComparer.Ordinal)) &&
                (!MailFailedPartsByIntent.TryGetValue(intent, out var failed) || failed.Length == 0))));
}

public sealed record HostDayWorkflowVersion(long ParentRecordId, long DecisionVersion);

public sealed record HostDayWorkflowKeyPage(IReadOnlyList<HostDayWorkflowKey> Items, string? NextCursor, int RawCount);

/// <summary>
/// Per-host-day durable workflow ledger. Each row is one bounded JSON blob, so writes for unrelated days
/// never rewrite a growing global collection. All state changes use an atomic blob mutation.
/// </summary>
public sealed class HostDayWorkflowStore(StorageBackend backend)
{
    public const string RecoveryCheckpointBlobKey = "workflow_recovery_cursor";
    private const int MaxMetadataChars = 64 * 1024;

    public HostDayWorkflowState? Get(long hostId, DateTime date)
    {
        var (raw, _, length) = Blob(hostId, date).ReadBoundedWithVersion(MaxMetadataChars);
        if (length > MaxMetadataChars || raw != null && Encoding.UTF8.GetByteCount(raw) > MaxMetadataChars)
            throw new InvalidDataException("Host-day workflow metadata exceeded its size limit.");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return JsonSerializer.Deserialize<HostDayWorkflowState>(raw, LfJsonOptions.Pretty);
    }

    public HostDayWorkflowKeyPage GetExistingHostDaysPage(string? afterKey, int limit)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var ctx = backend.CreateContext();
        var keys = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey.StartsWith("workflow_") &&
                row.BlobKey != RecoveryCheckpointBlobKey &&
                (afterKey == null || string.Compare(row.BlobKey, afterKey) > 0))
            .OrderBy(row => row.BlobKey).Select(row => row.BlobKey).Take(limit).ToList();
        var result = new List<HostDayWorkflowKey>(keys.Count);
        foreach (var key in keys)
        {
            var parts = key.AsSpan("workflow_".Length).ToString().Split('_');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var hostId) ||
                !DateTime.TryParseExact(parts[1], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
            {
                LogManager.GetCurrentClassLogger().Warn("Skipping malformed workflow blob key {BlobKey} while advancing recovery cursor", key);
                continue;
            }
            result.Add(new HostDayWorkflowKey(key, hostId, date.Date));
        }
        return new HostDayWorkflowKeyPage(result, keys.LastOrDefault(), keys.Count);
    }

    /// <summary>以 (hostId, date) 組出的精確 blob key 刪除該主機日的工作流狀態；不做前綴刪除，因此不會碰到復原游標。</summary>
    public bool Delete(long hostId, DateTime date)
    {
        var key = "workflow_" + Key(hostId, date);
        var deleted = false;
        var acquired = WorkflowMutex(hostId, date).RunExclusive(() =>
        {
            using var ctx = backend.CreateContext();
            var row = ctx.Blobs.SingleOrDefault(value => value.BlobKey == key);
            if (row == null) return;
            ctx.Blobs.Remove(row);
            ctx.SaveChanges();
            deleted = true;
        }, TimeSpan.FromSeconds(30), runActionWhenNotAcquired: false);
        if (!acquired) throw new TimeoutException("Could not acquire the host-day workflow write lock.");
        return deleted;
    }

    public HostDayWorkflowState Update(long hostId, DateTime date, Action<HostDayWorkflowState> change)
    {
        var blob = Blob(hostId, date);
        var mutex = WorkflowMutex(hostId, date);
        HostDayWorkflowState? updated = null;
        var acquired = mutex.RunExclusive(() =>
        {
            updated = blob.MutateWithContext((_, raw) =>
            {
                if (raw != null && (raw.Length > MaxMetadataChars || Encoding.UTF8.GetByteCount(raw) > MaxMetadataChars))
                    throw new InvalidDataException("Host-day workflow metadata exceeded its size limit.");
                var state = string.IsNullOrWhiteSpace(raw)
                    ? new HostDayWorkflowState { HostId = hostId, Date = date.Date }
                    : JsonSerializer.Deserialize<HostDayWorkflowState>(raw, LfJsonOptions.Pretty) ?? throw new InvalidDataException("Host-day workflow metadata is invalid.");
                change(state);
                state.UpdatedAt = DateTime.Now;
                var json = JsonSerializer.Serialize(state, LfJsonOptions.Pretty);
                if (json.Length > MaxMetadataChars || Encoding.UTF8.GetByteCount(json) > MaxMetadataChars)
                    throw new InvalidDataException("Host-day workflow metadata exceeded its size limit.");
                return (json, state);
            }, maxCurrentCharacters: MaxMetadataChars);
        }, TimeSpan.FromSeconds(30), runActionWhenNotAcquired: false);
        if (!acquired || updated is null) throw new TimeoutException("Could not acquire the host-day workflow write lock.");
        return updated;
    }

    /// <summary>Atomically applies recovery only while the authoritative SQL parent still has the captured revision.</summary>
    internal bool TryUpdateForRecovery(long hostId, DateTime date, long recordId, long capturedWriteRevision,
        Action<HostDayWorkflowState> change)
    {
        if (hostId <= 0 || recordId <= 0 || capturedWriteRevision < 0) return false;
        var blob = Blob(hostId, date);
        var mutex = WorkflowMutex(hostId, date);
        var applied = false;
        try
        {
            var acquired = mutex.RunExclusive(() =>
            {
                blob.MutateWithContext((ctx, raw) =>
                {
                    var stillCurrent = ctx.DailyRecords.AsNoTracking().Any(row => row.RecordId == recordId &&
                        row.HostId == hostId && row.RecordDate == date.Date && row.WriteRevision == capturedWriteRevision);
                    if (!stillCurrent) throw new AuthorityChangedException();
                    if (raw != null && (raw.Length > MaxMetadataChars || Encoding.UTF8.GetByteCount(raw) > MaxMetadataChars))
                        throw new InvalidDataException("Host-day workflow metadata exceeded its size limit.");
                    var state = string.IsNullOrWhiteSpace(raw)
                        ? new HostDayWorkflowState { HostId = hostId, Date = date.Date }
                        : JsonSerializer.Deserialize<HostDayWorkflowState>(raw, LfJsonOptions.Pretty) ?? throw new InvalidDataException("Host-day workflow metadata is invalid.");
                    change(state);
                    var unchanged = JsonSerializer.Serialize(state, LfJsonOptions.Pretty);
                    if (raw != null && StringComparer.Ordinal.Equals(raw, unchanged))
                    {
                        applied = true;
                        return (raw, true);
                    }
                    state.UpdatedAt = DateTime.Now;
                    var json = JsonSerializer.Serialize(state, LfJsonOptions.Pretty);
                    if (json.Length > MaxMetadataChars || Encoding.UTF8.GetByteCount(json) > MaxMetadataChars)
                        throw new InvalidDataException("Host-day workflow metadata exceeded its size limit.");
                    applied = true;
                    return (json, true);
                }, maxCurrentCharacters: MaxMetadataChars, isolationLevel: IsolationLevel.Serializable,
                    skipUnchangedContent: true);
            }, TimeSpan.FromSeconds(30), runActionWhenNotAcquired: false);
            if (!acquired) throw new TimeoutException("Could not acquire the host-day workflow write lock.");
        }
        catch (AuthorityChangedException) { return false; }
        return applied;
    }

    /// <summary>
    /// Clears a recovery orphan only when the parent is still absent inside retention. The parent
    /// absence predicate and blob mutation share a serializable transaction, fenced by the same
    /// per-host-day mutex used by all workflow writers.
    /// </summary>
    internal bool TryUpdateForOrphanRecovery(long hostId, DateTime date, DateTime retentionFrom,
        DateTime retentionTo, Action<HostDayWorkflowState> change)
    {
        if (hostId <= 0) return false;
        var blob = Blob(hostId, date);
        var mutex = WorkflowMutex(hostId, date);
        var applied = false;
        try
        {
            var acquired = mutex.RunExclusive(() =>
            {
                blob.MutateWithContext((ctx, raw) =>
                {
                    if (date.Date >= retentionFrom.Date && date.Date <= retentionTo.Date &&
                        ctx.DailyRecords.AsNoTracking().Any(row => row.HostId == hostId && row.RecordDate == date.Date))
                        throw new CurrentParentExistsException();
                    if (string.IsNullOrWhiteSpace(raw)) throw new MissingWorkflowException();
                    if (raw.Length > MaxMetadataChars || Encoding.UTF8.GetByteCount(raw) > MaxMetadataChars)
                        throw new InvalidDataException("Host-day workflow metadata exceeded its size limit.");
                    var state = JsonSerializer.Deserialize<HostDayWorkflowState>(raw, LfJsonOptions.Pretty)
                        ?? throw new InvalidDataException("Host-day workflow metadata is invalid.");
                    change(state);
                    var unchanged = JsonSerializer.Serialize(state, LfJsonOptions.Pretty);
                    if (StringComparer.Ordinal.Equals(raw, unchanged))
                    {
                        applied = true;
                        return (raw, true);
                    }
                    state.UpdatedAt = DateTime.Now;
                    var json = JsonSerializer.Serialize(state, LfJsonOptions.Pretty);
                    if (json.Length > MaxMetadataChars || Encoding.UTF8.GetByteCount(json) > MaxMetadataChars)
                        throw new InvalidDataException("Host-day workflow metadata exceeded its size limit.");
                    applied = true;
                    return (json, true);
                }, maxCurrentCharacters: MaxMetadataChars, isolationLevel: IsolationLevel.Serializable,
                    skipUnchangedContent: true);
            }, TimeSpan.FromSeconds(30), runActionWhenNotAcquired: false);
            if (!acquired) throw new TimeoutException("Could not acquire the host-day workflow write lock.");
        }
        catch (CurrentParentExistsException) { return false; }
        catch (MissingWorkflowException) { return true; }
        return applied;
    }

    /// <summary>Publishes a visible waiting marker only while the exact bounded SQL row is current.</summary>
    internal bool TryMarkRecoveryWaiting(long hostId, DateTime date, long recordId, long capturedWriteRevision,
        DateTime capturedAtUtc, string reasonCode, int reportedCharacters)
    {
        if (hostId <= 0 || recordId <= 0 || capturedWriteRevision < 0 ||
            capturedAtUtc.Kind != DateTimeKind.Utc || capturedAtUtc == default ||
            reportedCharacters < 0 || reasonCode is not ("recovery-host-id-invalid" or
                "recovery-payload-over-limit" or "recovery-payload-invalid")) return false;
        var blob = Blob(hostId, date);
        var mutex = WorkflowMutex(hostId, date);
        var applied = false;
        try
        {
            var acquired = mutex.RunExclusive(() =>
            {
                blob.MutateWithContext((ctx, raw) =>
                {
                    var stillCurrent = ctx.DailyRecords.AsNoTracking().Any(row => row.RecordId == recordId &&
                        row.HostId == hostId && row.RecordDate == date.Date && row.WriteRevision == capturedWriteRevision);
                    if (!stillCurrent) throw new AuthorityChangedException();
                    if (raw != null && (raw.Length > MaxMetadataChars || Encoding.UTF8.GetByteCount(raw) > MaxMetadataChars))
                        throw new InvalidDataException("Host-day workflow metadata exceeded its size limit.");
                    var state = string.IsNullOrWhiteSpace(raw)
                        ? new HostDayWorkflowState { HostId = hostId, Date = date.Date }
                        : JsonSerializer.Deserialize<HostDayWorkflowState>(raw, LfJsonOptions.Pretty)
                            ?? throw new InvalidDataException("Host-day workflow metadata is invalid.");
                    if (state.HostId != 0 && state.HostId != hostId ||
                        state.Date != default && state.Date.Date != date.Date ||
                        state.ParentRecordId is { } knownParent && knownParent != recordId)
                        throw new AuthorityChangedException();
                    if (IsAfter(state.PrtgAttemptAtUtc, capturedAtUtc) || IsAfter(state.ParentCompletedAt, capturedAtUtc))
                        throw new AuthorityChangedException();
                    if (state.RecoveryWaitingRecordId == recordId &&
                        state.RecoveryWaitingWriteRevision == capturedWriteRevision &&
                        state.RecoveryWaitingReason == reasonCode && state.Prtg == WorkflowLegState.Waiting)
                    {
                        applied = true;
                        return (raw ?? JsonSerializer.Serialize(state, LfJsonOptions.Pretty), true);
                    }
                    if (!state.IsRecoveryWaiting) state.PrtgBeforeRecoveryWaiting = state.Prtg;
                    state.RecoveryWaitingRecordId = recordId;
                    state.RecoveryWaitingWriteRevision = capturedWriteRevision;
                    state.RecoveryWaitingReason = reasonCode;
                    state.RecoveryWaitingReportedCharacters = reportedCharacters;
                    state.RecoveryWaitingCapturedAtUtc = capturedAtUtc;
                    state.Prtg = WorkflowLegState.Waiting;
                    state.PrtgFailure = reasonCode;
                    state.PrtgAttemptAtUtc = DateTime.UtcNow;
                    state.UpdatedAt = DateTime.Now;
                    var json = JsonSerializer.Serialize(state, LfJsonOptions.Pretty);
                    if (json.Length > MaxMetadataChars || Encoding.UTF8.GetByteCount(json) > MaxMetadataChars)
                        throw new InvalidDataException("Host-day workflow metadata exceeded its size limit.");
                    applied = true;
                    return (json, true);
                }, maxCurrentCharacters: MaxMetadataChars, isolationLevel: IsolationLevel.Serializable,
                    skipUnchangedContent: true);
            }, TimeSpan.FromSeconds(30), runActionWhenNotAcquired: false);
            if (!acquired) throw new TimeoutException("Could not acquire the host-day workflow write lock.");
        }
        catch (AuthorityChangedException) { return false; }
        return applied;
    }

    private static bool IsAfter(DateTime? value, DateTime capturedAtUtc) =>
        value is { } instant && instant.ToUniversalTime() > capturedAtUtc;

    private EfJsonBlobStore Blob(long hostId, DateTime date)
    {
        return backend.Blob("workflow_" + Key(hostId, date));
    }

    private static NamedMutexGate WorkflowMutex(long hostId, DateTime date) =>
        new("Global\\LogForesight.Workflow." + Key(hostId, date));

    private static string Key(long hostId, DateTime date) => $"{hostId}_{date:yyyyMMdd}";

    private sealed class AuthorityChangedException : Exception { }
    private sealed class CurrentParentExistsException : Exception { }
    private sealed class MissingWorkflowException : Exception { }
}

public sealed record HostDayWorkflowKey(string BlobKey, long HostId, DateTime Date);

/// <summary>Transition API used by source, AI and delivery consumers.</summary>
public sealed class HostDayWorkflowService(HostDayWorkflowStore store)
{
    private static readonly TimeSpan SupplementGrace = TimeSpan.FromMinutes(15);
    private readonly Dictionary<DateTime, HashSet<long>> _touched = new();
    private readonly object _touchedGate = new();

    public HostDayWorkflowState ParentSucceeded(long hostId, string hostName, DateTime date, string? runId,
        int eventCount, string decisionFingerprint, bool prtgEnabled, bool aiEnabled, DateTime? now = null,
        bool trackTouched = true, bool preserveMissingCompletionTime = false, long? parentRecordId = null)
    {
        if (trackTouched)
        {
            lock (_touchedGate)
            {
                if (!_touched.TryGetValue(date.Date, out var hosts)) _touched[date.Date] = hosts = new HashSet<long>();
                hosts.Add(hostId);
            }
        }
        return store.Update(hostId, date, state =>
        {
            state.HostName = hostName;
            state.ParentRunId = runId;
            var parentIdentityChanged = parentRecordId.HasValue && state.ParentRecordId != parentRecordId;
            if (parentRecordId.HasValue) state.ParentRecordId = parentRecordId;
            state.Parent = WorkflowLegState.Succeeded;
            state.ParentEventCount = Math.Max(0, eventCount);
            var changed = parentIdentityChanged || !string.Equals(state.DecisionFingerprint, decisionFingerprint, StringComparison.Ordinal);
            if (changed)
                state.ParentCompletedAt = now ?? (preserveMissingCompletionTime ? null : DateTime.Now);
            else if (state.ParentCompletedAt == null && now.HasValue)
                state.ParentCompletedAt = now;
            else if (state.ParentCompletedAt == null && !preserveMissingCompletionTime)
                state.ParentCompletedAt = DateTime.Now;
            state.DecisionFingerprint = decisionFingerprint;
            if (changed)
            {
                state.DecisionVersion = Math.Max(1, state.DecisionVersion + 1);
                ResetDelivery(state);
                state.PrtgFailure = null;
                state.PrtgAttemptAtUtc = null;
                ClearPrtgEvidenceDeadline(state);
                state.Prtg = !prtgEnabled ? WorkflowLegState.Disabled
                    : state.PrtgFingerprint is null ? WorkflowLegState.Waiting : state.Prtg;
                if (!prtgEnabled) state.PrtgEvidenceReadyAt = state.ParentCompletedAt;
                state.SupplementDueAt = null;
                if (prtgEnabled && state.PrtgEvidenceReadyAt is { } evidenceAt)
                    state.SupplementDueAt = (state.ParentCompletedAt > evidenceAt ? state.ParentCompletedAt : evidenceAt) + SupplementGrace;
                state.Ai = aiEnabled ? WorkflowLegState.Waiting : WorkflowLegState.Disabled;
                state.AiInputVersion = null;
                state.AiResultVersion = null;
                state.AiFailure = null;
            }
        });
    }

    public IReadOnlyCollection<long> ParentHostIdsForDay(DateTime date)
    {
        lock (_touchedGate) return _touched.TryGetValue(date.Date, out var hosts) ? hosts.ToArray() : Array.Empty<long>();
    }

    public void ReleaseTouchedDays(IEnumerable<DateTime> dates)
    {
        lock (_touchedGate)
            foreach (var date in dates) _touched.Remove(date.Date);
    }

    public HostDayWorkflowState ParentFailed(long hostId, string hostName, DateTime date, string? runId, string? fingerprint = null) =>
        store.Update(hostId, date, state =>
        {
            state.HostName = hostName;
            state.ParentRunId = runId;
            state.Parent = WorkflowLegState.Failed;
            state.ParentCompletedAt = DateTime.Now;
            state.DecisionFingerprint = fingerprint;
            state.DecisionVersion++;
            ResetDelivery(state);
            state.Prtg = WorkflowLegState.Waiting;
            state.PrtgFailure = null;
            state.PrtgAttemptAtUtc = null;
            state.Ai = WorkflowLegState.Waiting;
            state.AiInputVersion = null;
            state.AiResultVersion = null;
            state.PrtgFingerprint = null;
            state.PrtgEvidenceReadyAt = null;
            state.SupplementDueAt = null;
            ClearPrtgEvidenceDeadline(state);
        });

    public HostDayWorkflowState ParentDeleted(long hostId, DateTime date) =>
        store.Update(hostId, date, state =>
        {
            ApplyParentDeleted(state);
        });

    /// <summary>Recovery orphan deletion guarded by an authoritative in-transaction parent check.</summary>
    public bool ParentDeletedIfMissingOrOutsideRetention(long hostId, DateTime date,
        DateTime retentionFrom, DateTime retentionTo) =>
        store.TryUpdateForOrphanRecovery(hostId, date, retentionFrom, retentionTo, ApplyParentDeleted);

    private static void ApplyParentDeleted(HostDayWorkflowState state)
    {
        ClearRecoveryWaiting(state);
        if (state.Parent == WorkflowLegState.Waiting && state.ParentCompletedAt == null &&
            state.DecisionFingerprint == null && state.PrtgFingerprint == null &&
            state.Prtg == WorkflowLegState.Waiting && state.Ai == WorkflowLegState.Waiting) return;
        state.Parent = WorkflowLegState.Waiting;
        state.ParentRecordId = null;
        state.ParentCompletedAt = null;
        state.Prtg = WorkflowLegState.Waiting;
        state.PrtgFailure = null;
        state.PrtgAttemptAtUtc = null;
        state.Ai = WorkflowLegState.Waiting;
        state.DecisionVersion++;
        ResetDelivery(state);
        state.DecisionFingerprint = null;
        state.PrtgFingerprint = null;
        state.PrtgEvidenceVersion = 0;
        state.PrtgEvidenceReadyAt = null;
        state.SupplementDueAt = null;
        ClearPrtgEvidenceDeadline(state);
    }

    public HostDayWorkflowState SetPrtg(long hostId, DateTime date, WorkflowLegState result,
        bool evidenceReady, string? evidenceFingerprint = null, DateTime? now = null, string? failure = null) =>
        store.Update(hostId, date, state =>
        {
            var instant = now ?? DateTime.Now;
            state.PrtgAttemptAtUtc = DateTime.UtcNow;
            state.PrtgFailure = result is WorkflowLegState.Failed or WorkflowLegState.Degraded or WorkflowLegState.Waiting
                ? failure : null;
            if (result == WorkflowLegState.Waiting && !evidenceReady)
            {
                if (state.PrtgFingerprint != null || state.PrtgEvidenceReadyAt != null || state.Prtg is WorkflowLegState.Succeeded or WorkflowLegState.Degraded)
                {
                    state.DecisionVersion++;
                    ResetDelivery(state);
                    if (state.Ai != WorkflowLegState.Disabled) state.Ai = WorkflowLegState.Waiting;
                    state.AiInputVersion = null;
                    state.AiResultVersion = null;
                    state.AiFailure = null;
                }
                state.PrtgFingerprint = null;
                ClearPrtgEvidenceDeadline(state);
            }
            if (evidenceFingerprint != null && !string.Equals(state.PrtgFingerprint, evidenceFingerprint, StringComparison.Ordinal))
            {
                state.PrtgFingerprint = evidenceFingerprint;
                state.PrtgEvidenceVersion++;
                state.DecisionVersion++;
                ResetDelivery(state);
                if (state.Ai != WorkflowLegState.Disabled) state.Ai = WorkflowLegState.Waiting;
                state.AiInputVersion = null;
                state.AiResultVersion = null;
                state.AiFailure = null;
            }
            state.Prtg = result;
            if (evidenceReady)
            {
                state.PrtgEvidenceReadyAt ??= instant;
                if (state.ParentCompletedAt is { } parentAt)
                    state.SupplementDueAt ??= (parentAt > state.PrtgEvidenceReadyAt.Value ? parentAt : state.PrtgEvidenceReadyAt.Value) + SupplementGrace;
                if (result is WorkflowLegState.Waiting or WorkflowLegState.Deferred)
                    state.Prtg = state.Parent == WorkflowLegState.Succeeded && state.SupplementDueAt is { } due
                        ? instant >= due ? WorkflowLegState.Overdue : WorkflowLegState.Deferred
                        : WorkflowLegState.Waiting;
            }
        });

    public long BeginAi(long hostId, DateTime date) => store.Update(hostId, date, state =>
    {
        if (state.Parent != WorkflowLegState.Succeeded) return;
        state.Ai = WorkflowLegState.Running;
        state.AiInputVersion = state.DecisionVersion;
    }).AiInputVersion ?? 0;

    public HostDayWorkflowState RestoreFromRecord(LogForesight.Core.Models.DailyAnalysisRecord record,
        bool aiEnabled, bool prtgEnabled = true) =>
        ParentSucceeded(record.HostId, record.Host, record.Date, record.LatestNetiqAttemptAtUtc?.ToString("O"),
            record.AuditEventCount, HostDayWorkflowFingerprint.ForParentRecord(record), prtgEnabled, aiEnabled,
            now: record.LatestNetiqAttemptAtUtc is { } parentAt
                ? parentAt.Kind == DateTimeKind.Utc ? parentAt.ToLocalTime() : parentAt
                : null,
            trackTouched: false, preserveMissingCompletionTime: true,
            parentRecordId: record.RecordId > 0 ? record.RecordId : null);

    public bool IsCurrentAiVersion(long hostId, DateTime date, long inputVersion)
    {
        var current = store.Get(hostId, date);
        return current is { Parent: WorkflowLegState.Succeeded } && current.DecisionVersion == inputVersion &&
            current.AiInputVersion == inputVersion;
    }

    public bool CompleteAi(long hostId, DateTime date, long inputVersion, bool success, string? failure = null) =>
        store.Update(hostId, date, state =>
        {
            if (state.Parent != WorkflowLegState.Succeeded || inputVersion != state.DecisionVersion ||
                inputVersion != state.AiInputVersion) return;
            state.Ai = success ? WorkflowLegState.Succeeded : WorkflowLegState.Failed;
            state.AiResultVersion = success ? inputVersion : null;
            state.AiFailure = success ? null : failure;
        }).AiResultVersion == inputVersion && success;

    public HostDayWorkflowState RecordCaseIntents(long hostId, DateTime date, IEnumerable<string> intents, HostDayWorkflowVersion? expectedVersion = null) =>
        store.Update(hostId, date, state =>
        {
            if (expectedVersion != null && !MatchesVersion(state, expectedVersion)) return;
            state.CasePlanClosed = true;
            var hashed = intents.Where(value => !string.IsNullOrWhiteSpace(value)).Select(CaseIntentId);
            var combined = state.CaseIntents.Concat(hashed).Distinct(StringComparer.Ordinal).ToArray();
            if (combined.Length > 64) state.CaseTrackingIncomplete = true;
            state.CaseIntents = combined.Take(64).ToArray();
            state.CaseState = state.CaseTrackingIncomplete ? WorkflowLegState.Degraded :
                state.CaseIntents.Length == 0 ? WorkflowLegState.Succeeded : WorkflowLegState.Deferred;
        });

    public HostDayWorkflowState RecordCaseDelivery(long hostId, DateTime date, bool delivered) =>
        store.Update(hostId, date, state =>
        {
            if (delivered)
            {
                state.CaseDeliveredIntents = Merge(state.CaseDeliveredIntents, state.CaseIntents, 64);
                state.CaseFailedIntents = [];
                state.CaseState = state.CaseTrackingIncomplete ? WorkflowLegState.Degraded : WorkflowLegState.Succeeded;
            }
            else
            {
                if (state.CaseIntents.Length == 0)
                {
                    state.CaseTrackingIncomplete = true;
                    state.CaseState = WorkflowLegState.Degraded;
                    return;
                }
                state.CaseFailedIntents = Merge(state.CaseFailedIntents,
                    state.CaseIntents.Except(state.CaseDeliveredIntents, StringComparer.Ordinal), 64);
                state.CaseState = state.CaseFailedIntents.Length > 0 ? WorkflowLegState.Failed :
                    state.CaseIsComplete ? WorkflowLegState.Succeeded : WorkflowLegState.Deferred;
            }
        });

    public HostDayWorkflowState RecordCaseResults(long hostId, DateTime date,
        IEnumerable<string> expectedIntents, IEnumerable<string> deliveredIntents, IEnumerable<string> failedIntents, HostDayWorkflowVersion? expectedVersion = null) =>
        store.Update(hostId, date, state =>
        {
            if (expectedVersion != null && !MatchesVersion(state, expectedVersion)) return;
            state.CasePlanClosed = true;
            var expected = expectedIntents.Where(value => !string.IsNullOrWhiteSpace(value)).Select(CaseIntentId).ToArray();
            var delivered = deliveredIntents.Where(value => !string.IsNullOrWhiteSpace(value)).Select(CaseIntentId).ToHashSet(StringComparer.Ordinal);
            var failed = failedIntents.Where(value => !string.IsNullOrWhiteSpace(value)).Select(CaseIntentId).ToHashSet(StringComparer.Ordinal);
            var combined = state.CaseIntents.Concat(expected).Distinct(StringComparer.Ordinal).ToArray();
            if (combined.Length > 64 || delivered.Count > 64 || failed.Count > 64) state.CaseTrackingIncomplete = true;
            state.CaseIntents = combined.Take(64).ToArray();
            var expectedSet = state.CaseIntents.ToHashSet(StringComparer.Ordinal);
            var deliveredNow = delivered.Where(expectedSet.Contains).Except(failed, StringComparer.Ordinal).ToArray();
            state.CaseDeliveredIntents = Merge(state.CaseDeliveredIntents, deliveredNow, 64);
            state.CaseFailedIntents = Merge(state.CaseFailedIntents.Except(deliveredNow, StringComparer.Ordinal),
                failed.Where(expectedSet.Contains).Except(state.CaseDeliveredIntents, StringComparer.Ordinal), 64);
            state.CaseState = state.CaseTrackingIncomplete ? WorkflowLegState.Degraded : state.CaseFailedIntents.Length > 0 ? WorkflowLegState.Failed :
                state.CaseIsComplete ? WorkflowLegState.Succeeded : WorkflowLegState.Deferred;
        });

    public HostDayWorkflowState RecordDispatchOutcome(long hostId, DateTime date, NightlyDispatchItemOutcome outcome, HostDayWorkflowVersion? expectedVersion = null)
    {
        if (outcome.Outcome == "delivered")
            return RecordCaseResults(hostId, date, [outcome.SignatureKey], [outcome.SignatureKey], [], expectedVersion);
        if (outcome.Outcome == "failed")
            return RecordCaseResults(hostId, date, [outcome.SignatureKey], [], [outcome.SignatureKey], expectedVersion);
        return store.Update(hostId, date, state =>
        {
            if (expectedVersion != null && !MatchesVersion(state, expectedVersion)) return;
            var intent = CaseIntentId(outcome.SignatureKey);
            if (!state.CaseIntents.Contains(intent, StringComparer.Ordinal))
            {
                if (state.CaseIntents.Length >= 64) { state.CaseTrackingIncomplete = true; return; }
                state.CaseIntents = state.CaseIntents.Append(intent).ToArray();
            }
            if (outcome.Outcome == "skipped")
            {
                state.CaseSkippedIntents = Merge(state.CaseSkippedIntents, [intent], 64);
                state.CaseFailedIntents = state.CaseFailedIntents.Where(value => value != intent).ToArray();
            }
            var reason = outcome.Reason ?? "unknown-disposition";
            state.CaseDispositionReasons[intent] = reason.Length <= 128 ? reason : reason[..128];
            state.CaseState = state.CaseTrackingIncomplete ? WorkflowLegState.Degraded :
                state.CaseFailedIntents.Length > 0 ? WorkflowLegState.Failed :
                state.CaseIsComplete ? WorkflowLegState.Succeeded : WorkflowLegState.Deferred;
        });
    }

    public HostDayWorkflowState RecordCases(long hostId, DateTime date, IEnumerable<string> intents, bool delivered)
    {
        RecordCaseIntents(hostId, date, intents);
        return RecordCaseDelivery(hostId, date, delivered);
    }

    public HostDayWorkflowState RecordMail(long hostId, DateTime date, IEnumerable<string> intents,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> expectedParts,
        IEnumerable<string> deliveredParts, IEnumerable<string> failedParts, HostDayWorkflowVersion? expectedVersion = null) =>
        store.Update(hostId, date, state =>
        {
            if (expectedVersion != null && !MatchesVersion(state, expectedVersion)) return;
            var incomingIntents = intents.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
            // A host/day has one current content intent per lane. When a new intent arrives,
            // discard the prior intent for that lane before recording its expected parts. A
            // retry with the same intent keeps accepted receipts; a changed intent cannot
            // inherit receipts or failures from obsolete content.
            foreach (var intent in incomingIntents)
            {
                var separator = intent.IndexOf(':');
                if (separator <= 0 || intent[..separator] is not ("summary" or "urgent")) continue;
                var lanePrefix = intent[..(separator + 1)];
                foreach (var stale in state.MailIntents.Where(value => value.StartsWith(lanePrefix, StringComparison.Ordinal) && value != intent).ToArray())
                {
                    state.MailIntents = state.MailIntents.Where(value => value != stale).ToArray();
                    state.MailExpectedPartsByIntent.Remove(stale);
                    state.MailDeliveredPartsByIntent.Remove(stale);
                    state.MailFailedPartsByIntent.Remove(stale);
                }
            }
            var allIntents = state.MailIntents.Concat(incomingIntents).Distinct(StringComparer.Ordinal).ToArray();
            if (allIntents.Length > 4) state.MailTrackingIncomplete = true;
            state.MailIntents = allIntents.Take(4).ToArray();
            foreach (var (intent, expectedValues) in expectedParts.Where(pair => state.MailIntents.Contains(pair.Key, StringComparer.Ordinal)).Take(4))
            {
            var expected = expectedValues.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (expected.Length > 32)
                {
                    state.MailTrackingIncomplete = true;
                    expected = expected.Take(31).Append(intent + ":untracked-recipient-overflow").ToArray();
                }
                state.MailExpectedPartsByIntent[intent] = expected;
            }
            var activeIntents = state.MailIntents.ToHashSet(StringComparer.Ordinal);
            foreach (var key in state.MailExpectedPartsByIntent.Keys.Where(key => !activeIntents.Contains(key)).ToArray())
                state.MailExpectedPartsByIntent.Remove(key);
            foreach (var key in state.MailDeliveredPartsByIntent.Keys.Where(key => !activeIntents.Contains(key)).ToArray())
                state.MailDeliveredPartsByIntent.Remove(key);
            foreach (var key in state.MailFailedPartsByIntent.Keys.Where(key => !activeIntents.Contains(key)).ToArray())
                state.MailFailedPartsByIntent.Remove(key);
            var delivered = deliveredParts.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
            var failed = failedParts.Where(value => !string.IsNullOrWhiteSpace(value) && !delivered.Contains(value, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var intent in state.MailIntents)
            {
                var expected = state.MailExpectedPartsByIntent.GetValueOrDefault(intent) ?? [];
            state.MailDeliveredPartsByIntent[intent] = Merge(
                    (state.MailDeliveredPartsByIntent.GetValueOrDefault(intent) ?? [])
                        .Where(part => expected.Contains(part, StringComparer.Ordinal)),
                        delivered.Where(part => expected.Contains(part, StringComparer.Ordinal)), 32);
                state.MailFailedPartsByIntent[intent] = Merge(
                    (state.MailFailedPartsByIntent.GetValueOrDefault(intent) ?? [])
                        .Where(part => expected.Contains(part, StringComparer.Ordinal)).Except(delivered, StringComparer.Ordinal),
                    failed.Where(part => expected.Contains(part, StringComparer.Ordinal)), 32);
            }
            state.MailDeliveredParts = state.MailIntents
                .SelectMany(intent => state.MailDeliveredPartsByIntent.GetValueOrDefault(intent) ?? [])
                .Distinct(StringComparer.Ordinal).Take(1).ToArray();
            state.MailFailedParts = state.MailIntents
                .SelectMany(intent => state.MailFailedPartsByIntent.GetValueOrDefault(intent) ?? [])
                .Distinct(StringComparer.Ordinal).Take(1).ToArray();
        });

    /// <summary>Replace the evaluated recipient/content parts for one mail lane. SMTP receipts for identical
    /// parts survive retries; removed recipients or changed content no longer count toward this version.</summary>
    public HostDayWorkflowState ReplaceMailPlan(long hostId, DateTime date, string lane,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> expectedParts, HostDayWorkflowVersion version) =>
        store.Update(hostId, date, state =>
        {
            if (!MatchesVersion(state, version)) return;
            var prefix = lane + ":";
            var previous = state.MailIntents.Where(intent => intent.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            var replacements = expectedParts.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Take(4).ToDictionary(pair => pair.Key, pair => pair.Value
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Take(32).ToArray(), StringComparer.Ordinal);
            if (expectedParts.Count > 4 || expectedParts.Any(pair => pair.Value.Count > 32)) state.MailTrackingIncomplete = true;
            foreach (var intent in previous.Except(replacements.Keys, StringComparer.Ordinal))
            {
                state.MailIntents = state.MailIntents.Where(value => value != intent).ToArray();
                state.MailExpectedPartsByIntent.Remove(intent);
                state.MailDeliveredPartsByIntent.Remove(intent);
                state.MailFailedPartsByIntent.Remove(intent);
            }
            foreach (var (intent, expected) in replacements)
            {
                if (!state.MailIntents.Contains(intent, StringComparer.Ordinal))
                {
                    if (state.MailIntents.Length >= 4) { state.MailTrackingIncomplete = true; continue; }
                    state.MailIntents = state.MailIntents.Append(intent).ToArray();
                }
                state.MailExpectedPartsByIntent[intent] = expected;
                state.MailDeliveredPartsByIntent[intent] = (state.MailDeliveredPartsByIntent.GetValueOrDefault(intent) ?? [])
                    .Where(expected.Contains).Distinct(StringComparer.Ordinal).Take(32).ToArray();
                state.MailFailedPartsByIntent[intent] = (state.MailFailedPartsByIntent.GetValueOrDefault(intent) ?? [])
                    .Where(expected.Contains).Except(state.MailDeliveredPartsByIntent[intent], StringComparer.Ordinal)
                    .Distinct(StringComparer.Ordinal).Take(32).ToArray();
            }
            state.MailPlanClosed = false;
            state.MailPlanReason = null;
        });

    public bool HasAcceptedMailPart(long hostId, DateTime date, HostDayWorkflowVersion version,
        string intent, string part)
    {
        var state = store.Get(hostId, date);
        return state != null && MatchesVersion(state, version) &&
            state.MailExpectedPartsByIntent.TryGetValue(intent, out var expected) && expected.Contains(part, StringComparer.Ordinal) &&
            state.MailDeliveredPartsByIntent.TryGetValue(intent, out var delivered) && delivered.Contains(part, StringComparer.Ordinal);
    }

    public HostDayWorkflowVersion? CaptureDeliveryVersion(long hostId, DateTime date, long parentRecordId)
        => CaptureDeliveryVersion(hostId, date, parentRecordId, out _);

    public HostDayWorkflowVersion? CaptureDeliveryVersion(long hostId, DateTime date, long parentRecordId,
        out bool recoveryWaiting)
    {
        var state = store.Get(hostId, date);
        recoveryWaiting = state is { IsRecoveryWaiting: true } && state.RecoveryWaitingRecordId == parentRecordId;
        return state is { Parent: WorkflowLegState.Succeeded, IsRecoveryWaiting: false } &&
            parentRecordId > 0 && state.ParentRecordId == parentRecordId
            ? new HostDayWorkflowVersion(parentRecordId, state.DecisionVersion) : null;
    }

    public HostDayWorkflowState CloseMailPlan(long hostId, DateTime date, HostDayWorkflowVersion version,
        IReadOnlyCollection<string> requiredLanes, string reason) => store.Update(hostId, date, state =>
    {
        if (!MatchesVersion(state, version)) return;
        state.MailRequiredLanes = requiredLanes.Distinct(StringComparer.Ordinal).ToArray();
        var hasQualifiedParts = state.MailRequiredLanes.All(lane =>
            state.MailIntents.Where(intent => intent.StartsWith(lane + ":", StringComparison.Ordinal)).Any() &&
            state.MailIntents.Where(intent => intent.StartsWith(lane + ":", StringComparison.Ordinal)).All(intent =>
                state.MailExpectedPartsByIntent.TryGetValue(intent, out var expected) && expected.Length > 0));
        state.MailPlanClosed = state.MailRequiredLanes.Length == 0 || hasQualifiedParts;
        state.MailPlanReason = state.MailPlanClosed ? reason : "no-qualified-recipient-or-current-receipt";
    });

    private static bool MatchesVersion(HostDayWorkflowState state, HostDayWorkflowVersion version) =>
        state.Parent == WorkflowLegState.Succeeded && !state.IsRecoveryWaiting &&
        state.ParentRecordId == version.ParentRecordId &&
        state.DecisionVersion == version.DecisionVersion;

    private static void ClearRecoveryWaiting(HostDayWorkflowState state)
    {
        // 等待期間沒有別的路徑改寫 PRTG 腿時，還原標記前的狀態（PRTG 腿由每日評估寫入，恢復不重判）。
        if (state.Prtg == WorkflowLegState.Waiting && state.PrtgFailure == state.RecoveryWaitingReason &&
            state.PrtgBeforeRecoveryWaiting is { } prior)
            state.Prtg = prior;
        state.PrtgBeforeRecoveryWaiting = null;
        state.RecoveryWaitingRecordId = null;
        state.RecoveryWaitingWriteRevision = null;
        state.RecoveryWaitingReason = null;
        state.RecoveryWaitingReportedCharacters = null;
        state.RecoveryWaitingCapturedAtUtc = null;
        state.PrtgFailure = null;
    }

    private static void ResetDelivery(HostDayWorkflowState state)
    {
        state.CaseIntents = []; state.CaseDeliveredIntents = []; state.CaseFailedIntents = []; state.CaseSkippedIntents = [];
        state.CaseDispositionReasons.Clear(); state.CasePlanClosed = false; state.CaseTrackingIncomplete = false; state.CaseState = WorkflowLegState.Waiting;
        state.MailIntents = []; state.MailRequiredLanes = []; state.MailExpectedPartsByIntent.Clear(); state.MailDeliveredPartsByIntent.Clear();
        state.MailFailedPartsByIntent.Clear(); state.MailDeliveredParts = []; state.MailFailedParts = [];
        state.MailTrackingIncomplete = false; state.MailPlanClosed = false; state.MailPlanReason = null;
    }

    public HostDayWorkflowState? Get(long hostId, DateTime date) => store.Get(hostId, date);

    public bool IsRecoveryWaiting(long hostId, DateTime date, long recordId)
    {
        var state = store.Get(hostId, date);
        return state is { IsRecoveryWaiting: true } && state.RecoveryWaitingRecordId == recordId;
    }

    public bool MarkRecoveryWaiting(WorkflowRecoveryWaitingHostDay waiting) =>
        waiting.HostId > 0 && store.TryMarkRecoveryWaiting(waiting.HostId, waiting.Date, waiting.RecordId,
            waiting.CapturedWriteRevision, waiting.CapturedAtUtc, waiting.ReasonCode,
            waiting.ReportedPayloadCharacters);

    /// <summary>Recovery variant: one host-day mutation fenced by the exact SQL parent revision.</summary>
    internal bool ReconcileAuthorityRecord(LogForesight.Core.Models.DailyAnalysisRecord record,
        long capturedWriteRevision, bool prtgEnabled, bool aiEnabled)
    {
        if (record.HostId <= 0 || record.RecordId <= 0) return false;
        return store.TryUpdateForRecovery(record.HostId, record.Date, record.RecordId, capturedWriteRevision, state =>
        {
            if (state.RecoveryWaitingRecordId == record.RecordId &&
                state.RecoveryWaitingWriteRevision is { } waitingRevision && waitingRevision <= capturedWriteRevision)
                ClearRecoveryWaiting(state);
            if (IsRecoveryStateCurrent(state, record, prtgEnabled, aiEnabled)) return;
            if (record.LatestNetiqAttemptStatus == "failed")
                ApplyRecoveredParentFailure(state, record);
            else
                ApplyRecoveredParentSuccess(state, record, prtgEnabled, aiEnabled);

            // PRTG 腿由每日評估直接寫入（PrtgDailyPipeline）；恢復只校正停用與不合格父紀錄，不重判 PRTG。
            if (record.LatestNetiqAttemptStatus != "failed")
            {
                if (!prtgEnabled)
                {
                    if (state.Prtg != WorkflowLegState.Disabled)
                        ApplyRecoveredPrtg(state, WorkflowLegState.Disabled, false, null, null, null);
                }
                else if (!record.CanSupplementWithPrtg())
                    ApplyRecoveredPrtg(state, WorkflowLegState.Waiting, false, null, null, "parent-not-eligible-for-prtg");
            }

            ApplyRecoveredAi(state, record, aiEnabled);
        });
    }

    private static void ApplyRecoveredParentFailure(HostDayWorkflowState state,
        LogForesight.Core.Models.DailyAnalysisRecord record)
    {
        state.HostName = record.Host;
        state.ParentRunId = null;
        state.Parent = WorkflowLegState.Failed;
        state.ParentRecordId = record.RecordId;
        state.ParentCompletedAt = DateTime.Now;
        state.DecisionFingerprint = HostDayWorkflowFingerprint.ForRecord(record);
        state.ParentEventCount = Math.Max(0, record.AuditEventCount);
        state.DecisionVersion++;
        ResetDelivery(state);
        state.Prtg = WorkflowLegState.Waiting;
        state.PrtgFailure = null;
        state.PrtgAttemptAtUtc = null;
        state.Ai = WorkflowLegState.Waiting;
        state.AiInputVersion = null;
        state.AiResultVersion = null;
        state.AiFailure = null;
        state.PrtgFingerprint = null;
        ClearPrtgEvidenceDeadline(state);
        state.PrtgEvidenceVersion = 0;
    }

    private static void ApplyRecoveredParentSuccess(HostDayWorkflowState state,
        LogForesight.Core.Models.DailyAnalysisRecord record, bool prtgEnabled, bool aiEnabled)
    {
        var fingerprint = HostDayWorkflowFingerprint.ForParentRecord(record);
        var parentAt = record.LatestNetiqAttemptAtUtc is { } instant
            ? instant.Kind == DateTimeKind.Utc ? instant.ToLocalTime() : instant : (DateTime?)null;
        state.HostName = record.Host;
        state.ParentRunId = record.LatestNetiqAttemptAtUtc?.ToString("O");
        var identityChanged = state.ParentRecordId != record.RecordId;
        state.ParentRecordId = record.RecordId;
        state.Parent = WorkflowLegState.Succeeded;
        state.ParentEventCount = Math.Max(0, record.AuditEventCount);
        var changed = identityChanged || !StringComparer.Ordinal.Equals(state.DecisionFingerprint, fingerprint);
        if (changed) state.ParentCompletedAt = parentAt;
        else if (state.ParentCompletedAt == null && parentAt.HasValue) state.ParentCompletedAt = parentAt;
        state.DecisionFingerprint = fingerprint;
        if (!changed) return;
        state.DecisionVersion = Math.Max(1, state.DecisionVersion + 1);
        ResetDelivery(state);
        state.PrtgFailure = null;
        state.PrtgAttemptAtUtc = null;
        ClearPrtgEvidenceDeadline(state);
        state.Prtg = !prtgEnabled ? WorkflowLegState.Disabled : state.PrtgFingerprint is null ? WorkflowLegState.Waiting : state.Prtg;
        if (!prtgEnabled) state.PrtgEvidenceReadyAt = state.ParentCompletedAt;
        state.SupplementDueAt = null;
        if (prtgEnabled && state.PrtgEvidenceReadyAt is { } evidenceAt)
            state.SupplementDueAt = (state.ParentCompletedAt > evidenceAt ? state.ParentCompletedAt : evidenceAt) + SupplementGrace;
        state.Ai = aiEnabled ? WorkflowLegState.Waiting : WorkflowLegState.Disabled;
        state.AiInputVersion = null;
        state.AiResultVersion = null;
        state.AiFailure = null;
    }

    private static void ApplyRecoveredPrtg(HostDayWorkflowState state, WorkflowLegState result, bool evidenceReady,
        string? evidenceFingerprint, DateTime? now, string? failure)
    {
        var instant = now ?? DateTime.Now;
        state.PrtgAttemptAtUtc = DateTime.UtcNow;
        state.PrtgFailure = result is WorkflowLegState.Failed or WorkflowLegState.Degraded or WorkflowLegState.Waiting
            ? failure : null;
        if (result == WorkflowLegState.Waiting && !evidenceReady)
        {
            if (state.PrtgFingerprint != null || state.PrtgEvidenceReadyAt != null || state.Prtg is WorkflowLegState.Succeeded or WorkflowLegState.Degraded)
            {
                state.DecisionVersion++;
                ResetDelivery(state);
                if (state.Ai != WorkflowLegState.Disabled) state.Ai = WorkflowLegState.Waiting;
                state.AiInputVersion = null;
                state.AiResultVersion = null;
                state.AiFailure = null;
            }
            state.PrtgFingerprint = null;
            ClearPrtgEvidenceDeadline(state);
        }
        if (evidenceFingerprint != null && !StringComparer.Ordinal.Equals(state.PrtgFingerprint, evidenceFingerprint))
        {
            state.PrtgFingerprint = evidenceFingerprint;
            state.PrtgEvidenceVersion++;
            state.DecisionVersion++;
            ResetDelivery(state);
            if (state.Ai != WorkflowLegState.Disabled) state.Ai = WorkflowLegState.Waiting;
            state.AiInputVersion = null;
            state.AiResultVersion = null;
            state.AiFailure = null;
        }
        state.Prtg = result;
        if (evidenceReady)
        {
            state.PrtgEvidenceReadyAt ??= instant;
            if (state.ParentCompletedAt is { } parentAt)
                state.SupplementDueAt ??= (parentAt > state.PrtgEvidenceReadyAt.Value ? parentAt : state.PrtgEvidenceReadyAt.Value) + SupplementGrace;
        }
    }

    private static void ApplyRecoveredAi(HostDayWorkflowState state,
        LogForesight.Core.Models.DailyAnalysisRecord record, bool aiEnabled)
    {
        if (record.AiAnalyzed)
        {
            state.Ai = WorkflowLegState.Succeeded;
            state.AiInputVersion = state.DecisionVersion;
            state.AiResultVersion = state.DecisionVersion;
            state.AiFailure = null;
            return;
        }
        var candidate = aiEnabled && !record.DetailPruned && record.RiskLevel != RiskLevels.Low;
        if (candidate)
        {
            var running = state.Ai == WorkflowLegState.Running && state.AiInputVersion == state.DecisionVersion;
            if (!running) state.Ai = WorkflowLegState.Waiting;
            state.AiInputVersion = state.DecisionVersion;
            state.AiResultVersion = null;
            if (!running) state.AiFailure = null;
            return;
        }
        state.Ai = WorkflowLegState.Disabled;
        state.AiInputVersion = null;
        state.AiResultVersion = null;
        state.AiFailure = null;
    }

    private static bool IsRecoveryStateCurrent(HostDayWorkflowState state,
        LogForesight.Core.Models.DailyAnalysisRecord record, bool prtgEnabled, bool aiEnabled)
    {
        var failed = record.LatestNetiqAttemptStatus == "failed";
        var fingerprint = failed ? HostDayWorkflowFingerprint.ForRecord(record) : HostDayWorkflowFingerprint.ForParentRecord(record);
        if (state.Parent != (failed ? WorkflowLegState.Failed : WorkflowLegState.Succeeded) ||
            state.ParentRecordId != record.RecordId ||
            (!failed && state.ParentRunId != record.LatestNetiqAttemptAtUtc?.ToString("O")) ||
            state.ParentEventCount != Math.Max(0, record.AuditEventCount) ||
            state.HostName != record.Host || !StringComparer.Ordinal.Equals(state.DecisionFingerprint, fingerprint)) return false;
        if (failed) return state.Prtg == WorkflowLegState.Waiting && state.Ai == WorkflowLegState.Waiting &&
            state.PrtgFingerprint == null && state.PrtgEvidenceReadyAt == null &&
            state.MailIntents.Length == 0 && state.CaseIntents.Length == 0;
        if (!prtgEnabled)
        {
            if (state.Prtg != WorkflowLegState.Disabled) return false;
        }
        else if (!record.CanSupplementWithPrtg())
        {
            if (state.Prtg != WorkflowLegState.Waiting || state.PrtgFailure != "parent-not-eligible-for-prtg") return false;
        }
        var candidate = aiEnabled && !record.DetailPruned && record.RiskLevel != RiskLevels.Low;
        if (record.AiAnalyzed) return state.Ai == WorkflowLegState.Succeeded && state.AiInputVersion == state.DecisionVersion && state.AiResultVersion == state.DecisionVersion;
        if (candidate) return state.Ai is (WorkflowLegState.Waiting or WorkflowLegState.Running) &&
            state.AiInputVersion == state.DecisionVersion && state.AiResultVersion == null;
        return state.Ai == WorkflowLegState.Disabled && state.AiInputVersion == null && state.AiResultVersion == null;
    }

    private static void ClearPrtgEvidenceDeadline(HostDayWorkflowState state)
    {
        state.PrtgEvidenceReadyAt = null;
        state.SupplementDueAt = null;
        if (state.Prtg == WorkflowLegState.Overdue) state.Prtg = WorkflowLegState.Waiting;
    }

    private static string[] Merge(IEnumerable<string> existing, IEnumerable<string> added, int limit = 256)
    {
        var values = existing.Concat(added).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).Take(limit + 1).ToArray();
        if (values.Length > limit)
            throw new ArgumentOutOfRangeException(nameof(added), "A host-day workflow identity set exceeded its bounded size.");
        return values;
    }

    private static string[] MergeLatest(IEnumerable<string> existing, IEnumerable<string> added, int limit) => Merge(existing, added, limit);

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string CaseIntentId(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class HostDayWorkflowFingerprint
{
    public static string ForParentRecord(LogForesight.Core.Models.DailyAnalysisRecord record)
    {
        var input = JsonSerializer.Serialize(new
        {
            record.RecordId,
            record.HostId,
            record.Host,
            Date = record.Date.Date,
            record.LogSource,
            record.LatestNetiqAttemptStatus,
            record.LatestNetiqAttemptAtUtc,
            record.AuditEventCount,
            record.ErrorCount,
            record.WarningCount,
            record.DataIncomplete,
            record.SecurityLogAvailable,
            record.ChannelsRead,
            record.TrendAlerts,
            CorrelationAlerts = record.CorrelationAlerts.Where(text => !LogForesight.Core.Analysis.PrtgCorroboration.OwnsCorrelation(record, text)).ToArray(),
            record.SuppressedTrendAlerts,
            SuppressedCorrelationAlerts = record.SuppressedCorrelationAlerts.Where(text => !LogForesight.Core.Analysis.PrtgCorroboration.OwnsCorrelation(record, text)).ToArray(),
            record.UncoveredChecks,
            Issues = record.TopIssues.Where(issue => !PrtgFindingMapper.IsPrtg(issue)).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    public static string HashParts(IEnumerable<string?> parts) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts.Select(value => value ?? string.Empty)))));

    public static string PrtgInputFingerprint(LogForesight.Core.Models.DailyAnalysisRecord record) => HashParts([
        // 原為已移除的 PRTG manifest 指紋；保留空位讓既有紀錄的指紋值不變。
        (string?)null,
        PrtgFindingMapper.Fingerprint(record.TopIssues)
    ]);

    private static bool HasSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static string ForRecord(LogForesight.Core.Models.DailyAnalysisRecord record) =>
        ForRecordWithIdentity(record, record.RecordId);

    /// <summary>Full report renderer input with SQL identity normalized for pre-Append drafts; includes the four narrative fields rendered verbatim.</summary>
    public static string ForReportInput(LogForesight.Core.Models.DailyAnalysisRecord record) =>
        HashParts([ForRecordWithIdentity(record, 0), JsonSerializer.Serialize(new[]
        {
            record.Headline, record.Summary, record.TrendAssessment, record.Action
        })]);

    private static string ForRecordWithIdentity(LogForesight.Core.Models.DailyAnalysisRecord record, long recordId)
    {
        var input = JsonSerializer.Serialize(new
        {
            RecordId = recordId,
            record.HostId,
            record.Host,
            Date = record.Date.Date,
            record.LogSource,
            record.LatestNetiqAttemptStatus,
            record.LatestNetiqAttemptAtUtc,
            record.AuditEventCount,
            record.ErrorCount,
            record.WarningCount,
            record.TopIssues,
            record.TrendAlerts,
            record.CorrelationAlerts,
            record.SuppressedTrendAlerts,
            record.SuppressedCorrelationAlerts,
            record.RiskLevel,
            record.RiskBasis,
            record.RiskReview,
            // 原為已移除的 PRTG manifest 指紋；保留同名空欄讓既有紀錄的指紋值不變。
            PrtgEvidenceFingerprint = (string?)null,
            record.DataIncomplete,
            record.SecurityLogAvailable,
            record.ChannelsRead,
            record.UncoveredChecks,
            record.ScreenedTailCount,
            record.ScreeningNotes,
            record.AiAnalyzed,
            record.AiPending,
            record.DetailPruned
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
