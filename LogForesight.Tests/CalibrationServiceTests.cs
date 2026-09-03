using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace LogForesight.Tests;

public class CalibrationServiceTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hostStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeRuleStore _ruleStore = new();

    public CalibrationServiceTests()
    {
        // 預設開啟 PRTG 設定
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgRetentionDays = 180;
            s.RawEventRetentionDays = 120;
            s.PrtgSensorTypeWhitelist = new List<string> { "SNMP Disk Free", "SNMP CPU Load" };
        });
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private CalibrationService CreateService()
    {
        // 判定快取是靜態的，測試之間必須清乾淨（否則前一個測試的結論會漏到下一個）
        CalibrationService.ClearAssessmentCache();
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        var issueQuery = new EfIssueAggregateQuery(_fx.NewContext, _hostStore);
        return new CalibrationService(_fx.NewContext, prtgStore, issueQuery, _settingsStore, _ruleStore);
    }

    // ── 1. PRTG 值型基線：四種狀態測試 ─────────────────────────────────

    [Fact]
    public void AssessStatus_值型基線_PRTG未啟用或鏡像無Sensor_無法取得()
    {
        var service = CreateService();

        // (a) PRTG 未啟用
        _settingsStore.Update(s => s.PrtgEnabled = false);
        var summary1 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary1.PrtgValueBaseline.Status);
        Assert.Equal("無法取得", summary1.PrtgValueBaseline.StatusText);
        Assert.Contains(summary1.PrtgValueBaseline.Explanations, s => s.Contains("請先在 PRTG 維護頁完成連線設定"));

        // (b) PRTG 已啟用但鏡像無任何 sensor
        _settingsStore.Update(s => s.PrtgEnabled = true);
        var summary2 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary2.PrtgValueBaseline.Status);
    }

    [Fact]
    public void AssessStatus_值型基線_未達可用主機數或天數_不足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 建立 5 台主機（<10 台），每台 1 個 sensor 涵蓋 30 天
        var sensors = new List<PrtgSensorRow>();
        var hostMaps = new List<PrtgHostMapRow>();
        var values = new List<PrtgValueRow>();

        for (int h = 1; h <= 5; h++)
        {
            long devId = h * 10;
            long sensorId = h * 100;
            sensors.Add(new PrtgSensorRow { Objid = sensorId, DeviceObjid = devId, SensorType = "SNMP Disk Free", Paused = false });
            hostMaps.Add(new PrtgHostMapRow { MapDate = anchor, DeviceObjid = devId, HostId = h, HostName = $"HOST-{h}", MapStatus = PrtgMapStatus.Ok, CreatedAt = now });

            // 涵蓋 30 天，每天 12 列 ok
            for (int d = 0; d < 30; d++)
            {
                var day = anchor.AddDays(-d);
                for (int hour = 0; hour < 12; hour++)
                {
                    values.Add(new PrtgValueRow { SensorObjid = sensorId, PeriodStart = day.AddHours(hour), AvgValue = 50.0, Quality = PrtgDataQuality.Ok });
                }
            }
        }

        store.UpsertSensors(sensors, now);
        store.ReplaceHostMapForDate(anchor, hostMaps);
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Insufficient, summary.PrtgValueBaseline.Status);
        Assert.Equal("不足", summary.PrtgValueBaseline.StatusText);
        Assert.Equal(5, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["MappedHosts"]));
        Assert.Contains(summary.PrtgValueBaseline.Explanations, s => s.Contains("目前只有 5 台主機有基線資料，需要 10 台"));
    }

    [Fact]
    public void AssessStatus_值型基線_達10台且涵蓋28天_可用()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 建立 10 台主機，每台涵蓋 30 天（≥28 天，<56 天）
        var sensors = new List<PrtgSensorRow>();
        var hostMaps = new List<PrtgHostMapRow>();
        var values = new List<PrtgValueRow>();

        for (int h = 1; h <= 10; h++)
        {
            long devId = h * 10;
            long sensorId = h * 100;
            sensors.Add(new PrtgSensorRow { Objid = sensorId, DeviceObjid = devId, SensorType = "SNMP Disk Free", Paused = false });
            hostMaps.Add(new PrtgHostMapRow { MapDate = anchor, DeviceObjid = devId, HostId = h, HostName = $"HOST-{h}", MapStatus = PrtgMapStatus.Ok, CreatedAt = now });

            for (int d = 0; d < 30; d++)
            {
                var day = anchor.AddDays(-d);
                for (int hour = 0; hour < 12; hour++)
                {
                    values.Add(new PrtgValueRow { SensorObjid = sensorId, PeriodStart = day.AddHours(hour), AvgValue = 40.0, Quality = PrtgDataQuality.Ok });
                }
            }
        }

        store.UpsertSensors(sensors, now);
        store.ReplaceHostMapForDate(anchor, hostMaps);
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Available, summary.PrtgValueBaseline.Status);
        Assert.Equal("可用", summary.PrtgValueBaseline.StatusText);
        Assert.Equal(10, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["HostsReachingAvailable"]));
        Assert.Equal(0, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["HostsReachingSufficient"]));
    }

    [Fact]
    public void AssessStatus_值型基線_達10台且涵蓋56天_充足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 建立 10 台主機，每台涵蓋 56 天
        var sensors = new List<PrtgSensorRow>();
        var hostMaps = new List<PrtgHostMapRow>();
        var values = new List<PrtgValueRow>();

        for (int h = 1; h <= 10; h++)
        {
            long devId = h * 10;
            long sensorId = h * 100;
            sensors.Add(new PrtgSensorRow { Objid = sensorId, DeviceObjid = devId, SensorType = "SNMP Disk Free", Paused = false });
            hostMaps.Add(new PrtgHostMapRow { MapDate = anchor, DeviceObjid = devId, HostId = h, HostName = $"HOST-{h}", MapStatus = PrtgMapStatus.Ok, CreatedAt = now });

            for (int d = 0; d < 56; d++)
            {
                var day = anchor.AddDays(-d);
                for (int hour = 0; hour < 12; hour++)
                {
                    values.Add(new PrtgValueRow { SensorObjid = sensorId, PeriodStart = day.AddHours(hour), AvgValue = 40.0, Quality = PrtgDataQuality.Ok });
                }
            }
        }

        store.UpsertSensors(sensors, now);
        store.ReplaceHostMapForDate(anchor, hostMaps);
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Sufficient, summary.PrtgValueBaseline.Status);
        Assert.Equal("充足", summary.PrtgValueBaseline.StatusText);
        Assert.Equal(10, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["HostsReachingSufficient"]));
    }

    // ── 2. PRTG 規則門檻：四種狀態測試 ─────────────────────────────────

    [Fact]
    public void AssessStatus_規則門檻_PRTG未啟用或鏡像無Sensor_無法取得()
    {
        var service = CreateService();

        _settingsStore.Update(s => s.PrtgEnabled = false);
        var summary1 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary1.PrtgRuleThresholds.Status);
        Assert.Equal("無法取得", summary1.PrtgRuleThresholds.StatusText);

        _settingsStore.Update(s => s.PrtgEnabled = true);
        var summary2 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary2.PrtgRuleThresholds.Status);
    }

    [Fact]
    public void AssessStatus_規則門檻_變更天數或命中筆數未達標_不足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 狀態變更涵蓋 10 天（<28 天）
        var changes = new List<PrtgStateChangeRow>();
        for (int i = 0; i < 10; i++)
        {
            changes.Add(new PrtgStateChangeRow
            {
                SensorObjid = 1001,
                ChangedAt = anchor.AddDays(-i).AddHours(8),
                Status = "Down",
                Quality = "Good"
            });
        }
        store.AppendStateChanges(changes);

        // 寫入 15 筆 PRTG 規則命中（<30 筆）
        using (var ctx = _fx.NewContext())
        {
            var dr = new DailyRecordRow { HostId = 1, HostName = "HOST-1", RecordDate = anchor, RiskLevel = "中", ContentJson = "{}" };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            for (int i = 0; i < 15; i++)
            {
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId,
                    HostId = 1,
                    RecordDate = anchor,
                    SourceName = "PRTG",
                    EventId = 0,
                    EventKey = $"prtg:{PrtgRuleEvaluator.RuleDown}:{1000 + i}",
                    Category = "Service",
                    SeverityRank = 2
                });
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Insufficient, summary.PrtgRuleThresholds.Status);
        Assert.Equal(10, Convert.ToInt32(summary.PrtgRuleThresholds.KeyMetrics["DistinctCoverageDays"]));
        Assert.Equal(15, Convert.ToInt32(summary.PrtgRuleThresholds.KeyMetrics["TotalRuleHits"]));
    }

    [Fact]
    public void AssessStatus_規則門檻_涵蓋28天且命中30筆_可用()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        var changes = new List<PrtgStateChangeRow>();
        for (int i = 0; i < 30; i++)
        {
            changes.Add(new PrtgStateChangeRow
            {
                SensorObjid = 1001,
                ChangedAt = anchor.AddDays(-i).AddHours(8),
                Status = "Down",
                Quality = "Good"
            });
        }
        store.AppendStateChanges(changes);

        using (var ctx = _fx.NewContext())
        {
            var dr = new DailyRecordRow { HostId = 1, HostName = "HOST-1", RecordDate = anchor, RiskLevel = "中", ContentJson = "{}" };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            for (int i = 0; i < 35; i++)
            {
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId,
                    HostId = 1,
                    RecordDate = anchor.AddDays(-i % 30),
                    SourceName = "PRTG",
                    EventId = 0,
                    EventKey = $"prtg:{PrtgRuleEvaluator.RuleDown}:{1000 + i}",
                    Category = "Service",
                    SeverityRank = 2
                });
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Available, summary.PrtgRuleThresholds.Status);
        Assert.Equal("可用", summary.PrtgRuleThresholds.StatusText);
    }

    /// <summary>
    /// 判定快取：同一 anchor 在 TTL 內重複呼叫回同一份結果（四項判定是重查詢，
    /// 含逐筆反序列化，而累積量以「天」為單位變動）；forceRefresh 會略過快取。
    /// </summary>
    [Fact]
    public void AssessStatus_同一錨定日在TTL內回快取_forceRefresh則重算()
    {
        var anchor = new DateTime(2026, 8, 31);
        var service = CreateService();

        var first = service.AssessStatus(anchor);
        var second = service.AssessStatus(anchor);
        Assert.Same(first, second);

        var forced = service.AssessStatus(anchor, forceRefresh: true);
        Assert.NotSame(first, forced);
        Assert.Equal(first.PrtgValueBaseline.Status, forced.PrtgValueBaseline.Status);

        // 換一個錨定日不得回上一個的快取
        var other = service.AssessStatus(anchor.AddDays(-1));
        Assert.NotSame(forced, other);
    }

    /// <summary>
    /// 門檻校準是逐規則進行的：四條加總會讓「down 只有 3 筆但 flapping 有 100 筆」
    /// 被誤判成資料充足，而 down 的門檻其實仍然無從校準。
    /// </summary>
    [Fact]
    public void AssessStatus_規則門檻_命中集中在單一規則時不得以四條加總判定達標()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 狀態變更涵蓋 30 天（滿足可用的 28 天）
        var changes = new List<PrtgStateChangeRow>();
        for (int i = 0; i < 30; i++)
        {
            changes.Add(new PrtgStateChangeRow
            {
                SensorObjid = 1001,
                ChangedAt = anchor.AddDays(-i).AddHours(8),
                Status = "Down",
                Quality = "Good"
            });
        }
        store.AppendStateChanges(changes);

        using (var ctx = _fx.NewContext())
        {
            var dr = new DailyRecordRow { HostId = 1, HostName = "HOST-1", RecordDate = anchor, RiskLevel = "中", ContentJson = "{}" };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            // down 只有 3 筆（遠低於 30），flapping 有 40 筆——四條加總 43 筆會超過門檻
            for (int i = 0; i < 3; i++)
            {
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId, HostId = 1, RecordDate = anchor.AddDays(-i),
                    SourceName = "PRTG", EventId = 0,
                    EventKey = $"prtg:{PrtgRuleEvaluator.RuleDown}:{2000 + i}",
                    Category = "Service", SeverityRank = 2
                });
            }
            for (int i = 0; i < 40; i++)
            {
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId, HostId = 1, RecordDate = anchor.AddDays(-i % 30),
                    SourceName = "PRTG", EventId = 0,
                    EventKey = $"prtg:{PrtgRuleEvaluator.RuleFlapping}:{3000 + i}",
                    Category = "Service", SeverityRank = 3
                });
            }
            ctx.SaveChanges();
        }

        var summary = CreateService().AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Insufficient, summary.PrtgRuleThresholds.Status);
        Assert.Equal(3, summary.PrtgRuleThresholds.KeyMetrics["DownSensorDays"]);
        Assert.Equal(40, summary.PrtgRuleThresholds.KeyMetrics["FlappingSensorDays"]);
        Assert.Equal(43, summary.PrtgRuleThresholds.KeyMetrics["TotalRuleHits"]);
    }

    [Fact]
    public void AssessStatus_規則門檻_涵蓋56天且命中100筆_充足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        var changes = new List<PrtgStateChangeRow>();
        for (int i = 0; i < 56; i++)
        {
            changes.Add(new PrtgStateChangeRow
            {
                SensorObjid = 1001,
                ChangedAt = anchor.AddDays(-i).AddHours(8),
                Status = "Down",
                Quality = "Good"
            });
        }
        store.AppendStateChanges(changes);

        using (var ctx = _fx.NewContext())
        {
            var dr = new DailyRecordRow { HostId = 1, HostName = "HOST-1", RecordDate = anchor, RiskLevel = "中", ContentJson = "{}" };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            for (int i = 0; i < 110; i++)
            {
                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId,
                    HostId = 1,
                    RecordDate = anchor.AddDays(-i % 56),
                    SourceName = "PRTG",
                    EventId = 0,
                    EventKey = $"prtg:{PrtgRuleEvaluator.RuleDown}:{1000 + i}",
                    Category = "Service",
                    SeverityRank = 2
                });
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Sufficient, summary.PrtgRuleThresholds.Status);
        Assert.Equal("充足", summary.PrtgRuleThresholds.StatusText);
    }

    // ── 3. 觸發式取數量級：四種狀態測試 ─────────────────────────────────

    [Fact]
    public void AssessStatus_觸發式量級_PRTG未啟用或鏡像無Sensor_無法取得()
    {
        var service = CreateService();

        _settingsStore.Update(s => s.PrtgEnabled = false);
        var summary1 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary1.TriggeredFetchMagnitude.Status);

        _settingsStore.Update(s => s.PrtgEnabled = true);
        var summary2 = service.AssessStatus(new DateTime(2026, 8, 31));
        Assert.Equal(CalibrationStatus.Unavailable, summary2.TriggeredFetchMagnitude.Status);
    }

    [Fact]
    public void AssessStatus_觸發式量級_有數值天數未達14天_不足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 8 天有數值（<14 天）
        var values = new List<PrtgValueRow>();
        for (int i = 0; i < 8; i++)
        {
            values.Add(new PrtgValueRow
            {
                SensorObjid = 2001,
                PeriodStart = anchor.AddDays(-i).AddHours(2),
                AvgValue = 30.0,
                Quality = PrtgDataQuality.Ok
            });
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Insufficient, summary.TriggeredFetchMagnitude.Status);
        Assert.Equal(8, Convert.ToInt32(summary.TriggeredFetchMagnitude.KeyMetrics["DaysWithValues"]));
    }

    [Fact]
    public void AssessStatus_觸發式量級_有數值天數達14天_可用()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 18 天有數值（≥14 天，<28 天）
        var values = new List<PrtgValueRow>();
        for (int i = 0; i < 18; i++)
        {
            values.Add(new PrtgValueRow
            {
                SensorObjid = 2001,
                PeriodStart = anchor.AddDays(-i).AddHours(2),
                AvgValue = 30.0,
                Quality = PrtgDataQuality.Ok
            });
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Available, summary.TriggeredFetchMagnitude.Status);
        Assert.Equal("可用", summary.TriggeredFetchMagnitude.StatusText);
    }

    [Fact]
    public void AssessStatus_觸發式量級_有數值天數達28天_充足()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 2001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        // 29 天有數值（≥28 天）
        var values = new List<PrtgValueRow>();
        for (int i = 0; i < 29; i++)
        {
            values.Add(new PrtgValueRow
            {
                SensorObjid = 2001,
                PeriodStart = anchor.AddDays(-i).AddHours(2),
                AvgValue = 30.0,
                Quality = PrtgDataQuality.Ok
            });
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Sufficient, summary.TriggeredFetchMagnitude.Status);
        Assert.Equal("充足", summary.TriggeredFetchMagnitude.StatusText);
    }

    // ── 4. 殘留判定門檻：四種狀態測試 ─────────────────────────────────

    [Fact]
    public void AssessStatus_殘留判定_保留期內無候選_無法取得()
    {
        var service = CreateService();
        var summary = service.AssessStatus(new DateTime(2026, 8, 31));

        Assert.Equal(CalibrationStatus.Unavailable, summary.ResidualCredentialThresholds.Status);
        Assert.Equal("無法取得", summary.ResidualCredentialThresholds.StatusText);
        Assert.Contains(summary.ResidualCredentialThresholds.Explanations, s => s.Contains("請確認 4625／4771 與 Linux 登入失敗規則為啟用狀態"));
    }

    [Fact]
    public void AssessStatus_殘留判定_候選主機日數或天數未達標_不足()
    {
        var anchor = new DateTime(2026, 8, 31);
        using (var ctx = _fx.NewContext())
        {
            // 造 50 個候選主機日，涵蓋 5 天（<200 主機日，<14 天）
            for (int h = 1; h <= 10; h++)
            {
                for (int d = 0; d < 5; d++)
                {
                    var date = anchor.AddDays(-d);
                    var record = new DailyAnalysisRecord
                    {
                        HostId = h,
                        Host = $"HOST-{h}",
                        Date = date,
                        RiskLevel = "高",
                        TopIssues = new List<LogIssueSignature>
                        {
                            new()
                            {
                                Source = "Microsoft-Windows-Security-Auditing",
                                EventId = 4625,
                                LoginFailureDetails = new List<LoginFailureDetail>
                                {
                                    new() { Account = "svc_test", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 10 }
                                }
                            }
                        }
                    };

                    var dr = new DailyRecordRow
                    {
                        HostId = h,
                        HostName = $"HOST-{h}",
                        RecordDate = date,
                        RiskLevel = "高",
                        DetailPruned = false,
                        ContentJson = JsonSerializer.Serialize(record)
                    };
                    ctx.DailyRecords.Add(dr);
                    ctx.SaveChanges();

                    ctx.TopIssues.Add(new TopIssueRow
                    {
                        RecordId = dr.RecordId,
                        HostId = h,
                        RecordDate = date,
                        SourceName = "Microsoft-Windows-Security-Auditing",
                        EventId = 4625
                    });
                }
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Insufficient, summary.ResidualCredentialThresholds.Status);
        Assert.Equal("不足", summary.ResidualCredentialThresholds.StatusText);
        Assert.Equal(50, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["CandidateHostDays"]));
        Assert.Equal(5, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["DistinctCoverageDays"]));
    }

    [Fact]
    public void AssessStatus_殘留判定_達200主機日且涵蓋14天_可用()
    {
        var anchor = new DateTime(2026, 8, 31);
        using (var ctx = _fx.NewContext())
        {
            // 20 台主機 × 15 天 = 300 主機日（≥200 主機日，≥14 天，<1000 主機日）
            for (int h = 1; h <= 20; h++)
            {
                for (int d = 0; d < 15; d++)
                {
                    var date = anchor.AddDays(-d);
                    var record = new DailyAnalysisRecord
                    {
                        HostId = h,
                        Host = $"HOST-{h}",
                        Date = date,
                        RiskLevel = "高",
                        TopIssues = new List<LogIssueSignature>
                        {
                            new()
                            {
                                Source = "Microsoft-Windows-Security-Auditing",
                                EventId = 4625,
                                LoginFailureDetails = new List<LoginFailureDetail>
                                {
                                    new() { Account = "svc_test", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 10 }
                                }
                            }
                        }
                    };

                    var dr = new DailyRecordRow
                    {
                        HostId = h,
                        HostName = $"HOST-{h}",
                        RecordDate = date,
                        RiskLevel = "高",
                        DetailPruned = false,
                        ContentJson = JsonSerializer.Serialize(record)
                    };
                    ctx.DailyRecords.Add(dr);
                    ctx.SaveChanges();

                    ctx.TopIssues.Add(new TopIssueRow
                    {
                        RecordId = dr.RecordId,
                        HostId = h,
                        RecordDate = date,
                        SourceName = "Microsoft-Windows-Security-Auditing",
                        EventId = 4625
                    });
                }
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Available, summary.ResidualCredentialThresholds.Status);
        Assert.Equal("可用", summary.ResidualCredentialThresholds.StatusText);
        Assert.Equal(300, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["CandidateHostDays"]));
        Assert.Equal(15, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["DistinctCoverageDays"]));
    }

    [Fact]
    public void AssessStatus_殘留判定_達1000主機日且涵蓋28天_充足()
    {
        var anchor = new DateTime(2026, 8, 31);
        using (var ctx = _fx.NewContext())
        {
            // 40 台主機 × 30 天 = 1200 主機日（≥1000 主機日，≥28 天）
            for (int h = 1; h <= 40; h++)
            {
                for (int d = 0; d < 30; d++)
                {
                    var date = anchor.AddDays(-d);
                    var record = new DailyAnalysisRecord
                    {
                        HostId = h,
                        Host = $"HOST-{h}",
                        Date = date,
                        RiskLevel = "高",
                        TopIssues = new List<LogIssueSignature>
                        {
                            new()
                            {
                                Source = "Microsoft-Windows-Security-Auditing",
                                EventId = 4625,
                                LoginFailureDetails = new List<LoginFailureDetail>
                                {
                                    new() { Account = "svc_test", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 10 }
                                }
                            }
                        }
                    };

                    var dr = new DailyRecordRow
                    {
                        HostId = h,
                        HostName = $"HOST-{h}",
                        RecordDate = date,
                        RiskLevel = "高",
                        DetailPruned = false,
                        ContentJson = JsonSerializer.Serialize(record)
                    };
                    ctx.DailyRecords.Add(dr);
                    ctx.SaveChanges();

                    ctx.TopIssues.Add(new TopIssueRow
                    {
                        RecordId = dr.RecordId,
                        HostId = h,
                        RecordDate = date,
                        SourceName = "Microsoft-Windows-Security-Auditing",
                        EventId = 4625
                    });
                }
            }
            ctx.SaveChanges();
        }

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        Assert.Equal(CalibrationStatus.Sufficient, summary.ResidualCredentialThresholds.Status);
        Assert.Equal("充足", summary.ResidualCredentialThresholds.StatusText);
        Assert.Equal(1200, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["CandidateHostDays"]));
        Assert.Equal(30, Convert.ToInt32(summary.ResidualCredentialThresholds.KeyMetrics["DistinctCoverageDays"]));
    }

    // ── 5. 分母為零判不足測試（突變驗證核心） ─────────────────────────

    [Fact]
    public void AssessStatus_分母為零或無任何資料時一律判不足而非可用()
    {
        // 模擬 PRTG 已啟用、鏡像有 sensor，但「完全沒有任何時序資料」（零數值、零狀態變更、零命中、零候選）
        var store = new EfPrtgStore(_fx.NewContext);
        var now = DateTime.Now;
        var anchor = new DateTime(2026, 8, 31);

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 9001, DeviceObjid = 1, SensorType = "SNMP Disk Free", Paused = false }
        }, now);
        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 1, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        // 1. 值型基線：有 sensor 有對應主機，但數值列數為 0 → 必須為「不足」，絕不得為「可用」或「充足」
        Assert.Equal(CalibrationStatus.Insufficient, summary.PrtgValueBaseline.Status);
        Assert.NotEqual(CalibrationStatus.Available, summary.PrtgValueBaseline.Status);
        Assert.NotEqual(CalibrationStatus.Sufficient, summary.PrtgValueBaseline.Status);

        // 2. 規則門檻：無任何狀態變更與規則命中 → 必須為「不足」，絕不得為「可用」或「充足」
        Assert.Equal(CalibrationStatus.Insufficient, summary.PrtgRuleThresholds.Status);
        Assert.NotEqual(CalibrationStatus.Available, summary.PrtgRuleThresholds.Status);
        Assert.NotEqual(CalibrationStatus.Sufficient, summary.PrtgRuleThresholds.Status);

        // 3. 觸發式取數量級：無任何數值紀錄 → 必須為「不足」，絕不得為「可用」或「充足」
        Assert.Equal(CalibrationStatus.Insufficient, summary.TriggeredFetchMagnitude.Status);
        Assert.NotEqual(CalibrationStatus.Available, summary.TriggeredFetchMagnitude.Status);
        Assert.NotEqual(CalibrationStatus.Sufficient, summary.TriggeredFetchMagnitude.Status);

        // 4. 殘留判定門檻：無候選紀錄 → 為「無法取得」
        Assert.Equal(CalibrationStatus.Unavailable, summary.ResidualCredentialThresholds.Status);
    }

    // ── 6. 主機層涵蓋天數取 sensor 最大值 ──────────────────────────────

    [Fact]
    public void AssessStatus_主機層涵蓋天數取Sensor的最大值()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 同一台主機 HOST-1（Device 10）底下有兩個 sensor：
        // Sensor 101 涵蓋 10 天
        // Sensor 102 涵蓋 30 天
        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 101, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false },
            new() { Objid = 102, DeviceObjid = 10, SensorType = "SNMP CPU Load", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        var values = new List<PrtgValueRow>();
        // Sensor 101: 10 天
        for (int d = 0; d < 10; d++)
        {
            var day = anchor.AddDays(-d);
            for (int h = 0; h < 12; h++)
            {
                values.Add(new PrtgValueRow { SensorObjid = 101, PeriodStart = day.AddHours(h), AvgValue = 10.0, Quality = PrtgDataQuality.Ok });
            }
        }
        // Sensor 102: 30 天
        for (int d = 0; d < 30; d++)
        {
            var day = anchor.AddDays(-d);
            for (int h = 0; h < 12; h++)
            {
                values.Add(new PrtgValueRow { SensorObjid = 102, PeriodStart = day.AddHours(h), AvgValue = 20.0, Quality = PrtgDataQuality.Ok });
            }
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        // 主機最大涵蓋天數應為 30 天（不是 10 天，也不是 40 天）
        Assert.Equal(30, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["MaxCoverageDays"]));
    }

    // ── 7. 白名單留空＝不限制 ────────────────────────────────────────

    [Fact]
    public void AssessStatus_白名單留空時納入全部Sensor()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        // 清空白名單（留空＝不限制）
        _settingsStore.Update(s => s.PrtgSensorTypeWhitelist = new List<string>());

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 501, DeviceObjid = 1, SensorType = "Custom Ping Sensor", Paused = false },
            new() { Objid = 502, DeviceObjid = 1, SensorType = "Custom HTTP Sensor", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 1, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        // 白名單為空時，2 個 sensor 全部被納入計算
        Assert.Equal(2, Convert.ToInt32(summary.PrtgValueBaseline.KeyMetrics["WhitelistedSensors"]));
    }

    // ── 8. 補充說明包含實際數字 ──────────────────────────────────────

    [Fact]
    public void AssessStatus_補充說明包含實際數字與所需值()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        _settingsStore.Update(s => s.PrtgRetentionDays = 30); // 小於充足所需 56 天

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 101, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        var values = new List<PrtgValueRow>();
        for (int d = 0; d < 8; d++)
        {
            var day = anchor.AddDays(-d);
            for (int h = 0; h < 12; h++)
            {
                values.Add(new PrtgValueRow { SensorObjid = 101, PeriodStart = day.AddHours(h), AvgValue = 10.0, Quality = PrtgDataQuality.Ok });
            }
        }
        store.UpsertValues(values);

        var service = CreateService();
        var summary = service.AssessStatus(anchor);

        var explanations = summary.PrtgValueBaseline.Explanations;
        Assert.Contains(explanations, s => s.Contains("30 天") && s.Contains("56 天"));
        Assert.Contains(explanations, s => s.Contains("目前只有 1 台主機有基線資料，需要 10 台"));
        Assert.Contains(explanations, s => s.Contains("目前最長涵蓋 8 天，還需要約 20 天"));
    }

    // ── 9. 殘留資料集不含帳號名稱 ────────────────────────────────────

    /// <summary>
    /// 規則門檻資料集的核心：以最低門檻逐日評估取得 magnitude 分佈。
    /// 只有「現行門檻下命中幾次」無法回答「門檻該設多少」——那正是這一頁存在的理由。
    /// </summary>
    [Fact]
    public void BuildExportPackage_規則門檻資料集含最低門檻評估的magnitude分佈與分位數()
    {
        var anchor = new DateTime(2026, 8, 31);
        var store = new EfPrtgStore(_fx.NewContext);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false },
            new() { Objid = 1002, DeviceObjid = 10, SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        store.AppendStateChanges(new List<PrtgStateChangeRow>
        {
            // 08:00 進入 Down 且日終未恢復 → magnitude 960 分鐘（現行門檻 60 也命中）
            new() { SensorObjid = 1001, ChangedAt = anchor.AddHours(8), Status = "Down", Quality = "Good" },
            // 23:30 才進入 Down → magnitude 30 分鐘，**低於現行門檻 60**。
            // 校準要看的正是這種「目前不會報、但門檻調低就會報」的樣本；
            // 若評估時用現行門檻而非最低門檻，這筆會整個消失。
            new() { SensorObjid = 1002, ChangedAt = anchor.AddHours(23).AddMinutes(30), Status = "Down", Quality = "Good" }
        });

        var package = CreateService().BuildExportPackage(anchor);
        var dataset = package.RuleThresholds;

        var downSamples = dataset.MagnitudeSamples
            .Where(r => r.RuleCode == PrtgRuleEvaluator.RuleDown)
            .OrderBy(r => r.Magnitude)
            .ToList();
        Assert.Equal(2, downSamples.Count);
        Assert.Equal(30, downSamples[0].Magnitude);
        Assert.Equal(1002, downSamples[0].SensorObjid);
        Assert.Equal(960, downSamples[1].Magnitude);
        Assert.Equal(anchor.Date, downSamples[1].Date);

        var summary = dataset.MagnitudeSummaries.Single(x => x.RuleCode == PrtgRuleEvaluator.RuleDown);
        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(30, summary.Min);
        Assert.Equal(960, summary.Max);
        Assert.Equal(PrtgRuleCatalog.DefaultDownMinutes, summary.CurrentThreshold);
        // 兩筆樣本中只有一筆達現行門檻——這個對比就是校準的依據
        Assert.Equal(1, summary.HitsAtCurrentThreshold);
    }

    /// <summary>
    /// IsMatch 要能為 true：條件 4（跨日重現）需要 history，若匯出端不撈歷史紀錄，
    /// 這一欄會整欄恆為 false，下一輪拿它校準會得到「現行門檻命中率 0%」的錯誤結論。
    /// </summary>
    [Fact]
    public void BuildExportPackage_跨日重現的候選_IsMatch為true()
    {
        var anchor = new DateTime(2026, 8, 31);

        LogIssueSignature Sig() => new()
        {
            LogName = "Security",
            Source = "Microsoft-Windows-Security-Auditing",
            EventId = 4625,
            Count = 50,
            LoginFailureDetails = new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS-01", LogonType = 3, ReasonCode = "bad_password", Count = 50 }
            },
            LoginFailureTotalCount = 50
        };

        using (var ctx = _fx.NewContext())
        {
            // 前一日同一組 (帳號, 來源) 也有登入失敗 → 條件 4 成立
            foreach (var date in new[] { anchor.AddDays(-1), anchor })
            {
                var record = new DailyAnalysisRecord
                {
                    HostId = 101, Host = "SEC-SRV-01", Date = date, RiskLevel = "高",
                    TopIssues = new List<LogIssueSignature> { Sig() }
                };
                var dr = new DailyRecordRow
                {
                    HostId = 101, HostName = "SEC-SRV-01", RecordDate = date, RiskLevel = "高",
                    DetailPruned = false, ContentJson = JsonSerializer.Serialize(record)
                };
                ctx.DailyRecords.Add(dr);
                ctx.SaveChanges();

                ctx.TopIssues.Add(new TopIssueRow
                {
                    RecordId = dr.RecordId, HostId = 101, RecordDate = date,
                    SourceName = "Microsoft-Windows-Security-Auditing", EventId = 4625
                });
                ctx.SaveChanges();
            }
        }

        var package = CreateService().BuildExportPackage(anchor);

        var todays = package.ResidualCandidates.Single(c => c.Date.Date == anchor.Date);
        Assert.True(todays.IsMatch, "跨日重現成立時 IsMatch 應為 true；恆 false 代表 history 沒有被傳入");
        Assert.Equal(1.0, todays.ConcentrationRatio);
    }

    [Fact]
    public void BuildExportPackage_殘留資料集不含任何帳號名稱()
    {
        var anchor = new DateTime(2026, 8, 31);
        const string secretAccount = "victim_admin_user_secret";

        using (var ctx = _fx.NewContext())
        {
            var record = new DailyAnalysisRecord
            {
                HostId = 101,
                Host = "SEC-SRV-01",
                Date = anchor,
                RiskLevel = "高",
                TopIssues = new List<LogIssueSignature>
                {
                    new()
                    {
                        LogName = "Security",
                        Source = "Microsoft-Windows-Security-Auditing",
                        EventId = 4625,
                        LoginFailureDetails = new List<LoginFailureDetail>
                        {
                            new() { Account = secretAccount, Source = "192.168.1.100", LogonType = 3, ReasonCode = "bad_password", Count = 50 }
                        }
                    }
                }
            };

            var dr = new DailyRecordRow
            {
                HostId = 101,
                HostName = "SEC-SRV-01",
                RecordDate = anchor,
                RiskLevel = "高",
                DetailPruned = false,
                ContentJson = JsonSerializer.Serialize(record)
            };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = dr.RecordId,
                HostId = 101,
                RecordDate = anchor,
                SourceName = "Microsoft-Windows-Security-Auditing",
                EventId = 4625
            });
            ctx.SaveChanges();
        }

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        // 1. 斷言序列化後的整個 JSON 物件絕不含該帳號字串
        var json = JsonSerializer.Serialize(package);
        Assert.DoesNotContain(secretAccount, json);

        // 2. 斷言指標資料列正確產出且數值正確
        Assert.Single(package.ResidualCandidates);
        var candidate = package.ResidualCandidates[0];
        Assert.Equal(101, candidate.HostId);
        Assert.Equal("SEC-SRV-01", candidate.HostName);
        Assert.Equal(4625, candidate.EventId);
        Assert.Equal(1, candidate.CandidateGroupCount);
        Assert.Equal(50, candidate.TotalDetailCount);
        Assert.Equal(1.0, candidate.ConcentrationRatio);
        Assert.Equal(1.0, candidate.MechanicalLogonTypeRatio);
        Assert.Equal(1.0, candidate.SingleGroupRatio);
        Assert.False(candidate.IsTruncated);
    }

    // ── 10. 精簡紀錄被跳過 ──────────────────────────────────────────

    [Fact]
    public void BuildExportPackage_已精簡紀錄DetailPruned被跳過()
    {
        var anchor = new DateTime(2026, 8, 31);

        using (var ctx = _fx.NewContext())
        {
            var dr = new DailyRecordRow
            {
                HostId = 201,
                HostName = "PRUNED-SRV",
                RecordDate = anchor,
                RiskLevel = "高",
                DetailPruned = true, // 已精簡
                ContentJson = string.Empty
            };
            ctx.DailyRecords.Add(dr);
            ctx.SaveChanges();

            ctx.TopIssues.Add(new TopIssueRow
            {
                RecordId = dr.RecordId,
                HostId = 201,
                RecordDate = anchor,
                SourceName = "Microsoft-Windows-Security-Auditing",
                EventId = 4625
            });
            ctx.SaveChanges();
        }

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        // 已精簡紀錄的明細已不在，必須被跳過
        Assert.Empty(package.ResidualCandidates);
    }

    // ── 11. 匯出包完整組裝測試 ────────────────────────────────────────

    [Fact]
    public void BuildExportPackage_完整組裝_包含版本時間摘要與四大資料集()
    {
        var store = new EfPrtgStore(_fx.NewContext);
        var anchor = new DateTime(2026, 8, 31);
        var now = DateTime.Now;

        store.UpsertSensors(new List<PrtgSensorRow>
        {
            new() { Objid = 1001, DeviceObjid = 10, Name = "Disk Sensor", SensorType = "SNMP Disk Free", Paused = false }
        }, now);

        store.ReplaceHostMapForDate(anchor, new List<PrtgHostMapRow>
        {
            new() { MapDate = anchor, DeviceObjid = 10, HostId = 1, HostName = "HOST-1", MapStatus = PrtgMapStatus.Ok, CreatedAt = now }
        });

        store.UpsertValues(new List<PrtgValueRow>
        {
            new() { SensorObjid = 1001, PeriodStart = anchor.AddHours(2), AvgValue = 55.0, MinValue = 50.0, MaxValue = 60.0, Quality = PrtgDataQuality.Ok }
        });

        var service = CreateService();
        var package = service.BuildExportPackage(anchor);

        Assert.Equal(CalibrationConstants.CurrentFormatVersion, package.FormatVersion);
        Assert.NotNull(package.Summary);
        Assert.NotNull(package.Summary.PrtgValueBaseline);
        Assert.NotNull(package.Summary.PrtgRuleThresholds);
        Assert.NotNull(package.Summary.TriggeredFetchMagnitude);
        Assert.NotNull(package.Summary.ResidualCredentialThresholds);

        // 值型基線資料集
        Assert.Single(package.ValueBaselines);
        var vb = package.ValueBaselines[0];
        Assert.Equal(1001, vb.SensorObjid);
        Assert.Equal(10, vb.DeviceObjid);
        Assert.Equal(1, vb.HostId);
        Assert.Equal("HOST-1", vb.HostName);
        Assert.Equal("SNMP Disk Free", vb.SensorType);
        Assert.Equal(55.0, vb.AvgValue);

        // 規則門檻資料集（包含 4 條預設規則門檻現值）
        Assert.NotNull(package.RuleThresholds);
        Assert.Equal(4, package.RuleThresholds.CurrentRules.Count);
        Assert.Contains(package.RuleThresholds.CurrentRules, r => r.RuleCode == "down");
        Assert.Contains(package.RuleThresholds.CurrentRules, r => r.RuleCode == "flapping");
        Assert.Contains(package.RuleThresholds.CurrentRules, r => r.RuleCode == "warning");
        Assert.Contains(package.RuleThresholds.CurrentRules, r => r.RuleCode == "silent");

        // 觸發式量級資料集
        Assert.Single(package.TriggeredMagnitudes);
        Assert.Equal(anchor.Date, package.TriggeredMagnitudes[0].Date);
    }
}

public class CalibrationControllerTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hostStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeRuleStore _ruleStore = new();
    private readonly RecordingAuditService _audit = new();

    public CalibrationControllerTests()
    {
        _settingsStore.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgRetentionDays = 180;
            s.RawEventRetentionDays = 120;
            s.PrtgSensorTypeWhitelist = new List<string> { "SNMP Disk Free" };
        });
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private CalibrationController CreateController()
    {
        var prtgStore = new EfPrtgStore(_fx.NewContext);
        var issueQuery = new EfIssueAggregateQuery(_fx.NewContext, _hostStore);
        var service = new CalibrationService(_fx.NewContext, prtgStore, issueQuery, _settingsStore, _ruleStore);
        return new CalibrationController(service, _audit);
    }

    // ── 1. 授權：非 Maintain 角色打兩個端點皆被拒 ───────────────────────

    [Fact]
    public void CalibrationController_標註Maintain能力()
    {
        var attr = typeof(CalibrationController).GetCustomAttributes(typeof(PermissionAttribute), true)
            .Cast<PermissionAttribute>()
            .FirstOrDefault();
        Assert.NotNull(attr);
        Assert.NotNull(attr.Arguments);
        Assert.Equal(new[] { Capability.Maintain }, attr.Arguments[0]);
    }

    [Theory]
    [InlineData(Capability.Handle)]
    [InlineData(Capability.Assign)]
    [InlineData(Capability.ViewAll)]
    [InlineData(Capability.DevMonitor)]
    [InlineData(Capability.ConfirmPermission)]
    [InlineData(Capability.ViewAudit)]
    public void 非Maintain角色_存取Controller端點被PermissionFilter拒絕(Capability cap)
    {
        var user = FakeCurrentUser.WithCapabilities(cap);
        var filter = new PermissionFilter(new[] { Capability.Maintain }, user, _audit);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/admin/calibration/status";
        httpContext.Request.Method = "GET";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        filter.OnAuthorization(filterContext);

        var result = Assert.IsType<ObjectResult>(filterContext.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        var apiRes = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.False(apiRes.Success);
        Assert.Equal(ApiErrorCodes.Forbidden, apiRes.Error?.Code);
    }

    [Fact]
    public void Maintain角色_通過PermissionFilter()
    {
        var user = FakeCurrentUser.WithCapabilities(Capability.Maintain);
        var filter = new PermissionFilter(new[] { Capability.Maintain }, user, _audit);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/admin/calibration/status";
        httpContext.Request.Method = "GET";

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var filterContext = new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());

        filter.OnAuthorization(filterContext);

        Assert.Null(filterContext.Result);
    }

    // ── 2. 匯出閘門：四項未全部達標且未帶 override → 擲驗證例外；帶 override=true → 放行 ─

    [Fact]
    public void 匯出閘門_四項未達標且未覆寫_擲驗證例外()
    {
        var controller = CreateController();

        // 預設無資料時四項皆不足/無法取得
        var ex = Assert.Throws<DomainException>(() => controller.Export(isOverride: false));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("校準資料累積量未達標", ex.Message);
        Assert.Contains("仍要匯出", ex.Message);
    }

    [Fact]
    public void 匯出閘門_四項未達標但帶override_放行並回傳檔案()
    {
        var controller = CreateController();

        var result = controller.Export(isOverride: true);
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", fileResult.ContentType);
        Assert.StartsWith("calibration-", fileResult.FileDownloadName);
        Assert.EndsWith(".json", fileResult.FileDownloadName);
        Assert.NotEmpty(fileResult.FileContents);
    }

    // ── 3. 稽核：匯出成功寫稽核，覆寫匯出標記 Override = true ──────────────

    [Fact]
    public void 匯出稽核_覆寫匯出成功記錄稽核包含四項狀態與Override旗標()
    {
        var controller = CreateController();

        controller.Export(isOverride: true);

        var auditEntry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.CalibrationExport, auditEntry.Action);
        Assert.Equal("calibration_data", auditEntry.TargetKind);
        Assert.Contains("PRTG值型基線", auditEntry.Summary);
        Assert.Contains("PRTG規則門檻", auditEntry.Summary);
        Assert.Contains("觸發式取數量級", auditEntry.Summary);
        Assert.Contains("殘留判定門檻", auditEntry.Summary);
        Assert.Contains("覆寫匯出", auditEntry.Summary);

        Assert.NotNull(auditEntry.DetailJson);
        using var doc = JsonDocument.Parse(auditEntry.DetailJson!);
        Assert.True(doc.RootElement.GetProperty("Override").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("PrtgValueBaseline", out _));
        Assert.True(doc.RootElement.TryGetProperty("PrtgRuleThresholds", out _));
        Assert.True(doc.RootElement.TryGetProperty("TriggeredFetchMagnitude", out _));
        Assert.True(doc.RootElement.TryGetProperty("ResidualCredentialThresholds", out _));
    }

    [Fact]
    public void 匯出稽核_閘門阻擋失敗時不寫入匯出稽核()
    {
        var controller = CreateController();

        Assert.Throws<DomainException>(() => controller.Export(isOverride: false));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void GetStatus_正常回傳四項判定狀態與CanExport()
    {
        var controller = CreateController();

        var response = controller.GetStatus();
        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.NotNull(response.Data);

        var data = response.Data!;
        Assert.NotNull(data.PrtgValueBaseline);
        Assert.NotNull(data.PrtgRuleThresholds);
        Assert.NotNull(data.TriggeredFetchMagnitude);
        Assert.NotNull(data.ResidualCredentialThresholds);
        Assert.False(data.CanExport);
    }
}

