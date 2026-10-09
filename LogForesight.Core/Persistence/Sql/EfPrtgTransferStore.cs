using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// Durable, diagnostic-only transfer sessions. Each accepted chunk and the session counters commit in
/// the same SQL transaction. No method reads or writes the formal PRTG tables or policy blobs.
/// </summary>
public sealed class EfPrtgTransferStore
{
    public const int MaxChunkBytes = 4 * 1024 * 1024;
    // Keep transfer below the process-wide 1 GiB PRTG cap; polling and parsing retain headroom.
    public const long MaxConcurrentBufferBytes = 256L * 1024 * 1024;
    public const int MaxManifestUtf8Bytes = 64 * 1024;
    public const long ValidationBufferReservationBytes = 16L * 1024 * 1024;
    public const long ExportBufferReservationBytes = 128L * 1024 * 1024;
    public static readonly TimeSpan CompletedRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan AbandonedRetention = TimeSpan.FromDays(1);
    public static readonly TimeSpan IncompleteSessionLifetime = CompletedRetention;
    public static readonly TimeSpan FailedSessionRetention = AbandonedRetention;
    private const string ExportLeasePrefix = "export:";
    private const int WriteBufferMultiplier = 4;
    private static readonly TimeSpan WriteLeaseDuration = TimeSpan.FromMinutes(5);

    private readonly Func<LfDbContext> _contextFactory;
    private readonly IPrtgTransferCapacityProvider _capacity;

    public EfPrtgTransferStore(StorageBackend backend, IPrtgTransferCapacityProvider capacity)
        : this(backend.CreateContext, capacity) { }

    public EfPrtgTransferStore(Func<LfDbContext> contextFactory, IPrtgTransferCapacityProvider capacity)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _capacity = capacity ?? throw new ArgumentNullException(nameof(capacity));
    }

    public PrtgTransferStatus Create(PrtgTransferCreateRequest request, DateTimeOffset nowUtc)
    {
        ValidateCreateRequest(request);
        var binding = NormalizeBinding(request.Binding);
        return InTransaction(db =>
        {
            var existing = db.PrtgTransferSessions.SingleOrDefault(x => x.TransferId == request.TransferId);
            if (existing != null)
            {
                RequireBinding(existing, binding);
                if (existing.DeclaredBytes != request.DeclaredBytes || existing.ChunkCount != request.ChunkCount ||
                    existing.PackageSha256 != NormalizeHash(request.PackageSha256))
                    throw Conflict("transfer_id_conflict", "傳輸識別碼已用於不同的宣告內容。");
                ThrowIfExpired(existing, nowUtc);
                return ToStatus(existing);
            }

            EnsureCapacity(db, request.DeclaredBytes, nowUtc);
            var row = new PrtgTransferSessionRow
            {
                TransferId = request.TransferId,
                OwnerId = binding.OwnerId,
                ScopeHash = binding.ScopeHash,
                SourceIdentityHash = binding.SourceIdentityHash,
                DeclaredBytes = request.DeclaredBytes,
                ChunkCount = request.ChunkCount,
                PackageSha256 = NormalizeHash(request.PackageSha256),
                State = PrtgTransferStates.Receiving,
                Version = 1,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            };
            db.PrtgTransferSessions.Add(row);
            return ToStatus(row);
        });
    }

    /// <summary>Reserve export memory in the same durable ledger as upload/validation, before reading rows.</summary>
    public PrtgDiagnosticReadLease AcquireExportReadLease(Guid transferId, string leaseOwner,
        PrtgTransferBinding binding, DateTimeOffset nowUtc)
    {
        var normalized = NormalizeBinding(binding);
        if (transferId == Guid.Empty || string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > 120)
            throw BadRequest("export_lease_invalid", "匯出租約識別碼無效。");
        leaseOwner = ExportLeasePrefix + leaseOwner;
        return InTransaction(db =>
        {
            var existing = db.PrtgTransferSessions.SingleOrDefault(row => row.TransferId == transferId);
            if (existing != null)
            {
                RequireBinding(existing, normalized);
                if (existing.State == PrtgTransferStates.Validating && existing.LeaseOwner == leaseOwner &&
                    existing.LeaseUntilUtc > nowUtc)
                    return new PrtgDiagnosticReadLease(transferId, leaseOwner, existing.Version, existing.LeaseUntilUtc.Value);
                throw Conflict("export_lease_conflict", "匯出識別碼已使用；請重新開始下載。");
            }
            EnsureMemoryCapacity(db, ExportBufferReservationBytes, nowUtc);
            var until = nowUtc.AddHours(1);
            db.PrtgTransferSessions.Add(new PrtgTransferSessionRow
            {
                TransferId = transferId, OwnerId = normalized.OwnerId, ScopeHash = normalized.ScopeHash,
                SourceIdentityHash = normalized.SourceIdentityHash, State = PrtgTransferStates.Validating,
                DeclaredBytes = 1, ChunkCount = 1, PackageSha256 = new string('0', 64),
                LeaseOwner = leaseOwner, LeaseUntilUtc = until, Version = 1,
                CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc
            });
            return new PrtgDiagnosticReadLease(transferId, leaseOwner, 1, until);
        });
    }

    public bool ReleaseExportReadLease(PrtgDiagnosticReadLease lease, DateTimeOffset nowUtc)
    {
        if (!lease.LeaseOwner.StartsWith(ExportLeasePrefix, StringComparison.Ordinal)) return false;
        return InTransaction(db =>
        {
            var row = db.PrtgTransferSessions.SingleOrDefault(row => row.TransferId == lease.TransferId);
            if (row == null || row.State != PrtgTransferStates.Validating || row.Version != lease.Version ||
                row.LeaseOwner != lease.LeaseOwner) return false;
            row.State = PrtgTransferStates.Abandoned; row.AbandonedAtUtc = nowUtc;
            row.LeaseOwner = null; row.LeaseUntilUtc = null;
            row.Version++; row.UpdatedAtUtc = nowUtc;
            return true;
        });
    }

    public int ExpireExportReadLeases(DateTimeOffset nowUtc, int maximumRows = 16)
    {
        if (maximumRows is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        return InTransaction(db =>
        {
            var rows = db.PrtgTransferSessions.Where(row => row.State == PrtgTransferStates.Validating &&
                    row.LeaseOwner != null && row.LeaseOwner.StartsWith(ExportLeasePrefix) &&
                    row.LeaseUntilUtc <= nowUtc).OrderBy(row => row.LeaseUntilUtc).Take(maximumRows).ToArray();
            foreach (var row in rows)
            {
                row.State = PrtgTransferStates.Abandoned; row.AbandonedAtUtc = nowUtc;
                row.LeaseOwner = null; row.LeaseUntilUtc = null; row.Version++; row.UpdatedAtUtc = nowUtc;
            }
            return rows.Length;
        });
    }

    public PrtgTransferStatus GetStatus(Guid transferId, PrtgTransferBinding binding)
    {
        var normalized = NormalizeBinding(binding);
        using var db = _contextFactory();
        var row = db.PrtgTransferSessions.AsNoTracking().SingleOrDefault(x => x.TransferId == transferId)
            ?? throw NotFound();
        RequireBinding(row, normalized);
        // Reads do not renew or mutate retention. Expired records remain inspectable until the
        // bounded cleanup worker removes them; mutation/lease entry points enforce expiry.
        return ToStatus(row);
    }

    /// <summary>Reserve one bounded body buffer before the HTTP consumer reads its request body.</summary>
    public PrtgTransferChunkWriteLease BeginChunkWrite(
        Guid transferId, int ordinal, int expectedBytes, PrtgTransferBinding binding, DateTimeOffset nowUtc)
    {
        var normalized = NormalizeBinding(binding);
        if (expectedBytes is < 1 or > MaxChunkBytes)
            throw BadRequest("chunk_size_invalid", "每片必須為 1 至 4 MiB 的原始位元組。");
        var writeId = Guid.NewGuid();

        return InTransaction(db =>
        {
            var row = db.PrtgTransferSessions.SingleOrDefault(x => x.TransferId == transferId) ?? throw NotFound();
            RequireBinding(row, normalized);
            ThrowIfIncompleteAgeElapsed(row, nowUtc);
            if (row.State != PrtgTransferStates.Receiving)
                throw Conflict("transfer_not_receiving", "此傳輸目前不接受新片段。");
            if (ordinal < 0 || ordinal >= row.ChunkCount || expectedBytes != ExpectedChunkLength(row, ordinal))
                throw BadRequest("chunk_shape_invalid", "片段序號或位元組數與建立時宣告不符。");

            var prior = db.PrtgTransferChunks.AsNoTracking()
                .Where(x => x.TransferId == transferId && x.Ordinal == ordinal)
                .Select(x => new { x.ByteLength }).SingleOrDefault();
            if (prior != null && prior.ByteLength != expectedBytes)
                throw Conflict("chunk_content_conflict", "此序號已接受不同長度的片段。");
            var writeReservation = WriteBufferReservation(expectedBytes);
            if (row.ActiveWriteId == writeId && row.ActiveWriteBytes == writeReservation && row.ActiveWriteUntilUtc.HasValue)
                return new PrtgTransferChunkWriteLease(transferId, writeId, ordinal, expectedBytes, row.Version, row.ActiveWriteUntilUtc.Value);
            if (row.ActiveWriteId.HasValue && row.ActiveWriteUntilUtc > nowUtc)
                throw Conflict("chunk_write_in_progress", "此傳輸已有片段正在接收。");

            EnsureCapacity(db, 0, nowUtc);
            EnsureMemoryCapacity(db, writeReservation, nowUtc);
            var expiry = nowUtc + WriteLeaseDuration;
            row.ActiveWriteId = writeId;
            row.ActiveWriteBytes = writeReservation;
            row.ActiveWriteUntilUtc = expiry;
            row.Version = checked(row.Version + 1);
            row.UpdatedAtUtc = nowUtc;
            return new PrtgTransferChunkWriteLease(transferId, writeId, ordinal, expectedBytes, row.Version, expiry);
        });
    }

    /// <summary>
    /// Accept an already bounded request body. The server computes SHA-256; an existing ordinal is
    /// idempotent only when both bytes and hash match. Invalid bodies release their buffer lease.
    /// </summary>
    public PrtgTransferChunkReceipt AcceptChunk(
        PrtgTransferChunkWriteLease lease, byte[] payload, PrtgTransferBinding binding, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(payload);
        var normalized = NormalizeBinding(binding);
        if (payload.Length is < 1 or > MaxChunkBytes)
        {
            ReleaseChunkWrite(lease, normalized, nowUtc);
            throw BadRequest("chunk_size_invalid", "每片必須為 1 至 4 MiB 的原始位元組。");
        }
        var hash = Sha256(payload);

        try
        {
            return InTransaction(db =>
            {
                var row = db.PrtgTransferSessions.SingleOrDefault(x => x.TransferId == lease.TransferId) ?? throw NotFound();
                RequireBinding(row, normalized);
                if (payload.Length != lease.ExpectedBytes || lease.ExpectedBytes != ExpectedChunkLength(row, lease.Ordinal))
                    throw BadRequest("chunk_shape_invalid", "片段實際位元組數與建立時宣告不符。");

                var existing = db.PrtgTransferChunks.SingleOrDefault(x => x.TransferId == lease.TransferId && x.Ordinal == lease.Ordinal);
                if (existing != null)
                {
                    if (existing.ByteLength != payload.Length || existing.Sha256 != hash || !payload.AsSpan().SequenceEqual(existing.Payload))
                        throw Conflict("chunk_content_conflict", "相同片段序號已保存不同內容。");
                    if (row.ActiveWriteId == lease.WriteId && row.Version == lease.Version)
                    {
                        ClearWriteLease(row);
                        row.Version = checked(row.Version + 1);
                        row.UpdatedAtUtc = nowUtc;
                    }
                    return new PrtgTransferChunkReceipt(lease.Ordinal, payload.Length, hash, true);
                }

                RequireActiveWrite(row, lease, nowUtc);
                if (row.State != PrtgTransferStates.Receiving || row.ReceivedBytes > row.DeclaredBytes - payload.Length || row.ReceivedChunks >= row.ChunkCount)
                    throw Conflict("transfer_bounds_exceeded", "片段超出此傳輸宣告的總量。");

                EnsureCapacity(db, 0, nowUtc);
                db.PrtgTransferChunks.Add(new PrtgTransferChunkRow
                {
                    TransferId = lease.TransferId,
                    Ordinal = lease.Ordinal,
                    Payload = payload,
                    ByteLength = payload.Length,
                    Sha256 = hash,
                    AcceptedAtUtc = nowUtc
                });
                row.ReceivedBytes = checked(row.ReceivedBytes + payload.Length);
                row.ReceivedChunks = checked(row.ReceivedChunks + 1);

                ClearWriteLease(row);
                row.Version = checked(row.Version + 1);
                row.UpdatedAtUtc = nowUtc;
                return new PrtgTransferChunkReceipt(lease.Ordinal, payload.Length, hash, false);
            });
        }
        catch
        {
            try { ReleaseChunkWrite(lease, normalized, nowUtc); }
            catch { /* 保留原始錯誤；若釋放也失敗，持久 lease 會在期限到達後自然失效。 */ }
            throw;
        }
    }

    public void ReleaseChunkWrite(PrtgTransferChunkWriteLease lease, PrtgTransferBinding binding, DateTimeOffset nowUtc)
    {
        var normalized = NormalizeBinding(binding);
        InTransaction(db =>
        {
            var row = db.PrtgTransferSessions.SingleOrDefault(x => x.TransferId == lease.TransferId);
            if (row == null) return 0;
            RequireBinding(row, normalized);
            if (row.ActiveWriteId != lease.WriteId || row.Version != lease.Version) return 0;
            ClearWriteLease(row);
            row.Version = checked(row.Version + 1);
            row.UpdatedAtUtc = nowUtc;
            return 1;
        });
    }

    public PrtgTransferValidationLease AcquireValidationLease(
        Guid transferId, PrtgTransferBinding binding, string leaseOwner, TimeSpan leaseDuration, DateTimeOffset nowUtc)
    {
        var normalized = NormalizeBinding(binding);
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > 128 ||
            leaseOwner.StartsWith(ExportLeasePrefix, StringComparison.Ordinal) ||
            leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
            throw BadRequest("lease_invalid", "驗證 lease 參數無效。");

        return InTransaction(db =>
        {
            var row = db.PrtgTransferSessions.SingleOrDefault(x => x.TransferId == transferId) ?? throw NotFound();
            RequireBinding(row, normalized);
            ThrowIfIncompleteAgeElapsed(row, nowUtc);
            if (row.State == PrtgTransferStates.Complete)
                throw Conflict("transfer_already_complete", "此診斷包已完成驗證。");
            if (row.State == PrtgTransferStates.ValidationFailed || row.State == PrtgTransferStates.Abandoned)
                throw Conflict("transfer_not_validatable", "此傳輸已拒絕或放棄，不能取得驗證 lease。");
            if (row.State == PrtgTransferStates.Validating && row.LeaseUntilUtc > nowUtc)
            {
                if (row.LeaseOwner == leaseOwner)
                    return new PrtgTransferValidationLease(transferId, leaseOwner, row.Version, row.LeaseUntilUtc.Value);
                throw Conflict("validation_lease_held", "另一個驗證程序仍持有此傳輸的 lease。");
            }
            if (row.ReceivedChunks != row.ChunkCount || row.ReceivedBytes != row.DeclaredBytes)
                throw Conflict("transfer_incomplete", "片段尚未完整保存，不能開始驗證。");

            EnsureMemoryCapacity(db, ValidationBufferReservationBytes, nowUtc);

            var chunkStats = db.PrtgTransferChunks.AsNoTracking().Where(x => x.TransferId == transferId)
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), MinOrdinal = g.Min(x => x.Ordinal), MaxOrdinal = g.Max(x => x.Ordinal), Bytes = g.Sum(x => (long)x.ByteLength) })
                .SingleOrDefault();
            if (chunkStats == null || chunkStats.Count != row.ChunkCount || chunkStats.MinOrdinal != 0 ||
                chunkStats.MaxOrdinal != row.ChunkCount - 1 || chunkStats.Bytes != row.DeclaredBytes)
                throw Conflict("transfer_chunks_incomplete", "片段筆數、連續序號或實際位元組總數不完整。");

            row.State = PrtgTransferStates.Validating;
            row.LeaseOwner = leaseOwner;
            row.LeaseUntilUtc = nowUtc + leaseDuration;
            row.Version = checked(row.Version + 1);
            row.UpdatedAtUtc = nowUtc;
            return new PrtgTransferValidationLease(transferId, leaseOwner, row.Version, row.LeaseUntilUtc.Value);
        });
    }

    /// <summary>Reads exactly one persisted chunk after rechecking binding and the current validation lease.</summary>
    public byte[] ReadChunk(PrtgTransferValidationLease lease, int ordinal, PrtgTransferBinding binding, DateTimeOffset nowUtc)
    {
        var normalized = NormalizeBinding(binding);
        using var db = _contextFactory();
        var row = (from chunk in db.PrtgTransferChunks.AsNoTracking()
                   join session in db.PrtgTransferSessions.AsNoTracking() on chunk.TransferId equals session.TransferId
                   where chunk.TransferId == lease.TransferId && chunk.Ordinal == ordinal &&
                         session.OwnerId == normalized.OwnerId && session.ScopeHash == normalized.ScopeHash &&
                         session.SourceIdentityHash == normalized.SourceIdentityHash &&
                         session.State == PrtgTransferStates.Validating && session.Version == lease.Version &&
                         session.LeaseOwner == lease.LeaseOwner && session.LeaseUntilUtc > nowUtc
                   select new { chunk.Payload, chunk.ByteLength, chunk.Sha256 })
            .SingleOrDefault() ?? throw Conflict("validation_lease_stale", "驗證 lease 已失效或片段不存在。");
        if (row.ByteLength is < 1 or > MaxChunkBytes || row.Payload.Length != row.ByteLength || Sha256(row.Payload) != row.Sha256)
            throw Conflict("chunk_integrity_failed", "已保存片段的長度或 SHA-256 驗證失敗。");
        return row.Payload;
    }

    public PrtgTransferStatus CompleteValidation(
        PrtgTransferValidationLease lease,
        string actualPackageSha256,
        string boundedResultManifestJson,
        PrtgTransferBinding binding,
        DateTimeOffset nowUtc)
    {
        var normalized = NormalizeBinding(binding);
        var hash = NormalizeHash(actualPackageSha256);
        if (Encoding.UTF8.GetByteCount(boundedResultManifestJson ?? "") > MaxManifestUtf8Bytes ||
            !IsJsonObject(boundedResultManifestJson))
            throw BadRequest("manifest_invalid", "完成 manifest 無效或超過 64 KiB。");

        return InTransaction(db =>
        {
            var row = db.PrtgTransferSessions.SingleOrDefault(x => x.TransferId == lease.TransferId) ?? throw NotFound();
            RequireBinding(row, normalized);
            ThrowIfExpired(row, nowUtc);
            if (row.State == PrtgTransferStates.Complete)
            {
                if (row.PackageSha256 == hash) return ToStatus(row);
                throw Conflict("package_hash_conflict", "已完成傳輸的 package hash 不相符。");
            }
            if (row.State == PrtgTransferStates.ValidationFailed && row.FailureCode == "package_hash_mismatch" && row.PackageSha256 != hash)
                throw Conflict("package_hash_mismatch", "完整 package SHA-256 與建立時宣告不符。");
            RequireValidationLease(row, lease, nowUtc);
            if (row.ReceivedBytes != row.DeclaredBytes || row.ReceivedChunks != row.ChunkCount)
                throw Conflict("transfer_incomplete", "驗證期間傳輸位元組總數不完整。");
            if (row.PackageSha256 != hash)
            {
                row.State = PrtgTransferStates.ValidationFailed;
                row.FailureCode = "package_hash_mismatch";
                row.AbandonedAtUtc = nowUtc;
                row.LeaseOwner = null; row.LeaseUntilUtc = null;
                row.Version = checked(row.Version + 1); row.UpdatedAtUtc = nowUtc;
                db.SaveChanges();
                throw new DeferredTransferException(Conflict("package_hash_mismatch", "完整 package SHA-256 與建立時宣告不符。"));
            }
            row.State = PrtgTransferStates.Complete;
            row.ResultManifestJson = boundedResultManifestJson;
            row.FailureCode = null;
            row.CompletedAtUtc = nowUtc;
            row.LeaseOwner = null; row.LeaseUntilUtc = null;
            row.Version = checked(row.Version + 1); row.UpdatedAtUtc = nowUtc;
            return ToStatus(row);
        });
    }

    public PrtgTransferStatus FailValidation(
        PrtgTransferValidationLease lease, string failureCode, PrtgTransferBinding binding, DateTimeOffset nowUtc)
    {
        var normalized = NormalizeBinding(binding);
        if (!IsFailureCode(failureCode)) throw BadRequest("failure_code_invalid", "驗證失敗代碼格式無效。");
        return InTransaction(db =>
        {
            var row = db.PrtgTransferSessions.SingleOrDefault(x => x.TransferId == lease.TransferId) ?? throw NotFound();
            RequireBinding(row, normalized);
            ThrowIfExpired(row, nowUtc);
            if (row.State == PrtgTransferStates.ValidationFailed && row.FailureCode == failureCode)
                return ToStatus(row);
            RequireValidationLease(row, lease, nowUtc);
            row.State = PrtgTransferStates.ValidationFailed;
            row.FailureCode = failureCode;
            // Anchor failed-payload retention independently from UpdatedAtUtc, which cleanup cursors update.
            row.AbandonedAtUtc = nowUtc;
            row.LeaseOwner = null; row.LeaseUntilUtc = null;
            row.Version = checked(row.Version + 1); row.UpdatedAtUtc = nowUtc;
            return ToStatus(row);
        });
    }

    /// <summary>Release only this validator's lease after its bounded reader has stopped.</summary>
    public bool ReleaseValidationLease(PrtgTransferValidationLease lease, PrtgTransferBinding binding, DateTimeOffset nowUtc)
    {
        var normalized = NormalizeBinding(binding);
        return InTransaction(db =>
        {
            var row = db.PrtgTransferSessions.SingleOrDefault(x => x.TransferId == lease.TransferId);
            if (row == null) return false;
            RequireBinding(row, normalized);
            if (row.State != PrtgTransferStates.Validating || row.Version != lease.Version || row.LeaseOwner != lease.LeaseOwner)
                return false;
            row.State = PrtgTransferStates.Receiving;
            row.LeaseOwner = null; row.LeaseUntilUtc = null;
            row.Version = checked(row.Version + 1); row.UpdatedAtUtc = nowUtc;
            return true;
        });
    }

    public PrtgTransferStatus Abandon(Guid transferId, PrtgTransferBinding binding, DateTimeOffset nowUtc) =>
        AbandonCore(transferId, binding, nowUtc, allowChangedContext: false);

    /// <summary>
    /// An explicitly authorized owner may abandon their diagnostic payload after a source/scope change.
    /// This reveals no old payload or manifest and never changes formal monitoring data.
    /// The HTTP consumer must freshly verify Maintain and full current visibility.
    /// </summary>
    public void AbandonOwned(Guid transferId, PrtgTransferBinding currentBinding, DateTimeOffset nowUtc) =>
        _ = AbandonCore(transferId, currentBinding, nowUtc, allowChangedContext: true);

    private PrtgTransferStatus AbandonCore(Guid transferId, PrtgTransferBinding binding, DateTimeOffset nowUtc, bool allowChangedContext)
    {
        var normalized = NormalizeBinding(binding);
        return InTransaction(db =>
        {
            var row = db.PrtgTransferSessions.SingleOrDefault(x => x.TransferId == transferId) ?? throw NotFound();
            if (allowChangedContext)
            {
                if (row.OwnerId != normalized.OwnerId)
                    throw new PrtgTransferStoreException("transfer_owner_mismatch", "此診斷傳輸不屬於目前登入者。", 403);
            }
            else RequireBinding(row, normalized);
            if (row.State == PrtgTransferStates.Complete)
                throw Conflict("transfer_already_complete", "完整診斷包不能由未完成傳輸的放棄操作移除。");
            if (row.State == PrtgTransferStates.Abandoned)
                return ToStatus(row);
            if (row.ActiveWriteUntilUtc > nowUtc || row.LeaseUntilUtc > nowUtc)
                throw Conflict("transfer_lease_active", "片段接收或驗證 lease 尚未結束，稍後再放棄。");
            row.State = PrtgTransferStates.Abandoned;
            row.ActiveWriteId = null; row.ActiveWriteBytes = 0; row.ActiveWriteUntilUtc = null;
            row.LeaseOwner = null; row.LeaseUntilUtc = null;
            row.AbandonedAtUtc = nowUtc; row.Version = checked(row.Version + 1); row.UpdatedAtUtc = nowUtc;
            return ToStatus(row);
        });
    }

    /// <summary>
    /// Deletes bounded payload batches after the terminal retention deadline or the fixed maximum
    /// age of incomplete sessions. Active write/validation leases always protect their current reader.
    /// </summary>
    public PrtgTransferCleanupResult CleanupChunkBatch(
        Guid transferId, int afterOrdinal, int maxRows,
        DateTimeOffset completedBeforeUtc, DateTimeOffset abandonedBeforeUtc, DateTimeOffset nowUtc)
    {
        if (afterOrdinal < -1 || maxRows is < 1 or > 4096)
            throw BadRequest("cleanup_range_invalid", "清理游標或批次大小無效。");
        return InTransaction(db =>
        {
            var row = db.PrtgTransferSessions.SingleOrDefault(x => x.TransferId == transferId);
            if (row == null) return new PrtgTransferCleanupResult(0, true, afterOrdinal);
            var incompleteExpired = (row.State == PrtgTransferStates.Receiving || row.State == PrtgTransferStates.Validating) &&
                row.CreatedAtUtc <= completedBeforeUtc &&
                (!row.ActiveWriteUntilUtc.HasValue || row.ActiveWriteUntilUtc <= nowUtc) &&
                (!row.LeaseUntilUtc.HasValue || row.LeaseUntilUtc <= nowUtc);
            var eligible = (row.State == PrtgTransferStates.Complete && row.CompletedAtUtc <= completedBeforeUtc) ||
                           (row.State == PrtgTransferStates.Abandoned && row.AbandonedAtUtc <= abandonedBeforeUtc) ||
                           (row.State == PrtgTransferStates.ValidationFailed && (row.AbandonedAtUtc ?? row.UpdatedAtUtc) <= abandonedBeforeUtc) ||
                           incompleteExpired;
            if (!eligible) throw Conflict("transfer_retention_not_elapsed", "此傳輸尚未到達已設定的完成或放棄清理期限。");

            if (afterOrdinal < row.CleanupAfterOrdinal)
                return new PrtgTransferCleanupResult(0, false, row.CleanupAfterOrdinal);
            if (afterOrdinal > row.CleanupAfterOrdinal)
                throw Conflict("cleanup_cursor_conflict", "清理游標與 durable 清理狀態不一致。");

            var ordinals = db.PrtgTransferChunks.AsNoTracking()
                .Where(x => x.TransferId == transferId && x.Ordinal > afterOrdinal)
                .OrderBy(x => x.Ordinal).Select(x => x.Ordinal).Take(maxRows + 1).ToArray();
            var hasMore = ordinals.Length > maxRows;
            var delete = ordinals.Take(maxRows).ToArray();
            foreach (var ordinal in delete)
                db.Entry(new PrtgTransferChunkRow { TransferId = transferId, Ordinal = ordinal }).State = EntityState.Deleted;

            var deleted = delete.Length;
            if (hasMore)
            {
                row.CleanupAfterOrdinal = delete[^1];
                row.Version = checked(row.Version + 1); row.UpdatedAtUtc = nowUtc;
                return new PrtgTransferCleanupResult(deleted, false, row.CleanupAfterOrdinal);
            }

            db.PrtgTransferSessions.Remove(row);
            return new PrtgTransferCleanupResult(deleted, true, delete.Length == 0 ? afterOrdinal : delete[^1]);
        });
    }

    private T InTransaction<T>(Func<LfDbContext, T> action)
    {
        using var strategyContext = _contextFactory();
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return strategy.Execute(() =>
        {
            using var db = _contextFactory();
            using var transaction = db.Database.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                var result = action(db);
                db.SaveChanges();
                transaction.Commit();
                return result;
            }
            catch (DeferredTransferException ex)
            {
                transaction.Commit();
                throw ex.TransferException;
            }
            catch (DbUpdateConcurrencyException)
            {
                throw Conflict("transfer_version_conflict", "傳輸狀態已由其他程序更新，請讀取最新狀態後重試。");
            }
        });
    }

    private void EnsureCapacity(LfDbContext db, long additionalBytes, DateTimeOffset nowUtc)
    {
        var snapshot = _capacity.Capture();
        if (!snapshot.IsVerified)
            throw new PrtgTransferStoreException("capacity_unverified",
                string.IsNullOrWhiteSpace(snapshot.FailureReason) ? "無法確認資料庫容量，暫不接受傳輸。" : snapshot.FailureReason, 503);
        if (snapshot.SafeAdditionalPayloadBytes < 0)
            throw CapacityExceeded("資料庫容量已低於保留空間門檻。");

        var pending = db.PrtgTransferSessions
            .Where(x => x.State == PrtgTransferStates.Receiving || x.State == PrtgTransferStates.Validating)
            .Sum(x => x.DeclaredBytes - x.ReceivedBytes);
        if (additionalBytes > snapshot.SafeAdditionalPayloadBytes - pending)
            throw CapacityExceeded("此傳輸會耗盡已探測的容量預算；現有 durable 片段已保留。");

        EnsureMemoryCapacity(db, 0, nowUtc);
    }

    private static long WriteBufferReservation(int expectedBytes) => checked((long)expectedBytes * WriteBufferMultiplier);

    private static void EnsureMemoryCapacity(LfDbContext db, long additionalBytes, DateTimeOffset nowUtc)
    {
        var activeWrites = db.PrtgTransferSessions.Where(x => x.ActiveWriteUntilUtc > nowUtc)
            .Sum(x => (long?)x.ActiveWriteBytes) ?? 0;
        var activeValidators = db.PrtgTransferSessions.Count(x =>
            x.State == PrtgTransferStates.Validating && x.LeaseUntilUtc > nowUtc &&
            (x.LeaseOwner == null || !x.LeaseOwner.StartsWith(ExportLeasePrefix)));
        var activeExports = db.PrtgTransferSessions.Count(x =>
            x.State == PrtgTransferStates.Validating && x.LeaseUntilUtc > nowUtc &&
            x.LeaseOwner != null && x.LeaseOwner.StartsWith(ExportLeasePrefix));
        var reserved = checked(activeWrites + checked((long)activeValidators * ValidationBufferReservationBytes) +
            checked((long)activeExports * ExportBufferReservationBytes));
        if (additionalBytes < 0 || additionalBytes > MaxConcurrentBufferBytes - reserved)
            throw CapacityExceeded("PRTG transfer 已達 256 MiB 專用 buffer 預算。");
    }

    private static void ValidateCreateRequest(PrtgTransferCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TransferId == Guid.Empty) throw BadRequest("transfer_id_invalid", "傳輸識別碼無效。");
        if (request.DeclaredBytes <= 0) throw BadRequest("transfer_size_invalid", "宣告檔案長度必須大於零。");
        var expected = 1L + (request.DeclaredBytes - 1) / MaxChunkBytes;
        if (expected > int.MaxValue || request.ChunkCount != expected)
            throw BadRequest("chunk_count_invalid", "片數必須與宣告總 bytes 及 4 MiB 片長相符。");
        _ = NormalizeHash(request.PackageSha256);
        _ = NormalizeBinding(request.Binding);
    }

    private static PrtgTransferBinding NormalizeBinding(PrtgTransferBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var owner = binding.OwnerId?.Trim() ?? "";
        if (owner.Length is < 1 or > 128) throw BadRequest("transfer_owner_invalid", "傳輸 owner 識別無效。");
        return new PrtgTransferBinding(owner, NormalizeHash(binding.ScopeHash), NormalizeHash(binding.SourceIdentityHash));
    }

    private static string NormalizeHash(string? hash)
    {
        if (!IsHash(hash)) throw BadRequest("sha256_invalid", "SHA-256 必須是 64 位十六進位字串。");
        return hash!.ToUpperInvariant();
    }

    private static bool IsHash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static int ExpectedChunkLength(PrtgTransferSessionRow row, int ordinal)
    {
        var offset = checked((long)ordinal * MaxChunkBytes);
        return checked((int)Math.Min(MaxChunkBytes, row.DeclaredBytes - offset));
    }

    private static void RequireBinding(PrtgTransferSessionRow row, PrtgTransferBinding binding)
    {
        if (!string.Equals(row.OwnerId, binding.OwnerId, StringComparison.Ordinal) ||
            !string.Equals(row.ScopeHash, binding.ScopeHash, StringComparison.Ordinal) ||
            !string.Equals(row.SourceIdentityHash, binding.SourceIdentityHash, StringComparison.Ordinal))
            throw new PrtgTransferStoreException("transfer_binding_mismatch", "owner、範圍或來源身分與建立傳輸時不符。", 403);
    }

    private static void ThrowIfIncompleteAgeElapsed(PrtgTransferSessionRow row, DateTimeOffset nowUtc)
    {
        if ((row.State == PrtgTransferStates.Receiving || row.State == PrtgTransferStates.Validating) &&
            row.CreatedAtUtc <= nowUtc - IncompleteSessionLifetime)
            throw new PrtgTransferStoreException("transfer_expired", "此診斷傳輸已超過 7 天續傳期限；請重新匯入原檔。", 410);
    }

    private static void ThrowIfExpired(PrtgTransferSessionRow row, DateTimeOffset nowUtc)
    {
        var expired = row.State switch
        {
            PrtgTransferStates.Receiving or PrtgTransferStates.Validating =>
                row.CreatedAtUtc <= nowUtc - IncompleteSessionLifetime &&
                (!row.ActiveWriteUntilUtc.HasValue || row.ActiveWriteUntilUtc <= nowUtc) &&
                (!row.LeaseUntilUtc.HasValue || row.LeaseUntilUtc <= nowUtc),
            PrtgTransferStates.ValidationFailed => (row.AbandonedAtUtc ?? row.UpdatedAtUtc) <= nowUtc - FailedSessionRetention,
            PrtgTransferStates.Complete => row.CompletedAtUtc <= nowUtc - CompletedRetention,
            PrtgTransferStates.Abandoned => row.AbandonedAtUtc <= nowUtc - AbandonedRetention,
            _ => false
        };
        if (expired)
            throw new PrtgTransferStoreException("transfer_expired", "此診斷傳輸已到期；請重新匯入原檔。", 410);
    }

    private static void RequireActiveWrite(PrtgTransferSessionRow row, PrtgTransferChunkWriteLease lease, DateTimeOffset nowUtc)
    {
        if (row.State != PrtgTransferStates.Receiving || row.ActiveWriteId != lease.WriteId ||
            row.ActiveWriteUntilUtc <= nowUtc || row.Version != lease.Version || row.ActiveWriteBytes != WriteBufferReservation(lease.ExpectedBytes))
            throw Conflict("chunk_write_lease_stale", "片段寫入 lease 已失效或已被其他程序取代。");
    }

    private static void RequireValidationLease(PrtgTransferSessionRow row, PrtgTransferValidationLease lease, DateTimeOffset nowUtc)
    {
        if (row.State != PrtgTransferStates.Validating || row.LeaseOwner != lease.LeaseOwner ||
            row.LeaseUntilUtc <= nowUtc || row.Version != lease.Version)
            throw Conflict("validation_lease_stale", "驗證 lease 已逾期或已由其他程序接手。");
    }

    private static void ClearWriteLease(PrtgTransferSessionRow row)
    {
        row.ActiveWriteId = null; row.ActiveWriteBytes = 0; row.ActiveWriteUntilUtc = null;
    }

    private static bool IsJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsFailureCode(string? code) => code is { Length: > 0 and <= 64 } &&
        code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static PrtgTransferStatus ToStatus(PrtgTransferSessionRow row) => new(
        row.TransferId, row.State, row.DeclaredBytes, row.ReceivedBytes, row.ChunkCount, row.ReceivedChunks,
        row.Version, row.PackageSha256, row.ResultManifestJson, row.FailureCode, row.CreatedAtUtc,
        row.UpdatedAtUtc, row.CompletedAtUtc, row.AbandonedAtUtc);

    private static PrtgTransferStoreException BadRequest(string code, string message) => new(code, message, 400);
    private static PrtgTransferStoreException Conflict(string code, string message) => new(code, message, 409);
    private static PrtgTransferStoreException CapacityExceeded(string message) => new("capacity_exceeded", message, 507);
    private static PrtgTransferStoreException NotFound() => new("transfer_not_found", "找不到符合目前身分與範圍的傳輸。", 404);

    private sealed class DeferredTransferException(PrtgTransferStoreException transferException) : Exception
    {
        public PrtgTransferStoreException TransferException { get; } = transferException;
    }
}

public sealed record PrtgTransferCleanupResult(int DeletedChunks, bool SessionDeleted, int LastOrdinal);
