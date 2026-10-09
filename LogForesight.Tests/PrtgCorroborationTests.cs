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

    [Fact]
    public void 精確來源對齊模式已列入共用抑制目錄()
    {
        Assert.Contains(CorrelationPatternIds.PrtgStorageEvidenceAligned, CorrelationPatternIds.All);
        Assert.Contains(CorrelationPatternIds.PrtgCapacityEvidenceAligned, CorrelationPatternIds.All);
        Assert.Contains(CorrelationPatternIds.PrtgOutageEvidenceAligned, CorrelationPatternIds.All);
        Assert.All(new[]
        {
            CorrelationPatternIds.PrtgStorageEvidenceAligned,
            CorrelationPatternIds.PrtgCapacityEvidenceAligned,
            CorrelationPatternIds.PrtgOutageEvidenceAligned
        }, id => Assert.True(CorrelationPatternIds.IsValid(id)));
    }

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

    private static void AttachExactStorageProof(LogIssueSignature eventIssue, LogIssueSignature prtgIssue,
        Action<SourceEvidence, SourceEvidence>? alter = null)
    {
        var eventTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var eventProof = new SourceEvidence
        {
            SourceKind = SourceEvidenceKind.LocalEventRecord,
            ResourceScope = SourceResourceScope.Volume,
            ExactHostKey = "host-id:101", ExactResourceKey = "volume:C:",
            EventTimeUtc = eventTime,
            SourceReference = "event-record:machine:System:153:record-77",
            SourceReferenceQuality = SourceReferenceQuality.ExactNative
        };
        var prtgProof = new SourceEvidence
        {
            SourceKind = SourceEvidenceKind.Prtg,
            ResourceScope = SourceResourceScope.Volume,
            ExactHostKey = "host-id:101", ExactResourceKey = "volume:C:",
            WindowStartUtc = eventTime.AddMinutes(-2), WindowEndUtc = eventTime.AddMinutes(2),
            SourceReference = "prtg:device:70:sensor:700:epoch:2",
            SourceReferenceQuality = SourceReferenceQuality.ExactNative
        };
        alter?.Invoke(eventProof, prtgProof);
        eventIssue.SourceObservations = [eventProof];
        prtgIssue.SourceObservations = [prtgProof];
    }

    private static SourceEvidence ExactEventVolume(string volume, DateTimeOffset eventTime,
        string sourceReference = "event-record:machine:System:153:record-77") => new()
    {
        SourceKind = SourceEvidenceKind.LocalEventRecord,
        ResourceScope = SourceResourceScope.Volume,
        ExactHostKey = "host-id:101", ExactResourceKey = volume,
        EventTimeUtc = eventTime,
        SourceReference = sourceReference,
        SourceReferenceQuality = SourceReferenceQuality.ExactNative
    };

    private static SourceEvidence ExactPrtgVolume(string volume, string sourceReference, DateTimeOffset eventTime) => new()
    {
        SourceKind = SourceEvidenceKind.Prtg,
        ResourceScope = SourceResourceScope.Volume,
        ExactHostKey = "host-id:101", ExactResourceKey = volume,
        WindowStartUtc = eventTime.AddMinutes(-2), WindowEndUtc = eventTime.AddMinutes(2),
        SourceReference = sourceReference,
        SourceReferenceQuality = SourceReferenceQuality.ExactNative
    };

    [Fact]
    public void 修訂線索更新明細且維持抑制_不移除其他關聯()
    {
        var issue = Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Hardware, "舊明細");
        var record = Record(Event("disk", 153), issue);
        record.RiskLevel = RiskLevels.High;
        record.RiskBasis = "rule:disk#153";
        record.CorrelationAlerts.Add("原事件關聯");
        record.CorrelationAlertRefs.Add(new CorrelationAlertRef
        {
            Text = "原事件關聯", PatternId = CorrelationPatternIds.StorageChain
        });
        PrtgCorroboration.Apply(record, NoSuppression);
        issue.SampleMessages = ["新明細"];
        Assert.Equal(0, PrtgCorroboration.Refresh(record, NoSuppression).Added);
        Assert.Contains("新明細", Assert.Single(record.CorrelationAlertRefs.Where(
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated)).Text);
        PrtgCorroboration.Refresh(record, new HashSet<string> { CorrelationPatternIds.PrtgStorageCorroborated });
        Assert.Contains("原事件關聯", record.CorrelationAlerts);
        Assert.Contains(record.CorrelationAlertRefs, reference => reference.PatternId == CorrelationPatternIds.StorageChain);
        Assert.Equal(RiskLevels.High, record.RiskLevel);
        Assert.Equal("rule:disk#153", record.RiskBasis);
        Assert.Single(record.SuppressedCorrelationAlerts);
        PrtgCorroboration.Refresh(record);
        Assert.Single(record.SuppressedCorrelationAlerts);
        record.TopIssues.Remove(issue);
        PrtgCorroboration.Refresh(record);
        Assert.Empty(record.SuppressedCorrelationAlerts);
        Assert.Contains("原事件關聯", record.CorrelationAlerts);
        Assert.Contains(record.CorrelationAlertRefs, reference => reference.PatternId == CorrelationPatternIds.StorageChain);
    }

    [Fact]
    public void storage_磁碟153加硬體warning_只保留同日線索不提高風險()
    {
        var record = Record(Event("disk", 153),
            Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware, "[SRV] RAID 狀態 Warning"));

        var (risk, basis, added) = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Null(risk);
        Assert.Null(basis);
        Assert.Equal(1, added);
        var text = Assert.Single(record.CorrelationAlerts);
        Assert.StartsWith("【儲存異常同日訊號】", text);
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

        Assert.Null(risk);
        Assert.Null(basis);
    }

    [Fact]
    public void Apply精確同資源且事件時刻落在PRTG視窗_輸出對齊佐證不推升風險且冪等()
    {
        var eventIssue = Event("disk", 153);
        var prtgIssue = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware);
        AttachExactStorageProof(eventIssue, prtgIssue);
        var record = Record(eventIssue, prtgIssue);

        var first = PrtgCorroboration.Apply(record, NoSuppression);
        var weakReference = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated);
        var alignedReference = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned);
        var second = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal((null, null, 2), first);
        Assert.Equal((null, null, 0), second);
        Assert.StartsWith("【儲存異常同日訊號】", weakReference.Text);
        Assert.Contains("尚未確認為同一裝置或同一時段", weakReference.Text);
        Assert.StartsWith("【儲存異常來源對齊】", alignedReference.Text);
        Assert.Contains("event-record:machine:System:153:record-77", alignedReference.Text);
        Assert.Contains("prtg:device:70:sensor:700:epoch:2", alignedReference.Text);
        Assert.Contains("volume:C:", alignedReference.Text);
        Assert.Contains("UTC 時間窗", alignedReference.Text);
        Assert.Contains("不代表因果關係", alignedReference.Text);
        Assert.Equal(2, record.CorrelationAlerts.Count);
        Assert.Equal(2, record.CorrelationAlertRefs.Count);
        Assert.Equal(2, record.TopIssues.Count);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
        Assert.True(PrtgCorroboration.OwnsCorrelation(record, alignedReference.Text));
        Assert.Equal(SourceEvidenceRelation.Unknown, eventIssue.SourceObservations.Single().RelationContract);
        Assert.Equal(SourceEvidenceRelation.Unknown, prtgIssue.SourceObservations.Single().RelationContract);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 精確對齊會檢查同類同規則所有可信候選_弱線索仍使用第一筆(bool mismatchingVolumeFirst)
    {
        var eventIssue = Event("disk", 153);
        var eventTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        eventIssue.SourceObservations = [ExactEventVolume("volume:C:", eventTime)];
        var first = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware,
            mismatchingVolumeFirst ? "第一筆 D 槽" : "第一筆 C 槽");
        var second = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware,
            mismatchingVolumeFirst ? "第二筆 C 槽" : "第二筆 D 槽");
        var mismatch = mismatchingVolumeFirst ? first : second;
        var match = mismatchingVolumeFirst ? second : first;
        mismatch.SourceObservations = [ExactPrtgVolume("volume:D:", "prtg:device:70:sensor:701:epoch:2", eventTime)];
        match.SourceObservations = [ExactPrtgVolume("volume:C:", "prtg:device:70:sensor:700:epoch:2", eventTime)];
        var record = Record(eventIssue, first, second);
        var originalIssues = record.TopIssues.ToArray();
        var originalObservationRelations = new[] { eventIssue, first, second }
            .SelectMany(issue => issue.SourceObservations)
            .Select(observation => observation.RelationContract).ToArray();

        var result = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal((null, null, 2), result);
        var weak = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated);
        var aligned = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned);
        Assert.Contains(mismatchingVolumeFirst ? "第一筆 D 槽" : "第一筆 C 槽", weak.Text);
        Assert.Contains("prtg:device:70:sensor:700:epoch:2", aligned.Text);
        Assert.Contains("volume:C:", aligned.Text);
        Assert.DoesNotContain("sensor:701", aligned.Text);
        Assert.Equal(2, record.CorrelationAlerts.Count);
        Assert.Equal(2, record.CorrelationAlertRefs.Count);
        Assert.Equal(3, record.TopIssues.Count);
        Assert.Equal(originalIssues, record.TopIssues);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
        Assert.Equal(originalObservationRelations, new[] { eventIssue, first, second }
            .SelectMany(issue => issue.SourceObservations)
            .Select(observation => observation.RelationContract).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 儲存精確配對會檢查Warning與Down全部允許規則_弱明細仍採第一筆(bool warningMismatchFirst)
    {
        var eventIssue = Event("disk", 153);
        var eventTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        eventIssue.SourceObservations = [ExactEventVolume("volume:C:", eventTime)];
        var warning = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware,
            warningMismatchFirst ? "第一筆 Warning，D 槽" : "第二筆 Warning，C 槽");
        var down = Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Hardware,
            warningMismatchFirst ? "第二筆 Down，C 槽" : "第一筆 Down，D 槽");
        var mismatch = warningMismatchFirst ? warning : down;
        var match = warningMismatchFirst ? down : warning;
        mismatch.SourceObservations = [ExactPrtgVolume("volume:D:", "prtg:device:70:sensor:701:epoch:2", eventTime)];
        match.SourceObservations = [ExactPrtgVolume("volume:C:", "prtg:device:70:sensor:700:epoch:2", eventTime)];
        var record = warningMismatchFirst
            ? Record(eventIssue, warning, down)
            : Record(eventIssue, down, warning);
        var originalIssues = record.TopIssues.ToArray();

        var result = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal((null, null, 2), result);
        var weak = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated);
        var aligned = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned);
        Assert.Contains(warningMismatchFirst ? "第一筆 Warning" : "第一筆 Down", weak.Text);
        Assert.Contains("prtg:device:70:sensor:700:epoch:2", aligned.Text);
        Assert.Contains("volume:C:", aligned.Text);
        Assert.Equal(2, record.CorrelationAlerts.Count);
        Assert.Equal(2, record.CorrelationAlertRefs.Count);
        Assert.Equal(originalIssues, record.TopIssues);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 儲存精確配對會尋找後續NTFS可信事件_弱訊號仍依既有分類選擇(bool missingEvidenceFirst)
    {
        var disk = Event("disk", 153);
        var ntfs = Event("Ntfs", 55);
        var eventTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        ntfs.SourceObservations =
        [
            ExactEventVolume("volume:C:", eventTime, "event-record:machine:System:55:record-78")
        ];
        var eventOrder = missingEvidenceFirst ? new[] { disk, ntfs } : new[] { ntfs, disk };
        var prtgIssue = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware);
        prtgIssue.SourceObservations =
        [
            ExactPrtgVolume("volume:C:", "prtg:device:70:sensor:700:epoch:2", eventTime)
        ];
        var record = Record(eventOrder[0], eventOrder[1], prtgIssue);
        var originalIssues = record.TopIssues.ToArray();

        var result = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal((null, null, 2), result);
        var weak = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated);
        var aligned = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned);
        Assert.Contains("disk#153", weak.Text);
        Assert.Contains("event-record:machine:System:55:record-78", aligned.Text);
        Assert.Contains("prtg:device:70:sensor:700:epoch:2", aligned.Text);
        Assert.Equal(2, record.CorrelationAlerts.Count);
        Assert.Equal(2, record.CorrelationAlertRefs.Count);
        Assert.Equal(originalIssues, record.TopIssues);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
    }

    [Fact]
    public void 儲存對齊不擴大到未列入明確候選的事件或PRTG規則()
    {
        var eventIssue = Event("disk", 153);
        var eventTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        eventIssue.SourceObservations = [ExactEventVolume("volume:C:", eventTime)];
        var allowed = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware);
        allowed.SourceObservations = [ExactPrtgVolume("volume:C:", "prtg:device:70:sensor:700:epoch:2", eventTime)];
        var prohibitedEvent = Event("volsnap", 153);
        prohibitedEvent.SourceObservations = [ExactEventVolume("volume:C:", eventTime)];
        var prohibitedRule = Prtg("rule_critical", PrtgSensorCategories.Hardware);
        prohibitedRule.SourceObservations = [ExactPrtgVolume("volume:C:", "prtg:device:70:sensor:701:epoch:2", eventTime)];

        var record = Record(prohibitedEvent, eventIssue, prohibitedRule, allowed);

        PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Contains(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned);
        Assert.DoesNotContain("sensor:701", Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned).Text);
        Assert.Equal(2, record.CorrelationAlertRefs.Count);
        Assert.Equal(4, record.TopIssues.Count);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 關機精確配對檢查兩種關機事件與Down及Flapping候選(bool mismatchesFirst)
    {
        var eventTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var kernelPower = Event("Microsoft-Windows-Kernel-Power", 41);
        var eventLog = Event("EventLog", 6008);
        eventLog.SourceObservations =
        [
            new SourceEvidence
            {
                SourceKind = SourceEvidenceKind.LocalEventRecord,
                ResourceScope = SourceResourceScope.Host,
                ExactHostKey = "host-id:101", ExactResourceKey = "host-id:101",
                EventTimeUtc = eventTime,
                SourceReference = "event-record:machine:System:6008:record-88",
                SourceReferenceQuality = SourceReferenceQuality.ExactNative
            }
        ];
        var down = Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Availability,
            mismatchesFirst ? "第一筆 Down，其他主機" : "第二筆 Down，目標主機");
        var flapping = Prtg(PrtgRuleEvaluator.RuleFlapping, PrtgSensorCategories.Availability,
            mismatchesFirst ? "第二筆 Flapping，目標主機" : "第一筆 Flapping，其他主機");
        var mismatch = mismatchesFirst ? down : flapping;
        var match = mismatchesFirst ? flapping : down;
        mismatch.SourceObservations =
        [
            new SourceEvidence
            {
                SourceKind = SourceEvidenceKind.Prtg,
                ResourceScope = SourceResourceScope.Host,
                ExactHostKey = "host-id:102", ExactResourceKey = "host-id:102",
                WindowStartUtc = eventTime.AddMinutes(-2), WindowEndUtc = eventTime.AddMinutes(2),
                SourceReference = "prtg:device:72:sensor:702:epoch:2",
                SourceReferenceQuality = SourceReferenceQuality.ExactNative
            }
        ];
        match.SourceObservations =
        [
            new SourceEvidence
            {
                SourceKind = SourceEvidenceKind.Prtg,
                ResourceScope = SourceResourceScope.Host,
                ExactHostKey = "host-id:101", ExactResourceKey = "host-id:101",
                WindowStartUtc = eventTime.AddMinutes(-2), WindowEndUtc = eventTime.AddMinutes(2),
                SourceReference = "prtg:device:70:sensor:700:epoch:2",
                SourceReferenceQuality = SourceReferenceQuality.ExactNative
            }
        ];
        var record = mismatchesFirst
            ? Record(kernelPower, eventLog, down, flapping)
            : Record(eventLog, kernelPower, flapping, down);
        var originalIssues = record.TopIssues.ToArray();

        var result = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal((null, null, 2), result);
        var weak = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgOutageCorroborated);
        var aligned = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgOutageEvidenceAligned);
        Assert.Contains(mismatchesFirst ? "第一筆 Down" : "第一筆 Flapping", weak.Text);
        Assert.Contains("event-record:machine:System:6008:record-88", aligned.Text);
        Assert.Contains("prtg:device:70:sensor:700:epoch:2", aligned.Text);
        Assert.DoesNotContain("sensor:702", aligned.Text);
        Assert.Equal(2, record.CorrelationAlerts.Count);
        Assert.Equal(originalIssues, record.TopIssues);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
    }

    [Fact]
    public void 精確候選配對超過固定比較預算時失敗關閉不追加對齊()
    {
        var eventTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var events = Enumerable.Range(0, 65).Select(index =>
        {
            var issue = Event("disk", 153);
            var volume = index == 64 ? "volume:C:" : "volume:D:";
            issue.SourceObservations = [ExactEventVolume(volume, eventTime)];
            return issue;
        }).ToArray();
        var prtgIssues = Enumerable.Range(0, 64).Select(index =>
        {
            var issue = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware);
            issue.SourceObservations =
            [
                ExactPrtgVolume("volume:C:", $"prtg:device:70:sensor:{700 + index}:epoch:2", eventTime)
            ];
            return issue;
        }).ToArray();
        var record = Record(events.Concat(prtgIssues).ToArray());

        var result = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Equal((null, null, 1), result);
        Assert.Contains(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated);
        Assert.DoesNotContain(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("resource")]
    [InlineData("resource-scope")]
    [InlineData("missing-resource")]
    [InlineData("no-overlap")]
    [InlineData("unknown-time")]
    [InlineData("unknown-reference")]
    [InlineData("event-reference")]
    public void Apply精確證據條件不符時沿用弱同日線索(string mismatch)
    {
        var eventIssue = Event("disk", 153);
        var prtgIssue = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware);
        AttachExactStorageProof(eventIssue, prtgIssue, (eventProof, prtgProof) =>
        {
            switch (mismatch)
            {
                case "host": prtgProof.ExactHostKey = "host-id:102"; break;
                case "resource": prtgProof.ExactResourceKey = "volume:D:"; break;
                case "resource-scope": prtgProof.ResourceScope = SourceResourceScope.Host; break;
                case "missing-resource": prtgProof.ExactResourceKey = null; break;
                case "no-overlap":
                    prtgProof.WindowStartUtc = eventProof.EventTimeUtc!.Value.AddHours(1);
                    prtgProof.WindowEndUtc = eventProof.EventTimeUtc.Value.AddHours(2);
                    break;
                case "unknown-time": eventProof.EventTimeUtc = null; break;
                case "unknown-reference":
                    prtgProof.SourceReference = "";
                    prtgProof.SourceReferenceQuality = SourceReferenceQuality.Unknown;
                    break;
                case "event-reference":
                    eventProof.SourceReference = null;
                    eventProof.SourceReferenceQuality = SourceReferenceQuality.Unknown;
                    break;
            }
        });
        var record = Record(eventIssue, prtgIssue);

        var (risk, basis, added) = PrtgCorroboration.Apply(record, NoSuppression);
        var text = Assert.Single(record.CorrelationAlerts);

        Assert.Null(risk);
        Assert.Null(basis);
        Assert.Equal(1, added); // existing same-day behavior remains available
        Assert.Equal(CorrelationPatternIds.PrtgStorageCorroborated, Assert.Single(record.CorrelationAlertRefs).PatternId);
        Assert.DoesNotContain("精確來源佐證", text);
        Assert.DoesNotContain("prtg-storage-evidence-aligned", record.CorrelationAlertRefs.Select(r => r.PatternId));
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
    }

    [Fact]
    public void Refresh來源對齊撤回時保留弱佐證_弱模式抑制意圖也涵蓋精確線索()
    {
        var eventIssue = Event("disk", 153);
        var prtgIssue = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware);
        AttachExactStorageProof(eventIssue, prtgIssue);
        var record = Record(eventIssue, prtgIssue);
        PrtgCorroboration.Apply(record, NoSuppression);
        var weakText = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated).Text;
        prtgIssue.SourceObservations[0].SourceReference = "prtg:device:70:sensor:701:epoch:2";

        var refreshed = PrtgCorroboration.Refresh(record, NoSuppression);

        Assert.Null(refreshed.RiskLevel);
        Assert.Null(refreshed.RiskBasis);
        Assert.Equal(0, refreshed.Added);
        Assert.Contains(weakText, record.CorrelationAlerts);
        Assert.Contains("prtg:device:70:sensor:701:epoch:2",
            Assert.Single(record.CorrelationAlertRefs, reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned).Text);
        PrtgCorroboration.Refresh(record, new HashSet<string> { CorrelationPatternIds.PrtgStorageCorroborated });
        Assert.Empty(record.CorrelationAlerts);
        Assert.Contains(record.SuppressedCorrelationAlerts, text => text.StartsWith("【儲存異常同日訊號】", StringComparison.Ordinal));
        Assert.Contains(record.SuppressedCorrelationAlerts, text => text.StartsWith("【儲存異常來源對齊】", StringComparison.Ordinal));
        Assert.DoesNotContain(record.CorrelationAlertRefs, reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned);
        Assert.DoesNotContain(record.CorrelationAlertRefs, reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);

        prtgIssue.SourceObservations[0].SourceReference = null;
        prtgIssue.SourceObservations[0].SourceReferenceQuality = SourceReferenceQuality.Unknown;
        PrtgCorroboration.Refresh(record, new HashSet<string> { CorrelationPatternIds.PrtgStorageCorroborated });
        Assert.Empty(record.CorrelationAlertRefs.Where(reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned));
        Assert.DoesNotContain(record.CorrelationAlertRefs, reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated);
        Assert.Contains(weakText, record.SuppressedCorrelationAlerts);
        Assert.DoesNotContain(record.SuppressedCorrelationAlerts, text => text.StartsWith("【儲存異常來源對齊】", StringComparison.Ordinal));
        Assert.Empty(record.CorrelationAlerts);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
    }

    [Fact]
    public void 抑制弱同日模式會保留其既有靜音意圖涵蓋新精確對齊()
    {
        var eventIssue = Event("disk", 153);
        var prtgIssue = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware);
        AttachExactStorageProof(eventIssue, prtgIssue);
        var record = Record(eventIssue, prtgIssue);

        var result = PrtgCorroboration.Apply(record,
            new HashSet<string> { CorrelationPatternIds.PrtgStorageCorroborated });

        Assert.Equal((null, null, 0), result);
        Assert.Empty(record.CorrelationAlertRefs.Where(reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated));
        Assert.Contains(record.SuppressedCorrelationAlerts, text => text.StartsWith("【儲存異常同日訊號】", StringComparison.Ordinal));
        Assert.Contains(record.SuppressedCorrelationAlerts, text => text.StartsWith("【儲存異常來源對齊】", StringComparison.Ordinal));
        Assert.Empty(record.CorrelationAlertRefs);
        Assert.Empty(record.CorrelationAlerts);
        Assert.Equal(RiskLevels.Low, record.RiskLevel);
        Assert.Equal((null, null, 0), PrtgCorroboration.Apply(record,
            new HashSet<string> { CorrelationPatternIds.PrtgStorageCorroborated }));
        Assert.Equal(2, record.SuppressedCorrelationAlerts.Count);
        Assert.Empty(record.CorrelationAlertRefs);
    }

    [Fact]
    public void 僅抑制精確對齊不會擴大到弱同日線索且Refresh保留此模式()
    {
        var eventIssue = Event("disk", 153);
        var prtgIssue = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware);
        AttachExactStorageProof(eventIssue, prtgIssue);
        var record = Record(eventIssue, prtgIssue);
        var alignedOnly = new HashSet<string> { CorrelationPatternIds.PrtgStorageEvidenceAligned };

        Assert.Equal((null, null, 1), PrtgCorroboration.Apply(record, alignedOnly));
        Assert.Equal(CorrelationPatternIds.PrtgStorageCorroborated,
            Assert.Single(record.CorrelationAlertRefs).PatternId);
        Assert.StartsWith("【儲存異常來源對齊】", Assert.Single(record.SuppressedCorrelationAlerts));
        Assert.Equal((null, null, 0), PrtgCorroboration.Refresh(record, alignedOnly));
        Assert.Equal(CorrelationPatternIds.PrtgStorageCorroborated,
            Assert.Single(record.CorrelationAlertRefs).PatternId);
        Assert.StartsWith("【儲存異常來源對齊】", Assert.Single(record.SuppressedCorrelationAlerts));
        Assert.Equal((null, null, 0), PrtgCorroboration.Refresh(record));
        Assert.Equal(CorrelationPatternIds.PrtgStorageCorroborated,
            Assert.Single(record.CorrelationAlertRefs).PatternId);
    }

    [Fact]
    public void Refresh撤回精確對齊時保留弱同日線索及其他關聯與風險()
    {
        var eventIssue = Event("disk", 153);
        var prtgIssue = Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware);
        AttachExactStorageProof(eventIssue, prtgIssue);
        var record = Record(eventIssue, prtgIssue);
        PrtgCorroboration.Apply(record, NoSuppression);
        var weakText = Assert.Single(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated).Text;
        record.RiskLevel = RiskLevels.High;
        record.RiskBasis = "human-review";
        record.CorrelationAlerts.Add("人工保留的關聯");
        record.CorrelationAlertRefs.Add(new CorrelationAlertRef
        {
            Text = "人工保留的關聯", PatternId = CorrelationPatternIds.StorageChain
        });

        prtgIssue.SourceObservations[0].SourceReference = null;
        prtgIssue.SourceObservations[0].SourceReferenceQuality = SourceReferenceQuality.Unknown;
        var refreshed = PrtgCorroboration.Refresh(record, NoSuppression);

        Assert.Null(refreshed.RiskLevel);
        Assert.Null(refreshed.RiskBasis);
        Assert.Equal(0, refreshed.Added);
        Assert.Contains(weakText, record.CorrelationAlerts);
        Assert.DoesNotContain(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageEvidenceAligned);
        Assert.Contains(record.CorrelationAlertRefs,
            reference => reference.PatternId == CorrelationPatternIds.PrtgStorageCorroborated);
        Assert.Contains("人工保留的關聯", record.CorrelationAlerts);
        Assert.Contains(record.CorrelationAlertRefs, reference => reference.PatternId == CorrelationPatternIds.StorageChain);
        Assert.Equal(RiskLevels.High, record.RiskLevel);
        Assert.Equal("human-review", record.RiskBasis);
        Assert.Equal(2, record.TopIssues.Count);
    }

    [Fact]
    public void storage_PRTG分類為disk時不命中()
    {
        var record = Record(Event("disk", 153), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Disk));

        Assert.Equal((null, null, 0), PrtgCorroboration.Apply(record, NoSuppression));
        Assert.Empty(record.CorrelationAlerts);
    }

    [Fact]
    public void capacity_srv2013加磁碟warning_只保留同日線索()
    {
        var record = Record(Event("srv", 2013), Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Disk, "[SRV] C: 可用空間 Warning"));

        var (risk, basis, added) = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Null(risk);
        Assert.Null(basis);
        Assert.Equal(1, added);
        Assert.Equal("【容量異常同日訊號】事件日誌回報磁碟空間即將不足，PRTG 磁碟可用空間 sensor 同日示警（[SRV] C: 可用空間 Warning）；尚未確認為同一磁碟區",
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
    public void outage_KernelPower41加連通性flapping_只保留同日線索()
    {
        var record = Record(Event("Microsoft-Windows-Kernel-Power", 41), Prtg(PrtgRuleEvaluator.RuleFlapping, PrtgSensorCategories.Availability, "[SRV] Ping 震盪"));

        var (risk, basis, added) = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Null(risk);
        Assert.Null(basis);
        Assert.Equal(1, added);
        Assert.Equal("【關機與監測異常同日訊號】事件日誌記錄非預期關機，PRTG 連通性類 sensor 同日異常（[SRV] Ping 震盪）；尚未確認時間與探測對象相符",
            Assert.Single(record.CorrelationAlerts));
    }

    [Fact]
    public void outage_EventLog6008加連通性down也命中()
    {
        var record = Record(Event("EventLog", 6008), Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Availability));

        Assert.Equal(1, PrtgCorroboration.Apply(record, NoSuppression).Added);
        Assert.Equal(CorrelationPatternIds.PrtgOutageCorroborated, Assert.Single(record.CorrelationAlertRefs).PatternId);
    }

    [Fact]
    public void outage_KernelPower41加traffic_down不命中()
    {
        var record = Record(Event("Microsoft-Windows-Kernel-Power", 41), Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Traffic));

        Assert.Equal((null, null, 0), PrtgCorroboration.Apply(record, NoSuppression));
        Assert.Empty(record.CorrelationAlerts);
    }

    [Fact]
    public void 同時命中storage與outage_保留兩項線索但不提高風險()
    {
        var record = Record(
            Event("Microsoft-Windows-Kernel-Power", 41),
            Event("disk", 7),
            Prtg(PrtgRuleEvaluator.RuleDown, PrtgSensorCategories.Availability),
            Prtg(PrtgRuleEvaluator.RuleWarning, PrtgSensorCategories.Hardware));

        var (risk, basis, added) = PrtgCorroboration.Apply(record, NoSuppression);

        Assert.Null(risk);
        Assert.Null(basis);
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
        Assert.StartsWith("【儲存異常同日訊號】", Assert.Single(record.SuppressedCorrelationAlerts));
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
        Assert.StartsWith("【儲存異常同日訊號】", Assert.Single(record.CorrelationAlerts));
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
