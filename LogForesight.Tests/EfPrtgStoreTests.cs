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
}
