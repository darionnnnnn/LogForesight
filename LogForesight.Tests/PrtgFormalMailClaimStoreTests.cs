using System.Data;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgFormalMailClaimStoreTests
{
    [Fact]
    public void MutationSessionReadsOneClaimAndFenceAndPruneRechecksAcrossTwoCalls()
    {
        var directory = CreateDirectory();
        try
        {
            var backend = CreateBackend(directory);
            var hostId = 74L;
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            var oldDay = now.Date.AddDays(-180);
            var fenceKey = "9001|mode:7|issue-a";
            var shardKey = PrtgFormalMailClaimStore.BlobKey(hostId, oldDay, "urgent", "issue-a", "ops@example.test");
            var fence = new PrtgResourceFormalDeliveryFence(hostId, 9001, 7, 801, PrtgResourceFamily.Cpu,
                "source-a", "resource-a", "channel-a", "rule-a", new string('a', 64),
                new string('b', 64), new string('c', 64));
            var shard = new PrtgFormalMailClaimShard { HostId = hostId };
            shard.Fences[fenceKey] = new PrtgFormalMailFenceEntry
            {
                Fence = fence,
                CreatedAtUtc = now.AddDays(-120)
            };
            shard.Claims["ops@example.test|issue-a"] = new MailFormalStartClaim
            {
                FenceKey = fenceKey,
                Status = "smtp-accepted",
                UpdatedAtUtc = now.AddDays(-30),
                SmtpAcceptedAtUtc = now.AddDays(-30)
            };

            using (var seed = backend.CreateContext())
            {
                seed.Blobs.Add(new BlobRow
                {
                    BlobKey = shardKey,
                    Content = System.Text.Json.JsonSerializer.Serialize(shard, LfJsonOptions.Pretty),
                    UpdatedAt = now.AddDays(-120),
                    Version = 1
                });
                seed.SaveChanges();
            }

            using (var read = backend.CreateContext())
            using (var transaction = read.Database.BeginTransaction(IsolationLevel.Serializable))
            {
                var session = new PrtgFormalMailClaimStore.MutationSession(read, maximumShardCharacters: 4096);
                Assert.Equal(fence, session.GetFence(hostId, shardKey, fenceKey));
                Assert.Equal("smtp-accepted", session.GetClaim(hostId, shardKey, "ops@example.test|issue-a")?.Status);
                Assert.Same(transaction, read.Database.CurrentTransaction);
                transaction.Rollback();
            }

            var first = PrtgFormalMailClaimStore.PruneExpiredShardsForTesting(backend, backend.CreateContext,
                recordRetentionDays: 30, cursor: null, nowUtc: now, maximumKeys: 1,
                timeLimit: TimeSpan.FromSeconds(2), maximumShardCharacters: 4096);
            Assert.Equal(0, first.DeletedShards);
            Assert.Equal(0, first.UpdatedShards);
            using (var afterFirst = backend.CreateContext())
            {
                var unchanged = System.Text.Json.JsonSerializer.Deserialize<PrtgFormalMailClaimShard>(
                    afterFirst.Blobs.Single(row => row.BlobKey == shardKey).Content, LfJsonOptions.Pretty)!;
                Assert.Single(unchanged.Claims);
                Assert.Single(unchanged.Fences);
            }

            var second = PrtgFormalMailClaimStore.PruneExpiredShardsForTesting(backend, backend.CreateContext,
                recordRetentionDays: 30, cursor: null, nowUtc: now.AddDays(70), maximumKeys: 1,
                timeLimit: TimeSpan.FromSeconds(2), maximumShardCharacters: 4096);
            Assert.Equal(1, second.DeletedShards);
            Assert.Equal(0, second.UpdatedShards);
            using var verify = backend.CreateContext();
            Assert.False(verify.Blobs.Any(row => row.BlobKey == shardKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("over-cap")]
    [InlineData("nul-byte")]
    public void MutationSessionRefusesTinyInvalidPhysicalTextWithoutWriting(string shape)
    {
        var directory = CreateDirectory();
        try
        {
            var backend = CreateBackend(directory);
            const long hostId = 75;
            const int maximumCharacters = 16;
            var shardKey = PrtgFormalMailClaimStore.BlobKey(hostId, new DateTime(2026, 10, 15), "urgent", "invalid", "ops@example.test");
            using (var seed = backend.CreateContext())
            {
                seed.Blobs.Add(new BlobRow
                {
                    BlobKey = shardKey,
                    Content = "{}",
                    UpdatedAt = new DateTime(2026, 10, 15),
                    Version = 3
                });
                seed.SaveChanges();
            }

            if (shape == "over-cap")
            {
                using var update = backend.CreateContext();
                update.Blobs.Single(row => row.BlobKey == shardKey).Content = new string('x', maximumCharacters + 1);
                update.SaveChanges();
            }
            else
            {
                var nulBytes = Convert.ToHexString(Encoding.UTF8.GetBytes("{}\0"));
                using var update = backend.CreateContext();
                update.Database.ExecuteSqlRaw(
                    $"UPDATE lf_blobs SET content = CAST(X'{nulBytes}' AS TEXT) WHERE blob_key = {{0}}", shardKey);
            }

            var before = ReadPhysicalSnapshot(backend, shardKey);
            using (var read = backend.CreateContext())
            using (var transaction = read.Database.BeginTransaction(IsolationLevel.Serializable))
            {
                var session = new PrtgFormalMailClaimStore.MutationSession(read, maximumCharacters);
                Assert.Throws<InvalidDataException>(() => session.GetClaim(hostId, shardKey, "claim"));
                Assert.Same(transaction, read.Database.CurrentTransaction);
                transaction.Rollback();
            }

            Assert.Equal(before, ReadPhysicalSnapshot(backend, shardKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void MutationSessionRejectsCallerLocalShardThatDiffersFromBoundedTransactionRead()
    {
        var directory = CreateDirectory();
        try
        {
            var backend = CreateBackend(directory);
            const long hostId = 76;
            var shardKey = PrtgFormalMailClaimStore.BlobKey(hostId, new DateTime(2026, 10, 15), "urgent", "local", "ops@example.test");
            using (var seed = backend.CreateContext())
            {
                seed.Blobs.Add(new BlobRow
                {
                    BlobKey = shardKey,
                    Content = System.Text.Json.JsonSerializer.Serialize(new PrtgFormalMailClaimShard { HostId = hostId }, LfJsonOptions.Pretty),
                    UpdatedAt = new DateTime(2026, 10, 15),
                    Version = 4
                });
                seed.SaveChanges();
            }

            var before = ReadPhysicalSnapshot(backend, shardKey);
            using (var read = backend.CreateContext())
            {
                var local = read.Blobs.Single(row => row.BlobKey == shardKey);
                local.Content = "{}";
                using var transaction = read.Database.BeginTransaction(IsolationLevel.Serializable);
                var session = new PrtgFormalMailClaimStore.MutationSession(read, maximumShardCharacters: 4096);
                Assert.Throws<DbUpdateConcurrencyException>(() => session.GetClaim(hostId, shardKey, "claim"));
                Assert.Same(transaction, read.Database.CurrentTransaction);
                transaction.Rollback();
            }

            Assert.Equal(before, ReadPhysicalSnapshot(backend, shardKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(directory);
        }
    }

    private static StorageBackend CreateBackend(string directory) => new(
        new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "claims.db")}" },
        directory);

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-formal-claim-bounded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static (string Hex, long Version) ReadPhysicalSnapshot(StorageBackend backend, string shardKey)
    {
        using var context = backend.CreateContext();
        context.Database.OpenConnection();
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT hex(CAST(content AS BLOB)), version FROM lf_blobs WHERE blob_key = $key";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$key";
        parameter.Value = shardKey;
        command.Parameters.Add(parameter);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
