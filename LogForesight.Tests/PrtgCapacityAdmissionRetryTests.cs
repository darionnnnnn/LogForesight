using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Retry-boundary regressions for the admission-plan blob store. These use a real SQLite
/// in-memory database and fail one SaveChanges before EF writes, so the first attempt rolls back.
/// </summary>
public sealed class PrtgCapacityAdmissionRetryTests
{
    [Fact]
    public void Rebind_does_not_report_a_rolled_back_attempt_when_a_retry_rejects()
    {
        using var db = new InMemoryAdmissionDatabase();
        var now = DateTimeOffset.UtcNow;
        var store = db.PlanStore();
        var original = store.Publish(Candidate('A', now), "owner-a", now,
            TimeSpan.FromHours(24), "settings-r1", () => true);
        var calls = 0;
        db.FailNextSave();

        var result = store.TryRebindSettingsRevision(original, "settings-r2", now.AddMinutes(1), () =>
        {
            calls++;
            return calls is 1 or 3;
        }, out var rebound);

        Assert.False(result);
        Assert.Null(rebound);
        Assert.Equal(2, calls);
        Assert.Equal(original, store.ReadCurrent(now.AddMinutes(2)));
    }

    [Fact]
    public void Renew_does_not_report_a_rolled_back_attempt_after_retry_observes_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = new InMemoryAdmissionDatabase(now);
        var store = db.PlanStore(db.Clock);
        var original = store.Publish(Candidate('A', now), "owner-a", now,
            TimeSpan.FromHours(24), "settings-r1", () => true);
        db.FailNextSave(() => db.Clock.SetUtcNow(original.LeaseUntilUtc.AddTicks(1)));

        var renewed = store.Renew(original.Fingerprint, original.Owner, original.Version,
            now.AddMinutes(1), TimeSpan.FromHours(24));

        Assert.False(renewed);
        Assert.Equal(original, store.ReadCurrent(original.LeaseUntilUtc.AddTicks(-1)));
        Assert.Null(store.ReadCurrent(db.Clock.GetUtcNow()));
    }

    [Fact]
    public void Exact_rebind_retry_does_not_increment_the_persisted_blob_row_version()
    {
        using var db = new InMemoryAdmissionDatabase();
        var now = DateTimeOffset.UtcNow;
        var store = db.PlanStore();
        var original = store.Publish(Candidate('A', now), "owner-a", now,
            TimeSpan.FromHours(24), "settings-r1", () => true);

        Assert.True(store.TryRebindSettingsRevision(original, "settings-r2", now.AddMinutes(1),
            () => true, out var rebound));
        var rowVersionAfterRebind = db.ReadBlobVersion(PrtgCapacityAdmissionPlanStore.BlobKey);

        Assert.True(store.TryRebindSettingsRevision(original, "settings-r2", now.AddMinutes(2),
            () => true, out var retried));

        Assert.Equal(rebound, retried);
        Assert.Equal(rowVersionAfterRebind,
            db.ReadBlobVersion(PrtgCapacityAdmissionPlanStore.BlobKey));
    }

    [Fact]
    public void Publish_does_not_return_a_candidate_from_a_rolled_back_attempt_when_retry_rejects()
    {
        using var db = new InMemoryAdmissionDatabase();
        var now = DateTimeOffset.UtcNow;
        var store = db.PlanStore();
        var original = store.Publish(Candidate('A', now), "owner-a", now,
            TimeSpan.FromHours(24), "settings-r1", () => true);
        var calls = 0;
        db.FailNextSave();

        Assert.Throws<InvalidOperationException>(() => store.Publish(Candidate('B', now.AddMinutes(1)),
            "owner-b", now.AddMinutes(1), TimeSpan.FromHours(24), "settings-r1", () =>
            {
                calls++;
                return calls is 1 or 3;
            }));

        Assert.Equal(2, calls);
        Assert.Equal(original, store.ReadCurrent(now.AddMinutes(2)));
    }

    private sealed class InMemoryAdmissionDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly DbContextOptions<LfDbContext> _options;
        private readonly SaveFailureArm _failure = new();

        public InMemoryAdmissionDatabase(DateTimeOffset? now = null)
        {
            _connection.Open();
            _options = new DbContextOptionsBuilder<LfDbContext>().UseSqlite(_connection).Options;
            using var context = new LfDbContext(_options);
            context.Database.EnsureCreated();
            Clock = new MutableTimeProvider(now ?? DateTimeOffset.UtcNow);
        }

        public MutableTimeProvider Clock { get; }

        public PrtgCapacityAdmissionPlanStore PlanStore(TimeProvider? timeProvider = null) =>
            new(new EfJsonBlobStore(() => new SaveFailureContext(_options, _failure),
                PrtgCapacityAdmissionPlanStore.BlobKey), timeProvider);

        public void FailNextSave(Action? beforeThrow = null) => _failure.Arm(beforeThrow);

        public long ReadBlobVersion(string key)
        {
            using var context = new LfDbContext(_options);
            return context.Blobs.AsNoTracking().Where(row => row.BlobKey == key)
                .Select(row => row.Version).Single();
        }

        public void Dispose() => _connection.Dispose();

        private sealed class SaveFailureContext(DbContextOptions<LfDbContext> options, SaveFailureArm failure)
            : LfDbContext(options)
        {
            public override int SaveChanges(bool acceptAllChangesOnSuccess)
            {
                if (failure.TryConsume(out var beforeThrow))
                {
                    beforeThrow?.Invoke();
                    throw new DbUpdateException("Injected pre-save failure for retry regression.");
                }
                return base.SaveChanges(acceptAllChangesOnSuccess);
            }
        }

        private sealed class SaveFailureArm
        {
            private Action? _beforeThrow;
            private bool _armed;

            public void Arm(Action? beforeThrow = null)
            {
                _beforeThrow = beforeThrow;
                _armed = true;
            }

            public bool TryConsume(out Action? beforeThrow)
            {
                if (!_armed)
                {
                    beforeThrow = null;
                    return false;
                }
                _armed = false;
                beforeThrow = _beforeThrow;
                _beforeThrow = null;
                return true;
            }
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
