using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="SchemaUpgrader"/> 的 PRTG 鏡像層五張表建表與升級測試。
/// 既有部署的 DB 已經存在，EnsureCreated 對它什麼都不做，必須靠 SchemaUpgrader 的冪等 DDL 補齊。
/// </summary>
public class SchemaUpgraderPrtgTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 建表_缺席時建立五張表並可正常讀寫()
    {
        // 模擬既有部署：DB 已存在，刻意砍掉五張 PRTG 表，還原成「這五張表問世前」的既有 DB 現況
        using (var ctx = _fx.NewContext())
        {
            ctx.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS lf_prtg_devices");
            ctx.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS lf_prtg_sensors");
            ctx.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS lf_prtg_state_changes");
            ctx.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS lf_prtg_values");
            ctx.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS lf_prtg_host_map");
        }

        // 執行 SchemaUpgrader.Upgrade 補上表與索引
        using (var ctx = _fx.NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        var now = new DateTime(2026, 8, 31, 10, 0, 0);

        // 對每張表各寫入一列並讀回斷言
        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgDevices.Add(new PrtgDeviceRow
            {
                Objid = 1001,
                Name = "Core Switch",
                GroupPath = "Root/Network/Core",
                Ip = "192.168.1.1",
                Tags = "switch core",
                Status = "Up",
                DependencyObjid = null,
                Paused = false,
                SyncedAt = now,
                CreatedAt = now
            });

            ctx.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 2001,
                DeviceObjid = 1001,
                Name = "Ping",
                SensorType = "ping",
                Tags = "icmp",
                Unit = "ms",
                Status = "Up",
                ThresholdsJson = "{\"warn\": 50}",
                DependencyObjid = null,
                Paused = false,
                Category = null,
                CategorySource = null,
                SyncedAt = now,
                CreatedAt = now
            });

            ctx.PrtgStateChanges.Add(new PrtgStateChangeRow
            {
                SensorObjid = 2001,
                ChangedAt = now,
                Status = "Down",
                PrevStatus = "Up",
                Message = "Request timed out",
                Quality = PrtgDataQuality.Ok,
                CreatedAt = now
            });

            ctx.PrtgValues.Add(new PrtgValueRow
            {
                SensorObjid = 2001,
                PeriodStart = new DateTime(2026, 8, 31, 10, 0, 0),
                AvgValue = 12.5,
                MinValue = 5.0,
                MaxValue = 25.0,
                Coverage = 100.0,
                Quality = PrtgDataQuality.Ok,
                CreatedAt = now
            });

            ctx.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                MapDate = new DateTime(2026, 8, 31),
                DeviceObjid = 1001,
                Ip = "192.168.1.1",
                HostId = 1,
                HostName = "SW-CORE-01",
                MapStatus = PrtgMapStatus.Ok,
                Note = "自動對應",
                CreatedAt = now
            });

            ctx.SaveChanges();
        }

        using (var ctx = _fx.NewContext())
        {
            var dev = ctx.PrtgDevices.Find(1001L);
            Assert.NotNull(dev);
            Assert.Equal("Core Switch", dev.Name);
            Assert.Equal("192.168.1.1", dev.Ip);

            var sensor = ctx.PrtgSensors.Find(2001L);
            Assert.NotNull(sensor);
            Assert.Equal("Ping", sensor.Name);
            Assert.Equal("ping", sensor.SensorType);
            Assert.Null(sensor.Category);
            Assert.Null(sensor.CategorySource);

            var sc = ctx.PrtgStateChanges.FirstOrDefault(x => x.SensorObjid == 2001);
            Assert.NotNull(sc);
            Assert.Equal("Down", sc.Status);
            Assert.Equal(PrtgDataQuality.Ok, sc.Quality);

            var val = ctx.PrtgValues.FirstOrDefault(x => x.SensorObjid == 2001);
            Assert.NotNull(val);
            Assert.Equal(12.5, val.AvgValue);
            Assert.Equal(PrtgDataQuality.Ok, val.Quality);

            var map = ctx.PrtgHostMaps.Find(new DateTime(2026, 8, 31), 1001L);
            Assert.NotNull(map);
            Assert.Equal("SW-CORE-01", map.HostName);
            Assert.Equal(PrtgMapStatus.Ok, map.MapStatus);
        }
    }

    [Fact]
    public void 冪等_連續跑兩次Upgrade不擲例外()
    {
        using var ctx = _fx.NewContext();

        var ex1 = Record.Exception(() => SchemaUpgrader.Upgrade(ctx));
        var ex2 = Record.Exception(() => SchemaUpgrader.Upgrade(ctx));

        Assert.Null(ex1);
        Assert.Null(ex2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 欄位清單斷言_規格即測試(bool viaSchemaUpgraderDdl)
    {
        // viaSchemaUpgraderDdl=false：驗 EnsureCreated（EF 模型）建出來的表；
        // true：先砍表再讓 SchemaUpgrader 的手寫 DDL 重建。兩條路徑都要滿足同一份欄位清單——
        // 兩者漂移時 CREATE TABLE IF NOT EXISTS 會讓差異完全靜默，只有新部署或只有舊部署會壞。
        if (viaSchemaUpgraderDdl)
        {
            using var drop = _fx.NewContext();
            foreach (var t in PrtgTableNames)
            {
                // 表名來自本檔硬編的白名單常數，非外部輸入
                drop.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS " + t);
            }
        }

        using var ctx = _fx.NewContext();

        // 確保升級已跑過
        SchemaUpgrader.Upgrade(ctx);

        // 表一：lf_prtg_devices
        var devColumns = GetColumnNames(ctx, "lf_prtg_devices");
        var expectedDev = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "objid", "name", "group_path", "ip", "tags", "status", "dependency_objid", "paused", "synced_at", "created_at"
        };
        Assert.True(expectedDev.SetEquals(devColumns), $"lf_prtg_devices 欄位不符。實際: {string.Join(", ", devColumns)}");

        // 表二：lf_prtg_sensors
        var sensorColumns = GetColumnNames(ctx, "lf_prtg_sensors");
        var expectedSensor = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "objid", "device_objid", "name", "sensor_type", "tags", "unit", "status", "thresholds_json", "dependency_objid", "paused", "category", "category_source", "synced_at", "created_at"
        };
        Assert.True(expectedSensor.SetEquals(sensorColumns), $"lf_prtg_sensors 欄位不符。實際: {string.Join(", ", sensorColumns)}");

        // 表三：lf_prtg_state_changes
        var scColumns = GetColumnNames(ctx, "lf_prtg_state_changes");
        var expectedSc = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "sensor_objid", "changed_at", "status", "prev_status", "message", "quality", "created_at"
        };
        Assert.True(expectedSc.SetEquals(scColumns), $"lf_prtg_state_changes 欄位不符。實際: {string.Join(", ", scColumns)}");

        // 表四：lf_prtg_values
        var valColumns = GetColumnNames(ctx, "lf_prtg_values");
        var expectedVal = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "sensor_objid", "period_start", "avg_value", "min_value", "max_value", "coverage", "quality", "created_at"
        };
        Assert.True(expectedVal.SetEquals(valColumns), $"lf_prtg_values 欄位不符。實際: {string.Join(", ", valColumns)}");

        // 表五：lf_prtg_host_map
        var mapColumns = GetColumnNames(ctx, "lf_prtg_host_map");
        var expectedMap = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "map_date", "device_objid", "ip", "host_id", "host_name", "map_status", "note", "created_at"
        };
        Assert.True(expectedMap.SetEquals(mapColumns), $"lf_prtg_host_map 欄位不符。實際: {string.Join(", ", mapColumns)}");

        // 表六：lf_prtg_manual_map
        var manualColumns = GetColumnNames(ctx, "lf_prtg_manual_map");
        var expectedManual = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "device_objid", "host_id", "created_by", "note", "created_at"
        };
        Assert.True(expectedManual.SetEquals(manualColumns), $"lf_prtg_manual_map 欄位不符。實際: {string.Join(", ", manualColumns)}");
    }

    [Fact]
    public void 建表_人工對應表在EF與SchemaUpgrader兩軌下皆可讀寫()
    {
        var now = new DateTime(2026, 8, 31, 10, 0, 0);

        // 軌道一：EF EnsureCreated 直接建表
        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgManualMaps.Add(new PrtgManualMapRow
            {
                DeviceObjid = 1001,
                HostId = 10,
                CreatedBy = "admin",
                Note = "EF 建立",
                CreatedAt = now
            });
            ctx.SaveChanges();
        }

        using (var ctx = _fx.NewContext())
        {
            var row = ctx.PrtgManualMaps.Find(1001L);
            Assert.NotNull(row);
            Assert.Equal(10, row.HostId);
            Assert.Equal("admin", row.CreatedBy);
            Assert.Equal("EF 建立", row.Note);
            Assert.Equal(now, row.CreatedAt);
        }

        // 軌道二：模擬既有部署砍表後以 SchemaUpgrader 重建
        using (var ctx = _fx.NewContext())
        {
            ctx.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS lf_prtg_manual_map");
        }

        using (var ctx = _fx.NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgManualMaps.Add(new PrtgManualMapRow
            {
                DeviceObjid = 2002,
                HostId = 20,
                CreatedBy = "user1",
                Note = "SchemaUpgrader 建立",
                CreatedAt = now
            });
            ctx.SaveChanges();
        }

        using (var ctx = _fx.NewContext())
        {
            var row = ctx.PrtgManualMaps.Find(2002L);
            Assert.NotNull(row);
            Assert.Equal(20, row.HostId);
            Assert.Equal("user1", row.CreatedBy);
            Assert.Equal("SchemaUpgrader 建立", row.Note);
        }
    }

    [Fact]
    public void 唯一鍵_lf_prtg_values重複寫入時擲出例外()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS lf_prtg_values");
        }

        using (var ctx = _fx.NewContext())
        {
            SchemaUpgrader.Upgrade(ctx);
        }

        var periodStart = new DateTime(2026, 8, 31, 10, 0, 0);
        var now = new DateTime(2026, 8, 31, 10, 0, 0);

        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgValues.Add(new PrtgValueRow
            {
                SensorObjid = 3001,
                PeriodStart = periodStart,
                AvgValue = 10.0,
                Quality = PrtgDataQuality.Ok,
                CreatedAt = now
            });
            ctx.SaveChanges();
        }

        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgValues.Add(new PrtgValueRow
            {
                SensorObjid = 3001,
                PeriodStart = periodStart,
                AvgValue = 20.0,
                Quality = PrtgDataQuality.Ok,
                CreatedAt = now
            });

            Assert.ThrowsAny<DbUpdateException>(() => ctx.SaveChanges());
        }
    }

    [Fact]
    public void 表名與索引名_實際建出來的識別字皆合乎命名規範()
    {
        // 斷言對象是資料庫裡真的存在的識別字，不是這個測試自己硬編的字串。
        // 命名規範見 docs/DB-SPEC.md：lf_ 前綴、全小寫 snake_case、長度 ≤ 30 字元
        // （與公司共用 DB 的其他系統共存，且 Oracle 12.2 識別字上限 30 bytes）。
        using var ctx = _fx.NewContext();
        SchemaUpgrader.Upgrade(ctx);

        var tables = ctx.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'lf\\_prtg\\_%' ESCAPE '\\'")
            .ToList();

        Assert.Equal(PrtgTableNames.OrderBy(t => t), tables.OrderBy(t => t));

        var indexes = ctx.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX\\_lf\\_prtg\\_%' ESCAPE '\\'")
            .ToList();

        Assert.NotEmpty(indexes);

        foreach (var name in tables.Concat(indexes))
        {
            Assert.True(name.Length <= 30, $"識別字 '{name}' 超過 30 字元（實際長度 {name.Length}）");
        }

        foreach (var table in tables)
        {
            Assert.Equal(table.ToLowerInvariant(), table);
        }
    }

    private static readonly string[] PrtgTableNames =
    {
        "lf_prtg_devices",
        "lf_prtg_sensors",
        "lf_prtg_state_changes",
        "lf_prtg_values",
        "lf_prtg_host_map",
        "lf_prtg_manual_map"
    };

    private static HashSet<string> GetColumnNames(LfDbContext ctx, string table)
    {
        var rawColumns = ctx.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('" + table + "')").ToList();
        return new HashSet<string>(rawColumns, StringComparer.OrdinalIgnoreCase);
    }
}