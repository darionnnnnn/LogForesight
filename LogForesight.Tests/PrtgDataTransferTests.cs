using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgDataTransferTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfPrtgStore CreateStore(EfSqliteFixture? fx = null) => new((fx ?? _fx).NewContext);

    [Fact]
    public void RoundTrip_六張表完整匯出再匯入乾淨資料庫_筆數與欄位一致()
    {
        using var fxSource = new EfSqliteFixture();
        using var fxTarget = new EfSqliteFixture();
        var storeSource = CreateStore(fxSource);
        var storeTarget = CreateStore(fxTarget);

        var syncTime = new DateTime(2026, 8, 15, 10, 0, 0);
        var mapDate = new DateTime(2026, 8, 15);

        // 1. 寫入來源資料（六張表皆有資料）
        storeSource.UpsertDevices(new List<PrtgDeviceRow>
        {
            new()
            {
                Objid = 1001,
                Name = "Core-Switch-01",
                GroupPath = "Root/Core",
                Ip = "192.168.1.1",
                Tags = "switch,core",
                Status = "Up",
                DependencyObjid = null,
                Paused = false,
                CreatedAt = syncTime
            }
        }, syncTime);

        storeSource.UpsertSensors(new List<PrtgSensorRow>
        {
            new()
            {
                Objid = 2001,
                DeviceObjid = 1001,
                Name = "Ping",
                SensorType = "ping",
                Tags = "ping",
                Unit = "ms",
                Status = "Up",
                ThresholdsJson = "{}",
                DependencyObjid = null,
                Paused = false,
                Category = "網路延遲",
                CategorySource = "auto",
                CreatedAt = syncTime
            }
        }, syncTime);

        storeSource.AppendStateChanges(new List<PrtgStateChangeRow>
        {
            new()
            {
                SensorObjid = 2001,
                ChangedAt = new DateTime(2026, 8, 15, 12, 30, 0),
                Status = "Down",
                PrevStatus = "Up",
                Message = "Ping timeout",
                Quality = "Good",
                CreatedAt = syncTime
            }
        });

        storeSource.UpsertValues(new List<PrtgValueRow>
        {
            new()
            {
                SensorObjid = 2001,
                PeriodStart = new DateTime(2026, 8, 15, 12, 0, 0),
                AvgValue = 42.5,
                MinValue = 10.0,
                MaxValue = 120.0,
                Coverage = 100.0,
                Quality = "Good",
                CreatedAt = syncTime
            }
        });

        storeSource.ReplaceHostMapForDate(mapDate, new List<PrtgHostMapRow>
        {
            new()
            {
                MapDate = mapDate,
                DeviceObjid = 1001,
                Ip = "192.168.1.1",
                HostId = 101,
                HostName = "SRV-CORE-01",
                MapStatus = PrtgMapStatus.Ok,
                Note = "自動對應成功",
                CreatedAt = syncTime
            }
        });

        storeSource.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 101,
            CreatedBy = "admin",
            Note = "人工確認對應",
            CreatedAt = syncTime
        });

        // 2. 匯出
        var package = PrtgDataTransfer.Export(storeSource, mapDate, mapDate);
        Assert.Equal(PrtgDataTransfer.CurrentFormatVersion, package.FormatVersion);
        Assert.Single(package.Devices);
        Assert.Single(package.Sensors);
        Assert.Single(package.StateChanges);
        Assert.Single(package.Values);
        Assert.Single(package.HostMaps);
        Assert.Single(package.ManualMaps);

        // 3. 匯入到全新目標端
        var importResult = PrtgDataTransfer.Import(storeTarget, package);
        Assert.Equal(1, importResult.Devices);
        Assert.Equal(1, importResult.Sensors);
        Assert.Equal(1, importResult.StateChanges);
        Assert.Equal(1, importResult.Values);
        Assert.Equal(1, importResult.HostMaps);
        Assert.Equal(1, importResult.ManualMaps);

        // 4. 逐表斷言目標端筆數與欄位值
        using var ctx = fxTarget.NewContext();
        var dev = Assert.Single(ctx.PrtgDevices);
        Assert.Equal(1001, dev.Objid);
        Assert.Equal("Core-Switch-01", dev.Name);
        Assert.Equal("192.168.1.1", dev.Ip);

        var sen = Assert.Single(ctx.PrtgSensors);
        Assert.Equal(2001, sen.Objid);
        Assert.Equal(1001, sen.DeviceObjid);
        Assert.Equal("Ping", sen.Name);
        Assert.Equal("ping", sen.SensorType);
        Assert.Equal("網路延遲", sen.Category);
        Assert.Equal("auto", sen.CategorySource);

        var sc = Assert.Single(ctx.PrtgStateChanges);
        Assert.Equal(2001, sc.SensorObjid);
        Assert.Equal(new DateTime(2026, 8, 15, 12, 30, 0), sc.ChangedAt);
        Assert.Equal("Down", sc.Status);
        Assert.Equal("Ping timeout", sc.Message);

        var val = Assert.Single(ctx.PrtgValues);
        Assert.Equal(2001, val.SensorObjid);
        Assert.Equal(new DateTime(2026, 8, 15, 12, 0, 0), val.PeriodStart);
        Assert.Equal(42.5, val.AvgValue);

        var hm = Assert.Single(ctx.PrtgHostMaps);
        Assert.Equal(mapDate, hm.MapDate);
        Assert.Equal(1001, hm.DeviceObjid);
        Assert.Equal("SRV-CORE-01", hm.HostName);

        var mm = Assert.Single(ctx.PrtgManualMaps);
        Assert.Equal(1001, mm.DeviceObjid);
        Assert.Equal(101, mm.HostId);
        Assert.Equal("人工確認對應", mm.Note);
    }

    [Fact]
    public void IdempotentImport_重複匯入同一個資料包_第二次之後各表筆數不增()
    {
        var store = CreateStore();
        var syncTime = new DateTime(2026, 8, 15, 10, 0, 0);
        var mapDate = new DateTime(2026, 8, 15);

        var package = new PrtgDataPackage
        {
            FormatVersion = PrtgDataTransfer.CurrentFormatVersion,
            ExportedAt = syncTime,
            FromDate = mapDate,
            ToDate = mapDate,
            Devices = new List<PrtgDeviceRow>
            {
                new() { Objid = 1001, Name = "Dev-01", GroupPath = "Root", Ip = "10.0.0.1", CreatedAt = syncTime }
            },
            Sensors = new List<PrtgSensorRow>
            {
                new() { Objid = 2001, DeviceObjid = 1001, Name = "Ping", SensorType = "ping", CreatedAt = syncTime }
            },
            StateChanges = new List<PrtgStateChangeRow>
            {
                new() { SensorObjid = 2001, ChangedAt = new DateTime(2026, 8, 15, 10, 0, 0), Status = "Down", CreatedAt = syncTime }
            },
            Values = new List<PrtgValueRow>
            {
                new() { SensorObjid = 2001, PeriodStart = new DateTime(2026, 8, 15, 10, 0, 0), AvgValue = 10.0, CreatedAt = syncTime }
            },
            HostMaps = new List<PrtgHostMapRow>
            {
                new() { MapDate = mapDate, DeviceObjid = 1001, HostId = 1, HostName = "H1", MapStatus = "ok", CreatedAt = syncTime }
            },
            ManualMaps = new List<PrtgManualMapRow>
            {
                new() { DeviceObjid = 1001, HostId = 1, CreatedBy = "admin", Note = "N1", CreatedAt = syncTime }
            }
        };

        // 第一次匯入
        var res1 = PrtgDataTransfer.Import(store, package);
        Assert.Equal(1, res1.Devices);
        Assert.Equal(1, res1.Sensors);
        Assert.Equal(1, res1.StateChanges);
        Assert.Equal(1, res1.Values);
        Assert.Equal(1, res1.HostMaps);
        Assert.Equal(1, res1.ManualMaps);

        int countDev1, countSen1, countSc1, countVal1, countHm1, countMm1;
        using (var ctx = _fx.NewContext())
        {
            countDev1 = ctx.PrtgDevices.Count();
            countSen1 = ctx.PrtgSensors.Count();
            countSc1 = ctx.PrtgStateChanges.Count();
            countVal1 = ctx.PrtgValues.Count();
            countHm1 = ctx.PrtgHostMaps.Count();
            countMm1 = ctx.PrtgManualMaps.Count();
        }

        Assert.Equal(1, countDev1);
        Assert.Equal(1, countSen1);
        Assert.Equal(1, countSc1);
        Assert.Equal(1, countVal1);
        Assert.Equal(1, countHm1);
        Assert.Equal(1, countMm1);

        // 第二次匯入同一個 package
        var res2 = PrtgDataTransfer.Import(store, package);
        // StateChanges 去重後實際新增 0 筆
        Assert.Equal(0, res2.StateChanges);

        int countDev2, countSen2, countSc2, countVal2, countHm2, countMm2;
        using (var ctx = _fx.NewContext())
        {
            countDev2 = ctx.PrtgDevices.Count();
            countSen2 = ctx.PrtgSensors.Count();
            countSc2 = ctx.PrtgStateChanges.Count();
            countVal2 = ctx.PrtgValues.Count();
            countHm2 = ctx.PrtgHostMaps.Count();
            countMm2 = ctx.PrtgManualMaps.Count();
        }

        // 斷言第二次之後各表筆數完全沒有增加
        Assert.Equal(countDev1, countDev2);
        Assert.Equal(countSen1, countSen2);
        Assert.Equal(countSc1, countSc2);
        Assert.Equal(countVal1, countVal2);
        Assert.Equal(countHm1, countHm2);
        Assert.Equal(countMm1, countMm2);
    }

    [Fact]
    public void DateRangeFilter_只匯出期間內的時序資料_雙面斷言()
    {
        var store = CreateStore();
        var syncTime = new DateTime(2026, 8, 15, 10, 0, 0);

        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "Dev-01", GroupPath = "Root", Ip = "10.0.0.1", CreatedAt = syncTime }
        }, syncTime);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "Ping", SensorType = "ping", CreatedAt = syncTime }
        }, syncTime);

        // 狀態變更：一筆在 8/15 期間內、一筆在 8/14（期間前）、一筆在 8/16（期間後）
        store.AppendStateChanges(new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 2001, ChangedAt = new DateTime(2026, 8, 14, 23, 59, 59), Status = "Down", Message = "Before" },
            new() { SensorObjid = 2001, ChangedAt = new DateTime(2026, 8, 15, 10, 0, 0), Status = "Up", Message = "Inside" },
            new() { SensorObjid = 2001, ChangedAt = new DateTime(2026, 8, 16, 0, 0, 0), Status = "Down", Message = "After" }
        });

        // 數值：一筆在 8/15 期間內、一筆在 8/14（期間前）、一筆在 8/16（期間後）
        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 2001, PeriodStart = new DateTime(2026, 8, 14, 23, 0, 0), AvgValue = 14.0 },
            new() { SensorObjid = 2001, PeriodStart = new DateTime(2026, 8, 15, 10, 0, 0), AvgValue = 15.0 },
            new() { SensorObjid = 2001, PeriodStart = new DateTime(2026, 8, 16, 0, 0, 0), AvgValue = 16.0 }
        });

        // 主機對應：一筆在 8/15、一筆在 8/14、一筆在 8/16
        store.ReplaceHostMapForDate(new DateTime(2026, 8, 14), new List<PrtgHostMapRow>
        {
            new() { MapDate = new DateTime(2026, 8, 14), DeviceObjid = 1001, HostId = 1, HostName = "H14", MapStatus = "ok" }
        });
        store.ReplaceHostMapForDate(new DateTime(2026, 8, 15), new List<PrtgHostMapRow>
        {
            new() { MapDate = new DateTime(2026, 8, 15), DeviceObjid = 1001, HostId = 1, HostName = "H15", MapStatus = "ok" }
        });
        store.ReplaceHostMapForDate(new DateTime(2026, 8, 16), new List<PrtgHostMapRow>
        {
            new() { MapDate = new DateTime(2026, 8, 16), DeviceObjid = 1001, HostId = 1, HostName = "H16", MapStatus = "ok" }
        });

        // 只匯出 2026-08-15 當天
        var package = PrtgDataTransfer.Export(store, new DateTime(2026, 8, 15), new DateTime(2026, 8, 15));

        // 雙面斷言：期間內的有、期間外的沒有
        Assert.Single(package.StateChanges);
        Assert.Equal("Inside", package.StateChanges[0].Message);
        Assert.DoesNotContain(package.StateChanges, sc => sc.Message == "Before");
        Assert.DoesNotContain(package.StateChanges, sc => sc.Message == "After");

        Assert.Single(package.Values);
        Assert.Equal(15.0, package.Values[0].AvgValue);
        Assert.DoesNotContain(package.Values, v => v.AvgValue == 14.0);
        Assert.DoesNotContain(package.Values, v => v.AvgValue == 16.0);

        Assert.Single(package.HostMaps);
        Assert.Equal(new DateTime(2026, 8, 15), package.HostMaps[0].MapDate);
        Assert.Equal("H15", package.HostMaps[0].HostName);
        Assert.DoesNotContain(package.HostMaps, hm => hm.HostName == "H14");
        Assert.DoesNotContain(package.HostMaps, hm => hm.HostName == "H16");
    }

    [Fact]
    public void VersionMismatch_不支援的格式版本_擲出驗證例外()
    {
        var store = CreateStore();
        var package = new PrtgDataPackage
        {
            FormatVersion = 999,
            ExportedAt = DateTime.Now,
            FromDate = DateTime.Today,
            ToDate = DateTime.Today
        };

        var ex = Assert.Throws<InvalidOperationException>(() => PrtgDataTransfer.Import(store, package));
        Assert.Contains("999", ex.Message);
        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public void Import_不覆蓋既有人工分類與來源()
    {
        var store = CreateStore();
        var syncTime = new DateTime(2026, 8, 1, 10, 0, 0);

        // 1. 目標端已有一個 sensor，且具備人工分類
        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "Dev-01", GroupPath = "Root", CreatedAt = syncTime }
        }, syncTime);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new()
            {
                Objid = 2001,
                DeviceObjid = 1001,
                Name = "Disk C:",
                SensorType = "diskfree",
                Category = "人工分類",
                CategorySource = "manual",
                CreatedAt = syncTime
            }
        }, syncTime);

        // 驗證初始寫入確實是「人工分類」與「manual」
        using (var ctx = _fx.NewContext())
        {
            var initial = ctx.PrtgSensors.Single(s => s.Objid == 2001);
            Assert.Equal("人工分類", initial.Category);
            Assert.Equal("manual", initial.CategorySource);
        }

        // 2. 準備匯入的 package：同一個 sensor，但 Category 與 CategorySource 為自動或 null
        var package = new PrtgDataPackage
        {
            FormatVersion = PrtgDataTransfer.CurrentFormatVersion,
            ExportedAt = new DateTime(2026, 8, 15, 10, 0, 0),
            FromDate = new DateTime(2026, 8, 15),
            ToDate = new DateTime(2026, 8, 15),
            Sensors = new List<PrtgSensorRow>
            {
                new()
                {
                    Objid = 2001,
                    DeviceObjid = 1001,
                    Name = "Disk C: (Renamed)",
                    SensorType = "diskfree",
                    Category = "儲存空間",
                    CategorySource = "auto"
                }
            }
        };

        // 3. 執行匯入
        PrtgDataTransfer.Import(store, package);

        // 4. 斷言那兩欄完全沒變，但描述性欄位（如 Name）有更新
        using (var ctx = _fx.NewContext())
        {
            var updated = ctx.PrtgSensors.Single(s => s.Objid == 2001);
            Assert.Equal("Disk C: (Renamed)", updated.Name);
            Assert.Equal("人工分類", updated.Category);
            Assert.Equal("manual", updated.CategorySource);
        }
    }

    [Fact]
    public void Export_結構表全量時序表依期間()
    {
        var store = CreateStore();
        var tOld = new DateTime(2026, 1, 1, 10, 0, 0);
        var tMid = new DateTime(2026, 6, 1, 10, 0, 0);
        var targetDate = new DateTime(2026, 8, 15);

        // 建立不同時間建立的裝置與感測器
        store.UpsertDevices(new List<PrtgDeviceRow>
        {
            new() { Objid = 1001, Name = "Dev-Old", GroupPath = "Root", CreatedAt = tOld },
            new() { Objid = 1002, Name = "Dev-Mid", GroupPath = "Root", CreatedAt = tMid }
        }, tOld);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 1001, Name = "Ping-1", SensorType = "ping", CreatedAt = tOld },
            new() { Objid = 2002, DeviceObjid = 1002, Name = "Ping-2", SensorType = "ping", CreatedAt = tMid }
        }, tOld);

        store.UpsertManualMap(new PrtgManualMapRow
        {
            DeviceObjid = 1001,
            HostId = 1,
            CreatedBy = "admin",
            CreatedAt = tOld
        });

        // 狀態變更：一筆在期間內（8/15）、一筆在期間外（1/1）
        store.AppendStateChanges(new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 2001, ChangedAt = new DateTime(2026, 1, 1, 12, 0, 0), Status = "Down", Message = "Old" },
            new() { SensorObjid = 2002, ChangedAt = new DateTime(2026, 8, 15, 12, 0, 0), Status = "Down", Message = "Recent" }
        });

        // 匯出 8/15 當天
        var package = PrtgDataTransfer.Export(store, targetDate, targetDate);

        // 結構表不受期間影響，全量匯出
        Assert.Equal(2, package.Devices.Count);
        Assert.Contains(package.Devices, d => d.Objid == 1001);
        Assert.Contains(package.Devices, d => d.Objid == 1002);

        Assert.Equal(2, package.Sensors.Count);
        Assert.Contains(package.Sensors, s => s.Objid == 2001);
        Assert.Contains(package.Sensors, s => s.Objid == 2002);

        Assert.Single(package.ManualMaps);
        Assert.Equal(1001, package.ManualMaps[0].DeviceObjid);

        // 時序表受期間限制，只帶出 8/15 期間內的資料
        Assert.Single(package.StateChanges);
        Assert.Equal("Recent", package.StateChanges[0].Message);
    }
}
