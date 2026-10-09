using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>SQL-backed boundary checks for the durable trusted-profile refresh state store.</summary>
public sealed class PrtgProfileRefreshStateBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-profile-refresh-boundary-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly PrtgTrustedSamplingProfileRefreshStateStore store;

    public PrtgProfileRefreshStateBoundaryTests()
    {
        backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, root);
        store = new PrtgTrustedSamplingProfileRefreshStateStore(backend);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-version")]
    public void SavePage_rejects_invalid_lease_without_replacing_page_or_aggregates(string mutation)
    {
        Assert.True(store.TryAcquire("writer", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), out var version));
        var pageKey = PrtgTrustedSamplingProfileRefreshStateStore.PageKey("scope", 0);
        var countersBefore = SeedOverview("scope", "writer", version, DateTimeOffset.UtcNow.AddMinutes(5));
        backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.OverviewKey).Mutate(raw =>
        {
            var overview = JsonSerializer.Deserialize<PrtgTrustedSamplingProfileRefreshStateStore.Overview>(raw!)!;
            if (mutation == "expired") overview.LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            if (mutation == "wrong-owner") overview.LeaseOwner = "new-owner";
            if (mutation == "wrong-version") overview.LeaseVersion++;
            return (JsonSerializer.Serialize(overview), true);
        });

        Assert.Throws<OperationCanceledException>(() => store.SavePage("scope", 0,
            [Sensor(41, "qualified")], "writer", version, requestDelta: 9, requestSecondsDelta: 3));

        Assert.Null(backend.Blob(pageKey).Read());
        var after = store.ReadOverview();
        Assert.Equal(countersBefore.RawRequestCount, after.RawRequestCount);
        Assert.Equal(countersBefore.Qualified, after.Qualified);
        Assert.Equal(countersBefore.Waiting, after.Waiting);
    }

    [Fact]
    public void Old_writer_cannot_commit_after_new_lease_version_is_acquired()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(store.TryAcquire("old-writer", now.AddMinutes(-10), TimeSpan.FromMinutes(1), out var oldVersion));
        Assert.True(store.TryAcquire("new-writer", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), out var newVersion));
        Assert.True(newVersion > oldVersion);

        Assert.Throws<OperationCanceledException>(() => store.SavePage("scope", 0,
            [Sensor(42, "qualified")], "old-writer", oldVersion, requestDelta: 1));

        Assert.Null(backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.PageKey("scope", 0)).Read());
        Assert.Equal(0, store.ReadOverview().RawRequestCount);
        Assert.Equal("new-writer", store.ReadOverview().LeaseOwner);
        Assert.Equal(newVersion, store.ReadOverview().LeaseVersion);
    }

    [Fact]
    public void ReadPage_rejects_oversized_sql_blob_without_rewriting_it()
    {
        var key = PrtgTrustedSamplingProfileRefreshStateStore.PageKey("scope", 0);
        var raw = new string('x', PrtgTrustedSamplingProfileRefreshStateStore.MaximumPageBytes + 1);
        backend.Blob(key).Mutate(_ => (raw, true));

        Assert.Throws<InvalidDataException>(() => store.ReadPage("scope", 0));
        Assert.Equal(raw, backend.Blob(key).Read());
    }

    [Fact]
    public void ReadPage_rejects_malformed_or_overfull_sql_page_without_rewriting_it()
    {
        var key = PrtgTrustedSamplingProfileRefreshStateStore.PageKey("scope", 0);
        const string malformed = "{not-json";
        backend.Blob(key).Mutate(_ => (malformed, true));

        Assert.Throws<JsonException>(() => store.ReadPage("scope", 0));
        Assert.Equal(malformed, backend.Blob(key).Read());

        var overfull = JsonSerializer.Serialize(new PrtgTrustedSamplingProfileRefreshStateStore.SensorPage
        {
            Sensors = Enumerable.Range(1, 101).Select(id => Sensor(id, "waiting")).ToList()
        });
        backend.Blob(key).Mutate(_ => (overfull, true));
        Assert.Throws<InvalidDataException>(() => store.ReadPage("scope", 0));
        Assert.Equal(overfull, backend.Blob(key).Read());
    }

    [Fact]
    public void ReadOverview_rejects_oversized_or_malformed_sql_blob_without_rewriting_it()
    {
        var blob = backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.OverviewKey);
        var oversized = new string('x', PrtgTrustedSamplingProfileRefreshStateStore.MaximumOverviewBytes + 1);
        blob.Mutate(_ => (oversized, true));
        Assert.Throws<InvalidDataException>(() => store.ReadOverview());
        Assert.Equal(oversized, blob.Read());

        const string malformed = "{not-json";
        blob.Mutate(_ => (malformed, true));
        Assert.Throws<JsonException>(() => store.ReadOverview());
        Assert.Equal(malformed, blob.Read());
    }

    [Fact]
    public void EarliestNextAttempt_rejects_missing_or_misindexed_page_sets()
    {
        Assert.Throws<InvalidDataException>(() => store.EarliestNextAttempt("missing", 1));

        var scope = "misindexed";
        InsertBlob(PrtgTrustedSamplingProfileRefreshStateStore.PageKey(scope, 1),
            JsonSerializer.Serialize(Page([Sensor(51, "waiting")])), DateTime.UtcNow);
        Assert.Throws<InvalidDataException>(() => store.EarliestNextAttempt(scope, 1));
    }

    [Fact]
    public void EarliestNextAttempt_reads_all_150_pages_of_100_rows_and_returns_the_minimum_due_time()
    {
        const string scope = "large-scope";
        var now = DateTimeOffset.UtcNow;
        var rows = new List<BlobRow>(150);
        for (var pageIndex = 0; pageIndex < 150; pageIndex++)
        {
            var due = pageIndex == 149 ? now.AddMinutes(7) : now.AddHours(1);
            var sensors = Enumerable.Range(0, 100)
                .Select(offset => Sensor(pageIndex * 100L + offset + 1, "waiting", due)).ToList();
            var json = JsonSerializer.Serialize(Page(sensors));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= PrtgTrustedSamplingProfileRefreshStateStore.MaximumPageBytes);
            rows.Add(new BlobRow
            {
                BlobKey = PrtgTrustedSamplingProfileRefreshStateStore.PageKey(scope, pageIndex),
                Content = json, UpdatedAt = DateTime.UtcNow, Version = 1
            });
        }
        using (var ctx = backend.CreateContext())
        {
            ctx.Blobs.AddRange(rows);
            ctx.SaveChanges();
        }

        Assert.Equal(now.AddMinutes(7), store.EarliestNextAttempt(scope, 150));
    }

    [Fact]
    public void Expired_page_purge_caps_at_100_and_retains_a_recent_page()
    {
        const string prefix = "prtg_profile_page_purge-scope_";
        var cutoff = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, 101).Select(index => new BlobRow
        {
            BlobKey = $"{prefix}{index:D4}", Content = "{}", UpdatedAt = cutoff.UtcDateTime.AddDays(-1), Version = 1
        }).ToList();
        var recentKey = prefix + "recent";
        rows.Add(new BlobRow { BlobKey = recentKey, Content = "{}", UpdatedAt = cutoff.UtcDateTime, Version = 1 });
        using (var ctx = backend.CreateContext())
        {
            ctx.Blobs.AddRange(rows);
            ctx.SaveChanges();
        }

        store.PurgeExpiredPages(cutoff);

        using var verify = backend.CreateContext();
        Assert.Equal(1, verify.Blobs.Count(row => row.BlobKey.StartsWith(prefix) && row.UpdatedAt < cutoff.UtcDateTime));
        Assert.True(verify.Blobs.Any(row => row.BlobKey == recentKey));
    }

    private PrtgTrustedSamplingProfileRefreshStateStore.Overview SeedOverview(string scope, string owner,
        long version, DateTimeOffset leaseUntil)
    {
        var overview = new PrtgTrustedSamplingProfileRefreshStateStore.Overview
        {
            ScopeFingerprint = scope, SelectedSensors = 4, EligibleSensors = 3, Qualified = 1,
            Waiting = 2, Failed = 1, RawRequestCount = 12, ObservedRequestSeconds = 9,
            LeaseOwner = owner, LeaseVersion = version, LeaseUntilUtc = leaseUntil
        };
        backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.OverviewKey)
            .Mutate(_ => (JsonSerializer.Serialize(overview), true));
        return overview;
    }

    private static PrtgTrustedSamplingProfileRefreshStateStore.SensorState Sensor(long id, string status,
        DateTimeOffset? nextAttempt = null) => new()
    {
        SensorObjid = id, Eligible = true, ContractFingerprint = "fixture-contract", Status = status,
        Reason = "fixture-only", NextAttemptAtUtc = nextAttempt
    };

    private static PrtgTrustedSamplingProfileRefreshStateStore.SensorPage Page(
        List<PrtgTrustedSamplingProfileRefreshStateStore.SensorState> sensors) => new()
    {
        UpdatedAtUtc = DateTimeOffset.UtcNow, Sensors = sensors
    };

    private void InsertBlob(string key, string content, DateTime updatedAt)
    {
        using var ctx = backend.CreateContext();
        ctx.Blobs.Add(new BlobRow { BlobKey = key, Content = content, UpdatedAt = updatedAt, Version = 1 });
        ctx.SaveChanges();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }
}
