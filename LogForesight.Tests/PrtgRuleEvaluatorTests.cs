using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgRuleEvaluatorTests
{
    private readonly DateTime _day = new(2026, 8, 30);

    [Fact]
    public void 持續Down_達門檻產生finding_未達門檻不產生()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // sensor 101: 22:00 進入 Down，持續至當日結束（120 分鐘 >= 60）
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 22, 0, 0), Status = "Down" },
            // sensor 102: 23:30 進入 Down，持續至當日結束（30 分鐘 < 60）
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 23, 30, 0), Status = "Down" }
        };

        var sensorToDevice = new Dictionary<long, long>
        {
            [101] = 1,
            [102] = 1
        };

        var findings = PrtgRuleEvaluator.Evaluate(_day, changes, sensorToDevice, Array.Empty<(long, long, string?)>());

        Assert.Single(findings);
        var f = findings[0];
        Assert.Equal(1, f.DeviceObjid);
        Assert.Equal(101, f.SensorObjid);
        Assert.Equal(PrtgRuleEvaluator.RuleDown, f.RuleCode);
        Assert.Equal(120, f.Magnitude);
        Assert.DoesNotContain(findings, x => x.SensorObjid == 102);
    }

    [Fact]
    public void 跨午夜Down_前一日進入當日整天未恢復_持續分鐘數自當日零時起算()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // 前一日 20:00 進入 Down，當日整天無新變更
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 29, 20, 0, 0), Status = "Down" }
        };

        var sensorToDevice = new Dictionary<long, long> { [101] = 1 };

        var findings = PrtgRuleEvaluator.Evaluate(_day, changes, sensorToDevice, Array.Empty<(long, long, string?)>());

        Assert.Single(findings);
        var f = findings[0];
        Assert.Equal(1, f.DeviceObjid);
        Assert.Equal(101, f.SensorObjid);
        Assert.Equal(PrtgRuleEvaluator.RuleDown, f.RuleCode);
        Assert.Equal(1440, f.Magnitude);
        Assert.Contains("1440", f.Detail);
    }

    [Fact]
    public void 當日稍晚恢復Up_不算持續Down()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 8, 0, 0), Status = "Down" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 18, 0, 0), Status = "Up" }
        };

        var sensorToDevice = new Dictionary<long, long> { [101] = 1 };

        var findings = PrtgRuleEvaluator.Evaluate(_day, changes, sensorToDevice, Array.Empty<(long, long, string?)>());

        Assert.DoesNotContain(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleDown);
    }

    [Fact]
    public void Flapping_往返次數達門檻成立_未達門檻不成立()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // sensor 101: 5 次 Down -> Up 往返（達門檻 5 次）
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 1, 0, 0), Status = "Down" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 2, 0, 0), Status = "Up" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 3, 0, 0), Status = "Down" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 4, 0, 0), Status = "Up" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 5, 0, 0), Status = "Down" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 6, 0, 0), Status = "Up" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 7, 0, 0), Status = "Down" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 8, 0, 0), Status = "Up" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 9, 0, 0), Status = "Down" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 10, 0, 0), Status = "Up" },

            // sensor 102: 4 次 Down -> Up 往返（未達門檻 5 次）
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 1, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 2, 0, 0), Status = "Up" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 3, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 4, 0, 0), Status = "Up" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 5, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 6, 0, 0), Status = "Up" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 7, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 8, 0, 0), Status = "Up" }
        };

        var sensorToDevice = new Dictionary<long, long>
        {
            [101] = 1,
            [102] = 1
        };

        var findings = PrtgRuleEvaluator.Evaluate(_day, changes, sensorToDevice, Array.Empty<(long, long, string?)>());

        Assert.Contains(findings, f => f.SensorObjid == 101 && f.RuleCode == PrtgRuleEvaluator.RuleFlapping && f.Magnitude == 5);
        Assert.DoesNotContain(findings, f => f.SensorObjid == 102 && f.RuleCode == PrtgRuleEvaluator.RuleFlapping);
    }

    [Fact]
    public void 帶括號變體狀態_視為Down前綴比對生效()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // sensor 101: 10:00 進入 Down (Acknowledged)，持續至當日結束（840 分鐘 >= 60）
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 10, 0, 0), Status = "Down (Acknowledged)" },

            // sensor 102: 使用 Down (Partial) 做 flapping 判定
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 1, 0, 0), Status = "Down (Partial)" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 2, 0, 0), Status = "Up" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 3, 0, 0), Status = "Down (Acknowledged)" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 4, 0, 0), Status = "Up" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 5, 0, 0), Status = "down (ack)" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 6, 0, 0), Status = "up" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 7, 0, 0), Status = "DOWN" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 8, 0, 0), Status = "UP" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 9, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 10, 0, 0), Status = "Up" }
        };

        var sensorToDevice = new Dictionary<long, long>
        {
            [101] = 1,
            [102] = 1
        };

        var findings = PrtgRuleEvaluator.Evaluate(_day, changes, sensorToDevice, Array.Empty<(long, long, string?)>());

        Assert.Contains(findings, f => f.SensorObjid == 101 && f.RuleCode == PrtgRuleEvaluator.RuleDown && f.Magnitude == 840);
        Assert.Contains(findings, f => f.SensorObjid == 102 && f.RuleCode == PrtgRuleEvaluator.RuleFlapping && f.Magnitude == 5);
    }

    [Fact]
    public void 持續Warning_累計達門檻成立_同一sensor同時符合Down與Warning兩者皆產生()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // Warning 從 02:00 到 07:00（5 小時 = 300 分鐘 >= 240）
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 2, 0, 0), Status = "Warning" },
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 7, 0, 0), Status = "Up" },
            // Down 從 20:00 至當日結束（4 小時 = 240 分鐘 >= 60）
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 20, 0, 0), Status = "Down" }
        };

        var sensorToDevice = new Dictionary<long, long> { [101] = 1 };

        var findings = PrtgRuleEvaluator.Evaluate(_day, changes, sensorToDevice, Array.Empty<(long, long, string?)>());

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.SensorObjid == 101 && f.RuleCode == PrtgRuleEvaluator.RuleWarning && f.Magnitude == 300);
        Assert.Contains(findings, f => f.SensorObjid == 101 && f.RuleCode == PrtgRuleEvaluator.RuleDown && f.Magnitude == 240);
    }

    [Fact]
    public void 沉默Device_全部未暫停sensor皆Unknown成立_其中一個為Up不成立()
    {
        var sensorStatuses = new List<(long Objid, long DeviceObjid, string? Status)>
        {
            // Device 1: 全部未暫停 sensor 皆為 Unknown 或空值 -> 成立
            (101, 1, "Unknown"),
            (102, 1, ""),
            (103, 1, null),

            // Device 2: 其中一個 sensor 為 Up -> 不成立
            (201, 2, "Unknown"),
            (202, 2, "Up")
        };

        var findings = PrtgRuleEvaluator.Evaluate(
            _day,
            Array.Empty<PrtgStateChangeRow>(),
            new Dictionary<long, long>(),
            sensorStatuses);

        Assert.Single(findings);
        var f = findings[0];
        Assert.Equal(1, f.DeviceObjid);
        Assert.Null(f.SensorObjid);
        Assert.Equal(PrtgRuleEvaluator.RuleSilent, f.RuleCode);
        Assert.Equal(3, f.Magnitude);
        Assert.DoesNotContain(findings, x => x.DeviceObjid == 2);
    }

    [Fact]
    public void 沉默Device_無未暫停sensor不回報_孤兒sensor略過不判定()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // 孤兒 sensor 999: 不在 sensorToDevice 字典中，應略過
            new() { SensorObjid = 999, ChangedAt = new DateTime(2026, 8, 30, 10, 0, 0), Status = "Down" },
            // 合法 sensor 101: 22:00 進入 Down（120 分鐘）
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 22, 0, 0), Status = "Down" }
        };

        var sensorToDevice = new Dictionary<long, long>
        {
            [101] = 1
        };

        // sensorStatuses 完全沒有 Device 3（即 Device 3 沒有未暫停 sensor）
        var sensorStatuses = Array.Empty<(long Objid, long DeviceObjid, string? Status)>();

        var findings = PrtgRuleEvaluator.Evaluate(_day, changes, sensorToDevice, sensorStatuses);

        Assert.Single(findings);
        Assert.Equal(1, findings[0].DeviceObjid);
        Assert.Equal(101, findings[0].SensorObjid);
        Assert.Equal(PrtgRuleEvaluator.RuleDown, findings[0].RuleCode);
        Assert.DoesNotContain(findings, f => f.SensorObjid == 999);
        Assert.DoesNotContain(findings, f => f.DeviceObjid == 3);
    }

    [Fact]
    public void EnabledRuleCodes篩選生效_只啟用Down時其餘三種不產生()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // 觸發 Down (sensor 101: 22:00 進入 Down -> 120 分鐘 >= 60)
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 22, 0, 0), Status = "Down" },
            // 觸發 Flapping (sensor 102: 5 次往返)
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 1, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 2, 0, 0), Status = "Up" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 3, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 4, 0, 0), Status = "Up" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 5, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 6, 0, 0), Status = "Up" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 7, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 8, 0, 0), Status = "Up" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 9, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 10, 0, 0), Status = "Up" },
            // 觸發 Warning (sensor 103: 00:00~06:00 = 360 分鐘 >= 240)
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 0, 0, 0), Status = "Warning" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 6, 0, 0), Status = "Up" }
        };

        var sensorToDevice = new Dictionary<long, long>
        {
            [101] = 1,
            [102] = 1,
            [103] = 1
        };

        var sensorStatuses = new List<(long Objid, long DeviceObjid, string? Status)>
        {
            (201, 2, "Unknown") // 觸發 Silent (device 2)
        };

        var enabledOnlyDown = new HashSet<string> { PrtgRuleEvaluator.RuleDown };

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, new PrtgRuleThresholds(60, 5, 240), enabledOnlyDown);

        Assert.Single(findings);
        Assert.Equal(PrtgRuleEvaluator.RuleDown, findings[0].RuleCode);
        Assert.DoesNotContain(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleFlapping);
        Assert.DoesNotContain(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleWarning);
        Assert.DoesNotContain(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleSilent);
    }

    [Fact]
    public void 自訂門檻生效_DownMinutes設為30時未達60分鐘之案例成立()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // sensor 101: 23:20 進入 Down，持續至當日結束（40 分鐘 < 60 分鐘，但 >= 30 分鐘）
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 23, 20, 0), Status = "Down" }
        };

        var sensorToDevice = new Dictionary<long, long> { [101] = 1 };

        // 預設門檻 (60 分鐘) -> 不成立
        var defaultFindings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, Array.Empty<(long, long, string?)>(), new PrtgRuleThresholds(60, 5, 240));
        Assert.Empty(defaultFindings);

        // 自訂門檻 (30 分鐘) -> 成立
        var customThresholds = new PrtgRuleThresholds(30, 5, 240);
        var customFindings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, Array.Empty<(long, long, string?)>(), customThresholds);
        Assert.Single(customFindings);
        Assert.Equal(101, customFindings[0].SensorObjid);
        Assert.Equal(PrtgRuleEvaluator.RuleDown, customFindings[0].RuleCode);
        Assert.Equal(40, customFindings[0].Magnitude);
    }

    [Fact]
    public void 跨午夜Warning_前一日進入當日整天未恢復_成立且累計為1440分鐘()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // 前一日 20:00 進入 Warning，當日整天無新變更
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 29, 20, 0, 0), Status = "Warning" }
        };

        var sensorToDevice = new Dictionary<long, long> { [101] = 1 };

        var findings = PrtgRuleEvaluator.Evaluate(_day, changes, sensorToDevice, Array.Empty<(long, long, string?)>());

        Assert.Single(findings);
        var f = findings[0];
        Assert.Equal(1, f.DeviceObjid);
        Assert.Equal(101, f.SensorObjid);
        Assert.Equal(PrtgRuleEvaluator.RuleWarning, f.RuleCode);
        Assert.Equal(1440, f.Magnitude);
        Assert.Contains("1440", f.Detail);
    }

    [Fact]
    public void 跨午夜Warning_當日中途離開_累計只到離開時點且不是1440()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // 前一日 20:00 進入 Warning
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 29, 20, 0, 0), Status = "Warning" },
            // 當日 06:00 恢復 Up（00:00~06:00 共 360 分鐘 >= 門檻 240）
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 6, 0, 0), Status = "Up" }
        };

        var sensorToDevice = new Dictionary<long, long> { [101] = 1 };

        var findings = PrtgRuleEvaluator.Evaluate(_day, changes, sensorToDevice, Array.Empty<(long, long, string?)>());

        Assert.Single(findings);
        var f = findings[0];
        Assert.Equal(1, f.DeviceObjid);
        Assert.Equal(101, f.SensorObjid);
        Assert.Equal(PrtgRuleEvaluator.RuleWarning, f.RuleCode);
        Assert.Equal(360, f.Magnitude);
        Assert.NotEqual(1440, f.Magnitude);
        Assert.Contains("360", f.Detail);
        Assert.DoesNotContain("1440", f.Detail);
    }

    [Fact]
    public void 白名單過濾生效_非白名單type的sensor不產生finding()
    {
        // 模擬 orchestrator 白名單過濾邏輯：
        // sensor 101: SNMP Disk Free (白名單內), 發生 Down
        // sensor 102: ping (非白名單), 發生 Down
        var allSensors = new List<(long Objid, long DeviceObjid, string? Status, string SensorType)>
        {
            (101, 1, "Down", "SNMP Disk Free"),
            (102, 1, "Down", "ping")
        };

        var whitelist = new HashSet<string>(new[] { "SNMP Disk Free" }, StringComparer.OrdinalIgnoreCase);
        var filteredSensors = whitelist.Count == 0 ? allSensors : allSensors.Where(s => whitelist.Contains(s.SensorType)).ToList();
        var allowedSensorObjids = filteredSensors.Select(s => s.Objid).ToHashSet();

        var allChanges = new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 20, 0, 0), Status = "Down" },
            new() { SensorObjid = 102, ChangedAt = new DateTime(2026, 8, 30, 20, 0, 0), Status = "Down" }
        };

        var changes = allChanges.Where(c => allowedSensorObjids.Contains(c.SensorObjid)).ToList();
        var sensorToDevice = filteredSensors.GroupBy(s => s.Objid).ToDictionary(g => g.Key, g => g.First().DeviceObjid);
        var sensorStatuses = filteredSensors.Select(s => (s.Objid, s.DeviceObjid, s.Status)).ToList();

        var findings = PrtgRuleEvaluator.Evaluate(_day, changes, sensorToDevice, sensorStatuses);

        Assert.Single(findings);
        Assert.Equal(101, findings[0].SensorObjid);
        Assert.DoesNotContain(findings, f => f.SensorObjid == 102);
    }
}
