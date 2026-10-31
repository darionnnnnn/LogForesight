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

    /// <summary>
    /// 建立對應服務。位址解析器預設用正式實作——測試用的 IP 都是合法 IP，
    /// 第一層純語法正規化就會命中，不會真的去查 DNS。
    /// 需要驗證 DNS 名稱路徑的測試自行傳入 <see cref="FakeResolver"/>。
    /// </summary>
    private static PrtgHostMapper CreateMapper(
        EfPrtgStore store, IHostStore hostStore, IRunConsole console, IPrtgAddressResolver? resolver = null)
        => new(store, hostStore, console, resolver ?? new PrtgAddressResolver());

    /// <summary>可控的位址解析器：先走純語法正規化，對不到時查這張表。</summary>
    private sealed class FakeResolver : IPrtgAddressResolver
    {
        private readonly Dictionary<string, string?> _map;

        public FakeResolver(Dictionary<string, string?> map)
        {
            _map = new Dictionary<string, string?>(map, StringComparer.OrdinalIgnoreCase);
        }

        public string? Resolve(string? value)
        {
            var normalized = PrtgAddress.Normalize(value);
            if (normalized != null) return normalized;

            var token = PrtgAddress.HostToken(value);
            if (token == null) return null;
            return _map.TryGetValue(token, out var mapped) ? mapped : null;
        }
    }

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
        var mapper = CreateMapper(store, hostStore, console);
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
        var mapper = CreateMapper(store, hostStore, console);
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
    public void MapForDate_一IP多個device時全部對應到該IP的主機()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.Upsert(new WebHost { HostId = 1, HostName = "srv-web", IpAddress = "192.168.1.50", Active = true });

        SeedDevices(
            new PrtgDeviceRow { Objid = 3001, Name = "PRTG-Web-Ping", Ip = "192.168.1.50" },
            new PrtgDeviceRow { Objid = 3002, Name = "PRTG-Web-SNMP", Ip = "192.168.1.50" });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert：同一台伺服器在 PRTG 有兩個 device（Ping、SNMP），以 IP 為主全部對到 srv-web
        Assert.Equal(2, result.Ok);
        Assert.Equal(0, result.Conflict);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(0, result.SkippedNoIp);
        Assert.Equal(1, result.MappedHosts);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            Assert.Equal(1, row.HostId);
            Assert.Equal("srv-web", row.HostName);
            Assert.Equal(PrtgMapStatus.Ok, row.MapStatus);
            // 裝置名稱不帶括號＝PRTG 自取的顯示名，不拿來比對主機名
            Assert.Null(row.Note);
        }
    }

    [Fact]
    public void MapForDate_一IP多個device且IP由兩台主機共用_全部標Conflict並指向HostId最小者()
    {
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 5, HostName = "srv-b", IpAddress = "192.168.9.9", Active = true });
            hosts.Add(new WebHost { HostId = 2, HostName = "srv-a", IpAddress = "192.168.9.9", Active = true });
        });
        SeedDevices(
            new PrtgDeviceRow { Objid = 1, Name = "Ping", Ip = "192.168.9.9" },
            new PrtgDeviceRow { Objid = 2, Name = "WMI", Ip = "192.168.9.9" });

        var store = CreateStore();
        var result = CreateMapper(store, hostStore, new TestConsole()).MapForDate(new DateTime(2026, 8, 30));

        Assert.Equal(0, result.Ok);
        Assert.Equal(2, result.Conflict);
        var rows = store.GetHostMapForDate(new DateTime(2026, 8, 30));
        Assert.All(rows, r =>
        {
            Assert.Equal(PrtgMapStatus.Conflict, r.MapStatus);
            Assert.Equal(2, r.HostId);
        });
    }

    [Fact]
    public void MapForDate_IP對上但括號內主機名與主檔不同_Ok並記名稱不一致()
    {
        var hostStore = new FakeHostStore();
        hostStore.Upsert(new WebHost { HostId = 1, HostName = "srv-new.corp.local", IpAddress = "10.1.1.1", Active = true });
        hostStore.Upsert(new WebHost { HostId = 2, HostName = "srv-same", IpAddress = "10.1.1.2", Active = true });
        hostStore.Upsert(new WebHost { HostId = 3, HostName = "srv-dns", IpAddress = "10.1.1.3", Active = true });
        SeedDevices(
            new PrtgDeviceRow { Objid = 1, Name = "W(srv-old)_10.1.1.1", Ip = "10.1.1.1" },
            new PrtgDeviceRow { Objid = 2, Name = "W(SRV-SAME)_10.1.1.2", Ip = "10.1.1.2" },
            new PrtgDeviceRow { Objid = 3, Name = "Core Switch", Ip = "10.1.1.3" });

        var store = CreateStore();
        var result = CreateMapper(store, hostStore, new TestConsole()).MapForDate(new DateTime(2026, 8, 30));
        var rows = store.GetHostMapForDate(new DateTime(2026, 8, 30)).ToDictionary(r => r.DeviceObjid);

        Assert.Equal(3, result.Ok);
        Assert.Equal(1, result.NameMismatch);
        Assert.Equal(PrtgMapStatus.Ok, rows[1].MapStatus);
        Assert.Equal(1, rows[1].HostId);
        Assert.StartsWith(PrtgHostMapper.NoteNameMismatchPrefix, rows[1].Note);
        Assert.Contains("srv-old", rows[1].Note);
        // 大小寫不同但同名：不提醒；不帶括號的顯示名：不比對
        Assert.Null(rows[2].Note);
        Assert.Null(rows[3].Note);
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
        var mapper = CreateMapper(store, hostStore, console);
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
    public void MapForDate_device位址是簡寫_不得被當成IP對上主機()
    {
        // 10.1 若被 IPAddress.TryParse 當成 10.0.0.1，會錯對應到這台主機
        var hostStore = new FakeHostStore();
        hostStore.Upsert(new WebHost { HostId = 1, HostName = "srv-a", IpAddress = "10.0.0.1", Active = true });

        SeedDevices(new PrtgDeviceRow { Objid = 4101, Name = "short", Ip = "10.1" });

        var store = CreateStore();
        var mapper = CreateMapper(store, hostStore, new TestConsole());
        var mapDate = new DateTime(2026, 8, 30);

        var result = mapper.MapForDate(mapDate);

        Assert.Equal(0, result.Ok);
        Assert.Equal(0, result.Conflict);
        Assert.DoesNotContain(store.GetHostMapForDate(mapDate), r => r.HostId == 1);
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
        var mapper = CreateMapper(store, hostStore, console);
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
        var mapper = CreateMapper(store, hostStore, console);
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
        var mapper = CreateMapper(store, hostStore, console);
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
        var mapper = CreateMapper(store, hostStore, console);
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
        var mapper = CreateMapper(store, hostStore, console);
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
        var mapper = CreateMapper(store, hostStore, console);
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

    [Fact]
    public void MapForDate_人工對應優先_即使IP命中其他主機仍以人工指定為主_雙面斷言()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 10, HostName = "srv-app01", IpAddress = "192.168.1.10", Active = true });
            hosts.Add(new WebHost { HostId = 20, HostName = "srv-manual-target", IpAddress = "10.0.0.20", Active = true });
        });

        SeedDevices(new PrtgDeviceRow { Objid = 1001, Name = "PRTG-Dev1", Ip = "192.168.1.10" });

        var store = CreateStore();
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 20,
            Note = "特別指定",
            CreatedBy = "admin",
            CreatedAt = DateTime.Now
        });

        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(1, result.Ok);
        Assert.Equal(1, result.Manual);
        Assert.Equal(0, result.Conflict);
        Assert.Equal(0, result.Unmatched);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(1001, row.DeviceObjid);

        // 雙面斷言：斷言 HostId 是人工指定的那台 (20)，不是 IP 命中的那台 (10)
        Assert.Equal(20, row.HostId);
        Assert.NotEqual(10, row.HostId);

        // 雙面斷言：斷言 HostName 是人工指定的主機名，不是 IP 命中的主機名
        Assert.Equal("srv-manual-target", row.HostName);
        Assert.NotEqual("srv-app01", row.HostName);

        Assert.Equal(PrtgMapStatus.Ok, row.MapStatus);
        Assert.Equal("人工指定對應：特別指定", row.Note);
    }

    [Fact]
    public void MapForDate_人工對應不影響同IP其他device_不會誤判成Conflict_雙面斷言()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 10, HostName = "srv-auto", IpAddress = "192.168.1.50", Active = true });
            hosts.Add(new WebHost { HostId = 20, HostName = "srv-manual", IpAddress = "10.0.0.20", Active = true });
        });

        SeedDevices(
            new PrtgDeviceRow { Objid = 1001, Name = "PRTG-Manual", Ip = "192.168.1.50" },
            new PrtgDeviceRow { Objid = 1002, Name = "PRTG-Auto", Ip = "192.168.1.50" });

        var store = CreateStore();
        // 1001 設定人工對應到 20，1002 無人工對應
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 20,
            Note = "人工指派",
            CreatedBy = "admin",
            CreatedAt = DateTime.Now
        });

        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(1, result.Ok);
        Assert.Equal(1, result.Manual);
        Assert.Equal(0, result.Conflict);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(1, result.SkippedManualSibling);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);

        var row1001 = rows.Single(r => r.DeviceObjid == 1001);
        Assert.Equal(20, row1001.HostId);
        Assert.NotEqual(10, row1001.HostId);
        Assert.Equal(PrtgMapStatus.Ok, row1001.MapStatus);

        // 規則二：同 IP 的 1002 不進分組，不產生對應列，避免自動變 Ok 繞過人工指定
        Assert.DoesNotContain(rows, r => r.DeviceObjid == 1002);
    }

    [Fact]
    public void MapForDate_人工對應主機不存在或已停用_回退自動判定且輸出警告()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 10, HostName = "srv-active", IpAddress = "192.168.1.10", Active = true });
            hosts.Add(new WebHost { HostId = 30, HostName = "srv-disabled", IpAddress = "192.168.1.30", Active = false });
        });

        // 裝置 IP 查無主機
        SeedDevices(new PrtgDeviceRow { Objid = 1001, Name = "PRTG-Dev1", Ip = "10.99.99.99" });

        var store = CreateStore();
        // 人工對應到已停用的主機 30
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 30,
            Note = "指派給停用主機",
            CreatedBy = "admin",
            CreatedAt = DateTime.Now
        });

        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(0, result.Ok); // 不是 Ok
        Assert.Equal(0, result.Manual);
        Assert.Equal(1, result.Unmatched); // 回退到自動判定（查無 IP -> Unmatched）

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        Assert.Equal(1001, rows[0].DeviceObjid);
        Assert.Equal(PrtgMapStatus.Unmatched, rows[0].MapStatus);
        Assert.Null(rows[0].HostId);

        // 警告行斷言
        Assert.Contains(console.Lines, l => l.Contains("警告") && l.Contains("1001"));
    }

    [Fact]
    public void MapForDate_刪除人工對應後重跑_恢復自動判定()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 10, HostName = "srv-app01", IpAddress = "192.168.1.10", Active = true });
            hosts.Add(new WebHost { HostId = 20, HostName = "srv-manual", IpAddress = "10.0.0.20", Active = true });
        });

        SeedDevices(new PrtgDeviceRow { Objid = 1001, Name = "PRTG-Dev1", Ip = "192.168.1.10" });

        var store = CreateStore();
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 20,
            CreatedBy = "admin",
            CreatedAt = DateTime.Now
        });

        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // 第一次執行：人工對應生效
        var result1 = mapper.MapForDate(mapDate);
        Assert.Equal(1, result1.Ok);
        Assert.Equal(1, result1.Manual);
        var rows1 = store.GetHostMapForDate(mapDate);
        Assert.Equal(20, rows1[0].HostId);

        // 刪除人工對應
        store.DeleteManualMap(1001);

        // 第二次執行：恢復自動判定（IP 192.168.1.10 對應到 Host 10）
        var result2 = mapper.MapForDate(mapDate);
        Assert.Equal(1, result2.Ok);
        Assert.Equal(0, result2.Manual);
        var rows2 = store.GetHostMapForDate(mapDate);
        Assert.Equal(10, rows2[0].HostId);
        Assert.Equal("srv-app01", rows2[0].HostName);
    }

    [Fact]
    public void MapForDate_排除生效_排除清單內的IP不產生對應列且SkippedExcluded累計()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 10, HostName = "srv-app01", IpAddress = "192.168.1.10", Active = true });
            hosts.Add(new WebHost { HostId = 20, HostName = "srv-db01", IpAddress = "192.168.1.20", Active = true });
        });

        SeedDevices(
            new PrtgDeviceRow { Objid = 1001, Name = "PRTG-App01", Ip = "192.168.1.10" },
            new PrtgDeviceRow { Objid = 1002, Name = "PRTG-Db01", Ip = "192.168.1.20" }
        );

        var store = CreateStore();
        store.UpsertIpExclude(new PrtgIpExcludeRow
        {
            Ip = "192.168.1.20",
            Note = "不納入 PRTG 監控",
            CreatedBy = "admin",
            CreatedAt = DateTime.Now
        });

        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(1, result.Ok);
        Assert.Equal(1, result.SkippedExcluded);
        Assert.Equal(0, result.SkippedManualSibling);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        Assert.Equal(1001, rows[0].DeviceObjid);
        Assert.Equal(10, rows[0].HostId);
        Assert.Equal(PrtgMapStatus.Ok, rows[0].MapStatus);
        Assert.DoesNotContain(rows, r => r.DeviceObjid == 1002);
    }

    [Fact]
    public void MapForDate_排除清單比對不分大小寫且忽略前後空白()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 10, HostName = "srv-app01", IpAddress = "10.0.0.5", Active = true });
        });

        SeedDevices(
            new PrtgDeviceRow { Objid = 1001, Name = "PRTG-App01", Ip = " 10.0.0.5 " }
        );

        var store = CreateStore();
        store.UpsertIpExclude(new PrtgIpExcludeRow
        {
            Ip = "10.0.0.5",
            Note = "測試排除",
            CreatedBy = "admin",
            CreatedAt = DateTime.Now
        });

        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(0, result.Ok);
        Assert.Equal(1, result.SkippedExcluded);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Empty(rows);
    }

    [Fact]
    public void MapForDate_人工對應優先於排除清單()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 10, HostName = "srv-app01", IpAddress = "10.0.0.5", Active = true });
        });

        SeedDevices(
            new PrtgDeviceRow { Objid = 1001, Name = "PRTG-App01", Ip = "10.0.0.5" }
        );

        var store = CreateStore();
        store.UpsertIpExclude(new PrtgIpExcludeRow
        {
            Ip = "10.0.0.5",
            Note = "已排除的 IP",
            CreatedBy = "admin",
            CreatedAt = DateTime.Now
        });
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 10,
            Note = "強制指定",
            CreatedBy = "admin",
            CreatedAt = DateTime.Now
        });

        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(1, result.Ok);
        Assert.Equal(1, result.Manual);
        Assert.Equal(0, result.SkippedExcluded);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        Assert.Equal(1001, rows[0].DeviceObjid);
        Assert.Equal(10, rows[0].HostId);
        Assert.Equal("srv-app01", rows[0].HostName);
        Assert.Equal(PrtgMapStatus.Ok, rows[0].MapStatus);
        Assert.Contains("人工指定對應：強制指定（此 IP 在排除清單中，人工指定優先）", rows[0].Note);
    }

    [Fact]
    public void MapForDate_同IP人工指定後其餘Device不再變Ok()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 10, HostName = "srv-app01", IpAddress = "192.168.1.10", Active = true });
        });

        // 同一個 IP 有兩台 PRTG device
        SeedDevices(
            new PrtgDeviceRow { Objid = 1001, Name = "PRTG-App01-Primary", Ip = "192.168.1.10" },
            new PrtgDeviceRow { Objid = 1002, Name = "PRTG-App01-Secondary", Ip = "192.168.1.10" }
        );

        var store = CreateStore();
        // 管理者對 1001 設定人工對應
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 10,
            CreatedBy = "admin",
            CreatedAt = DateTime.Now
        });

        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(1, result.Ok);
        Assert.Equal(1, result.Manual);
        Assert.Equal(0, result.Conflict);
        Assert.Equal(1, result.SkippedManualSibling);

        var rows = store.GetHostMapForDate(mapDate);
        Assert.Single(rows);
        Assert.Equal(1001, rows[0].DeviceObjid);
        Assert.Equal(10, rows[0].HostId);
        Assert.Equal(PrtgMapStatus.Ok, rows[0].MapStatus);

        // 特別斷言結果中不存在 MapStatus == Ok 且 device 為另一台（1002）的列，且 1002 根本不產生列
        Assert.DoesNotContain(rows, r => r.DeviceObjid == 1002);
        Assert.DoesNotContain(rows, r => r.MapStatus == PrtgMapStatus.Ok && r.DeviceObjid == 1002);
    }

    [Fact]
    public void MapForDate_排除清單為空時SkippedExcluded與SkippedManualSibling皆為零()
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
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(1, result.Ok);
        Assert.Equal(0, result.SkippedExcluded);
        Assert.Equal(0, result.SkippedManualSibling);
    }

    [Fact]
    public void MapForDate_device的Ip帶port時仍能對到主機()
    {
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 41, HostName = "srv-port", IpAddress = "10.1.2.3", Active = true });
        });

        SeedDevices(new PrtgDeviceRow { Objid = 1401, Name = "PRTG-Port", Ip = "10.1.2.3:8080" });

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        var result = mapper.MapForDate(mapDate);

        Assert.Equal(1, result.Ok);
        var row = Assert.Single(store.GetHostMapForDate(mapDate));
        Assert.Equal(PrtgMapStatus.Ok, row.MapStatus);
        Assert.Equal(41, row.HostId);
    }

    [Fact]
    public void MapForDate_device的Ip為DNS名稱且能解析時對到主機()
    {
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 42, HostName = "srv-dns", IpAddress = "10.4.4.4", Active = true });
        });

        SeedDevices(new PrtgDeviceRow { Objid = 1402, Name = "PRTG-Dns", Ip = "srv-dns.example.local" });

        var store = CreateStore();
        var console = new TestConsole();
        var resolver = new FakeResolver(new Dictionary<string, string?>
        {
            ["srv-dns.example.local"] = "10.4.4.4"
        });
        var mapper = CreateMapper(store, hostStore, console, resolver);
        var mapDate = new DateTime(2026, 8, 30);

        var result = mapper.MapForDate(mapDate);

        Assert.Equal(1, result.Ok);
        var row = Assert.Single(store.GetHostMapForDate(mapDate));
        Assert.Equal(PrtgMapStatus.Ok, row.MapStatus);
        Assert.Equal(42, row.HostId);
    }

    [Fact]
    public void MapForDate_device的Ip為DNS名稱但解析不到時計入略過而非unmatched()
    {
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 43, HostName = "srv-other", IpAddress = "10.5.5.5", Active = true });
        });

        SeedDevices(new PrtgDeviceRow { Objid = 1403, Name = "PRTG-NoDns", Ip = "unknown.example.local" });

        var store = CreateStore();
        var console = new TestConsole();
        var resolver = new FakeResolver(new Dictionary<string, string?>());
        var mapper = CreateMapper(store, hostStore, console, resolver);
        var mapDate = new DateTime(2026, 8, 30);

        var result = mapper.MapForDate(mapDate);

        Assert.Equal(1, result.SkippedNoIp);
        Assert.Equal(0, result.Unmatched);
        Assert.Empty(store.GetHostMapForDate(mapDate));
    }

    /// <summary>
    /// 記錄「哪些值真的被拿去查 DNS」的假解析器：正式實作的候選判定在解析器內，
    /// 這裡用正式實作包一層假 DNS，證明佔位值連假 DNS 都沒被呼叫。
    /// </summary>
    [Fact]
    public void MapForDate_device的Ip為打壞的IPv4佔位值_計入略過且不送DNS()
    {
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 44, HostName = "srv-x", IpAddress = "10.6.6.6", Active = true });
        });

        SeedDevices(new PrtgDeviceRow { Objid = 1404, Name = "Placeholder", Ip = "10.2xx.x.x" });

        var store = CreateStore();
        var console = new TestConsole();
        var dnsCalls = new List<string>();
        var resolver = new PrtgAddressResolver(host => { dnsCalls.Add(host); return Array.Empty<System.Net.IPAddress>(); });
        var mapper = CreateMapper(store, hostStore, console, resolver);
        var mapDate = new DateTime(2026, 8, 30);

        var result = mapper.MapForDate(mapDate);

        Assert.Equal(1, result.SkippedNoIp);
        Assert.Empty(dnsCalls);
    }

    [Fact]
    public void MapForDate_固定夾具八種情境逐台驗證與hostname統計()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 1, HostName = "srv-ip01", IpAddress = "10.0.1.1", Active = true });
            hosts.Add(new WebHost { HostId = 2, HostName = "srv-ip02", IpAddress = "10.0.2.1", Active = true });
            hosts.Add(new WebHost { HostId = 3, HostName = "srv-name01", IpAddress = "10.0.3.1", Active = true });
            hosts.Add(new WebHost { HostId = 4, HostName = "srv-prefix", IpAddress = "10.0.4.1", Active = true });
            hosts.Add(new WebHost { HostId = 51, HostName = "srv-multi", IpAddress = "10.0.5.1", Active = true });
            hosts.Add(new WebHost { HostId = 52, HostName = "srv-multi.corp.local", IpAddress = "10.0.5.2", Active = true });
            hosts.Add(new WebHost { HostId = 6, HostName = "srv-ip-win", IpAddress = "10.0.6.1", Active = true });
            hosts.Add(new WebHost { HostId = 60, HostName = "srv-name-lose", IpAddress = "10.0.6.2", Active = true });
            hosts.Add(new WebHost { HostId = 7, HostName = "SRV-X.corp.local", IpAddress = "10.0.7.1", Active = true });
            hosts.Add(new WebHost { HostId = 8, HostName = "vm-idc-eosm02", IpAddress = "10.0.8.1", Active = true });
        });

        SeedDevices(
            // 1. IP 命中 → Ok（Note null）
            new PrtgDeviceRow { Objid = 101, Name = "Dev-IP-Hit", Ip = "10.0.1.1" },
            // 2. 同 IP 兩台裝置 → 以 IP 為主兩台都對到主機 2
            new PrtgDeviceRow { Objid = 201, Name = "Dev-Conflict-A", Ip = "10.0.2.1" },
            new PrtgDeviceRow { Objid = 202, Name = "Dev-Conflict-B", Ip = "10.0.2.1" },
            // 3. 無 IP＋括號名稱命中 → Ok、Note「hostname」
            new PrtgDeviceRow { Objid = 301, Name = "U(srv-name01)216.45.101", Ip = null },
            // 4. 無 IP＋去單字母前綴後命中 → Ok
            new PrtgDeviceRow { Objid = 401, Name = "U(O-srv-prefix)216.45.101", Ip = null },
            // 5. 無 IP＋名稱命中多台主機 → Conflict hostname 多主機
            new PrtgDeviceRow { Objid = 501, Name = "U(srv-multi)216.45.101", Ip = null },
            // 6. 有 IP 且 IP 命中主機 A、名稱指向主機 B → 仍是 A（IP 優先）
            new PrtgDeviceRow { Objid = 601, Name = "U(srv-name-lose)216.45.101", Ip = "10.0.6.1" },
            // 7. 名稱大小寫不同且帶域名（主機主檔 HostName＝SRV-X.corp.local、裝置名稱 U(srv-x)1.2.3）→ 命中
            new PrtgDeviceRow { Objid = 701, Name = "U(srv-x)1.2.3", Ip = null },
            // 8. VM- 開頭不去前綴（主機叫 vm-idc-eosm02）→ 命中
            new PrtgDeviceRow { Objid = 801, Name = "W(VM-IDC-EOSM02)_216.11.10", Ip = null }
        );

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        var rows = store.GetHostMapForDate(mapDate).ToDictionary(r => r.DeviceObjid);

        // 1. IP 命中 → Ok（Note null）
        Assert.Equal(PrtgMapStatus.Ok, rows[101].MapStatus);
        Assert.Equal(1, rows[101].HostId);
        Assert.Null(rows[101].Note);

        // 2. 同 IP 兩台裝置 → 以 IP 為主兩台都對到主機 2
        Assert.Equal(PrtgMapStatus.Ok, rows[201].MapStatus);
        Assert.Equal(2, rows[201].HostId);
        Assert.Equal(PrtgMapStatus.Ok, rows[202].MapStatus);
        Assert.Equal(2, rows[202].HostId);

        // 3. 無 IP＋括號名稱命中 → Ok、Note「hostname」
        Assert.Equal(PrtgMapStatus.Ok, rows[301].MapStatus);
        Assert.Equal(3, rows[301].HostId);
        Assert.Equal("hostname", rows[301].Note);

        // 4. 無 IP＋去單字母前綴後命中 → Ok
        Assert.Equal(PrtgMapStatus.Ok, rows[401].MapStatus);
        Assert.Equal(4, rows[401].HostId);
        Assert.Equal("hostname", rows[401].Note);

        // 5. 無 IP＋名稱命中多台主機 → Conflict hostname 多主機
        Assert.Equal(PrtgMapStatus.Conflict, rows[501].MapStatus);
        Assert.Null(rows[501].HostId);
        Assert.NotNull(rows[501].Note);
        Assert.StartsWith(PrtgHostMapper.NoteHostnameMultiHostsPrefix, rows[501].Note);

        // 6. 有 IP 且 IP 命中主機 A、名稱指向主機 B → 仍是 A（IP 優先），並提醒名稱不一致
        Assert.Equal(PrtgMapStatus.Ok, rows[601].MapStatus);
        Assert.Equal(6, rows[601].HostId);
        Assert.StartsWith(PrtgHostMapper.NoteNameMismatchPrefix, rows[601].Note);

        // 7. 名稱大小寫不同且帶域名（主機主檔 HostName＝SRV-X.corp.local、裝置名稱 U(srv-x)1.2.3）→ 命中
        Assert.Equal(PrtgMapStatus.Ok, rows[701].MapStatus);
        Assert.Equal(7, rows[701].HostId);
        Assert.Equal("hostname", rows[701].Note);

        // 8. VM- 開頭不去前綴（主機叫 vm-idc-eosm02）→ 命中
        Assert.Equal(PrtgMapStatus.Ok, rows[801].MapStatus);
        Assert.Equal(8, rows[801].HostId);
        Assert.Equal("hostname", rows[801].Note);

        // 另斷言 console 摘要行含名稱退路 4
        Assert.Equal(4, result.Hostname);
        Assert.Contains(console.Lines, l => l.Contains("名稱退路 4"));
    }

    [Fact]
    public void MapForDate_主機已被IP命中且另一無IP裝置指向該主機_該裝置落Unmatched不列衝突()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 10, HostName = "srv-alpha", IpAddress = "10.10.10.10", Active = true });
        });

        SeedDevices(
            new PrtgDeviceRow { Objid = 1, Name = "Dev-IP", Ip = "10.10.10.10" },
            new PrtgDeviceRow { Objid = 2, Name = "U(srv-alpha)", Ip = null }
        );

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        var rows = store.GetHostMapForDate(mapDate).ToDictionary(r => r.DeviceObjid);

        Assert.Equal(PrtgMapStatus.Ok, rows[1].MapStatus);
        Assert.Equal(10, rows[1].HostId);

        // 名稱退路只補 IP 沒對到的主機：srv-alpha 已由 IP 對上，同名 device 不屬於它
        Assert.Equal(PrtgMapStatus.Unmatched, rows[2].MapStatus);
        Assert.Null(rows[2].HostId);
        Assert.Equal(0, result.Conflict);
    }

    [Fact]
    public void MapForDate_兩台無IP裝置名稱命中同一台未對應主機_兩台都Ok()
    {
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts => hosts.Add(new WebHost { HostId = 10, HostName = "srv-gamma", IpAddress = "10.99.0.1", Active = true }));
        SeedDevices(
            new PrtgDeviceRow { Objid = 1, Name = "U(srv-gamma)ping", Ip = null },
            new PrtgDeviceRow { Objid = 2, Name = "W(srv-gamma)wmi", Ip = "10.50.0.9" });

        var store = CreateStore();
        var result = CreateMapper(store, hostStore, new TestConsole()).MapForDate(new DateTime(2026, 8, 30));
        var rows = store.GetHostMapForDate(new DateTime(2026, 8, 30)).ToDictionary(r => r.DeviceObjid);

        Assert.Equal(0, result.Conflict);
        Assert.Equal(2, result.Hostname);
        Assert.All(rows.Values, r =>
        {
            Assert.Equal(PrtgMapStatus.Ok, r.MapStatus);
            Assert.Equal(10, r.HostId);
            Assert.Equal(PrtgHostMapper.NoteHostnameOk, r.Note);
        });
    }

    [Fact]
    public void MapForDate_無任何hostname命中時摘要含名稱退路0且計數維持原有()
    {
        // Arrange
        var hostStore = new FakeHostStore();
        hostStore.MutateBatch(hosts =>
        {
            hosts.Add(new WebHost { HostId = 1, HostName = "srv-beta", IpAddress = "10.20.20.20", Active = true });
        });

        SeedDevices(
            new PrtgDeviceRow { Objid = 1, Name = "Dev-IP", Ip = "10.20.20.20" },
            new PrtgDeviceRow { Objid = 2, Name = "U(srv-unknown)", Ip = "10.30.30.30" },
            new PrtgDeviceRow { Objid = 3, Name = "U(srv-unknown2)", Ip = null }
        );

        var store = CreateStore();
        var console = new TestConsole();
        var mapper = CreateMapper(store, hostStore, console);
        var mapDate = new DateTime(2026, 8, 30);

        // Act
        var result = mapper.MapForDate(mapDate);

        // Assert
        Assert.Equal(1, result.Ok);
        Assert.Equal(0, result.Conflict);
        Assert.Equal(1, result.Unmatched);
        Assert.Equal(1, result.SkippedNoIp);
        Assert.Equal(0, result.Hostname);

        Assert.Contains(console.Lines, l => l.Contains("名稱退路 0"));
        // 主控台不逐筆列出主機清單外的 device（docs/PRTG-SPEC.md §1）
        Assert.DoesNotContain(console.Lines, l => l.Contains("Device 2") || l.Contains("10.30.30.30"));
    }
}
