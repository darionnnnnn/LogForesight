using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgEfBoundedMutationTests : IDisposable
{
    private readonly EfSqliteFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    [Fact]
    public void TwoBoundedSnapshotsAttachWithoutTrackedContentQueryAndSaveWithOriginalCasTokens()
    {
        fixture.Blob("bounded-mutation-a").Mutate(_ => ("first-small-value", true));
        fixture.Blob("bounded-mutation-b").Mutate(_ => ("second-small-value", true));

        using (var context = fixture.NewContext())
        using (var transaction = context.Database.BeginTransaction())
        {
            var bounded = EfPrtgStore.ReadBoundedBlobs(context,
                ["bounded-mutation-a", "bounded-mutation-b"], 128, 128, "test-cap");
            var first = EfPrtgStore.AttachBoundedBlobSnapshot(context, "bounded-mutation-a", bounded["bounded-mutation-a"], "changed-a");
            var second = EfPrtgStore.AttachBoundedBlobSnapshot(context, "bounded-mutation-b", bounded["bounded-mutation-b"], "changed-b");

            Assert.Equal(2, context.ChangeTracker.Entries<BlobRow>().Count());
            Assert.All(context.ChangeTracker.Entries<BlobRow>(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
            Assert.Equal("first-small-value", first!.Content);
            Assert.Equal("second-small-value", second!.Content);
            first.Content = "first-updated";
            first.Version++;
            first.UpdatedAt = DateTime.UtcNow;
            second.Content = "second-updated";
            second.Version++;
            second.UpdatedAt = DateTime.UtcNow;
            context.SaveChanges();
            transaction.Commit();
        }

        Assert.Equal("first-updated", fixture.Blob("bounded-mutation-a").Read());
        Assert.Equal("second-updated", fixture.Blob("bounded-mutation-b").Read());
    }

    [Fact]
    public void StaleBoundedSnapshotCannotOverwriteAConcurrentBlobUpdate()
    {
        fixture.Blob("bounded-mutation-stale").Mutate(_ => ("before", true));
        (string? Content, long Version, int Length, DateTime UpdatedAt) snapshot;
        using (var context = fixture.NewContext())
        {
            var rows = EfPrtgStore.ReadBoundedBlobs(context, ["bounded-mutation-stale"], 128, 128, "test-cap");
            snapshot = rows["bounded-mutation-stale"];
        }

        fixture.Blob("bounded-mutation-stale").Mutate(_ => ("concurrent-value", true));
        using (var context = fixture.NewContext())
        using (context.Database.BeginTransaction())
        {
            var row = EfPrtgStore.AttachBoundedBlobSnapshot(context, "bounded-mutation-stale", snapshot, "changed");
            row!.Content = "must-not-win";
            row.Version++;
            row.UpdatedAt = DateTime.UtcNow;
            Assert.Throws<DbUpdateConcurrencyException>(() => context.SaveChanges());
        }

        Assert.Equal("concurrent-value", fixture.Blob("bounded-mutation-stale").Read());
    }
}
