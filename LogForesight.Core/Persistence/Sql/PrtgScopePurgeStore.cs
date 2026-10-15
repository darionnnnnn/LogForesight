using System.Text.Json;
using System.Data;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>Durable source versions checked in the same transaction as a human purge batch.</summary>
public sealed record PrtgScopePurgeAuthorizationFence(
    long PermissionRevision, long? UsersRevision, long? GroupsRevision, string? AuthFingerprint);

/// <summary>The immutable sensor and row boundary captured by one manual preview.</summary>
public sealed record PrtgScopePurgePreviewBoundary(
    IReadOnlySet<long> CandidateSensorIds, long ValuesWatermark, long StateChangesWatermark);

/// <summary>Version rows for every durable input that can change the protected scope.</summary>
public sealed record PrtgScopePurgeScopeFence(IReadOnlyDictionary<string, long> Revisions);

/// <summary>Durable preview, ownership and bounded progress for PRTG scope purge.</summary>
public sealed class PrtgScopePurgeStore
{
    public const int BatchSize = 250;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OwnerLeaseLifetime = TimeSpan.FromMinutes(5);
    private const int MaximumCapturedIds = 100_000;
    private static readonly string[] ScopeFenceBlobKeys =
    [
        EfPrtgStore.ScopeRevisionBlobKey,
        EfPrtgStore.HostMapDataRevisionBlobKey,
        EfPrtgStore.CatalogueDataRevisionBlobKey,
        "hosts", "sentinels", "system_settings"
    ];
    private readonly Func<LfDbContext> _contextFactory;

    public PrtgScopePurgeStore(Func<LfDbContext> contextFactory) => _contextFactory = contextFactory;

    /// <summary>Captures scope-source versions around scope computation without loading the source blobs.</summary>
    public string CaptureScopeFenceJson()
    {
        using var ctx = _contextFactory();
        return JsonSerializer.Serialize(CaptureScopeFence(ctx));
    }

    /// <summary>Checks a preview against current source versions while holding the shared scope gate.</summary>
    public bool IsCurrentScopeFence(string jobId, long actorId)
    {
        using var ctx = _contextFactory();
        using var tx = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
        LockScopeMutationGate(ctx);
        var row = ctx.PrtgScopePurgeJobs.AsNoTracking().SingleOrDefault(x => x.JobId == jobId && x.ActorId == actorId);
        var current = row != null && LockScopeFence(ctx, row.ScopeFenceJson);
        tx.Commit();
        return current;
    }

    public PrtgScopePurgeJobRow CreatePreview(long actorId, long permissionRevision, string? authFingerprint,
        string fingerprint, IReadOnlySet<long> keepSet, int monitoredDevices, DateTimeOffset now,
        string? previousJobId = null, string? expectedScopeFenceJson = null)
    {
        if (actorId < 0) throw new ArgumentOutOfRangeException(nameof(actorId));
        if (keepSet.Count == 0) throw new ArgumentException("保留集合不得為空。", nameof(keepSet));
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
            LockScopeMutationGate(ctx);
            PauseExpiredOwner(ctx, now, exceptJobId: null);
            if (expectedScopeFenceJson != null && !LockScopeFence(ctx, expectedScopeFenceJson))
                throw new InvalidOperationException("監看範圍在計算期間已變更，請重新預覽。");
            if (previousJobId != null)
            {
                var previous = ctx.PrtgScopePurgeJobs.AsNoTracking().SingleOrDefault(x => x.JobId == previousJobId);
                if (previous == null || previous.ActorId != actorId || previous.Source != "manual" || previous.Status != "stopped"
                    || !string.Equals(previous.AuthFingerprint, authFingerprint, StringComparison.Ordinal)
                    || previous.DeletedValues + previous.DeletedStateChanges == 0)
                    throw new InvalidOperationException("前次部分清除作業不存在或不屬於目前操作者。");
            }
            var row = NewJob(ctx, actorId, null, "manual", "preview", fingerprint, keepSet,
                monitoredDevices, permissionRevision, authFingerprint, now);
            row.PreviousJobId = previousJobId;
            ctx.PrtgScopePurgeJobs.Add(row);
            ctx.SaveChanges();
            tx.Commit();
            return row;
        });
    }

    /// <summary>Creates or returns the same automatic child job for a verified parent operation.</summary>
    public PrtgScopePurgeJobRow CreateAutomatic(string parentJobId, string fingerprint,
        IReadOnlySet<long> keepSet, int monitoredDevices, DateTimeOffset now, string? expectedScopeFenceJson = null)
    {
        if (string.IsNullOrWhiteSpace(parentJobId)) throw new ArgumentException("父作業識別不得為空。", nameof(parentJobId));
        if (keepSet.Count == 0) throw new ArgumentException("保留集合不得為空。", nameof(keepSet));
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
            LockScopeMutationGate(ctx);
            PauseExpiredOwner(ctx, now, exceptJobId: null);
            var existing = ctx.PrtgScopePurgeJobs.SingleOrDefault(x => x.Source == "automatic" && x.ParentJobId == parentJobId);
            if (existing != null) { tx.Commit(); return existing; }
            if (expectedScopeFenceJson != null && !LockScopeFence(ctx, expectedScopeFenceJson))
                throw new InvalidOperationException("監看範圍在計算期間已變更，拒絕自動清除。");
            var occupied = ctx.PrtgScopePurgeJobs.AsNoTracking().Any(x => x.ActiveOwner == "active");
            if (occupied) throw new InvalidOperationException("另一個範圍外清除作業正在執行。");
            var row = NewJob(ctx, actorId: 0, parentJobId, "automatic", "accepted", fingerprint,
                keepSet, monitoredDevices, permissionRevision: 0, authFingerprint: null, now);
            row.ActiveOwner = "active";
            row.LeaseExpiresAtUtc = LeaseExpiresAt(now);
            ctx.PrtgScopePurgeJobs.Add(row);
            try { ctx.SaveChanges(); }
            catch (DbUpdateException) { throw new InvalidOperationException("另一個範圍外清除作業正在執行。"); }
            tx.Commit();
            return row;
        });
    }

    private static PrtgScopePurgeJobRow NewJob(LfDbContext ctx, long actorId, string? parentJobId,
        string source, string status, string fingerprint, IReadOnlySet<long> keepSet,
        int monitoredDevices, long permissionRevision, string? authFingerprint, DateTimeOffset now)
    {
        if (keepSet.Count > MaximumCapturedIds)
            throw new InvalidOperationException($"保留集合超過預覽上限 {MaximumCapturedIds}，未建立清除作業。");
        var valueWatermark = ctx.PrtgValues.Select(x => (long?)x.Id).Max() ?? 0;
        var stateWatermark = ctx.PrtgStateChanges.Select(x => (long?)x.Id).Max() ?? 0;
        var candidateSensors = CaptureCandidateSensorIds(ctx, keepSet, valueWatermark, stateWatermark);
        var candidateValues = CountCandidates(ctx, values: true, candidateSensors, valueWatermark);
        var candidateStates = CountCandidates(ctx, values: false, candidateSensors, stateWatermark);
        return new PrtgScopePurgeJobRow
        {
            JobId = Guid.NewGuid().ToString("N"), ActorId = actorId, ParentJobId = parentJobId,
            Source = source, Status = status,
            KeepSetJson = JsonSerializer.Serialize(keepSet.OrderBy(x => x)), ScopeFingerprint = fingerprint,
            CandidateSensorIdsJson = JsonSerializer.Serialize(candidateSensors.OrderBy(x => x)),
            ScopeFenceJson = JsonSerializer.Serialize(CaptureScopeFence(ctx)),
            PermissionRevision = permissionRevision, AuthFingerprint = authFingerprint,
            AcceptedAtUtc = now.UtcDateTime, UpdatedAtUtc = now.UtcDateTime,
            ValuesWatermark = valueWatermark, StateChangesWatermark = stateWatermark,
            CandidateValues = candidateValues, CandidateStateChanges = candidateStates,
            RemainingValues = candidateValues, RemainingStateChanges = candidateStates,
            CountsReconciled = true,
            MonitoredDevices = monitoredDevices, Revision = 1
        };
    }

    public PrtgScopePurgeJobRow? Get(string jobId)
    {
        using var ctx = _contextFactory();
        return ctx.PrtgScopePurgeJobs.AsNoTracking().SingleOrDefault(x => x.JobId == jobId);
    }

    public PrtgScopePurgePreviewBoundary GetPreviewBoundary(string jobId, long actorId)
    {
        using var ctx = _contextFactory();
        var row = ctx.PrtgScopePurgeJobs.AsNoTracking().SingleOrDefault(x => x.JobId == jobId && x.ActorId == actorId)
            ?? throw new InvalidOperationException("預覽不存在或不屬於目前操作者。");
        if (row.Source != "manual" || row.Status != "preview")
            throw new InvalidOperationException("預覽已不在可檢視狀態，請重新預覽。");
        var ids = JsonSerializer.Deserialize<HashSet<long>>(row.CandidateSensorIdsJson) ?? new();
        if (ids.Count > MaximumCapturedIds)
            throw new InvalidOperationException("預覽候選集合超過安全上限，請重新預覽。");
        return new PrtgScopePurgePreviewBoundary(ids, row.ValuesWatermark, row.StateChangesWatermark);
    }

    public bool HasActiveOwner(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        ReapExpiredOwner(at);
        using var ctx = _contextFactory();
        return ctx.PrtgScopePurgeJobs.AsNoTracking().Any(x => x.ActiveOwner == "active"
            && x.LeaseExpiresAtUtc > at.UtcDateTime);
    }

    /// <summary>CAS-pauses an expired owner; it never transfers ownership or deletes progress.</summary>
    public bool ReapExpiredOwner(DateTimeOffset now)
    {
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
            var changed = PauseExpiredOwner(ctx, now, exceptJobId: null);
            tx.Commit();
            return changed;
        });
    }

    public PrtgScopePurgeJobRow? GetLatestStoppedPartial(long actorId, string? authFingerprint = null)
    {
        using var ctx = _contextFactory();
        return ctx.PrtgScopePurgeJobs.AsNoTracking()
            .Where(x => x.ActorId == actorId && x.Source == "manual" && x.Status == "stopped"
                && (x.DeletedValues > 0 || x.DeletedStateChanges > 0)
                && x.AuthFingerprint == authFingerprint)
            .OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.JobId)
            .FirstOrDefault();
    }

    public PrtgScopePurgeJobRow Accept(string previewId, long actorId, long permissionRevision,
        string? authFingerprint, string expectedFingerprint, DateTimeOffset now)
    {
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
            LockScopeMutationGate(ctx);
            PauseExpiredOwner(ctx, now, exceptJobId: null);
            var row = ctx.PrtgScopePurgeJobs.SingleOrDefault(x => x.JobId == previewId)
                ?? throw new InvalidOperationException("預覽不存在或已失效，請重新預覽。");
            if (row.ActorId != actorId) throw new InvalidOperationException("預覽操作者不符。");
            if (row.Status is "accepted" or "running" or "paused" or "complete")
            {
                tx.Commit();
                return row; // Same accepted preview is an idempotency key.
            }
            if (row.Status != "preview" || now.UtcDateTime - row.AcceptedAtUtc > PreviewLifetime)
                throw new InvalidOperationException("預覽已逾期，請重新預覽。");
            if (!LockScopeFence(ctx, row.ScopeFenceJson))
                throw new InvalidOperationException("監看範圍或對應在預覽後已變更，請重新預覽。");
            if (row.PermissionRevision != permissionRevision || row.AuthFingerprint != authFingerprint)
                throw new InvalidOperationException("權限已變更，請重新預覽。");
            if (!string.Equals(row.ScopeFingerprint, expectedFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("監看範圍或對應已變更，請重新預覽。");

            // The unique nullable slot is a database-enforced cross-process mutex.
            var occupied = ctx.PrtgScopePurgeJobs.AsNoTracking().Any(x => x.ActiveOwner == "active");
            if (occupied) throw new InvalidOperationException("另一個範圍外清除作業正在執行。");
            row.Status = "accepted";
            row.ActiveOwner = "active";
            row.LeaseExpiresAtUtc = LeaseExpiresAt(now);
            row.UpdatedAtUtc = now.UtcDateTime;
            row.Revision++;
            try { ctx.SaveChanges(); }
            catch (DbUpdateException) { throw new InvalidOperationException("另一個範圍外清除作業正在執行。"); }
            tx.Commit();
            return row;
        });
    }

    /// <summary>
    /// Deletes at most one bounded page from each data table and commits both deletes, cursors,
    /// and durable counts in one transaction. A retry after an unknown commit reads this row first.
    /// </summary>
    public PrtgScopePurgeJobRow RunBatch(string jobId, long expectedRevision, string currentFingerprint, DateTimeOffset now,
        CancellationToken cancellationToken = default, PrtgScopePurgeAuthorizationFence? authorizationFence = null,
        Func<bool>? ownershipCheck = null)
    {
        using var probe = _contextFactory();
        return probe.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var ctx = _contextFactory();
            using var tx = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
            LockScopeMutationGate(ctx);
            var row = ctx.PrtgScopePurgeJobs.SingleOrDefault(x => x.JobId == jobId)
                ?? throw new InvalidOperationException("清除作業不存在。");
            if (row.Revision != expectedRevision) { tx.Commit(); return row; }
            if (row.Status == "complete" || row.Status == "stopped") { tx.Commit(); return row; }
            if (row.Status is not ("accepted" or "running" or "paused"))
                throw new InvalidOperationException("清除作業尚未接受。");
            if (row.Source == "automatic" && ownershipCheck == null)
            {
                row.Status = "paused"; row.ActiveOwner = null; row.LeaseExpiresAtUtc = null;
                row.StopReason = "parent-ownership-unverifiable";
                RefreshFinalCounts(ctx, row);
                row.UpdatedAtUtc = now.UtcDateTime; row.Revision++;
                ctx.SaveChanges(); tx.Commit(); return row;
            }
            if (!LockScopeFence(ctx, row.ScopeFenceJson))
            {
                row.Status = "stopped"; row.ActiveOwner = null; row.LeaseExpiresAtUtc = null;
                row.StopReason = "scope-revision-changed";
                RefreshFinalCounts(ctx, row);
                row.UpdatedAtUtc = now.UtcDateTime; row.Revision++;
                ctx.SaveChanges(); tx.Commit(); return row;
            }
            if (!string.Equals(row.ScopeFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                row.Status = "stopped"; row.ActiveOwner = null; row.LeaseExpiresAtUtc = null; row.StopReason = "scope-changed";
                RefreshFinalCounts(ctx, row);
                row.UpdatedAtUtc = now.UtcDateTime; row.Revision++;
                ctx.SaveChanges(); tx.Commit(); return row;
            }
            PauseExpiredOwner(ctx, now, exceptJobId: jobId);
            if (row.ActiveOwner != "active" && ctx.PrtgScopePurgeJobs.AsNoTracking().Any(x => x.JobId != jobId && x.ActiveOwner == "active"))
                throw new InvalidOperationException("另一個範圍外清除作業正在執行。");
            cancellationToken.ThrowIfCancellationRequested();
            if (authorizationFence != null &&
                (row.Source != "manual" || row.AuthFingerprint != authorizationFence.AuthFingerprint ||
                 (authorizationFence.UsersRevision is { } usersRevision && !LockBlobVersion(ctx, "users", usersRevision)) ||
                 (authorizationFence.GroupsRevision is { } groupsRevision && !LockBlobVersion(ctx, "user_groups", groupsRevision)) ||
                 !LockBlobVersion(ctx, "permission_version", authorizationFence.PermissionRevision)))
            {
                row.Status = "paused"; row.ActiveOwner = null; row.LeaseExpiresAtUtc = null;
                row.StopReason = "authorization-changed";
                RefreshFinalCounts(ctx, row);
                row.UpdatedAtUtc = now.UtcDateTime; row.Revision++;
                ctx.SaveChanges(); tx.Commit(); return row;
            }
            row.Status = "running"; row.ActiveOwner = "active"; row.LeaseExpiresAtUtc = LeaseExpiresAt(now); row.StopReason = null;
            var candidateSensors = JsonSerializer.Deserialize<HashSet<long>>(row.CandidateSensorIdsJson) ?? new();
            var valuePage = ReadCandidatePage(ctx, values: true, row.ValuesCursor, row.ValuesWatermark, candidateSensors);
            var statePage = ReadCandidatePage(ctx, values: false, row.StateChangesCursor, row.StateChangesWatermark, candidateSensors);
            cancellationToken.ThrowIfCancellationRequested();
            if (ownershipCheck != null && !ownershipCheck())
            {
                row.Status = "stopped"; row.ActiveOwner = null; row.LeaseExpiresAtUtc = null;
                row.StopReason = "parent-ownership-lost";
                RefreshFinalCounts(ctx, row);
                row.UpdatedAtUtc = now.UtcDateTime; row.Revision++;
                ctx.SaveChanges(); tx.Commit(); return row;
            }
            var deletedValues = valuePage.CandidateIds.Count == 0 ? 0 : ctx.PrtgValues.Where(x => valuePage.CandidateIds.Contains(x.Id)).ExecuteDelete();
            var deletedStates = statePage.CandidateIds.Count == 0 ? 0 : ctx.PrtgStateChanges.Where(x => statePage.CandidateIds.Contains(x.Id)).ExecuteDelete();
            row.DeletedValues += deletedValues;
            row.DeletedStateChanges += deletedStates;
            row.ValuesCursor = valuePage.Cursor;
            row.StateChangesCursor = statePage.Cursor;
            row.UpdatedAtUtc = now.UtcDateTime;

            if (row.ValuesCursor >= row.ValuesWatermark && row.StateChangesCursor >= row.StateChangesWatermark)
            {
                row.Status = "complete"; row.ActiveOwner = null; row.StopReason = null;
                row.LeaseExpiresAtUtc = null;
                RefreshFinalCounts(ctx, row);
                RecordBaselineInTransaction(ctx, row.MonitoredDevices, now.UtcDateTime);
            }
            else
            {
                // Keep each committed page O(batch size). Other retention can change these totals
                // between pages, so mark the suffix as an estimate and reconcile exactly only at a
                // terminal/pause boundary. This avoids rescanning the full candidate prefix per page.
                row.RemainingValues = Math.Max(0, row.CandidateValues - row.DeletedValues);
                row.RemainingStateChanges = Math.Max(0, row.CandidateStateChanges - row.DeletedStateChanges);
                row.CountsReconciled = false;
            }
            row.Revision++;
            cancellationToken.ThrowIfCancellationRequested();
            ctx.SaveChanges();
            tx.Commit();
            return row;
        });
    }

    public PrtgScopePurgeJobRow Stop(string jobId, string reason, DateTimeOffset now)
    {
        using var ctx = _contextFactory();
        using var tx = ctx.Database.BeginTransaction();
        var row = ctx.PrtgScopePurgeJobs.Single(x => x.JobId == jobId);
        if (row.Status != "complete")
        {
            row.Status = "paused"; row.ActiveOwner = null; row.LeaseExpiresAtUtc = null; row.StopReason = reason;
            row.UpdatedAtUtc = now.UtcDateTime;
            RefreshFinalCounts(ctx, row);
            row.Revision++;
            ctx.SaveChanges();
        }
        tx.Commit();
        return row;
    }

    private static void RefreshFinalCounts(LfDbContext ctx, PrtgScopePurgeJobRow row)
    {
        var sensors = JsonSerializer.Deserialize<HashSet<long>>(row.CandidateSensorIdsJson) ?? new();
        row.RemainingValues = CountCandidates(ctx, values: true, sensors, row.ValuesWatermark);
        row.RemainingStateChanges = CountCandidates(ctx, values: false, sensors, row.StateChangesWatermark);
        row.OtherwiseRemovedValues = Math.Max(0, row.CandidateValues - row.DeletedValues - row.RemainingValues);
        row.OtherwiseRemovedStateChanges = Math.Max(0, row.CandidateStateChanges - row.DeletedStateChanges - row.RemainingStateChanges);
        row.CountsReconciled = true;
    }

    private static bool LockBlobVersion(LfDbContext ctx, string blobKey, long expectedVersion)
    {
        var currentVersion = ctx.Blobs.AsNoTracking().Where(x => x.BlobKey == blobKey)
            .Select(x => (long?)x.Version).SingleOrDefault();
        if (currentVersion is null) return expectedVersion == 0; // Serializable key-range read protects absence.
        if (currentVersion.Value != expectedVersion) return false;
        // A no-op update takes and holds the provider's write lock until this batch commits. Permission
        // source mutations update the same blob rows, so they either precede this check or follow the batch.
        return ctx.Blobs.Where(x => x.BlobKey == blobKey && x.Version == expectedVersion)
            .ExecuteUpdate(setters => setters.SetProperty(x => x.Version, x => x.Version)) == 1;
    }

    /// <summary>Serializes source writers with preview acceptance and deletion transactions.</summary>
    private static void LockScopeMutationGate(LfDbContext ctx)
    {
        var version = ctx.Blobs.AsNoTracking().Where(x => x.BlobKey == EfPrtgStore.ScopeRevisionBlobKey)
            .Select(x => (long?)x.Version).SingleOrDefault();
        if (version == null)
        {
            // StorageBackend normally seeds this row during SchemaUpgrader. Keep direct provider
            // contexts and partially upgraded stores safe too; the unique blob key resolves
            // simultaneous first writers, and the caller's serializable transaction is retained.
            var gate = new BlobRow
            {
                BlobKey = EfPrtgStore.ScopeRevisionBlobKey, Content = "{}", UpdatedAt = DateTime.UtcNow, Version = 1
            };
            ctx.Blobs.Add(gate);
            try { ctx.SaveChanges(); }
            catch (DbUpdateException)
            {
                ctx.Entry(gate).State = EntityState.Detached;
                if (!ctx.Blobs.AsNoTracking().Any(x => x.BlobKey == EfPrtgStore.ScopeRevisionBlobKey)) throw;
            }
        }
        if (ctx.Blobs.Where(x => x.BlobKey == EfPrtgStore.ScopeRevisionBlobKey)
                .ExecuteUpdate(setters => setters.SetProperty(x => x.Version, x => x.Version)) != 1)
            throw new InvalidOperationException("範圍清除互斥資料遺失，已拒絕操作。");
    }

    private static PrtgScopePurgeScopeFence CaptureScopeFence(LfDbContext ctx)
    {
        var versions = ctx.Blobs.AsNoTracking().Where(row => ScopeFenceBlobKeys.Contains(row.BlobKey))
            .Select(row => new { row.BlobKey, row.Version }).ToList()
            .ToDictionary(row => row.BlobKey, row => row.Version, StringComparer.Ordinal);
        foreach (var key in ScopeFenceBlobKeys) versions.TryAdd(key, 0);
        return new PrtgScopePurgeScopeFence(versions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    private static bool LockScopeFence(LfDbContext ctx, string? scopeFenceJson)
    {
        if (string.IsNullOrWhiteSpace(scopeFenceJson)) return false;
        Dictionary<string, long>? expected;
        try
        {
            using var document = JsonDocument.Parse(scopeFenceJson);
            if (!document.RootElement.TryGetProperty(nameof(PrtgScopePurgeScopeFence.Revisions), out var revisions) ||
                revisions.ValueKind != JsonValueKind.Object) return false;
            expected = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var item in revisions.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Number || !item.Value.TryGetInt64(out var version)) return false;
                expected.Add(item.Name, version);
            }
        }
        catch (JsonException) { return false; }
        if (expected == null || ScopeFenceBlobKeys.Any(key => !expected.ContainsKey(key)) ||
            expected.Keys.Any(key => !ScopeFenceBlobKeys.Contains(key, StringComparer.Ordinal)))
            return false;
        foreach (var key in ScopeFenceBlobKeys)
            if (!LockBlobVersion(ctx, key, expected[key])) return false;
        return true;
    }

    /// <summary>Serializes scope writers against preview acceptance and every purge batch.</summary>
    internal static void EnsureScopeMutationSlotFree(LfDbContext ctx)
    {
        LockScopeMutationGate(ctx);
        PauseExpiredOwner(ctx, DateTimeOffset.UtcNow, exceptJobId: null);
        if (ctx.PrtgScopePurgeJobs.AsNoTracking().Any(x => x.ActiveOwner == "active"))
            throw new InvalidOperationException("監看範圍正在清除中，請稍後再變更來源、對應或主機範圍。");
    }

    private static DateTime LeaseExpiresAt(DateTimeOffset now) => now.UtcDateTime.Add(OwnerLeaseLifetime);

    private static bool PauseExpiredOwner(LfDbContext ctx, DateTimeOffset now, string? exceptJobId)
    {
        var expired = ctx.PrtgScopePurgeJobs.SingleOrDefault(x => x.ActiveOwner == "active"
            && (exceptJobId == null || x.JobId != exceptJobId)
            && (x.LeaseExpiresAtUtc == null || x.LeaseExpiresAtUtc <= now.UtcDateTime));
        if (expired == null) return false;
        expired.Status = "paused";
        expired.ActiveOwner = null;
        expired.LeaseExpiresAtUtc = null;
        expired.StopReason = "owner-lease-expired";
        expired.UpdatedAtUtc = now.UtcDateTime;
        RefreshFinalCounts(ctx, expired);
        expired.Revision++;
        ctx.SaveChanges(); // Revision is a DB concurrency token; an in-flight batch wins atomically.
        return true;
    }

    private static (List<long> CandidateIds, long Cursor) ReadCandidatePage(LfDbContext ctx, bool values,
        long after, long watermark, IReadOnlySet<long> candidateSensors)
    {
        var rows = values
            ? ctx.PrtgValues.AsNoTracking().Where(x => x.Id > after && x.Id <= watermark).OrderBy(x => x.Id)
                .Select(x => new PurgeSourceRow(x.Id, x.SensorObjid)).Take(BatchSize).ToList()
            : ctx.PrtgStateChanges.AsNoTracking().Where(x => x.Id > after && x.Id <= watermark).OrderBy(x => x.Id)
                .Select(x => new PurgeSourceRow(x.Id, x.SensorObjid)).Take(BatchSize).ToList();
        if (rows.Count == 0) return (new(), watermark);
        var candidates = rows.Where(x => candidateSensors.Contains(x.SensorId)).Select(x => x.Id).ToList();
        return (candidates, rows[^1].Id);
    }

    private static long CountCandidates(LfDbContext ctx, bool values, IReadOnlySet<long> candidateSensors, long watermark)
    {
        if (candidateSensors.Count == 0 || watermark <= 0) return 0;
        long count = 0;
        foreach (var sensors in candidateSensors.Chunk(500))
        {
            // Keep parameter counts comfortably below SQLite and SQL Server limits. Counts stay in
            // the provider; progress requests never materialize the entire retained history.
            var sensorPage = sensors.ToArray();
            count += values
                ? ctx.PrtgValues.AsNoTracking().Where(x => x.Id <= watermark && sensorPage.Contains(x.SensorObjid)).LongCount()
                : ctx.PrtgStateChanges.AsNoTracking().Where(x => x.Id <= watermark && sensorPage.Contains(x.SensorObjid)).LongCount();
        }
        return count;
    }

    private static HashSet<long> CaptureCandidateSensorIds(LfDbContext ctx, IReadOnlySet<long> keep,
        long valueWatermark, long stateWatermark)
    {
        var candidate = new HashSet<long>();
        AddCandidatesFromSensorPages(ctx,
            ctx.PrtgValues.AsNoTracking().Where(x => x.Id <= valueWatermark).Select(x => x.SensorObjid), keep, candidate);
        AddCandidatesFromSensorPages(ctx,
            ctx.PrtgStateChanges.AsNoTracking().Where(x => x.Id <= stateWatermark).Select(x => x.SensorObjid), keep, candidate);
        return candidate;
    }

    private static void AddCandidatesFromSensorPages(LfDbContext ctx, IQueryable<long> sourceSensorIds,
        IReadOnlySet<long> keep, HashSet<long> candidate)
    {
        long cursor = long.MinValue;
        while (true)
        {
            // A seek on sensor_objid returns only one bounded distinct projection page.
            var page = sourceSensorIds.Where(x => x > cursor).Distinct().OrderBy(x => x).Take(500).ToList();
            if (page.Count == 0) break;
            var deviceBySensor = ctx.PrtgSensors.AsNoTracking().Where(x => page.Contains(x.Objid))
                .Select(x => new { x.Objid, x.DeviceObjid }).ToList().ToDictionary(x => x.Objid, x => x.DeviceObjid);
            foreach (var sensorId in page)
                if (!keep.Contains(sensorId) && (!deviceBySensor.TryGetValue(sensorId, out var deviceId) || !keep.Contains(deviceId)))
                {
                    candidate.Add(sensorId);
                    if (candidate.Count > MaximumCapturedIds)
                        throw new InvalidOperationException($"候選感測器超過預覽上限 {MaximumCapturedIds}，未建立清除作業。");
                }
            cursor = page[^1];
        }
    }

    private static void RecordBaselineInTransaction(LfDbContext ctx, int deviceCount, DateTime atUtc)
    {
        var blob = ctx.Blobs.SingleOrDefault(x => x.BlobKey == PrtgScopeBaselineStore.BlobKey);
        var baseline = blob == null ? new PrtgScopeBaseline() :
            JsonSerializer.Deserialize<PrtgScopeBaseline>(blob.Content, LfJsonOptions.Pretty) ?? new PrtgScopeBaseline();
        baseline.DeviceCount = deviceCount; baseline.At = atUtc.ToLocalTime();
        baseline.BlockedReason = null; baseline.BlockedAt = null;
        var content = JsonSerializer.Serialize(baseline, LfJsonOptions.Pretty);
        if (blob == null) ctx.Blobs.Add(new BlobRow { BlobKey = PrtgScopeBaselineStore.BlobKey, Content = content, UpdatedAt = atUtc, Version = 1 });
        else { blob.Content = content; blob.UpdatedAt = atUtc; blob.Version++; }
    }

    private sealed record PurgeSourceRow(long Id, long SensorId);
}
