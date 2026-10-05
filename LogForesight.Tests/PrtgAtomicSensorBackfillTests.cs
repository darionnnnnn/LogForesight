using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgAtomicSensorBackfillTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public PrtgAtomicSensorBackfillTests()
    {
        _connection.Open();
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    private LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlite(_connection)
        .Options);

    [Fact]
    public void AtomicSensorUpsert_第二批失敗會回滾全部感測器與目錄版本_移除故障後可重試()
    {
        var store = new EfPrtgStore(NewContext);
        var sensors = Enumerable.Range(1, 501).Select(id => new PrtgSensorRow
        {
            Objid = id,
            DeviceObjid = 10,
            Name = $"sensor-{id}",
            SensorType = "ping",
            Category = "availability"
        }).ToArray();

        using (var context = NewContext())
            context.Database.ExecuteSqlRaw("""
                CREATE TRIGGER fail_sensor_second_batch BEFORE INSERT ON lf_prtg_sensors
                WHEN NEW.objid = 501
                BEGIN SELECT RAISE(ABORT, 'injected second-batch failure'); END;
                """);

        Assert.ThrowsAny<Exception>(() => store.UpsertSensors(sensors, DateTime.Now, requireAtomic: true));
        using (var context = NewContext())
            Assert.Empty(context.PrtgSensors);
        Assert.Equal(0, store.ReadCatalogueDataRevision());

        using (var context = NewContext())
            context.Database.ExecuteSqlRaw("DROP TRIGGER fail_sensor_second_batch;");

        Assert.Equal(501, store.UpsertSensors(sensors, DateTime.Now, requireAtomic: true));
        using (var context = NewContext())
            Assert.Equal(501, context.PrtgSensors.Count());
        Assert.Equal(2, store.ReadCatalogueDataRevision());
    }

    [Fact]
    public void AtomicSensorUpsertAndQueue_第二批失敗後鏡像目錄版本與Queue皆為零_重試後一起提交()
    {
        var store = new EfPrtgStore(NewContext);
        var sensors = Enumerable.Range(1, 501).Select(id => new PrtgSensorRow
        {
            Objid = id, DeviceObjid = 20, Name = $"sensor-{id}", SensorType = "ping", Category = "availability"
        }).ToArray();
        var item = QueueItem(20);
        using (var context = NewContext())
            context.Database.ExecuteSqlRaw("""
                CREATE TRIGGER fail_sensor_second_batch_with_queue BEFORE INSERT ON lf_prtg_sensors
                WHEN NEW.objid = 501
                BEGIN SELECT RAISE(ABORT, 'injected second-batch failure'); END;
                """);

        Assert.ThrowsAny<Exception>(() => store.UpsertSensorsAndEnqueueRecentStateChanges(sensors, DateTime.Now, item));
        using (var context = NewContext()) Assert.Empty(context.PrtgSensors);
        Assert.Empty(store.ReadRecentStateChangeQueue());
        Assert.Equal(0, store.ReadCatalogueDataRevision());

        using (var context = NewContext()) context.Database.ExecuteSqlRaw("DROP TRIGGER fail_sensor_second_batch_with_queue;");
        Assert.Equal(501, store.UpsertSensorsAndEnqueueRecentStateChanges(sensors, DateTime.Now, item));
        Assert.Equal(501, store.GetAllSensors().Count);
        Assert.Equal(1, store.ReadRecentStateChangeQueue().Count);
        Assert.Equal(2, store.ReadCatalogueDataRevision());
    }

    [Fact]
    public void RecentStateQueue_前50失敗退避後第51項不會被餓死_Lease版本阻擋舊OwnerAck()
    {
        var store = new EfPrtgStore(NewContext);
        var now = DateTime.UtcNow;
        for (var i = 1; i <= 51; i++)
        {
            var deviceId = 1000 + i;
            var sensor = new PrtgSensorRow { Objid = deviceId, DeviceObjid = deviceId, Name = "ping", SensorType = "ping" };
            store.UpsertSensorsAndEnqueueRecentStateChanges(new[] { sensor }, DateTime.Now, QueueItem(deviceId, now));
        }

        var firstBatch = store.ClaimRecentStateChanges("owner-a", now, TimeSpan.FromMinutes(2), 50);
        Assert.Equal(50, firstBatch.Count);
        foreach (var lease in firstBatch)
            Assert.True(store.RetryRecentStateChange(lease, now, TimeSpan.FromMinutes(1)));

        var fairNext = store.ClaimRecentStateChanges("owner-b", now, TimeSpan.FromMinutes(2), 50);
        Assert.Single(fairNext);
        Assert.Equal(1051, fairNext[0].Item.DeviceObjid);
        var expiredOwnerLease = firstBatch[0];
        var replacement = store.ClaimRecentStateChanges("owner-c", now.AddMinutes(2), TimeSpan.FromMinutes(2), 1);
        Assert.Single(replacement);
        Assert.False(store.AcknowledgeRecentStateChange(expiredOwnerLease));
    }

    [Fact]
    public void RecentStateQueue_只在目前業務範圍內更換來源或scope並重建已完成項()
    {
        var store = new EfPrtgStore(NewContext);
        var now = DateTime.UtcNow;
        const long deviceId = 77;
        store.UpsertSensorsAndEnqueueRecentStateChanges(
            new[] { new PrtgSensorRow { Objid = 77, DeviceObjid = 77, Name = "ping", SensorType = "ping" } },
            DateTime.Now, QueueItem(deviceId, now));
        var lease = Assert.Single(store.ClaimRecentStateChanges("owner", now, TimeSpan.FromMinutes(2), 1));
        Assert.True(store.AcknowledgeRecentStateChange(lease));
        Assert.Empty(store.ClaimRecentStateChanges("owner2", now.AddSeconds(1), TimeSpan.FromMinutes(2), 50));

        Assert.Equal(1, store.ReconcileRecentStateChanges(new HashSet<long>(), "source-b", "scope-b",
            now.Date.AddDays(-1), now.Date, now.AddSeconds(2)));
        Assert.Empty(store.ClaimRecentStateChanges("owner3", now.AddSeconds(2), TimeSpan.FromMinutes(2), 50));
        Assert.Equal(1, store.ReconcileRecentStateChanges(new HashSet<long> { deviceId }, "source-b", "scope-b",
            now.Date.AddDays(-1), now.Date, now.AddSeconds(3)));
        var rebuilt = Assert.Single(store.ClaimRecentStateChanges("owner4", now.AddSeconds(3), TimeSpan.FromMinutes(2), 50));
        Assert.Equal("source-b", rebuilt.Item.SourceIdentityHash);
        Assert.Equal("scope-b", rebuilt.Item.BusinessScopeVersion);
    }

    [Fact]
    public void RecentStateQueue_退出範圍即清理完成項_鏡像仍在時重新入scope可重建()
    {
        var store = new EfPrtgStore(NewContext);
        var now = DateTime.UtcNow;
        const long deviceId = 88;
        store.UpsertSensorsAndEnqueueRecentStateChanges(
            new[] { new PrtgSensorRow { Objid = 88, DeviceObjid = 88, Name = "ping", SensorType = "ping" } },
            DateTime.Now, QueueItem(deviceId, now));
        var lease = Assert.Single(store.ClaimRecentStateChanges("owner", now, TimeSpan.FromMinutes(2), 1));
        Assert.True(store.AcknowledgeRecentStateChange(lease));

        var afterRemoval = now.AddMinutes(1);
        Assert.Equal(1, store.ReconcileRecentStateChanges(new HashSet<long>(), "source-b", "scope-b",
            afterRemoval.Date.AddDays(-1), afterRemoval.Date, afterRemoval));
        Assert.Empty(store.ReadRecentStateChangeQueue());

        Assert.Equal(1, store.ReconcileRecentStateChanges(new HashSet<long> { deviceId }, "source-b", "scope-b",
            afterRemoval.Date.AddDays(-1), afterRemoval.Date, afterRemoval));
        var rebuilt = Assert.Single(store.ClaimRecentStateChanges("owner-2", afterRemoval, TimeSpan.FromMinutes(2), 50));
        Assert.Equal("source-b", rebuilt.Item.SourceIdentityHash);
        Assert.Equal("scope-b", rebuilt.Item.BusinessScopeVersion);
    }

    [Fact]
    public void RecentStateQueue_兩台換兩台時取消舊pending並重建新鏡像工作()
    {
        var store = new EfPrtgStore(NewContext);
        var now = DateTime.UtcNow;
        foreach (var id in new long[] { 101, 102 })
            store.UpsertSensorsAndEnqueueRecentStateChanges(
                new[] { new PrtgSensorRow { Objid = id, DeviceObjid = id, Name = "ping", SensorType = "ping" } },
                DateTime.Now, QueueItem(id, now));
        store.UpsertSensors(new long[] { 201, 202 }.Select(id => new PrtgSensorRow
        { Objid = id, DeviceObjid = id, Name = "ping", SensorType = "ping" }).ToArray(), DateTime.Now);

        Assert.True(store.ReconcileRecentStateChanges(new HashSet<long> { 201, 202 }, "source-b", "scope-b",
            now.Date.AddDays(-1), now.Date, now.AddSeconds(1)) > 0);
        Assert.Equal(new long[] { 201, 202 }, store.ReadRecentStateChangeQueue().Select(x => x.DeviceObjid).Order().ToArray());
        Assert.All(store.ReadRecentStateChangeQueue(), item => Assert.Null(item.CompletedAtUtc));
        var stopped = Assert.IsType<PrtgRecentStateChangeQueueStopSummary>(store.ReadRecentStateChangeQueueStopSummary());
        Assert.Equal("business-scope-removed", stopped.Reason);
        Assert.Equal(2, stopped.StoppedCount);
        Assert.Equal(new long[] { 101, 102 }, stopped.StoppedDeviceObjidSample);
        Assert.Equal(0, stopped.RejectedDeviceCount);
    }

    [Fact]
    public void RecentStateQueue_三千換三千不受舊queue容量阻塞且無scope外ledger()
    {
        var store = new EfPrtgStore(NewContext);
        var now = DateTime.UtcNow;
        var oldIds = Enumerable.Range(1, 3000).Select(id => 100_000L + id).ToArray();
        var newIds = Enumerable.Range(1, 3000).Select(id => 200_000L + id).ToArray();
        using (var context = NewContext())
        {
            foreach (var id in oldIds)
                context.Blobs.Add(new BlobRow
                {
                    BlobKey = EfPrtgStore.RecentStateChangeQueuePrefix + id,
                    Content = JsonSerializer.Serialize(QueueItem(id, now)), UpdatedAt = now, Version = 1
                });
            foreach (var id in newIds)
                context.PrtgSensors.Add(new PrtgSensorRow { Objid = id, DeviceObjid = id, Name = "ping", SensorType = "ping" });
            context.SaveChanges();
        }

        Assert.True(store.ReconcileRecentStateChanges(newIds.ToHashSet(), "source-b", "scope-b",
            now.Date.AddDays(-1), now.Date, now.AddSeconds(1)) > 0);
        var queue = store.ReadRecentStateChangeQueue();
        Assert.Equal(3000, queue.Count);
        Assert.Equal(newIds.Order().ToArray(), queue.Select(x => x.DeviceObjid).Order().ToArray());
        Assert.Equal(3000, store.ReadRecentStateChangeQueueStopSummary()?.StoppedCount);
        Assert.Equal("business-scope-removed", store.ReadRecentStateChangeQueueStopSummary()?.Reason);
    }

    [Fact]
    public void RecentStateQueue_超過15k完整scope明確拒收且保留容量原因()
    {
        var store = new EfPrtgStore(NewContext);
        var now = DateTime.UtcNow;
        var overCapacity = Enumerable.Range(1, EfPrtgStore.RecentStateChangeQueueCapacity + 1).Select(id => (long)id).ToHashSet();
        var error = Assert.Throws<InvalidOperationException>(() => store.ReconcileRecentStateChanges(overCapacity,
            "source", "scope", now.Date.AddDays(-1), now.Date, now));
        Assert.Contains("系統未靜默省略任何裝置。", error.Message);
        Assert.Equal("business-scope-capacity-exceeded", store.ReadRecentStateChangeQueueStopSummary()?.Reason);
        Assert.Equal(EfPrtgStore.RecentStateChangeQueueCapacity + 1,
            store.ReadRecentStateChangeQueueStopSummary()?.RejectedDeviceCount);
    }

    [Fact]
    public void RecentStateQueue_取消尚未開始的批次會釋放lease但不增加重試次數()
    {
        var store = new EfPrtgStore(NewContext);
        var now = DateTime.UtcNow;
        const long deviceId = 808;
        store.UpsertSensorsAndEnqueueRecentStateChanges(
            new[] { new PrtgSensorRow { Objid = deviceId, DeviceObjid = deviceId, Name = "ping", SensorType = "ping" } },
            DateTime.Now, QueueItem(deviceId, now));
        var lease = Assert.Single(store.ClaimRecentStateChanges("owner", now, TimeSpan.FromMinutes(2), 1));

        Assert.True(store.ReleaseRecentStateChangeLease(lease, now.AddSeconds(1)));
        var released = Assert.Single(store.ReadRecentStateChangeQueue());
        Assert.Equal(0, released.Attempts);
        Assert.Null(released.LeaseOwner);
        Assert.Single(store.ClaimRecentStateChanges("owner-2", now.AddSeconds(2), TimeSpan.FromMinutes(2), 1));
    }

    [Fact]
    public void RecentStateQueue_跨午夜保留未完成工作固定日期_完成後才以新日期重建()
    {
        var store = new EfPrtgStore(NewContext);
        var beforeMidnight = new DateTime(2026, 10, 5, 23, 59, 0, DateTimeKind.Utc);
        const long deviceId = 909;
        store.UpsertSensorsAndEnqueueRecentStateChanges(
            new[] { new PrtgSensorRow { Objid = deviceId, DeviceObjid = deviceId, Name = "ping", SensorType = "ping" } },
            DateTime.Now, QueueItem(deviceId, beforeMidnight));

        var afterMidnight = beforeMidnight.AddMinutes(2);
        Assert.Equal(0, store.ReconcileRecentStateChanges(new HashSet<long> { deviceId }, "source-a", "scope-a",
            afterMidnight.Date.AddDays(-1), afterMidnight.Date, afterMidnight));
        var stillFixed = Assert.Single(store.ClaimRecentStateChanges("owner", afterMidnight, TimeSpan.FromMinutes(2), 1));
        Assert.Equal(beforeMidnight.Date.AddDays(-1), stillFixed.Item.FromLocalDate);
        Assert.Equal(beforeMidnight.Date, stillFixed.Item.ToLocalDate);
        Assert.True(store.AcknowledgeRecentStateChange(stillFixed));

        var followingDay = afterMidnight.AddDays(1);
        Assert.Equal(1, store.ReconcileRecentStateChanges(new HashSet<long> { deviceId }, "source-a", "scope-a",
            followingDay.Date.AddDays(-1), followingDay.Date, followingDay));
        var freshWindow = Assert.Single(store.ClaimRecentStateChanges("owner-2", followingDay, TimeSpan.FromMinutes(2), 1));
        Assert.Equal(followingDay.Date.AddDays(-1), freshWindow.Item.FromLocalDate);
        Assert.Equal(followingDay.Date, freshWindow.Item.ToLocalDate);
    }

    private static PrtgRecentStateChangeQueueItem QueueItem(long deviceId, DateTime? now = null)
    {
        var created = now ?? DateTime.UtcNow;
        return new PrtgRecentStateChangeQueueItem(deviceId, created.Date.AddDays(-1), created.Date,
            "source-a", "scope-a", created, created, 0, null, null, null);
    }

    public void Dispose() => _connection.Dispose();
}
