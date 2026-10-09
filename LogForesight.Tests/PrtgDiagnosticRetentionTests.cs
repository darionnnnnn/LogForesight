using System.Security.Cryptography;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgDiagnosticRetentionTests
{
    [Fact]
    public async Task ActualTickReclaimsExpiredFailedAndInterruptedPayloadsWithoutDeletingLiveLeases()
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-prtg-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, root);
            var now = DateTimeOffset.UtcNow;
            var complete = Guid.NewGuid();
            var expiredReceiving = Guid.NewGuid();
            var failed = Guid.NewGuid();
            var expiredValidating = Guid.NewGuid();
            var recentAbandon = Guid.NewGuid();
            var recentReceiving = Guid.NewGuid();
            var recentFailed = Guid.NewGuid();
            var activeReceiving = Guid.NewGuid();
            var activeValidating = Guid.NewGuid();
            var shortPayload = new byte[1024];
            using (var db = backend.CreateContext())
            {
                var sessions = new[]
                {
                    Session(complete, PrtgTransferStates.Complete, now.AddDays(-8), 10L * shortPayload.Length, 10,
                        completed: now.AddDays(-8)),
                    Session(expiredReceiving, PrtgTransferStates.Receiving, now.AddDays(-8), shortPayload.Length, 1),
                    Session(failed, PrtgTransferStates.ValidationFailed, now.AddDays(-10), shortPayload.Length, 1,
                        updated: now.AddDays(-2), abandoned: now.AddDays(-2)),
                    Session(expiredValidating, PrtgTransferStates.Validating, now.AddDays(-8), shortPayload.Length, 1,
                        updated: now.AddDays(-8), leaseOwner: "crashed-validator", leaseUntil: now.AddHours(-1)),
                    Session(recentAbandon, PrtgTransferStates.Abandoned, now.AddHours(-1), shortPayload.Length, 1,
                        abandoned: now.AddHours(-1)),
                    Session(recentReceiving, PrtgTransferStates.Receiving, now.AddHours(-1), shortPayload.Length, 1),
                    Session(recentFailed, PrtgTransferStates.ValidationFailed, now.AddDays(-8), shortPayload.Length, 1,
                        updated: now.AddHours(-1), abandoned: now.AddHours(-1)),
                    Session(activeReceiving, PrtgTransferStates.Receiving, now.AddDays(-8), shortPayload.Length, 1,
                        activeWriteUntil: now.AddMinutes(10)),
                    Session(activeValidating, PrtgTransferStates.Validating, now.AddDays(-8), shortPayload.Length, 1,
                        updated: now.AddDays(-8), leaseOwner: "live-validator", leaseUntil: now.AddHours(1))
                };
                db.PrtgTransferSessions.AddRange(sessions);
                AddChunks(db, complete, 10, shortPayload, now.AddDays(-8));
                foreach (var id in new[] { expiredReceiving, failed, expiredValidating, recentAbandon,
                             recentReceiving, recentFailed, activeValidating })
                    AddChunks(db, id, 1, shortPayload, now);
                db.SaveChanges();
            }
            var capacity = new PrtgTransferCapacityProbe(backend);
            var transferStore = new EfPrtgTransferStore(backend, capacity);
            var failedBinding = new PrtgTransferBinding("test", new string('A', 64), new string('B', 64));
            using (var beforeRead = backend.CreateContext())
            {
                var original = beforeRead.PrtgTransferSessions.Single(row => row.TransferId == recentFailed);
                var failureAt = original.AbandonedAtUtc;
                var lastUpdatedAt = original.UpdatedAtUtc;
                Assert.Equal(PrtgTransferStates.ValidationFailed, transferStore.GetStatus(recentFailed, failedBinding).State);
                using var afterRead = backend.CreateContext();
                var unchanged = afterRead.PrtgTransferSessions.Single(row => row.TransferId == recentFailed);
                Assert.Equal(failureAt, unchanged.AbandonedAtUtc);
                Assert.Equal(lastUpdatedAt, unchanged.UpdatedAtUtc);
            }
            // Each new service instance models process restart; cleanup resumes from the durable ordinal.
            Assert.Equal(11, await new PrtgDiagnosticRetentionHostedService(backend, capacity).TickAsync(now, CancellationToken.None));
            using (var verify = backend.CreateContext())
            {
                Assert.Equal(7, verify.PrtgTransferSessions.Single(row => row.TransferId == complete).CleanupAfterOrdinal);
                Assert.Equal(2, verify.PrtgTransferChunks.Count(row => row.TransferId == complete));
                Assert.False(verify.PrtgTransferSessions.Any(row => row.TransferId == expiredReceiving));
                Assert.False(verify.PrtgTransferSessions.Any(row => row.TransferId == failed));
                Assert.False(verify.PrtgTransferSessions.Any(row => row.TransferId == expiredValidating));
                Assert.True(verify.PrtgTransferSessions.Any(row => row.TransferId == activeReceiving));
                Assert.True(verify.PrtgTransferSessions.Any(row => row.TransferId == activeValidating));
                Assert.Equal(6L * shortPayload.Length, verify.PrtgTransferChunks.Sum(row => (long)row.ByteLength));
            }
            Assert.Equal(2, await new PrtgDiagnosticRetentionHostedService(backend, capacity).TickAsync(now, CancellationToken.None));
            using (var verify = backend.CreateContext())
            {
                Assert.Equal(5, verify.PrtgTransferSessions.Count());
                Assert.False(verify.PrtgTransferSessions.Any(row => row.TransferId == complete));
                Assert.Equal(4, verify.PrtgTransferChunks.Count());
                Assert.Equal(4L * shortPayload.Length, verify.PrtgTransferChunks.Sum(row => (long)row.ByteLength));
            }
            Assert.Equal(1, await new PrtgDiagnosticRetentionHostedService(backend, capacity)
                .TickAsync(now.AddHours(2), CancellationToken.None));
            using (var verify = backend.CreateContext())
            {
                Assert.Equal(3, verify.PrtgTransferSessions.Count());
                Assert.False(verify.PrtgTransferSessions.Any(row => row.TransferId == activeReceiving));
                Assert.False(verify.PrtgTransferSessions.Any(row => row.TransferId == activeValidating));
                Assert.Equal(3L * shortPayload.Length, verify.PrtgTransferChunks.Sum(row => (long)row.ByteLength));
            }
            Assert.Equal(0, await new PrtgDiagnosticRetentionHostedService(backend, capacity)
                .TickAsync(now.AddHours(2), CancellationToken.None));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new PrtgDiagnosticRetentionHostedService(backend, capacity).TickAsync(now, new CancellationToken(true)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static PrtgTransferSessionRow Session(Guid id, string state, DateTimeOffset created,
        long declaredBytes, int chunkCount, DateTimeOffset? updated = null, DateTimeOffset? completed = null,
        DateTimeOffset? abandoned = null, string? leaseOwner = null, DateTimeOffset? leaseUntil = null,
        DateTimeOffset? activeWriteUntil = null) => new()
    {
        TransferId = id, OwnerId = "test", ScopeHash = new string('A', 64), SourceIdentityHash = new string('B', 64),
        DeclaredBytes = declaredBytes, ChunkCount = chunkCount, PackageSha256 = new string('C', 64), State = state,
        CreatedAtUtc = created, UpdatedAtUtc = updated ?? created, CompletedAtUtc = completed, AbandonedAtUtc = abandoned,
        LeaseOwner = leaseOwner, LeaseUntilUtc = leaseUntil,
        ActiveWriteId = activeWriteUntil.HasValue ? Guid.NewGuid() : null,
        ActiveWriteBytes = activeWriteUntil.HasValue ? short.MaxValue : 0, ActiveWriteUntilUtc = activeWriteUntil
    };

    private static void AddChunks(LfDbContext db, Guid id, int count, byte[] payload, DateTimeOffset acceptedAt)
    {
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        for (var ordinal = 0; ordinal < count; ordinal++)
            db.PrtgTransferChunks.Add(new()
            {
                TransferId = id, Ordinal = ordinal, Payload = payload, ByteLength = payload.Length,
                Sha256 = hash, AcceptedAtUtc = acceptedAt
            });
    }
}
