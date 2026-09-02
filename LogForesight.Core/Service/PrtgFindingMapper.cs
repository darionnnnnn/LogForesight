using System.Diagnostics;
using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>
/// 把 PRTG finding 映射成問題簽章（EventId=0 + EventKey，同 Linux 規則模式）。
/// 分類與嚴重度由本檔的對照表直接給定，刻意不走通用規則分類表——
/// 避免 EventKey 被清空或 finding 脫離處理狀態鏈。
/// </summary>
public static class PrtgFindingMapper
{
    public const string PrtgLogName = "PRTG";
    public const string PrtgSource = "PRTG";

    public static LogIssueSignature ToSignature(PrtgFinding finding, DateTime day)
    {
        var (category, severity, elevatesDayRisk, knownIssue) = finding.RuleCode switch
        {
            PrtgRuleEvaluator.RuleDown => (
                IssueCategory.Service,
                IssueSeverity.High,
                true,
                "監控 sensor 持續無回應，可能是服務或主機失聯"),
            PrtgRuleEvaluator.RuleFlapping => (
                IssueCategory.Service,
                IssueSeverity.Medium,
                false,
                "監控 sensor 狀態頻繁震盪，可能是網路不穩或服務反覆重啟"),
            PrtgRuleEvaluator.RuleWarning => (
                IssueCategory.Resource,
                IssueSeverity.Medium,
                false,
                "監控 sensor 長時間處於警告狀態，資源可能接近上限"),
            PrtgRuleEvaluator.RuleSilent => (
                IssueCategory.Service,
                IssueSeverity.Medium,
                false,
                "該 device 的全部 sensor 皆無狀態，監控本身可能已失效"),
            _ => (
                IssueCategory.Other,
                IssueSeverity.Medium,
                false,
                (string?)null)
        };

        var targetObjid = finding.SensorObjid ?? finding.DeviceObjid;

        return new LogIssueSignature
        {
            LogName = PrtgLogName,
            Source = PrtgSource,
            EventId = 0,
            EntryType = EventLogEntryType.Warning,
            EventKey = $"prtg:{finding.RuleCode}:{targetObjid}",
            Count = 1,
            FirstSeen = "00:00",
            LastSeen = "23:59",
            SampleMessages = new List<string> { finding.Detail },
            DistinctMessageCount = 1,
            Category = category,
            Severity = severity,
            ElevatesDayRisk = elevatesDayRisk,
            KnownIssue = knownIssue,
            RuleId = $"prtg-{finding.RuleCode}"
        };
    }
}
