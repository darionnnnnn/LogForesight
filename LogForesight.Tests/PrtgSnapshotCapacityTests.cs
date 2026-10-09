using LogForesight.Core.Service;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgSnapshotCapacityTests
{
    private static readonly string Scope = new('A', 64);
    private static readonly string Endpoint = new('B', 64);
    private static readonly string Shape = new('C', 64);

    private static PrtgSnapshotCapacitySample[] Samples(DateTimeOffset now, string outcome = "success") =>
        Enumerable.Range(0, PrtgSnapshotCapacityEvaluator.RequiredFullBatchSamples)
            .Select(i => new PrtgSnapshotCapacitySample(Scope, Endpoint, Shape, now.AddMinutes(-i),
                9000, 50, outcome)).ToArray();

    [Theory]
    [InlineData("conservative")]
    [InlineData("aggressive")]
    public void Capacity_1000targets_9sP95_qualified_with25percentheadroom(string strategy)
    {
        var now = DateTimeOffset.UtcNow;
        var result = PrtgSnapshotCapacityEvaluator.Evaluate(1000, strategy, Scope, Endpoint, Shape,
            Samples(now), now);

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, result.Status);
        Assert.Equal(20, result.BatchCount);
        Assert.Equal(9d, result.P95BatchSeconds);
        Assert.Equal(63d, result.EstimatedSeconds);
    }

    [Theory]
    [InlineData("conservative")]
    [InlineData("aggressive")]
    public void Capacity_15000targets_9sP95_rejected_by_fixed_window(string strategy)
    {
        var now = DateTimeOffset.UtcNow;
        var result = PrtgSnapshotCapacityEvaluator.Evaluate(15000, strategy, Scope, Endpoint, Shape,
            Samples(now), now);

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityExceeded, result.Status);
        Assert.Equal(300, result.BatchCount);
        Assert.Equal(900d, result.EstimatedSeconds);
    }

    [Fact]
    public void Missing_stale_or_different_source_samples_remain_unverified()
    {
        var now = DateTimeOffset.UtcNow;
        var old = Samples(now.AddHours(-25));
        var differentSource = Samples(now).Select(x => x with { EndpointFingerprint = new string('D', 64) });

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified,
            PrtgSnapshotCapacityEvaluator.Evaluate(1000, "conservative", Scope, Endpoint, Shape, [], now).Status);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified,
            PrtgSnapshotCapacityEvaluator.Evaluate(1000, "conservative", Scope, Endpoint, Shape, old, now).Status);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified,
            PrtgSnapshotCapacityEvaluator.Evaluate(1000, "conservative", Scope, Endpoint, Shape, differentSource, now).Status);
    }

    [Fact]
    public void Recent_failed_or_timed_out_full_batch_cannot_be_dropped_from_quality()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = Samples(now).Append(new PrtgSnapshotCapacitySample(Scope, Endpoint, Shape,
            now, 30_000, 50, "timeout"));

        var result = PrtgSnapshotCapacityEvaluator.Evaluate(1000, "conservative", Scope, Endpoint, Shape,
            samples, now);

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, result.Status);
        Assert.Equal("recent_full_batch_failure_or_timeout", result.Reason);
    }

    [Fact]
    public void Small_exact_scope_can_use_five_distinct_partial_batches_but_cannot_qualify_a_larger_scope()
    {
        var now = DateTimeOffset.UtcNow;
        var smallScope = new string('E', 64);
        var smallShape = PrtgSnapshotCapacityStore.Fingerprint("GET snapshot;batch=17");
        var samples = Enumerable.Range(0, 5).Select(i => new PrtgSnapshotCapacitySample(
            smallScope, Endpoint, smallShape, now.AddMinutes(-i), 9000, 17, "success")).ToArray();

        var exact = PrtgSnapshotCapacityEvaluator.Evaluate(17, "conservative", smallScope, Endpoint,
            smallShape, samples, now);
        var expanded = PrtgSnapshotCapacityEvaluator.Evaluate(18, "conservative", smallScope, Endpoint,
            smallShape, samples, now);

        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified, exact.Status);
        Assert.Equal(5, exact.FreshMatchingFullBatchSamples);
        Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified, expanded.Status);
    }

    [Fact]
    public void Pilot_batch_builder_never_duplicates_padding_ids_and_partial_scope_batches_are_exact()
    {
        var small = PrtgSnapshotCapacityPilot.BuildPilotBatches(Enumerable.Range(1, 17).Select(x => (long)x).ToArray());
        Assert.Equal(5, small.Count);
        Assert.All(small, batch => Assert.Equal(17, batch.Distinct().Count()));
        Assert.All(small, batch => Assert.Equal(17, batch.Length));

        var medium = PrtgSnapshotCapacityPilot.BuildPilotBatches(Enumerable.Range(1, 113).Select(x => (long)x).ToArray());
        Assert.Equal(5, medium.Count);
        Assert.All(medium, batch => Assert.Equal(50, batch.Length));
        Assert.All(medium, batch => Assert.Equal(50, batch.Distinct().Count()));
        Assert.DoesNotContain(medium, batch => batch.Length != PrtgSnapshotCapacityEvaluator.BatchSize);
    }

    [Fact]
    public void Later_slower_same_hour_requests_are_retained_instead_of_optimistic_early_samples()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-prtg-capacity-slowest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var store = new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
            var now = new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 5; i++)
                store.Record(new PrtgSnapshotCapacitySample(Scope, Endpoint, Shape, now.AddSeconds(i), 9000, 50, "success"));
            store.Record(new PrtgSnapshotCapacitySample(Scope, Endpoint, Shape, now.AddSeconds(10), 30_000, 50, "success"));

            var retained = store.Read();
            Assert.Equal(5, retained.Count);
            Assert.Contains(retained, x => x.ElapsedMilliseconds == 30_000);
            Assert.Equal(30d, PrtgSnapshotCapacityEvaluator.Evaluate(1000, "conservative", Scope, Endpoint,
                Shape, retained, now.AddMinutes(1)).P95BatchSeconds);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Snapshot_transport_response_must_match_exact_sensor_ids_and_basic_status_time_shape()
    {
        var valid = "{\"sensors\":[{\"objid\":41,\"status\":\"Up\",\"lastcheck\":\"2026-10-06 04:00:00\"},{\"objid\":42,\"status\":\"Warning\",\"lastcheck\":\"2026-10-06 04:00:00\"}]}";
        PrtgSnapshotCapacityResponseValidator.Validate(valid, [41, 42]);

        Assert.Throws<InvalidDataException>(() => PrtgSnapshotCapacityResponseValidator.Validate(
            "{\"sensors\":[{\"objid\":41,\"status\":\"Up\",\"lastcheck\":\"now\"}]}", [41, 42]));
        Assert.Throws<InvalidDataException>(() => PrtgSnapshotCapacityResponseValidator.Validate(
            "{\"sensors\":[{\"objid\":41,\"status\":\"Up\",\"lastcheck\":\"now\"},{\"objid\":99,\"status\":\"Up\",\"lastcheck\":\"now\"}]}", [41, 42]));
        Assert.Throws<InvalidDataException>(() => PrtgSnapshotCapacityResponseValidator.Validate(
            "{\"sensors\":[{\"objid\":41,\"status\":\"Up\",\"lastcheck\":\"now\"},{\"objid\":41,\"status\":\"Up\",\"lastcheck\":\"now\"}]}", [41, 42]));
        Assert.Throws<InvalidDataException>(() => PrtgSnapshotCapacityResponseValidator.Validate(
            "{\"sensors\":[{\"objid\":41,\"lastcheck\":\"now\"},{\"objid\":42,\"status\":\"Up\",\"lastcheck\":\"now\"}]}", [41, 42]));
        Assert.Throws<InvalidDataException>(() => PrtgSnapshotCapacityResponseValidator.Validate("{\"error\":\"login required\"}", [41]));
    }

    [Theory]
    [InlineData("{\"sensors\":[{\"objid\":41,\"status\":\"Up\",\"lastcheck\":\"2026-10-06 04:00:00\"}]}", true)]
    [InlineData("{\"sensors\":[{\"objid\":99,\"status\":\"Up\",\"lastcheck\":\"now\"}]}", false)]
    [InlineData("{\"error\":\"login required\"}", false)]
    public async Task Actual_table_http_response_is_validated_before_it_counts_as_capacity(string body, bool valid)
    {
        using var client = new PrtgClient("https://prtg.example", "token", 10, false,
            new FixedBodyHandler(body), PrtgAuthModes.Token, "", "", "");
        var sent = false;
        var json = await client.GetBoundedJsonAsync("api/table.json?content=sensors&columns=objid,lastvalue,interval,lastcheck,status,primarychannel", 8192,
            onRequestSent: () => sent = true);

        Assert.True(sent);
        if (valid) PrtgSnapshotCapacityResponseValidator.Validate(json, [41]);
        else Assert.ThrowsAny<Exception>(() => PrtgSnapshotCapacityResponseValidator.Validate(json, [41]));
    }

    private sealed class FixedBodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public void Store_is_bounded_per_shape_per_hour_and_failure_requires_five_clean_retries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-prtg-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var store = new PrtgSnapshotCapacityStore(backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
            var now = new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 6; i++)
                store.Record(new PrtgSnapshotCapacitySample(Scope, Endpoint, Shape, now.AddSeconds(i), 9000, 50, "success"));
            Assert.Equal(5, store.Read().Count);

            store.Record(new PrtgSnapshotCapacitySample(Scope, Endpoint, Shape, now.AddMinutes(1), 30_000, 50, "timeout"));
            Assert.Single(store.Read());
            Assert.Equal(PrtgSnapshotCapacityStatus.CapacityUnverified,
                PrtgSnapshotCapacityEvaluator.Evaluate(1000, "conservative", Scope, Endpoint, Shape,
                    store.Read(), now.AddMinutes(1)).Status);

            for (var i = 0; i < 5; i++)
                store.Record(new PrtgSnapshotCapacitySample(Scope, Endpoint, Shape, now.AddHours(1).AddSeconds(i), 9000, 50, "success"));
            Assert.Equal(PrtgSnapshotCapacityStatus.CapacityQualified,
                PrtgSnapshotCapacityEvaluator.Evaluate(1000, "conservative", Scope, Endpoint, Shape,
                    store.Read(), now.AddHours(1).AddMinutes(1)).Status);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(backend.Blob(PrtgSnapshotCapacityStore.BlobKey).Read())
                <= PrtgSnapshotCapacityStore.MaximumSerializedBytes);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
