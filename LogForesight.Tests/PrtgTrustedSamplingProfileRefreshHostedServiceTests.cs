using System.Text.Json;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTrustedSamplingProfileRefreshHostedServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-profile-refresh-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly FakeHostStore hosts = new();

    public PrtgTrustedSamplingProfileRefreshHostedServiceTests()
    {
        backend = new(new StorageSettings { Type = "Sqlite" }, root);
        var settings = new SystemSettingsStore(backend.Blob("system_settings"));
        settings.Update(value =>
        {
            value.PrtgEnabled = true; value.PrtgUrl = "https://source.example";
            value.PrtgAuthMode = PrtgAuthModes.Token; value.PrtgApiTokenEnc = "fixture-token";
            value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        hosts.Upsert(new WebHost { HostName = "enabled-host", Active = true, Source = "netiq" });
    }

    [Fact]
    public async Task FullSweepPersistsCurrentPerSensorReasonsAndBoundedPagesAcrossRestart()
    {
        var ids = Enumerable.Range(10_001, 101).Select(value => (long)value).ToArray();
        SeedScope(ids);
        backend.PrtgStore().UpsertDevices(ids.Select(id => new PrtgDeviceRow { Objid = id + 50_000, Name = $"device-{id}" }).ToArray(), DateTime.UtcNow);
        backend.PrtgStore().UpsertSensors(ids.Select(id => new PrtgSensorRow
        {
            Objid = id, DeviceObjid = id + 50_000, Name = $"CPU {id}", SensorType = "CPU",
            Category = PrtgSensorCategories.Cpu, CategorySource = "auto"
        }).ToArray(), DateTime.UtcNow);

        var first = Worker();
        await first.RunSliceAsync(CancellationToken.None);

        var progress = first.ReadProgress();
        Assert.Equal(101, progress.SelectedSensors);
        Assert.Equal(0, progress.EligibleSensors); // no enabled current resource rule
        Assert.Equal("full-scope-traversal-complete", progress.LastOutcome);
        Assert.Equal(0, progress.RawRequestCount);
        var firstPage = first.ReadSensorProgressPage(0, 100);
        Assert.Equal(101, firstPage.Total);
        Assert.Equal(100, firstPage.Rows.Count);
        Assert.Equal(100, firstPage.NextOffset);
        Assert.All(firstPage.Rows, row =>
        {
            Assert.False(row.Eligible);
            Assert.Equal("unavailable", row.Status);
            Assert.Equal("no-enabled-current-rule", row.Reason);
            Assert.True(row.NextAttemptAtUtc > DateTimeOffset.UtcNow);
        });
        Assert.Single(first.ReadSensorProgressPage(100, 100).Rows);

        var restarted = Worker();
        await restarted.RunSliceAsync(CancellationToken.None);
        Assert.Equal(progress.CompletedAtUtc, restarted.ReadProgress().CompletedAtUtc);
        Assert.Equal(firstPage.Rows.Select(row => row.Reason),
            restarted.ReadSensorProgressPage(0, 100).Rows.Select(row => row.Reason));
    }

    [Fact]
    public void SensorPageWritesStayBoundedAndAggregateDeltasAreIdempotent()
    {
        var store = new PrtgTrustedSamplingProfileRefreshStateStore(backend);
        Assert.True(store.TryAcquire("test-owner", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), out var leaseVersion));
        var overview = backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.OverviewKey);
        var page = Enumerable.Range(1, 100).Select(index => new PrtgTrustedSamplingProfileRefreshStateStore.SensorState
        {
            SensorObjid = index, Eligible = true, ContractFingerprint = "contract",
            Status = index <= 60 ? "qualified" : "waiting", Reason = index <= 60 ? "current" : "unsupported",
            NextAttemptAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        }).ToArray();

        // Use the actual durable owner/version columns and full scope key expected by the lease-gated writer.
        overview.Mutate(_ => (JsonSerializer.Serialize(new
        {
            ScopeFingerprint = "A1B2", Cursor = 0, SelectedSensors = 100,
            LeaseOwner = "test-owner", LeaseVersion = leaseVersion,
            LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(2)
        }), true));
        store.SavePage("A1B2", 0, page, "test-owner", leaseVersion);
        var savedPage = store.ReadPage("A1B2", 0);
        Assert.Equal(100, savedPage.Sensors.Count);
        Assert.True(Encoding.UTF8.GetByteCount(backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.PageKey("A1B2", 0)).Read()!)
            <= PrtgTrustedSamplingProfileRefreshStateStore.MaximumPageBytes);
        Assert.Equal(60, store.ReadOverview().Qualified);
        Assert.Equal(40, store.ReadOverview().Waiting);

        store.SavePage("A1B2", 0, page, "test-owner", leaseVersion);
        Assert.Equal(60, store.ReadOverview().Qualified);
        Assert.Equal(40, store.ReadOverview().Waiting);
    }

    [Fact]
    public async Task ActiveDurableLeasePreventsSecondWorkerFromTakingOver()
    {
        var blob = backend.Blob(PrtgTrustedSamplingProfileRefreshHostedService.ProgressBlobKey);
        blob.Mutate(_ => (JsonSerializer.Serialize(new
        {
            ScopeFingerprint = "existing", Cursor = 0, SelectedSensors = 1,
            LeaseOwner = "first-instance", LeaseVersion = 9,
            LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        }), true));

        await Worker().RunSliceAsync(CancellationToken.None);

        using var saved = JsonDocument.Parse(blob.Read()!);
        Assert.Equal("first-instance", saved.RootElement.GetProperty("LeaseOwner").GetString());
        Assert.Equal(9, saved.RootElement.GetProperty("LeaseVersion").GetInt64());
    }

    private void SeedScope(long[] sensorIds)
    {
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.Revision = "r1"; policy.CoreSystemId = "core"; policy.SourceGeneration = "source";
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl);
            policy.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1); policy.HostIds = [1]; policy.SensorIds = sensorIds.ToList();
            policy.SourceTimeZoneId = "UTC"; policy.SourceCultureName = "en-US";
            policy.RawTimestampTimeZoneId = "UTC"; policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "fixture-time-basis";
        });
    }

    private PrtgTrustedSamplingProfileRefreshHostedService Worker() =>
        new(backend, hosts, NullLogger<PrtgTrustedSamplingProfileRefreshHostedService>.Instance);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }
}
