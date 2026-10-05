using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgStoreManagementConsumerTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly DateTime _mapDate = new(2026, 10, 3);

    [Fact]
    public void PausedDeviceAndSensorAreExcludedFromSelectableScopeButRemainInInventoryCounts()
    {
        using (var db = _fixture.NewContext())
        {
            db.PrtgDevices.AddRange(
                new PrtgDeviceRow { Objid = 101, Paused = false },
                new PrtgDeviceRow { Objid = 102, Paused = true },
                new PrtgDeviceRow { Objid = 103, Paused = false });
            db.PrtgSensors.AddRange(
                new PrtgSensorRow { Objid = 201, DeviceObjid = 101, Category = PrtgSensorCategories.Disk, SensorType = "disk" },
                new PrtgSensorRow { Objid = 202, DeviceObjid = 102, Category = PrtgSensorCategories.Disk, SensorType = "disk" },
                new PrtgSensorRow { Objid = 203, DeviceObjid = 103, Category = PrtgSensorCategories.Disk, SensorType = "disk", Paused = true });
            db.PrtgHostMaps.AddRange(Map(101), Map(102), Map(103));
            db.SaveChanges();
        }

        var store = new EfPrtgStore(_fixture.NewContext);
        var hosts = Enumerable.Range(1, 300).Select(id => (long)id).ToArray();
        var sensors = Enumerable.Range(201, 300).Select(id => (long)id).ToArray();

        var page = store.GetMonitoringSensorPage(_mapDate, [7], null, 0, 100);
        var validCount = store.CountValidMonitoringSensors(_mapDate, hosts, sensors);
        var inventory = store.GetReadinessInventoryCounts(hosts, ["disk"], _mapDate, _mapDate.AddDays(-30));

        Assert.Equal(1, page.Total);
        Assert.Equal(201, Assert.Single(page.Rows).SensorId);
        Assert.Equal(1, validCount);
        Assert.Equal(new EfPrtgStore.PrtgReadinessInventoryCounts(3, 3, 3, 2, 0, 0, 0), inventory);
    }

    [Fact]
    public void NoMapFullScopeEvaluationCountsAndPagesScalarRows_ExcludingPausedDevicesAndSensors()
    {
        using (var db = _fixture.NewContext())
        {
            db.PrtgDevices.AddRange(
                new PrtgDeviceRow { Objid = 301, Paused = false },
                new PrtgDeviceRow { Objid = 302, Paused = true },
                new PrtgDeviceRow { Objid = 303, Paused = false });
            db.PrtgSensors.AddRange(
                new PrtgSensorRow { Objid = 401, DeviceObjid = 301, Name = "active", SensorType = "ping" },
                new PrtgSensorRow { Objid = 402, DeviceObjid = 302, Name = "paused-device", SensorType = "ping" },
                new PrtgSensorRow { Objid = 403, DeviceObjid = 303, Name = "paused-sensor", SensorType = "ping", Paused = true },
                new PrtgSensorRow { Objid = 404, DeviceObjid = 999, Name = "missing-device", SensorType = "ping" });
            db.SaveChanges();
        }

        var store = new EfPrtgStore(_fixture.NewContext);
        var page = store.GetMonitoringEvaluationPage(null, [], fullScope: true, offset: 0, take: 100);
        var laterPage = store.GetMonitoringEvaluationPage(null, [], fullScope: true, offset: 1, take: 1);
        var restricted = store.GetMonitoringEvaluationPage(null, [], fullScope: false, offset: 0, take: 100);

        Assert.Equal(2, page.Total);
        Assert.Equal(new long[] { 401, 404 }, page.Rows.Select(row => row.SensorId));
        Assert.All(page.Rows, row => Assert.Null(row.HostId));
        Assert.Equal(2, laterPage.Total);
        Assert.Equal(404, Assert.Single(laterPage.Rows).SensorId);
        Assert.Equal(0, restricted.Total);
        Assert.Empty(restricted.Rows);
    }

    private PrtgHostMapRow Map(long deviceObjid) => new()
    {
        DeviceObjid = deviceObjid,
        MapDate = _mapDate,
        HostId = 7,
        MapStatus = PrtgMapStatus.Ok
    };

    public void Dispose() => _fixture.Dispose();
}
