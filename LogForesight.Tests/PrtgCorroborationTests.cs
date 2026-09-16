using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 跨來源佐證（<see cref="PrtgCorroboration"/>）的純函式行為：三種刻意配對、抑制、冪等、抑制簽章不參與。
/// </summary>
public class PrtgCorroborationTests
{
    private static readonly IReadOnlySet<string> NoSuppression = new HashSet<string>();

    private static LogIssueSignature Event(string source, int eventId, bool suppressed = false) => new()
    {
        LogName = "System", Source = source, EventId = eventId, Count = 3, Suppressed = suppressed
    };

    private static LogIssueSignature Prtg(string ruleCode, string? category, string detail = "PRTG 明細", bool suppressed = false) => new()
    {
        LogName = PrtgFindingMapper.PrtgLogName,
        Source = $"PRTG:{ruleCode}",
        EventId = 0,
        EventKey = $"prtg:{ruleCode}:2001",
        Count = 1,
        SampleMessages = new List<string> { detail },
        PrtgSensorCategory = category,
        Suppressed = suppressed
    };

    private static DailyAnalysisRecord Record(params LogIssueSignature[] issues) => new()
    {
        HostId = 101, Host = "SRV-TEST", Date = DateTime.Today, RiskLevel = RiskLevels.Low,
        TopIssues = issues.ToList()
    };

    [Fact]
    public void storage_磁碟153加硬體warning_命中高風險且依據為佐證模式()
    {
        var record = Record(Event("disk", 153),
            Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware, "[SRV] RAID 狀態 Warning"));

        var (risk, basis, added) = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal(RiskLevels.High, risk);
        Assert.Equal(CorrelationPatternIds.PrtgStorageCorroborated, basis);
        Assert.Equal(1, added);
        var text = Assert.Single(record.CorrelationAlerts);
        Assert.StartsWith("【儲存故障雙重確認】", text);
        Assert.Contains("disk#153", text);
        Assert.Contains("[SRV] RAID 狀態 Warning", text);
        var reference = Assert.Single(record.CorrelationAlertRefs);
        Assert.Equal(text, reference.Text);
        Assert.Equal(CorrelationPatternIds.PrtgStorageCorroborated, reference.PatternId);
        // 風險由呼叫端套用，Apply 不自己改
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
    }

    [Fact]
    public void storage_硬體down也算示警()
    {
        var record = Record(Event("Ntfs", 55), Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Hardware));

        var (risk, basis, _) = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal(RiskLevels.High, risk);
        Assert.Equal(CorrelationPatternIds.PrtgStorageCorroborated, basis);
    }

    [Fact]
    public void storage_PRTG分類為disk時不命中()
    {
        var record = Record(Event("disk", 153), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Disk));

        Assert.Equal((null, null, 0), PrtgCorroboration.Apply(record, NoSuppression));
        Assert.Empty(record.CorrelationAlerts);
    }

    [Fact]
    public void capacity_srv2013加磁碟warning_命中中風險()
    {
        var record = Record(Event("srv", 2013), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Disk, "[SRV] C: 可用空間 Warning"));

        var (risk, basis, added) = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal(RiskLevels.Medium, risk);
        Assert.Equal(CorrelationPatternIds.PrtgCapacityCorroborated, basis);
        Assert.Equal(1, added);
        Assert.Equal("【磁碟容量雙重確認】事件日誌回報磁碟空間即將不足，PRTG 磁碟可用空間 sensor 同日示警（[SRV] C: 可用空間 Warning）",
            Assert.Single(record.CorrelationAlerts));
    }

    [Fact]
    public void capacity_磁碟warning換成硬體warning時不命中capacity()
    {
        var record = Record(Event("srv", 2013), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware));

        PrtgCorroboration.Apply(record, NoSuppression);

        Assert.DoesNotContain(record.CorrelationAlertRefs, r => r.PatternId == CorrelationPatternIds.PrtgCapacityCorroborated);
        Assert.Empty(record.CorrelationAlerts);
    }

    [Fact]
    public void capacity_磁碟down不算容量示警()
    {
        var record = Record(Event("srv", 2013), Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Disk));

        Assert.Equal((null, null, 0), PrtgCorroboration.Apply(record, NoSuppression));
    }

    [Fact]
    public void storage與capacity不交叉配對_IO錯誤加磁碟空間warning不算佐證()
    {
        var record = Record(Event("disk", 153), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Disk));

        Assert.Equal((null, null, 0), PrtgCorroboration.Apply(record, NoSuppression));
    }

    [Fact]
    public void outage_KernelPower41加連通性flapping_命中中風險()
    {
        var record = Record(Event("Microsoft-Windows-Kernel-Power", 41), Prtg(PrtgRuleEvaluator.RuleFlapping, PrtgSensorCategories.Availability, "[SRV] Ping 震盪"));

        var (risk, basis, added) = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal(RiskLevels.Medium, risk);
        Assert.Equal(CorrelationPatternIds.PrtgOutageCorroborated, basis);
        Assert.Equal(1, added);
        Assert.Equal("【失聯獲 PRTG 證實】事件日誌記錄非預期關機，PRTG 同日觀測到主機失聯（[SRV] Ping 震盪），不是日誌誤報",
            Assert.Single(record.CorrelationAlerts));
    }

    [Fact]
    public void outage_EventLog6008加連通性down也命中()
    {
        var record = Record(Event("EventLog", 6008), Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Availability));

        Assert.Equal(CorrelationPatternIds.PrtgOutageCorroborated, PrtgCorroboration.Apply(record, NoSuppression).RiskBasis);
    }

    [Fact]
    public void outage_KernelPower41加traffic_down不命中()
    {
        var record = Record(Event("Microsoft-Windows-Kernel-Power", 41), Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Traffic));

        Assert.Equal((null, null, 0), PrtgCorroboration.Apply(record, NoSuppression));
        Assert.Empty(record.CorrelationAlerts);
    }

    [Fact]
    public void 同時命中storage與outage_風險取高且依據為會拉高的storage()
    {
        var record = Record(
            Event("Microsoft-Windows-Kernel-Power", 41),
            Event("disk", 7),
            Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Availability),
            Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware));

        var (risk, basis, added) = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal(RiskLevels.High, risk);
        Assert.Equal(CorrelationPatternIds.PrtgStorageCorroborated, basis);
        Assert.Equal(2, added);
    }

    [Fact]
    public void 模式被抑制_進已抑制清單且不影響風險()
    {
        var record = Record(Event("disk", 153), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware));
        var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CorrelationPatternIds.PrtgStorageCorroborated };

        var result = PrtgCorroboration.Apply(record, suppressed);

        Assert.Equal((null, null, 0), result);
        Assert.Empty(record.CorrelationAlerts);
        Assert.Empty(record.CorrelationAlertRefs);
        Assert.StartsWith("【儲存故障雙重確認】", Assert.Single(record.SuppressedCorrelationAlerts));
    }

    [Fact]
    public void 重複呼叫兩次只加一次()
    {
        var record = Record(Event("disk", 153), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware));

        var first = PrtgCorroboration.Apply(record, NoSuppression);
        var second = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal(1, first.Added);
        Assert.Equal((null, null, 0), second);
        Assert.Single(record.CorrelationAlerts);
        Assert.Single(record.CorrelationAlertRefs);
    }

    [Fact]
    public void 被抑制的模式重複呼叫也只記一次()
    {
        var record = Record(Event("disk", 153), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware));
        var suppressed = new HashSet<string> { CorrelationPatternIds.PrtgStorageCorroborated };

        PrtgCorroboration.Apply(record, suppressed);
        PrtgCorroboration.Apply(record, suppressed);

        Assert.Single(record.SuppressedCorrelationAlerts);
    }

    [Fact]
    public void 取消抑制後重跑_佐證補進關聯告警並移出已抑制清單()
    {
        var record = Record(Event("disk", 153), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware));
        var suppressed = new HashSet<string> { CorrelationPatternIds.PrtgStorageCorroborated };

        PrtgCorroboration.Apply(record, suppressed);
        var result = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal(1, result.Added);
        Assert.StartsWith("【儲存故障雙重確認】", Assert.Single(record.CorrelationAlerts));
        Assert.Empty(record.SuppressedCorrelationAlerts);
    }

    [Fact]
    public void PRTG簽章已抑制時不參與()
    {
        var record = Record(Event("disk", 153), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware, suppressed: true));

        Assert.Equal((null, null, 0), PrtgCorroboration.Apply(record, NoSuppression));
        Assert.Empty(record.CorrelationAlerts);
        Assert.Empty(record.SuppressedCorrelationAlerts);
    }

    [Fact]
    public void 事件簽章已抑制時不參與()
    {
        var record = Record(Event("disk", 153, suppressed: true), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware));

        Assert.Equal((null, null, 0), PrtgCorroboration.Apply(record, NoSuppression));
        Assert.Empty(record.CorrelationAlerts);
    }

    [Fact]
    public void 分類為null的PRTG簽章不參與()
    {
        // 舊紀錄或 silent（device 層）沒有 sensor 分類，不能被當成任何一類
        var record = Record(Event("disk", 153), Prtg(PrtgRuleEvaluator.RuleWarning, null));

        Assert.Equal((null, null, 0), PrtgCorroboration.Apply(record, NoSuppression));
    }
}
