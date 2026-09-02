using LogForesight.Core.Models;
using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

public class EfPrtgStoreTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfPrtgStore CreateStore() => new(_fx.NewContext);

    [Fact]
    public void UpsertDevices_新增與更新_同objid就地取代不重複()
    {
        var store = CreateStore();
        var t1 = new DateTime(2026, 8, 31, 10, 0, 0);
        var t2 = new DateTime(2026, 8, 31, 11, 0, 0);

        var firstBatch = new List<PrtgDeviceRow>
        {
            new()
            {
                Objid = 1001,
                Name = "Old Name",
                GroupPath = "Group/A",
                Ip = "192.168.1.1",
                Tags = "tag1",
                Status = "Up",
                DependencyObjid = null,
                Paused = false
            }
        };

        var count1 = store.UpsertDevices(firstBatch, t1);
        Assert.Equal(1, count1);

        using (var ctx = _fx.NewContext())
        {
            var row = ctx.PrtgDevices.Single(d => d.Objid == 1001);
            Assert.Equal("Old Name", row.Name);
            Assert.Equal(t1, row.SyncedAt);
            Assert.Equal(t1, row.CreatedAt);
        }

        // 第二次改名同 objid
        var secondBatch = new List<PrtgDeviceRow>
        {
            new()
            {
                Objid = 1001,
                Name = "New Name",
                GroupPath = "Group/B",
                Ip = "192.168.1.2",
                Tags = "tag2",
                Status = "Down",
                DependencyObjid = 999,
                Paused = true
            }
        };

        var count2 = store.UpsertDevices(secondBatch, t2);
        Assert.Equal(1, count2);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(1, ctx.PrtgDevices.Count());
            var row = ctx.PrtgDevices.Single(d => d.Objid == 1001);
            Assert.Equal("New Name", row.Name);
            Assert.Equal("Group/B", row.GroupPath);
            Assert.Equal("192.168.1.2", row.Ip);
            Assert.Equal("tag2", row.Tags);
            Assert.Equal("Down", row.Status);
            Assert.Equal(999, row.DependencyObjid);
            Assert.True(row.Paused);
            Assert.Equal(t2, row.SyncedAt);
            Assert.Equal(t1, row.CreatedAt); // CreatedAt 保持原值
        }
    }

    [Fact]
    public void UpsertDevices_更新時不覆蓋CreatedAt()
    {
        var store = CreateStore();
        var t1 = new DateTime(2026, 8, 1, 8, 0, 0);
        var t2 = new DateTime(2026, 8, 31, 12, 0, 0);

        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 2001, Name = "Dev 1", GroupPath = "G1", CreatedAt = t1 }
        }, t1);

        using (var ctx = _fx.NewContext())
        {
            var row = ctx.PrtgDevices.Single(d => d.Objid == 2001);
            Assert.Equal(t1, row.CreatedAt);
        }

        // 第二次更新，傳入不同的 CreatedAt 甚至不傳，都不可覆蓋
        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 2001, Name = "Dev 1 Updated", GroupPath = "G1", CreatedAt = t2 }
        }, t2);

        using (var ctx = _fx.NewContext())
        {
            var row = ctx.PrtgDevices.Single(d => d.Objid == 2001);
            Assert.Equal("Dev 1 Updated", row.Name);
            Assert.Equal(t2, row.SyncedAt);
            Assert.Equal(t1, row.CreatedAt);
        }
    }

    [Fact]
    public void UpsertSensors_更新時不覆蓋Category與CategorySource()
    {
        var store = CreateStore();
        var t1 = new DateTime(2026, 8, 31, 9, 0, 0);
        var t2 = new DateTime(2026, 8, 31, 15, 0, 0);

        // 先寫入一筆 sensor
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new()
            {
                Objid = 3001,
                DeviceObjid = 1001,
                Name = "Disk C:",
                SensorType = "diskspace",
                Category = null,
                CategorySource = null
            }
        }, t1);

        // 手動設定 Category="disk"、CategorySource="L4"
        using (var ctx = _fx.NewContext())
        {
            var row = ctx.PrtgSensors.Single(s => s.Objid == 3001);
            row.Category = "disk";
            row.CategorySource = "L4";
            ctx.SaveChanges();
        }

        // 再跑一次 UpsertSensors（傳入 Category=null 的資料）
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new()
            {
                Objid = 3001,
                DeviceObjid = 1001,
                Name = "Disk C: Updated",
                SensorType = "diskspace",
                Category = null,
                CategorySource = null
            }
        }, t2);

        // 斷言 Category 仍是 "disk"、CategorySource 仍是 "L4"
        using (var ctx = _fx.NewContext())
        {
            var row = ctx.PrtgSensors.Single(s => s.Objid == 3001);
            Assert.Equal("Disk C: Updated", row.Name);
            Assert.Equal("disk", row.Category);
            Assert.Equal("L4", row.CategorySource);
            Assert.Equal(t2, row.SyncedAt);
            Assert.Equal(t1, row.CreatedAt);
        }
    }

    [Fact]
    public void UpsertDevices_超過批次大小時分批寫入且全部落地()
    {
        var store = CreateStore();
        var syncedAt = new DateTime(2026, 8, 31, 10, 0, 0);
        const int totalCount = 1200;

        var devices = Enumerable.Range(1, totalCount).Select(i => new PrtgDeviceRow
        {
            Objid = i,
            Name = $"Device {i}",
            GroupPath = "Root",
            Ip = $"10.0.0.{i % 250}",
            Status = "Up"
        }).ToList();

        var written = store.UpsertDevices(devices, syncedAt);
        Assert.Equal(totalCount, written);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(totalCount, ctx.PrtgDevices.Count());
            var first = ctx.PrtgDevices.Single(d => d.Objid == 1);
            var last = ctx.PrtgDevices.Single(d => d.Objid == 1200);
            Assert.Equal("Device 1", first.Name);
            Assert.Equal("Device 1200", last.Name);
        }
    }

    [Fact]
    public void AppendStateChanges_同sensor同時間重複時不新增()
    {
        var store = CreateStore();
        var changedAt = new DateTime(2026, 8, 31, 10, 30, 0);

        var first = new List<PrtgStateChangeRow>
        {
            new()
            {
                SensorObjid = 5001,
                ChangedAt = changedAt,
                Status = "Down",
                PrevStatus = "Up",
                Message = "Connection timeout",
                Quality = "Good"
            }
        };

        var added1 = store.AppendStateChanges(first);
        Assert.Equal(1, added1);

        // 再次寫入同 sensor、同時間
        var duplicate = new List<PrtgStateChangeRow>
        {
            new()
            {
                SensorObjid = 5001,
                ChangedAt = changedAt,
                Status = "Down",
                PrevStatus = "Up",
                Message = "Duplicate entry",
                Quality = "Good"
            }
        };

        var added2 = store.AppendStateChanges(duplicate);
        Assert.Equal(0, added2);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(1, ctx.PrtgStateChanges.Count());
            var row = ctx.PrtgStateChanges.Single();
            Assert.Equal("Connection timeout", row.Message);
        }
    }

    [Fact]
    public void AppendStateChanges_同sensor不同時間都新增()
    {
        var store = CreateStore();
        var t1 = new DateTime(2026, 8, 31, 10, 0, 0);
        var t2 = new DateTime(2026, 8, 31, 11, 0, 0);

        var changes = new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 5001, ChangedAt = t1, Status = "Down", Quality = "Good" },
            new() { SensorObjid = 5001, ChangedAt = t2, Status = "Up", Quality = "Good" }
        };

        var added = store.AppendStateChanges(changes);
        Assert.Equal(2, added);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(2, ctx.PrtgStateChanges.Count(c => c.SensorObjid == 5001));
        }
    }

    [Fact]
    public void UpsertValues_同sensor同時段就地更新且CreatedAt被更新()
    {
        var store = CreateStore();
        var period = new DateTime(2026, 8, 31, 14, 0, 0);
        var t1 = new DateTime(2026, 8, 31, 14, 30, 0);
        var t2 = new DateTime(2026, 8, 31, 15, 30, 0);

        var first = new List<PrtgValueRow>
        {
            new()
            {
                SensorObjid = 6001,
                PeriodStart = period,
                AvgValue = 10.5,
                MinValue = 5.0,
                MaxValue = 20.0,
                Coverage = 0.9,
                Quality = "Good",
                CreatedAt = t1
            }
        };

        store.UpsertValues(first);

        using (var ctx = _fx.NewContext())
        {
            var row = ctx.PrtgValues.Single(v => v.SensorObjid == 6001 && v.PeriodStart == period);
            Assert.Equal(10.5, row.AvgValue);
            Assert.Equal(t1, row.CreatedAt);
        }

        // 第二次更新量測值與 CreatedAt
        var second = new List<PrtgValueRow>
        {
            new()
            {
                SensorObjid = 6001,
                PeriodStart = period,
                AvgValue = 25.0,
                MinValue = 10.0,
                MaxValue = 40.0,
                Coverage = 1.0,
                Quality = "Good",
                CreatedAt = t2
            }
        };

        store.UpsertValues(second);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(1, ctx.PrtgValues.Count());
            var row = ctx.PrtgValues.Single(v => v.SensorObjid == 6001 && v.PeriodStart == period);
            Assert.Equal(25.0, row.AvgValue);
            Assert.Equal(10.0, row.MinValue);
            Assert.Equal(40.0, row.MaxValue);
            Assert.Equal(1.0, row.Coverage);
            Assert.Equal(t2, row.CreatedAt); // CreatedAt 每次覆蓋都更新
        }
    }

    [Fact]
    public void ReplaceHostMapForDate_同日重跑就地取代不累積()
    {
        var store = CreateStore();
        var mapDate = new DateTime(2026, 8, 31);

        var firstRows = new List<PrtgHostMapRow>
        {
            new() { MapDate = mapDate, DeviceObjid = 101, HostName = "SRV-1", MapStatus = "matched" },
            new() { MapDate = mapDate, DeviceObjid = 102, HostName = "SRV-2", MapStatus = "matched" },
            new() { MapDate = mapDate, DeviceObjid = 103, HostName = "SRV-3", MapStatus = "matched" }
        };

        var count1 = store.ReplaceHostMapForDate(mapDate, firstRows);
        Assert.Equal(3, count1);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(3, ctx.PrtgHostMaps.Count(m => m.MapDate == mapDate));
        }

        // 第二次跑同日，只給 2 筆
        var secondRows = new List<PrtgHostMapRow>
        {
            new() { MapDate = mapDate, DeviceObjid = 201, HostName = "SRV-201", MapStatus = "matched" },
            new() { MapDate = mapDate, DeviceObjid = 202, HostName = "SRV-202", MapStatus = "unmatched" }
        };

        var count2 = store.ReplaceHostMapForDate(mapDate, secondRows);
        Assert.Equal(2, count2);

        using (var ctx = _fx.NewContext())
        {
            var rows = ctx.PrtgHostMaps.Where(m => m.MapDate == mapDate).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.DeviceObjid == 201);
            Assert.Contains(rows, r => r.DeviceObjid == 202);
            Assert.DoesNotContain(rows, r => r.DeviceObjid == 101);
        }
    }

    [Fact]
    public void ReplaceHostMapForDate_傳入空清單時清空該日但不影響其他日期()
    {
        var store = CreateStore();
        var day1 = new DateTime(2026, 8, 30);
        var day2 = new DateTime(2026, 8, 31);

        store.ReplaceHostMapForDate(day1, new List<PrtgHostMapRow>
        {
            new() { MapDate = day1, DeviceObjid = 101, HostName = "SRV-1", MapStatus = "matched" }
        });
        store.ReplaceHostMapForDate(day2, new List<PrtgHostMapRow>
        {
            new() { MapDate = day2, DeviceObjid = 201, HostName = "SRV-2", MapStatus = "matched" }
        });

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(2, ctx.PrtgHostMaps.Count());
        }

        // 傳入空清單取代 day2
        var count = store.ReplaceHostMapForDate(day2, Array.Empty<PrtgHostMapRow>());
        Assert.Equal(0, count);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(1, ctx.PrtgHostMaps.Count());
            Assert.True(ctx.PrtgHostMaps.Any(m => m.MapDate == day1 && m.DeviceObjid == 101));
            Assert.False(ctx.PrtgHostMaps.Any(m => m.MapDate == day2));
        }
    }

    [Fact]
    public void Prune_只刪超過保留天數的三張表且不動devices與sensors()
    {
        var store = CreateStore();
        var oldDate = DateTime.Today.AddDays(-100);
        var recentDate = DateTime.Today.AddDays(-10);

        // 寫入 devices 與 sensors（不論日期，皆不可被刪除）
        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgDevices.Add(new PrtgDeviceRow
            {
                Objid = 1,
                Name = "DevOld",
                GroupPath = "Root",
                CreatedAt = oldDate,
                SyncedAt = oldDate
            });
            ctx.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 10,
                DeviceObjid = 1,
                Name = "SensOld",
                SensorType = "ping",
                CreatedAt = oldDate,
                SyncedAt = oldDate
            });

            // values: 一筆過期、一筆未過期
            ctx.PrtgValues.Add(new PrtgValueRow
            {
                SensorObjid = 10,
                PeriodStart = oldDate,
                CreatedAt = oldDate,
                Quality = "Good"
            });
            ctx.PrtgValues.Add(new PrtgValueRow
            {
                SensorObjid = 10,
                PeriodStart = recentDate,
                CreatedAt = recentDate,
                Quality = "Good"
            });

            // state_changes: 一筆過期、一筆未過期
            ctx.PrtgStateChanges.Add(new PrtgStateChangeRow
            {
                SensorObjid = 10,
                ChangedAt = oldDate,
                CreatedAt = oldDate,
                Quality = "Good"
            });
            ctx.PrtgStateChanges.Add(new PrtgStateChangeRow
            {
                SensorObjid = 10,
                ChangedAt = recentDate,
                CreatedAt = recentDate,
                Quality = "Good"
            });

            // host_map: 一筆過期、一筆未過期
            ctx.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                MapDate = oldDate.Date,
                DeviceObjid = 1,
                CreatedAt = oldDate,
                MapStatus = "matched"
            });
            ctx.PrtgHostMaps.Add(new PrtgHostMapRow
            {
                MapDate = recentDate.Date,
                DeviceObjid = 1,
                CreatedAt = recentDate,
                MapStatus = "matched"
            });

            ctx.SaveChanges();
        }

        // 清理 retentionDays = 90
        var pruned = store.Prune(90);
        Assert.Equal(3, pruned); // 3 張表各刪 1 筆

        using (var ctx = _fx.NewContext())
        {
            // devices 與 sensors 完全不動
            Assert.Equal(1, ctx.PrtgDevices.Count());
            Assert.Equal(1, ctx.PrtgSensors.Count());

            // values / state_changes / host_map 過期被刪，未過期留著
            Assert.Equal(1, ctx.PrtgValues.Count());
            Assert.Equal(recentDate, ctx.PrtgValues.Single().CreatedAt);

            Assert.Equal(1, ctx.PrtgStateChanges.Count());
            Assert.Equal(recentDate, ctx.PrtgStateChanges.Single().CreatedAt);

            Assert.Equal(1, ctx.PrtgHostMaps.Count());
            Assert.Equal(recentDate, ctx.PrtgHostMaps.Single().CreatedAt);
        }
    }

    [Fact]
    public void Prune_分批與上限生效()
    {
        var store = CreateStore();
        var oldDate = DateTime.Today.AddDays(-100);

        using (var ctx = _fx.NewContext())
        {
            for (var i = 1; i <= 10; i++)
            {
                ctx.PrtgValues.Add(new PrtgValueRow
                {
                    SensorObjid = 100,
                    PeriodStart = oldDate.AddHours(i),
                    CreatedAt = oldDate,
                    Quality = "Good"
                });
            }
            ctx.SaveChanges();
        }

        // 10 筆過期，上限 maxRows = 4，batchSize = 2
        var pruned1 = store.Prune(retentionDays: 90, maxRows: 4, batchSize: 2);
        Assert.Equal(4, pruned1);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(6, ctx.PrtgValues.Count());
        }

        // 第二次再刪 4 筆
        var pruned2 = store.Prune(retentionDays: 90, maxRows: 4, batchSize: 2);
        Assert.Equal(4, pruned2);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(2, ctx.PrtgValues.Count());
        }

        // 第三次刪剩餘 2 筆
        var pruned3 = store.Prune(retentionDays: 90, maxRows: 4, batchSize: 2);
        Assert.Equal(2, pruned3);

        using (var ctx = _fx.NewContext())
        {
            Assert.Equal(0, ctx.PrtgValues.Count());
        }
    }

    [Fact]
    public void ApplyAutoCategories_未分類且命中對照表_填入正確分類與auto來源()
    {
        var store = CreateStore();
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 101, DeviceObjid = 1, Name = "Traffic 64", SensorType = "SNMP Traffic 64bit", Paused = false },
            new() { Objid = 102, DeviceObjid = 1, Name = "Disk Space", SensorType = "SNMP Disk Free", Paused = false },
            new() { Objid = 103, DeviceObjid = 1, Name = "CPU Load", SensorType = "SNMP CPU Load", Paused = false },
            new() { Objid = 104, DeviceObjid = 1, Name = "Mem", SensorType = "SNMP Memory", Paused = false },
            new() { Objid = 105, DeviceObjid = 1, Name = "Linux Mem", SensorType = "snmp linux meminfo", Paused = false } // 測試不分大小寫
        }, now);

        var count = store.ApplyAutoCategories();
        Assert.Equal(5, count);

        using var ctx = _fx.NewContext();
        var s101 = ctx.PrtgSensors.Single(s => s.Objid == 101);
        Assert.Equal(PrtgSensorCategories.Traffic, s101.Category);
        Assert.Equal(PrtgCategorySources.Auto, s101.CategorySource);

        var s102 = ctx.PrtgSensors.Single(s => s.Objid == 102);
        Assert.Equal(PrtgSensorCategories.Disk, s102.Category);
        Assert.Equal(PrtgCategorySources.Auto, s102.CategorySource);

        var s103 = ctx.PrtgSensors.Single(s => s.Objid == 103);
        Assert.Equal(PrtgSensorCategories.Cpu, s103.Category);
        Assert.Equal(PrtgCategorySources.Auto, s103.CategorySource);

        var s104 = ctx.PrtgSensors.Single(s => s.Objid == 104);
        Assert.Equal(PrtgSensorCategories.Memory, s104.Category);
        Assert.Equal(PrtgCategorySources.Auto, s104.CategorySource);

        var s105 = ctx.PrtgSensors.Single(s => s.Objid == 105);
        Assert.Equal(PrtgSensorCategories.Memory, s105.Category);
        Assert.Equal(PrtgCategorySources.Auto, s105.CategorySource);
    }

    [Fact]
    public void ApplyAutoCategories_不覆蓋既有分類_人工指定值完全不變()
    {
        var store = CreateStore();
        var now = DateTime.Now;

        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 201,
                DeviceObjid = 1,
                Name = "Manual Sensor",
                SensorType = "SNMP Traffic 64bit", // 在對照表中對應 traffic
                Category = "人工分類",
                CategorySource = "manual",
                Paused = false,
                SyncedAt = now,
                CreatedAt = now
            });
            ctx.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = 202,
                DeviceObjid = 1,
                Name = "Unclassified Sensor",
                SensorType = "SNMP Traffic 64bit",
                Category = null,
                CategorySource = null,
                Paused = false,
                SyncedAt = now,
                CreatedAt = now
            });
            ctx.SaveChanges();
        }

        var count = store.ApplyAutoCategories();
        Assert.Equal(1, count); // 只更新 202

        using (var ctx = _fx.NewContext())
        {
            var s201 = ctx.PrtgSensors.Single(s => s.Objid == 201);
            Assert.Equal("人工分類", s201.Category);
            Assert.Equal("manual", s201.CategorySource);

            var s202 = ctx.PrtgSensors.Single(s => s.Objid == 202);
            Assert.Equal(PrtgSensorCategories.Traffic, s202.Category);
            Assert.Equal(PrtgCategorySources.Auto, s202.CategorySource);
        }
    }

    [Fact]
    public void ApplyAutoCategories_type不在對照表者不填維持null_回傳筆數正確()
    {
        var store = CreateStore();
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 301, DeviceObjid = 1, Name = "Ping Sensor", SensorType = "ping", Paused = false },
            new() { Objid = 302, DeviceObjid = 1, Name = "HTTP Sensor", SensorType = "http", Paused = false },
            new() { Objid = 303, DeviceObjid = 1, Name = "Disk Sensor", SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        var count = store.ApplyAutoCategories();
        Assert.Equal(1, count); // 只有 303 命中

        using var ctx = _fx.NewContext();
        var s301 = ctx.PrtgSensors.Single(s => s.Objid == 301);
        Assert.Null(s301.Category);
        Assert.Null(s301.CategorySource);

        var s302 = ctx.PrtgSensors.Single(s => s.Objid == 302);
        Assert.Null(s302.Category);
        Assert.Null(s302.CategorySource);

        var s303 = ctx.PrtgSensors.Single(s => s.Objid == 303);
        Assert.Equal(PrtgSensorCategories.Disk, s303.Category);
        Assert.Equal(PrtgCategorySources.Auto, s303.CategorySource);
    }

    [Fact]
    public void GetWhitelistCoverage_混合資料與空白名單_計算正確()
    {
        var store = CreateStore();
        var now = DateTime.Now;
        var mapDate = new DateTime(2026, 8, 30);

        // 建立主機對應資料：Device 1 (Ok), Device 2 (Conflict), Device 3 (Unmatched)
        using (var ctx = _fx.NewContext())
        {
            ctx.PrtgHostMaps.AddRange(
                new PrtgHostMapRow { MapDate = mapDate.Date, DeviceObjid = 1, MapStatus = PrtgMapStatus.Ok, CreatedAt = now },
                new PrtgHostMapRow { MapDate = mapDate.Date, DeviceObjid = 2, MapStatus = PrtgMapStatus.Conflict, CreatedAt = now },
                new PrtgHostMapRow { MapDate = mapDate.Date, DeviceObjid = 3, MapStatus = PrtgMapStatus.Unmatched, CreatedAt = now }
            );
            ctx.SaveChanges();
        }

        // 建立 sensor 資料：
        // 401: Dev 1 (Ok), Type "SNMP Disk Free" (命中白名單), Paused false -> 命中白名單且在 Ok device 上
        // 402: Dev 2 (Conflict), Type "snmp disk free" (命中白名單，小寫), Paused false -> 命中白名單，但不在 Ok device 上
        // 403: Dev 1 (Ok), Type "SNMP Disk Free", Paused true -> 暫停，不算
        // 404: Dev 1 (Ok), Type "ping" (未命中白名單), Paused false -> 未命中白名單
        // 405: Dev 4 (無對應), Type "SNMP CPU Load" (命中白名單), Paused false -> 命中白名單，但不在 Ok device 上
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 401, DeviceObjid = 1, Name = "Disk1", SensorType = "SNMP Disk Free", Paused = false },
            new() { Objid = 402, DeviceObjid = 2, Name = "Disk2", SensorType = "snmp disk free", Paused = false },
            new() { Objid = 403, DeviceObjid = 1, Name = "Disk3", SensorType = "SNMP Disk Free", Paused = true },
            new() { Objid = 404, DeviceObjid = 1, Name = "Ping", SensorType = "ping", Paused = false },
            new() { Objid = 405, DeviceObjid = 4, Name = "CPU", SensorType = "SNMP CPU Load", Paused = false }
        }, now);

        var whitelist = new[] { "SNMP Disk Free", "SNMP CPU Load" };

        // 正常情境（有 mapDate）
        var coverage = store.GetWhitelistCoverage(whitelist, mapDate);
        // 命中白名單未暫停的 sensor：401, 402, 405 -> 共 3 個
        // 其中位於 Ok device (Dev 1) 上的 sensor：401 -> 共 1 個
        Assert.Equal(3, coverage.WhitelistSensorCount);
        Assert.Equal(1, coverage.OnMappedDeviceCount);

        // mapDate 為 null 情境
        var coverageNoMap = store.GetWhitelistCoverage(whitelist, null);
        Assert.Equal(3, coverageNoMap.WhitelistSensorCount);
        Assert.Equal(0, coverageNoMap.OnMappedDeviceCount);

        // 白名單為空清單情境
        var coverageEmpty = store.GetWhitelistCoverage(Array.Empty<string>(), mapDate);
        Assert.Equal(0, coverageEmpty.WhitelistSensorCount);
        Assert.Equal(0, coverageEmpty.OnMappedDeviceCount);

        // 白名單為 null 情境
        var coverageNull = store.GetWhitelistCoverage(null, mapDate);
        Assert.Equal(0, coverageNull.WhitelistSensorCount);
        Assert.Equal(0, coverageNull.OnMappedDeviceCount);
    }

    [Fact]
    public void GetValueFetchTargets_只回傳指定device上的sensor()
    {
        var store = CreateStore();
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, Name = "Sensor-Dev10", SensorType = "ping", Paused = false },
            new() { Objid = 1002, DeviceObjid = 20, Name = "Sensor-Dev20", SensorType = "ping", Paused = false }
        }, now);

        var targets = store.GetValueFetchTargets(whitelist: null, deviceObjids: new[] { 10L });

        Assert.Single(targets);
        Assert.Contains(1001L, targets);
        Assert.DoesNotContain(1002L, targets);
    }

    [Fact]
    public void GetValueFetchTargets_排除已暫停的sensor()
    {
        var store = CreateStore();
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 10, Name = "Active-Sensor", SensorType = "ping", Paused = false },
            new() { Objid = 2002, DeviceObjid = 10, Name = "Paused-Sensor", SensorType = "ping", Paused = true }
        }, now);

        var targets = store.GetValueFetchTargets(whitelist: null, deviceObjids: new[] { 10L });

        Assert.Single(targets);
        Assert.Contains(2001L, targets);
        Assert.DoesNotContain(2002L, targets);
    }

    [Fact]
    public void GetValueFetchTargets_白名單比對不分大小寫且白名單為空時不限制type()
    {
        var store = CreateStore();
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 3001, DeviceObjid = 10, Name = "Disk Sensor 1", SensorType = "SNMP Disk Free", Paused = false },
            new() { Objid = 3002, DeviceObjid = 10, Name = "Disk Sensor 2", SensorType = "snmp disk free", Paused = false },
            new() { Objid = 3003, DeviceObjid = 10, Name = "Ping Sensor", SensorType = "ping", Paused = false },
            new() { Objid = 3004, DeviceObjid = 10, Name = "HTTP Sensor", SensorType = "http", Paused = false }
        }, now);

        // 1. 白名單比對不分大小寫：給予不同大小寫的白名單 "snmp disk free"
        var targetsWithWhitelist = store.GetValueFetchTargets(
            whitelist: new[] { "snmp disk free" },
            deviceObjids: new[] { 10L });

        Assert.Equal(2, targetsWithWhitelist.Count);
        Assert.Contains(3001L, targetsWithWhitelist);
        Assert.Contains(3002L, targetsWithWhitelist);
        Assert.DoesNotContain(3003L, targetsWithWhitelist);
        Assert.DoesNotContain(3004L, targetsWithWhitelist);

        // 2. 白名單為空清單時不限制 type
        var targetsEmptyWhitelist = store.GetValueFetchTargets(
            whitelist: Array.Empty<string>(),
            deviceObjids: new[] { 10L });

        Assert.Equal(4, targetsEmptyWhitelist.Count);
        Assert.Contains(3001L, targetsEmptyWhitelist);
        Assert.Contains(3002L, targetsEmptyWhitelist);
        Assert.Contains(3003L, targetsEmptyWhitelist);
        Assert.Contains(3004L, targetsEmptyWhitelist);

        // 3. 白名單為 null 時不限制 type
        var targetsNullWhitelist = store.GetValueFetchTargets(
            whitelist: null,
            deviceObjids: new[] { 10L });

        Assert.Equal(4, targetsNullWhitelist.Count);
        Assert.Contains(3001L, targetsNullWhitelist);
        Assert.Contains(3002L, targetsNullWhitelist);
        Assert.Contains(3003L, targetsNullWhitelist);
        Assert.Contains(3004L, targetsNullWhitelist);
    }

    [Fact]
    public void GetValueFetchTargets_device集合為空時回空清單()
    {
        var store = CreateStore();
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 4001, DeviceObjid = 10, Name = "Sensor", SensorType = "ping", Paused = false }
        }, now);

        // deviceObjids 為空清單
        var targetsEmpty = store.GetValueFetchTargets(whitelist: null, deviceObjids: Array.Empty<long>());
        Assert.Empty(targetsEmpty);

        // deviceObjids 為 null
        var targetsNull = store.GetValueFetchTargets(whitelist: null, deviceObjids: null!);
        Assert.Empty(targetsNull);
    }

    [Fact]
    public void GetManualMaps_無資料時回傳空清單_有資料時回傳全部()
    {
        var store = CreateStore();
        Assert.Empty(store.GetManualMaps());

        var now = DateTime.Now;
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 10,
            CreatedBy = "admin",
            Note = "對應1",
            CreatedAt = now
        });
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1002,
            HostId = 20,
            CreatedBy = "admin",
            Note = "對應2",
            CreatedAt = now
        });

        var maps = store.GetManualMaps();
        Assert.Equal(2, maps.Count);
        Assert.Contains(maps, m => m.DeviceObjid == 1001 && m.HostId == 10);
        Assert.Contains(maps, m => m.DeviceObjid == 1002 && m.HostId == 20);
    }

    [Fact]
    public void UpsertManualMap_新增後可讀回且再次更新時CreatedAt不變()
    {
        var store = CreateStore();
        var t1 = new DateTime(2026, 8, 31, 10, 0, 0);
        var t2 = new DateTime(2026, 8, 31, 11, 0, 0);

        // 首次新增
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 10,
            CreatedBy = "admin",
            Note = "首次建立",
            CreatedAt = t1
        });

        var maps = store.GetManualMaps();
        Assert.Single(maps);
        Assert.Equal(1001, maps[0].DeviceObjid);
        Assert.Equal(10, maps[0].HostId);
        Assert.Equal("admin", maps[0].CreatedBy);
        Assert.Equal("首次建立", maps[0].Note);
        Assert.Equal(t1, maps[0].CreatedAt);

        // 再次 upsert 同一 device：更新 HostId, Note, CreatedBy，但傳入不同 CreatedAt
        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 20,
            CreatedBy = "operator1",
            Note = "修改備註",
            CreatedAt = t2
        });

        var updatedMaps = store.GetManualMaps();
        Assert.Single(updatedMaps);
        Assert.Equal(1001, updatedMaps[0].DeviceObjid);
        Assert.Equal(20, updatedMaps[0].HostId);
        Assert.Equal("operator1", updatedMaps[0].CreatedBy);
        Assert.Equal("修改備註", updatedMaps[0].Note);
        Assert.Equal(t1, updatedMaps[0].CreatedAt); // CreatedAt 保持 t1，不被覆蓋
    }

    [Fact]
    public void DeleteManualMap_刪除後讀不到且回傳筆數為1_刪不存在的回0()
    {
        var store = CreateStore();
        var now = DateTime.Now;

        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 10,
            CreatedBy = "admin",
            Note = "測試",
            CreatedAt = now
        });

        Assert.Single(store.GetManualMaps());

        // 刪除存在的 device
        var deletedCount = store.DeleteManualMap(1001);
        Assert.Equal(1, deletedCount);
        Assert.Empty(store.GetManualMaps());

        // 刪除不存在的 device
        var deletedNonExistent = store.DeleteManualMap(9999);
        Assert.Equal(0, deletedNonExistent);
    }

    [Fact]
    public void GetSensorsByDevice_只回指定Device的Sensors_雙面斷言()
    {
        var store = CreateStore();
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "CPU Load", SensorType = "wmicpu", Paused = false },
            new() { Objid = 2002, DeviceObjid = 1001, Name = "Memory", SensorType = "snmpmem", Paused = false },
            new() { Objid = 2003, DeviceObjid = 1002, Name = "Disk Space", SensorType = "wmidisk", Paused = false }
        }, now);

        var sensors1001 = store.GetSensorsByDevice(1001);
        Assert.Equal(2, sensors1001.Count);
        Assert.All(sensors1001, s => Assert.Equal(1001, s.DeviceObjid));
        Assert.Contains(sensors1001, s => s.Objid == 2001);
        Assert.Contains(sensors1001, s => s.Objid == 2002);
        Assert.DoesNotContain(sensors1001, s => s.Objid == 2003);

        var sensors1002 = store.GetSensorsByDevice(1002);
        Assert.Single(sensors1002);
        Assert.Equal(2003, sensors1002[0].Objid);
        Assert.Equal(1002, sensors1002[0].DeviceObjid);
    }

    [Fact]
    public void GetSensorStatuses_回傳含SensorType且只回未暫停sensor()
    {
        var store = CreateStore();
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 101, DeviceObjid = 1, Name = "Active Disk", SensorType = "SNMP Disk Free", Status = "Up", Paused = false },
            new() { Objid = 102, DeviceObjid = 1, Name = "Active Ping", SensorType = "ping", Status = "Down", Paused = false },
            new() { Objid = 103, DeviceObjid = 2, Name = "Paused Sensor", SensorType = "wmicpu", Status = "Paused", Paused = true }
        }, now);

        var statuses = store.GetSensorStatuses();

        Assert.Equal(2, statuses.Count);
        Assert.Contains(statuses, s => s.Objid == 101 && s.DeviceObjid == 1 && s.Status == "Up" && s.SensorType == "SNMP Disk Free");
        Assert.Contains(statuses, s => s.Objid == 102 && s.DeviceObjid == 1 && s.Status == "Down" && s.SensorType == "ping");
        Assert.DoesNotContain(statuses, s => s.Objid == 103);
    }

    [Fact]
    public void GetLatestHostMapWithDate_資料稀疏仍取得最近一日()
    {
        var store = CreateStore();
        var targetDate = DateTime.Today.AddDays(-20);

        store.ReplaceHostMapForDate(targetDate, new List<PrtgHostMapRow>
        {
            new() { MapDate = targetDate, DeviceObjid = 101, HostName = "SRV-101", MapStatus = PrtgMapStatus.Ok }
        });

        var (mapDate, rows) = store.GetLatestHostMapWithDate(30);

        Assert.Equal(targetDate, mapDate);
        Assert.Single(rows);
        Assert.Equal(101, rows[0].DeviceObjid);

        var latestRows = store.GetLatestHostMap(30);
        Assert.Single(latestRows);
        Assert.Equal(101, latestRows[0].DeviceObjid);
    }

    [Fact]
    public void GetLatestHostMapWithDate_多日資料取最新那一日且只包含該日列()
    {
        var store = CreateStore();
        var day20Ago = DateTime.Today.AddDays(-20);
        var day10Ago = DateTime.Today.AddDays(-10);
        var day3Ago = DateTime.Today.AddDays(-3);

        store.ReplaceHostMapForDate(day20Ago, new List<PrtgHostMapRow>
        {
            new() { MapDate = day20Ago, DeviceObjid = 201, HostName = "SRV-201", MapStatus = PrtgMapStatus.Ok }
        });

        store.ReplaceHostMapForDate(day10Ago, new List<PrtgHostMapRow>
        {
            new() { MapDate = day10Ago, DeviceObjid = 301, HostName = "SRV-301", MapStatus = PrtgMapStatus.Conflict },
            new() { MapDate = day10Ago, DeviceObjid = 302, HostName = "SRV-302", MapStatus = PrtgMapStatus.Unmatched }
        });

        store.ReplaceHostMapForDate(day3Ago, new List<PrtgHostMapRow>
        {
            new() { MapDate = day3Ago, DeviceObjid = 401, HostName = "SRV-401", MapStatus = PrtgMapStatus.Ok },
            new() { MapDate = day3Ago, DeviceObjid = 402, HostName = "SRV-402", MapStatus = PrtgMapStatus.Ok }
        });

        var (mapDate, rows) = store.GetLatestHostMapWithDate(30);

        Assert.Equal(day3Ago, mapDate);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.DeviceObjid == 401);
        Assert.Contains(rows, r => r.DeviceObjid == 402);
        Assert.DoesNotContain(rows, r => r.DeviceObjid == 201);
        Assert.DoesNotContain(rows, r => r.DeviceObjid == 301);
        Assert.DoesNotContain(rows, r => r.DeviceObjid == 302);

        var latestRows = store.GetLatestHostMap(30);
        Assert.Equal(2, latestRows.Count);
        Assert.Contains(latestRows, r => r.DeviceObjid == 401);
        Assert.Contains(latestRows, r => r.DeviceObjid == 402);
    }

    [Fact]
    public void GetLatestHostMapWithDate_以指定基準日回看時不取到基準日之後的對應()
    {
        var store = CreateStore();
        var day10Ago = DateTime.Today.AddDays(-10);
        var day3Ago = DateTime.Today.AddDays(-3);

        store.ReplaceHostMapForDate(day10Ago, new List<PrtgHostMapRow>
        {
            new() { MapDate = day10Ago, DeviceObjid = 601, HostName = "SRV-601", MapStatus = PrtgMapStatus.Ok }
        });
        store.ReplaceHostMapForDate(day3Ago, new List<PrtgHostMapRow>
        {
            new() { MapDate = day3Ago, DeviceObjid = 602, HostName = "SRV-602", MapStatus = PrtgMapStatus.Ok }
        });

        // 基準日為 7 天前：只能看到 10 天前那筆，不得取到基準日之後的 3 天前那筆
        var (mapDate, rows) = store.GetLatestHostMapWithDate(31, DateTime.Today.AddDays(-7));

        Assert.Equal(day10Ago, mapDate);
        Assert.Single(rows);
        Assert.Equal(601, rows[0].DeviceObjid);
    }

    [Fact]
    public void GetLatestHostMapWithDate_窗內無資料回傳Null與空清單()
    {
        var store = CreateStore();
        var day60Ago = DateTime.Today.AddDays(-60);

        store.ReplaceHostMapForDate(day60Ago, new List<PrtgHostMapRow>
        {
            new() { MapDate = day60Ago, DeviceObjid = 501, HostName = "SRV-501", MapStatus = PrtgMapStatus.Ok }
        });

        var (mapDate, rows) = store.GetLatestHostMapWithDate(30);

        Assert.Null(mapDate);
        Assert.Empty(rows);

        var latestRows = store.GetLatestHostMap(30);
        Assert.Empty(latestRows);
    }

    [Fact]
    public void GetDailyValueAggregations_品質過濾_平均最小最大只採計ok且null不拉低平均()
    {
        var store = CreateStore();
        var date = new DateTime(2026, 8, 31);
        var from = date;
        var to = date.AddDays(1);

        // 同一個 sensor 同一天寫入 3 列 ok（值 10/20/30）、1 列 unknown（null）、1 列 nodata（null）
        var values = new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = date.AddHours(10), AvgValue = 10.0, MinValue = 10.0, MaxValue = 10.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 1001, PeriodStart = date.AddHours(11), AvgValue = 20.0, MinValue = 20.0, MaxValue = 20.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 1001, PeriodStart = date.AddHours(12), AvgValue = 30.0, MinValue = 30.0, MaxValue = 30.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 1001, PeriodStart = date.AddHours(13), AvgValue = null, MinValue = null, MaxValue = null, Quality = PrtgDataQuality.Unknown },
            new() { SensorObjid = 1001, PeriodStart = date.AddHours(14), AvgValue = null, MinValue = null, MaxValue = null, Quality = PrtgDataQuality.NoData },
            // 有值但品質不是 ok（暫停期間仍可能帶回數值）：必須被品質過濾擋掉。
            // 少了這一列，「只靠 AvgValue != null 判斷」與「同時判斷 Quality == ok」結果相同，
            // 品質過濾等於沒被驗到（突變測試會存活）。
            new() { SensorObjid = 1001, PeriodStart = date.AddHours(15), AvgValue = 999.0, MinValue = 999.0, MaxValue = 999.0, Quality = PrtgDataQuality.Paused },
        };
        store.UpsertValues(values);

        var result = store.GetDailyValueAggregations(from, to);

        Assert.Single(result);
        var row = result[0];
        Assert.Equal(1001, row.SensorObjid);
        Assert.Equal(date, row.Date);
        Assert.Equal(20.0, row.AvgValue);
        Assert.Equal(10.0, row.MinValue);
        Assert.Equal(30.0, row.MaxValue);
        Assert.Equal(3, row.OkCount);
        Assert.Equal(1, row.UnknownCount);
        Assert.Equal(1, row.NodataCount);
        Assert.Equal(1, row.OtherCount);
        Assert.Equal(6, row.TotalCount);
    }

    [Fact]
    public void GetDailyValueAggregations_完全沒有ok列的日子仍回傳一列且統計值為null()
    {
        var store = CreateStore();
        var date = new DateTime(2026, 8, 31);
        var from = date;
        var to = date.AddDays(1);

        // 某 sensor 某日只有 unknown 與 nodata
        var values = new List<PrtgValueRow>
        {
            new() { SensorObjid = 2001, PeriodStart = date.AddHours(10), AvgValue = null, Quality = PrtgDataQuality.Unknown },
            new() { SensorObjid = 2001, PeriodStart = date.AddHours(11), AvgValue = null, Quality = PrtgDataQuality.NoData },
        };
        store.UpsertValues(values);

        var result = store.GetDailyValueAggregations(from, to);

        Assert.Single(result);
        var row = result[0];
        Assert.Equal(2001, row.SensorObjid);
        Assert.Equal(date, row.Date);
        Assert.Null(row.AvgValue);
        Assert.Null(row.MinValue);
        Assert.Null(row.MaxValue);
        Assert.Equal(0, row.OkCount);
        Assert.Equal(1, row.UnknownCount);
        Assert.Equal(1, row.NodataCount);
        Assert.Equal(2, row.TotalCount);
    }

    [Fact]
    public void GetValueCoverageSummary_涵蓋摘要_只看ok品質之涵蓋天數與起訖時間()
    {
        var store = CreateStore();
        var from = new DateTime(2026, 8, 20);
        var to = new DateTime(2026, 8, 25);

        // sensor 3001 在三個不同日期各有 ok 列（其中一天有兩列 ok）
        // sensor 3002 在三個不同日期只有 unknown/nodata 列
        var values = new List<PrtgValueRow>
        {
            new() { SensorObjid = 3001, PeriodStart = new DateTime(2026, 8, 20, 10, 0, 0), AvgValue = 10.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 3001, PeriodStart = new DateTime(2026, 8, 21, 11, 0, 0), AvgValue = 15.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 3001, PeriodStart = new DateTime(2026, 8, 21, 15, 0, 0), AvgValue = 20.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 3001, PeriodStart = new DateTime(2026, 8, 22, 9, 0, 0), AvgValue = 25.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 3001, PeriodStart = new DateTime(2026, 8, 22, 18, 0, 0), AvgValue = null, Quality = PrtgDataQuality.Unknown },

            new() { SensorObjid = 3002, PeriodStart = new DateTime(2026, 8, 20, 10, 0, 0), AvgValue = null, Quality = PrtgDataQuality.Unknown },
            new() { SensorObjid = 3002, PeriodStart = new DateTime(2026, 8, 21, 11, 0, 0), AvgValue = null, Quality = PrtgDataQuality.NoData },
            new() { SensorObjid = 3002, PeriodStart = new DateTime(2026, 8, 22, 12, 0, 0), AvgValue = null, Quality = PrtgDataQuality.Unknown },
        };
        store.UpsertValues(values);

        var result = store.GetValueCoverageSummary(from, to);

        Assert.Equal(2, result.Count);

        var s3001 = result.Single(r => r.SensorObjid == 3001);
        Assert.Equal(4, s3001.OkCount);
        Assert.Equal(3, s3001.OkDays);
        Assert.Equal(new DateTime(2026, 8, 20, 10, 0, 0), s3001.EarliestOkPeriod);
        Assert.Equal(new DateTime(2026, 8, 22, 9, 0, 0), s3001.LatestOkPeriod);
        Assert.Equal(1, s3001.UnknownCount);
        Assert.Equal(0, s3001.NodataCount);
        Assert.Equal(5, s3001.TotalCount);

        var s3002 = result.Single(r => r.SensorObjid == 3002);
        Assert.Equal(0, s3002.OkCount);
        Assert.Equal(0, s3002.OkDays); // 涵蓋天數為 0（不是 3）
        Assert.Null(s3002.EarliestOkPeriod);
        Assert.Null(s3002.LatestOkPeriod);
        Assert.Equal(2, s3002.UnknownCount);
        Assert.Equal(1, s3002.NodataCount);
        Assert.Equal(3, s3002.TotalCount);
    }

    [Fact]
    public void GetDailyValueMagnitudes_每日量級_逐日相異sensor數與各品質筆數正確()
    {
        var store = CreateStore();
        var d1 = new DateTime(2026, 8, 30);
        var d2 = new DateTime(2026, 8, 31);
        var from = d1;
        var to = d2.AddDays(1);

        // Day 1: 2 sensors, 4 rows (2 ok, 1 unknown, 1 nodata)
        // Day 2: 3 sensors, 6 rows (5 ok, 1 unknown, 0 nodata)
        var values = new List<PrtgValueRow>
        {
            new() { SensorObjid = 4001, PeriodStart = d1.AddHours(1), AvgValue = 1.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 4001, PeriodStart = d1.AddHours(2), AvgValue = 2.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 4001, PeriodStart = d1.AddHours(3), AvgValue = null, Quality = PrtgDataQuality.Unknown },
            new() { SensorObjid = 4002, PeriodStart = d1.AddHours(1), AvgValue = null, Quality = PrtgDataQuality.NoData },

            new() { SensorObjid = 4001, PeriodStart = d2.AddHours(1), AvgValue = 10.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 4001, PeriodStart = d2.AddHours(2), AvgValue = 20.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 4001, PeriodStart = d2.AddHours(3), AvgValue = 30.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 4002, PeriodStart = d2.AddHours(1), AvgValue = 40.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 4002, PeriodStart = d2.AddHours(2), AvgValue = 50.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 4003, PeriodStart = d2.AddHours(1), AvgValue = null, Quality = PrtgDataQuality.Unknown },
        };
        store.UpsertValues(values);

        var result = store.GetDailyValueMagnitudes(from, to);

        Assert.Equal(2, result.Count);

        var r1 = result[0];
        Assert.Equal(d1, r1.Date);
        Assert.Equal(2, r1.SensorCount);
        Assert.Equal(4, r1.TotalCount);
        Assert.Equal(2, r1.OkCount);
        Assert.Equal(1, r1.UnknownCount);
        Assert.Equal(1, r1.NodataCount);

        var r2 = result[1];
        Assert.Equal(d2, r2.Date);
        Assert.Equal(3, r2.SensorCount);
        Assert.Equal(6, r2.TotalCount);
        Assert.Equal(5, r2.OkCount);
        Assert.Equal(1, r2.UnknownCount);
        Assert.Equal(0, r2.NodataCount);
    }

    [Fact]
    public void GetStateChangeCoverageSummary_狀態變更涵蓋_跨三日變更之相異日期數sensor數與起訖時間正確()
    {
        var store = CreateStore();
        var from = new DateTime(2026, 8, 25);
        var to = new DateTime(2026, 8, 28);

        var changes = new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 5001, ChangedAt = new DateTime(2026, 8, 25, 8, 30, 0), Status = "Down", Quality = "Good" },
            new() { SensorObjid = 5001, ChangedAt = new DateTime(2026, 8, 25, 9, 15, 0), Status = "Up", Quality = "Good" },
            new() { SensorObjid = 5002, ChangedAt = new DateTime(2026, 8, 26, 14, 0, 0), Status = "Down", Quality = "Good" },
            new() { SensorObjid = 5003, ChangedAt = new DateTime(2026, 8, 27, 18, 45, 0), Status = "Warning", Quality = "Good" },
        };
        store.AppendStateChanges(changes);

        var summary = store.GetStateChangeCoverageSummary(from, to);

        Assert.Equal(3, summary.DistinctDates);
        Assert.Equal(3, summary.DistinctSensors);
        Assert.Equal(4, summary.TotalCount);
        Assert.Equal(new DateTime(2026, 8, 25, 8, 30, 0), summary.EarliestChangedAt);
        Assert.Equal(new DateTime(2026, 8, 27, 18, 45, 0), summary.LatestChangedAt);

        // 查無資料之區間
        var emptySummary = store.GetStateChangeCoverageSummary(new DateTime(2026, 9, 1), new DateTime(2026, 9, 2));
        Assert.Equal(0, emptySummary.DistinctDates);
        Assert.Equal(0, emptySummary.DistinctSensors);
        Assert.Equal(0, emptySummary.TotalCount);
        Assert.Null(emptySummary.EarliestChangedAt);
        Assert.Null(emptySummary.LatestChangedAt);
    }

    [Fact]
    public void PRTG聚合查詢_區間邊界_界外資料不計入()
    {
        var store = CreateStore();
        var from = new DateTime(2026, 8, 31, 0, 0, 0);
        var to = new DateTime(2026, 9, 1, 0, 0, 0);

        var values = new List<PrtgValueRow>
        {
            // 區間前（界外）
            new() { SensorObjid = 6001, PeriodStart = new DateTime(2026, 8, 30, 23, 0, 0), AvgValue = 99.0, Quality = PrtgDataQuality.Ok },
            // 區間內
            new() { SensorObjid = 6001, PeriodStart = new DateTime(2026, 8, 31, 0, 0, 0), AvgValue = 10.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 6001, PeriodStart = new DateTime(2026, 8, 31, 12, 0, 0), AvgValue = 20.0, Quality = PrtgDataQuality.Ok },
            new() { SensorObjid = 6001, PeriodStart = new DateTime(2026, 8, 31, 23, 0, 0), AvgValue = 30.0, Quality = PrtgDataQuality.Ok },
            // 區間後（界外，剛好等於 toExclusive）
            new() { SensorObjid = 6001, PeriodStart = new DateTime(2026, 9, 1, 0, 0, 0), AvgValue = 99.0, Quality = PrtgDataQuality.Ok },
        };
        store.UpsertValues(values);

        var changes = new List<PrtgStateChangeRow>
        {
            // 區間前（界外）
            new() { SensorObjid = 6001, ChangedAt = new DateTime(2026, 8, 30, 23, 59, 59), Status = "Down", Quality = "Good" },
            // 區間內
            new() { SensorObjid = 6001, ChangedAt = new DateTime(2026, 8, 31, 12, 0, 0), Status = "Up", Quality = "Good" },
            // 區間後（界外）
            new() { SensorObjid = 6001, ChangedAt = new DateTime(2026, 9, 1, 0, 0, 0), Status = "Down", Quality = "Good" },
        };
        store.AppendStateChanges(changes);

        // 1. GetValueCoverageSummary
        var cov = store.GetValueCoverageSummary(from, to);
        Assert.Single(cov);
        Assert.Equal(3, cov[0].OkCount);
        Assert.Equal(3, cov[0].TotalCount);
        Assert.Equal(new DateTime(2026, 8, 31, 0, 0, 0), cov[0].EarliestOkPeriod);
        Assert.Equal(new DateTime(2026, 8, 31, 23, 0, 0), cov[0].LatestOkPeriod);

        // 2. GetDailyValueAggregations
        var aggs = store.GetDailyValueAggregations(from, to);
        Assert.Single(aggs);
        Assert.Equal(20.0, aggs[0].AvgValue);
        Assert.Equal(10.0, aggs[0].MinValue);
        Assert.Equal(30.0, aggs[0].MaxValue);
        Assert.Equal(3, aggs[0].OkCount);

        // 3. GetDailyValueMagnitudes
        var mags = store.GetDailyValueMagnitudes(from, to);
        Assert.Single(mags);
        Assert.Equal(new DateTime(2026, 8, 31), mags[0].Date);
        Assert.Equal(3, mags[0].TotalCount);

        // 4. GetStateChangeCoverageSummary
        var stateSummary = store.GetStateChangeCoverageSummary(from, to);
        Assert.Equal(1, stateSummary.TotalCount);
        Assert.Equal(1, stateSummary.DistinctDates);
        Assert.Equal(1, stateSummary.DistinctSensors);
        Assert.Equal(new DateTime(2026, 8, 31, 12, 0, 0), stateSummary.EarliestChangedAt);
        Assert.Equal(new DateTime(2026, 8, 31, 12, 0, 0), stateSummary.LatestChangedAt);
    }
}

