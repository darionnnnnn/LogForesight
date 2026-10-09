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

    [Fact]
    public void Settings_revision_rebind_preserves_capacity_contract_and_retry_is_idempotent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-capacity-plan-rebind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var store = new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
            var now = DateTimeOffset.UtcNow;
            var original = store.Publish(Candidate('A', now), "settings-owner", now,
                TimeSpan.FromHours(24), "settings-r1", () => true);
            var reboundAt = now.AddMinutes(1);

            Assert.True(store.TryRebindSettingsRevision(original, "settings-r2", reboundAt,
                () => true, out var rebound));
            var expected = original with { SettingsRevision = "settings-r2", Version = original.Version + 1 };
            Assert.Equal(expected, rebound);
            Assert.Equal(original.Fingerprint, rebound!.Fingerprint);
            Assert.Equal(original.Owner, rebound.Owner);
            Assert.Equal(original.CreatedAtUtc, rebound.CreatedAtUtc);
            Assert.Equal(original.LeaseUntilUtc, rebound.LeaseUntilUtc);
            Assert.Equal(original.SourceFingerprint, rebound.SourceFingerprint);
            Assert.Equal(original.SnapshotScopeFingerprint, rebound.SnapshotScopeFingerprint);
            Assert.Equal(original.ProfileScopeFingerprint, rebound.ProfileScopeFingerprint);
            Assert.Equal(original.StrategyFingerprint, rebound.StrategyFingerprint);
            Assert.Equal(original.SnapshotRequestShapeFingerprint, rebound.SnapshotRequestShapeFingerprint);
            Assert.Equal(original.RequestShapeFingerprint, rebound.RequestShapeFingerprint);
            Assert.Equal(original.RuntimeVersionFingerprint, rebound.RuntimeVersionFingerprint);
            Assert.Equal(original.PolicyRevision, rebound.PolicyRevision);
            Assert.Equal(original.SnapshotTableRequestsPerSecond, rebound.SnapshotTableRequestsPerSecond);
            Assert.Equal(original.ProfileTableRequestsPerSecond, rebound.ProfileTableRequestsPerSecond);
            Assert.Equal(original.GeneralResidualRequestsPerSecond, rebound.GeneralResidualRequestsPerSecond);

            Assert.True(store.TryRebindSettingsRevision(original, "settings-r2", reboundAt.AddSeconds(1),
                () => true, out var retried));
            Assert.Equal(rebound, retried);
            Assert.Equal(original.Version + 1, retried!.Version);
            Assert.Equal(rebound, store.ReadCurrent(reboundAt.AddSeconds(2)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Settings_revision_rebind_rejects_expired_plan_without_rewriting_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-capacity-plan-rebind-expired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var store = new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
            var now = DateTimeOffset.UtcNow;
            var original = store.Publish(Candidate('A', now), "settings-owner", now,
                TimeSpan.FromHours(24), "settings-r1", () => true);
            var afterExpiry = original.LeaseUntilUtc.AddTicks(1);
            var callbackCount = 0;

            Assert.False(store.TryRebindSettingsRevision(original, "settings-r2", afterExpiry,
                () => { callbackCount++; return true; }, out var rebound));
            Assert.Null(rebound);
            Assert.Equal(0, callbackCount);
            Assert.Equal(original, store.ReadCurrent(original.LeaseUntilUtc.AddTicks(-1)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Settings_revision_rebind_rejects_stale_owner_or_version_without_touching_newer_plan()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-capacity-plan-rebind-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var store = new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
            var now = DateTimeOffset.UtcNow;
            var stale = store.Publish(Candidate('A', now), "old-owner", now,
                TimeSpan.FromHours(24), "settings-r1", () => true);
            var newer = store.Publish(Candidate('B', now.AddSeconds(1)), "new-owner", now.AddSeconds(1),
                TimeSpan.FromHours(24), "settings-r1", () => true);

            Assert.False(store.TryRebindSettingsRevision(stale, "settings-r2", now.AddSeconds(2),
                () => true, out var rebound));
            Assert.Null(rebound);
            Assert.Equal(newer, store.ReadCurrent(now.AddSeconds(3)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Settings_revision_rebind_false_callback_leaves_original_plan_and_post_callback_race_cannot_clear_newer_plan()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-capacity-plan-rebind-callback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var store = new PrtgCapacityAdmissionPlanStore(backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
            var now = DateTimeOffset.UtcNow;
            var original = store.Publish(Candidate('A', now), "old-owner", now,
                TimeSpan.FromHours(24), "settings-r1", () => true);

            Assert.False(store.TryRebindSettingsRevision(original, "settings-r2", now.AddSeconds(1),
                () => false, out var rejected));
            Assert.Null(rejected);
            Assert.Equal(original, store.ReadCurrent(now.AddSeconds(2)));

            PrtgCapacityAdmissionPlan? newer = null;
            var checks = 0;
            Assert.False(store.TryRebindSettingsRevision(original, "settings-r2", now.AddSeconds(3), () =>
            {
                checks++;
                if (checks == 1) return true;
                newer = store.Publish(Candidate('B', now.AddSeconds(4)), "new-owner", now.AddSeconds(4),
                    TimeSpan.FromHours(24), "settings-r2", () => true);
                return false;
            }, out var raced));
            Assert.Null(raced);
            Assert.NotNull(newer);
            Assert.Equal("new-owner", newer!.Owner);
            Assert.Equal(newer, store.ReadCurrent(now.AddSeconds(5)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Settings_revision_rebind_rechecks_time_after_first_callback_before_commit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-capacity-plan-rebind-expiry-before-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var now = DateTimeOffset.UtcNow;
            var clock = new MutableTimeProvider(now);
            var store = new PrtgCapacityAdmissionPlanStore(
                backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey), clock);
            var original = store.Publish(Candidate('A', now), "settings-owner", now,
                TimeSpan.FromHours(24), "settings-r1", () => true);
            var callbackCount = 0;

            Assert.False(store.TryRebindSettingsRevision(original, "settings-r2", clock.GetUtcNow(), () =>
            {
                callbackCount++;
                clock.SetUtcNow(original.LeaseUntilUtc.AddTicks(1));
                return true;
            }, out var rebound));

            Assert.Null(rebound);
            Assert.Equal(1, callbackCount);
            Assert.Equal(original, store.ReadCurrent(original.LeaseUntilUtc.AddTicks(-1)));
            Assert.Null(store.ReadCurrent(clock.GetUtcNow()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Settings_revision_rebind_rechecks_time_after_final_callback_and_does_not_leave_new_revision()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-capacity-plan-rebind-expiry-after-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var now = DateTimeOffset.UtcNow;
            var clock = new MutableTimeProvider(now);
            var store = new PrtgCapacityAdmissionPlanStore(
                backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey), clock);
            var original = store.Publish(Candidate('A', now), "settings-owner", now,
                TimeSpan.FromHours(24), "settings-r1", () => true);
            var callbackCount = 0;

            Assert.False(store.TryRebindSettingsRevision(original, "settings-r2", clock.GetUtcNow(), () =>
            {
                callbackCount++;
                if (callbackCount == 2) clock.SetUtcNow(original.LeaseUntilUtc.AddTicks(1));
                return true;
            }, out var rebound));

            Assert.Null(rebound);
            Assert.True(callbackCount >= 2);
            var beforeExpiry = store.ReadCurrent(original.LeaseUntilUtc.AddTicks(-1));
            Assert.True(beforeExpiry is null || beforeExpiry.SettingsRevision == original.SettingsRevision,
                "An expired rebind must not leave the new settings revision active in the persisted plan.");
            Assert.Null(store.ReadCurrent(clock.GetUtcNow()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Renew_rejects_a_captured_pre_expiry_time_when_the_store_clock_is_already_past_lease()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-capacity-plan-renew-expiry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var now = DateTimeOffset.UtcNow;
            var clock = new MutableTimeProvider(now);
            var store = new PrtgCapacityAdmissionPlanStore(
                backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey), clock);
            var original = store.Publish(Candidate('A', now), "settings-owner", now,
                TimeSpan.FromHours(24), "settings-r1", () => true);
            clock.SetUtcNow(original.LeaseUntilUtc.AddTicks(1));

            Assert.False(store.Renew(original.Fingerprint, original.Owner, original.Version,
                now, TimeSpan.FromHours(24)));

            Assert.Equal(original, store.ReadCurrent(original.LeaseUntilUtc.AddTicks(-1)));
            Assert.Null(store.ReadCurrent(clock.GetUtcNow()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
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
