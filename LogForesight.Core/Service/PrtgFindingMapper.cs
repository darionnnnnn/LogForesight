using System.Diagnostics;
using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>
/// 把 PRTG finding 映射成問題簽章（EventId=0 + EventKey，同 Linux 規則模式）。
/// 分類與嚴重度由 <see cref="PrtgRuleCatalog"/> 給定，刻意不走通用規則分類表
/// （<c>KnownIssueCatalog.Classify</c> 只認 windows／linux 平台，PRTG 走它會讓 EventKey
/// 被清空、finding 脫離處理狀態鏈）。
/// </summary>
public static class PrtgFindingMapper
{
    public const string PrtgLogName = "PRTG";
    public const string PrtgSource = "PRTG";

    public static LogIssueSignature ToSignature(PrtgFinding finding, DateTime day)
    {
        var (category, severity, elevatesDayRisk, knownIssue) = PrtgRuleCatalog.TryGetRule(finding.RuleCode, out var rule)
            ? (rule.Category, rule.Severity, rule.ElevatesDayRisk, (string?)rule.KnownIssue)
            : (IssueCategory.Other, IssueSeverity.Medium, false, (string?)null);

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
