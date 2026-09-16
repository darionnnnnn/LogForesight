using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgRuleEvaluatorTests
{
    private readonly DateTime _day = new(2026, 8, 30);
    private static readonly IReadOnlyList<KnownIssueRule> DefaultRules =
        KnownIssueSeed.CreateRules().Where(r => r.Platform == "prtg").ToList();
    private static readonly IReadOnlyDictionary<long, string> EmptySensorNames = new Dictionary<long, string>();
    private static readonly IReadOnlyDictionary<long, string> EmptyDeviceNames = new Dictionary<long, string>();
    private static readonly IReadOnlyList<PrtgSensorStatusInput> EmptyStatuses =
        Array.Empty<PrtgSensorStatusInput>();

    private static KnownIssueRule CloneRuleWithThreshold(KnownIssueRule r, int threshold) => new()
    {
        Id = r.Id,
        Origin = r.Origin,
        Enabled = r.Enabled,
        Scope = r.Scope,
        Platform = r.Platform,
        MatchAllEventIds = r.MatchAllEventIds,
        MatchFilter = r.MatchFilter,
        SourcePattern = r.SourcePattern,
        EventIds = r.EventIds,
        ProgramPattern = r.ProgramPattern,
        EventNamePattern = r.EventNamePattern,
        MessagePatterns = r.MessagePatterns,
        PrtgRuleCode = r.PrtgRuleCode,
        PrtgThreshold = threshold,
        Category = r.Category,
        Severity = r.Severity,
        ElevatesDayRisk = r.ElevatesDayRisk,
        Description = r.Description,
        CountThreshold = r.CountThreshold,
        PlainExplanation = r.PlainExplanation,
        Impact = r.Impact,
        LikelyCauses = r.LikelyCauses,
        NextSteps = r.NextSteps,
        ModifiedBy = r.ModifiedBy,
        ModifiedAt = r.ModifiedAt,
    };

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

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

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

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

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

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

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

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

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

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

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

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.SensorObjid == 101 && f.RuleCode == PrtgRuleEvaluator.RuleWarning && f.Magnitude == 300);
        Assert.Contains(findings, f => f.SensorObjid == 101 && f.RuleCode == PrtgRuleEvaluator.RuleDown && f.Magnitude == 240);
    }

    [Fact]
    public void 沉默Device_全部未暫停sensor皆Unknown成立_其中一個為Up不成立()
    {
        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            // Device 1: 全部未暫停 sensor 皆為 Unknown 或空值 -> 成立
            new(101, 1, "Unknown", "ping", null),
            new(102, 1, "", "ping", null),
            new(103, 1, null, "ping", null),

            // Device 2: 其中一個 sensor 為 Up -> 不成立
            new(201, 2, "Unknown", "ping", null),
            new(202, 2, "Up", "ping", null)
        };

        var findings = PrtgRuleEvaluator.Evaluate(
            _day,
            Array.Empty<PrtgStateChangeRow>(),
            new Dictionary<long, long>(),
            sensorStatuses,
            DefaultRules,
            EmptySensorNames,
            EmptyDeviceNames);

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
        var sensorStatuses = Array.Empty<PrtgSensorStatusInput>();

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

        Assert.Single(findings);
        Assert.Equal(1, findings[0].DeviceObjid);
        Assert.Equal(101, findings[0].SensorObjid);
        Assert.Equal(PrtgRuleEvaluator.RuleDown, findings[0].RuleCode);
        Assert.DoesNotContain(findings, f => f.SensorObjid == 999);
        Assert.DoesNotContain(findings, f => f.DeviceObjid == 3);
    }

    [Fact]
    public void 傳入規則篩選生效_只傳入Down規則時其餘三種不產生()
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

        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            new(201, 2, "Unknown", "ping", null) // 觸發 Silent (device 2)
        };

        var downOnlyRules = DefaultRules
            .Where(r => r.PrtgRuleCode == PrtgRuleEvaluator.RuleDown)
            .ToList();

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, downOnlyRules, EmptySensorNames, EmptyDeviceNames);

        Assert.Single(findings);
        Assert.Equal(PrtgRuleEvaluator.RuleDown, findings[0].RuleCode);
        Assert.DoesNotContain(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleFlapping);
        Assert.DoesNotContain(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleWarning);
        Assert.DoesNotContain(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleSilent);
    }

    [Fact]
    public void 自訂門檻生效_DownThreshold設為30時未達60分鐘之案例成立()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // sensor 101: 23:20 進入 Down，持續至當日結束（40 分鐘 < 60 分鐘，但 >= 30 分鐘）
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 23, 20, 0), Status = "Down" }
        };

        var sensorToDevice = new Dictionary<long, long> { [101] = 1 };

        // 預設門檻 (60 分鐘) -> 不成立
        var defaultFindings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);
        Assert.Empty(defaultFindings);

        // 自訂門檻 (30 分鐘) -> 成立
        var customRules = DefaultRules
            .Select(r => r.PrtgRuleCode == PrtgRuleEvaluator.RuleDown ? CloneRuleWithThreshold(r, 30) : r)
            .ToList();
        var customFindings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, customRules, EmptySensorNames, EmptyDeviceNames);
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

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

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

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

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
    public void Evaluate_includeSilent控制是否評估沉默Device()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            // sensor 101: 22:00 進入 Down（120 分鐘 >= 60）
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 22, 0, 0), Status = "Down" }
        };

        var sensorToDevice = new Dictionary<long, long>
        {
            [101] = 1
        };

        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            new(201, 2, "Unknown", "ping", null) // Device 2 全 Unknown -> 觸發 Silent
        };

        // includeSilent: true (預設)
        var findingsWithSilent = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);
        Assert.Contains(findingsWithSilent, f => f.RuleCode == PrtgRuleEvaluator.RuleDown && f.SensorObjid == 101);
        Assert.Contains(findingsWithSilent, f => f.RuleCode == PrtgRuleEvaluator.RuleSilent && f.DeviceObjid == 2);

        // includeSilent: false
        var findingsWithoutSilent = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames, includeSilent: false);
        Assert.Contains(findingsWithoutSilent, f => f.RuleCode == PrtgRuleEvaluator.RuleDown && f.SensorObjid == 101);
        Assert.DoesNotContain(findingsWithoutSilent, f => f.RuleCode == PrtgRuleEvaluator.RuleSilent);
    }

    [Fact]
    public void Down_狀態為Acknowledged時標記Acknowledged且Detail包含已於PRTG確認_若非Ack則為false()
    {
        var changesAck = new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 22, 30, 0), Status = "Down (Acknowledged)" }
        };
        var changesPartial = new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 22, 30, 0), Status = "Down (Partial)" }
        };

        var sensorToDevice = new Dictionary<long, long> { [101] = 1 };
        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            new(101, 1, "Down", "SNMP Traffic", null)
        };
        var sensorNames = new Dictionary<long, string> { [101] = "Port 1" };
        var deviceNames = new Dictionary<long, string> { [1] = "Core Switch" };

        // 90 分鐘持續到午夜 (22:30 ~ 24:00 = 90 分鐘 >= 60)
        var findingsAck = PrtgRuleEvaluator.Evaluate(
            _day, changesAck, sensorToDevice, sensorStatuses, DefaultRules, sensorNames, deviceNames);
        Assert.Single(findingsAck);
        var fAck = findingsAck[0];
        Assert.True(fAck.Acknowledged);
        Assert.Contains("，已於 PRTG 確認", fAck.Detail);
        Assert.Equal(90, fAck.Magnitude);

        // 同輸入改為 Down (Partial)
        var findingsPartial = PrtgRuleEvaluator.Evaluate(
            _day, changesPartial, sensorToDevice, sensorStatuses, DefaultRules, sensorNames, deviceNames);
        Assert.Single(findingsPartial);
        var fPartial = findingsPartial[0];
        Assert.False(fPartial.Acknowledged);
        Assert.DoesNotContain("，已於 PRTG 確認", fPartial.Detail);
        Assert.Equal(90, fPartial.Magnitude);
    }

    [Fact]
    public void 同代碼多條啟用規則_選中Id字典序最小者且產生DuplicateRuleWarnings()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 22, 0, 0), Status = "Down" }
        };
        var sensorToDevice = new Dictionary<long, long> { [101] = 1 };

        var downRule1 = new KnownIssueRule
        {
            Id = "builtin-prtg-down",
            Platform = "prtg",
            PrtgRuleCode = "down",
            PrtgThreshold = 60,
            Enabled = true,
            Severity = IssueSeverity.High,
            ElevatesDayRisk = true,
            Description = "Builtin down rule"
        };
        var downRule2 = new KnownIssueRule
        {
            Id = "a-down",
            Platform = "prtg",
            PrtgRuleCode = "down",
            PrtgThreshold = 60,
            Enabled = true,
            Severity = IssueSeverity.Critical,
            ElevatesDayRisk = true,
            Description = "Custom a-down rule"
        };

        var rules = new List<KnownIssueRule> { downRule1, downRule2 };

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, rules, EmptySensorNames, EmptyDeviceNames);

        Assert.Single(findings);
        Assert.Equal("a-down", findings[0].Rule.Id);
        Assert.Equal("Custom a-down rule", findings[0].Rule.Description);
        Assert.Single(findings.DuplicateRuleWarnings);
        Assert.Contains("a-down", findings.DuplicateRuleWarnings[0]);
        Assert.Contains("down", findings.DuplicateRuleWarnings[0]);
    }

    [Fact]
    public void Detail_包含設備名Sensor名與Type_字典缺項時顯示objid且不擲例外()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            new() { SensorObjid = 101, ChangedAt = new DateTime(2026, 8, 30, 22, 0, 0), Status = "Down" }
        };
        var sensorToDevice = new Dictionary<long, long> { [101] = 1 };

        // 字典完整情境
        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            new(101, 1, "Down", "SNMP Traffic", null)
        };
        var sensorNames = new Dictionary<long, string> { [101] = "Core Sensor" };
        var deviceNames = new Dictionary<long, string> { [1] = "Gateway Switch" };

        var findingsFull = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, DefaultRules, sensorNames, deviceNames);

        Assert.Single(findingsFull);
        Assert.Contains("[Gateway Switch]", findingsFull[0].Detail);
        Assert.Contains("Core Sensor", findingsFull[0].Detail);
        Assert.Contains("（SNMP Traffic）", findingsFull[0].Detail);

        // 字典缺漏情境（sensorNames, deviceNames, sensorStatuses 均為空）
        var findingsEmpty = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, EmptyStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

        Assert.Single(findingsEmpty);
        Assert.Contains("[1]", findingsEmpty[0].Detail);
        Assert.Contains("101", findingsEmpty[0].Detail);
        Assert.DoesNotContain("Gateway Switch", findingsEmpty[0].Detail);
    }

    private static PrtgStateChangeRow DownAt2230(long sensorObjid) =>
        new() { SensorObjid = sensorObjid, ChangedAt = new DateTime(2026, 8, 30, 22, 30, 0), Status = "Down" };

    [Fact]
    public void 同裝置折疊_PingDown加兩顆TrafficDown與DiskWarning_回傳Ping的Down與Warning()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            DownAt2230(101),
            DownAt2230(102),
            DownAt2230(103),
            // disk：整日 Warning（自前一日起）
            new() { SensorObjid = 104, ChangedAt = new DateTime(2026, 8, 29, 12, 0, 0), Status = "Warning" }
        };
        var sensorToDevice = new Dictionary<long, long> { [101] = 1, [102] = 1, [103] = 1, [104] = 1 };
        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            new(101, 1, "Down", "Ping", PrtgSensorCategories.Availability.ToUpperInvariant()),
            new(102, 1, "Down", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic),
            new(103, 1, "Down", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic),
            new(104, 1, "Warning", "SNMP Disk Free", PrtgSensorCategories.Disk)
        };

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

        Assert.Equal(2, findings.Count);
        var down = Assert.Single(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleDown);
        Assert.Equal(101, down.SensorObjid);
        Assert.EndsWith("；同裝置另有 2 顆 sensor 同時 Down 或震盪（已合併）", down.Detail);
        var warning = Assert.Single(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleWarning);
        Assert.Equal(104, warning.SensorObjid);
        Assert.Equal(2, findings.MergedCount);
    }

    [Fact]
    public void 同裝置折疊_無availability的sensor_兩顆TrafficDown照常回傳兩筆()
    {
        var changes = new List<PrtgStateChangeRow> { DownAt2230(102), DownAt2230(103) };
        var sensorToDevice = new Dictionary<long, long> { [102] = 1, [103] = 1 };
        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            new(102, 1, "Down", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic),
            new(103, 1, "Down", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic)
        };

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

        Assert.Equal(2, findings.Count(f => f.RuleCode == PrtgRuleEvaluator.RuleDown));
        Assert.DoesNotContain(findings, f => f.Detail.Contains("已合併"));
        Assert.Equal(0, findings.MergedCount);
    }

    [Fact]
    public void 同裝置折疊_只折疊有availabilityDown的device_另一device的TrafficDown不受影響()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            DownAt2230(101), DownAt2230(102),
            DownAt2230(201)
        };
        var sensorToDevice = new Dictionary<long, long> { [101] = 1, [102] = 1, [201] = 2 };
        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            new(101, 1, "Down", "Ping", PrtgSensorCategories.Availability),
            new(102, 1, "Down", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic),
            new(201, 2, "Down", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic)
        };

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

        Assert.Contains(findings, f => f.DeviceObjid == 1 && f.SensorObjid == 101 && f.Detail.Contains("另有 1 顆"));
        Assert.DoesNotContain(findings, f => f.SensorObjid == 102);
        var b = Assert.Single(findings, f => f.DeviceObjid == 2);
        Assert.Equal(201, b.SensorObjid);
        Assert.DoesNotContain("已合併", b.Detail);
        Assert.Equal(1, findings.MergedCount);
    }

    [Fact]
    public void 同裝置折疊_availability的sensor未Down時_同裝置震盪與Down照常回傳()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            DownAt2230(102),
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 1, 0, 0), Status = "Down" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 1, 5, 0), Status = "Up" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 2, 0, 0), Status = "Down" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 2, 5, 0), Status = "Up" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 3, 0, 0), Status = "Down" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 3, 5, 0), Status = "Up" }
        };
        var sensorToDevice = new Dictionary<long, long> { [101] = 1, [102] = 1, [103] = 1 };
        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            new(101, 1, "Up", "Ping", PrtgSensorCategories.Availability),
            new(102, 1, "Down", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic),
            new(103, 1, "Up", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic)
        };
        var rules = DefaultRules
            .Select(r => r.PrtgRuleCode == PrtgRuleEvaluator.RuleFlapping ? CloneRuleWithThreshold(r, 3) : r)
            .ToList();

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, rules, EmptySensorNames, EmptyDeviceNames);

        Assert.Contains(findings, f => f.SensorObjid == 102 && f.RuleCode == PrtgRuleEvaluator.RuleDown);
        Assert.Contains(findings, f => f.SensorObjid == 103 && f.RuleCode == PrtgRuleEvaluator.RuleFlapping);
        Assert.Equal(0, findings.MergedCount);
    }

    [Fact]
    public void 同裝置折疊_availabilityDown時同裝置震盪也合併()
    {
        var changes = new List<PrtgStateChangeRow>
        {
            DownAt2230(101),
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 1, 0, 0), Status = "Down" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 1, 5, 0), Status = "Up" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 2, 0, 0), Status = "Down" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 2, 5, 0), Status = "Up" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 3, 0, 0), Status = "Down" },
            new() { SensorObjid = 103, ChangedAt = new DateTime(2026, 8, 30, 3, 5, 0), Status = "Up" }
        };
        var sensorToDevice = new Dictionary<long, long> { [101] = 1, [103] = 1 };
        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            new(101, 1, "Down", "Ping", PrtgSensorCategories.Availability),
            new(103, 1, "Up", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic)
        };
        var rules = DefaultRules
            .Select(r => r.PrtgRuleCode == PrtgRuleEvaluator.RuleFlapping ? CloneRuleWithThreshold(r, 3) : r)
            .ToList();

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, changes, sensorToDevice, sensorStatuses, rules, EmptySensorNames, EmptyDeviceNames);

        var only = Assert.Single(findings);
        Assert.Equal(101, only.SensorObjid);
        Assert.Contains("另有 1 顆", only.Detail);
        Assert.Equal(1, findings.MergedCount);
    }

    [Fact]
    public void 沉默Device_PingUp其餘Unknown_不算沉默()
    {
        var sensorStatuses = new List<PrtgSensorStatusInput>
        {
            new(101, 1, "Up", "Ping", PrtgSensorCategories.Availability),
            new(102, 1, "Unknown", "SNMP Traffic 64bit", PrtgSensorCategories.Traffic),
            new(103, 1, null, "SNMP Disk Free", PrtgSensorCategories.Disk)
        };

        var findings = PrtgRuleEvaluator.Evaluate(
            _day, Array.Empty<PrtgStateChangeRow>(), new Dictionary<long, long>(),
            sensorStatuses, DefaultRules, EmptySensorNames, EmptyDeviceNames);

        Assert.DoesNotContain(findings, f => f.RuleCode == PrtgRuleEvaluator.RuleSilent);
    }
}
