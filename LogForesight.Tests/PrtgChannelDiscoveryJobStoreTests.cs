using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgChannelDiscoveryJobStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-prtg-channel-discovery-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly PrtgChannelDiscoveryJobStore store;

    public PrtgChannelDiscoveryJobStoreTests()
    {
        backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, root);
        store = new PrtgChannelDiscoveryJobStore(backend);
    }

    [Fact]
    public void QueueIsDurableIdempotentAndCapsActiveJobsAtSixteen()
    {
        var now = DateTimeOffset.UtcNow;
        var first = store.Queue(Job(11, now), now);
        var duplicate = store.Queue(Job(11, now), now);
        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.Equal("queued", new PrtgChannelDiscoveryJobStore(new StorageBackend(
            new StorageSettings { Type = "Sqlite" }, root)).Read(first.JobId)!.Status);

        var jobs = new List<PrtgChannelDiscoveryJobStore.Job> { first };
        for (var sensor = 12; sensor <= 26; sensor++) jobs.Add(store.Queue(Job(sensor, now), now));
        Assert.Throws<InvalidOperationException>(() => store.Queue(Job(27, now), now));
        Assert.Equal(PrtgChannelDiscoveryJobStore.MaximumActiveJobs,
            jobs.Count(job => store.Read(job.JobId)?.Status == "queued"));
    }

    [Fact]
    public void DatabaseLaneLeaseAllowsOnlyOneGeneralJobAcrossStoreInstancesAndSurvivesRestart()
    {
        var now = DateTimeOffset.UtcNow;
        store.Queue(Job(11, now), now);
        store.Queue(Job(12, now), now);
        var secondInstance = new PrtgChannelDiscoveryJobStore(new StorageBackend(new StorageSettings { Type = "Sqlite" }, root));

        var acquired = Assert.IsType<PrtgChannelDiscoveryJobStore.Job>(store.TryAcquire("worker-a", now, TimeSpan.FromSeconds(30)));
        Assert.Null(secondInstance.TryAcquire("worker-b", now, TimeSpan.FromSeconds(30)));
        Assert.True(store.Complete(acquired, now.AddSeconds(1),
            [new() { ChannelObjectId = 9001, Caption = "CPU Load", Unit = "%", RawValue = 12.5 }], false));

        var saved = secondInstance.Read(acquired.JobId)!;
        Assert.Equal("completed", saved.Status);
        Assert.Single(saved.Channels);
        Assert.False(saved.ChannelsTruncated);
        var next = Assert.IsType<PrtgChannelDiscoveryJobStore.Job>(
            secondInstance.TryAcquire("worker-b", now.AddSeconds(2), TimeSpan.FromSeconds(30)));
        Assert.NotEqual(acquired.SensorObjid, next.SensorObjid);
    }

    [Fact]
    public void DeadlineExpiresQueuedJobsAndDoesNotExtendOnReadOrRestart()
    {
        var now = DateTimeOffset.UtcNow;
        var queued = store.Queue(Job(11, now), now);
        store.Expire(queued.DeadlineUtc);
        var afterRestart = new PrtgChannelDiscoveryJobStore(new StorageBackend(new StorageSettings { Type = "Sqlite" }, root))
            .Read(queued.JobId)!;
        Assert.Equal("expired", afterRestart.Status);
        Assert.Equal(queued.DeadlineUtc, afterRestart.DeadlineUtc);
        Assert.Empty(afterRestart.Channels);
    }

    private PrtgChannelDiscoveryJobStore.Job Job(long sensorId, DateTimeOffset now) => new()
    {
        JobId = Guid.NewGuid().ToString("N"), RequesterUserId = 1, SensorObjid = sensorId,
        SettingsRevision = "settings-r1", PolicyRevision = "policy-r1", SourceGeneration = "source-r1",
        HostSnapshotVersion = 1, ResourceGeneration = "generation-r1", IdentityEpoch = sensorId,
        ChannelGeneration = "channel-r1", QueuedAtUtc = now,
        DeadlineUtc = now + PrtgTrustedSamplingChannelDiscoveryHostedService.JobDeadline
    };

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
