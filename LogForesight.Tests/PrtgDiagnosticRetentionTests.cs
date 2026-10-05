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
    public async Task ActualTickDeletesOnlyExpiredTerminalChunksAndRestartsFromDurableCursor()
    {
        var root = Path.Combine(Path.GetTempPath(), "lf-prtg-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, root);
            var now = DateTimeOffset.UtcNow;
            var complete = Guid.NewGuid();
            var receiving = Guid.NewGuid();
            var failed = Guid.NewGuid();
            var recentAbandon = Guid.NewGuid();
            var payload = new byte[EfPrtgTransferStore.MaxChunkBytes];
            using (var db = backend.CreateContext())
            {
                foreach (var pair in new[] { (complete, PrtgTransferStates.Complete), (receiving, PrtgTransferStates.Receiving),
                    (failed, PrtgTransferStates.ValidationFailed), (recentAbandon, PrtgTransferStates.Abandoned) })
                {
                    var at = pair.Item1 == recentAbandon ? now.AddHours(-1) : now.AddDays(-8);
                    db.PrtgTransferSessions.Add(new()
                    {
                        TransferId = pair.Item1, OwnerId = "test", ScopeHash = new string('A', 64), SourceIdentityHash = new string('B', 64),
                        DeclaredBytes = pair.Item1 == complete ? 10L * payload.Length : payload.Length,
                        ChunkCount = pair.Item1 == complete ? 10 : 1, PackageSha256 = new string('C', 64), State = pair.Item2,
                        CreatedAtUtc = at, UpdatedAtUtc = at, CompletedAtUtc = pair.Item2 == PrtgTransferStates.Complete ? at : null,
                        AbandonedAtUtc = pair.Item2 == PrtgTransferStates.Abandoned ? at : null
                    });
                }
                for (var ordinal = 0; ordinal < 10; ordinal++)
                    db.PrtgTransferChunks.Add(new() { TransferId = complete, Ordinal = ordinal, Payload = payload,
                        ByteLength = payload.Length, Sha256 = Convert.ToHexString(SHA256.HashData(payload)), AcceptedAtUtc = now.AddDays(-8) });
                db.SaveChanges();
            }
            var capacity = new PrtgTransferCapacityProbe(backend);
            Assert.Equal(8, await new PrtgDiagnosticRetentionHostedService(backend, capacity).TickAsync(now, CancellationToken.None));
            using (var verify = backend.CreateContext())
            {
                Assert.Equal(7, verify.PrtgTransferSessions.Single(row => row.TransferId == complete).CleanupAfterOrdinal);
                Assert.Equal(2, verify.PrtgTransferChunks.Count(row => row.TransferId == complete));
                Assert.Equal(4, verify.PrtgTransferSessions.Count());
            }
            Assert.Equal(2, await new PrtgDiagnosticRetentionHostedService(backend, capacity).TickAsync(now, CancellationToken.None));
            using (var verify = backend.CreateContext())
            {
                Assert.Equal(3, verify.PrtgTransferSessions.Count());
                Assert.False(verify.PrtgTransferSessions.Any(row => row.TransferId == complete));
                Assert.Empty(verify.PrtgTransferChunks);
            }
            Assert.Equal(0, await new PrtgDiagnosticRetentionHostedService(backend, capacity).TickAsync(now, CancellationToken.None));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new PrtgDiagnosticRetentionHostedService(backend, capacity).TickAsync(now, new CancellationToken(true)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
