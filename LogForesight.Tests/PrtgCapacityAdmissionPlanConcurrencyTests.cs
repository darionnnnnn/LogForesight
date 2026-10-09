using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgCapacityAdmissionPlanConcurrencyTests
{
    [Fact]
    public void Stale_publish_cannot_invalidate_a_newer_plan_after_final_source_check()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-capacity-plan-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var store = new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
            var now = DateTimeOffset.UtcNow;
            PrtgCapacityAdmissionPlan? newer = null;
            var checks = 0;

            Assert.Throws<InvalidOperationException>(() => store.Publish(Candidate('A', now), "older-owner",
                now, TimeSpan.FromHours(24), "settings-r1", () =>
                {
                    checks++;
                    if (checks == 1) return true;
                    newer = store.Publish(Candidate('B', now.AddSeconds(1)), "newer-owner",
                        now.AddSeconds(1), TimeSpan.FromHours(24), "settings-r1", () => true);
                    return false;
                }));

            var current = Assert.IsType<PrtgCapacityAdmissionPlan>(store.ReadCurrent(now.AddSeconds(2)));
            Assert.Equal("newer-owner", current.Owner);
            Assert.Equal(newer!.Fingerprint, current.Fingerprint);
            Assert.Equal(newer.Version, current.Version);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Stale_owner_cannot_invalidate_or_renew_newer_plan_and_false_source_check_publishes_nothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-capacity-plan-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var store = new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
            var now = DateTimeOffset.UtcNow;
            var older = store.Publish(Candidate('A', now), "older-owner", now,
                TimeSpan.FromHours(24), "settings-r1", () => true);
            var newer = store.Publish(Candidate('B', now.AddSeconds(1)), "newer-owner", now.AddSeconds(1),
                TimeSpan.FromHours(24), "settings-r1", () => true);

            Assert.False(store.Renew(older.Fingerprint, older.Owner, older.Version,
                now.AddMinutes(1), TimeSpan.FromHours(24)));
            store.Invalidate(older.Owner, older.Version);
            var current = Assert.IsType<PrtgCapacityAdmissionPlan>(store.ReadCurrent(now.AddMinutes(1)));
            Assert.Equal(newer.Fingerprint, current.Fingerprint);
            Assert.Equal("newer-owner", current.Owner);

            Assert.Throws<InvalidOperationException>(() => store.Publish(Candidate('C', now.AddMinutes(2)),
                "rejected-owner", now.AddMinutes(2), TimeSpan.FromHours(24), "settings-r1", () => false));
            current = Assert.IsType<PrtgCapacityAdmissionPlan>(store.ReadCurrent(now.AddMinutes(2)));
            Assert.Equal(newer.Fingerprint, current.Fingerprint);
            Assert.Equal("newer-owner", current.Owner);

            var rejectedStore = new PrtgCapacityAdmissionPlanStore(backend.Blob("capacity-plan-rejected-test"));
            Assert.Throws<InvalidOperationException>(() => rejectedStore.Publish(Candidate('C', now.AddMinutes(2)),
                "rejected-owner", now.AddMinutes(2), TimeSpan.FromHours(24), "settings-r1", () => false));
            Assert.Null(rejectedStore.ReadCurrent(now.AddMinutes(2)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static PrtgCapacityAdmissionPlan Candidate(char marker, DateTimeOffset now)
    {
        var estimate = new PrtgJointCapacityEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            2, .4, .5, .6, 30, 60, 92, 450,
            PrtgProfileTransportCapacityEvaluator.ProfileRefreshWindow.TotalSeconds,
            "fixture-qualified");
        string Hash(char value) => new(value, 64);
        return PrtgJointCapacityEvaluator.CreatePlan(estimate, Hash(marker), Hash(marker), Hash(marker),
            Hash(marker), Hash(marker), Hash(marker), Hash(marker), now, "settings-r1", "policy-r1");
    }
}
