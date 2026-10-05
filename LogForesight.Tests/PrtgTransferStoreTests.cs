using System.Security.Cryptography;
using System.Data.Common;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Real EF/SQLite contract tests for the diagnostic-only transfer state machine.</summary>
public sealed class PrtgTransferStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly MutableCapacity _capacity = new(new(true, 64L * 1024 * 1024, "sqlite-test-volume", null));
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly PrtgTransferBinding Binding = new("owner-a", Hash("scope-a"u8.ToArray()), Hash("source-a"u8.ToArray()));
    private EfPrtgTransferStore Store() => new(_fx.NewContext, _capacity);

    [Fact]
    public void ExportReadersShareDurableBufferLedgerAndReleaseOnlyTheirOwnLease()
    {
        var store = Store();
        var first = store.AcquireExportReadLease(Guid.NewGuid(), "one", Binding, Start);
        var second = Store().AcquireExportReadLease(Guid.NewGuid(), "two", Binding, Start);
        Assert.Equal(first, store.AcquireExportReadLease(first.TransferId, "one", Binding, Start.AddSeconds(1)));
        Assert.Equal(507, Assert.Throws<PrtgTransferStoreException>(() =>
            store.AcquireExportReadLease(Guid.NewGuid(), "three", Binding, Start)).HttpStatusCode);
        var upload = Create("upload"u8.ToArray());
        Assert.Equal(507, Assert.Throws<PrtgTransferStoreException>(() =>
            store.BeginChunkWrite(upload, 0, 6, Binding, Start)).HttpStatusCode);
        Assert.False(store.ReleaseExportReadLease(first with { LeaseOwner = second.LeaseOwner }, Start.AddSeconds(2)));
        Assert.True(Store().ReleaseExportReadLease(first, Start.AddSeconds(3)));
        Assert.False(store.ReleaseExportReadLease(first, Start.AddSeconds(4)));
        var write = store.BeginChunkWrite(upload, 0, 6, Binding, Start.AddSeconds(5));
        store.ReleaseChunkWrite(write, Binding, Start.AddSeconds(6));
        Assert.Equal(1, store.ExpireExportReadLeases(Start.AddHours(2)));
        Assert.Equal(PrtgTransferStates.Abandoned, store.GetStatus(second.TransferId, Binding).State);
    }

    [Fact]
    public void SQLitePersistsAndResumesExactChunkAcrossNewStoreInstances()
    {
        var body = "small archive"u8.ToArray();
        var id = Create(body);
        var firstStore = Store();
        var lease = firstStore.BeginChunkWrite(id, 0, body.Length, Binding, Start.AddSeconds(1));
        var accepted = firstStore.AcceptChunk(lease, body, Binding, Start.AddSeconds(2));
        Assert.False(accepted.AlreadyAccepted);

        var resumedStore = Store();
        Assert.Equal(body.Length, resumedStore.GetStatus(id, Binding).ReceivedBytes);
        var retryLease = resumedStore.BeginChunkWrite(id, 0, body.Length, Binding, Start.AddSeconds(3));
        Assert.True(resumedStore.AcceptChunk(retryLease, body, Binding, Start.AddSeconds(4)).AlreadyAccepted);
        Assert.Equal(1, resumedStore.GetStatus(id, Binding).ReceivedChunks);
    }

    [Fact]
    public void FileBackedSqliteResumesAfterAllContextsAndConnectionsClose()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "logforesight-r11-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var databasePath = Path.Combine(workspace, "transfer.sqlite");
            LfDbContext OpenContext() => new(new DbContextOptionsBuilder<LfDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options);
            using (var initial = OpenContext()) initial.Database.EnsureCreated();
            var fileStore = new EfPrtgTransferStore(OpenContext, _capacity);
            var body = new byte[EfPrtgTransferStore.MaxChunkBytes + 1];
            body[^1] = 9;
            var id = Guid.NewGuid();
            fileStore.Create(Request(id, body), Start);
            var lease = fileStore.BeginChunkWrite(id, 0, EfPrtgTransferStore.MaxChunkBytes, Binding, Start.AddSeconds(1));
            fileStore.AcceptChunk(lease, body[..EfPrtgTransferStore.MaxChunkBytes], Binding, Start.AddSeconds(2));

            // Every store call owns/disposes its context; a new store opens the same file after prior connections close.
            var reopenedStore = new EfPrtgTransferStore(OpenContext, _capacity);
            Assert.Equal(EfPrtgTransferStore.MaxChunkBytes,
                reopenedStore.GetStatus(id, Binding).ReceivedBytes);
            var tail = reopenedStore.BeginChunkWrite(id, 1, 1, Binding, Start.AddSeconds(3));
            reopenedStore.AcceptChunk(tail, body[^1..], Binding, Start.AddSeconds(4));
            Assert.Equal(body.Length, reopenedStore.GetStatus(id, Binding).ReceivedBytes);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void ConflictingRetryAndMismatchedBindingAreRejected()
    {
        var body = "abc"u8.ToArray();
        var id = Create(body);
        var store = Store();
        var lease = store.BeginChunkWrite(id, 0, body.Length, Binding, Start.AddSeconds(1));
        store.AcceptChunk(lease, body, Binding, Start.AddSeconds(2));
        var retry = store.BeginChunkWrite(id, 0, body.Length, Binding, Start.AddSeconds(3));
        AssertCode("chunk_content_conflict", () => store.AcceptChunk(retry, "xyz"u8.ToArray(), Binding, Start.AddSeconds(4)));
        AssertCode("transfer_binding_mismatch", () => store.GetStatus(id, Binding with { OwnerId = "owner-b" }));
        AssertCode("transfer_binding_mismatch", () => store.GetStatus(id, Binding with { ScopeHash = Hash("scope-b"u8.ToArray()) }));
        AssertCode("transfer_binding_mismatch", () => store.GetStatus(id, Binding with { SourceIdentityHash = Hash("source-b"u8.ToArray()) }));
    }

    [Fact]
    public void ExactDuplicateWithFreshWriteLeaseReleasesItsReservationForNextChunk()
    {
        var body = new byte[EfPrtgTransferStore.MaxChunkBytes + 1];
        var id = Create(body);
        var store = Store();
        var first = store.BeginChunkWrite(id, 0, EfPrtgTransferStore.MaxChunkBytes, Binding, Start.AddSeconds(1));
        store.AcceptChunk(first, body[..EfPrtgTransferStore.MaxChunkBytes], Binding, Start.AddSeconds(2));

        var duplicate = store.BeginChunkWrite(id, 0, EfPrtgTransferStore.MaxChunkBytes, Binding, Start.AddSeconds(3));
        Assert.True(store.AcceptChunk(duplicate, body[..EfPrtgTransferStore.MaxChunkBytes], Binding, Start.AddSeconds(4)).AlreadyAccepted);

        var next = store.BeginChunkWrite(id, 1, 1, Binding, Start.AddSeconds(5));
        Assert.Equal(1, next.Ordinal);
    }

    [Fact]
    public void ReplayOfExpiredDuplicateLeaseDoesNotReleaseAnotherChunksCurrentLease()
    {
        var body = new byte[2 * EfPrtgTransferStore.MaxChunkBytes + 1];
        var id = Create(body);
        var store = Store();
        var first = store.BeginChunkWrite(id, 0, EfPrtgTransferStore.MaxChunkBytes, Binding, Start.AddSeconds(1));
        store.AcceptChunk(first, body[..EfPrtgTransferStore.MaxChunkBytes], Binding, Start.AddSeconds(2));
        var staleDuplicate = store.BeginChunkWrite(id, 0, EfPrtgTransferStore.MaxChunkBytes, Binding, Start.AddSeconds(3));

        var current = store.BeginChunkWrite(id, 1, EfPrtgTransferStore.MaxChunkBytes, Binding,
            Start.AddSeconds(3).AddMinutes(5).AddSeconds(1));
        Assert.True(store.AcceptChunk(staleDuplicate, body[..EfPrtgTransferStore.MaxChunkBytes], Binding,
            Start.AddSeconds(3).AddMinutes(5).AddSeconds(2)).AlreadyAccepted);

        AssertCode("chunk_write_in_progress", () => store.BeginChunkWrite(id, 2, 1, Binding,
            Start.AddSeconds(3).AddMinutes(5).AddSeconds(3)));
        store.ReleaseChunkWrite(current, Binding, Start.AddSeconds(3).AddMinutes(5).AddSeconds(4));
        Assert.Equal(1, store.GetStatus(id, Binding).ReceivedChunks);
    }

    [Fact]
    public void MissingChunkCannotAcquireLeaseAndDeclaredBoundsAreEnforced()
    {
        var bytes = new byte[EfPrtgTransferStore.MaxChunkBytes + 1];
        bytes[^1] = 7;
        var id = Create(bytes);
        var store = Store();
        var first = store.BeginChunkWrite(id, 0, EfPrtgTransferStore.MaxChunkBytes, Binding, Start.AddSeconds(1));
        store.AcceptChunk(first, bytes[..EfPrtgTransferStore.MaxChunkBytes], Binding, Start.AddSeconds(2));
        AssertCode("transfer_incomplete", () => store.AcquireValidationLease(id, Binding, "worker-1", TimeSpan.FromMinutes(1), Start.AddSeconds(3)));
        AssertCode("chunk_count_invalid", () => Store().Create(new(Guid.NewGuid(), long.MaxValue, int.MaxValue, Hash(bytes), Binding), Start));
    }

    [Fact]
    public void ValidationLeaseExpiryAllowsTakeoverButOldLeaseCannotComplete()
    {
        var body = "abc"u8.ToArray();
        var id = Create(body);
        var store = Store();
        var write = store.BeginChunkWrite(id, 0, body.Length, Binding, Start.AddSeconds(1));
        store.AcceptChunk(write, body, Binding, Start.AddSeconds(2));
        var oldLease = store.AcquireValidationLease(id, Binding, "worker-old", TimeSpan.FromMinutes(1), Start.AddSeconds(3));
        var sameInstantDifferentOffset = Store().AcquireValidationLease(id, Binding, "worker-old",
            TimeSpan.FromMinutes(1), Start.AddSeconds(4).ToOffset(TimeSpan.FromHours(8)));
        Assert.Equal(oldLease.Version, sameInstantDifferentOffset.Version);
        var newLease = store.AcquireValidationLease(id, Binding, "worker-new", TimeSpan.FromMinutes(1), Start.AddMinutes(2));
        AssertCode("validation_lease_stale", () => store.CompleteValidation(oldLease, Hash(body), "{}", Binding, Start.AddMinutes(2).AddSeconds(1)));
        Assert.Equal("complete", store.CompleteValidation(newLease, Hash(body), "{}", Binding, Start.AddMinutes(2).AddSeconds(2)).State);
    }

    [Fact]
    public void WrongPackageHashPersistsFailureAndCapacityMustBeVerifiedAndReserved()
    {
        var body = "abc"u8.ToArray();
        var id = Create(body);
        var store = Store();
        var write = store.BeginChunkWrite(id, 0, body.Length, Binding, Start.AddSeconds(1));
        store.AcceptChunk(write, body, Binding, Start.AddSeconds(2));
        var validation = store.AcquireValidationLease(id, Binding, "worker", TimeSpan.FromMinutes(1), Start.AddSeconds(3));
        AssertCode("package_hash_mismatch", () => store.CompleteValidation(validation, Hash("bad"u8.ToArray()), "{}", Binding, Start.AddSeconds(4)));
        Assert.Equal("validation-failed", Store().GetStatus(id, Binding).State);

        _capacity.Value = new(false, 0, null, "probe unavailable");
        AssertCode("capacity_unverified", () => Store().Create(Request(Guid.NewGuid(), body), Start.AddMinutes(1)));
        _capacity.Value = new(true, body.Length - 1, "sqlite-test-volume", null);
        AssertCode("capacity_exceeded", () => Store().Create(Request(Guid.NewGuid(), body), Start.AddMinutes(1)));
    }

    [Fact]
    public void DurableReservationsPreventAggregateCapacityOvercommit()
    {
        var first = "12345678"u8.ToArray();
        var second = "1234"u8.ToArray();
        _capacity.Value = new(true, 10, "sqlite-test-volume", null);
        Create(first);
        AssertCode("capacity_exceeded", () => Store().Create(Request(Guid.NewGuid(), second), Start.AddSeconds(1)));
    }

    [Fact]
    public void WriteBufferLeasesReserveFourTimesBodyAndStopAtTransferBudget()
    {
        _capacity.Value = new(true, 128L * 1024 * 1024, "sqlite-test-volume", null);
        var ids = new List<Guid>();
        for (var i = 0; i < 17; i++)
        {
            var id = Guid.NewGuid();
            Store().Create(new(id, EfPrtgTransferStore.MaxChunkBytes, 1, Hash("declared"u8.ToArray()), Binding), Start.AddSeconds(i));
            ids.Add(id);
        }
        for (var i = 0; i < 16; i++)
            Store().BeginChunkWrite(ids[i], 0, EfPrtgTransferStore.MaxChunkBytes, Binding, Start.AddSeconds(30 + i));
        AssertCode("capacity_exceeded", () => Store().BeginChunkWrite(ids[16], 0,
            EfPrtgTransferStore.MaxChunkBytes, Binding, Start.AddSeconds(50)));
    }

    [Fact]
    public void ValidationLeasesReserveAndReleaseSixteenMiBEach()
    {
        var body = "x"u8.ToArray();
        var ids = new List<Guid>();
        for (var i = 0; i < 17; i++)
        {
            var id = Create(body);
            var store = Store();
            var write = store.BeginChunkWrite(id, 0, body.Length, Binding, Start.AddSeconds(10 + i * 2));
            store.AcceptChunk(write, body, Binding, Start.AddSeconds(11 + i * 2));
            ids.Add(id);
        }
        var leases = new List<PrtgTransferValidationLease>();
        for (var i = 0; i < 16; i++)
            leases.Add(Store().AcquireValidationLease(ids[i], Binding, "validator-" + i,
                TimeSpan.FromMinutes(1), Start.AddMinutes(1)));
        AssertCode("capacity_exceeded", () => Store().AcquireValidationLease(ids[16], Binding, "validator-last",
            TimeSpan.FromMinutes(1), Start.AddMinutes(1)));

        Store().CompleteValidation(leases[0], Hash(body), "{}", Binding, Start.AddMinutes(1).AddSeconds(1));
        var lastLease = Store().AcquireValidationLease(ids[16], Binding, "validator-last",
            TimeSpan.FromMinutes(1), Start.AddMinutes(1).AddSeconds(2));
        Assert.Equal("validator-last", lastLease.LeaseOwner);
    }

    [Fact]
    public void ExecutionStrategyRetriesCommitAcknowledgementLossForEveryMutation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var commitFault = new CommitAcknowledgementLossInterceptor();
        DbContextOptions<LfDbContext> Options() => new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IExecutionStrategyFactory, CommitAcknowledgementRetryStrategyFactory>()
            .AddInterceptors(commitFault)
            .Options;
        using (var initial = new LfDbContext(Options())) initial.Database.EnsureCreated();
        LfDbContext Context() => new(Options());
        var store = new EfPrtgTransferStore(Context, _capacity);

        var body = "commit-ack"u8.ToArray();
        var binding = Binding;
        var id = Guid.NewGuid();
        commitFault.FailAfterNextCommit();
        Assert.Equal(PrtgTransferStates.Receiving, store.Create(Request(id, body), Start).State);
        using (var db = Context()) Assert.Equal(1, db.PrtgTransferSessions.Count(x => x.TransferId == id));

        commitFault.FailAfterNextCommit();
        var write = store.BeginChunkWrite(id, 0, body.Length, binding, Start.AddSeconds(1));
        Assert.NotEqual(Guid.Empty, write.WriteId);

        commitFault.FailAfterNextCommit();
        var receipt = store.AcceptChunk(write, body, binding, Start.AddSeconds(2));
        Assert.True(receipt.AlreadyAccepted);
        Assert.Equal(1, store.GetStatus(id, binding).ReceivedChunks);

        commitFault.FailAfterNextCommit();
        var validation = store.AcquireValidationLease(id, binding, "worker-main", TimeSpan.FromMinutes(1), Start.AddSeconds(3));
        Assert.Equal("worker-main", validation.LeaseOwner);
        commitFault.FailAfterNextCommit();
        Assert.Equal(PrtgTransferStates.Complete,
            store.CompleteValidation(validation, Hash(body), "{}", binding, Start.AddSeconds(4)).State);

        var mismatchId = Guid.NewGuid();
        store.Create(Request(mismatchId, body), Start.AddSeconds(5));
        var mismatchWrite = store.BeginChunkWrite(mismatchId, 0, body.Length, binding, Start.AddSeconds(6));
        store.AcceptChunk(mismatchWrite, body, binding, Start.AddSeconds(7));
        var mismatchLease = store.AcquireValidationLease(mismatchId, binding, "worker-mismatch", TimeSpan.FromMinutes(1), Start.AddSeconds(8));
        commitFault.FailAfterNextCommit();
        AssertCode("package_hash_mismatch", () => store.CompleteValidation(mismatchLease, Hash("wrong"u8.ToArray()), "{}", binding, Start.AddSeconds(9)));
        Assert.Equal(PrtgTransferStates.ValidationFailed, store.GetStatus(mismatchId, binding).State);

        var failId = Guid.NewGuid();
        store.Create(Request(failId, body), Start.AddMinutes(1));
        var failWrite = store.BeginChunkWrite(failId, 0, body.Length, binding, Start.AddMinutes(1).AddSeconds(1));
        store.AcceptChunk(failWrite, body, binding, Start.AddMinutes(1).AddSeconds(2));
        var failLease = store.AcquireValidationLease(failId, binding, "worker-fail", TimeSpan.FromMinutes(1), Start.AddMinutes(1).AddSeconds(3));
        commitFault.FailAfterNextCommit();
        Assert.Equal(PrtgTransferStates.ValidationFailed,
            store.FailValidation(failLease, "parser-rejected", binding, Start.AddMinutes(1).AddSeconds(4)).State);

        var releaseId = Guid.NewGuid();
        store.Create(Request(releaseId, body), Start.AddMinutes(2));
        var releaseWrite = store.BeginChunkWrite(releaseId, 0, body.Length, binding, Start.AddMinutes(2).AddSeconds(1));
        commitFault.FailAfterNextCommit();
        store.ReleaseChunkWrite(releaseWrite, binding, Start.AddMinutes(2).AddSeconds(2));
        Assert.Equal(0, store.GetStatus(releaseId, binding).ReceivedChunks);
        Assert.NotEqual(Guid.Empty, store.BeginChunkWrite(releaseId, 0, body.Length, binding, Start.AddMinutes(2).AddSeconds(3)).WriteId);

        var abandonId = Guid.NewGuid();
        store.Create(Request(abandonId, body), Start.AddMinutes(3));
        commitFault.FailAfterNextCommit();
        Assert.Equal(PrtgTransferStates.Abandoned, store.Abandon(abandonId, binding, Start.AddMinutes(3).AddSeconds(1)).State);

        var cleanupId = Guid.NewGuid();
        store.Create(Request(cleanupId, body), Start.AddMinutes(4));
        var cleanupWrite = store.BeginChunkWrite(cleanupId, 0, body.Length, binding, Start.AddMinutes(4).AddSeconds(1));
        store.AcceptChunk(cleanupWrite, body, binding, Start.AddMinutes(4).AddSeconds(2));
        store.Abandon(cleanupId, binding, Start.AddMinutes(4).AddSeconds(3));
        commitFault.FailAfterNextCommit();
        var cleanup = store.CleanupChunkBatch(cleanupId, -1, 10, Start, Start.AddMinutes(4).AddSeconds(3), Start.AddMinutes(4).AddSeconds(4));
        Assert.True(cleanup.SessionDeleted);
        Assert.True(store.CleanupChunkBatch(cleanupId, -1, 10, Start, Start.AddMinutes(4).AddSeconds(3), Start.AddMinutes(4).AddSeconds(5)).SessionDeleted);
    }

    [Fact]
    public void FailedChunkTransactionDoesNotLeaveChunkOrAdvanceCounters()
    {
        var body = "abc"u8.ToArray();
        var id = Create(body);
        var store = Store();
        var lease = store.BeginChunkWrite(id, 0, body.Length, Binding, Start.AddSeconds(1));
        using (var db = _fx.NewContext())
            db.Database.ExecuteSqlRaw("CREATE TRIGGER reject_transfer_chunk BEFORE INSERT ON lf_prtg_transfer_chunks BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");

        Assert.ThrowsAny<Exception>(() => store.AcceptChunk(lease, body, Binding, Start.AddSeconds(2)));
        var status = Store().GetStatus(id, Binding);
        Assert.Equal(0, status.ReceivedChunks);
        Assert.Equal(0, status.ReceivedBytes);
        using (var db = _fx.NewContext()) Assert.Equal(0, db.PrtgTransferChunks.Count(x => x.TransferId == id));
    }

    [Fact]
    public void TransferWritesLeaveFormalPrtgAndPolicySentinelsUntouched()
    {
        const string policyKey = "prtg_alert_policy";
        using (var db = _fx.NewContext())
        {
            db.Blobs.Add(new BlobRow { BlobKey = policyKey, Content = "sentinel-policy", Version = 7, UpdatedAt = Start.UtcDateTime });
            db.PrtgDevices.Add(new PrtgDeviceRow { Objid = 90210, Name = "sentinel-device", GroupPath = "root", Paused = false, SyncedAt = Start.UtcDateTime, CreatedAt = Start.UtcDateTime });
            db.SaveChanges();
        }

        var body = "archive"u8.ToArray();
        Create(body);
        using var verify = _fx.NewContext();
        var blob = Assert.Single(verify.Blobs.Where(x => x.BlobKey == policyKey));
        var device = Assert.Single(verify.PrtgDevices.Where(x => x.Objid == 90210));
        Assert.Equal("sentinel-policy", blob.Content);
        Assert.Equal(7, blob.Version);
        Assert.Equal("sentinel-device", device.Name);
    }

    [Fact]
    public void AbandonedChunksCleanInBoundedKeysetOnlyAfterRetention()
    {
        var body = new byte[EfPrtgTransferStore.MaxChunkBytes + 1];
        var id = Create(body);
        var store = Store();
        var first = store.BeginChunkWrite(id, 0, EfPrtgTransferStore.MaxChunkBytes, Binding, Start.AddSeconds(1));
        store.AcceptChunk(first, body[..EfPrtgTransferStore.MaxChunkBytes], Binding, Start.AddSeconds(2));
        var last = store.BeginChunkWrite(id, 1, 1, Binding, Start.AddSeconds(3));
        store.AcceptChunk(last, body[^1..], Binding, Start.AddSeconds(4));
        store.Abandon(id, Binding, Start.AddSeconds(5));
        AssertCode("transfer_retention_not_elapsed", () => store.CleanupChunkBatch(id, -1, 1, Start, Start.AddSeconds(4), Start.AddSeconds(6)));
        var batch = store.CleanupChunkBatch(id, -1, 1, Start, Start.AddSeconds(5), Start.AddSeconds(6));
        Assert.Equal(1, batch.DeletedChunks);
        Assert.False(batch.SessionDeleted);
        var replay = store.CleanupChunkBatch(id, -1, 1, Start, Start.AddSeconds(5), Start.AddSeconds(6).AddMilliseconds(1));
        Assert.Equal(0, replay.DeletedChunks);
        Assert.Equal(batch.LastOrdinal, replay.LastOrdinal);
        var tail = store.CleanupChunkBatch(id, replay.LastOrdinal, 1, Start, Start.AddSeconds(5), Start.AddSeconds(7));
        Assert.True(tail.SessionDeleted);
        Assert.Equal(1, tail.DeletedChunks);
    }

    [Fact]
    public void StoppedValidatorReleasesOnlyItsLeaseAndKeepsAcceptedChunks()
    {
        var body = "diagnostic"u8.ToArray();
        var id = Create(body);
        var store = Store();
        var write = store.BeginChunkWrite(id, 0, body.Length, Binding, Start.AddSeconds(1));
        store.AcceptChunk(write, body, Binding, Start.AddSeconds(2));
        var first = store.AcquireValidationLease(id, Binding, "first", TimeSpan.FromMinutes(1), Start.AddSeconds(3));
        Assert.True(store.ReleaseValidationLease(first, Binding, Start.AddSeconds(4)));
        Assert.Equal(body.Length, store.GetStatus(id, Binding).ReceivedBytes);
        var next = store.AcquireValidationLease(id, Binding, "next", TimeSpan.FromMinutes(1), Start.AddSeconds(5));
        Assert.False(store.ReleaseValidationLease(first, Binding, Start.AddSeconds(6)));
        Assert.Equal(PrtgTransferStates.Validating, store.GetStatus(id, Binding).State);
        Assert.True(store.ReleaseValidationLease(next, Binding, Start.AddSeconds(7)));
        Assert.False(store.ReleaseValidationLease(next, Binding, Start.AddSeconds(8)));
    }

    [Fact]
    public void ExplicitOwnerAbandonCanReleaseObsoleteContextWithoutRevealingPayload()
    {
        var id = Create("diagnostic"u8.ToArray());
        var store = Store();
        var changed = Binding with { ScopeHash = Hash("new-scope"u8.ToArray()), SourceIdentityHash = Hash("new-source"u8.ToArray()) };
        var denied = Assert.Throws<PrtgTransferStoreException>(() => store.AbandonOwned(id,
            changed with { OwnerId = "other-owner" }, Start.AddSeconds(1)));
        Assert.Equal("transfer_owner_mismatch", denied.Code);
        Assert.Equal(PrtgTransferStates.Receiving, store.GetStatus(id, Binding).State);
        store.AbandonOwned(id, changed, Start.AddSeconds(2));
        Assert.Equal(PrtgTransferStates.Abandoned, store.GetStatus(id, Binding).State);
        store.AbandonOwned(id, changed, Start.AddSeconds(3));
    }

    private Guid Create(byte[] body)
    {
        var id = Guid.NewGuid();
        Store().Create(Request(id, body), Start);
        return id;
    }

    private static PrtgTransferCreateRequest Request(Guid id, byte[] body) =>
        new(id, body.LongLength, checked((int)(1L + (body.LongLength - 1) / EfPrtgTransferStore.MaxChunkBytes)), Hash(body), Binding);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void AssertCode(string code, Action action)
    {
        var ex = Assert.Throws<PrtgTransferStoreException>(action);
        Assert.Equal(code, ex.Code);
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private sealed class MutableCapacity(PrtgTransferCapacitySnapshot initial) : IPrtgTransferCapacityProvider
    {
        public PrtgTransferCapacitySnapshot Value { get; set; } = initial;
        public PrtgTransferCapacitySnapshot Capture() => Value;
    }
}

internal sealed class CommitAcknowledgementRetryStrategyFactory : IExecutionStrategyFactory
{
    private readonly ExecutionStrategyDependencies _dependencies;

    public CommitAcknowledgementRetryStrategyFactory(ExecutionStrategyDependencies dependencies) => _dependencies = dependencies;

    public IExecutionStrategy Create() => new CommitAcknowledgementRetryStrategy(_dependencies);
}

internal sealed class CommitAcknowledgementRetryStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, 3, TimeSpan.Zero)
{
    protected override bool ShouldRetryOn(Exception exception) => exception is CommitAcknowledgementLostException;
}

internal sealed class CommitAcknowledgementLossInterceptor : DbTransactionInterceptor
{
    private int _failNextCommit;

    public void FailAfterNextCommit() => Interlocked.Exchange(ref _failNextCommit, 1);

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (Interlocked.Exchange(ref _failNextCommit, 0) == 1)
            throw new CommitAcknowledgementLostException();
    }
}

internal sealed class CommitAcknowledgementLostException : Exception { }
