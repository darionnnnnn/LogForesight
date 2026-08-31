using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgHostMapperTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfPrtgStore CreateStore() => new(_fx.NewContext);

    private sealed class TestConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private void SeedDevices(params PrtgDeviceRow[] devices)
    {
        var store = CreateStore();
        store.UpsertDevices(devices, DateTime.Now);
    }

    [Fact]
    public void MapForDate_一對一時狀態為Ok且填入主機()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 10, HostName = "srv-app01", IpAddress = "192.168.1.10", Active = true });
        });

        SeedDevices(new PrtgDeviceRow { Objid = 1001, Name = "PRTG-App01", Ip = "192.168.1.10" });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = new PrtgHostMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(1, result.Ok);
        Assert.Equal(0, result.Conflict);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(0, result.SkippedNoIp);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        Assert.Equal(1001, rows[0].DeviceObjid);
        Assert.Equal(10, rows[0].HostId);
        Assert.Equal("srv-app01", rows[0].HostName);
        Assert.Equal(PrtgMapStatus.Ok, rows[0].MapStatus);
        Assert.Null(rows[0].Note);
    }

    [Fact]
    public void MapForDate_一IP多台主機時取HostId最小者且標Conflict()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 20, HostName = "srv-db02", IpAddress = "10.0.0.5", Active = true });
            hosts.Add(new WebHost { HostId = 5, HostName = "srv-db01", IpAddress = "10.0.0.5", Active = true });
        });

        SeedDevices(new PrtgDeviceRow { Objid = 2001, Name = "PRTG-Db", Ip = "10.0.0.5" });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = new PrtgHostMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(0, result.Ok);
        Assert.Equal(1, result.Conflict);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(0, result.SkippedNoIp);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        Assert.Equal(2001, rows[0].DeviceObjid);
        Assert.Equal(5, rows[0].HostId); // 最小的 HostId
        Assert.Equal("srv-db01", rows[0].HostName);
        Assert.Equal(PrtgMapStatus.Conflict, rows[0].MapStatus);
        Assert.NotNull(rows[0].Note);
        Assert.Contains("IP 由 2 台主機共用", rows[0].Note);
        Assert.Contains("srv-db01", rows[0].Note);
        Assert.Contains("srv-db02", rows[0].Note);
    }

    [Fact]
    public void MapForDate_一IP多個device時全部標Conflict且不填主機()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.Upsert(new WebHost { HostId = 1, HostName = "srv-web", IpAddress = "192.168.1.50", Active = true });

        SeedDevices(
            new PrtgDeviceRow { Objid = 3001, Name = "PRTG-Web-Ping", Ip = "192.168.1.50" },
            new PrtgDeviceRow { Objid = 3002, Name = "PRTG-Web-SNMP", Ip = "192.168.1.50" });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = new PrtgHostMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(0, result.Ok);
        Assert.Equal(2, result.Conflict);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(0, result.SkippedNoIp);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            Assert.Null(row.HostId);
            Assert.Null(row.HostName);
            Assert.Equal(PrtgMapStatus.Conflict, row.MapStatus);
            Assert.Equal("此 IP 同時有 2 個 PRTG device", row.Note);
        }
    }

    [Fact]
    public void MapForDate_IP查無對應時狀態為Unmatched且不填主機()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.Upsert(new WebHost { HostId = 1, HostName = "srv-other", IpAddress = "192.168.99.99", Active = true });

        SeedDevices(new PrtgDeviceRow { Objid = 4001, Name = "PRTG-Standalone", Ip = "192.168.1.200" });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = new PrtgHostMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(0, result.Ok);
        Assert.Equal(0, result.Conflict);
        Assert.Equal(1, result.Unmatched);
        Assert.Equal(0, result.SkippedNoIp);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        Assert.Equal(4001, rows[0].DeviceObjid);
        Assert.Null(rows[0].HostId);
        Assert.Null(rows[0].HostName);
        Assert.Equal(PrtgMapStatus.Unmatched, rows[0].MapStatus);
        Assert.Equal("PRTG 有此 device，主機主檔查無對應 IP", rows[0].Note);
    }

    [Fact]
    public void MapForDate_device沒有IP時不產生對應列()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.Upsert(new WebHost { HostId = 1, HostName = "srv-app", IpAddress = "192.168.1.1", Active = true });

        SeedDevices(
            new PrtgDeviceRow { Objid = 5001, Name = "Probe Device", Ip = null },
            new PrtgDeviceRow { Objid = 5002, Name = "Empty IP Device", Ip = "   " });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = new PrtgHostMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(0, result.Ok);
        Assert.Equal(0, result.Conflict);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(2, result.SkippedNoIp);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Empty(rows);
    }

    [Fact]
    public void MapForDate_已停用或已合併的主機不參與對應()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        // 停用的主機
        hostStore.Upsert(new WebHost { HostId = 1, HostName = "srv-inactive", IpAddress = "192.168.1.30", Active = false });
        // 已合併進其他主機的墓碑
        hostStore.Upsert(new WebHost { HostId = 2, HostName = "srv-merged", IpAddress = "192.168.1.30", Active = true, MergedInto = 99 });

        SeedDevices(new PrtgDeviceRow { Objid = 6001, Name = "PRTG-Srv", Ip = "192.168.1.30" });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = new PrtgHostMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        // 兩台主機均被排除（一台 Active=false、一台 MergedInto != null），結果應為 Unmatched 且不填入主機
        Assert.Equal(0, result.Ok);
        Assert.Equal(0, result.Conflict);
        Assert.Equal(1, result.Unmatched);
        Assert.Equal(0, result.SkippedNoIp);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        Assert.Equal(6001, rows[0].DeviceObjid);
        Assert.Null(rows[0].HostId);
        Assert.Null(rows[0].HostName);
        Assert.Equal(PrtgMapStatus.Unmatched, rows[0].MapStatus);
    }

    [Fact]
    public void MapForDate_同日重跑就地取代不累積()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.Upsert(new WebHost { HostId = 10, HostName = "srv-01", IpAddress = "192.168.1.10", Active = true });

        SeedDevices(new PrtgDeviceRow { Objid = 7001, Name = "PRTG-01", Ip = "192.168.1.10" });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = new PrtgHostMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act - 跑第 1 次
        var result1 = mapper.MapForDate(mapDate);
        Assert.Equal(1, result1.Ok);
        var rowsFirst = store.GetHostMapForDate(mapDate);
        Assert.Single(rowsFirst);

        // Act - 跑第 2 次
        var result2 = mapper.MapForDate(mapDate);
        Assert.Equal(1, result2.Ok);
        var rowsSecond = store.GetHostMapForDate(mapDate);

        // Assert - 不產生重複列
        Assert.Single(rowsSecond);
        Assert.Equal(7001, rowsSecond[0].DeviceObjid);
    }

    [Fact]
    public void MapForDate_不同日期各自獨立保存()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.Upsert(new WebHost { HostId = 10, HostName = "srv-01", IpAddress = "192.168.1.10", Active = true });

        SeedDevices(new PrtgDeviceRow { Objid = 8001, Name = "PRTG-01", Ip = "192.168.1.10" });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = new PrtgHostMapper(store, hostStore, console);
        var day1 = new DateTime(2026, 8, 29);
        var day2 = new DateTime(2026, 8, 30);

        // Act
        mapper.MapForDate(day1);
        mapper.MapForDate(day2);

        // Assert
        var day1Rows = store.GetHostMapForDate(day1);
        var day2Rows = store.GetHostMapForDate(day2);
        Assert.Single(day1Rows);
        Assert.Single(day2Rows);
        Assert.Equal(day1.Date, day1Rows[0].MapDate);
        Assert.Equal(day2.Date, day2Rows[0].MapDate);
    }

    [Fact]
    public void MapForDate_IP比對忽略前後空白與大小寫()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 15, HostName = "srv-case", IpAddress = "  192.168.1.88  ", Active = true });
        });

        SeedDevices(new PrtgDeviceRow { Objid = 9001, Name = "PRTG-Case", Ip = "192.168.1.88 " });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = new PrtgHostMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(1, result.Ok);
        Assert.Equal(0, result.Conflict);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        Assert.Equal(15, rows[0].HostId);
        Assert.Equal("srv-case", rows[0].HostName);
    }

    [Fact]
    public void MapForDate_一IP超過五台主機_Note標記等N台()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        for (var i = 1; i <= 7; i++)
        {
            hostStore.Upsert(new WebHost
            {
                HostId = i,
                HostName = $"srv-cluster-{i:D2}",
                IpAddress = "10.10.10.10",
                Active = true
            });
        }

        SeedDevices(new PrtgDeviceRow { Objid = 9500, Name = "PRTG-Cluster", Ip = "10.10.10.10" });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = new PrtgHostMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(1, result.Conflict);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        Assert.Equal(1, rows[0].HostId); // 最小 HostId
        Assert.Equal("srv-cluster-01", rows[0].HostName);
        Assert.NotNull(rows[0].Note);
        Assert.Contains("等 7 台", rows[0].Note);
        Assert.Contains("srv-cluster-01", rows[0].Note);
        Assert.Contains("srv-cluster-05", rows[0].Note);
    }

    [Fact]
    public void GetMirrorSummary_回傳正確的計數與最後同步時間()
    {
        // Arrange
        var store = CreateStore();
        var now = DateTime.Now;
        var syncTime = new DateTime(2026, 8, 30, 10, 0, 0);

        store.UpsertDevices(new[]
        {
            new PrtgDeviceRow { Objid = 101, Name = "Dev1", SyncedAt = syncTime }
        }, syncTime);

        store.UpsertSensors(new[]
        {
            new PrtgSensorRow { Objid = 201, DeviceObjid = 101, Name = "Sen1", SyncedAt = syncTime }
        }, syncTime);

        var periodTime = new DateTime(2026, 8, 30, 11, 0, 0);
        store.UpsertValues(new[]
        {
            new PrtgValueRow { SensorObjid = 201, PeriodStart = periodTime, AvgValue = 50 }
        });

        var changeTime = new DateTime(2026, 8, 30, 11, 30, 0);
        store.AppendStateChanges(new[]
        {
            new PrtgStateChangeRow { SensorObjid = 201, ChangedAt = changeTime, Status = "Down" }
        });

        // Act
        var summary = store.GetMirrorSummary();

        // Assert
        Assert.Equal(1, summary.DeviceCount);
        Assert.Equal(1, summary.SensorCount);
        Assert.Equal(syncTime, summary.LastDeviceSync);
        Assert.Equal(syncTime, summary.LastSensorSync);
        Assert.Equal(periodTime, summary.LastValueAt);
        Assert.Equal(changeTime, summary.LastStateChangeAt);
    }
}
