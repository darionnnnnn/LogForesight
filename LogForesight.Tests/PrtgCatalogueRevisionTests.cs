using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgCatalogueRevisionTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public PrtgCatalogueRevisionTests()
    {
        _connection.Open();
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    private LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlite(_connection)
        .Options);

    [Fact]
    public void DeviceAndSensorDeletesAdvanceRevisionOnceAndZeroDeletesLeaveItAlone()
    {
        var cutoff = new DateTime(2026, 10, 3, 2, 0, 0);
        using (var context = NewContext())
        {
            context.PrtgDevices.AddRange(
                new PrtgDeviceRow { Objid = 1, SyncedAt = cutoff.AddMinutes(-1) },
                new PrtgDeviceRow { Objid = 2, SyncedAt = cutoff.AddMinutes(1) });
            context.PrtgSensors.AddRange(
                new PrtgSensorRow { Objid = 11, DeviceObjid = 1, SyncedAt = cutoff.AddMinutes(-1) },
                new PrtgSensorRow { Objid = 22, DeviceObjid = 2, SyncedAt = cutoff.AddMinutes(1) });
            context.SaveChanges();
        }
        var store = new EfPrtgStore(NewContext);

        var deviceDelete = store.DeleteDevicesNotSyncedSince(cutoff);
        Assert.Equal(1, deviceDelete.Deleted);
        Assert.Equal(1, store.ReadCatalogueDataRevision());
        Assert.Equal(0, store.DeleteDevicesNotSyncedSince(cutoff.AddDays(-1)).Deleted);
        Assert.Equal(1, store.ReadCatalogueDataRevision());

        var sensorDelete = store.DeleteSensorsNotSyncedSince(cutoff, Array.Empty<long>(), cutoff,
            Array.Empty<long>());
        Assert.Equal(1, sensorDelete.Deleted);
        Assert.Equal(2, store.ReadCatalogueDataRevision());
        Assert.Equal(0, store.DeleteSensorsNotSyncedSince(cutoff.AddDays(-1), Array.Empty<long>(), cutoff,
            Array.Empty<long>()).Deleted);
        Assert.Equal(2, store.ReadCatalogueDataRevision());

        using var verify = NewContext();
        Assert.Equal(new long[] { 2 }, verify.PrtgDevices.Select(x => x.Objid).ToArray());
        Assert.Equal(new long[] { 22 }, verify.PrtgSensors.Select(x => x.Objid).ToArray());
    }

    [Fact]
    public void RevisionWriteFailureRollsBackTheDeviceDelete()
    {
        var cutoff = new DateTime(2026, 10, 3, 2, 0, 0);
        using (var context = NewContext())
        {
            context.PrtgDevices.AddRange(
                new PrtgDeviceRow { Objid = 1, SyncedAt = cutoff.AddMinutes(-1) },
                new PrtgDeviceRow { Objid = 2, SyncedAt = cutoff.AddMinutes(1) });
            context.Blobs.Add(new BlobRow
            {
                BlobKey = EfPrtgStore.CatalogueDataRevisionBlobKey,
                Content = "{}",
                Version = 7,
                UpdatedAt = cutoff
            });
            context.SaveChanges();
            context.Database.ExecuteSqlRaw("""
                CREATE TRIGGER fail_catalogue_revision_update BEFORE UPDATE OF version ON lf_blobs
                WHEN OLD.blob_key = 'prtg_catalogue_data_revision'
                BEGIN SELECT RAISE(ABORT, 'injected catalogue revision failure'); END;
                """);
        }
        var store = new EfPrtgStore(NewContext);

        Assert.ThrowsAny<Exception>(() => store.DeleteDevicesNotSyncedSince(cutoff));

        using var verify = NewContext();
        Assert.Equal(2, verify.PrtgDevices.Count());
        Assert.Equal(7, verify.Blobs.Single(x => x.BlobKey == EfPrtgStore.CatalogueDataRevisionBlobKey).Version);
    }

    public void Dispose() => _connection.Dispose();
}
