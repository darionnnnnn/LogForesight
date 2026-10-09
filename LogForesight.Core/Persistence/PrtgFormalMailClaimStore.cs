using System.Text;
using System.Data;
using System.Diagnostics;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence;

/// <summary>
/// Per-host storage for immutable formal-mail fences and recipient-start outcomes. A
/// <see cref="MutationSession"/> is created inside the mail-state transaction, so its shard
/// writes commit atomically with recipient claims and current mode validation.
/// </summary>
public static class PrtgFormalMailClaimStore
{
    private const int PrunePageSize = 16;
    private const int DefaultPruneKeyLimit = 100_000;
    private static readonly TimeSpan DefaultPruneTimeLimit = TimeSpan.FromSeconds(30);
    internal const int MaxClaimsPerHost = 256;
    internal const int MaxFencesPerHost = 512;
    internal const int MaxShardCharacters = 512 * 1024;
    internal static readonly TimeSpan TerminalRetention = TimeSpan.FromDays(90);

    public sealed record PruneResult(int DeletedShards, int UpdatedShards, string? Cursor, bool HasMore,
        bool StateUnavailable = false);

    /// <summary>
    /// Bounded keyset scan for old dated shards. Mail outbox references are snapshotted once and
    /// each small page rechecks the singleton version in a serializable transaction before it
    /// changes a shard. Unknown outcomes and referenced shards are always retained.
    /// </summary>
    public static PruneResult PruneExpiredShards(StorageBackend backend, int recordRetentionDays,
        string? cursor, DateTime nowUtc, int maximumKeys = DefaultPruneKeyLimit,
        TimeSpan? timeLimit = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return PruneExpiredShardsCore(backend, backend.CreateContext, recordRetentionDays, cursor, nowUtc,
            maximumKeys, timeLimit);
    }

    internal static PruneResult PruneExpiredShardsForTesting(StorageBackend backend,
        Func<LfDbContext> contextFactory, int recordRetentionDays, string? cursor, DateTime nowUtc,
        int maximumKeys = DefaultPruneKeyLimit, TimeSpan? timeLimit = null)
        => PruneExpiredShardsCore(backend, contextFactory, recordRetentionDays, cursor, nowUtc,
            maximumKeys, timeLimit);

    private static PruneResult PruneExpiredShardsCore(StorageBackend backend,
        Func<LfDbContext>? contextFactory, int recordRetentionDays, string? cursor, DateTime nowUtc,
        int maximumKeys, TimeSpan? timeLimit)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(contextFactory);
        if (maximumKeys < 1) throw new ArgumentOutOfRangeException(nameof(maximumKeys));
        var budget = timeLimit ?? DefaultPruneTimeLimit;
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeLimit));
        var clock = Stopwatch.StartNew();
        var scanCount = 0;
        var deleted = 0;
        var updated = 0;
        var stateSnapshot = ReadOutboxReferenceSnapshot(backend);
        if (!stateSnapshot.Available)
            return new PruneResult(0, 0, cursor, false, StateUnavailable: true);

        var cutoff = nowUtc.Date.AddDays(-Math.Max((int)TerminalRetention.TotalDays, Math.Max(0, recordRetentionDays)));
        var hasMore = false;
        while (scanCount < maximumKeys && clock.Elapsed < budget)
        {
            var requested = Math.Min(PrunePageSize, maximumKeys - scanCount);
            List<ShardSnapshot>? retryPage = null;
            PrunePageOutcome pageOutcome;
            using (var probe = contextFactory())
            {
                var strategy = probe.Database.CreateExecutionStrategy();
                pageOutcome = strategy.Execute(() =>
                {
                    // Retry the same captured keyset page with a fresh context. If a commit succeeded
                    // but its acknowledgement was lost, re-querying after that page would skip the
                    // cursor accounting and could consume a second page in one bounded operation.
                    using var context = contextFactory();
                    using var transaction = context.Database.BeginTransaction(IsolationLevel.Serializable);
                    var mailVersion = context.Blobs.AsNoTracking()
                        .Where(row => row.BlobKey == MailNotifyStateStore.BlobKey)
                        .Select(row => row.Version).FirstOrDefault();
                    if (mailVersion != stateSnapshot.Version)
                    {
                        transaction.Rollback();
                        return new PrunePageOutcome(0, cursor, false, RefreshOutboxSnapshot: true);
                    }

                    if (retryPage == null)
                    {
                        var query = context.Blobs.AsNoTracking().Where(row =>
                            row.BlobKey.StartsWith("prtg_formal_mail_claims_v2_") ||
                            row.BlobKey.StartsWith("prtg_formal_mail_claims_v3_"));
                        if (!string.IsNullOrEmpty(cursor)) query = query.Where(row => row.BlobKey.CompareTo(cursor) > 0);
                        retryPage = query.OrderBy(row => row.BlobKey).Take(requested)
                            .Select(row => new ShardSnapshot(row.BlobKey,
                                row.Content.Substring(0, MaxShardCharacters + 1), row.Content.Length,
                                row.Version, row.UpdatedAt))
                            .ToList();
                    }
                    if (retryPage.Count == 0)
                    {
                        transaction.Commit();
                        return new PrunePageOutcome(0, null, false, false, Empty: true);
                    }

                    var pageDeleted = 0;
                    var pageUpdated = 0;
                    foreach (var candidate in retryPage)
                    {
                        if (!TryGetShardDate(candidate.BlobKey, out var shardDate) || shardDate >= cutoff ||
                            stateSnapshot.ReferencedShardKeys.Contains(candidate.BlobKey) ||
                            candidate.Length > MaxShardCharacters || candidate.Content.Length > MaxShardCharacters ||
                            Encoding.UTF8.GetByteCount(candidate.Content) > MaxShardCharacters || string.IsNullOrWhiteSpace(candidate.Content))
                            continue;

                        PrtgFormalMailClaimShard shard;
                        try
                        {
                            shard = JsonSerializer.Deserialize<PrtgFormalMailClaimShard>(candidate.Content, LfJsonOptions.Pretty)
                                ?? throw new JsonException("Formal mail claim shard root is null.");
                            ValidateShard(shard, shard.HostId);
                        }
                        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
                        {
                            continue; // Malformed state is preserved for diagnosis; never delete on an unreadable shape.
                        }

                        var before = candidate.Content;
                        PruneTerminalEntries(shard, nowUtc,
                            TimeSpan.FromDays(Math.Max((int)TerminalRetention.TotalDays, Math.Max(0, recordRetentionDays))));
                        var serialized = JsonSerializer.Serialize(shard, LfJsonOptions.Pretty);
                        var empty = shard.Claims.Count == 0 && shard.Fences.Count == 0;
                        if (!empty && StringComparer.Ordinal.Equals(before, serialized)) continue;
                        ValidateShard(shard, shard.HostId);

                        var tracked = context.Blobs.FirstOrDefault(row => row.BlobKey == candidate.BlobKey);
                        if (tracked == null)
                        {
                            // A previous attempt may have committed this same planned deletion before
                            // its acknowledgement was lost. Count it only when this replay independently
                            // derives the same terminal-empty outcome from the immutable page snapshot.
                            if (empty) pageDeleted++;
                            continue;
                        }
                        if (tracked.Version != candidate.Version || tracked.UpdatedAt != candidate.UpdatedAt)
                        {
                            // Likewise, recognize only the exact one-version result of this page's
                            // intended rewrite; unrelated concurrent edits remain untouched/un-counted.
                            if (!empty && tracked.Version == candidate.Version + 1 &&
                                StringComparer.Ordinal.Equals(tracked.Content, serialized))
                                pageUpdated++;
                            continue;
                        }
                        if (empty)
                        {
                            context.Blobs.Remove(tracked);
                            pageDeleted++;
                        }
                        else
                        {
                            EnsureBoundedShard(serialized);
                            tracked.Content = serialized;
                            tracked.UpdatedAt = DateTime.Now;
                            tracked.Version++;
                            pageUpdated++;
                        }
                    }

                    context.SaveChanges();
                    transaction.Commit();
                    return new PrunePageOutcome(retryPage.Count, retryPage[^1].BlobKey,
                        retryPage.Count == requested, false, Empty: false,
                        Deleted: pageDeleted, Updated: pageUpdated);
                });
            }

            if (pageOutcome.RefreshOutboxSnapshot)
            {
                stateSnapshot = ReadOutboxReferenceSnapshot(backend);
                if (!stateSnapshot.Available)
                    return new PruneResult(deleted, updated, cursor, true, StateUnavailable: true);
                continue;
            }
            if (pageOutcome.Empty)
            {
                return new PruneResult(deleted, updated, null, false);
            }

            deleted += pageOutcome.Deleted;
            updated += pageOutcome.Updated;
            scanCount += pageOutcome.Scanned;
            cursor = pageOutcome.Cursor;
            hasMore = pageOutcome.HasMore;
            if (scanCount >= maximumKeys || clock.Elapsed >= budget)
            {
                hasMore = true;
                break;
            }
            if (!pageOutcome.HasMore)
            {
                cursor = null;
                hasMore = false;
                break;
            }
        }

        if (scanCount < maximumKeys && clock.Elapsed >= budget) hasMore = true;
        return new PruneResult(deleted, updated, cursor, hasMore);
    }

    private sealed record ShardSnapshot(string BlobKey, string Content, int Length, long Version, DateTime UpdatedAt);
    private sealed record PrunePageOutcome(int Scanned, string? Cursor, bool HasMore,
        bool RefreshOutboxSnapshot, bool Empty = false, int Deleted = 0, int Updated = 0);

    private sealed record OutboxReferenceSnapshot(bool Available, long Version, HashSet<string> ReferencedShardKeys);

    private static OutboxReferenceSnapshot ReadOutboxReferenceSnapshot(StorageBackend backend)
    {
        var (prefix, version, length) = backend.Blob(MailNotifyStateStore.BlobKey)
            .ReadBoundedWithVersion(MailNotifyStateStore.MaxMutationCharacters);
        if (prefix is null) return new OutboxReferenceSnapshot(true, version, new HashSet<string>(StringComparer.Ordinal));
        if (length > MailNotifyStateStore.MaxMutationCharacters || prefix.Length > MailNotifyStateStore.MaxMutationCharacters ||
            Encoding.UTF8.GetByteCount(prefix) > MailNotifyStateStore.MaxMutationCharacters)
            return new OutboxReferenceSnapshot(false, version, new HashSet<string>(StringComparer.Ordinal));
        try
        {
            var state = JsonSerializer.Deserialize<MailNotifyState>(prefix, LfJsonOptions.Pretty);
            if (state?.UrgentOutbox is null)
                return new OutboxReferenceSnapshot(false, version, new HashSet<string>(StringComparer.Ordinal));
            var references = state.UrgentOutbox.Values
                .Where(intent => intent?.FormalStartFenceRefs != null)
                .SelectMany(intent => intent.FormalStartFenceRefs.Values)
                .Where(reference => reference != null && !string.IsNullOrWhiteSpace(reference.ShardKey))
                .Select(reference => reference.ShardKey)
                .ToHashSet(StringComparer.Ordinal);
            return new OutboxReferenceSnapshot(true, version, references);
        }
        catch (JsonException)
        {
            return new OutboxReferenceSnapshot(false, version, new HashSet<string>(StringComparer.Ordinal));
        }
    }

    private static bool TryGetShardDate(string key, out DateTime date)
    {
        date = default;
        var parts = key.Split('_');
        return parts.Length >= 7 && parts[4] is "v2" or "v3" &&
            DateTime.TryParseExact(parts[6], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date);
    }

    public static string BlobKey(long hostId)
    {
        if (hostId <= 0) throw new ArgumentOutOfRangeException(nameof(hostId));
        return $"prtg_formal_mail_claims_v1_{hostId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>New claims are partitioned by the immutable analysis day, so active fences do not accumulate in one host-wide blob.</summary>
    public static string BlobKey(long hostId, DateTime recordDate)
    {
        if (hostId <= 0) throw new ArgumentOutOfRangeException(nameof(hostId));
        return $"prtg_formal_mail_claims_v2_{hostId.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{recordDate:yyyyMMdd}";
    }

    /// <summary>Stable bounded partition for a host, analysis day, delivery lane, and immutable issue key.</summary>
    public static string BlobKey(long hostId, DateTime recordDate, string lane, string keySegment)
    {
        if (hostId <= 0) throw new ArgumentOutOfRangeException(nameof(hostId));
        ArgumentException.ThrowIfNullOrWhiteSpace(lane);
        ArgumentException.ThrowIfNullOrWhiteSpace(keySegment);
        var laneKey = lane.StartsWith("urgent", StringComparison.Ordinal) ? "urgent" :
            lane.StartsWith("summary", StringComparison.Ordinal) ? "summary" :
            lane.StartsWith("weekly-digest", StringComparison.Ordinal) ? "weekly-digest" :
            lane.StartsWith("daily-digest", StringComparison.Ordinal) ? "daily-digest" :
            throw new ArgumentOutOfRangeException(nameof(lane), "Unknown formal mail claim lane.");
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(laneKey + "|" + keySegment));
        var segment = Convert.ToHexString(hash.AsSpan(0, 1));
        return $"prtg_formal_mail_claims_v3_{hostId.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{recordDate:yyyyMMdd}_{laneKey}_{segment}";
    }

    /// <summary>Recipient-scoped partition so one issue's claims scale with recipients, not one shared 256-entry shard.</summary>
    public static string BlobKey(long hostId, DateTime recordDate, string lane, string keySegment, string recipient)
    {
        if (hostId <= 0) throw new ArgumentOutOfRangeException(nameof(hostId));
        ArgumentException.ThrowIfNullOrWhiteSpace(lane);
        ArgumentException.ThrowIfNullOrWhiteSpace(keySegment);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        var laneKey = lane.StartsWith("urgent", StringComparison.Ordinal) ? "urgent" :
            lane.StartsWith("summary", StringComparison.Ordinal) ? "summary" :
            lane.StartsWith("weekly-digest", StringComparison.Ordinal) ? "weekly-digest" :
            lane.StartsWith("daily-digest", StringComparison.Ordinal) ? "daily-digest" :
            throw new ArgumentOutOfRangeException(nameof(lane), "Unknown formal mail claim lane.");
        var normalizedRecipient = recipient.Trim().ToLowerInvariant();
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            laneKey + "|" + normalizedRecipient + "|" + keySegment));
        var segment = Convert.ToHexString(hash.AsSpan(0, 2));
        return $"prtg_formal_mail_claims_v3_{hostId.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{recordDate:yyyyMMdd}_{laneKey}_{segment}";
    }

    /// <summary>Transaction-scoped cache. It loads each host shard at most once and never owns the transaction.</summary>
    public sealed class MutationSession
    {
        private readonly LfDbContext _context;
        private readonly Dictionary<string, LoadedShard> _loaded = new(StringComparer.Ordinal);

        public MutationSession(LfDbContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

        public PrtgResourceFormalDeliveryFence? GetFence(long hostId, string fenceKey)
        {
            return GetFence(hostId, BlobKey(hostId), fenceKey);
        }

        public PrtgResourceFormalDeliveryFence? GetFence(long hostId, string shardKey, string fenceKey)
        {
            var shard = Load(hostId, shardKey).Value;
            return shard.Fences.TryGetValue(fenceKey, out var entry) ? entry.Fence : null;
        }

        public MailFormalStartClaim? GetClaim(long hostId, string claimKey)
        {
            return GetClaim(hostId, BlobKey(hostId), claimKey);
        }

        public MailFormalStartClaim? GetClaim(long hostId, string shardKey, string claimKey)
        {
            var shard = Load(hostId, shardKey).Value;
            return shard.Claims.TryGetValue(claimKey, out var claim) ? claim : null;
        }

        public void SetFence(long hostId, string fenceKey, PrtgResourceFormalDeliveryFence fence, DateTime nowUtc)
            => SetFence(hostId, BlobKey(hostId), fenceKey, fence, nowUtc);

        public void SetFence(long hostId, string shardKey, string fenceKey, PrtgResourceFormalDeliveryFence fence, DateTime nowUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fenceKey);
            ArgumentNullException.ThrowIfNull(fence);
            var loaded = Load(hostId, shardKey);
            if (!loaded.Value.Fences.ContainsKey(fenceKey))
                loaded.Value.Fences[fenceKey] = new PrtgFormalMailFenceEntry { Fence = fence, CreatedAtUtc = nowUtc };
            else if (loaded.Value.Fences[fenceKey].Fence != fence)
                throw new InvalidDataException("An immutable formal mail fence key was reused with different proof.");
            loaded.Dirty = true;
        }

        public void SetClaim(long hostId, string claimKey, MailFormalStartClaim claim)
            => SetClaim(hostId, BlobKey(hostId), claimKey, claim);

        public void SetClaim(long hostId, string shardKey, string claimKey, MailFormalStartClaim claim)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(claimKey);
            ArgumentNullException.ThrowIfNull(claim);
            var loaded = Load(hostId, shardKey);
            loaded.Value.Claims[claimKey] = claim;
            loaded.Dirty = true;
        }

        /// <summary>Writes changed shards into the caller's context; the owning transaction commits them.</summary>
        public void Persist()
        {
            foreach (var (shardKey, loaded) in _loaded)
            {
                if (!loaded.Dirty) continue;
                ValidateShard(loaded.Value, loaded.Value.HostId);
                var serialized = JsonSerializer.Serialize(loaded.Value, LfJsonOptions.Pretty);
                EnsureBoundedShard(serialized);
                if (loaded.Row is null)
                {
                    _context.Blobs.Add(new BlobRow
                    {
                        BlobKey = shardKey, Content = serialized, UpdatedAt = DateTime.Now, Version = 1
                    });
                }
                else
                {
                    loaded.Row.Content = serialized;
                    loaded.Row.UpdatedAt = DateTime.Now;
                    loaded.Row.Version++;
                }
            }
        }

        private LoadedShard Load(long hostId) => Load(hostId, BlobKey(hostId));

        private LoadedShard Load(long hostId, string key)
        {
            if (hostId <= 0) throw new ArgumentOutOfRangeException(nameof(hostId));
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (_loaded.TryGetValue(key, out var cached))
            {
                if (cached.Value.HostId != hostId)
                    throw new InvalidDataException("Formal mail shard key was reused for another host.");
                return cached;
            }

            var existing = _context.Blobs.AsNoTracking().Where(row => row.BlobKey == key)
                .Select(row => new
                {
                    Content = row.Content.Substring(0, MaxShardCharacters + 1),
                    Length = row.Content.Length
                }).FirstOrDefault();
            BlobRow? tracked = null;
            PrtgFormalMailClaimShard shard;
            var dirty = false;
            if (existing is null)
            {
                shard = new PrtgFormalMailClaimShard { HostId = hostId };
            }
            else
            {
                if (existing.Length > MaxShardCharacters || existing.Content.Length > MaxShardCharacters ||
                    Encoding.UTF8.GetByteCount(existing.Content) > MaxShardCharacters || string.IsNullOrWhiteSpace(existing.Content))
                    throw new InvalidDataException($"Formal mail claim shard for host {hostId} exceeds its bounded read limit.");
                shard = JsonSerializer.Deserialize<PrtgFormalMailClaimShard>(existing.Content, LfJsonOptions.Pretty)
                    ?? throw new JsonException($"Formal mail claim shard for host {hostId} has a null root.");
                if (shard.HostId != hostId || shard.Claims is null || shard.Fences is null)
                    throw new InvalidDataException($"Formal mail claim shard for host {hostId} has an invalid shape.");
                ValidateShard(shard, hostId);
                tracked = _context.Blobs.FirstOrDefault(row => row.BlobKey == key)
                    ?? throw new InvalidDataException($"Formal mail claim shard for host {hostId} disappeared inside its transaction.");
            }

            cached = new LoadedShard(shard, tracked, dirty);
            _loaded.Add(key, cached);
            return cached;
        }
    }

    internal static void PruneTerminalEntries(PrtgFormalMailClaimShard shard, DateTime nowUtc,
        TimeSpan? minimumTerminalAge = null)
    {
        // Ordinary delivery writes do not know configured record retention. Only the scheduled
        // retention consumer may pass its effective horizon; absent that value, use the stable
        // legacy 90-day rule for direct callers and focused tests.
        var retention = minimumTerminalAge ?? TerminalRetention;
        if (retention < TerminalRetention) retention = TerminalRetention;
        shard.Claims ??= new Dictionary<string, MailFormalStartClaim>(StringComparer.Ordinal);
        shard.Fences ??= new Dictionary<string, PrtgFormalMailFenceEntry>(StringComparer.Ordinal);
        var cutoff = nowUtc - retention;
        foreach (var key in shard.Claims.Where(entry => entry.Value is { Status: "revoked" or "smtp-accepted" } &&
                     entry.Value.UpdatedAtUtc < cutoff).Select(entry => entry.Key).ToArray())
            shard.Claims.Remove(key);

        var activeFenceKeys = shard.Claims.Values.Where(claim => claim.Status is not ("revoked" or "smtp-accepted"))
            .Select(claim => claim.FenceKey).Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in shard.Fences.Where(entry => !activeFenceKeys.Contains(entry.Key) &&
                     entry.Value.CreatedAtUtc < cutoff).Select(entry => entry.Key).ToArray())
            shard.Fences.Remove(key);
    }

    internal static void ValidateShard(PrtgFormalMailClaimShard shard, long hostId)
    {
        if (shard.HostId != hostId || shard.Claims.Count > MaxClaimsPerHost || shard.Fences.Count > MaxFencesPerHost)
            throw new InvalidDataException($"Formal mail claim shard for host {hostId} exceeds its per-host entry bound.");
        if (shard.Claims.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 1024 ||
                pair.Value is null || string.IsNullOrWhiteSpace(pair.Value.Status) || pair.Value.Status.Length > 64 ||
                string.IsNullOrWhiteSpace(pair.Value.FenceKey) || pair.Value.FenceKey.Length > 1024 ||
                pair.Value.Status != "revoked" && !shard.Fences.ContainsKey(pair.Value.FenceKey)))
            throw new InvalidDataException($"Formal mail claim shard for host {hostId} contains an invalid claim.");
        if (shard.Fences.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 1024 ||
                pair.Value?.Fence is null || pair.Value.Fence.HostId != hostId ||
                !pair.Key.StartsWith($"{pair.Value.Fence.ParentRecordId}|mode:{pair.Value.Fence.ResourceModeBlobVersion}|",
                    StringComparison.Ordinal)))
            throw new InvalidDataException($"Formal mail claim shard for host {hostId} contains an invalid fence.");
    }

    internal static void EnsureBoundedShard(string serialized)
    {
        if (serialized.Length > MaxShardCharacters || Encoding.UTF8.GetByteCount(serialized) > MaxShardCharacters)
            throw new InvalidDataException($"Formal mail claim shard output exceeds its {MaxShardCharacters}-character/byte limit.");
    }

    private sealed class LoadedShard(PrtgFormalMailClaimShard value, BlobRow? row, bool dirty)
    {
        public PrtgFormalMailClaimShard Value { get; } = value;
        public BlobRow? Row { get; } = row;
        public bool Dirty { get; set; } = dirty;
    }
}
